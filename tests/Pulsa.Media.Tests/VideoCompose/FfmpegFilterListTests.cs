using AwesomeAssertions;
using FFMpegCore;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.Media.Tests.VideoCompose;

public class FfmpegFilterListTests
{
    [Fact]
    public void ABuildWithoutTheSubtitlesFilter_GetsASentenceNamingTheCause()
    {
        var problem = FfmpegFilterList.SubtitlesProblem(new HashSet<string> { "scale", "zoompan", "ass" });

        problem.Should().Contain("subtitles").And.Contain("libass");
    }

    [Fact]
    public void ABuildWithTheSubtitlesFilter_HasNoProblem()
    {
        FfmpegFilterList.SubtitlesProblem(new HashSet<string> { "scale", "subtitles" }).Should().BeNull();
    }

    [Fact]
    public void ABuildThatCouldNotBeRead_HasNoProblemSoTheRealErrorComesFromTheRun()
    {
        FfmpegFilterList.SubtitlesProblem(null).Should().BeNull();
    }

    [Fact]
    public async Task TheRealFfmpeg_ListsItsFilters()
    {
        var folder = Environment.GetEnvironmentVariable("PULSA_FFMPEG_PATH") ?? string.Empty;

        var filters = await FfmpegFilterList.ReadAsync(new FFOptions { BinaryFolder = folder }, TestContext.Current.CancellationToken);

        filters.Should().NotBeNull().And.Contain(["scale", "zoompan"]);
    }

    [Fact]
    public async Task AnFfmpegThatCannotBeRun_IsUnknownNotAFailure()
    {
        var empty = Directory.CreateTempSubdirectory("pulsa-nofilters-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(empty, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), [0, 1, 2, 3]);

            var filters = await FfmpegFilterList.ReadAsync(new FFOptions { BinaryFolder = empty }, TestContext.Current.CancellationToken);

            filters.Should().BeNull();
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
