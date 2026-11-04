using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AwesomeAssertions;
using PulsaChannel;
using PulsaChannel.Cli;
using Xunit;

namespace Pulsa.Channel.Cli.Tests;

public class McpStdioServerTests
{
    private sealed class Session : IAsyncDisposable
    {
        private readonly Channel<string> toServer = System.Threading.Channels.Channel.CreateUnbounded<string>();
        private readonly Channel<JsonObject> fromServer = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
        private readonly CancellationTokenSource stop = new();
        private readonly Task running;

        public Session()
        {
            Provider = new FakeChannelProvider();
            Connector = new ChannelConnector(Provider);
            Connector.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            running = new McpStdioServer(Connector, new QueueReader(toServer), new QueueWriter(fromServer)).RunAsync(stop.Token);
        }

        public FakeChannelProvider Provider { get; }

        public ChannelConnector Connector { get; }

        public void Send(JsonObject message) => toServer.Writer.TryWrite(message.ToJsonString());

        public void SendRaw(string line) => toServer.Writer.TryWrite(line);

        public async Task<JsonObject> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await fromServer.Reader.ReadAsync(timeout.Token);
        }

        public bool HasNoOutput => !fromServer.Reader.TryPeek(out _);

        public async Task<JsonObject> CallAsync(int id, string method, JsonObject? parameters = null)
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            return await NextAsync();
        }

        public Task<JsonObject> ToolAsync(int id, string name, JsonObject? arguments = null) =>
            CallAsync(id, "tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() });

        public async ValueTask DisposeAsync()
        {
            toServer.Writer.TryComplete();
            await stop.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            await Connector.DisposeAsync();
        }
    }

    private sealed class QueueReader(Channel<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await lines.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    private sealed class QueueWriter(Channel<JsonObject> messages) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => messages.Writer.TryWrite((JsonObject)JsonNode.Parse(value!)!);

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        public override Task FlushAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task Initialize_names_the_server_and_echoes_the_protocol_version_the_client_asked_for()
    {
        await using var session = new Session();

        var reply = await session.CallAsync(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });

        reply["id"]!.GetValue<int>().Should().Be(1);
        reply["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2025-03-26");
        reply["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("pulsa-channel");
        reply["result"]!["capabilities"]!["tools"].Should().NotBeNull();
    }

    [Fact]
    public async Task The_three_tools_are_listed_with_safety_hints_and_only_send_is_not_read_only()
    {
        await using var session = new Session();

        var reply = await session.CallAsync(2, "tools/list");

        var tools = reply["result"]!["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        tools.Keys.Should().BeEquivalentTo("channel_send", "channel_status", "channel_poll");
        tools["channel_send"]["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeFalse();
        tools["channel_send"]["annotations"]!["destructiveHint"]!.GetValue<bool>().Should().BeFalse();
        tools["channel_status"]["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();
        tools["channel_poll"]["annotations"]!["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();
        tools["channel_send"]["inputSchema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().BeEquivalentTo("conversation", "text");
    }

    [Fact]
    public async Task A_send_goes_to_the_provider_and_answers_with_the_message_id()
    {
        await using var session = new Session();

        var reply = await session.ToolAsync(3, "channel_send", new JsonObject { ["conversation"] = "conversation-1", ["text"] = "hello", ["link"] = "https://example.test/a" });

        reply["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
        reply["result"]!["structuredContent"]!["delivered"]!.GetValue<bool>().Should().BeTrue();
        reply["result"]!["structuredContent"]!["messageId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        JsonNode.Parse(reply["result"]!["content"]![0]!["text"]!.GetValue<string>())!["delivered"]!.GetValue<bool>().Should().BeTrue();
        session.Provider.Sent.Should().ContainSingle().Which.Link.Should().Be(new Uri("https://example.test/a"));
    }

    [Fact]
    public async Task A_send_the_contract_refuses_is_a_tool_error_the_model_can_read_not_a_protocol_error()
    {
        await using var session = new Session();

        var reply = await session.ToolAsync(4, "channel_send", new JsonObject { ["conversation"] = "conversation-1", ["text"] = "   " });

        reply["error"].Should().BeNull();
        reply["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        reply["result"]!["structuredContent"]!["error"]!["code"]!.GetValue<string>().Should().Be("invalid_request");
        reply["result"]!["structuredContent"]!["error"]!["retryable"]!.GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData("channel_send", """{"text":"no conversation"}""", "conversation is required")]
    [InlineData("channel_send", """{"conversation":5,"text":"x"}""", "conversation must be a string")]
    [InlineData("channel_poll", """{"max":0}""", "max must be between 1 and 100")]
    [InlineData("channel_poll", """{"timeoutMs":31000}""", "timeoutMs must be between 0 and 30000")]
    [InlineData("channel_poll", """{"after":"a"}""", "after must be an integer")]
    [InlineData("no_such_tool", "{}", "Unknown tool")]
    public async Task Arguments_that_do_not_fit_the_schema_are_an_invalid_params_error(string tool, string arguments, string mention)
    {
        await using var session = new Session();

        var reply = await session.ToolAsync(5, tool, (JsonObject)JsonNode.Parse(arguments)!);

        reply["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        reply["error"]!["message"]!.GetValue<string>().Should().Contain(mention);
    }

    [Fact]
    public async Task Status_reports_the_connection()
    {
        await using var session = new Session();

        var reply = await session.ToolAsync(6, "channel_status");

        reply["result"]!["structuredContent"]!["provider"]!.GetValue<string>().Should().Be("fake");
        reply["result"]!["structuredContent"]!["state"]!.GetValue<string>().Should().Be("connected");
    }

    [Fact]
    public async Task Poll_returns_what_people_wrote_by_cursor_and_does_not_consume_it()
    {
        await using var session = new Session();
        session.Provider.Receive("conversation-1", "person-a", "hello agent");

        var first = await session.ToolAsync(7, "channel_poll");
        var events = first["result"]!["structuredContent"]!["events"]!.AsArray();
        var message = events.Single(e => e!["type"]!.GetValue<string>() == "message_received")!;
        message["text"]!.GetValue<string>().Should().Be("hello agent");
        message["conversation"]!.GetValue<string>().Should().Be("conversation-1");
        message["seq"]!.GetValue<long>().Should().Be(2);

        var again = await session.ToolAsync(8, "channel_poll");
        again["result"]!["structuredContent"]!["events"]!.AsArray().Should().HaveCount(2, "a read does not consume");
        var after = await session.ToolAsync(9, "channel_poll", new JsonObject { ["after"] = 2 });
        after["result"]!["structuredContent"]!["events"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_ping_is_answered_while_a_long_poll_is_still_waiting_and_the_poll_finishes_when_a_message_arrives()
    {
        await using var session = new Session();
        session.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 10, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "channel_poll", ["arguments"] = new JsonObject { ["after"] = 1, ["timeoutMs"] = 20000 } } });
        session.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 11, ["method"] = "ping" });

        var pong = await session.NextAsync();
        pong["id"]!.GetValue<int>().Should().Be(11);

        session.Provider.Receive("conversation-1", "person-a", "late message");
        var poll = await session.NextAsync();
        poll["id"]!.GetValue<int>().Should().Be(10);
        poll["result"]!["structuredContent"]!["events"]![0]!["text"]!.GetValue<string>().Should().Be("late message");
    }

    [Fact]
    public async Task Notifications_and_blank_lines_get_no_answer_and_bad_input_is_a_protocol_error()
    {
        await using var session = new Session();
        session.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        session.SendRaw("");
        session.SendRaw("{ this is not json");
        var parseError = await session.NextAsync();
        var unknown = await session.CallAsync(12, "resources/list");

        parseError["error"]!["code"]!.GetValue<int>().Should().Be(-32700);
        unknown["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
        session.HasNoOutput.Should().BeTrue();
    }

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "PulsaChannel.Cli.dll");

    private static (int Exit, string Out) RunCli(string arguments, string? stdin = null, params (string Name, string Value)[] environment)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {arguments}") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        if (stdin is not null) process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000).Should().BeTrue("the process ends when its input ends");
        return (process.ExitCode, output);
    }

    [Fact]
    public void The_command_line_sends_through_the_fake_and_exits_zero_or_one_by_whether_it_was_delivered()
    {
        var delivered = RunCli("send --conversation conversation-1 --text hello");
        var refused = RunCli("send --conversation nobody --text hello");
        var misuse = RunCli("send --text only-text");

        delivered.Exit.Should().Be(0);
        JsonNode.Parse(delivered.Out)!["delivered"]!.GetValue<bool>().Should().BeTrue();
        refused.Exit.Should().Be(1);
        JsonNode.Parse(refused.Out)!["error"]!["code"]!.GetValue<string>().Should().Be("unknown_conversation");
        misuse.Exit.Should().Be(2);
    }

    [Fact]
    public void An_unknown_provider_or_a_missing_credential_is_a_usage_error_that_does_not_echo_the_credential()
    {
        RunCli("status --provider nonsense").Exit.Should().Be(2);
        var missing = RunCli("status --provider telegram");
        missing.Exit.Should().Be(2);
    }

    [Fact]
    public void The_server_runs_as_its_own_process_over_stdin_and_stdout_until_its_input_ends()
    {
        var input = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"channel_poll","arguments":{}}}""") + "\n";

        var run = RunCli("serve", input, ("PULSA_CHANNEL_FAKE_INBOUND", "conversation-1|person-a|hello from the environment"));

        run.Exit.Should().Be(0);
        var replies = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JsonObject)JsonNode.Parse(l)!).ToDictionary(r => r["id"]!.GetValue<int>());
        replies[1]["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("pulsa-channel");
        replies[2]["result"]!["structuredContent"]!["events"]!.AsArray().Select(e => e!["type"]!.GetValue<string>()).Should().Contain("message_received");
    }
}
