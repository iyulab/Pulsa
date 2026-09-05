using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class CaptionStyleFilterBuilderTests
{
    [Fact]
    public void Build_UsesAnOpaqueBackgroundBandNotOutlineOnly()
    {
        var filter = CaptionStyleFilterBuilder.Build("C:/tmp/out.srt");

        filter.Should().Contain("force_style=");
        filter.Should().Contain("BorderStyle=3"); // opaque box, not the libass outline-only default
        filter.Should().Contain("BackColour=");
    }

    [Fact]
    public void Build_EscapesWindowsDriveLetterColonAndBackslashes()
    {
        var filter = CaptionStyleFilterBuilder.Build(@"C:\videos\out.srt");

        filter.Should().Contain(@"C\:\\videos\\out.srt");
    }

    [Fact]
    public void BuildTitleStyle_IsCenteredAndLargerThanTheDefaultBand()
    {
        var filter = CaptionStyleFilterBuilder.BuildTitleStyle("C:/tmp/out.srt", videoHeight: 1080);

        // Alignment=10, not numpad 5 — confirmed empirically, see BuildTitleStyle's doc comment:
        // ffmpeg's `subtitles` filter converts SRT to legacy SSA v4, whose Alignment field uses
        // SSA's own numbering (10 = middle-center), not ASS v4+'s numpad scheme.
        filter.Should().Contain("Alignment=10");
        filter.Should().Contain("FontSize=72"); // 1080 / 15
    }

    [Fact]
    public void BuildTitleStyle_ScalesFontSizeWithVideoHeight()
    {
        var landscape = CaptionStyleFilterBuilder.BuildTitleStyle("C:/tmp/out.srt", videoHeight: 1080);
        var portrait = CaptionStyleFilterBuilder.BuildTitleStyle("C:/tmp/out.srt", videoHeight: 1920);

        landscape.Should().Contain("FontSize=72");
        portrait.Should().Contain("FontSize=128");
    }

    [Fact]
    public void BuildTitleStyle_ClampsAMinimumFontSizeForTinyHeights()
    {
        var filter = CaptionStyleFilterBuilder.BuildTitleStyle("C:/tmp/out.srt", videoHeight: 200);

        filter.Should().Contain("FontSize=32");
    }
}
