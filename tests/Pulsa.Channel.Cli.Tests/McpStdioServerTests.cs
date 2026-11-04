using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

    [Fact]
    public async Task Input_of_the_wrong_shape_gets_an_error_answer_and_the_session_carries_on()
    {
        await using var session = new Session();

        session.SendRaw("""{"jsonrpc":"2.0","id":1,"method":5}""");
        var badMethod = await session.NextAsync();
        var badName = await session.CallAsync(2, "tools/call", new JsonObject { ["name"] = 5 });
        var badArguments = await session.CallAsync(3, "tools/call", new JsonObject { ["name"] = "channel_status", ["arguments"] = "text" });
        var stillAlive = await session.CallAsync(4, "ping");

        badMethod["error"]!["code"]!.GetValue<int>().Should().Be(-32600);
        badName["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        badArguments["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        stillAlive["result"].Should().NotBeNull();
    }

    [Fact]
    public async Task Poll_names_its_epoch_and_a_cursor_from_another_run_starts_over()
    {
        await using var session = new Session();
        session.Provider.Receive("conversation-1", "person-a", "hello");

        var first = (await session.ToolAsync(1, "channel_poll"))["result"]!["structuredContent"]!;
        var epoch = first["epoch"]!.GetValue<string>();
        var sameRun = (await session.ToolAsync(2, "channel_poll", new JsonObject { ["after"] = 2, ["epoch"] = epoch }))["result"]!["structuredContent"]!;
        var otherRun = (await session.ToolAsync(3, "channel_poll", new JsonObject { ["after"] = 2, ["epoch"] = "a-run-that-ended" }))["result"]!["structuredContent"]!;

        first["reset"]!.GetValue<bool>().Should().BeFalse();
        sameRun["reset"]!.GetValue<bool>().Should().BeFalse();
        sameRun["events"]!.AsArray().Should().BeEmpty();
        otherRun["reset"]!.GetValue<bool>().Should().BeTrue();
        otherRun["events"]!.AsArray().Should().HaveCount(2, "a cursor from another run reads from the start instead of silently getting nothing");
    }

    [Fact]
    public async Task Status_does_not_reach_outside_and_poll_does_because_it_returns_what_strangers_wrote()
    {
        await using var session = new Session();

        var tools = (await session.CallAsync(1, "tools/list"))["result"]!["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!["annotations"]!);

        tools["channel_status"]["openWorldHint"]!.GetValue<bool>().Should().BeFalse();
        tools["channel_poll"]["openWorldHint"]!.GetValue<bool>().Should().BeTrue();
        tools["channel_send"]["openWorldHint"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Text_on_standard_input_is_read_as_utf8_whatever_the_consoles_code_page_is()
    {
        var input = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"한글 도구","arguments":{}}}""" + "\n";

        var run = RunCli("serve", input);

        var reply = (JsonObject)JsonNode.Parse(run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0])!;
        reply["error"]!["message"]!.GetValue<string>().Should().Contain("한글 도구");
    }

    /// <summary>A stand-in for the Telegram Bot API on a local port: records every request address and answers the three methods the connector uses.</summary>
    private sealed class FakeBotApi : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly List<(string Path, string Body)> seen = [];
        private int updatesServed;

        public FakeBotApi()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Root = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(Root);
            listener.Start();
            _ = Task.Run(Serve);
        }

        public string Root { get; }

        public IReadOnlyList<(string Path, string Body)> Seen
        {
            get { lock (seen) { return seen.ToList(); } }
        }

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var path = context.Request.Url!.AbsolutePath;
                    lock (seen) { seen.Add((path, body)); }
                    string answer;
                    if (path.EndsWith("/getMe")) answer = """{"ok":true,"result":{"id":1,"is_bot":true}}""";
                    else if (path.EndsWith("/sendMessage")) answer = """{"ok":true,"result":{"message_id":9}}""";
                    else if (path.EndsWith("/getUpdates"))
                    {
                        if (Interlocked.Increment(ref updatesServed) == 1) answer = """{"ok":true,"result":[{"update_id":5,"message":{"message_id":1,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"안녕하세요"}}]}""";
                        else { await Task.Delay(200); answer = """{"ok":true,"result":[]}"""; }
                    }
                    else answer = """{"ok":false}""";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                });
            }
        }

        public void Dispose() => listener.Close();
    }

    private const string FakeToken = "999:never-print-this";

    [Fact]
    public void Status_of_a_real_service_asks_whether_the_credential_works_and_reads_no_messages()
    {
        using var api = new FakeBotApi();

        var run = RunCli("status --provider telegram", null, ("PULSA_CHANNEL_TELEGRAM_TOKEN", FakeToken), ("PULSA_CHANNEL_TELEGRAM_API", api.Root));

        run.Exit.Should().Be(0);
        JsonNode.Parse(run.Out)!["state"]!.GetValue<string>().Should().Be("connected");
        run.Out.Should().NotContain(FakeToken);
        api.Seen.Select(r => r.Path).Should().Equal([$"/bot{FakeToken}/getMe"], "getUpdates would have confirmed messages nobody read");
    }

    [Fact]
    public void A_one_shot_send_to_a_real_service_posts_the_message_and_reads_nothing()
    {
        using var api = new FakeBotApi();

        var run = RunCli("send --provider telegram --conversation 555 --text 반갑습니다", null, ("PULSA_CHANNEL_TELEGRAM_TOKEN", FakeToken), ("PULSA_CHANNEL_TELEGRAM_API", api.Root));

        run.Exit.Should().Be(0);
        JsonNode.Parse(run.Out)!["messageId"]!.GetValue<string>().Should().Be("555:9");
        var request = api.Seen.Should().ContainSingle().Which;
        request.Path.Should().Be($"/bot{FakeToken}/sendMessage");
        JsonNode.Parse(request.Body)!["text"]!.GetValue<string>().Should().Be("반갑습니다");
    }

    [Fact]
    public void The_server_over_a_real_service_hands_the_agent_what_a_person_wrote_to_a_client_that_follows_the_cursor()
    {
        using var api = new FakeBotApi();
        var start = new ProcessStartInfo("dotnet", $"\"{CliDll}\" serve --provider telegram") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["PULSA_CHANNEL_TELEGRAM_TOKEN"] = FakeToken;
        start.Environment["PULSA_CHANNEL_TELEGRAM_API"] = api.Root;
        using var process = Process.Start(start)!;
        var transcript = new StringBuilder();
        JsonObject Ask(int id, string method, JsonObject? parameters)
        {
            process.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
            process.StandardInput.Flush();
            var line = process.StandardOutput.ReadLine()!;
            transcript.AppendLine(line);
            return (JsonObject)JsonNode.Parse(line)!;
        }

        Ask(1, "initialize", new JsonObject());
        long after = 0;
        string? epoch = null;
        string? text = null;
        for (var id = 2; id < 12 && text is null; id++)
        {
            var arguments = new JsonObject { ["after"] = after, ["timeoutMs"] = 5000 };
            if (epoch is not null) arguments["epoch"] = epoch;
            var poll = Ask(id, "tools/call", new JsonObject { ["name"] = "channel_poll", ["arguments"] = arguments })["result"]!["structuredContent"]!;
            epoch = poll["epoch"]!.GetValue<string>();
            foreach (var e in poll["events"]!.AsArray())
            {
                after = e!["seq"]!.GetValue<long>();
                if (e["type"]!.GetValue<string>() == "message_received") text = e["text"]!.GetValue<string>();
            }
        }

        process.StandardInput.Close();
        process.WaitForExit(30000).Should().BeTrue();
        text.Should().Be("안녕하세요");
        transcript.ToString().Should().NotContain(FakeToken);
    }
}
