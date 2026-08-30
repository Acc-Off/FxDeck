using System.Text;
using System.Windows.Forms;
using FxDeck.Commands;
using FxDeck.Config;
using FxDeck.FxConsole;
using FxDeck.Localization;
using FxDeck.Tray;
using FxDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FxDeck;

/// <summary>
/// FxDeck lives in the tray. The web host (deck + admin UI) runs in the background;
/// <c>--console</c> additionally prints the deck URL / QR to the terminal, <c>--send</c> skips the host entirely.
/// Messages printed before the configuration is loaded follow the OS language (design memo §3.9).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var hostOptions = new FxDeckHostOptions();
        string? oneShot = null;
        var connectWait = TimeSpan.FromSeconds(10);
        var console = false;
        var lang = Strings.FromCulture();
        string T(string key, params object?[] a) => Strings.Get(lang, key, a);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host":
                    hostOptions.GameHost = Next("--host");
                    break;
                case "--port":
                    hostOptions.GamePort = int.Parse(Next("--port"));
                    break;
                case "--deck-port":
                    hostOptions.DeckPort = int.Parse(Next("--deck-port"));
                    break;
                case "--admin-port":
                    hostOptions.AdminPort = int.Parse(Next("--admin-port"));
                    break;
                case "--data-dir":
                    hostOptions.DataDirectory = Path.GetFullPath(Next("--data-dir"));
                    break;
                case "--send":
                    oneShot = Next("--send");
                    break;
                case "--timeout":
                    connectWait = TimeSpan.FromMilliseconds(int.Parse(Next("--timeout")));
                    break;
                case "--console":
                    console = true;
                    break;
                case "--verbose":
                case "-v":
                    hostOptions.MinimumLogLevel = LogLevel.Debug;
                    break;
                case "--help":
                case "-h":
                    ConsoleAttach.TryAttach(allocateIfNone: true);
                    Console.WriteLine(T("program.usage"));
                    return 0;
                default:
                    ConsoleAttach.TryAttach(allocateIfNone: true);
                    Console.Error.WriteLine(T("program.unknownArg", args[i]));
                    Console.WriteLine(T("program.usage"));
                    return 64;
            }

            string Next(string name)
            {
                if (i + 1 >= args.Length)
                {
                    ConsoleAttach.TryAttach(allocateIfNone: true);
                    Console.Error.WriteLine(T("program.needsValue", name));
                    Environment.Exit(64);
                }

                return args[++i];
            }
        }

        if (oneShot is not null)
        {
            ConsoleAttach.TryAttach(allocateIfNone: true);
            return SendOnceAsync(hostOptions, oneShot, connectWait, lang).GetAwaiter().GetResult();
        }

        hostOptions.ConsoleLogging = console && ConsoleAttach.TryAttach(allocateIfNone: false);

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);

        Directory.CreateDirectory(hostOptions.DataDirectory);
        using var instance = SingleInstance.Acquire(hostOptions.DataDirectory);
        if (!instance.IsFirst)
        {
            var existing = SingleInstance.ReadAdminUrl(hostOptions.DataDirectory);
            if (existing is not null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(existing) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(T("program.alreadyRunning"), "FxDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return 0;
        }

        WebApplication app;
        try
        {
            app = FxDeckHost.Build(hostOptions);
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var message = ex is IOException && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                ? T("program.portInUse", ex.Message)
                : T("program.startFailed", ex.Message);
            if (hostOptions.ConsoleLogging)
            {
                Console.Error.WriteLine(message);
            }

            MessageBox.Show(message, "FxDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 4;
        }

        var listeners = app.Services.GetRequiredService<ListenerInfo>();
        listeners.EnsureResolved();
        var adminUrl = $"http://127.0.0.1:{listeners.AdminPort}/admin/";
        SingleInstance.PublishAdminUrl(hostOptions.DataDirectory, adminUrl);

        if (hostOptions.ConsoleLogging)
        {
            PrintBanner(app.Services, adminUrl);
        }

        var firstRun = app.Services.GetRequiredService<ConfigStore>().CreatedDefault;
        bool restart;
        using (var context = new TrayApplicationContext(app, adminUrl, openAdminOnStart: firstRun))
        {
            Application.Run(context);
            restart = context.RestartRequested;
        }

        try
        {
            File.Delete(SingleInstance.AdminUrlPath(hostOptions.DataDirectory));
        }
        catch (IOException)
        {
            // best effort
        }

        instance.Dispose(); // release the single-instance mutex before the successor starts
        if (restart && Environment.ProcessPath is { } exe)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                ArgumentList = { },
            }.WithArguments(args));
        }

        // Nothing of ours should keep the process alive, but never leave a ghost in the tray-less background.
        Environment.Exit(0);
        return 0;
    }

    private static System.Diagnostics.ProcessStartInfo WithArguments(this System.Diagnostics.ProcessStartInfo info, string[] arguments)
    {
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static void PrintBanner(IServiceProvider services, string adminUrl)
    {
        var listeners = services.GetRequiredService<ListenerInfo>();
        var config = services.GetRequiredService<ConfigStore>();
        var tokens = services.GetRequiredService<DeckTokenStore>();
        var l = services.GetRequiredService<Localizer>();
        var settings = config.Current.Settings;
        var lan = LanAddress.Detect(settings.LanAdapter);

        Console.WriteLine();
        Console.WriteLine("================ FxDeck ================");
        Console.WriteLine(l.T("program.banner.config", config.ConfigPath));
        Console.WriteLine(l.T("program.banner.admin", adminUrl));
        if (lan is null)
        {
            Console.WriteLine(l.T("program.banner.noLan"));
        }
        else
        {
            var url = DeckLinks.LanUrl(lan, listeners.DeckPort, tokens.Token);
            Console.WriteLine(l.T("program.banner.deckUrl", url));
            Console.WriteLine();
            Console.Write(QrRenderer.ToConsoleString(url));
        }

        Console.WriteLine(l.T("program.banner.exit"));
        Console.WriteLine("========================================");
    }

    private static async Task<int> SendOnceAsync(FxDeckHostOptions options, string macro, TimeSpan connectWait, Lang lang)
    {
        string T(string key, params object?[] a) => Strings.Get(lang, key, a);
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(options.MinimumLogLevel)
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            }));

        var clientOptions = new FxConsoleClientOptions
        {
            Host = options.GameHost ?? FxConsoleProtocol.DefaultHost,
            Port = options.GamePort ?? FxConsoleProtocol.DefaultPort,
        };
        await using var client = new TcpFxConsoleClient(clientOptions, loggerFactory.CreateLogger<TcpFxConsoleClient>());
        await using var executor = new MacroExecutor(client, logger: loggerFactory.CreateLogger<MacroExecutor>());
        client.LineReceived += (_, e) => Console.WriteLine($"[PRNT] {e.Line}");

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, e) =>
        {
            Console.WriteLine($"[state] {Describe(e.Current, lang)}");
            if (e.Current == FxConsoleConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };

        Console.WriteLine(T("program.send.connecting", clientOptions.Host, clientOptions.Port));
        client.Start();
        if (await Task.WhenAny(connected.Task, Task.Delay(connectWait)) != connected.Task)
        {
            Console.Error.WriteLine(T("program.send.timeout", clientOptions.Host, clientOptions.Port, connectWait.TotalSeconds.ToString("0")));
            return 2;
        }

        var result = await executor.ExecuteAsync(macro);
        Console.WriteLine(result.Success
            ? T("program.send.ok", result.StepsCompleted)
            : T("program.send.failed", result.Reason, result.StepsCompleted, result.StepCount, result.Message is null ? string.Empty : ": " + result.Message));
        await client.StopAsync();
        return result.Success ? 0 : 1;
    }

    private static string Describe(FxConsoleConnectionState state, Lang lang) => state switch
    {
        FxConsoleConnectionState.Connected => Strings.Get(lang, "program.state.connected"),
        FxConsoleConnectionState.Connecting => Strings.Get(lang, "program.state.connecting"),
        _ => Strings.Get(lang, "program.state.disconnected"),
    };
}
