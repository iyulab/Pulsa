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
        var frames = Math.Max(1, (int)Math.Round(sceneDurationSeconds * opts.Fps, MidpointRounding.AwayFromZero));

        return string.Join(",",
            $"scale={opts.Width}:{opts.Height}:force_original_aspect_ratio=increase",
            $"crop={opts.Width}:{opts.Height}",
            string.Create(CultureInfo.InvariantCulture,
                $"zoompan=z='min(zoom+{opts.ZoomIncrementPerFrame},{opts.MaxZoom})':d={frames}:s={opts.Width}x{opts.Height}:fps={opts.Fps}"),
            "format=yuv420p");
    }
}
