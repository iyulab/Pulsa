using FFMpegCore;
using Pulsa;

namespace PulsaAudioConvert.Workers;

public class ConvertWorker(
    FileQueue queue,
    IReadOnlyList<ConvertTaskOptions> tasks,
    ILogger<ConvertWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Convert worker started with {Count} task(s):", tasks.Count);
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            logger.LogInformation("  [{Index}] {Name}: {Path} ({Pattern} → {Ext}, codec: {Codec}, bitrate: {Bitrate}k)",
                i, t.Name ?? $"Task#{i}", t.WatchPath, t.FilePattern, t.OutputExtension, t.AudioCodec, t.AudioBitrate);
        }

        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var opts = tasks[item.TaskIndex];
            var label = opts.Name ?? $"Task#{item.TaskIndex}";
            try
            {
                await ConvertAsync(item.FilePath, opts, label, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[{Label}] Conversion failed: {Path}", label, item.FilePath);
            }
            finally
            {
                queue.Complete(item.FilePath, item.TaskIndex);
            }
        }
    }

    private async Task ConvertAsync(string filePath, ConvertTaskOptions opts, string label, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            logger.LogWarning("[{Label}] File not found, skipping: {Path}", label, filePath);
            return;
        }

        var outputPath = opts.ResolveOutputPath(filePath);
        if (File.Exists(outputPath))
        {
            logger.LogDebug("[{Label}] Output already exists, skipping: {Path}", label, outputPath);
            if (opts.DeleteSource) TryDeleteSource(filePath, label);
            return;
        }

        if (!await FileHelper.WaitUntilReadyAsync(filePath, opts.FileReadyRetries, opts.FileReadyRetryDelayMs, logger, ct))
            return;

        var dir = Path.GetDirectoryName(outputPath)!;
        var tempPath = Path.Combine(dir, $".~tmp_{Path.GetFileName(outputPath)}");
        logger.LogInformation("[{Label}] Converting: {Source} → {Dest}", label, filePath, outputPath);
        try
        {
            var success = await FFMpegArguments
                .FromFileInput(filePath)
                .OutputToFile(tempPath, overwrite: true, o => o
                    .WithAudioCodec(opts.AudioCodec)
                    .WithAudioBitrate(opts.AudioBitrate))
                .ProcessAsynchronously();

            if (success)
            {
                File.Move(tempPath, outputPath, overwrite: false);
                logger.LogInformation("[{Label}] Done: {Path}", label, outputPath);
                if (opts.DeleteSource) TryDeleteSource(filePath, label);
            }
            else
            {
                logger.LogError("[{Label}] FFMpeg returned failure: {Path}", label, filePath);
                FileHelper.TryDelete(tempPath);
            }
        }
        catch (OperationCanceledException)
        {
            FileHelper.TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Label}] Conversion failed: {Path}", label, filePath);
            FileHelper.TryDelete(tempPath);
        }
    }

    private void TryDeleteSource(string path, string label)
    {
        try
        {
            File.Delete(path);
            logger.LogInformation("[{Label}] Deleted source: {Path}", label, path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Label}] Failed to delete source: {Path}", label, path);
        }
    }
}
