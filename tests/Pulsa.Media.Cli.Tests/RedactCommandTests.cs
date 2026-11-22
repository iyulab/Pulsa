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
