using FxDeck.Commands;
using FxDeck.Tests.Fakes;

namespace FxDeck.Tests.Commands;

public class MacroExecutorTests
{
    [Fact]
    public async Task ExecutesCommandsInOrder()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);

        var result = await executor.ExecuteAsync("a; b ;c");

        Assert.True(result.Success);
        Assert.Equal(MacroFailureReason.None, result.Reason);
        Assert.Equal(3, result.StepsCompleted);
        Assert.Equal(3, result.StepCount);
        Assert.Equal(["a", "b", "c"], client.SentCommands);
        Assert.False(executor.IsBusy);
    }

    [Fact]
    public async Task EmptyMacroSucceedsWithoutSending()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);

        var result = await executor.ExecuteAsync("   ");

        Assert.True(result.Success);
        Assert.Equal(0, result.StepCount);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task HonoursDelays()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);

        var result = await executor.ExecuteAsync("a;{150ms};b");

        Assert.True(result.Success);
        Assert.Equal(3, result.StepsCompleted);
        var sent = client.Sent;
        Assert.Equal(2, sent.Count);
        Assert.True(TestHelpers.Between(sent[0].Timestamp, sent[1].Timestamp) >= TimeSpan.FromMilliseconds(140),
            "the delay step should separate the two sends");
    }

    [Fact]
    public async Task StopsAtFirstFailedSend()
    {
        var client = new FakeFxConsoleClient { Connected = false };
        await using var executor = new MacroExecutor(client);

        var result = await executor.ExecuteAsync("a;{10ms};b");

        Assert.False(result.Success);
        Assert.Equal(MacroFailureReason.NotConnected, result.Reason);
        Assert.Equal(0, result.StepsCompleted);
        Assert.Equal(3, result.StepCount);
        Assert.Contains("'a'", result.Message);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task AbortsRemainingStepsWhenConnectionDropsMidMacro()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);

        var task = executor.ExecuteAsync("a;{200ms};b;c");
        await TestHelpers.WaitForAsync(() => client.Sent.Count == 1, "first command sent");
        client.Connected = false;

        var result = await task;

        Assert.False(result.Success);
        Assert.Equal(MacroFailureReason.NotConnected, result.Reason);
        Assert.Equal(2, result.StepsCompleted); // "a" and the delay
        Assert.Equal(["a"], client.SentCommands);
    }

    [Fact]
    public async Task RunsMacrosStrictlyFifo()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);

        var first = executor.ExecuteAsync("a1;{30ms};a2");
        var second = executor.ExecuteAsync("b1;{30ms};b2");
        var third = executor.ExecuteAsync("c1");
        Assert.Equal(3, executor.PendingCount);
        Assert.True(executor.IsBusy);

        await Task.WhenAll(first, second, third);

        Assert.Equal(["a1", "a2", "b1", "b2", "c1"], client.SentCommands);
        Assert.Equal(0, executor.PendingCount);
    }

    [Fact]
    public async Task CancellationStopsTheMacroButNotTheQueue()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);
        using var cts = new CancellationTokenSource();

        var cancelled = executor.ExecuteAsync("a;{5000ms};b", cts.Token);
        var next = executor.ExecuteAsync("c");
        await TestHelpers.WaitForAsync(() => client.Sent.Count == 1, "first command sent");
        cts.Cancel();

        var result = await cancelled;
        Assert.False(result.Success);
        Assert.Equal(MacroFailureReason.Cancelled, result.Reason);
        Assert.Equal(1, result.StepsCompleted);

        var nextResult = await next;
        Assert.True(nextResult.Success);
        Assert.Equal(["a", "c"], client.SentCommands);
    }

    [Fact]
    public async Task AlreadyCancelledMacroIsSkipped()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);

        var result = await executor.ExecuteAsync("a", new CancellationToken(canceled: true));

        Assert.Equal(MacroFailureReason.Cancelled, result.Reason);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task InvalidCommandIsReportedNotThrown()
    {
        var client = new FakeFxConsoleClient();
        await using var executor = new MacroExecutor(client);

        var result = await executor.ExecuteAsync("a;b\0c");

        Assert.False(result.Success);
        Assert.Equal(MacroFailureReason.InvalidCommand, result.Reason);
        Assert.Equal(1, result.StepsCompleted);
        Assert.Equal(["a"], client.SentCommands);
    }

    [Fact]
    public async Task DisposeFailsPendingMacros()
    {
        var client = new FakeFxConsoleClient { SendLatency = TimeSpan.FromMilliseconds(300) };
        var executor = new MacroExecutor(client);

        var running = executor.ExecuteAsync("a");
        var queued = executor.ExecuteAsync("b");
        await Task.Delay(50);
        await executor.DisposeAsync();

        Assert.Equal(MacroFailureReason.Disposed, (await running).Reason);
        Assert.Equal(MacroFailureReason.Disposed, (await queued).Reason);
        Assert.Equal(MacroFailureReason.Disposed, (await executor.ExecuteAsync("c")).Reason);
        Assert.Empty(client.Sent);
    }
}
