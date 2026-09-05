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
}
