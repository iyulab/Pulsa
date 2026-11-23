namespace PulsaRedact;

/// <summary>A rectangle to pixelate, in pixels of the source media's own resolution.</summary>
public readonly record struct RedactRegion(int X, int Y, int Width, int Height);

/// <param name="Regions">The rectangles to pixelate. May be empty only when a detector is given — then the detector's
/// regions are all there is to hide.</param>
/// <param name="StartTime">Video only, in seconds. Null (the default) means "from the start of the
/// video" — ignored entirely for a still-image input.</param>
/// <param name="EndTime">Video only, in seconds. Null (the default) means "to the end of the video" —
/// ignored entirely for a still-image input.</param>
public sealed record RedactRequest(
    string InputPath,
    string OutputPath,
    IReadOnlyList<RedactRegion> Regions,
    double? StartTime = null,
    double? EndTime = null);

/// <summary>
/// What redaction did. <see cref="Detected"/> are the regions a detector found and that were hidden;
/// <see cref="Review"/> are the ones it found below the confidence bar, returned for a person to decide on and not
/// hidden; <see cref="Notes"/> name detector regions the code refused (outside the picture, too small).
/// </summary>
public sealed record RedactResult(bool Success, string? OutputPath, string? Error)
{
    public IReadOnlyList<DetectedRegion> Detected { get; init; } = [];
    public IReadOnlyList<DetectedRegion> Review { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A region a detector found: where, what (the detector's own label) and how sure it is (0–1).</summary>
public sealed record DetectedRegion(RedactRegion Region, string Label, double Confidence);

/// <summary>
/// Detection port — the model role of redaction: given an image, where the things to hide are. Implemented by a vision
/// model (<see cref="ChatRegionDetector"/>) or a detection model adapter. The code checks what it returns.
/// </summary>
public interface IRegionDetector
{
    string Name { get; }

    /// <param name="imagePath">The image to look at.</param>
    /// <param name="width">Its width in pixels — regions are in this coordinate space.</param>
    /// <param name="height">Its height in pixels.</param>
    Task<IReadOnlyList<DetectedRegion>> DetectAsync(string imagePath, int width, int height, CancellationToken cancellationToken);
}

/// <summary>
/// Redaction's extending stage: a detector, and the confidence a found region needs to be hidden (default 0.5).
/// For a video the detector looks at one picture — the frame at <paramref name="FrameAt"/> seconds (default: the start
/// of the time range, else the first frame) — and what it finds is hidden as fixed rectangles over the whole time range.
/// </summary>
public sealed record RedactDetection(IRegionDetector Detector, double MinimumConfidence = 0.5, double? FrameAt = null);
