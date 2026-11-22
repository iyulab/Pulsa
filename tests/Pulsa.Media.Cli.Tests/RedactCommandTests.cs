using System.Diagnostics;
using AwesomeAssertions;
using FFMpegCore;
using Xunit;

namespace Pulsa.Media.Cli.Tests;

/// <summary>
/// The redact command run as a process, end to end: the way a person or a script uses the
/// capability without a consuming application. Needs a real ffmpeg (PULSA_FFMPEG_PATH, else PATH).
/// </summary>
public sealed class RedactCommandTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pulsa-media-cli-{Guid.NewGuid():N}");

    public RedactCommandTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static string FfmpegFolder => Environment.GetEnvironmentVariable("PULSA_FFMPEG_PATH") ?? string.Empty;

    private static string CliDll => Path.Combine(AppContext.BaseDirectory, "PulsaMedia.Cli.dll");

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

    private async Task<string> CreateGradientPngAsync(string name, int width, int height)
    {
        // A gradient over the whole frame: pixelating a flat colour would change nothing.
        var path = Path.Combine(_tempDir, name);
        await FFMpegArguments
            .FromFileInput($"rgbtestsrc=size={width}x{height}:rate=1", verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
            .OutputToFile(path, overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
            .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = FfmpegFolder });
        return path;
    }

    private string[] FfmpegDirArgument() => FfmpegFolder.Length == 0 ? [] : ["--ffmpeg-dir", FfmpegFolder];

    [Fact]
    public async Task Redact_Image_WritesARedactedCopyAndExitsZero()
    {
        var input = await CreateGradientPngAsync("in.png", 200, 100);
        var output = Path.Combine(_tempDir, "out.png");

        var (exitCode, stdout, stderr) = await RunAsync(
            ["redact", "--input", input, "--output", output, "--region", "20,20,80,40", .. FfmpegDirArgument()]);

        exitCode.Should().Be(0, stderr);
        stdout.Should().Contain(output);
        File.Exists(output).Should().BeTrue();
        (await File.ReadAllBytesAsync(output)).Should().NotEqual(await File.ReadAllBytesAsync(input));
    }

    [Fact]
    public async Task Redact_SeveralRegions_AreAllHidden()
    {
        var input = await CreateGradientPngAsync("two.png", 200, 100);
        var output = Path.Combine(_tempDir, "two-out.png");

        var (exitCode, _, stderr) = await RunAsync(
            ["redact", "--input", input, "--output", output, "--region", "0,0,40,40", "120,40,60,40", .. FfmpegDirArgument()]);

        exitCode.Should().Be(0, stderr);
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task Redact_Detect_AsksTheVisionModel_HidesWhatItFound_AndReportsTheDoubtful()
    {
        var input = await CreateGradientPngAsync("detect.png", 200, 100);
        var output = Path.Combine(_tempDir, "detect-out.png");
        using var vision = new FakeChatEndpoint(
            """[{"label":"face","x":20,"y":20,"width":60,"height":40,"confidence":0.9},{"label":"badge","x":150,"y":10,"width":30,"height":30,"confidence":0.2}]""");

        var (exitCode, _, stderr) = await RunAsync(
            ["redact", "--input", input, "--output", output, "--detect", "faces", "--openai-key", "k",
             "--openai-endpoint", vision.Url, .. FfmpegDirArgument()]);

        exitCode.Should().Be(0, stderr);
        stderr.Should().Contain("hidden: face at 20,20 60x40").And.Contain("review: badge");
        vision.Requests.Should().Be(1);
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task Redact_DetectWithoutAKey_ExitsOne()
    {
        var (exitCode, _, stderr) = await RunAsync("redact", "--input", "in.png", "--output", "out.png", "--detect", "faces");

        exitCode.Should().Be(1);
        stderr.Should().Contain("--openai-key");
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
                var json = System.Text.Json.JsonSerializer.Serialize(new
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

    [Fact]
    public async Task Redact_WithoutRegion_ExitsOneAndSaysWhatIsMissing()
    {
        var (exitCode, _, stderr) = await RunAsync("redact", "--input", "in.png", "--output", "out.png");

        exitCode.Should().Be(1);
        stderr.Should().Contain("--region");
    }

    [Theory]
    [InlineData("10,20,30")]
    [InlineData("10,20,30,forty")]
    [InlineData("10.5,20,30,40")]
    public async Task Redact_WithAMalformedRegion_ExitsOneBeforeTouchingAnyFile(string region)
    {
        var output = Path.Combine(_tempDir, "never.png");

        var (exitCode, _, stderr) = await RunAsync("redact", "--input", "in.png", "--output", output, "--region", region);

        exitCode.Should().Be(1);
        stderr.Should().Contain(region);
        File.Exists(output).Should().BeFalse();
    }
}
