using System.Globalization;

namespace PulsaVideoCompose;

/// <summary>
/// Builds the ffmpeg -vf filter chain that turns one still image into an N-second Ken-Burns clip,
/// forced to a fixed 16:9 (or whatever VideoComposeOptions declares) frame size.
/// </summary>
/// <remarks>
/// Motion variety is entirely internal: the caller supplies only a scene index, never a pan or
/// zoom-direction knob. <see cref="Presets"/> is a small fixed, ordered table of motions; the
/// scene index selects one deterministically (plain modulo), so the same scene index always
/// yields the same motion (reproducible, testable — never randomized) and consecutive scenes
/// cycle through visually distinct Ken-Burns moves instead of every clip repeating the same
/// top-left-anchored zoom-in. Rotation is deliberately not part of the table (not part of standard Ken
/// Burns, added complexity for no clear benefit) and scene-to-scene transitions stay hard cuts
/// (no crossfade — see FfmpegVideoComposer's class doc on why this codebase avoids
/// filter_complex).
/// </remarks>
public static class ZoompanFilterBuilder
{
    private enum ZoomDirection { In, Out }

    /// <summary>
    /// One entry in the motion preset table. <paramref name="PanTargetX"/>/<paramref name="PanTargetY"/>
    /// are each a fraction in [0,1] describing which point of the source frame the crop window is
    /// ANCHORED to at maximum zoom: 0 = that axis's start edge, 1 = its end edge, 0.5 = that axis's
    /// center (no pan on that axis). This is an anchor, not a "drift toward" direction — for a
    /// zoom-IN preset the window visibly moves toward that anchor as zoom increases from 1.0; for a
    /// zoom-OUT preset zoom instead starts pinned at that anchor (seeded at MaxZoom, see the `z`
    /// expression below) and the window drifts back toward frame CENTER as zoom decreases toward
    /// 1.0 — the opposite visible direction, same anchor point. <see langword="null"/> for both means
    /// "omit x=/y= from the emitted filter entirely" — zoompan's own default (x=0, y=0, i.e. a fixed
    /// top-left anchor) then applies, which is exactly what the pre-variety <c>Build</c> emitted.
    /// Preset 0 uses this so its output is byte-identical to the old signature's — see the
    /// ZoompanFilterBuilderTests test suite.
    /// </summary>
    private sealed record MotionPreset(ZoomDirection Zoom, double? PanTargetX, double? PanTargetY);

    /// <summary>
    /// The fixed, ordered motion table. Index 0 is exactly today's shipped behavior (zoom in,
    /// legacy top-left-anchored default) for backward compatibility; the rest add genuine variety
    /// — zoom in AND out, plus four corner pan targets — while staying tasteful (max pan distance
    /// at <see cref="VideoComposeOptions.MaxZoom"/>'s default of 1.5 is ~33% of frame width/height,
    /// not a dizzying edge-to-edge sweep).
    /// </summary>
    private static readonly MotionPreset[] Presets =
    [
        new(ZoomDirection.In,  null, null), // 0: legacy default — zoom in, top-left anchor (byte-compat)
        new(ZoomDirection.In,  1.0,  1.0),  // 1: zoom in,  anchored bottom-right
        new(ZoomDirection.Out, 0.0,  0.0),  // 2: zoom out, anchored top-left (peak zoom), drifts to center
        new(ZoomDirection.In,  1.0,  0.0),  // 3: zoom in,  anchored top-right
        new(ZoomDirection.Out, 0.0,  1.0),  // 4: zoom out, anchored bottom-left (peak zoom), drifts to center
        new(ZoomDirection.Out, 0.5,  0.5),  // 5: zoom out, centered anchor (no pan)
    ];

    public static string Build(int sceneIndex, double sceneDurationSeconds, VideoComposeOptions? options = null)
    {
        if (sceneIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sceneIndex));
        if (sceneDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sceneDurationSeconds));
        var opts = options ?? new VideoComposeOptions();
        var frames = ComputeFrameCount(sceneDurationSeconds, opts);
        var preset = Presets[sceneIndex % Presets.Length];

        // z: same "min(zoom+inc,max)" ramp the old code always used for zoom-in. For zoom-out,
        // zoompan's own `zoom` state variable defaults to 1 on the first output frame — there is
        // no way to seed it directly — so the `on` (output-frame-number) variable is used to seed
        // MaxZoom explicitly on frame 0, then ramp down toward 1.0 afterward. This `on`-seeding
        // technique is the standard documented pattern for a zoompan zoom-out (ffmpeg's own
        // zoompan filter docs list `on` for exactly this kind of first-frame conditional).
        var zExpr = preset.Zoom == ZoomDirection.In
            ? string.Create(CultureInfo.InvariantCulture, $"min(zoom+{opts.ZoomIncrementPerFrame},{opts.MaxZoom})")
            : string.Create(CultureInfo.InvariantCulture, $"if(eq(on,0),{opts.MaxZoom},max(zoom-{opts.ZoomIncrementPerFrame},1.0))");

        // x/y: the crop window's top-left corner in source pixels is (iw-iw/zoom, ih-ih/zoom) at
        // full pan toward the end edge; multiplying by the preset's [0,1] target fraction gives a
        // window that sits still at zoom=1 (no headroom to pan yet, since scale+crop upstream
        // already sized the source to exactly iw=ow, ih=oh) and drifts linearly toward the target
        // corner/center as zoom grows — the standard "pan to corner" zoompan idiom.
        var zoompanSegment = preset.PanTargetX is { } tx && preset.PanTargetY is { } ty
            ? string.Create(CultureInfo.InvariantCulture,
                $"zoompan=z='{zExpr}':x='(iw-iw/zoom)*{tx}':y='(ih-ih/zoom)*{ty}':d={frames}:s={opts.Width}x{opts.Height}:fps={opts.Fps}")
            : string.Create(CultureInfo.InvariantCulture,
                $"zoompan=z='{zExpr}':d={frames}:s={opts.Width}x{opts.Height}:fps={opts.Fps}");

        return string.Join(",",
            $"scale={opts.Width}:{opts.Height}:force_original_aspect_ratio=increase",
            $"crop={opts.Width}:{opts.Height}",
            zoompanSegment,
            "format=yuv420p");
    }

    /// <summary>
    /// The same scale/crop/zoompan/format chain <see cref="Build"/> emits, but with no zoom or pan
    /// at all — a static hold, for content (a title/outro card) that shouldn't move. Omitting
    /// zoompan's <c>z=</c> entirely leaves it at the filter's own documented default of <c>1</c>
    /// (no zoom change frame to frame), the same "omit for the plain default" idiom
    /// <see cref="Presets"/>'s entry 0 already uses for <c>x=</c>/<c>y=</c>.
    /// </summary>
    public static string BuildStaticHold(double sceneDurationSeconds, VideoComposeOptions? options = null)
    {
        if (sceneDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sceneDurationSeconds));
        var opts = options ?? new VideoComposeOptions();
        var frames = ComputeFrameCount(sceneDurationSeconds, opts);

        return string.Join(",",
            $"scale={opts.Width}:{opts.Height}:force_original_aspect_ratio=increase",
            $"crop={opts.Width}:{opts.Height}",
            string.Create(CultureInfo.InvariantCulture, $"zoompan=d={frames}:s={opts.Width}x{opts.Height}:fps={opts.Fps}"),
            "format=yuv420p");
    }

    /// <summary>
    /// The exact output frame count the zoompan filter above is built to produce for one scene —
    /// the caller must cap ffmpeg's output at this same frame count (e.g. `-frames:v`), not at a
    /// wall-clock duration. zoompan's `d` parameter means "emit d output frames per received input
    /// frame," so an input-side time cap (`-t`) on a looped still image forces the demuxer to hand
    /// zoompan multiple distinct input frames within that window, and each one restarts a fresh
    /// d-frame zoom — multiplying, not capping, the clip length.
    /// </summary>
    public static int ComputeFrameCount(double sceneDurationSeconds, VideoComposeOptions? options = null)
    {
        var opts = options ?? new VideoComposeOptions();
        return Math.Max(1, (int)Math.Round(sceneDurationSeconds * opts.Fps, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The scene duration actually realized on screen once <see cref="ComputeFrameCount"/>'s
    /// frame-quantization is applied — rarely identical to <paramref name="sceneDurationSeconds"/>
    /// (e.g. 2.5s @ 25fps rounds to 63 frames, i.e. 2.52s). Captions must be timed against this
    /// effective duration, not the raw requested one, or subtitle timing drifts out of sync with
    /// the rendered picture, and the drift compounds scene over scene.
    /// </summary>
    public static double ComputeEffectiveSceneDuration(double sceneDurationSeconds, VideoComposeOptions? options = null)
    {
        var opts = options ?? new VideoComposeOptions();
        return ComputeFrameCount(sceneDurationSeconds, opts) / (double)opts.Fps;
    }
}
