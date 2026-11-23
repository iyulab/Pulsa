using FFMpegCore;

namespace PulsaRedact;

/// <summary>
/// Pixelates rectangular regions of an image or video via a single ffmpeg pass. Image vs. video is decided by file
/// extension (as the video composer does — no content-sniffing). No motion tracking: each region is one fixed rectangle
/// for the whole applicable time range. Given a detector, it also finds what to hide: in an image, in the picture; in a
/// video, in the one frame the caller picks, and what it finds is hidden over the whole time range. The code checks each
/// found region against the picture and hides only those above the confidence bar, returning the rest for review.
/// </summary>
public sealed class MediaRedactor
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    /// <summary>A detected region smaller than this on either side, after it is clipped to the picture, is refused.</summary>
    internal const int MinimumDetectedSide = 2;

    private readonly FFOptions _ffOptions;

    public MediaRedactor(string ffmpegBinaryFolder)
    {
        _ffOptions = new FFOptions { BinaryFolder = ffmpegBinaryFolder };
    }

    public Task<RedactResult> RedactAsync(RedactRequest request, CancellationToken cancellationToken = default) =>
        RedactAsync(request, detection: null, cancellationToken);

    public async Task<RedactResult> RedactAsync(
        RedactRequest request, RedactDetection? detection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (File.Exists(request.OutputPath))
            return new RedactResult(false, null, $"{request.OutputPath} already exists");
        if (!File.Exists(request.InputPath))
            return new RedactResult(false, null, $"{request.InputPath} does not exist");

        var isImage = ImageExtensions.Contains(Path.GetExtension(request.InputPath));
        if (detection is null && request.Regions.Count == 0)
            return new RedactResult(false, null, "There is nothing to redact: no regions and no detector.");
        if (detection is { MinimumConfidence: < 0 or > 1 })
            return new RedactResult(false, null, "The minimum confidence must be between 0 and 1.");
        if (detection is { FrameAt: < 0 })
            return new RedactResult(false, null, "The frame to look at must not be before the start of the video.");

        try
        {
            var regions = request.Regions.ToList();
            List<DetectedRegion> hidden = [], review = [];
            List<string> notes = [];
            if (detection is not null)
            {
                // The picture the detector looks at: the image itself, or for a video one frame taken out of it. Its
                // size is read from that picture, so regions are in the coordinates the pixelate filter sees (a video
                // with rotation metadata is turned upright when decoded, and so is the frame).
                string picture = request.InputPath, frameFile = string.Empty;
                try
                {
                    if (!isImage)
                    {
                        var at = detection.FrameAt ?? request.StartTime ?? 0;
                        frameFile = Path.Combine(Path.GetTempPath(), $"pulsa-redact-frame-{Guid.NewGuid():N}.png");
                        await FFMpegArguments
                            .FromFileInput(request.InputPath, verifyExists: true, opt => opt.Seek(TimeSpan.FromSeconds(at)))
                            .OutputToFile(frameFile, overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
                            .CancellableThrough(cancellationToken)
                            .ProcessAsynchronously(ffMpegOptions: _ffOptions);
                        if (!File.Exists(frameFile) || new FileInfo(frameFile).Length == 0)
                            return new RedactResult(false, null, $"There is no picture at {at:0.###} s in {request.InputPath}.");
                        picture = frameFile;
                    }

                    var (width, height) = await PictureSize.ReadAsync(picture, _ffOptions, cancellationToken);
                    var found = await detection.Detector.DetectAsync(picture, width, height, cancellationToken);
                    foreach (var candidate in found)
                    {
                        var clipped = Clip(candidate.Region, width, height);
                        if (clipped is null)
                        {
                            notes.Add($"Refused '{candidate.Label}' at {Describe(candidate.Region)}: it lies outside the " +
                                      $"{width}x{height} picture or is under {MinimumDetectedSide} px once clipped.");
                            continue;
                        }
                        var checkedRegion = candidate with { Region = clipped.Value };
                        if (candidate.Confidence >= detection.MinimumConfidence)
                        {
                            hidden.Add(checkedRegion);
                            regions.Add(clipped.Value);
                        }
                        else
                        {
                            review.Add(checkedRegion);
                        }
                    }
                }
                finally
                {
                    if (frameFile.Length > 0 && File.Exists(frameFile)) File.Delete(frameFile);
                }
            }

            if (regions.Count == 0)
                return new RedactResult(false, null, "The detector found nothing to hide with enough confidence.")
                {
                    Review = review,
                    Notes = notes,
                };

            // Images never carry a time range — StartTime/EndTime are accepted but ignored. Build is called inside
            // the try so its own ArgumentException becomes a failure result, like every other failure here.
            var filter = PixelateFilterBuilder.Build(
                regions, isImage ? null : request.StartTime, isImage ? null : request.EndTime);

            var args = FFMpegArguments
                .FromFileInput(request.InputPath, verifyExists: true, opt =>
                {
                    if (isImage) opt.WithCustomArgument("-loop 1");
                })
                .OutputToFile(request.OutputPath, overwrite: false, opt =>
                {
                    opt.WithCustomArgument($"-filter_complex \"{filter}\"");
                    if (isImage)
                    {
                        opt.WithCustomArgument("-frames:v 1");
                        // -update 1 is the documented-stable form for single-image output.
                        opt.WithCustomArgument("-update 1");
                    }
                });

            await args.CancellableThrough(cancellationToken).ProcessAsynchronously(ffMpegOptions: _ffOptions);
            return new RedactResult(true, request.OutputPath, null) { Detected = hidden, Review = review, Notes = notes };
        }
        catch (Exception ex)
        {
            return new RedactResult(false, null, ex.Message);
        }
    }

    /// <summary>The part of <paramref name="region"/> inside the picture, or null when too little of it is.</summary>
    internal static RedactRegion? Clip(RedactRegion region, int width, int height)
    {
        var left = Math.Max(0, region.X);
        var top = Math.Max(0, region.Y);
        var right = Math.Min(width, (long)region.X + region.Width);
        var bottom = Math.Min(height, (long)region.Y + region.Height);
        if (right - left < MinimumDetectedSide || bottom - top < MinimumDetectedSide) return null;
        return new RedactRegion(left, top, (int)(right - left), (int)(bottom - top));
    }

    private static string Describe(RedactRegion r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";
}
