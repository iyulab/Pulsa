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
}
