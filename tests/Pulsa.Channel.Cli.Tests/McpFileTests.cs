using System.Text.Json.Nodes;
using AwesomeAssertions;
using PulsaChannel;
using PulsaChannel.Cli;
using Xunit;

namespace Pulsa.Channel.Cli.Tests;

public sealed class McpFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pulsa-mcp-files-" + Guid.NewGuid().ToString("N"));

    public McpFileTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "note.txt"), "hello file");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private async Task<(McpHandler Handler, FakeChannelProvider Provider)> StartedAsync(string? fileRoot)
    {
        var provider = new FakeChannelProvider();
        var connector = new ChannelConnector(provider, fileRoot: fileRoot);
        await connector.StartAsync(TestContext.Current.CancellationToken);
        return (new McpHandler(connector), provider);
    }

    private static Task<JsonObject?> SendAsync(McpHandler handler, JsonObject arguments) =>
        handler.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "channel_send", ["arguments"] = arguments },
        }.ToJsonString(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_send_tool_takes_a_file_path_and_the_file_reaches_the_provider()
    {
        var (handler, provider) = await StartedAsync(root);

        var reply = await SendAsync(handler, new JsonObject { ["conversation"] = "conversation-1", ["text"] = "The note", ["file"] = "note.txt" });

        reply!["result"]!["structuredContent"]!["delivered"]!.GetValue<bool>().Should().BeTrue();
        provider.SentFiles.Should().ContainSingle().Which.Name.Should().Be("note.txt");
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_the_folder_is_a_tool_error_with_invalid_request_and_nothing_is_sent()
    {
        var (handler, provider) = await StartedAsync(root);

        var reply = await SendAsync(handler, new JsonObject { ["conversation"] = "conversation-1", ["text"] = "x", ["file"] = "../outside.txt" });

        reply!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        reply["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>().Should().Be("invalid_request");
        provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connector_without_a_folder_answers_not_supported()
    {
        var (handler, provider) = await StartedAsync(null);

        var reply = await SendAsync(handler, new JsonObject { ["conversation"] = "conversation-1", ["text"] = "x", ["file"] = "note.txt" });

        reply!["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>().Should().Be("not_supported");
        provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_not_a_string_is_an_invalid_params_error()
    {
        var (handler, _) = await StartedAsync(root);

        var reply = await SendAsync(handler, new JsonObject { ["conversation"] = "conversation-1", ["text"] = "x", ["file"] = 5 });

        reply!["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }
}
