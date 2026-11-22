using FFMpegCore;

namespace PulsaRedact;

/// <summary>
/// Pixelates a fixed rectangular region of an image or video via a single ffmpeg pass. Image vs.
/// video is decided by file extension (matches PulsaVideoCompose's FfmpegVideoComposer, which makes
/// the same assumption throughout — no content-sniffing). No motion tracking: the region is one fixed
/// rectangle for the whole applicable time range, by design (see the plan's Global Constraints).
/// </summary>
public sealed class MediaRedactor
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    private readonly FFOptions _ffOptions;

    public MediaRedactor(string ffmpegBinaryFolder)
    {
        _ffOptions = new FFOptions { BinaryFolder = ffmpegBinaryFolder };
    }

    public async Task<RedactResult> RedactAsync(RedactRequest request, CancellationToken cancellationToken = default)
    {
        if (File.Exists(request.OutputPath))
            return new RedactResult(false, null, $"{request.OutputPath} already exists");
        if (!File.Exists(request.InputPath))
            return new RedactResult(false, null, $"{request.InputPath} does not exist");

        var isImage = ImageExtensions.Contains(Path.GetExtension(request.InputPath));

        try
        {
            // Images never carry a time range — StartTime/EndTime are accepted but ignored, not
            // rejected, matching the plan's "ignored entirely for a still-image input" contract.
            // Build is called INSIDE the try (not before it) so its own ArgumentException (e.g. a
            // video request supplying only one of StartTime/EndTime) is caught below and converted
            // into a failure RedactResult, like every other failure path on this method — instead
            // of escaping as a faulted Task. Matches FfmpegVideoComposer.ComposeAsync's contract
            // for this exact class of exception.
            var filter = PixelateFilterBuilder.Build(
                request.X, request.Y, request.Width, request.Height,
                isImage ? null : request.StartTime, isImage ? null : request.EndTime);

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
                        // -update 1 is the documented-stable form for single-image output; without
                        // it, current ffmpeg builds emit a deprecation warning ("Use -update option
                        // ... to write a single image") though it still works.
                        opt.WithCustomArgument("-update 1");
                    }
                });

            await args.CancellableThrough(cancellationToken).ProcessAsynchronously(ffMpegOptions: _ffOptions);
            return new RedactResult(true, request.OutputPath, null);
        }
        catch (Exception ex)
        {
            return new RedactResult(false, null, ex.Message);
        }
    }
}
