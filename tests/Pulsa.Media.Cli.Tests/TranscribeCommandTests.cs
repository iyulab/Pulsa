using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using FFMpegCore;
using Xunit;

namespace Pulsa.Media.Cli.Tests;

/// <summary>
/// The transcribe command run as a process, end to end, against a stand-in for an OpenAI-compatible transcription
/// endpoint (<c>--openai-endpoint</c>) that answers every segment. Needs a real ffmpeg on PATH.
/// </summary>
public sealed class TranscribeCommandTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pulsa-transcribe-cli-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

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

    /// <summary>A tone from 1 s to 2 s and from 3 s to 4 s, 5 s in all, as an m4a.</summary>
    private async Task<string> TwoTonesAsync()
    {
        var path = Path.Combine(_tempDir, "speech.m4a");
        await FFMpegArguments
            .FromFileInput("aevalsrc='if(between(t,1,2)+between(t,3,4),0.3*sin(2*PI*440*t),0)':s=44100:d=5",
                verifyExists: false, opt => opt.ForceFormat("lavfi"))
            .OutputToFile(path, overwrite: true)
            .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = string.Empty });
        return path;
    }

    [Fact]
    public async Task Transcribe_WritesWebVttWithACuePerSpeechSegment()
    {
        var input = await TwoTonesAsync();
        var output = Path.Combine(_tempDir, "speech.vtt");
        using var endpoint = new FakeTranscriptionEndpoint();

        var (exitCode, _, stderr) = await RunAsync(
            "transcribe", "--input", input, "--output", output, "--openai-key", "test-key", "--openai-endpoint", endpoint.Url);

        exitCode.Should().Be(0, stderr);
        var vtt = await File.ReadAllTextAsync(output);
        vtt.Should().StartWith("WEBVTT").And.Contain("segment 1").And.Contain("segment 2");
        endpoint.Requests.Should().Be(2);
    }

    [Fact]
    public async Task Transcribe_WithoutAKey_ExitsOneWithUsage()
    {
        var (exitCode, _, stderr) = await RunAsync("transcribe", "--input", "speech.m4a");

        exitCode.Should().Be(1);
        stderr.Should().Contain("--openai-key");
    }

    [Fact]
    public async Task Transcribe_AGlossaryWithoutARefineModel_ExitsOne()
    {
        var (exitCode, _, stderr) = await RunAsync(
            "transcribe", "--input", "speech.m4a", "--openai-key", "k", "--glossary", "terms.md");

        exitCode.Should().Be(1);
        stderr.Should().Contain("--refine-model");
    }

    [Fact]
    public async Task Transcribe_AFileThatIsNotAudio_ExitsOneWithTheReason()
    {
        var input = Path.Combine(_tempDir, "notes.txt");
        await File.WriteAllTextAsync(input, "not audio", TestContext.Current.CancellationToken);
        using var endpoint = new FakeTranscriptionEndpoint();

        var (exitCode, _, stderr) = await RunAsync(
            "transcribe", "--input", input, "--openai-key", "k", "--openai-endpoint", endpoint.Url);

        exitCode.Should().Be(1);
        stderr.Should().Contain("transcribe failed");
        endpoint.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Transcribe_WithAVocabulary_SendsItsTermsToTheRecognizer()
    {
        var input = await TwoTonesAsync();
        var vocabulary = Path.Combine(_tempDir, "terms.md");
        await File.WriteAllTextAsync(vocabulary, "- Pulsa\n- PulsaMedia\n", TestContext.Current.CancellationToken);
        using var endpoint = new FakeTranscriptionEndpoint();

        var (exitCode, _, stderr) = await RunAsync(
            "transcribe", "--input", input, "--openai-key", "k", "--openai-endpoint", endpoint.Url, "--vocabulary", vocabulary);

        exitCode.Should().Be(0, stderr);
        endpoint.Bodies.Should().NotBeEmpty().And.OnlyContain(body => body.Contains("name=prompt") && body.Contains("Pulsa, PulsaMedia"));
    }

    [Fact]
    public async Task Score_PrintsWordAndCharacterErrorRates()
    {
        var reference = Path.Combine(_tempDir, "ref.vtt");
        var hypothesis = Path.Combine(_tempDir, "hyp.txt");
        await File.WriteAllTextAsync(reference, "WEBVTT\n\n00:00.000 --> 00:01.000\nthe cat sat\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(hypothesis, "the cat sit", TestContext.Current.CancellationToken);

        var (exitCode, stdout, stderr) = await RunAsync("score", "--reference", reference, "--hypothesis", hypothesis);

        exitCode.Should().Be(0, stderr);
        stdout.Should().Contain("WER 0.3333").And.Contain("CER ");
    }

    /// <summary>Answers <c>POST …/audio/transcriptions</c> with "segment N".</summary>
    private sealed class FakeTranscriptionEndpoint : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;
        public System.Collections.Concurrent.ConcurrentQueue<string> Bodies { get; } = new();

        public FakeTranscriptionEndpoint()
        {
            var port = FreePort();
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
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                using var drain = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(drain);
                Bodies.Enqueue(Encoding.Latin1.GetString(drain.ToArray()));
                var n = Interlocked.Increment(ref _requests);
                var body = Encoding.UTF8.GetBytes($$"""{"text":"segment {{n}}"}""");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose() => _listener.Close();
    }
}
