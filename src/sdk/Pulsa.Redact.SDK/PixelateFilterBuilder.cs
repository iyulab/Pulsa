using System.Globalization;

namespace PulsaRedact;

/// <summary>
/// Builds the ffmpeg <c>-vf</c> filter-graph string for region-limited pixelation: crop the target
/// rectangle out, downscale it by <paramref name="blockSize"/>, scale it back up with nearest-neighbor
/// (no interpolation — this is what produces the blocky mosaic look, not a blur), then composite it
/// back over the original frame at the same coordinates. Optionally time-gated for video via ffmpeg's
/// <c>enable</c> expression, evaluated per-frame against the filter's local timeline (empirically
/// confirmed to work for <c>overlay</c> at implementation time — see MediaRedactorTests' live test,
/// not assumed by analogy to composer's own <c>subtitles</c>-filter caveat).
/// </summary>
public static class PixelateFilterBuilder
{
    public static string Build(
        int x, int y, int width, int height,
        double? startTime = null, double? endTime = null, int blockSize = 16)
    {
        if (startTime.HasValue != endTime.HasValue)
            throw new ArgumentException("startTime and endTime must both be supplied together, or neither.");

        var inv = CultureInfo.InvariantCulture;
        var crop = string.Format(inv, "crop={0}:{1}:{2}:{3}", width, height, x, y);
        var downscale = string.Format(inv, "scale={0}/{1}:{2}/{1}", width, blockSize, height);
        var upscale = string.Format(inv, "scale={0}:{1}:flags=neighbor", width, height);

        var enableClause = startTime.HasValue
            ? string.Format(inv, ":enable='between(t,{0},{1})'", startTime.Value, endTime!.Value)
            : string.Empty;
        var overlay = string.Format(inv, "overlay={0}:{1}{2}", x, y, enableClause);

        return string.Join(",",
            $"[0:v]{crop},{downscale},{upscale}[blk]",
            $"[0:v][blk]{overlay}");
    }
}
