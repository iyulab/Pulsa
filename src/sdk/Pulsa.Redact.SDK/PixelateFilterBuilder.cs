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
        if (width <= 0 || height <= 0)
            throw new ArgumentException("width and height must both be positive.");
        if (startTime.HasValue && startTime.Value >= endTime!.Value)
            throw new ArgumentException("startTime must be less than endTime.");

        var inv = CultureInfo.InvariantCulture;
        var crop = string.Format(inv, "crop={0}:{1}:{2}:{3}", width, height, x, y);
        // Computed as integers in C#, floored to 1, and emitted as literals -- NOT as an ffmpeg
        // expression string ("scale=width/blockSize:height/blockSize"). ffmpeg evaluates that
        // expression as a double and truncates to int; `scale` treats a resulting 0 as "keep the
        // input dimension" -- so any region smaller than blockSize on an axis (e.g. a 10x10 region
        // with the default blockSize=16) silently skipped the downscale entirely and came back
        // unpixelated while RedactResult.Success stayed true. See
        // PixelateFilterBuilderTests.Build_RegionSmallerThanBlockSize_StillProducesNonTrivialDownscale
        // and MediaRedactorTests' live sub-blockSize regression test.
        var blockWidth = Math.Max(1, width / blockSize);
        var blockHeight = Math.Max(1, height / blockSize);
        var downscale = string.Format(inv, "scale={0}:{1}", blockWidth, blockHeight);
        var upscale = string.Format(inv, "scale={0}:{1}:flags=neighbor", width, height);

        var enableClause = startTime.HasValue
            ? string.Format(inv, ":enable='between(t,{0},{1})'", startTime.Value, endTime!.Value)
            : string.Empty;
        // format=auto lets overlay pick the best pixel format for what's actually feeding it,
        // instead of falling back to its default internal yuv420 -- which would force the ENTIRE
        // frame (not just the pixelated rectangle) through 4:2:0 chroma subsampling when the
        // source is an RGB image. Video inputs are already yuv420p so they're unaffected either way.
        var overlay = string.Format(inv, "overlay={0}:{1}:format=auto{2}", x, y, enableClause);

        // ";" separates filterchains per ffmpeg's documented filter-graph grammar; "," (used
        // above, inside each chain) separates filters within a single chain. The old code used
        // "," for both, which happened to parse via ffmpeg's parser leniency but isn't the
        // documented separator and isn't directly copy-pasteable as a standalone -filter_complex.
        return string.Join(";",
            $"[0:v]{crop},{downscale},{upscale}[blk]",
            $"[0:v][blk]{overlay}");
    }
}
