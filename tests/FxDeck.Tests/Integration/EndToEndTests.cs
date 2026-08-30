using FxDeck.Commands;
using FxDeck.Emulator;
using FxDeck.FxConsole;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.Integration;

/// <summary>Parser → executor → TCP client → emulator, the whole roadmap-step-1 pipeline.</summary>
public class EndToEndTests
{
    [Fact]
    public async Task MacroReachesTheGameInOrderWithDelays()
    {
        await using var emulator = new FxConsoleEmulator(EmulatorOptions());
        await emulator.StartAsync();
        var received = new List<EmulatorCommandEventArgs>();
        emulator.CommandReceived += (_, e) => { lock (received) received.Add(e); };

        await using var client = new TcpFxConsoleClient(ClientOptions(emulator.Port));
        await using var executor = new MacroExecutor(client);
        client.Start();
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Connected, "connected");

        var result = await executor.ExecuteAsync("e wave; {100ms}; e dance;;e c");

        Assert.True(result.Success, result.Message);
        Assert.Equal(5, result.StepsCompleted);
        await WaitForAsync(() => received.Count == 3, "all commands received");
        Assert.Equal(["e wave", "e dance", "e c"], received.Select(r => r.Command));
        Assert.True(Between(received[0].Timestamp, received[1].Timestamp) >= TimeSpan.FromMilliseconds(90));
        Assert.True(Between(received[1].Timestamp, received[2].Timestamp) >= TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task MacroSurvivesTheGamesIdleTimeoutMidMacro()
    {
        // The game drops idle sockets after ~5 s; a delay longer than that must not make the next command fail.
        await using var emulator = new FxConsoleEmulator(EmulatorOptions(idleTimeout: TimeSpan.FromMilliseconds(300)));
        await emulator.StartAsync();
        await using var client = new TcpFxConsoleClient(ClientOptions(emulator.Port));
        await using var executor = new MacroExecutor(client);
        client.Start();

        var result = await executor.ExecuteAsync("e wave; {700ms}; e dance");

        Assert.True(result.Success, result.Message);
        await WaitForAsync(() => emulator.ReceivedCommands.Count == 2, "both commands received");
        Assert.Equal(["e wave", "e dance"], emulator.ReceivedCommands);
        Assert.True(emulator.TotalConnections >= 2, "the idle timeout should have forced a reconnect");
    }

    [Fact]
    public async Task MacroFailsWithNotConnectedWhenTheGameIsDown()
    {
        await using var client = new TcpFxConsoleClient(ClientOptions(GetFreePort()));
        await using var executor = new MacroExecutor(client);
        client.Start();
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Disconnected, "connect attempt failed");

        var result = await executor.ExecuteAsync("e wave; e dance");

        Assert.False(result.Success);
        Assert.Equal(MacroFailureReason.NotConnected, result.Reason);
        Assert.Equal(0, result.StepsCompleted);
    }

    [Fact]
    public async Task ConsoleOutputFlowsBackWhileMacrosRun()
    {
        await using var emulator = new FxConsoleEmulator(EmulatorOptions());
        await emulator.StartAsync();
        await using var client = new TcpFxConsoleClient(ClientOptions(emulator.Port));
        await using var executor = new MacroExecutor(client);
        var lines = new List<string>();
        client.LineReceived += (_, e) => { lock (lines) lines.Add(e.Line); };
        client.Start();
        await WaitForAsync(() => client.State == FxConsoleConnectionState.Connected, "connected");

        await executor.ExecuteAsync("e wave; e dance");

        await WaitForAsync(() => lines.Count == 2, "replies received");
        Assert.Equal(["emulator: e wave", "emulator: e dance"], lines);
    }
}
