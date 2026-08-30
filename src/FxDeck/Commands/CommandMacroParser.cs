using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FxDeck.Commands;

/// <summary>
/// Parses the fxcommands-compatible macro notation used by a button:
/// <list type="bullet">
///   <item><c>cmd1; cmd2</c> — commands separated by <c>;</c> (line breaks work the same way)</item>
///   <item><c>{500ms}</c> — delay; <c>ms</c> is case-insensitive, whitespace inside the braces is allowed</item>
///   <item><c>;;</c> — shorthand for the default delay (500 ms)</item>
/// </list>
/// Commands are trimmed and empty ones dropped. Any other <c>{...}</c> is passed through as part of the command.
/// Delays are clamped to <see cref="MaxDelay"/>.
/// </summary>
public sealed partial class CommandMacroParser
{
    public static readonly TimeSpan DefaultChainDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(60);

    public static CommandMacroParser Default { get; } = new();

    public CommandMacroParser(TimeSpan? chainDelay = null, TimeSpan? maxDelay = null)
    {
        ChainDelay = chainDelay ?? DefaultChainDelay;
        MaxDelay = maxDelay ?? DefaultMaxDelay;
        ArgumentOutOfRangeException.ThrowIfNegative(ChainDelay.Ticks, nameof(chainDelay));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDelay.Ticks, nameof(maxDelay));
    }

    /// <summary>Delay inserted for <c>;;</c>.</summary>
    public TimeSpan ChainDelay { get; }

    /// <summary>Upper bound applied to every delay so a single button cannot block the queue for long.</summary>
    public TimeSpan MaxDelay { get; }

    public IReadOnlyList<MacroStep> Parse(string? macro)
    {
        var steps = new List<MacroStep>();
        if (string.IsNullOrWhiteSpace(macro))
        {
            return steps;
        }

        var current = new StringBuilder();
        var i = 0;
        while (i < macro.Length)
        {
            var c = macro[i];
            switch (c)
            {
                case ';' when i + 1 < macro.Length && macro[i + 1] == ';':
                    Flush(current, steps);
                    steps.Add(new DelayStep(Clamp(ChainDelay)));
                    i += 2;
                    continue;

                case ';':
                case '\n':
                case '\r':
                    Flush(current, steps);
                    i++;
                    continue;

                case '{':
                    var match = DelayPattern().Match(macro, i);
                    if (match.Success)
                    {
                        Flush(current, steps);
                        steps.Add(new DelayStep(ParseDelay(match.Groups[1].Value)));
                        i += match.Length;
                        continue;
                    }

                    break;
            }

            current.Append(c);
            i++;
        }

        Flush(current, steps);
        return steps;
    }

    private static void Flush(StringBuilder current, List<MacroStep> steps)
    {
        var command = current.ToString().Trim();
        current.Clear();
        if (command.Length > 0)
        {
            steps.Add(new CommandStep(command));
        }
    }

    private TimeSpan ParseDelay(string digits)
    {
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || ms > (long)MaxDelay.TotalMilliseconds)
        {
            return MaxDelay; // overflow or over the cap
        }

        return Clamp(TimeSpan.FromMilliseconds(ms));
    }

    private TimeSpan Clamp(TimeSpan delay) => delay > MaxDelay ? MaxDelay : delay;

    [GeneratedRegex(@"\G\{\s*(\d+)\s*ms\s*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelayPattern();
}
