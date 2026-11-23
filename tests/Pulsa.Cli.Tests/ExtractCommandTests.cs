using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Pulsa.Cli.Tests;

/// <summary>
/// The extract and verify commands run as a process, end to end: the way a person or a script uses grounded extraction
/// without a consuming application. The chat model is a local server answering in the OpenAI wire format; no ffmpeg, no key.
/// </summary>
public sealed class ExtractCommandTests : IDisposable
{
    private const string Text = "Invoice 2026-11-02 from Han River Supplies. Total due: 1,200 won. Paid by card, ref 77-12.";

    private const string Schema = """
        {"fields":[
          {"name":"total","type":"number","required":true},
          {"name":"issued","type":"date","required":true},
          {"name":"method","type":"enum","values":["card","cash"]},
          {"name":"ref","type":"string"}
        ]}
        """;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pulsa-cli-{Guid.NewGuid():N}");

    public ExtractCommandTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "Pulsa.Cli.dll");

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(CliDll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    private async Task<(string Text, string Schema)> WriteInputsAsync()
    {
        var textPath = Path.Combine(_tempDir, "invoice.txt");
        var schemaPath = Path.Combine(_tempDir, "schema.json");
        await File.WriteAllTextAsync(textPath, Text);
        await File.WriteAllTextAsync(schemaPath, Schema);
        return (textPath, schemaPath);
    }

    [Fact]
    public async Task Extract_AsksTheModel_ReturnsOnlyWhatTheTextSupports_AndNamesWhatItRefused()
    {
        var (text, schema) = await WriteInputsAsync();
        // The model gets the total right, invents a quote for the date, and gives a reference its quote does not carry.
        using var model = new FakeChatEndpoint("""
            {"fields":{
              "total":[{"value":"1200","quote":"1,200 won"}],
              "issued":[{"value":"2026-11-05","quote":"issued on 5 November 2026"}],
              "ref":[{"value":"99-99","quote":"ref 77-12"}]
            }}
            """);

        var (exitCode, stdout, stderr) = await RunAsync(
            "extract", "--text", text, "--schema", schema, "--openai-endpoint", model.Url);

        exitCode.Should().Be(0, stderr);
        model.Requests.Should().Be(1);
        using var result = JsonDocument.Parse(stdout);
        var fields = result.RootElement.GetProperty("fields").EnumerateArray().ToArray();
        fields.Should().ContainSingle();
        fields[0].GetProperty("name").GetString().Should().Be("total");
        fields[0].GetProperty("value").GetString().Should().Be("1200");
        fields[0].GetProperty("span").GetProperty("start").GetInt32().Should().Be(Text.IndexOf("1,200 won", StringComparison.Ordinal));
        result.RootElement.GetProperty("rejected").EnumerateArray()
            .Select(r => (r.GetProperty("name").GetString(), r.GetProperty("reason").GetString()))
            .Should().BeEquivalentTo([("issued", "not-in-text"), ("ref", "not-supported"), ("issued", "missing")],
                "a required field without a grounded value is also named missing");
        stderr.Should().Contain("refused: issued (NotInText)").And.Contain("refused: ref (NotSupported)");
    }

    [Fact]
    public async Task Extract_WritesTheResultToTheOutputFile_AndNeverReplacesOne()
    {
        var (text, schema) = await WriteInputsAsync();
        var output = Path.Combine(_tempDir, "result.json");
        using var model = new FakeChatEndpoint("""{"fields":{"total":[{"value":"1200","quote":"1,200 won"}]}}""");

        var first = await RunAsync("extract", "--text", text, "--schema", schema, "--openai-endpoint", model.Url, "--output", output);
        var second = await RunAsync("extract", "--text", text, "--schema", schema, "--openai-endpoint", model.Url, "--output", output);

        first.ExitCode.Should().Be(0, first.Error);
        first.Error.Should().Contain("Wrote").And.Contain("1 grounded");
        first.Output.Should().BeEmpty();
        JsonDocument.Parse(await File.ReadAllTextAsync(output)).RootElement.GetProperty("fields").GetArrayLength().Should().Be(1);
        second.ExitCode.Should().Be(1);
        second.Error.Should().Contain("already exists");
        model.Requests.Should().Be(1, "the second run stops before asking the model");
    }

    [Fact]
    public async Task Verify_ChecksValuesSomeoneAlreadyProposed_WithoutAModel()
    {
        var (text, schema) = await WriteInputsAsync();
        var candidates = Path.Combine(_tempDir, "candidates.json");
        await File.WriteAllTextAsync(candidates,
            """{"fields":{"total":[{"value":"1200","quote":"1,200 won"}],"issued":[{"value":"2026-11-02","quote":"Invoice 2026-11-02"}]}}""");

        var (exitCode, stdout, stderr) = await RunAsync("verify", "--text", text, "--schema", schema, "--candidates", candidates);

        exitCode.Should().Be(0, stderr);
        using var result = JsonDocument.Parse(stdout);
        result.RootElement.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString())
            .Should().Equal("total", "issued");
        result.RootElement.GetProperty("rejected").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Verify_AFuzzyQuote_IsAcceptedOnlyWhenAsked()
    {
        var (text, schema) = await WriteInputsAsync();
        var candidates = Path.Combine(_tempDir, "candidates.json");
        await File.WriteAllTextAsync(candidates,
            """{"fields":{"total":[{"value":"1200","quote":"total due is 1,200 won"}],"issued":[{"value":"2026-11-02","quote":"Invoice 2026-11-02"}]}}""");

        var strict = await RunAsync("verify", "--text", text, "--schema", schema, "--candidates", candidates);
        var lenient = await RunAsync("verify", "--text", text, "--schema", schema, "--candidates", candidates, "--fuzzy", "--threshold", "0.6");

        JsonDocument.Parse(strict.Output).RootElement.GetProperty("fields").GetArrayLength().Should().Be(1);
        var fields = JsonDocument.Parse(lenient.Output).RootElement.GetProperty("fields").EnumerateArray().ToArray();
        fields.Should().HaveCount(2);
        fields.Single(f => f.GetProperty("name").GetString() == "total").GetProperty("match").GetString().Should().Be("fuzzy");
    }

    [Fact]
    public async Task Extract_WithoutAKeyOrAnEndpoint_ExitsOneAndSaysSo()
    {
        var (text, schema) = await WriteInputsAsync();

        var (exitCode, _, stderr) = await RunAsync("extract", "--text", text, "--schema", schema);

        exitCode.Should().Be(1);
        stderr.Should().Contain("--openai-key");
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""[{"name":"a","type":"integer"}]""", "must be string, number")]
    [InlineData("""[{"name":"a","type":"enum"}]""", "values")]
    public async Task ASchemaThatCannotBeUsed_ExitsOneBeforeTheModelIsAsked(string schemaJson, string reason)
    {
        var (text, _) = await WriteInputsAsync();
        var schema = Path.Combine(_tempDir, "bad.json");
        await File.WriteAllTextAsync(schema, schemaJson);
        using var model = new FakeChatEndpoint("{}");

        var (exitCode, _, stderr) = await RunAsync("extract", "--text", text, "--schema", schema, "--openai-endpoint", model.Url);

        exitCode.Should().Be(1);
        stderr.Should().Contain("extract failed").And.Contain(reason);
        model.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Verify_CandidatesThatAreNotAnObject_ExitOneWithTheReason()
    {
        var (text, schema) = await WriteInputsAsync();
        var candidates = Path.Combine(_tempDir, "candidates.json");
        await File.WriteAllTextAsync(candidates, "no values");

        var (exitCode, _, stderr) = await RunAsync("verify", "--text", text, "--schema", schema, "--candidates", candidates);

        exitCode.Should().Be(1);
        stderr.Should().Contain("verify failed").And.Contain("fields");
    }

    [Fact]
    public async Task AThresholdOutsideZeroToOne_ExitsOne()
    {
        var (text, schema) = await WriteInputsAsync();

        var (exitCode, _, stderr) = await RunAsync(
            "verify", "--text", text, "--schema", schema, "--candidates", "c.json", "--fuzzy", "--threshold", "1.5");

        exitCode.Should().Be(1);
        stderr.Should().Contain("--threshold");
    }

    /// <summary>Answers <c>POST …/chat/completions</c> with one assistant message.</summary>
    private sealed class FakeChatEndpoint : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();
        private readonly string _content;
        private int _requests;

        public FakeChatEndpoint(string content)
        {
            _content = content;
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/v1/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public int Requests => Volatile.Read(ref _requests);

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                await context.Request.InputStream.CopyToAsync(Stream.Null);
                Interlocked.Increment(ref _requests);
                var json = JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-test",
                    @object = "chat.completion",
                    created = 0,
                    model = "test",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = _content }, finish_reason = "stop" } },
                });
                var body = System.Text.Encoding.UTF8.GetBytes(json);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }
}
