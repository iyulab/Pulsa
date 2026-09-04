using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class ZoompanFilterBuilderTests
{
    [Fact]
    public void Build_DefaultOptions_ProducesScaleCropZoompanFormatChain()
    {
        var filter = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds: 4);

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
        var filter = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds: 3, options);

        filter.Should().Contain("scale=1080:1920:force_original_aspect_ratio=increase");
        filter.Should().Contain("d=90"); // 3 seconds * 30 fps
        filter.Should().Contain("min(zoom+0.002,1.3)");
    }

    [Fact]
    public void Build_FractionalDuration_RoundsFrameCount()
    {
        var filter = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds: 2.5); // 2.5 * 25 = 62.5 -> 63

        filter.Should().Contain("d=63");
    }

    [Fact]
    public void Build_SceneIndexZero_IsByteIdenticalToLegacyTopLeftAnchoredZoomInBehavior()
    {
        // Preset 0 must reproduce exactly what the pre-variety Build(sceneDurationSeconds, options)
        // used to emit — same z expression, and no explicit x=/y= at all (zoompan's own default of
        // x=0,y=0 applies). This is the sanity check that scene 0's behavior did not regress.
        var filter = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds: 4);

        filter.Should().Be(
            "scale=1920:1080:force_original_aspect_ratio=increase," +
            "crop=1920:1080," +
            "zoompan=z='min(zoom+0.0015,1.5)':d=100:s=1920x1080:fps=25," +
            "format=yuv420p");
    }

    [Fact]
    public void Build_NegativeSceneIndex_Throws()
    {
        var act = () => ZoompanFilterBuilder.Build(sceneIndex: -1, sceneDurationSeconds: 4);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Build_DifferentSceneIndices_ProduceDifferentZoompanFilters()
    {
        // Genuine variety: two different scene indices must select two different presets, so the
        // zoompan segment (z/x/y) differs — not just an incidental string difference elsewhere.
        var scene0 = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds: 4);
        var scene1 = ZoompanFilterBuilder.Build(sceneIndex: 1, sceneDurationSeconds: 4);

        scene0.Should().NotBe(scene1);
    }

    [Fact]
    public void Build_SameSceneIndexCalledTwice_ProducesIdenticalFilters()
    {
        // Determinism: motion selection must be a pure function of sceneIndex, never randomized.
        var first = ZoompanFilterBuilder.Build(sceneIndex: 3, sceneDurationSeconds: 4);
        var second = ZoompanFilterBuilder.Build(sceneIndex: 3, sceneDurationSeconds: 4);

        first.Should().Be(second);
    }

    [Fact]
    public void Build_SceneIndexBeyondPresetCount_WrapsAroundDeterministically()
    {
        // Plain modulo cycling: scene index N and N + presetCount must select the same preset.
        // The preset table currently has 6 entries (indices 0-5); scene 0 and scene 6 must match.
        var scene0 = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds: 4);
        var scene6 = ZoompanFilterBuilder.Build(sceneIndex: 6, sceneDurationSeconds: 4);

        scene0.Should().Be(scene6);
    }

    [Fact]
    public void Build_AZoomOutPreset_HasAZExpressionThatIsTheGenuineReverseOfZoomIn()
    {
        // Scene 0's z expression zoom-INs: starts near 1.0 (zoompan's implicit initial zoom) and
        // increases toward MaxZoom via min(zoom+inc, MaxZoom). Scene 2 (an "Out" preset) must
        // instead start AT MaxZoom and decrease toward 1.0 — the actual reverse semantics, not
        // just an arbitrary different string.
        var zoomInFilter = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds: 4);
        var zoomOutFilter = ZoompanFilterBuilder.Build(sceneIndex: 2, sceneDurationSeconds: 4);

        zoomInFilter.Should().Contain("min(zoom+0.0015,1.5)");
        zoomOutFilter.Should().Contain("if(eq(on,0),1.5,max(zoom-0.0015,1.0))");
    }

    [Fact]
    public void Build_APannedPreset_EmitsExplicitXAndYExpressionsDrivenByZoom()
    {
        // A directional pan (scene 1: bottom-right) must emit explicit x=/y= expressions that move
        // the crop window as a function of the current zoom level — not the legacy omitted-x/y
        // (fixed at 0,0) that scene 0 keeps for backward compatibility.
        var pannedFilter = ZoompanFilterBuilder.Build(sceneIndex: 1, sceneDurationSeconds: 4);

        pannedFilter.Should().Contain("x='(iw-iw/zoom)*1'");
        pannedFilter.Should().Contain("y='(ih-ih/zoom)*1'");
    }

    [Fact]
    public void ComputeFrameCount_MatchesTheDValueBuildEmits()
    {
        // The caller (FfmpegVideoComposer) must cap ffmpeg's output at exactly this many frames
        // (e.g. -frames:v) — not at a wall-clock -t on the input — or zoompan's per-input-frame
        // `d` re-triggers and multiplies the clip length instead of bounding it. Asserting against
        // Build()'s own emitted `d=` value (not a hardcoded number) is what actually guards against
        // the two ever drifting apart — e.g. if a future edit inlines the formula back into Build
        // and it silently disagrees with ComputeFrameCount, this is the test that would catch it.
        AssertBuildAndComputeFrameCountAgree(sceneDurationSeconds: 4, options: null);
        AssertBuildAndComputeFrameCountAgree(sceneDurationSeconds: 2.5, options: null);
        AssertBuildAndComputeFrameCountAgree(sceneDurationSeconds: 3, options: new VideoComposeOptions(Fps: 30));
    }

    private static void AssertBuildAndComputeFrameCountAgree(double sceneDurationSeconds, VideoComposeOptions? options)
    {
        var filter = ZoompanFilterBuilder.Build(sceneIndex: 0, sceneDurationSeconds, options);
        var frameCount = ZoompanFilterBuilder.ComputeFrameCount(sceneDurationSeconds, options);

        filter.Should().Contain($"d={frameCount}");
    }

    [Fact]
    public void ComputeEffectiveSceneDuration_DurationDividesFrameCountEvenly_ReturnsTheInputUnchanged()
    {
        // 4s @ 25fps (default) -> 100 frames, which divides back out to exactly 4s.
        var effective = ZoompanFilterBuilder.ComputeEffectiveSceneDuration(sceneDurationSeconds: 4);

        effective.Should().Be(4.0);
    }

    [Fact]
    public void ComputeEffectiveSceneDuration_FractionalDuration_ReturnsTheFrameQuantizedValue()
    {
        // 2.5s @ 25fps rounds up to 63 frames (ComputeFrameCount_MatchesTheDValueBuildEmits /
        // Build_FractionalDuration_RoundsFrameCount already assert d=63) -> 63/25 = 2.52s actual,
        // not the raw 2.5s requested. This is the case WriteSubtitlesAsync must time captions
        // against, or subtitle timing drifts out of sync with the rendered picture.
        var effective = ZoompanFilterBuilder.ComputeEffectiveSceneDuration(sceneDurationSeconds: 2.5);

        effective.Should().Be(63.0 / 25.0);
        effective.Should().NotBe(2.5);
    }
}
