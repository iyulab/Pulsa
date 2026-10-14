using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class SubtitlesFilterTests
{
    [Fact]
    public void Build_NamesTheScriptAndLeavesStylingToIt()
    {
        var filter = SubtitlesFilter.Build("C:/tmp/out.ass");

        filter.Should().StartWith("subtitles=");
        filter.Should().NotContain("force_style", "the script carries its own style in the frame's own coordinates");
    }

    [Fact]
    public void Build_EscapesWindowsDriveLetterColonAndBackslashes()
    {
        var filter = SubtitlesFilter.Build(@"C:\videos\out.ass");

        filter.Should().Contain(@"C\:\\videos\\out.ass");
    }

    [Fact]
    public void Build_WithAFontsDirectory_NamesItEscapedLikeTheScriptPath()
    {
        var filter = SubtitlesFilter.Build(@"C:\videos\out.ass", @"D:\project\fonts");

        filter.Should().Be(@"subtitles='C\:\\videos\\out.ass':fontsdir='D\:\\project\\fonts'");
    }
}
