using FFMpegCore;

namespace PulsaVideoCompose;

/// <summary>
/// Composes N images + captions into one captioned, Ken-Burns-animated video via a 4-stage ffmpeg
/// pipeline: per-image Ken-Burns clip -> concat-demuxer join -> subtitle burn-in. Every stage is a
/// single-input FFMpegCore call — no multi-input filter_complex graph.
/// </summary>
public sealed class FfmpegVideoComposer
{
    private readonly VideoComposeOptions _options;
    private readonly FFOptions _ffOptions;

    public FfmpegVideoComposer(string ffmpegBinaryFolder, VideoComposeOptions? options = null)
    {
        _options = options ?? new VideoComposeOptions();
        // Per-instance, not GlobalFFOptions.Configure: that mutates process-global state shared by
        // every FFMpegCore consumer in the process, so constructing a second FfmpegVideoComposer
        // with a different binary folder would silently repoint the first one. Each stage method
        // threads _ffOptions through ProcessAsynchronously's ffMpegOptions parameter instead.
        _ffOptions = new FFOptions { BinaryFolder = ffmpegBinaryFolder };
    }

    public async Task<ComposeVideoResult> ComposeAsync(
        ComposeVideoRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ImagePaths.Count < 2)
            return new ComposeVideoResult(false, null, null, "At least 2 images are required.");
        if (request.ImagePaths.Count != request.Captions.Count)
            return new ComposeVideoResult(false, null, null, "captions must have exactly one entry per image.");
        if (File.Exists(request.OutputPath))
            return new ComposeVideoResult(false, null, null, $"{request.OutputPath} already exists");

        var workDir = Directory.CreateTempSubdirectory("pulsa-videocompose-");
        try
        {
            var clipPaths = await RenderClipsAsync(request, workDir.FullName, cancellationToken);
            var concatenatedPath = await ConcatClipsAsync(clipPaths, workDir.FullName, cancellationToken);
            var srtPath = await WriteSubtitlesAsync(request, cancellationToken);
            await BurnSubtitlesAsync(concatenatedPath, srtPath, request.OutputPath, cancellationToken);

            return new ComposeVideoResult(true, request.OutputPath, srtPath, null);
        }
        catch (Exception ex)
        {
            return new ComposeVideoResult(false, null, null, ex.Message);
        }
        finally
        {
            try { workDir.Delete(recursive: true); }
            catch (IOException) { /* best-effort temp cleanup — a locked handle here is not this call's failure */ }
        }
    }

    private async Task<List<string>> RenderClipsAsync(
        ComposeVideoRequest request, string workDirPath, CancellationToken cancellationToken)
    {
        var vf = ZoompanFilterBuilder.Build(request.SceneDurationSeconds, _options);
        var frameCount = ZoompanFilterBuilder.ComputeFrameCount(request.SceneDurationSeconds, _options);
        var clipPaths = new List<string>();
        for (var i = 0; i < request.ImagePaths.Count; i++)
        {
            var clipPath = Path.Combine(workDirPath, $"clip-{i:D4}.mp4");
            await FFMpegArguments
                .FromFileInput(request.ImagePaths[i], verifyExists: true, opt => opt
                    .WithCustomArgument("-loop 1"))
                .OutputToFile(clipPath, overwrite: true, opt => opt
                    .WithCustomArgument($"-vf \"{vf}\"")
                    .WithFramerate(_options.Fps)
                    .WithVideoCodec("libx264")
                    // Cap the OUTPUT frame count, not input duration (-t on a looped still image
                    // forces the demuxer to emit many distinct input frames within that window,
                    // and zoompan's `d` re-triggers a full d-frame zoom for each one — see
                    // ZoompanFilterBuilder.ComputeFrameCount's doc comment). This was a real bug,
                    // live-verified: it produced a 100s clip for a 2s request (50x too long)
                    // before this fix, confirmed 2.0s exactly after.
                    .WithCustomArgument($"-frames:v {frameCount}"))
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously(ffMpegOptions: _ffOptions);
            clipPaths.Add(clipPath);
        }
        return clipPaths;
    }

    private async Task<string> ConcatClipsAsync(
        List<string> clipPaths, string workDirPath, CancellationToken cancellationToken)
    {
        var listPath = Path.Combine(workDirPath, "concat.txt");
        await ConcatListWriter.WriteAsync(clipPaths, listPath, cancellationToken);
        var concatenatedPath = Path.Combine(workDirPath, "concatenated.mp4");
        await FFMpegArguments
            .FromFileInput(listPath, verifyExists: false, opt => opt
                .WithCustomArgument("-f concat -safe 0"))
            .OutputToFile(concatenatedPath, overwrite: true, opt => opt
                .WithCustomArgument("-c copy"))
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously(ffMpegOptions: _ffOptions);
        return concatenatedPath;
    }

    private async Task<string> WriteSubtitlesAsync(
        ComposeVideoRequest request, CancellationToken cancellationToken)
    {
        var srtPath = Path.ChangeExtension(request.OutputPath, ".srt");
        // Scene duration is frame-quantized by RenderClipsAsync's -frames:v cap — the actual
        // rendered clip length is rarely exactly request.SceneDurationSeconds (e.g. 2.5s @ 25fps ->
        // 63 frames -> 2.52s actual). Subtitles must be timed against that effective, quantized
        // duration, not the raw request value, or captions drift out of sync with picture, and the
        // drift compounds scene over scene.
        var effectiveSceneDurationSeconds =
            ZoompanFilterBuilder.ComputeEffectiveSceneDuration(request.SceneDurationSeconds, _options);
        var srtContent = SrtGenerator.Generate(request.Captions, effectiveSceneDurationSeconds);
        await File.WriteAllTextAsync(srtPath, srtContent, cancellationToken);
        return srtPath;
    }

    private async Task BurnSubtitlesAsync(
        string concatenatedPath, string srtPath, string outputPath, CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(concatenatedPath)
            .OutputToFile(outputPath, overwrite: false, opt => opt
                .WithCustomArgument($"-vf \"subtitles='{EscapeForFilterArgument(srtPath)}'\""))
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously(ffMpegOptions: _ffOptions);
    }

    // ffmpeg's filter-option parser treats ':' and '\' specially inside a filter's own option
    // string — a Windows path's drive-letter colon and every backslash must be escaped, or the
    // subtitles filter misreads the path as a run of filter options instead of a file path.
    private static string EscapeForFilterArgument(string path) =>
        path.Replace("\\", "\\\\").Replace(":", "\\:");
}
