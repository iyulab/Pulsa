using AwesomeAssertions;
using Xunit;

namespace Pulsa.Media.Tests.Support;

public class FfmpegFiltersTests
{
    [Fact]
    public void ParseFilterList_ReadsTheNameOfEachFilterRow()
    {
        const string output = """
            Filters:
              T.. = Timeline support
              ... = Commands
              A = Audio input/output
              | = Source or sink filter
             .. ass               V->V       Render ASS subtitles onto input video using the libass library.
             TS pixelize          V->V       Pixelize video.
             .. zoompan           V->V       Apply Zoom & Pan effect.
            """;

        FfmpegFilters.ParseFilterList(output).Should().BeEquivalentTo(["ass", "pixelize", "zoompan"]);
    }

    [Fact]
    public void ParseFilterList_WithNoFilterRows_IsUnknown()
    {
        FfmpegFilters.ParseFilterList("ffmpeg: not what we expected").Should().BeNull();
    }

    [Fact]
    public void Missing_NamesTheFiltersTheBuildLacks()
    {
        FfmpegFilters.Missing(new HashSet<string> { "zoompan", "pixelize" }, ["subtitles", "zoompan"])
            .Should().Equal("subtitles");
    }

    [Fact]
    public void Missing_WhenTheBuildCouldNotBeRead_IsEmptySoTheTestRunsAndFailsOnTheRealError()
    {
        FfmpegFilters.Missing(null, ["subtitles"]).Should().BeEmpty();
    }
}
