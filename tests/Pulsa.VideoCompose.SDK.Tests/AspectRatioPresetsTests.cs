using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class AspectRatioPresetsTests
{
    [Fact]
    public void Resolve_16by9_ReturnsLandscapeDimensions()
    {
        var resolved = AspectRatioPresets.Resolve("16:9", new VideoComposeOptions());

        resolved.Width.Should().Be(1920);
        resolved.Height.Should().Be(1080);
    }

    [Fact]
    public void Resolve_9by16_ReturnsPortraitDimensions()
    {
        var resolved = AspectRatioPresets.Resolve("9:16", new VideoComposeOptions());

        resolved.Width.Should().Be(1080);
        resolved.Height.Should().Be(1920);
    }

    [Fact]
    public void Resolve_PreservesEveryOtherOptionFromBaseOptions()
    {
        var baseOptions = new VideoComposeOptions(Fps: 30, ZoomIncrementPerFrame: 0.002, MaxZoom: 1.3);

        var resolved = AspectRatioPresets.Resolve("9:16", baseOptions);

        resolved.Fps.Should().Be(30);
        resolved.ZoomIncrementPerFrame.Should().Be(0.002);
        resolved.MaxZoom.Should().Be(1.3);
    }

    [Fact]
    public void Resolve_UnsupportedValue_Throws()
    {
        var act = () => AspectRatioPresets.Resolve("4:3", new VideoComposeOptions());

        act.Should().Throw<ArgumentException>().WithMessage("*4:3*");
    }
}
