using System.Threading.Channels;
using FxDeck.FxConsole;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FxDeck.Commands;

/// <summary>
/// Runs button macros strictly one after another (FIFO) against an <see cref="IFxConsoleClient"/>.
/// A macro stops at its first failed command. The 25 ms send gap is the client's responsibility.
/// </summary>
public sealed class MacroExecutor : IAsyncDisposable
{
    private sealed record Job(
        string Macro,
        IReadOnlyList<MacroStep> Steps,
        CancellationToken CancellationToken,
        TaskCompletionSource<MacroExecutionResult> Completion);

    private readonly IFxConsoleClient _client;
    private readonly CommandMacroParser _parser;
    private readonly ILogger _logger;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _pending;
    private bool _disposed;

    public MacroExecutor(IFxConsoleClient client, CommandMacroParser? parser = null, ILogger<MacroExecutor>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _parser = parser ?? CommandMacroParser.Default;
        _logger = logger ?? NullLogger<MacroExecutor>.Instance;
        _worker = Task.Run(WorkerAsync);
    }

    /// <summary>Macros queued or currently running.</summary>
    public int PendingCount => Volatile.Read(ref _pending);

    public bool IsBusy => PendingCount > 0;

    /// <summary>Queues <paramref name="macro"/>; the returned task completes when it has run.</summary>
    public Task<MacroExecutionResult> ExecuteAsync(string macro, CancellationToken cancellationToken = default)
    {
        var steps = _parser.Parse(macro);
        var completion = new TaskCompletionSource<MacroExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new Job(macro, steps, cancellationToken, completion);

        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(job))
        {
            Interlocked.Decrement(ref _pending);
            completion.SetResult(Disposed(steps.Count));
        }

        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private static MacroExecutionResult Disposed(int stepCount) =>
        new(false, MacroFailureReason.Disposed, 0, stepCount, "The executor has been disposed.");

    private async Task WorkerAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var job))
                {
                    var result = await RunAsync(job).ConfigureAwait(false);
                    Interlocked.Decrement(ref _pending);
                    job.Completion.TrySetResult(result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }

        while (reader.TryRead(out var job))
        {
            Interlocked.Decrement(ref _pending);
            job.Completion.TrySetResult(Disposed(job.Steps.Count));
        }
    }

    private async Task<MacroExecutionResult> RunAsync(Job job)
    {
        var completed = 0;
        var total = job.Steps.Count;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken, _shutdown.Token);
        var token = linked.Token;

        try
        {
            foreach (var step in job.Steps)
            {
                token.ThrowIfCancellationRequested();
                switch (step)
                {
                    case CommandStep command:
                        if (!await _client.SendAsync(command.Command, token).ConfigureAwait(false))
                        {
                            _logger.LogWarning("Macro {Macro} aborted: not connected", job.Macro);
                            return new MacroExecutionResult(
                                false,
                                MacroFailureReason.NotConnected,
                                completed,
                                total,
                                $"Not connected; '{command.Command}' was not sent.");
                        }

                        break;

                    case DelayStep delay:
                        await Task.Delay(delay.Delay, token).ConfigureAwait(false);
                        break;
                }

                completed++;
            }

            return MacroExecutionResult.Ok(total);
        }
        catch (OperationCanceledException)
        {
            var reason = _shutdown.IsCancellationRequested ? MacroFailureReason.Disposed : MacroFailureReason.Cancelled;
            return new MacroExecutionResult(false, reason, completed, total);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Macro {Macro} aborted: {Message}", job.Macro, ex.Message);
            return new MacroExecutionResult(false, MacroFailureReason.InvalidCommand, completed, total, ex.Message);
        }
    }
}
