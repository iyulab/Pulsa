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
        if (detection is { SampleEvery: <= 0 })
            return new RedactResult(false, null, "The time between frames to look at must be positive.");
        if (detection is { SampleEvery: not null, FrameAt: not null })
            return new RedactResult(false, null, "Look at one frame (FrameAt) or at a frame every so often (SampleEvery), not both.");
        if (detection is { MaxSamples: < 1 })
            return new RedactResult(false, null, "At least one frame must be allowed for the detector.");

        try
        {
            // Images never carry a time range — StartTime/EndTime are accepted but ignored.
            var from = isImage ? null : request.StartTime;
            var until = isImage ? null : request.EndTime;
            var regions = request.Regions.Select(r => new TimedRegion(r, from, until)).ToList();
            List<DetectedRegion> hidden = [], review = [];
            List<string> notes = [];
            if (detection is not null)
            {
                if (!isImage && detection.SampleEvery is { } every)
                {
                    var length = until ?? await PictureSize.ReadDurationAsync(request.InputPath, _ffOptions, cancellationToken);
                    var first = from ?? 0;
                    if (first >= length)
                        return new RedactResult(false, null, "The time range to redact is empty: it starts at or after its end.");
                    var count = (int)Math.Min(int.MaxValue, Math.Floor((length - first) / every - 1e-9) + 1);
                    if (count > detection.MaxSamples)
                        return new RedactResult(false, null,
                            $"A frame every {every:0.###} s over {length - first:0.###} s is {count} frames for the detector, " +
                            $"above the {detection.MaxSamples} allowed; use a longer interval or raise MaxSamples.");

                    var looks = new List<SampledLook>();
                    for (var k = 0; k < count; k++)
                    {
                        var at = first + k * every;
                        var look = await LookAsync(request.InputPath, isImage, at, detection, cancellationToken);
                        hidden.AddRange(look.Hidden.Select(d => d with { At = at }));
                        review.AddRange(look.Review.Select(d => d with { At = at }));
                        notes.AddRange(look.Notes.Select(n => $"At {at:0.###} s: {n}"));
                        looks.Add(new SampledLook(at, look.Hidden.Select(d => d with { At = at }).ToList()));
                    }
                    regions.AddRange(RegionTrack.Build(looks, length));
                }
                else
                {
                    var look = await LookAsync(request.InputPath, isImage, detection.FrameAt ?? request.StartTime ?? 0, detection, cancellationToken);
                    hidden.AddRange(look.Hidden);
                    review.AddRange(look.Review);
                    notes.AddRange(look.Notes);
                    regions.AddRange(look.Hidden.Select(d => new TimedRegion(d.Region, from, until)));
                }
            }

            if (regions.Count == 0)
                return new RedactResult(false, null, "The detector found nothing to hide with enough confidence.")
                {
                    Review = review,
                    Notes = notes,
                };

            // Build is called inside the try so its own ArgumentException becomes a failure result, like every other
            // failure here.
            var filter = PixelateFilterBuilder.BuildTimed(regions);

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

    private sealed record Look(List<DetectedRegion> Hidden, List<DetectedRegion> Review, List<string> Notes);

    /// <summary>
    /// One look of the detector at a picture: the image itself, or for a video the frame at <paramref name="at"/> seconds.
    /// Its size is read from that picture, so regions are in the coordinates the pixelate filter sees (a video with
    /// rotation metadata is turned upright when decoded, and so is the frame). Each found region is checked against the
    /// picture and sorted into those hidden (at or above the confidence bar) and those left for review.
    /// </summary>
    private async Task<Look> LookAsync(
        string input, bool isImage, double at, RedactDetection detection, CancellationToken cancellationToken)
    {
        var look = new Look([], [], []);
        string picture = input, frameFile = string.Empty;
        try
        {
            if (!isImage)
            {
                frameFile = Path.Combine(Path.GetTempPath(), $"pulsa-redact-frame-{Guid.NewGuid():N}.png");
                await FFMpegArguments
                    .FromFileInput(input, verifyExists: true, opt => opt.Seek(TimeSpan.FromSeconds(at)))
                    .OutputToFile(frameFile, overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
                    .CancellableThrough(cancellationToken)
                    .ProcessAsynchronously(ffMpegOptions: _ffOptions);
                if (!File.Exists(frameFile) || new FileInfo(frameFile).Length == 0)
                    throw new InvalidDataException($"There is no picture at {at:0.###} s in {input}.");
                picture = frameFile;
            }

            var (width, height) = await PictureSize.ReadAsync(picture, _ffOptions, cancellationToken);
            var found = await detection.Detector.DetectAsync(picture, width, height, cancellationToken);
            foreach (var candidate in found)
            {
                var clipped = Clip(candidate.Region, width, height);
                if (clipped is null)
                {
                    look.Notes.Add($"Refused '{candidate.Label}' at {Describe(candidate.Region)}: it lies outside the " +
                                   $"{width}x{height} picture or is under {MinimumDetectedSide} px once clipped.");
                    continue;
                }
                var checkedRegion = candidate with { Region = clipped.Value };
                (candidate.Confidence >= detection.MinimumConfidence ? look.Hidden : look.Review).Add(checkedRegion);
            }
        }
        finally
        {
            if (frameFile.Length > 0 && File.Exists(frameFile)) File.Delete(frameFile);
        }
        return look;
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
