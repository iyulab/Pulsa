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
        filter.Should().Contain("scale=100/16:50/16");
        filter.Should().Contain("scale=100:50:flags=neighbor");
        filter.Should().Contain("overlay=10:20");
        filter.Should().NotContain("enable=");
    }

    [Fact]
    public void Build_WithTimeRange_GatesOverlayWithEnableBetween()
    {
        var filter = PixelateFilterBuilder.Build(x: 10, y: 20, width: 100, height: 50, startTime: 2.5, endTime: 8);

        filter.Should().Contain("enable='between(t,2.5,8)'");
        filter.Should().Contain("overlay=10:20:enable='between(t,2.5,8)'");
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

        filter.Should().Contain("scale=100/8:50/8");
    }
}
