using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class ZoompanFilterBuilderTests
{
    [Fact]
    public void Build_DefaultOptions_ProducesScaleCropZoompanFormatChain()
    {
        var filter = ZoompanFilterBuilder.Build(sceneDurationSeconds: 4);

        filter.Should().Contain("scale=1920:1080:force_original_aspect_ratio=increase");
        filter.Should().Contain("crop=1920:1080");
        filter.Should().Contain("zoompan=");
        filter.Should().Contain("d=100"); // 4 seconds * 25 fps
        filter.Should().Contain("s=1920x1080:fps=25");
        filter.Should().Contain("format=yuv420p");
    }

    [Fact]
    public void Build_CustomOptions_UsesThem()
    {
        var options = new VideoComposeOptions(Width: 1080, Height: 1920, Fps: 30, ZoomIncrementPerFrame: 0.002, MaxZoom: 1.3);
        var filter = ZoompanFilterBuilder.Build(sceneDurationSeconds: 3, options);

        filter.Should().Contain("scale=1080:1920:force_original_aspect_ratio=increase");
        filter.Should().Contain("d=90"); // 3 seconds * 30 fps
        filter.Should().Contain("min(zoom+0.002,1.3)");
    }

    [Fact]
    public void Build_FractionalDuration_RoundsFrameCount()
    {
        var filter = ZoompanFilterBuilder.Build(sceneDurationSeconds: 2.5); // 2.5 * 25 = 62.5 -> 63

        filter.Should().Contain("d=63");
    }
}
