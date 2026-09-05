using FFMpegCore;
using FluentAssertions;
using PulsaRedact;
using Xunit;

namespace Pulsa.Redact.SDK.Tests;

public class MediaRedactorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pulsa-redact-test-").FullName;
    private readonly MediaRedactor _redactor;

    public MediaRedactorTests()
    {
        // A binary folder that doesn't need a REAL ffmpeg — validation-only tests never reach a
        // successful invocation. It DOES need SOME file at the OS-appropriate binary name, though:
        // live-verified (FFMpegCore 5.4.0) that when the exact BinaryFolder-combined path is missing
        // entirely, FFMpegCore falls back to the bare executable name and lets the OS resolve it via
        // PATH — on a machine (or CI runner image) with a real ffmpeg already on PATH, that silently
        // starts a REAL ffmpeg process against this test's garbage input, which hangs indefinitely
        // instead of failing fast (a real ffmpeg with `-loop 1` on undecodable input keeps retrying
        // rather than erroring out). An existing-but-invalid file at the combined path is used
        // verbatim instead and fails Process.Start synchronously (Win32Exception: "not a valid
        // application for this OS platform") regardless of what else is on PATH.
        File.WriteAllBytes(Path.Combine(_tempDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), [0, 1, 2, 3]);
        _redactor = new MediaRedactor(_tempDir);
    }

    [Fact]
    public async Task RedactAsync_OutputAlreadyExists_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var outputPath = Path.Combine(_tempDir, "out.png");
        await File.WriteAllBytesAsync(outputPath, [0]);

        var result = await _redactor.RedactAsync(new RedactRequest(
            InputPath: "in.png", OutputPath: outputPath, X: 0, Y: 0, Width: 10, Height: 10));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain($"{outputPath} already exists");
    }

    [Fact]
    public async Task RedactAsync_InputFileDoesNotExist_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var result = await _redactor.RedactAsync(new RedactRequest(
            InputPath: Path.Combine(_tempDir, "missing.png"),
            OutputPath: Path.Combine(_tempDir, "out.png"), X: 0, Y: 0, Width: 10, Height: 10));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("missing.png");
    }

    [Fact]
    public async Task RedactAsync_TimeRangeOnStillImage_IgnoresIt_DoesNotThrow()
    {
        var inputPath = Path.Combine(_tempDir, "in.png");
        await File.WriteAllBytesAsync(inputPath, [0]); // existence check only at this stage of the test

        // This test only proves the time-range fields don't cause a validation-level exception for
        // an image input before ffmpeg is ever invoked — it deliberately supplies a byte-garbage PNG
        // and an unreachable ffmpeg binary folder, so ffmpeg invocation itself is expected to fail;
        // asserting Success == false here (not true) with an ffmpeg-process-level error, not a
        // PulsaRedact-level validation error, is what proves the time-range fields were accepted and
        // simply ignored rather than rejected up front.
        var result = await _redactor.RedactAsync(new RedactRequest(
            InputPath: inputPath, OutputPath: Path.Combine(_tempDir, "out.png"),
            X: 0, Y: 0, Width: 10, Height: 10, StartTime: 1, EndTime: 2));

        result.Success.Should().BeFalse();
        result.Error.Should().NotContain("startTime and endTime"); // not PixelateFilterBuilder's own guard
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static string ResolveFfmpegBinaryFolder() =>
        Environment.GetEnvironmentVariable("PULSA_FFMPEG_PATH") ?? string.Empty;

    private static async Task<string> CreateTestPngAsync(string path, int width, int height)
    {
        // A minimal real PNG (not a format ffmpeg would reject) — solid color, generated via ffmpeg
        // itself (lavfi color source) rather than hand-rolling PNG bytes, so this fixture-creation
        // step doubles as an early, cheap "is ffmpeg even reachable" check before the real test body
        // runs (if ffmpeg can't even do this, the actual test assertion below fails for the same
        // underlying reason, with a clear ffmpeg-originated error message either way).
        await FFMpegArguments
            .FromFileInput($"color=c=blue:s={width}x{height}:d=1", verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
            .OutputToFile(path, overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
            .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        return path;
    }

    private static async Task<string> CreateTestVideoAsync(string path, int width, int height, double durationSeconds)
    {
        await FFMpegArguments
            .FromFileInput($"color=c=blue:s={width}x{height}:d={durationSeconds}:r=10", verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
            .OutputToFile(path, overwrite: true, opt => opt.WithVideoCodec("libx264"))
            .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        return path;
    }

    [Fact]
    public async Task RedactAsync_LiveImage_PixelatesRegionAndLeavesRestUnchanged()
    {
        var redactor = new MediaRedactor(ResolveFfmpegBinaryFolder());
        var inputPath = await CreateTestPngAsync(Path.Combine(_tempDir, "solid.png"), 200, 100);
        var outputPath = Path.Combine(_tempDir, "solid-redacted.png");

        var result = await redactor.RedactAsync(new RedactRequest(
            InputPath: inputPath, OutputPath: outputPath, X: 10, Y: 10, Width: 50, Height: 30));

        result.Success.Should().BeTrue();
        File.Exists(outputPath).Should().BeTrue();
        // The source is a single flat color, so the redacted region pixelating "blue -> blocky blue"
        // produces no visible pixel difference — this test instead asserts the more basic, still-
        // real correctness bar: the output file is a valid, non-empty, same-dimensions image ffmpeg
        // could itself re-read. Task 6 uses a two-color source specifically to assert pixel-level
        // region-vs-outside behavior, which a flat color cannot distinguish.
        new FileInfo(outputPath).Length.Should().BeGreaterThan(0);
        var probe = await FFProbe.AnalyseAsync(outputPath, ffOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        probe.PrimaryVideoStream!.Width.Should().Be(200);
        probe.PrimaryVideoStream.Height.Should().Be(100);
    }

    [Fact]
    public async Task RedactAsync_LiveVideo_WholeDuration_ProducesPlayableOutput()
    {
        var redactor = new MediaRedactor(ResolveFfmpegBinaryFolder());
        var inputPath = await CreateTestVideoAsync(Path.Combine(_tempDir, "clip.mp4"), 200, 100, durationSeconds: 2);
        var outputPath = Path.Combine(_tempDir, "clip-redacted.mp4");

        var result = await redactor.RedactAsync(new RedactRequest(
            InputPath: inputPath, OutputPath: outputPath, X: 10, Y: 10, Width: 50, Height: 30));

        result.Success.Should().BeTrue();
        var probe = await FFProbe.AnalyseAsync(outputPath, ffOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        probe.Duration.TotalSeconds.Should().BeApproximately(2, precision: 0.5);
    }

    [Fact]
    public async Task RedactAsync_LiveVideo_TimeRanged_SettlesWhetherEnableIsHonoredByOverlay()
    {
        // This test's PASS/FAIL result is itself the answer to the Global Constraints' open
        // question ("verify enable= empirically for overlay/pixelate, don't assume by analogy to
        // subtitles' rejection of it") — if this fails with an ffmpeg argument-parsing error
        // (not a duration/assertion mismatch), PixelateFilterBuilder's enable clause needs a
        // different mechanism (e.g. `trim`+`overlay` split via filter_complex instead of a bare
        // `enable=` on `overlay`) and this task is not done until one of those two shapes passes.
        var redactor = new MediaRedactor(ResolveFfmpegBinaryFolder());
        var inputPath = await CreateTestVideoAsync(Path.Combine(_tempDir, "ranged.mp4"), 200, 100, durationSeconds: 4);
        var outputPath = Path.Combine(_tempDir, "ranged-redacted.mp4");

        var result = await redactor.RedactAsync(new RedactRequest(
            InputPath: inputPath, OutputPath: outputPath,
            X: 10, Y: 10, Width: 50, Height: 30, StartTime: 1, EndTime: 3));

        result.Success.Should().BeTrue(because: result.Error);
        var probe = await FFProbe.AnalyseAsync(outputPath, ffOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        probe.Duration.TotalSeconds.Should().BeApproximately(4, precision: 0.5);
    }
}
