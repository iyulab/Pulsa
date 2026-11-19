using System.Text.Json.Nodes;
using AwesomeAssertions;
using PulsaChannel;
using PulsaChannel.Cli;
using Xunit;

namespace Pulsa.Channel.Cli.Tests;

public class McpChoicesTests
{
    private static async Task<(McpHandler Handler, FakeChannelProvider Provider, ChannelConnector Connector)> StartedAsync()
    {
        var provider = new FakeChannelProvider();
        var connector = new ChannelConnector(provider);
        await connector.StartAsync(TestContext.Current.CancellationToken);
        return (new McpHandler(connector), provider, connector);
    }

    private static Task<JsonObject?> CallAsync(McpHandler handler, string tool, JsonObject arguments) =>
        handler.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        }.ToJsonString(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_send_tool_describes_choices_as_an_array_of_id_and_label()
    {
        var (handler, _, _) = await StartedAsync();

        var listed = await handler.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", TestContext.Current.CancellationToken);

        var send = listed!["result"]!["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "channel_send")!;
        var choices = send["inputSchema"]!["properties"]!["choices"]!;
        choices["type"]!.GetValue<string>().Should().Be("array");
        choices["items"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().BeEquivalentTo("id", "label");
        send["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().BeEquivalentTo("conversation", "text");
    }

    [Fact]
    public async Task A_send_with_choices_reaches_the_provider_and_the_pick_comes_back_through_poll_as_choice_selected()
    {
        var (handler, provider, _) = await StartedAsync();

        var sent = await CallAsync(handler, "channel_send", new JsonObject
        {
            ["conversation"] = "conversation-1",
            ["text"] = "Send it?",
            ["choices"] = new JsonArray(new JsonObject { ["id"] = "yes", ["label"] = "Yes" }, new JsonObject { ["id"] = "no", ["label"] = "No" }),
        });
        var messageId = sent!["result"]!["structuredContent"]!["messageId"]!.GetValue<string>();
        provider.Sent.Should().ContainSingle().Which.Choices.Should().BeEquivalentTo([new ChannelChoice("yes", "Yes"), new ChannelChoice("no", "No")]);

        provider.Choose("person-1", messageId, "no").Should().BeTrue();
        var polled = await CallAsync(handler, "channel_poll", new JsonObject());

        var pick = polled!["result"]!["structuredContent"]!["events"]!.AsArray().Single(e => e!["type"]!.GetValue<string>() == "choice_selected")!;
        pick["conversation"]!.GetValue<string>().Should().Be("conversation-1");
        pick["sender"]!.GetValue<string>().Should().Be("person-1");
        pick["messageId"]!.GetValue<string>().Should().Be(messageId);
        pick["choiceId"]!.GetValue<string>().Should().Be("no");
        pick["closed"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"conversation":"c","text":"t","choices":"yes"}""", "choices must be an array")]
    [InlineData("""{"conversation":"c","text":"t","choices":["yes"]}""", "every choice must be an object")]
    [InlineData("""{"conversation":"c","text":"t","choices":[{"id":"a"}]}""", "label is required")]
    [InlineData("""{"conversation":"c","text":"t","choices":[{"id":1,"label":"x"}]}""", "id must be a string")]
    public async Task Choices_that_do_not_fit_the_schema_are_an_invalid_params_error(string arguments, string mention)
    {
        var (handler, _, _) = await StartedAsync();

        var reply = await CallAsync(handler, "channel_send", JsonNode.Parse(arguments)!.AsObject());

        reply!["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        reply["error"]!["message"]!.GetValue<string>().Should().Contain(mention);
    }

    [Fact]
    public async Task Too_many_choices_are_a_tool_error_with_invalid_request_not_a_protocol_error()
    {
        var (handler, provider, _) = await StartedAsync();
        var nine = new JsonArray(Enumerable.Range(1, 9).Select(i => (JsonNode)new JsonObject { ["id"] = $"c{i}", ["label"] = $"C{i}" }).ToArray());

        var reply = await CallAsync(handler, "channel_send", new JsonObject { ["conversation"] = "conversation-1", ["text"] = "Pick", ["choices"] = nine });

        reply!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        reply["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>().Should().Be("invalid_request");
        provider.Sent.Should().BeEmpty();
    }
}
