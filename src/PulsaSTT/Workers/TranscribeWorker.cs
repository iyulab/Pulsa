using LMSupply.Transcriber;
using Microsoft.Extensions.Options;
using Pulsa;

namespace PulsaSTT.Workers;

public class TranscribeWorker(
    FileQueue queue,
    IOptions<SttOptions> options,
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
        var opts = options.Value;
        logger.LogInformation("Loading STT model: {Model}...", opts.Model);
        var progress = new Progress<LMSupply.DownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
                logger.LogInformation("Downloading {File}: {Pct:F1}% ({Downloaded:F1}/{Total:F1} MB)",
                    p.FileName, p.PercentComplete, p.BytesDownloaded / 1_048_576.0, p.TotalBytes / 1_048_576.0);
            else
                logger.LogInformation("Downloading {File}: {Downloaded:F1} MB", p.FileName, p.BytesDownloaded / 1_048_576.0);
        });
        await using var transcriber = await LocalTranscriber.LoadAsync(opts.Model, progress: progress, cancellationToken: stoppingToken);
        logger.LogInformation("STT model loaded. GPU: {Gpu} [{Providers}]",
            transcriber.IsGpuActive, string.Join(", ", transcriber.ActiveProviders));

        await foreach (var filePath in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await TranscribeAsync(transcriber, filePath, opts, stoppingToken);
            }
            finally
            {
                queue.Complete(filePath);
            }
        }
    }

    private async Task TranscribeAsync(ITranscriberModel transcriber, string filePath, SttOptions opts, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            logger.LogWarning("File not found, skipping: {Path}", filePath);
            return;
        }

        var outputPath = opts.ResolveOutputPath(filePath);
        if (File.Exists(outputPath))
        {
            logger.LogDebug("Output already exists, skipping: {Path}", outputPath);
            return;
        }

        if (!await FileHelper.WaitUntilReadyAsync(filePath, opts.FileReadyRetries, opts.FileReadyRetryDelayMs, logger, ct))
            return;

        var tempPath = outputPath + ".tmp";
        logger.LogInformation("Transcribing: {Path}", filePath);
        try
        {
            var transcribeOptions = new TranscribeOptions
            {
                NoSpeechThreshold = opts.NoSpeechThreshold,
            };
            if (!string.IsNullOrWhiteSpace(opts.Language))
                transcribeOptions.Language = opts.Language;

            var result = await transcriber.TranscribeAsync(filePath, transcribeOptions, ct);

            if (string.IsNullOrWhiteSpace(result.Text))
                logger.LogWarning("Empty transcription: {Path} (segments: {Count})", filePath, result.Segments.Count);

            await File.WriteAllTextAsync(tempPath, result.Text, ct);
            File.Move(tempPath, outputPath, overwrite: false);

            logger.LogInformation("Done: {Path} ({Duration:F1}s audio, RTF {Rtf:F1}x, segments: {Count})",
                outputPath, result.DurationSeconds, result.RealTimeFactor, result.Segments.Count);
        }
        catch (OperationCanceledException)
        {
            FileHelper.TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription failed: {Path}", filePath);
            FileHelper.TryDelete(tempPath);
        }
    }
}
