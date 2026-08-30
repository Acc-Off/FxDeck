using FxDeck.Services;
using static FxDeck.Services.FirewallService;

namespace FxDeck.Tests.Web;

public class FirewallServiceTests
{
    private const string Exe = @"C:\Apps\FxDeck\FxDeck.exe";

    [Fact]
    public void AddRuleArgumentsAreFixedTextPlusPort()
    {
        Assert.Equal(
            "advfirewall firewall add rule name=\"FxDeck\" dir=in action=allow protocol=TCP localport=20200",
            BuildAddRuleArguments(20200));
    }

    [Fact]
    public void AllowCommandDeletesOldRulesThenAddsThePortRule()
    {
        var command = BuildAllowCommandLine(20200);

        Assert.Equal("/c netsh advfirewall firewall delete rule name=\"FxDeck\" dir=in & netsh advfirewall firewall add rule name=\"FxDeck\" dir=in action=allow protocol=TCP localport=20200", command);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RejectsPortsOutOfRange(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildAddRuleArguments(port));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildAllowCommandLine(port));
    }

    [Theory]
    [InlineData("*", 20200, true)]
    [InlineData("", 20200, true)]
    [InlineData(null, 20200, true)]
    [InlineData("20200", 20200, true)]
    [InlineData("80,443,20200", 20200, true)]
    [InlineData("20000-21000", 20200, true)]
    [InlineData("80", 20200, false)]
    [InlineData("20201-20300", 20200, false)]
    [InlineData("RPC", 20200, false)]
    public void PortMatching(string? localPorts, int port, bool expected)
    {
        Assert.Equal(expected, PortMatches(localPorts, port));
    }

    [Fact]
    public void NoRulesMeansNothingAllowed()
    {
        var status = Evaluate([], 20200, Exe);

        Assert.False(status.RuleExists);
        Assert.False(status.PortAllowed);
        Assert.False(status.Blocked);
    }

    [Fact]
    public void OurPortRuleAllows()
    {
        var status = Evaluate([new Rule("FxDeck", null, true, 1, 1, 6, "20200")], 20200, Exe);

        Assert.True(status.RuleExists);
        Assert.True(status.PortAllowed);
        Assert.False(Evaluate([new Rule("FxDeck", null, true, 1, 1, 6, "20200")], 20201, Exe).PortAllowed);
    }

    [Fact]
    public void WindowsAutoAllowRuleForTheExeCounts()
    {
        var rules = new[]
        {
            new Rule("FxDeck", Exe.ToLowerInvariant(), true, 1, 1, 6, "*"),
            new Rule("FxDeck", Exe.ToLowerInvariant(), true, 1, 1, 17, "*"),
        };

        Assert.True(Evaluate(rules, 20200, Exe).PortAllowed);
    }

    [Fact]
    public void BlockRuleOverridesAllowRule()
    {
        var rules = new[]
        {
            new Rule("FxDeck", null, true, 1, 1, 6, "20200"),
            new Rule("FxDeck", Exe, true, 1, 0, 256, "*"),
        };

        var status = Evaluate(rules, 20200, Exe);

        Assert.True(status.Blocked);
        Assert.False(status.PortAllowed);
    }

    [Fact]
    public void DisabledOutboundAndForeignRulesAreIgnored()
    {
        var rules = new[]
        {
            new Rule("FxDeck", null, false, 1, 1, 6, "20200"), // disabled
            new Rule("FxDeck", null, true, 2, 1, 6, "20200"), // outbound
            new Rule("Other app", @"C:\Other\app.exe", true, 1, 0, 256, "*"), // not ours
        };

        var status = Evaluate(rules, 20200, Exe);

        Assert.True(status.RuleExists);
        Assert.False(status.PortAllowed);
        Assert.False(status.Blocked);
    }
}
