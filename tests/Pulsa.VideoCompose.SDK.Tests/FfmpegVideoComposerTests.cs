using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class FfmpegVideoComposerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pulsa-videocompose-test-").FullName;
    private readonly FfmpegVideoComposer _composer;

    public FfmpegVideoComposerTests()
    {
        // A binary folder that doesn't need to contain a real ffmpeg for these validation-only
        // tests — none of them reach the point of actually invoking it.
        _composer = new FfmpegVideoComposer(_tempDir);
    }

    [Fact]
    public async Task ComposeAsync_FewerThanTwoImages_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["only.png"], Captions: ["one"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("At least 2 images");
    }

    [Fact]
    public async Task ComposeAsync_CaptionCountMismatch_ReturnsFailure()
    {
        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["only one"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("captions must have exactly one entry per image");
    }

    [Fact]
    public async Task ComposeAsync_UnsupportedAspectRatio_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4"), AspectRatio: "4:3"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("4:3");
    }

    [Fact]
    public async Task ComposeAsync_OutputPathAlreadyExists_RefusesToOverwrite()
    {
        var outputPath = Path.Combine(_tempDir, "existing.mp4");
        await File.WriteAllTextAsync(outputPath, "not a real video");

        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: outputPath));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("already exists");
        (await File.ReadAllTextAsync(outputPath)).Should().Be("not a real video"); // never touched
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
