using System.Net.Sockets;
using System.Reflection;
using FxDeck.Commands;
using FxDeck.Config;
using FxDeck.FxConsole;
using FxDeck.Localization;
using FxDeck.NuiInspect;
using FxDeck.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace FxDeck.Web;

/// <summary>Localhost-only API (the middleware in <see cref="FxDeckHost"/> hides it on the deck listener). Design memo §3.3.</summary>
public static class AdminEndpoints
{
    public sealed record SendRequest(string? Command);

    public sealed record GameTestRequest(string? Host, int? Port);

    public sealed record AutoStartRequest(bool Enabled);

    /// <summary>Shape of <c>status.tunnel</c> and of the start/stop responses (design memo §3.3).</summary>
    private static object TunnelJson(TunnelState state, TunnelSettings settings, string token) => new
    {
        mode = settings.Mode,
        autoStart = settings.AutoStart,
        status = state.Status.ToString().ToLowerInvariant(),
        activeMode = state.Status == TunnelStatus.Stopped ? null : state.Mode,
        url = state.IsRunning ? state.Url : null,
        deckUrl = state.IsRunning && state.Url is not null ? DeckLinks.TunnelUrl(state.Url, token) : null,
        error = state.Error is null ? null : new { phase = state.Error.Phase.ToString().ToLowerInvariant(), message = state.Error.Message, exitCode = state.Error.ExitCode },
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin");

        admin.MapGet("/status", (ConfigStore config, DeckTokenStore tokens, IFxConsoleClient client, DeckHub hub, ListenerInfo listeners, TunnelService tunnel) =>
        {
            listeners.EnsureResolved();
            var settings = config.Current.Settings;
            var lan = LanAddress.Detect(settings.LanAdapter);
            return Results.Json(new
            {
                game = DeckMessages.GameState(client.State),
                gameEndpoint = $"{settings.Game.Host}:{settings.Game.Port}",
                adminPort = listeners.AdminPort,
                deckPort = listeners.DeckPort,
                lanAddress = lan?.ToString(),
                deckUrl = lan is null ? null : DeckLinks.LanUrl(lan, listeners.DeckPort, tokens.Token),
                deckUrlWithoutToken = lan is null ? null : DeckLinks.LanUrlWithoutToken(lan, listeners.DeckPort),
                connectedDecks = hub.ConnectedCount,
                tunnel = TunnelJson(tunnel.State, settings.Tunnel, tokens.Token),
                dataDirectory = config.Directory,
                configPath = config.ConfigPath,
                restartRequired = listeners.RequiresRestart(settings),
            }, FxJson.Wire);
        });

        admin.MapGet("/qr", (string? kind, ConfigStore config, DeckTokenStore tokens, ListenerInfo listeners, TunnelService tunnel) =>
        {
            listeners.EnsureResolved();
            if (kind == "tunnel")
            {
                var state = tunnel.State;
                if (!state.IsRunning || state.Url is null)
                {
                    return Results.NotFound(new { error = state.IsRunning ? "tunnelUrlNotConfigured" : "tunnelNotRunning" });
                }

                return Results.Bytes(QrRenderer.ToPng(DeckLinks.TunnelUrl(state.Url, tokens.Token)), "image/png");
            }

            if (kind is not (null or "lan"))
            {
                return Results.NotFound(new { error = "unknownKind" });
            }

            var lan = LanAddress.Detect(config.Current.Settings.LanAdapter);
            if (lan is null)
            {
                return Results.NotFound(new { error = "noLanAddress" });
            }

            var png = QrRenderer.ToPng(DeckLinks.LanUrl(lan, listeners.DeckPort, tokens.Token));
            return Results.Bytes(png, "image/png");
        });

        admin.MapGet("/config", (ConfigStore config) => Results.Json(config.Current, FxJson.Options));

        // The admin UI auto-saves by PUTting the whole document.
        admin.MapPut("/config", (AppConfig? incoming, ConfigStore config, ListenerInfo listeners) =>
        {
            // Validate in the language the incoming document asks for: the UI switches at the same moment.
            var errors = ConfigValidator.Validate(incoming, Strings.Resolve(incoming?.Settings?.Language));
            if (errors.Count > 0)
            {
                return Results.Json(new { errors }, FxJson.Wire, statusCode: StatusCodes.Status400BadRequest);
            }

            // Ports are only read at start-up; tell the UI when a restart is needed.
            incoming!.Settings.Tunnel.NamedToken ??= config.Current.Settings.Tunnel.NamedToken;
            config.Save(incoming);
            return Results.Json(new { ok = true, restartRequired = listeners.RequiresRestart(incoming.Settings) }, FxJson.Wire);
        });

        admin.MapPost("/token/rotate", (DeckTokenStore tokens) =>
        {
            tokens.Rotate();
            return Results.Json(new { ok = true }, FxJson.Wire);
        });

        // "Test send" from the admin UI: a raw macro, not a key.
        admin.MapPost("/send", async (SendRequest request, MacroExecutor executor) =>
        {
            if (string.IsNullOrWhiteSpace(request.Command))
            {
                return Results.BadRequest(new { error = "commandRequired" });
            }

            var result = await executor.ExecuteAsync(request.Command);
            return Results.Json(new
            {
                success = result.Success,
                reason = DeckMessages.ReasonName(result.Reason),
                stepsCompleted = result.StepsCompleted,
                stepCount = result.StepCount,
                message = result.Message,
            }, FxJson.Wire);
        });

        admin.MapGet("/export", (string? profile, ConfigStore config, AssetStore assets) =>
        {
            var current = config.Current;
            if (profile is null)
            {
                return Results.File(ConfigPackage.ExportAll(current, assets), "application/zip", ConfigPackage.ExportFileName(null));
            }

            var target = current.Profiles.FirstOrDefault(p => p.Id == profile);
            return target is null
                ? Results.NotFound(new { error = "profileNotFound" })
                : Results.File(ConfigPackage.ExportProfile(target, assets), "application/zip", ConfigPackage.ExportFileName(target));
        });

        admin.MapPost("/import", async (IFormFile file, string? mode, ConfigStore config, AssetStore assets, Localizer l, ILoggerFactory loggers) =>
        {
            var importMode = mode switch
            {
                null or "profile" => ImportMode.Profile,
                "all" => ImportMode.All,
                _ => (ImportMode?)null,
            };
            if (importMode is null)
            {
                return Results.BadRequest(new { error = l.T("api.importModeInvalid"), code = "importModeInvalid" });
            }

            if (file.Length > 32 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = l.T("api.fileTooLarge"), code = "fileTooLarge" });
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            ImportResult result;
            try
            {
                result = ConfigPackage.Import(buffer.ToArray(), importMode.Value, config.Current, assets, l.Current);
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message, code = "invalidPackage" });
            }

            var errors = ConfigValidator.Validate(result.Config, l.Current);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { error = l.T("api.importInvalid"), code = "importInvalid", errors });
            }

            config.Save(result.Config);
            loggers.CreateLogger("FxDeck.Import").LogInformation("Imported {Count} profile(s) from {File} ({Mode})", result.ProfilesAdded, file.FileName, importMode);
            return Results.Json(new { ok = true, profilesAdded = result.ProfilesAdded, warnings = result.Warnings }, FxJson.Wire);
        }).DisableAntiforgery();

        admin.MapGet("/firewall/status", async (FirewallService firewall, ListenerInfo listeners, CancellationToken ct) =>
        {
            listeners.EnsureResolved();
            var status = await firewall.GetStatusAsync(listeners.DeckPort, ct);
            return Results.Json(new { ruleExists = status.RuleExists, portAllowed = status.PortAllowed, blocked = status.Blocked, port = status.Port, ruleName = FirewallService.RuleName }, FxJson.Wire);
        });

        admin.MapPost("/firewall/allow", async (FirewallService firewall, ListenerInfo listeners, CancellationToken ct) =>
        {
            listeners.EnsureResolved();
            var result = await firewall.AllowAsync(listeners.DeckPort, ct);
            return Results.Json(new { outcome = result.Outcome.ToString().ToLowerInvariant(), message = result.Message, port = listeners.DeckPort }, FxJson.Wire);
        });

        admin.MapGet("/network/adapters", (ConfigStore config) =>
        {
            var selected = config.Current.Settings.LanAdapter;
            var auto = LanAddress.Detect(null)?.ToString();
            return Results.Json(new
            {
                selected,
                automatic = auto,
                adapters = LanAddress.ListCandidates().Select(c => new { id = c.AdapterId, name = c.AdapterName, address = c.Address.ToString(), hasGateway = c.HasGateway }),
            }, FxJson.Wire);
        });

        // User images (design memo §3.8).
        admin.MapGet("/assets", (AssetStore assets, ConfigStore config) =>
        {
            var referenced = AssetStore.ReferencedHashes(config.Current);
            return Results.Json(new
            {
                assets = assets.List().Select(a => new { hash = a.Hash, size = a.Size, modified = a.ModifiedUtc, referenced = referenced.Contains(a.Hash) }),
            }, FxJson.Wire);
        });

        admin.MapPost("/assets", async (IFormFile file, AssetStore assets, Localizer l) =>
        {
            if (file.Length > 16 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = l.T("api.imageTooLarge"), code = "imageTooLarge" });
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            try
            {
                return Results.Json(new { hash = assets.Save(buffer.ToArray()) }, FxJson.Wire);
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = l.T("asset.notImage"), code = "notImage" });
            }
        }).DisableAntiforgery();

        admin.MapPost("/assets/prune", (AssetStore assets, ConfigStore config, ILoggerFactory loggers) =>
        {
            var deleted = assets.DeleteUnused(config.Current);
            loggers.CreateLogger("FxDeck.Assets").LogInformation("Deleted {Count} unused image(s)", deleted);
            return Results.Json(new { deleted }, FxJson.Wire);
        });

        admin.MapPost("/game/test", async (GameTestRequest request, ConfigStore config, Localizer l, CancellationToken ct) =>
        {
            var host = string.IsNullOrWhiteSpace(request.Host) ? config.Current.Settings.Game.Host : request.Host;
            var port = request.Port ?? config.Current.Settings.Game.Port;
            if (port is < 1 or > 65535)
            {
                return Results.BadRequest(new { error = l.T("api.portInvalid"), code = "portInvalid" });
            }

            try
            {
                using var tcp = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await tcp.ConnectAsync(host, port, timeout.Token);
                return Results.Json(new { ok = true, message = l.T("api.gameTestOk", host, port) }, FxJson.Wire);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return Results.Json(new { ok = false, message = l.T("api.gameTestFailed", host, port) }, FxJson.Wire);
            }
        });

        admin.MapGet("/autostart", (AutoStartService autoStart) => Results.Json(new { enabled = autoStart.IsEnabled(), command = autoStart.Command }, FxJson.Wire));

        admin.MapPut("/autostart", (AutoStartRequest request, AutoStartService autoStart, ConfigStore config) =>
        {
            autoStart.SetEnabled(request.Enabled);
            var current = config.Current;
            if (current.Settings.AutoStart != request.Enabled)
            {
                current.Settings.AutoStart = request.Enabled;
                config.Save(current);
            }

            return Results.Json(new { enabled = autoStart.IsEnabled() }, FxJson.Wire);
        });

        // Tunnel (design memo §3.5). Start waits until cloudflared is ready or has failed, so the UI gets a definite
        // answer; the request's cancellation is deliberately not propagated (closing the tab must not abort the start).
        admin.MapPost("/tunnel/start", async (TunnelService tunnel, ConfigStore config, DeckTokenStore tokens) =>
        {
            var state = await tunnel.StartAsync();
            var body = TunnelJson(state, config.Current.Settings.Tunnel, tokens.Token);
            return state.Status == TunnelStatus.Error
                ? Results.Json(new { tunnel = body, error = state.Error?.Message }, FxJson.Wire, statusCode: StatusCodes.Status502BadGateway)
                : Results.Json(new { tunnel = body }, FxJson.Wire);
        });

        admin.MapPost("/tunnel/stop", async (TunnelService tunnel, ConfigStore config, DeckTokenStore tokens) =>
        {
            var state = await tunnel.StopAsync();
            return Results.Json(new { tunnel = TunnelJson(state, config.Current.Settings.Tunnel, tokens.Token) }, FxJson.Wire);
        });

        admin.MapPost("/restart", (AppLifecycle lifecycle) =>
        {
            lifecycle.RequestRestart();
            return Results.Json(new { ok = true }, FxJson.Wire);
        });

        // NUI command extraction (design memo §3.10): explicit user action only — never polled.
        admin.MapPost("/commands/extract", async (ChatCommandExtractor extractor, CommandCacheStore commands, Localizer l, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var result = await extractor.ExtractAsync(ct);
            if (!result.Success)
            {
                var code = result.Failure switch
                {
                    ExtractionFailure.GameNotRunning => "gameNotRunning",
                    ExtractionFailure.NotInSession => "notInSession",
                    _ => "chatUnavailable",
                };
                return Results.Json(new { error = l.T("api.commands." + code), code }, FxJson.Wire, statusCode: StatusCodes.Status409Conflict);
            }

            var cache = new CommandCache
            {
                ExtractedAt = DateTimeOffset.Now,
                Server = null, // no cheap source for a server label yet (design memo §3.10)
                Count = result.Commands.Count,
                Commands = [.. result.Commands],
            };
            commands.Save(cache);
            loggers.CreateLogger("FxDeck.Commands").LogInformation("Cached {Count} extracted commands", cache.Count);
            return Results.Json(cache, FxJson.Wire);
        });

        admin.MapGet("/commands", (CommandCacheStore commands) =>
            commands.Current is { } cache
                ? Results.Json(cache, FxJson.Wire)
                : Results.Json(new { commands = Array.Empty<object>() }, FxJson.Wire));

        admin.MapDelete("/commands", (CommandCacheStore commands) =>
        {
            commands.Delete();
            return Results.Json(new { ok = true }, FxJson.Wire);
        });

        admin.MapGet("/about", () =>
        {
            var assembly = typeof(AdminEndpoints).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "?";
            using var stream = assembly.GetManifestResourceStream("THIRD-PARTY-NOTICES.md");
            var notices = stream is null ? string.Empty : new StreamReader(stream).ReadToEnd();
            return Results.Json(new
            {
                name = "FxDeck",
                version = version.Split('+')[0],
                license = "MIT",
                repository = "https://github.com/Acc-Off/FxDeck",
                thirdPartyNotices = notices,
            }, FxJson.Wire);
        });
    }
}
