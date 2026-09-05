namespace PulsaRedact;

/// <param name="X">Left edge of the region to pixelate, in pixels, relative to the source media's
/// native resolution.</param>
/// <param name="Y">Top edge of the region, same coordinate space as <paramref name="X"/>.</param>
/// <param name="Width">Region width in pixels.</param>
/// <param name="Height">Region height in pixels.</param>
/// <param name="StartTime">Video only, in seconds. Null (the default) means "from the start of the
/// video" — ignored entirely for a still-image input.</param>
/// <param name="EndTime">Video only, in seconds. Null (the default) means "to the end of the video" —
/// ignored entirely for a still-image input.</param>
public sealed record RedactRequest(
    string InputPath,
    string OutputPath,
    int X,
    int Y,
    int Width,
    int Height,
    double? StartTime = null,
    double? EndTime = null);

public sealed record RedactResult(bool Success, string? OutputPath, string? Error);
