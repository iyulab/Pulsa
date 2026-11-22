using AwesomeAssertions;
using PulsaRedact;
using Xunit;

namespace Pulsa.Media.Tests.Redact;

public class PixelateFilterBuilderTests
{
    [Fact]
    public void Build_NoTimeRange_ProducesCropScaleScaleOverlayChain()
    {
        var filter = PixelateFilterBuilder.Build([new RedactRegion(10, 20, 100, 50)]);

        filter.Should().Contain("crop=100:50:10:20");
        filter.Should().Contain("scale=6:3"); // 100/16=6, 50/16=3 (integer division)
        filter.Should().Contain("scale=100:50:flags=neighbor");
        filter.Should().Contain("overlay=10:20:format=auto");
        filter.Should().NotContain("enable=");
    }

    [Fact]
    public void Build_WithTimeRange_GatesOverlayWithEnableBetween()
    {
        var filter = PixelateFilterBuilder.Build([new RedactRegion(10, 20, 100, 50)], startTime: 2.5, endTime: 8);

        filter.Should().Contain("enable='between(t,2.5,8)'");
        filter.Should().Contain("overlay=10:20:format=auto:enable='between(t,2.5,8)'");
    }

    [Fact]
    public void Build_OnlyStartTimeGiven_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build([new RedactRegion(0, 0, 10, 10)], startTime: 2.0);
        act.Should().Throw<ArgumentException>().WithMessage("*startTime and endTime*");
    }

    [Fact]
    public void Build_OnlyEndTimeGiven_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build([new RedactRegion(0, 0, 10, 10)], endTime: 5.0);
        act.Should().Throw<ArgumentException>().WithMessage("*startTime and endTime*");
    }

    [Fact]
    public void Build_CustomBlockSize_ScalesByThatFactor()
    {
        var filter = PixelateFilterBuilder.Build([new RedactRegion(0, 0, 100, 50)], blockSize: 8);

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
        var filter = PixelateFilterBuilder.Build([new RedactRegion(0, 0, 10, 10)]);

        filter.Should().Contain("scale=1:1");
    }

    [Fact]
    public void Build_NonPositiveWidthOrHeight_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build([new RedactRegion(0, 0, 0, 10)]);
        act.Should().Throw<ArgumentException>().WithMessage("*width and height*");
    }

    [Fact]
    public void Build_StartTimeAtOrAfterEndTime_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build([new RedactRegion(0, 0, 10, 10)], startTime: 5, endTime: 2);
        act.Should().Throw<ArgumentException>().WithMessage("*startTime must be less than endTime*");
    }

    [Fact]
    public void Build_SeveralRegions_OverlaysEachInTurnOnTheResultOfTheLast()
    {
        var filter = PixelateFilterBuilder.Build([new RedactRegion(0, 0, 32, 32), new RedactRegion(64, 0, 32, 32)]);

        filter.Should().Be(
            "[0:v]crop=32:32:0:0,scale=2:2,scale=32:32:flags=neighbor[blk0];" +
            "[0:v]crop=32:32:64:0,scale=2:2,scale=32:32:flags=neighbor[blk1];" +
            "[0:v][blk0]overlay=0:0:format=auto[out0];" +
            "[out0][blk1]overlay=64:0:format=auto");
    }

    [Fact]
    public void Build_NoRegions_ThrowsArgumentException()
    {
        var act = () => PixelateFilterBuilder.Build([]);
        act.Should().Throw<ArgumentException>().WithMessage("*at least one region*");
    }
}
