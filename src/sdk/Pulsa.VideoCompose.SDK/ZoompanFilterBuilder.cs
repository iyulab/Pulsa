using System.Globalization;

namespace PulsaVideoCompose;

/// <summary>
/// Builds the ffmpeg -vf filter chain that turns one still image into an N-second Ken-Burns clip,
/// forced to a fixed 16:9 (or whatever VideoComposeOptions declares) frame size.
/// </summary>
public static class ZoompanFilterBuilder
{
    public static string Build(double sceneDurationSeconds, VideoComposeOptions? options = null)
    {
        if (sceneDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sceneDurationSeconds));
        var opts = options ?? new VideoComposeOptions();
        var frames = ComputeFrameCount(sceneDurationSeconds, opts);

        return string.Join(",",
            $"scale={opts.Width}:{opts.Height}:force_original_aspect_ratio=increase",
            $"crop={opts.Width}:{opts.Height}",
            string.Create(CultureInfo.InvariantCulture,
                $"zoompan=z='min(zoom+{opts.ZoomIncrementPerFrame},{opts.MaxZoom})':d={frames}:s={opts.Width}x{opts.Height}:fps={opts.Fps}"),
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
