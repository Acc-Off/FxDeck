using FxDeck.Commands;

namespace FxDeck.Tests.Commands;

public class CommandMacroParserTests
{
    private static readonly CommandMacroParser Parser = new();

    private static CommandStep Cmd(string command) => new(command);

    private static DelayStep Delay(int milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    public static TheoryData<string, MacroStep[]> Cases => new()
    {
        // single commands
        { "e wave", [Cmd("e wave")] },
        { "  e wave  ", [Cmd("e wave")] },
        { "e wave;", [Cmd("e wave")] },
        { "say こんにちは 👋", [Cmd("say こんにちは 👋")] },

        // ; chains
        { "a;b;c", [Cmd("a"), Cmd("b"), Cmd("c")] },
        { "a ; b ;c", [Cmd("a"), Cmd("b"), Cmd("c")] },
        { "e sit;me relaxes on the ground", [Cmd("e sit"), Cmd("me relaxes on the ground")] },
        { "say こんにちは; e wave", [Cmd("say こんにちは"), Cmd("e wave")] },

        // line breaks behave like ;
        { "a\nb", [Cmd("a"), Cmd("b")] },
        { "a\r\nb\n", [Cmd("a"), Cmd("b")] },

        // {NNNms} delays
        { "a;{500ms};b", [Cmd("a"), Delay(500), Cmd("b")] },
        { "a{500ms}b", [Cmd("a"), Delay(500), Cmd("b")] },
        { "a; {1500MS} ; b", [Cmd("a"), Delay(1500), Cmd("b")] },
        { "a;{ 250 ms };b", [Cmd("a"), Delay(250), Cmd("b")] },
        { "{500ms}", [Delay(500)] },
        { "a{500ms}", [Cmd("a"), Delay(500)] },
        { "{100ms}{200ms}", [Delay(100), Delay(200)] },
        { "{0ms}", [Delay(0)] },
        { "e think;me thinking;{2000ms};e c", [Cmd("e think"), Cmd("me thinking"), Delay(2000), Cmd("e c")] },

        // ;; shorthand for the default delay
        { "a;;b", [Cmd("a"), Delay(500), Cmd("b")] },
        { ";;", [Delay(500)] },
        { "a;;", [Cmd("a"), Delay(500)] },
        { ";;a", [Delay(500), Cmd("a")] },
        { "a;;;b", [Cmd("a"), Delay(500), Cmd("b")] },
        { "a;;;;b", [Cmd("a"), Delay(500), Delay(500), Cmd("b")] },
        { ";;;", [Delay(500)] },
        { "e sit;;me relaxes", [Cmd("e sit"), Delay(500), Cmd("me relaxes")] },

        // not delays: passed through as command text
        { "a;{500}b", [Cmd("a"), Cmd("{500}b")] },
        { "say {value} now", [Cmd("say {value} now")] },
        { "{500ms", [Cmd("{500ms")] },
        { "{ms}", [Cmd("{ms}")] },
        { "{500 s}", [Cmd("{500 s}")] },
        { "{-5ms}", [Cmd("{-5ms}")] },

        // empty pieces are dropped
        { ";", [] },
        { "; ;", [] },
        { ";a;;;", [Cmd("a"), Delay(500)] },

        // delays are clamped to 60 s
        { "{60000ms}", [Delay(60_000)] },
        { "{70000ms}", [Delay(60_000)] },
        { "{99999999999999999999999ms}", [Delay(60_000)] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parses(string macro, MacroStep[] expected)
    {
        Assert.Equal(expected, Parser.Parse(macro));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\r\n")]
    public void BlankMacroYieldsNoSteps(string? macro)
    {
        Assert.Empty(Parser.Parse(macro));
    }

    [Fact]
    public void ChainDelayIsConfigurable()
    {
        var parser = new CommandMacroParser(chainDelay: TimeSpan.FromMilliseconds(200));

        Assert.Equal([Cmd("a"), Delay(200), Cmd("b")], parser.Parse("a;;b"));
    }

    [Fact]
    public void MaxDelayIsConfigurableAndAppliesToChainDelay()
    {
        var parser = new CommandMacroParser(maxDelay: TimeSpan.FromMilliseconds(100));

        Assert.Equal([Delay(100), Delay(100), Delay(50)], parser.Parse(";;{5000ms}{50ms}"));
    }

    [Fact]
    public void NegativeDelaysAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandMacroParser(chainDelay: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandMacroParser(maxDelay: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void DefaultsMatchFxcommands()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), CommandMacroParser.DefaultChainDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), CommandMacroParser.DefaultMaxDelay);
        Assert.Same(CommandMacroParser.Default, CommandMacroParser.Default);
    }
}
