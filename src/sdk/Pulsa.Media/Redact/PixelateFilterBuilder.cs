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
/// <summary>A rectangle to pixelate and, for a video, the seconds it is hidden for (both or neither).</summary>
public readonly record struct TimedRegion(RedactRegion Region, double? Start = null, double? End = null);

public static class PixelateFilterBuilder
{
    public static string Build(
        IReadOnlyList<RedactRegion> regions,
        double? startTime = null, double? endTime = null, int blockSize = 16)
    {
        ArgumentNullException.ThrowIfNull(regions);
        return BuildTimed(regions.Select(r => new TimedRegion(r, startTime, endTime)).ToList(), blockSize);
    }

    /// <summary>
    /// The same pixelation where each region has its own time range — a region without one is hidden for the whole
    /// video. Each range needs both its start and its end, with the start before the end.
    /// </summary>
    public static string BuildTimed(IReadOnlyList<TimedRegion> regions, int blockSize = 16)
    {
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
            throw new ArgumentException("Redaction needs at least one region.");
        if (regions.Any(r => r.Start.HasValue != r.End.HasValue))
            throw new ArgumentException("startTime and endTime must both be supplied together, or neither.");
        if (regions.Any(r => r.Region.Width <= 0 || r.Region.Height <= 0))
            throw new ArgumentException("width and height must both be positive.");
        if (regions.Any(r => r.Start.HasValue && r.Start.Value >= r.End!.Value))
            throw new ArgumentException("startTime must be less than endTime.");

        var inv = CultureInfo.InvariantCulture;

        var chains = new List<string>();
        for (var i = 0; i < regions.Count; i++)
        {
            var (x, y, width, height) = regions[i].Region;
            var crop = string.Format(inv, "crop={0}:{1}:{2}:{3}", width, height, x, y);
            // Computed as integers in C#, floored to 1, and emitted as literals -- NOT as an ffmpeg
            // expression string ("scale=width/blockSize:height/blockSize"). ffmpeg evaluates that
            // expression as a double and truncates to int; `scale` treats a resulting 0 as "keep the
            // input dimension" -- so any region smaller than blockSize on an axis silently skipped the
            // downscale and came back unpixelated. See
            // PixelateFilterBuilderTests.Build_RegionSmallerThanBlockSize_StillProducesNonTrivialDownscale.
            var downscale = string.Format(inv, "scale={0}:{1}", Math.Max(1, width / blockSize), Math.Max(1, height / blockSize));
            var upscale = string.Format(inv, "scale={0}:{1}:flags=neighbor", width, height);
            chains.Add($"[0:v]{crop},{downscale},{upscale}[blk{i}]");
        }

        // Each block is laid over the result of the previous overlay, so every region ends up in one picture.
        // format=auto lets overlay pick the best pixel format for what feeds it, instead of its default yuv420 --
        // which would push the whole frame of an RGB image through 4:2:0 chroma subsampling.
        for (var i = 0; i < regions.Count; i++)
        {
            var source = i == 0 ? "[0:v]" : $"[out{i - 1}]";
            var label = i == regions.Count - 1 ? string.Empty : $"[out{i}]";
            var enable = regions[i].Start.HasValue
                ? string.Format(inv, ":enable='between(t,{0},{1})'", regions[i].Start!.Value, regions[i].End!.Value)
                : string.Empty;
            chains.Add(string.Format(inv, "{0}[blk{1}]overlay={2}:{3}:format=auto{4}{5}",
                source, i, regions[i].Region.X, regions[i].Region.Y, enable, label));
        }

        // ";" separates filterchains per ffmpeg's filter-graph grammar; "," separates filters within a chain.
        return string.Join(";", chains);
    }
}
