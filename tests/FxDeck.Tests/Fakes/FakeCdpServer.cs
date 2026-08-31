using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FxDeck.Config;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxDeck.Tests.Fakes;

/// <summary>One iframe the fake NUI page reports, with the execution context it announces.</summary>
public sealed class FakeCdpFrame
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? Name { get; set; }

    public string Url { get; set; } = "nui://game/ui/root.html";

    /// <summary>Announced via <c>Runtime.executionContextCreated</c> after <c>Runtime.enable</c>; null = no context.</summary>
    public long? ContextId { get; set; }

    public bool IsDefault { get; set; } = true;
}

/// <summary>
/// Stand-in for the FiveM CEF debug endpoint (port 13172): <c>GET /json</c> plus a WebSocket speaking just
/// enough CDP for <c>ChatCommandExtractor</c>. There is no emulator for the real thing (design memo §3.10),
/// so this covers the framing and flow; the live path is verified in-game.
/// </summary>
public sealed class FakeCdpServer : IAsyncDisposable
{
    private WebApplication? _app;

    public FakeCdpFrame RootFrame { get; } = new() { Url = "nui://game/ui/root.html", ContextId = 1 };

    public List<FakeCdpFrame> ChildFrames { get; } = [];

    /// <summary>JSON string handed back by <c>Runtime.evaluate</c> (the extract script's return value).</summary>
    public string EvaluateValue { get; set; } = """{"found":false}""";

    /// <summary>Respond to <c>Runtime.evaluate</c> with a CDP error instead of a result.</summary>
    public bool FailEvaluate { get; set; }

    /// <summary>Messages are sent in fragments of this size to exercise reassembly.</summary>
    public int FragmentSize { get; set; } = 16 * 1024;

    /// <summary>Context id the client evaluated in (captured for assertions).</summary>
    public long? EvaluatedContextId { get; private set; }

    public int Port { get; private set; }

    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o => o.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();

        _app.UseWebSockets();
        _app.MapGet("/json", () => Results.Json(new[]
        {
            new
            {
                type = "page",
                title = "CitizenFX root UI",
                url = RootFrame.Url,
                webSocketDebuggerUrl = $"ws://127.0.0.1:{Port}/devtools/page/1",
            },
        }));
        _app.Map("/devtools/page/1", HandleWebSocketAsync);

        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                await HandleCommandAsync(socket, message.ToArray(), context.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // client went away
        }
    }

    private async Task HandleCommandAsync(WebSocket socket, byte[] request, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(request);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt64();
        var method = root.GetProperty("method").GetString();
        switch (method)
        {
            case "Runtime.enable":
                await SendAsync(socket, new { id, result = new { } }, ct);
                foreach (var frame in AllFrames().Where(f => f.ContextId is not null))
                {
                    await SendAsync(socket, new
                    {
                        method = "Runtime.executionContextCreated",
                        @params = new { context = new { id = frame.ContextId!.Value, auxData = new { frameId = frame.Id, isDefault = frame.IsDefault } } },
                    }, ct);
                }

                break;

            case "Page.getFrameTree":
                await SendAsync(socket, new
                {
                    id,
                    result = new
                    {
                        frameTree = new
                        {
                            frame = FrameJson(RootFrame),
                            childFrames = ChildFrames.Select(f => new { frame = FrameJson(f), childFrames = Array.Empty<object>() }),
                        },
                    },
                }, ct);
                break;

            case "Runtime.evaluate":
                EvaluatedContextId = root.GetProperty("params").TryGetProperty("contextId", out var contextId) ? contextId.GetInt64() : null;
                if (FailEvaluate)
                {
                    await SendAsync(socket, new { id, error = new { message = "evaluate failed" } }, ct);
                }
                else
                {
                    await SendAsync(socket, new { id, result = new { result = new { type = "string", value = EvaluateValue } } }, ct);
                }

                break;

            default:
                await SendAsync(socket, new { id, result = new { } }, ct);
                break;
        }
    }

    private IEnumerable<FakeCdpFrame> AllFrames() => [RootFrame, .. ChildFrames];

    private static object FrameJson(FakeCdpFrame frame) => new { id = frame.Id, name = frame.Name, url = frame.Url };

    private async Task SendAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, FxJson.Wire);
        for (var offset = 0; offset < bytes.Length; offset += FragmentSize)
        {
            var length = Math.Min(FragmentSize, bytes.Length - offset);
            var last = offset + length >= bytes.Length;
            await socket.SendAsync(bytes.AsMemory(offset, length), WebSocketMessageType.Text, endOfMessage: last, ct);
        }
    }
}
