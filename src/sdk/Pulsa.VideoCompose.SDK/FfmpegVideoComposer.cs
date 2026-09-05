using System.Text;
using FFMpegCore;

namespace PulsaVideoCompose;

/// <summary>
/// Composes N images + captions into one captioned, Ken-Burns-animated video via a 4-stage ffmpeg
/// pipeline: per-image Ken-Burns clip -> concat-demuxer join -> subtitle burn-in. Every stage is a
/// single-input FFMpegCore call — no multi-input filter_complex graph. An optional title/outro card
/// (any caller-supplied image, large centered text, no Ken-Burns motion) is rendered the same way as
/// a body clip and prepended/appended before concatenation — its own caption is burned directly onto
/// its own clip rather than folded into the whole-video subtitle burn, since ffmpeg's <c>subtitles</c>
/// filter does not support per-instance time-gating (<c>enable=</c> is rejected for that filter,
/// confirmed empirically) that a single shared burn pass would otherwise need.
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
        if ((request.TitleImagePath is null) != (request.TitleText is null))
            return new ComposeVideoResult(false, null, null, "titleImagePath and titleText must both be supplied together, or neither.");
        if ((request.OutroImagePath is null) != (request.OutroText is null))
            return new ComposeVideoResult(false, null, null, "outroImagePath and outroText must both be supplied together, or neither.");

        VideoComposeOptions effectiveOptions;
        try
        {
            effectiveOptions = AspectRatioPresets.Resolve(request.AspectRatio, _options);
        }
        catch (ArgumentException ex)
        {
            return new ComposeVideoResult(false, null, null, ex.Message);
        }

        var workDir = Directory.CreateTempSubdirectory("pulsa-videocompose-");
        try
        {
            var effectiveSceneDurationSeconds =
                ZoompanFilterBuilder.ComputeEffectiveSceneDuration(request.SceneDurationSeconds, effectiveOptions);
            var titleDurationSeconds = request.TitleImagePath is not null ? effectiveSceneDurationSeconds : 0;
            var outroDurationSeconds = request.OutroImagePath is not null ? effectiveSceneDurationSeconds : 0;

            var clipPaths = new List<string>();
            if (request.TitleImagePath is not null)
                clipPaths.Add(await RenderTitleOrOutroClipAsync(
                    request.TitleImagePath, request.TitleText!, request.SceneDurationSeconds,
                    effectiveOptions, workDir.FullName, "title", cancellationToken));
            clipPaths.AddRange(await RenderClipsAsync(request, effectiveOptions, workDir.FullName, cancellationToken));
            if (request.OutroImagePath is not null)
                clipPaths.Add(await RenderTitleOrOutroClipAsync(
                    request.OutroImagePath, request.OutroText!, request.SceneDurationSeconds,
                    effectiveOptions, workDir.FullName, "outro", cancellationToken));

            var concatenatedPath = await ConcatClipsAsync(clipPaths, workDir.FullName, cancellationToken);

            // Body captions burn over the WHOLE concatenated video (title+body+outro), so their
            // timestamps must be offset by the title card's duration or they'd land during the
            // title card instead of the body — a temp file, not the returned sidecar (below).
            var bodyBurnSrtPath = Path.Combine(workDir.FullName, "body-burn.srt");
            await File.WriteAllTextAsync(
                bodyBurnSrtPath,
                SrtGenerator.Generate(request.Captions, effectiveSceneDurationSeconds, startOffsetSeconds: titleDurationSeconds),
                cancellationToken);
            await BurnSubtitlesAsync(concatenatedPath, bodyBurnSrtPath, request.OutputPath, cancellationToken);

            // The returned .srt sidecar is the full transcript (title/outro included) even though
            // the title/outro text is already burned directly into those clips' own pixels above —
            // consistent with body captions, which are likewise both burned in AND present here.
            var srtPath = await WriteSubtitlesAsync(
                request, effectiveSceneDurationSeconds, titleDurationSeconds, outroDurationSeconds, cancellationToken);

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

    private async Task<string> RenderTitleOrOutroClipAsync(
        string imagePath, string text, double sceneDurationSeconds, VideoComposeOptions effectiveOptions,
        string workDirPath, string clipNamePrefix, CancellationToken cancellationToken)
    {
        var effectiveDuration = ZoompanFilterBuilder.ComputeEffectiveSceneDuration(sceneDurationSeconds, effectiveOptions);
        var srtPath = Path.Combine(workDirPath, $"{clipNamePrefix}.srt");
        await File.WriteAllTextAsync(srtPath, SrtGenerator.Generate([text], effectiveDuration), cancellationToken);

        var vf = string.Join(",",
            ZoompanFilterBuilder.BuildStaticHold(sceneDurationSeconds, effectiveOptions),
            CaptionStyleFilterBuilder.BuildTitleStyle(srtPath, effectiveOptions.Height));
        var frameCount = ZoompanFilterBuilder.ComputeFrameCount(sceneDurationSeconds, effectiveOptions);
        var clipPath = Path.Combine(workDirPath, $"{clipNamePrefix}.mp4");

        await FFMpegArguments
            .FromFileInput(imagePath, verifyExists: true, opt => opt
                .WithCustomArgument("-loop 1"))
            .OutputToFile(clipPath, overwrite: true, opt => opt
                .WithCustomArgument($"-vf \"{vf}\"")
                .WithFramerate(effectiveOptions.Fps)
                .WithVideoCodec("libx264")
                .WithCustomArgument($"-frames:v {frameCount}"))
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously(ffMpegOptions: _ffOptions);

        return clipPath;
    }

    private async Task<List<string>> RenderClipsAsync(
        ComposeVideoRequest request, VideoComposeOptions effectiveOptions, string workDirPath, CancellationToken cancellationToken)
    {
        var frameCount = ZoompanFilterBuilder.ComputeFrameCount(request.SceneDurationSeconds, effectiveOptions);
        var clipPaths = new List<string>();
        for (var i = 0; i < request.ImagePaths.Count; i++)
        {
            // Built per scene, not hoisted out of the loop: ZoompanFilterBuilder picks its Ken-Burns
            // motion (zoom direction + pan target) from the scene index, so every clip gets its own
            // internally-selected, deterministic motion instead of every clip sharing one filter string.
            var vf = ZoompanFilterBuilder.Build(i, request.SceneDurationSeconds, effectiveOptions);
            var clipPath = Path.Combine(workDirPath, $"clip-{i:D4}.mp4");
            await FFMpegArguments
                .FromFileInput(request.ImagePaths[i], verifyExists: true, opt => opt
                    .WithCustomArgument("-loop 1"))
                .OutputToFile(clipPath, overwrite: true, opt => opt
                    .WithCustomArgument($"-vf \"{vf}\"")
                    .WithFramerate(effectiveOptions.Fps)
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

    /// <summary>
    /// Writes the returned .srt sidecar — the full transcript (title/outro cues included, when
    /// present) even though title/outro text is separately burned directly into those clips' own
    /// pixels by <see cref="RenderTitleOrOutroClipAsync"/>, not via this file. Scene duration is
    /// frame-quantized by <see cref="RenderClipsAsync"/>'s <c>-frames:v</c> cap — the actual rendered
    /// clip length is rarely exactly the requested duration (e.g. 2.5s @ 25fps -> 63 frames -> 2.52s
    /// actual) — <paramref name="effectiveSceneDurationSeconds"/> must already reflect that, or
    /// captions drift out of sync with picture, and the drift compounds scene over scene.
    /// </summary>
    private async Task<string> WriteSubtitlesAsync(
        ComposeVideoRequest request, double effectiveSceneDurationSeconds,
        double titleDurationSeconds, double outroDurationSeconds, CancellationToken cancellationToken)
    {
        var srtPath = Path.ChangeExtension(request.OutputPath, ".srt");
        var bodyTotalSeconds = effectiveSceneDurationSeconds * request.Captions.Count;

        var sb = new StringBuilder();
        var nextIndex = 1;
        if (titleDurationSeconds > 0)
        {
            sb.Append(SrtGenerator.Generate([request.TitleText!], titleDurationSeconds));
            nextIndex++;
        }
        sb.Append(SrtGenerator.Generate(request.Captions, effectiveSceneDurationSeconds, startOffsetSeconds: titleDurationSeconds, startIndex: nextIndex));
        nextIndex += request.Captions.Count;
        if (outroDurationSeconds > 0)
        {
            sb.Append(SrtGenerator.Generate(
                [request.OutroText!], outroDurationSeconds,
                startOffsetSeconds: titleDurationSeconds + bodyTotalSeconds, startIndex: nextIndex));
        }

        await File.WriteAllTextAsync(srtPath, sb.ToString(), cancellationToken);
        return srtPath;
    }

    private async Task BurnSubtitlesAsync(
        string concatenatedPath, string srtPath, string outputPath, CancellationToken cancellationToken)
    {
        await FFMpegArguments
            .FromFileInput(concatenatedPath)
            .OutputToFile(outputPath, overwrite: false, opt => opt
                .WithCustomArgument($"-vf \"{CaptionStyleFilterBuilder.Build(srtPath)}\""))
            .CancellableThrough(cancellationToken)
            .ProcessAsynchronously(ffMpegOptions: _ffOptions);
    }
}
