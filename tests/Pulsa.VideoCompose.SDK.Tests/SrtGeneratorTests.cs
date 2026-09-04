using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class SrtGeneratorTests
{
    [Fact]
    public void Generate_ThreeCaptions_FourSecondScenes_ProducesSequentialTimestamps()
    {
        var srt = SrtGenerator.Generate(["First", "Second", "Third"], sceneDurationSeconds: 4);

        srt.Should().Be(
            "1\r\n00:00:00,000 --> 00:00:04,000\r\nFirst\r\n\r\n" +
            "2\r\n00:00:04,000 --> 00:00:08,000\r\nSecond\r\n\r\n" +
            "3\r\n00:00:08,000 --> 00:00:12,000\r\nThird\r\n\r\n");
    }

    [Fact]
    public void Generate_FractionalDuration_RoundsMillisecondsCorrectly()
    {
        var srt = SrtGenerator.Generate(["Only"], sceneDurationSeconds: 2.5);

        srt.Should().Contain("00:00:00,000 --> 00:00:02,500");
    }

    [Fact]
    public void Generate_EmptyCaptions_Throws()
    {
        var act = () => SrtGenerator.Generate([], sceneDurationSeconds: 4);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Generate_NonPositiveDuration_Throws()
    {
        var act = () => SrtGenerator.Generate(["x"], sceneDurationSeconds: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Generate_HourLongDuration_FormatsHoursCorrectly()
    {
        var srt = SrtGenerator.Generate(["a", "b"], sceneDurationSeconds: 3661); // 1h 1m 1s per scene

        srt.Should().Contain("00:00:00,000 --> 01:01:01,000");
        srt.Should().Contain("01:01:01,000 --> 02:02:02,000");
    }
}
