namespace FxDeck.Commands;

/// <summary>One step of a parsed button macro.</summary>
public abstract record MacroStep;

/// <summary>Send <see cref="Command"/> to the console.</summary>
public sealed record CommandStep(string Command) : MacroStep;

/// <summary>Wait for <see cref="Delay"/> before the next step.</summary>
public sealed record DelayStep(TimeSpan Delay) : MacroStep;
