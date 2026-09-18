using FluentAssertions;
using PulsaTranscript;
using Xunit;

namespace Pulsa.Transcript.SDK.Tests;

public class WebVttTests
{
    private const string Sample = "WEBVTT\n\n00:00:00.000 --> 00:00:12.799\n지금부터 회의를 시작하겠습니다\n\n00:01:45.099 --> 00:01:49.000\n박준호입니다.\n저도 찬성합니다.\n";

    [Fact]
    public void Parse_ReadsEachCueWithItsTimingAndText()
    {
        var cues = WebVtt.Parse(Sample);

        cues.Should().HaveCount(2);
        cues[0].Start.Should().Be(TimeSpan.Zero);
        cues[0].End.Should().Be(TimeSpan.FromMilliseconds(12_799));
        cues[1].Start.Should().Be(new TimeSpan(0, 0, 1, 45, 99));
        cues[1].Text.Should().Be("박준호입니다.\n저도 찬성합니다.");
    }

    [Fact]
    public void Write_RoundTripsTheTimingLinesExactly()
    {
        var cues = WebVtt.Parse(Sample);

        WebVtt.Write(cues).Should().Be(Sample);
    }

    [Fact]
    public void Parse_AcceptsCrlfCueIdentifiersAndHoursLessTimings()
    {
        var text = "WEBVTT\r\n\r\n1\r\n00:05.000 --> 00:07.250\r\nhello\r\n";

        var cues = WebVtt.Parse(text);

        cues.Should().ContainSingle();
        cues[0].Start.Should().Be(TimeSpan.FromSeconds(5));
        cues[0].End.Should().Be(TimeSpan.FromMilliseconds(7_250));
        cues[0].Text.Should().Be("hello");
    }

    [Fact]
    public void Parse_RejectsTextThatIsNotWebVtt()
    {
        var act = () => WebVtt.Parse("1\n00:00:01,000 --> 00:00:02,000\nsrt, not vtt\n");

        act.Should().Throw<FormatException>();
    }
}
