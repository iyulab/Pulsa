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

    [Fact]
    public async Task RedactAsync_VideoWithOnlyStartTimeGiven_ReturnsFailureInsteadOfThrowing()
    {
        // Regression test for the ArgumentException-escapes-the-result-contract bug: a video
        // request supplying only StartTime (not EndTime) trips PixelateFilterBuilder.Build's own
        // "must both be supplied together, or neither" guard. That call used to happen OUTSIDE
        // MediaRedactor's try block, so the ArgumentException propagated out as a faulted Task
        // instead of the RedactResult(false, ...) every other failure path returns. This never
        // reaches ffmpeg (the garbage binary-folder fixture from the constructor would otherwise
        // make that hang/fail differently) -- the exception is thrown during Build, before
        // FFMpegArguments is even constructed.
        var inputPath = Path.Combine(_tempDir, "in.mp4");
        await File.WriteAllBytesAsync(inputPath, [0]);

        var act = () => _redactor.RedactAsync(new RedactRequest(
            InputPath: inputPath, OutputPath: Path.Combine(_tempDir, "out.mp4"),
            X: 0, Y: 0, Width: 10, Height: 10, StartTime: 2.0));

        var result = await act.Should().NotThrowAsync();
        result.Which.Success.Should().BeFalse();
    }

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

    private static async Task<string> CreateTestsrcPngAsync(string path, int width, int height)
    {
        // Unlike CreateTestPngAsync's flat color, `rgbtestsrc` produces a continuous R/G/B
        // gradient across the ENTIRE frame (no flat solid-colored blocks anywhere, unlike
        // ffmpeg's `testsrc`/`smptebars` patterns, which have large flat regions a small crop can
        // easily land entirely inside of). That continuous variation is what's actually needed
        // here: pixelating a region that happens to already be a flat color is a true no-op even
        // WITH the fix (downsample-then-upsample of a constant region reproduces the same
        // constant), so a region with real internal gradient is required to prove "the region's
        // pixels changed" and to give yuv420 chroma subsampling something to visibly perturb.
        await FFMpegArguments
            .FromFileInput($"rgbtestsrc=size={width}x{height}:rate=1", verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
            .OutputToFile(path, overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
            .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        return path;
    }

    /// <summary>
    /// Crops the given rectangle out of <paramref name="imagePath"/> and decodes it to raw rgb24
    /// bytes (no container/compression involved) so two crops can be compared byte-for-byte in C#.
    /// Both source and output in these tests are lossless PNGs, so this is an exact, deterministic
    /// pixel comparison -- not a perceptual metric -- proving real pixel-level differences (or the
    /// lack thereof) rather than inferring them from file size or dimensions.
    /// </summary>
    private async Task<byte[]> CropToRawRgbAsync(string imagePath, int x, int y, int width, int height)
    {
        var rawPath = Path.Combine(_tempDir, $"crop-{Path.GetFileNameWithoutExtension(imagePath)}-{x}-{y}-{width}x{height}-{Guid.NewGuid():N}.raw");
        await FFMpegArguments
            .FromFileInput(imagePath, verifyExists: true)
            .OutputToFile(rawPath, overwrite: true, opt => opt
                .WithCustomArgument($"-vf \"crop={width}:{height}:{x}:{y}\"")
                .WithCustomArgument("-pix_fmt rgb24")
                .WithCustomArgument("-frames:v 1")
                .WithCustomArgument("-f rawvideo"))
            .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        return await File.ReadAllBytesAsync(rawPath);
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
        // Uses ffmpeg's `testsrc` source (structured color-bar/gradient pattern), not a flat
        // color: a flat region can prove neither half of this test's name. Pixelating a flat
        // color as a silent no-op looks identical to pixelating it correctly (can't prove
        // "region changed"), and the whole-frame yuv420-subsampling degradation Fix 4 addresses
        // doesn't visibly perturb a flat color either, since a constant region's chroma resamples
        // to the same value regardless (can't prove "rest unchanged"). Both halves are verified
        // via byte-exact raw rgb24 crop comparison (see CropToRawRgbAsync) — both source and
        // output are lossless PNGs, so an exact byte comparison is the correct bar, not a
        // perceptual/lossy metric.
        var redactor = new MediaRedactor(ResolveFfmpegBinaryFolder());
        var inputPath = await CreateTestsrcPngAsync(Path.Combine(_tempDir, "testsrc-200x100.png"), 200, 100);
        var outputPath = Path.Combine(_tempDir, "testsrc-200x100-redacted.png");
        const int x = 10, y = 10, width = 50, height = 30;

        var result = await redactor.RedactAsync(new RedactRequest(
            InputPath: inputPath, OutputPath: outputPath, X: x, Y: y, Width: width, Height: height));

        result.Success.Should().BeTrue(because: result.Error);
        File.Exists(outputPath).Should().BeTrue();
        new FileInfo(outputPath).Length.Should().BeGreaterThan(0);
        var probe = await FFProbe.AnalyseAsync(outputPath, ffOptions: new FFOptions { BinaryFolder = ResolveFfmpegBinaryFolder() });
        probe.PrimaryVideoStream!.Width.Should().Be(200);
        probe.PrimaryVideoStream.Height.Should().Be(100);

        // The redacted region must actually be pixelated (not a silent no-op).
        var regionBefore = await CropToRawRgbAsync(inputPath, x, y, width, height);
        var regionAfter = await CropToRawRgbAsync(outputPath, x, y, width, height);
        regionAfter.Should().NotEqual(regionBefore, "the redacted region must actually be pixelated");

        // A patch OUTSIDE the redacted region must be byte-identical to the source — proving
        // overlay's format=auto avoids forcing the whole RGB frame through yuv420 chroma
        // subsampling (Fix 4's regression: without format=auto, overlay's default internal
        // format is yuv420, which degrades pixels far outside the redacted rectangle too via the
        // RGB<->YUV round trip it forces on the entire frame).
        const int outsideX = 120, outsideY = 60, outsideWidth = 40, outsideHeight = 20;
        var outsideBefore = await CropToRawRgbAsync(inputPath, outsideX, outsideY, outsideWidth, outsideHeight);
        var outsideAfter = await CropToRawRgbAsync(outputPath, outsideX, outsideY, outsideWidth, outsideHeight);
        outsideAfter.Should().Equal(outsideBefore, "pixels outside the redacted region must be untouched");
    }

    [Fact]
    public async Task RedactAsync_LiveImage_SubBlockSizeRegion_ActuallyPixelatesNotSilentNoOp()
    {
        // Regression test for the sub-blockSize no-op bug (Fix 1): with the old expression-based
        // downscale ("scale=width/blockSize:height/blockSize", evaluated by ffmpeg as a double
        // and truncated to int), a region smaller than blockSize on both axes (here 10x8 against
        // the default blockSize=16) truncates that expression to 0, and ffmpeg's `scale` filter
        // treats a 0 dimension as "keep the input dimension" — so the "pixelated" block was
        // byte-for-byte identical to the original crop, and the region silently passed through
        // completely unredacted while RedactResult.Success stayed true. This test proves the
        // fixed integer floor-of-1 downscale actually changes the region's pixels. It would FAIL
        // (regionAfter would equal regionBefore) against the pre-fix expression-based code and
        // PASS against the fixed integer-based code — that is its entire purpose.
        var redactor = new MediaRedactor(ResolveFfmpegBinaryFolder());
        var inputPath = await CreateTestsrcPngAsync(Path.Combine(_tempDir, "testsrc-100x60.png"), 100, 60);
        var outputPath = Path.Combine(_tempDir, "testsrc-100x60-redacted.png");
        const int x = 40, y = 20, width = 10, height = 8; // both smaller than default blockSize=16

        var result = await redactor.RedactAsync(new RedactRequest(
            InputPath: inputPath, OutputPath: outputPath, X: x, Y: y, Width: width, Height: height));

        result.Success.Should().BeTrue(because: result.Error);
        var regionBefore = await CropToRawRgbAsync(inputPath, x, y, width, height);
        var regionAfter = await CropToRawRgbAsync(outputPath, x, y, width, height);
        regionAfter.Should().NotEqual(regionBefore, "a sub-blockSize region must still be pixelated, not silently passed through unchanged");
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
