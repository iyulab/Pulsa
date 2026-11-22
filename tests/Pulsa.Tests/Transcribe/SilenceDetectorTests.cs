using AwesomeAssertions;
using PulsaTranscribe;
using Xunit;

namespace Pulsa.Tests.Transcribe;

public class SilenceDetectorTests
{
    private static (double, double)[] Rounded(IReadOnlyList<TimeRange> spans) =>
        spans.Select(s => (Math.Round(s.Start.TotalSeconds, 2), Math.Round(s.End.TotalSeconds, 2))).ToArray();

    [Fact]
    public void APauseAtLeastTheMinimum_SplitsSpeech()
    {
        var audio = Audio.Of(Audio.Tone(1), Audio.Silence(1), Audio.Tone(1));

        Rounded(SilenceDetector.Detect(audio, TimeSpan.FromSeconds(0.5))).Should().Equal((0.0, 1.0), (2.0, 3.0));
    }

    [Fact]
    public void APauseShorterThanTheMinimum_DoesNotSplitSpeech()
    {
        var audio = Audio.Of(Audio.Tone(1), Audio.Silence(0.3), Audio.Tone(1));

        Rounded(SilenceDetector.Detect(audio, TimeSpan.FromSeconds(0.5))).Should().Equal((0.0, 2.3));
    }

    [Fact]
    public void SilenceOnly_HasNoSpeech()
    {
        SilenceDetector.Detect(Audio.Of(Audio.Silence(3)), TimeSpan.FromSeconds(0.5)).Should().BeEmpty();
    }

    [Fact]
    public void ABlipShorterThanAHundredMilliseconds_IsNotSpeech()
    {
        var audio = Audio.Of(Audio.Silence(1), Audio.Tone(0.04), Audio.Silence(1));

        SilenceDetector.Detect(audio, TimeSpan.FromSeconds(0.5)).Should().BeEmpty();
    }
}
