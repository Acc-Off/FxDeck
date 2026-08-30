namespace FxDeck.Commands;

public enum MacroFailureReason
{
    None,

    /// <summary>The game is not connected; the command was not sent and the rest of the macro was skipped.</summary>
    NotConnected,

    /// <summary>A command contained characters the protocol cannot carry (NUL, line breaks).</summary>
    InvalidCommand,

    /// <summary>The caller cancelled the macro.</summary>
    Cancelled,

    /// <summary>The executor was disposed before or while the macro ran.</summary>
    Disposed,
}

/// <param name="StepsCompleted">Number of steps that finished before success or the first failure.</param>
public sealed record MacroExecutionResult(
    bool Success,
    MacroFailureReason Reason,
    int StepsCompleted,
    int StepCount,
    string? Message = null)
{
    public static MacroExecutionResult Ok(int stepCount) => new(true, MacroFailureReason.None, stepCount, stepCount);
}
