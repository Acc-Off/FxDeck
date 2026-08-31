using System.Text.Json;
using FxDeck.NuiInspect;

namespace FxDeck.Tests.NuiInspect;

/// <summary>Pure parsing/normalisation helpers of the extractor (design memo §3.10).</summary>
public class ChatCommandExtractorTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void NormalizeStripsLeadingSlashAndSorts()
    {
        var result = ChatCommandExtractor.Normalize(
        [
            new NuiCommand { Name = "/me" },
            new NuiCommand { Name = "/Car" },
            new NuiCommand { Name = "fix" },
        ]);

        Assert.Equal(["Car", "fix", "me"], result.Select(c => c.Name));
    }

    [Fact]
    public void NormalizeDropsEmptyNames()
    {
        var result = ChatCommandExtractor.Normalize(
        [
            new NuiCommand { Name = "  " },
            new NuiCommand { Name = "/" },
            new NuiCommand { Name = "ok" },
        ]);

        Assert.Equal(["ok"], result.Select(c => c.Name));
    }

    [Fact]
    public void NormalizePrefersTheEntryWithHelpOrParams()
    {
        var result = ChatCommandExtractor.Normalize(
        [
            new NuiCommand { Name = "/jail" },
            new NuiCommand { Name = "jail", Help = "Jail a player", Params = [new NuiCommandParam { Name = "id" }] },
            new NuiCommand { Name = "jail" }, // a later bare duplicate must not win either
        ]);

        var command = Assert.Single(result);
        Assert.Equal("jail", command.Name);
        Assert.Equal("Jail a player", command.Help);
        Assert.Equal("id", Assert.Single(command.Params!).Name);
    }

    [Fact]
    public void NormalizeTurnsEmptyHelpAndParamsIntoNull()
    {
        var result = ChatCommandExtractor.Normalize([new NuiCommand { Name = "wave", Help = "", Params = [] }]);

        var command = Assert.Single(result);
        Assert.Null(command.Help);
        Assert.Null(command.Params);
    }

    [Fact]
    public void FindChatFrameIdMatchesByNameOrUrlAnywhereInTheTree()
    {
        var tree = Parse("""
        {
          "frameTree": {
            "frame": { "id": "root", "url": "nui://game/ui/root.html" },
            "childFrames": [
              { "frame": { "id": "map", "name": "map", "url": "nui://map/index.html" }, "childFrames": [] },
              { "frame": { "id": "chatframe", "name": "chat", "url": "nui://chat/dist/ui.html" }, "childFrames": [] }
            ]
          }
        }
        """);
        Assert.Equal("chatframe", ChatCommandExtractor.FindChatFrameId(tree));

        var byUrl = Parse("""
        {
          "frameTree": {
            "frame": { "id": "root", "url": "nui://game/ui/root.html" },
            "childFrames": [
              { "frame": { "id": "x1", "url": "https://cfx-nui-chat/dist/ui.html" }, "childFrames": [] }
            ]
          }
        }
        """);
        Assert.Equal("x1", ChatCommandExtractor.FindChatFrameId(byUrl));
    }

    [Fact]
    public void FindChatFrameIdReturnsNullWithoutAChatFrame()
    {
        var tree = Parse("""
        {
          "frameTree": {
            "frame": { "id": "root", "url": "https://cfx.re/" },
            "childFrames": []
          }
        }
        """);
        Assert.Null(ChatCommandExtractor.FindChatFrameId(tree));
    }

    [Fact]
    public void TryReadContextReadsFrameAndWorld()
    {
        var ok = ChatCommandExtractor.TryReadContext(
            Parse("""{ "context": { "id": 7, "auxData": { "frameId": "chatframe", "isDefault": true } } }"""),
            out var context);

        Assert.True(ok);
        Assert.Equal(("chatframe", true, 7L), context);

        Assert.False(ChatCommandExtractor.TryReadContext(Parse("""{ "context": { "id": 7 } }"""), out _));
    }

    [Fact]
    public void ParseEvaluatePayloadUnwrapsTheDoubleEncodedCommands()
    {
        var payload = """{"found":true,"commands":[{"name":"/jail","help":"Jail","params":[{"name":"id","optional":false}]}]}""";
        var evaluated = Parse($$"""{ "result": { "type": "string", "value": {{JsonSerializer.Serialize(payload)}} } }""");

        var commands = ChatCommandExtractor.ParseEvaluatePayload(evaluated);

        var command = Assert.Single(commands!);
        Assert.Equal("/jail", command.Name);
        Assert.Equal("Jail", command.Help);
        Assert.False(Assert.Single(command.Params!).Optional);
    }

    [Theory]
    [InlineData("""{ "result": { "type": "string", "value": "{\"found\":false}" } }""")] // custom chat
    [InlineData("""{ "result": { "type": "string", "value": "not json" } }""")]
    [InlineData("""{ "result": { "type": "undefined" } }""")]
    [InlineData("""{ "result": { "type": "string", "value": "{}" }, "exceptionDetails": { "text": "boom" } }""")]
    public void ParseEvaluatePayloadRejectsAnythingUnexpected(string json)
    {
        Assert.Null(ChatCommandExtractor.ParseEvaluatePayload(Parse(json)));
    }

    [Fact]
    public void PickPagePrefersTheCitizenFxRootUi()
    {
        var pages = new List<CdpPage>
        {
            new("page", "Some overlay", "https://example/", "ws://x/1"),
            new("page", "CitizenFX root UI", "nui://game/ui/root.html", "ws://x/2"),
        };
        Assert.Equal("ws://x/2", CdpClient.PickPage(pages)?.WebSocketDebuggerUrl);

        // Without a recognisable page, the first page-typed entry with a socket wins.
        var fallback = new List<CdpPage>
        {
            new("iframe", "frame", "nui://chat/", "ws://x/3"),
            new("page", "CFX UI", "https://cfx.re/", null),
            new("page", "CFX UI", "https://cfx.re/", "ws://x/4"),
        };
        Assert.Equal("ws://x/4", CdpClient.PickPage(fallback)?.WebSocketDebuggerUrl);

        Assert.Null(CdpClient.PickPage([]));
    }
}
