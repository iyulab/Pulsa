namespace PulsaVideoCompose;

/// <summary>
/// Maps a named frame shape to the fixed pixel dimensions <see cref="ZoompanFilterBuilder"/>'s
/// scale/crop/zoompan chain already renders at any size — this is the only place those two named
/// values become concrete numbers, so a third shape is a one-line addition here, not a change to
/// the render pipeline itself.
/// </summary>
public static class AspectRatioPresets
{
    /// <summary>
    /// Returns <paramref name="baseOptions"/> with <c>Width</c>/<c>Height</c> replaced by the named
    /// shape's dimensions (every other option — Fps, zoom tuning — passes through unchanged).
    /// Throws <see cref="ArgumentException"/> for any value other than "16:9" or "9:16".
    /// </summary>
    public static VideoComposeOptions Resolve(string aspectRatio, VideoComposeOptions baseOptions) =>
        aspectRatio switch
        {
            "16:9" => baseOptions with { Width = 1920, Height = 1080 },
            "9:16" => baseOptions with { Width = 1080, Height = 1920 },
            _ => throw new ArgumentException(
                $"Unsupported aspectRatio '{aspectRatio}'. Supported values: 16:9, 9:16.", nameof(aspectRatio))
        };
}
