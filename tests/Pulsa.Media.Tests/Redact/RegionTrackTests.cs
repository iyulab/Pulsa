using AwesomeAssertions;
using PulsaRedact;
using Xunit;

namespace Pulsa.Media.Tests.Redact;

public class RegionTrackTests
{
    private static DetectedRegion Face(int x, int y, int w = 40, int h = 40, string label = "face") =>
        new(new RedactRegion(x, y, w, h), label, 0.9);

    private static SampledLook Look(double time, params DetectedRegion[] found) => new(time, found);

    [Fact]
    public void Build_ARegionThatStaysPut_IsOneStretchFromItsFirstFrameToTheEnd()
    {
        var stretches = RegionTrack.Build([Look(0, Face(10, 10)), Look(2, Face(10, 10)), Look(4, Face(10, 10))], end: 6);

        stretches.Should().Equal(new TimedRegion(new RedactRegion(10, 10, 40, 40), 0, 6));
    }

    [Fact]
    public void Build_ARegionThatMoves_IsHiddenOverWhereItWasAndWhereItWentUntilTheNextFrame()
    {
        var stretches = RegionTrack.Build([Look(0, Face(10, 10)), Look(2, Face(30, 20))], end: 4);

        stretches.Should().Equal(
            new TimedRegion(new RedactRegion(10, 10, 60, 50), 0, 2),   // both positions: x 10..70, y 10..60
            new TimedRegion(new RedactRegion(30, 20, 40, 40), 2, 4));  // after the last frame, where it was last seen
    }

    [Fact]
    public void Build_ARegionThatIsGoneInTheNextFrame_IsHiddenOnlyUntilThatFrame()
    {
        var stretches = RegionTrack.Build([Look(0, Face(10, 10)), Look(2), Look(4)], end: 6);

        stretches.Should().Equal(new TimedRegion(new RedactRegion(10, 10, 40, 40), 0, 2));
    }

    [Fact]
    public void Build_ARegionThatAppears_IsHiddenFromItsFrame_NotBefore()
    {
        var stretches = RegionTrack.Build([Look(0), Look(2, Face(50, 50))], end: 4);

        stretches.Should().Equal(new TimedRegion(new RedactRegion(50, 50, 40, 40), 2, 4));
    }

    [Fact]
    public void Build_TwoThingsOfTheSameKind_FollowTheirOwnNeighbour()
    {
        var stretches = RegionTrack.Build(
            [Look(0, Face(0, 0), Face(100, 0)), Look(2, Face(104, 0), Face(4, 0))], end: 2.0001);

        stretches.Select(s => s.Region).Should().BeEquivalentTo(
            [new RedactRegion(0, 0, 44, 40), new RedactRegion(100, 0, 44, 40),
             new RedactRegion(104, 0, 40, 40), new RedactRegion(4, 0, 40, 40)]);
    }

    [Fact]
    public void Build_ARegionOfAnotherLabelOrWithNoOverlap_IsNotTheSameThing()
    {
        var stretches = RegionTrack.Build(
            [Look(0, Face(10, 10)), Look(2, Face(20, 20, label: "plate"), Face(200, 200))], end: 4);

        stretches.Should().Contain(new TimedRegion(new RedactRegion(10, 10, 40, 40), 0, 2));
        stretches.Should().Contain(new TimedRegion(new RedactRegion(20, 20, 40, 40), 2, 4));
        stretches.Should().Contain(new TimedRegion(new RedactRegion(200, 200, 40, 40), 2, 4));
    }

    [Fact]
    public void Build_NoLooks_NoStretches() =>
        RegionTrack.Build([], end: 10).Should().BeEmpty();
}
