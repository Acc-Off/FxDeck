using System.Net.Sockets;
using System.Text;
using FxDeck.Emulator;

// FiveM/RedM console socket emulator: listens on 29200, logs every command it receives and answers with PRNT.
// Lines typed on stdin are broadcast to connected clients as PRNT frames (fake console output).

Console.OutputEncoding = Encoding.UTF8; // Japanese messages regardless of the console code page

var options = new FxConsoleEmulatorOptions { Log = Console.Out };

foreach (var arg in args)
{
    var parts = arg.TrimStart('-').Split('=', 2);
    var key = parts[0];
    var value = parts.Length > 1 ? parts[1] : null;
    switch (key)
    {
        case "host":
            options.Host = value ?? options.Host;
            break;
        case "port":
            if (!int.TryParse(value, out var port) || port is < 0 or > 65535)
            {
                Console.Error.WriteLine($"Invalid --port value: {value} (0-65535)");
                return 64;
            }

            options.Port = port;
            break;
        case "idle":
            options.IdleTimeout = TimeSpan.FromMilliseconds(int.Parse(value ?? "0"));
            break;
        case "delay":
            options.ReplyDelay = TimeSpan.FromMilliseconds(int.Parse(value ?? "0"));
            break;
        case "garbage":
            options.PrefixGarbage = true;
            break;
        case "split":
            options.SplitReplies = true;
            break;
        case "no-reply":
            options.ReplyToCommands = false;
            break;
        case "help":
        case "h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {arg}");
            PrintUsage();
            return 64;
    }
}

await using var emulator = new FxConsoleEmulator(options);
try
{
    await emulator.StartAsync();
}
catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
{
    Console.Error.WriteLine($"Port {options.Port} is in use. Is FiveM running?");
    return 1;
}

var flags = new List<string>();
if (options.IdleTimeout > TimeSpan.Zero) flags.Add($"idle={options.IdleTimeout.TotalMilliseconds:0}ms");
if (options.ReplyDelay > TimeSpan.Zero) flags.Add($"delay={options.ReplyDelay.TotalMilliseconds:0}ms");
if (options.PrefixGarbage) flags.Add("garbage");
if (options.SplitReplies) flags.Add("split");
if (!options.ReplyToCommands) flags.Add("no-reply");

Console.WriteLine("========================================");
Console.WriteLine("  FxDeck — FiveM/RedM console emulator");
Console.WriteLine($"  Listening on {options.Host}:{emulator.Port}");
if (flags.Count > 0)
{
    Console.WriteLine($"  Options: {string.Join(", ", flags)}");
}

Console.WriteLine("========================================");
Console.WriteLine("Waiting for FxDeck. Lines typed here are sent to connected clients as PRNT. Ctrl+C to quit.");
Console.WriteLine();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

try
{
    while (!shutdown.IsCancellationRequested)
    {
        var line = await Console.In.ReadLineAsync(shutdown.Token);
        if (line is null)
        {
            // stdin closed (e.g. started from a script): keep serving until Ctrl+C / process kill.
            await Task.Delay(Timeout.Infinite, shutdown.Token);
            break;
        }

        if (!string.IsNullOrWhiteSpace(line))
        {
            await emulator.BroadcastPrintAsync(line);
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C
}

await emulator.StopAsync();
return 0;

static void PrintUsage()
{
    Console.WriteLine("""
        Usage: FxDeck.Emulator [--host=<ip>] [--port=<port>] [--idle=<ms>] [--delay=<ms>] [--garbage] [--split] [--no-reply]

          --host=<ip>     Address to bind (default 127.0.0.1; use 0.0.0.0 to accept other PCs)
          --port=<port>   Listening port (default 29200; 0 picks a free port)
          --idle=<ms>     Idle timeout before disconnecting, ms (default 5000; 0 disables; the real game drops after about 5 s)
          --delay=<ms>    Delay PRNT replies by this many ms
          --garbage       Prepend garbage bytes to replies (tests resynchronisation)
          --split         Send replies in two TCP chunks (tests buffering)
          --no-reply      Do not answer commands with PRNT
        """);
}
