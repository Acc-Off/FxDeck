using System.Text.Json;
using FxDeck.NuiInspect;
using FxDeck.Tests.Fakes;
using static FxDeck.Tests.TestHelpers;

namespace FxDeck.Tests.NuiInspect;

/// <summary>
/// Full extraction flow against <see cref="FakeCdpServer"/> (page discovery, CDP framing, context
/// mapping, evaluate). The real 13172 endpoint has no emulator; the live path is verified in-game.
/// </summary>
public class ChatCommandExtractionIntegrationTests
{
    private static NuiInspectOptions OptionsFor(FakeCdpServer server) => new()
    {
        BaseAddress = server.BaseAddress,
        ContextEventDelay = TimeSpan.FromMilliseconds(100),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        OverallTimeout = TimeSpan.FromSeconds(5),
    };

    private static FakeCdpFrame ChatFrame(long contextId) => new()
    {
        Id = "chatframe",
        Name = "chat",
        Url = "nui://chat/dist/ui.html",
        ContextId = contextId,
    };

    [Fact]
    public async Task ExtractsNormalizedCommandsFromTheChatFrame()
    {
        await using var server = new FakeCdpServer();
        server.ChildFrames.Add(ChatFrame(contextId: 5));
        server.EvaluateValue = """
            {"found":true,"commands":[
              {"name":"/jail","help":"Jail a player","params":[{"name":"id","help":"player id","optional":false}]},
              {"name":"/jail","help":"","params":[]},
              {"name":"/fix","help":"Repair","params":[]}
            ]}
            """;
        await server.StartAsync();
        using var extractor = new ChatCommandExtractor(OptionsFor(server));

        var result = await extractor.ExtractAsync();

        Assert.True(result.Success);
        Assert.Equal(5, server.EvaluatedContextId); // the chat frame's main-world context, not the root's
        Assert.Equal(["fix", "jail"], result.Commands.Select(c => c.Name));
        var jail = result.Commands.Single(c => c.Name == "jail");
        Assert.Equal("Jail a player", jail.Help);
        Assert.Equal("player id", Assert.Single(jail.Params!).Help);
    }

    [Fact]
    public async Task ReassemblesLargeFragmentedResponses()
    {
        await using var server = new FakeCdpServer { FragmentSize = 4 * 1024 };
        server.ChildFrames.Add(ChatFrame(contextId: 5));
        var commands = Enumerable.Range(0, 600).Select(i => new { name = $"/command{i:D4}", help = new string('h', 400), @params = Array.Empty<object>() });
        server.EvaluateValue = JsonSerializer.Serialize(new { found = true, commands });
        await server.StartAsync();
        using var extractor = new ChatCommandExtractor(OptionsFor(server));

        var result = await extractor.ExtractAsync();

        Assert.True(result.Success);
        Assert.Equal(600, result.Commands.Count);
        Assert.Equal("command0000", result.Commands[0].Name);
    }

    [Fact]
    public async Task NoChatFrameMeansNotInSession()
    {
        await using var server = new FakeCdpServer();
        server.ChildFrames.Add(new FakeCdpFrame { Id = "map", Name = "map", Url = "nui://map/index.html", ContextId = 9 });
        await server.StartAsync();
        using var extractor = new ChatCommandExtractor(OptionsFor(server));

        var result = await extractor.ExtractAsync();

        Assert.Equal(ExtractionFailure.NotInSession, result.Failure);
    }

    [Fact]
    public async Task UnrecognisedChatStateMeansChatUnavailable()
    {
        await using var server = new FakeCdpServer();
        server.ChildFrames.Add(ChatFrame(contextId: 5));
        server.EvaluateValue = """{"found":false}"""; // custom chat resource without backingSuggestions
        await server.StartAsync();
        using var extractor = new ChatCommandExtractor(OptionsFor(server));

        var result = await extractor.ExtractAsync();

        Assert.Equal(ExtractionFailure.ChatUnavailable, result.Failure);
    }

    [Fact]
    public async Task EvaluateErrorMeansChatUnavailable()
    {
        await using var server = new FakeCdpServer { FailEvaluate = true };
        server.ChildFrames.Add(ChatFrame(contextId: 5));
        await server.StartAsync();
        using var extractor = new ChatCommandExtractor(OptionsFor(server));

        var result = await extractor.ExtractAsync();

        Assert.Equal(ExtractionFailure.ChatUnavailable, result.Failure);
    }

    [Fact]
    public async Task ChatFrameWithoutAContextMeansChatUnavailable()
    {
        await using var server = new FakeCdpServer();
        server.ChildFrames.Add(new FakeCdpFrame { Id = "chatframe", Name = "chat", Url = "nui://chat/dist/ui.html", ContextId = null });
        await server.StartAsync();
        using var extractor = new ChatCommandExtractor(OptionsFor(server));

        var result = await extractor.ExtractAsync();

        Assert.Equal(ExtractionFailure.ChatUnavailable, result.Failure);
    }

    [Fact]
    public async Task DeadPortMeansGameNotRunning()
    {
        var options = new NuiInspectOptions
        {
            BaseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}/"),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            OverallTimeout = TimeSpan.FromSeconds(5),
        };
        using var extractor = new ChatCommandExtractor(options);

        var result = await extractor.ExtractAsync();

        Assert.Equal(ExtractionFailure.GameNotRunning, result.Failure);
    }
}
