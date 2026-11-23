using AwesomeAssertions;
using PulsaTranscribe;
using Xunit;
using static Pulsa.Tests.Transcribe.Time;

namespace Pulsa.Tests.Transcribe;

// A TimeMap is what keeps cues in order on the input's timeline: it can only be built moving forward, and it cannot be
// changed after it is built.
public sealed class TimeMapTests
{
    [Fact]
    public void AnchorsThatMoveBackwardOnEitherTimeline_AreRefused()
    {
        var act1 = () => new TimeMap([(S(0), S(0)), (S(2), S(6)), (S(1), S(7))]);
        var act2 = () => new TimeMap([(S(0), S(5)), (S(2), S(4))]);

        act1.Should().Throw<ArgumentException>().WithMessage("*forward*");
        act2.Should().Throw<ArgumentException>().WithMessage("*forward*");
    }

    [Fact]
    public void ChangingTheListItWasBuiltFrom_DoesNotChangeTheMap()
    {
        var anchors = new List<(TimeSpan Prepared, TimeSpan Input)> { (S(0), S(0)), (S(2), S(6)) };
        var map = new TimeMap(anchors);

        anchors[1] = (S(2), S(-5));
        anchors.Add((S(1), S(0)));

        map.ToInput(S(1)).Should().Be(S(3));
        map.Anchors.Should().HaveCount(2);
    }

    [Fact]
    public void BetweenAnchorsTimeIsLinear_AndBeyondThemTheSlopeIsOne()
    {
        var map = new TimeMap([(S(2), S(10)), (S(4), S(16))]);

        map.ToInput(S(3)).Should().Be(S(13));
        map.ToInput(S(1)).Should().Be(S(9));
        map.ToInput(S(5)).Should().Be(S(17));
    }
}
