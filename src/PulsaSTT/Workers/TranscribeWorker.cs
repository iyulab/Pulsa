using LMSupply.Transcriber;
using Pulsa;

namespace PulsaSTT.Workers;

public class TranscribeWorker(
    FileQueue queue,
    IReadOnlyList<SttTaskOptions> tasks,
    ILogger<TranscribeWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Factory.StartNew(
            () => RunAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // Pre-load all unique models
        var modelNames = tasks.Select(t => t.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var models = new Dictionary<string, ITranscriberModel>(StringComparer.OrdinalIgnoreCase);

        var progress = new Progress<LMSupply.DownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
                logger.LogInformation("Downloading {File}: {Pct:F1}% ({Downloaded:F1}/{Total:F1} MB)",
                    p.FileName, p.PercentComplete, p.BytesDownloaded / 1_048_576.0, p.TotalBytes / 1_048_576.0);
            else
                logger.LogInformation("Downloading {File}: {Downloaded:F1} MB", p.FileName, p.BytesDownloaded / 1_048_576.0);
        });

        foreach (var modelName in modelNames)
        {
            logger.LogInformation("Loading STT model: {Model}...", modelName);
            var transcriber = await LocalTranscriber.LoadAsync(modelName, progress: progress, cancellationToken: stoppingToken);
            models[modelName] = transcriber;
            logger.LogInformation("STT model loaded: {Model}. GPU: {Gpu} [{Providers}]",
                modelName, transcriber.IsGpuActive, string.Join(", ", transcriber.ActiveProviders));
        }

        logger.LogInformation("STT worker started with {Count} task(s):", tasks.Count);
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            logger.LogInformation("  [{Index}] {Name}: {Path} ({Pattern}, model: {Model}, lang: {Lang})",
                i, t.Name ?? $"Task#{i}", t.WatchPath, t.FilePattern, t.Model,
                string.IsNullOrEmpty(t.Language) ? "auto" : t.Language);
        }

        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var opts = tasks[item.TaskIndex];
                var label = opts.Name ?? $"Task#{item.TaskIndex}";
                var model = models[opts.Model];
                try
                {
                    await TranscribeAsync(model, item.FilePath, opts, label, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "[{Label}] Transcription failed: {Path}", label, item.FilePath);
                }
                finally
                {
                    queue.Complete(item.FilePath, item.TaskIndex);
                }
            }
        }
        finally
        {
            foreach (var model in models.Values)
            {
                if (model is IAsyncDisposable ad) await ad.DisposeAsync();
                else if (model is IDisposable d) d.Dispose();
            }
        }
    }

    private async Task TranscribeAsync(
        ITranscriberModel transcriber, string filePath, SttTaskOptions opts, string label, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            logger.LogWarning("[{Label}] File not found, skipping: {Path}", label, filePath);
            return;
        }

        var formats = opts.OutputFormats;

        if (formats.All(f => File.Exists(opts.ResolveOutputPath(filePath, f))))
        {
            logger.LogDebug("[{Label}] All outputs already exist, skipping: {Path}", label, filePath);
            return;
        }

        if (!await FileHelper.WaitUntilReadyAsync(filePath, opts.FileReadyRetries, opts.FileReadyRetryDelayMs, logger, ct))
            return;

        logger.LogInformation("[{Label}] Transcribing: {Path}", label, filePath);
        try
        {
            var transcribeOptions = new TranscribeOptions
            {
                NoSpeechThreshold = opts.NoSpeechThreshold,
                WordTimestamps = true,
            };
            if (!string.IsNullOrWhiteSpace(opts.Language))
                transcribeOptions.Language = opts.Language;

            var result = await transcriber.TranscribeAsync(filePath, transcribeOptions, ct);

            if (string.IsNullOrWhiteSpace(result.Text))
                logger.LogWarning("[{Label}] Empty transcription: {Path} (segments: {Count})", label, filePath, result.Segments.Count);

            foreach (var format in formats)
            {
                var outputPath = opts.ResolveOutputPath(filePath, format);
                if (File.Exists(outputPath))
                    continue;

                var tempPath = outputPath + ".tmp";
                try
                {
                    var content = SubtitleFormatter.Format(result, format);
                    await File.WriteAllTextAsync(tempPath, content, ct);
                    File.Move(tempPath, outputPath, overwrite: false);
                    logger.LogInformation("[{Label}] Saved: {Path}", label, outputPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "[{Label}] Failed to write: {Path}", label, outputPath);
                    FileHelper.TryDelete(tempPath);
                }
            }

            logger.LogInformation("[{Label}] Done: {File} ({Duration:F1}s audio, RTF {Rtf:F1}x, segments: {Count}, formats: {Formats})",
                label, Path.GetFileName(filePath), result.DurationSeconds, result.RealTimeFactor,
                result.Segments.Count, string.Join(",", formats));
        }
        catch (OperationCanceledException)
        {
            foreach (var format in formats)
                FileHelper.TryDelete(opts.ResolveOutputPath(filePath, format) + ".tmp");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Label}] Transcription failed: {Path}", label, filePath);
            foreach (var format in formats)
                FileHelper.TryDelete(opts.ResolveOutputPath(filePath, format) + ".tmp");
        }
    }
}
