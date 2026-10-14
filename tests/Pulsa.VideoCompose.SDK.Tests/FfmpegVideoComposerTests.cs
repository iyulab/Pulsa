using FFMpegCore;
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
    public async Task ComposeAsync_UnreadableCaptionStyle_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var composer = new FfmpegVideoComposer(_tempDir, new VideoComposeOptions(
            Captions: CaptionStyle.DefaultCaptions with { TextColor = "white" }));

        var result = await composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("white");
    }

    [Fact]
    public async Task ComposeAsync_TitleImagePathWithoutTitleText_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4"), TitleImagePath: "logo.png"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("titleImagePath and titleText must both be supplied together");
    }

    [Fact]
    public async Task ComposeAsync_TitleTextWithoutTitleImagePath_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4"), TitleText: "Our product"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("titleImagePath and titleText must both be supplied together");
    }

    [Fact]
    public async Task ComposeAsync_OutroImagePathWithoutOutroText_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4"), OutroImagePath: "logo.png"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outroImagePath and outroText must both be supplied together");
    }

    [Fact]
    public async Task ComposeAsync_OutroTextWithoutOutroImagePath_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var result = await _composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4"), OutroText: "Thanks for watching"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outroImagePath and outroText must both be supplied together");
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

    [Fact]
    public async Task ComposeAsync_FontsDirectoryThatDoesNotExist_ReturnsFailureWithoutTouchingFfmpeg()
    {
        var missing = Path.Combine(_tempDir, "no-such-fonts");
        var composer = new FfmpegVideoComposer(_tempDir, new VideoComposeOptions(FontsDirectory: missing));

        var result = await composer.ComposeAsync(new ComposeVideoRequest(
            ImagePaths: ["a.png", "b.png"], Captions: ["one", "two"], SceneDurationSeconds: 4,
            OutputPath: Path.Combine(_tempDir, "out.mp4")));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(missing);
    }

    // Live: needs a real ffmpeg on PATH (CI installs one).
    [Fact]
    public async Task ComposeAsync_AFontThatIsNotFound_IsReportedAndTheVideoIsStillWritten()
    {
        var images = await CreateImagesAsync();
        var composer = new FfmpegVideoComposer(string.Empty, new VideoComposeOptions(
            Captions: CaptionStyle.DefaultCaptions with { FontName = "No Such Font Family 41c7" }));

        var result = await composer.ComposeAsync(new ComposeVideoRequest(
            images, ["one", "two"], SceneDurationSeconds: 1, OutputPath: Path.Combine(_tempDir, "missing-font.mp4"),
            TitleImagePath: images[0], TitleText: "Title"));

        result.Success.Should().BeTrue(result.Error);
        File.Exists(result.OutputPath).Should().BeTrue();
        result.FontSubstitutions.Should().ContainSingle()
            .Which.Should().Match<FontSubstitution>(s =>
                s.RequestedFamily == "No Such Font Family 41c7" && !s.Bold && s.UsedFont.Length > 0);
    }

    // Live: needs a real ffmpeg on PATH. The check renders over a source ffmpeg does not have, so it fails — after the
    // video is written, which must stay a success with the check reported as not determined.
    [Fact]
    public async Task ComposeAsync_WhenTheFontCheckFails_TheWrittenVideoIsStillASuccess()
    {
        var images = await CreateImagesAsync();
        var composer = new FfmpegVideoComposer(
            string.Empty,
            new VideoComposeOptions(Captions: CaptionStyle.DefaultCaptions with { FontName = "No Such Font Family 41c7" }),
            fontProbeInput: "no_such_lavfi_source_41c7");

        var result = await composer.ComposeAsync(new ComposeVideoRequest(
            images, ["one", "two"], SceneDurationSeconds: 1, OutputPath: Path.Combine(_tempDir, "check-fails.mp4")));

        result.Success.Should().BeTrue(result.Error);
        File.Exists(result.OutputPath).Should().BeTrue();
        result.FontSubstitutions.Should().BeNull();
    }

    // Live: needs a real ffmpeg on PATH. The default font is found, so nothing is reported — including for the cards'
    // bold text, which takes the bold face of the same family.
    [Fact]
    public async Task ComposeAsync_FontsThatAreFound_ReportNoSubstitution()
    {
        var images = await CreateImagesAsync();

        var result = await new FfmpegVideoComposer(string.Empty).ComposeAsync(new ComposeVideoRequest(
            images, ["one", "two"], SceneDurationSeconds: 1, OutputPath: Path.Combine(_tempDir, "found-font.mp4"),
            TitleImagePath: images[0], TitleText: "Title"));

        result.Success.Should().BeTrue(result.Error);
        result.FontSubstitutions.Should().BeEmpty();
    }

    private async Task<string[]> CreateImagesAsync()
    {
        var paths = new[] { Path.Combine(_tempDir, "a.png"), Path.Combine(_tempDir, "b.png") };
        foreach (var path in paths)
            await FFMpegArguments
                .FromFileInput("color=c=navy:s=320x180:d=1", verifyExists: false, opt => opt.ForceFormat("lavfi"))
                .OutputToFile(path, overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
                .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = string.Empty });
        return paths;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
