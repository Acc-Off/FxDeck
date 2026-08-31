using System.Text.Json;
using FxDeck.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FxDeck.NuiInspect;

/// <summary>Where the FiveM NUI (CEF) debug endpoint lives and how patiently to talk to it.</summary>
public sealed class NuiInspectOptions
{
    /// <summary>Open whenever the game runs; loopback only (design memo §3.10).</summary>
    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:13172/");

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary><c>Runtime.executionContextCreated</c> events trail the <c>Runtime.enable</c> response; wait for them.</summary>
    public TimeSpan ContextEventDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Cap for one whole extraction so the admin UI always gets a definite answer.</summary>
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Machine-readable reason codes of the admin API (design memo §3.3).</summary>
public enum ExtractionFailure
{
    /// <summary>The debug port did not answer — FiveM is not running.</summary>
    GameNotRunning,

    /// <summary>No chat frame — the player is on the main menu, not on a server.</summary>
    NotInSession,

    /// <summary>The chat NUI exists but its state could not be read (replaced chat resource, changed internals).</summary>
    ChatUnavailable,
}

public sealed class ExtractionResult
{
    private ExtractionResult(ExtractionFailure? failure, IReadOnlyList<NuiCommand> commands)
    {
        Failure = failure;
        Commands = commands;
    }

    public bool Success => Failure is null;

    public ExtractionFailure? Failure { get; }

    public IReadOnlyList<NuiCommand> Commands { get; }

    public static ExtractionResult Ok(IReadOnlyList<NuiCommand> commands) => new(null, commands);

    public static ExtractionResult Failed(ExtractionFailure failure) => new(failure, []);
}

/// <summary>
/// Reads the command suggestions the official chat NUI already holds in memory
/// (<c>backingSuggestions − removedSuggestions</c>) through the CEF debug port — Tier 1, purely passive:
/// nothing is sent to the game or the server (design memo §3.10). Everything here is undocumented and
/// implemented defensively; every failure collapses into one of the three <see cref="ExtractionFailure"/> codes.
/// </summary>
public sealed class ChatCommandExtractor : IDisposable
{
    /// <summary>
    /// Walks the Vue 3 component tree for the instance holding <c>backingSuggestions</c> instead of assuming a
    /// root shape (production builds carry no <c>__vueParentComponent</c>). Returns a JSON string so the CDP
    /// result stays one scalar. Verified against the bundled system-resource chat (DevelopmentNote §5).
    /// </summary>
    private const string ExtractScript =
        """
        (() => {
          const app = [...document.querySelectorAll('#app')].map(h=>h.__vue_app__).find(Boolean);
          if(!app) return JSON.stringify({found:false});
          const root = app._container && app._container._vnode && app._container._vnode.component;
          const insts=[]; const seen=new Set();
          const walk=v=>{ if(!v||typeof v!=='object') return; if(v.component) col(v.component); if(Array.isArray(v.children)) v.children.forEach(walk); };
          const col=i=>{ if(!i||seen.has(i)) return; seen.add(i); insts.push(i); if(i.subTree) walk(i.subTree); };
          col(root);
          let owner=null;
          for(const i of insts) for(const bag of [i.data,i.ctx,i.setupState,i.props].filter(b=>b&&typeof b==='object'))
            if(Array.isArray(bag.backingSuggestions)){ owner={i,bag}; break; }
          if(!owner) return JSON.stringify({found:false});
          const removed=new Set();
          for(const bag of [owner.i.data,owner.i.ctx].filter(Boolean)) if(Array.isArray(bag.removedSuggestions)) bag.removedSuggestions.forEach(n=>removed.add(n));
          const list=owner.bag.backingSuggestions.filter(s=>!removed.has(s.name));
          return JSON.stringify({found:true, commands: JSON.parse(JSON.stringify(list.map(s=>({name:s.name,help:s.help||'',params:s.params||[]}))))});
        })()
        """;

    private readonly NuiInspectOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public ChatCommandExtractor(NuiInspectOptions options, ILogger<ChatCommandExtractor>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<ChatCommandExtractor>.Instance;
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = options.ConnectTimeout })
        {
            Timeout = options.OverallTimeout,
        };
    }

    public async Task<ExtractionResult> ExtractAsync(CancellationToken cancellationToken = default)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_options.OverallTimeout);
        var token = overall.Token;

        // Phase 1: find the in-game page. No answer at all means the game is not running.
        CdpPage? page;
        try
        {
            page = CdpClient.PickPage(await CdpClient.ListPagesAsync(_http, _options.BaseAddress, token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("NUI debug port {BaseAddress} not reachable: {Message}", _options.BaseAddress, ex.Message);
            return ExtractionResult.Failed(ExtractionFailure.GameNotRunning);
        }

        if (page?.WebSocketDebuggerUrl is null || !Uri.TryCreate(page.WebSocketDebuggerUrl, UriKind.Absolute, out var webSocketUrl))
        {
            _logger.LogInformation("No debuggable NUI page found");
            return ExtractionResult.Failed(ExtractionFailure.GameNotRunning);
        }

        // Phase 2: talk CDP to that page.
        await using var cdp = new CdpClient(_logger);
        var contexts = new List<(string FrameId, bool IsDefault, long ContextId)>();
        cdp.EventReceived += (method, args) =>
        {
            if (method == "Runtime.executionContextCreated" && TryReadContext(args, out var context))
            {
                lock (contexts)
                {
                    contexts.Add(context);
                }
            }
        };

        try
        {
            await cdp.ConnectAsync(webSocketUrl, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Could not open the CDP WebSocket: {Message}", ex.Message);
            return ExtractionResult.Failed(ExtractionFailure.GameNotRunning);
        }

        try
        {
            await cdp.SendAsync("Runtime.enable", null, token);
            await cdp.SendAsync("Page.enable", null, token);
            await Task.Delay(_options.ContextEventDelay, token);

            var tree = await cdp.SendAsync("Page.getFrameTree", null, token);
            var chatFrameId = FindChatFrameId(tree);
            if (chatFrameId is null)
            {
                _logger.LogInformation("No chat frame in the NUI page (main menu?)");
                return ExtractionResult.Failed(ExtractionFailure.NotInSession);
            }

            long contextId;
            lock (contexts)
            {
                // Main world only; fall back to any context of the frame if isDefault never arrived.
                var forFrame = contexts.Where(c => c.FrameId == chatFrameId).ToList();
                if (forFrame.Count == 0)
                {
                    _logger.LogWarning("Chat frame {FrameId} has no known execution context", chatFrameId);
                    return ExtractionResult.Failed(ExtractionFailure.ChatUnavailable);
                }

                contextId = forFrame.FirstOrDefault(c => c.IsDefault, forFrame[0]).ContextId;
            }

            var evaluated = await cdp.SendAsync("Runtime.evaluate", new { expression = ExtractScript, contextId, returnByValue = true }, token);
            var commands = ParseEvaluatePayload(evaluated);
            if (commands is null)
            {
                _logger.LogWarning("The chat NUI did not yield backingSuggestions (custom chat resource?)");
                return ExtractionResult.Failed(ExtractionFailure.ChatUnavailable);
            }

            var normalized = Normalize(commands);
            _logger.LogInformation("Extracted {Count} commands from the chat NUI", normalized.Count);
            return ExtractionResult.Ok(normalized);
        }
        catch (Exception ex) when (ex is CdpException or JsonException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Command extraction failed mid-session: {Message}", ex.Message);
            return ExtractionResult.Failed(ExtractionFailure.ChatUnavailable);
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Finds the chat frame in a <c>Page.getFrameTree</c> result by name or nui url.</summary>
    public static string? FindChatFrameId(JsonElement frameTreeResult)
    {
        return frameTreeResult.ValueKind == JsonValueKind.Object && frameTreeResult.TryGetProperty("frameTree", out var root)
            ? Walk(root)
            : null;

        static string? Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (node.TryGetProperty("frame", out var frame) && IsChatFrame(frame)
                && frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }

            if (node.TryGetProperty("childFrames", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (Walk(child) is { } found)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        static bool IsChatFrame(JsonElement frame)
        {
            var name = frame.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name == "chat")
            {
                return true;
            }

            var url = frame.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            return url is not null
                && (url.StartsWith("nui://chat/", StringComparison.OrdinalIgnoreCase)
                    || url.Contains("cfx-nui-chat/", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Reads frame id, main-world flag and context id from a <c>Runtime.executionContextCreated</c> event.</summary>
    public static bool TryReadContext(JsonElement eventArgs, out (string FrameId, bool IsDefault, long ContextId) context)
    {
        context = default;
        if (eventArgs.ValueKind != JsonValueKind.Object
            || !eventArgs.TryGetProperty("context", out var element)
            || !element.TryGetProperty("id", out var id) || !id.TryGetInt64(out var contextId)
            || !element.TryGetProperty("auxData", out var auxData)
            || !auxData.TryGetProperty("frameId", out var frame) || frame.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var isDefault = auxData.TryGetProperty("isDefault", out var flag) && flag.ValueKind == JsonValueKind.True;
        context = (frame.GetString()!, isDefault, contextId);
        return true;
    }

    /// <summary>
    /// Unwraps the <c>Runtime.evaluate</c> response: the script returns a JSON string, so the raw command list
    /// sits two layers deep. <c>null</c> when the shape is unexpected or the script reported <c>found:false</c>.
    /// </summary>
    public static List<NuiCommand>? ParseEvaluatePayload(JsonElement evaluateResult)
    {
        if (evaluateResult.ValueKind != JsonValueKind.Object
            || evaluateResult.TryGetProperty("exceptionDetails", out _)
            || !evaluateResult.TryGetProperty("result", out var result)
            || !result.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(value.GetString()!);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("commands", out var commands))
            {
                return null;
            }

            return commands.Deserialize<List<NuiCommand>>(FxJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Chat suggests <c>/jail</c>; the console socket takes <c>jail</c> — strip the leading slash, drop empties,
    /// deduplicate preferring the entry that carries help/params, and sort for a stable cache (design memo §3.10).
    /// </summary>
    public static List<NuiCommand> Normalize(IEnumerable<NuiCommand> commands)
    {
        var byName = new Dictionary<string, NuiCommand>(StringComparer.Ordinal);
        foreach (var raw in commands)
        {
            var name = raw.Name.Trim().TrimStart('/');
            if (name.Length == 0)
            {
                continue;
            }

            var candidate = new NuiCommand
            {
                Name = name,
                Help = string.IsNullOrWhiteSpace(raw.Help) ? null : raw.Help.Trim(),
                Params = raw.Params is { Count: > 0 }
                    ? raw.Params.Select(p => new NuiCommandParam
                    {
                        Name = p.Name.Trim(),
                        Help = string.IsNullOrWhiteSpace(p.Help) ? null : p.Help.Trim(),
                        Type = string.IsNullOrWhiteSpace(p.Type) ? null : p.Type.Trim(),
                        Optional = p.Optional,
                    }).ToList()
                    : null,
            };

            if (!byName.TryGetValue(name, out var existing) || (!HasDetail(existing) && HasDetail(candidate)))
            {
                byName[name] = candidate;
            }
        }

        return byName.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        static bool HasDetail(NuiCommand command) => command.Help is not null || command.Params is { Count: > 0 };
    }
}
