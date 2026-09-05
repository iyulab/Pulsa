using FluentAssertions;
using PulsaRedact;
using Xunit;

namespace Pulsa.Redact.SDK.Tests;

public class PixelateFilterBuilderTests
{
    [Fact]
    public void Build_NoTimeRange_ProducesCropScaleScaleOverlayChain()
    {
        var filter = PixelateFilterBuilder.Build(x: 10, y: 20, width: 100, height: 50);

        filter.Should().Contain("crop=100:50:10:20");
        filter.Should().Contain("scale=6:3"); // 100/16=6, 50/16=3 (integer division)
        filter.Should().Contain("scale=100:50:flags=neighbor");
        filter.Should().Contain("overlay=10:20:format=auto");
        filter.Should().NotContain("enable=");
    }

    [Fact]
    public void Build_WithTimeRange_GatesOverlayWithEnableBetween()
    {
        var filter = PixelateFilterBuilder.Build(x: 10, y: 20, width: 100, height: 50, startTime: 2.5, endTime: 8);

        filter.Should().Contain("enable='between(t,2.5,8)'");
        filter.Should().Contain("overlay=10:20:format=auto:enable='between(t,2.5,8)'");
    }

    [Fact]
    public void Build_OnlyStartTimeGiven_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build(x: 0, y: 0, width: 10, height: 10, startTime: 2.0);
        act.Should().Throw<ArgumentException>().WithMessage("*startTime and endTime*");
    }

    [Fact]
    public void Build_OnlyEndTimeGiven_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build(x: 0, y: 0, width: 10, height: 10, endTime: 5.0);
        act.Should().Throw<ArgumentException>().WithMessage("*startTime and endTime*");
    }

    [Fact]
    public void Build_CustomBlockSize_ScalesByThatFactor()
    {
        var filter = PixelateFilterBuilder.Build(x: 0, y: 0, width: 100, height: 50, blockSize: 8);

        filter.Should().Contain("scale=12:6"); // 100/8=12, 50/8=6
    }

    [Fact]
    public void Build_RegionSmallerThanBlockSize_StillProducesNonTrivialDownscale()
    {
        // Regression test for the sub-blockSize no-op bug: with the old expression-based
        // downscale ("scale=10/16:10/16"), ffmpeg evaluates the expression as a double, truncates
        // to int (0), and `scale` treats a 0 dimension as "keep the input dimension" -- so the
        // "downscale" was silently a no-op and the region came back unpixelated. The fixed code
        // floors to 1 instead, guaranteeing the downscale step always does SOMETHING.
        var filter = PixelateFilterBuilder.Build(x: 0, y: 0, width: 10, height: 10);

        filter.Should().Contain("scale=1:1");
    }

    [Fact]
    public void Build_NonPositiveWidthOrHeight_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build(x: 0, y: 0, width: 0, height: 10);
        act.Should().Throw<ArgumentException>().WithMessage("*width and height*");
    }

    [Fact]
    public void Build_StartTimeAtOrAfterEndTime_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build(x: 0, y: 0, width: 10, height: 10, startTime: 5, endTime: 2);
        act.Should().Throw<ArgumentException>().WithMessage("*startTime must be less than endTime*");
    }
}
