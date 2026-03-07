using FFMpegCore;
using Microsoft.Extensions.Options;
using Pulsa;

namespace PulsaAudioConvert.Workers;

public class ConvertWorker(
    FileQueue queue,
    IOptions<ConvertOptions> options,
    ILogger<ConvertWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        logger.LogInformation("Convert worker started. {Pattern} → {Ext} (codec: {Codec}, bitrate: {Bitrate}k, deleteSource: {Delete})",
            opts.FilePattern, opts.OutputExtension, opts.AudioCodec, opts.AudioBitrate, opts.DeleteSource);

        await foreach (var filePath in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ConvertAsync(filePath, opts, stoppingToken);
            }
            finally
            {
                queue.Complete(filePath);
            }
        }
    }

    private async Task ConvertAsync(string filePath, ConvertOptions opts, CancellationToken ct)
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
            if (opts.DeleteSource) TryDeleteSource(filePath);
            return;
        }

        if (!await FileHelper.WaitUntilReadyAsync(filePath, opts.FileReadyRetries, opts.FileReadyRetryDelayMs, logger, ct))
            return;

        var tempPath = outputPath + ".tmp";
        logger.LogInformation("Converting: {Source} → {Dest}", filePath, outputPath);
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
                logger.LogInformation("Done: {Path}", outputPath);
                if (opts.DeleteSource) TryDeleteSource(filePath);
            }
            else
            {
                logger.LogError("FFMpeg returned failure: {Path}", filePath);
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
            logger.LogError(ex, "Conversion failed: {Path}", filePath);
            FileHelper.TryDelete(tempPath);
        }
    }

    private void TryDeleteSource(string path)
    {
        try
        {
            File.Delete(path);
            logger.LogInformation("Deleted source: {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete source: {Path}", path);
        }
    }
}
