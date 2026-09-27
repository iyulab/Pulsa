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

    [Fact]
    public void Parse_TakesTheVoiceSpanAsTheSpeaker_AndWriteRestoresIt()
    {
        // A diarized transcript names who is talking with the voice span. The words a refiner edits must not carry the
        // markup, and writing the cues back must put it where it was.
        const string diarized = "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\n<v Speaker 1>Shall we start?\n\n00:00:01.000 --> 00:00:02.000\n<v Speaker 2>Yes.</v>\n\n00:00:02.000 --> 00:00:03.000\nOkay.\n";

        var cues = WebVtt.Parse(diarized);

        cues.Select(c => (c.Speaker, c.Text)).Should().Equal(
            ("Speaker 1", "Shall we start?"), ("Speaker 2", "Yes."), ((string?)null, "Okay."));
        WebVtt.Write(cues).Should().Be(diarized.Replace("</v>", ""), "the optional closing tag is not needed at the end of a cue");
    }

    [Fact]
    public void Parse_LeavesABodyWithTwoVoicesAsIs()
    {
        const string twoVoices = "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\n<v A>Hi</v> <v B>Hello</v>\n";

        var cue = WebVtt.Parse(twoVoices).Should().ContainSingle().Subject;

        cue.Speaker.Should().BeNull();
        cue.Text.Should().Be("<v A>Hi</v> <v B>Hello</v>");
    }

    [Fact]
    public void Parse_DropsVoiceClasses_KeepsTheName()
    {
        var cue = WebVtt.Parse("WEBVTT\n\n00:00:00.000 --> 00:00:01.000\n<v.loud Esme>It's a blue apple tree!\n").Should().ContainSingle().Subject;

        cue.Speaker.Should().Be("Esme");
        cue.Text.Should().Be("It's a blue apple tree!");
    }
}
