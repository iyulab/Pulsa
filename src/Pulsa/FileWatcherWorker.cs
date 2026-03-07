using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pulsa;

public class FileWatcherWorker<TOptions>(
    FileQueue queue,
    IOptions<TOptions> options,
    ILogger<FileWatcherWorker<TOptions>> logger) : BackgroundService
    where TOptions : class, IPulsaOptions
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var watchPath = Path.GetFullPath(opts.WatchPath);

        Directory.CreateDirectory(watchPath);

        CleanStaleTempFiles(watchPath, opts);
        ScanAndEnqueue(watchPath, opts);

        using var watcher = new FileSystemWatcher(watchPath, opts.FilePattern)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };

        watcher.Created += (_, e) => OnFileDetected(e.FullPath, opts);
        watcher.Renamed += (_, e) =>
        {
            if (opts.MatchesPattern(e.FullPath))
                OnFileDetected(e.FullPath, opts);
        };
        watcher.Error += (_, e) =>
        {
            logger.LogWarning(e.GetException(), "FileSystemWatcher error, triggering rescan");
            ScanAndEnqueue(watchPath, opts);
        };

        using var outputWatcher = new FileSystemWatcher(watchPath, opts.OutputWatchPattern)
        {
            NotifyFilter = NotifyFilters.FileName,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };

        outputWatcher.Deleted += (_, e) =>
        {
            logger.LogInformation("Output removed, re-scanning: {File}", e.FullPath);
            ScanAndEnqueue(watchPath, opts);
        };
        outputWatcher.Renamed += (_, e) =>
        {
            logger.LogInformation("Output renamed, re-scanning: {OldFile} → {NewFile}", e.OldFullPath, e.FullPath);
            ScanAndEnqueue(watchPath, opts);
        };
        outputWatcher.Error += (_, e) =>
        {
            logger.LogWarning(e.GetException(), "Output watcher error, triggering rescan");
            ScanAndEnqueue(watchPath, opts);
        };

        logger.LogInformation("Watching {Path} for {Pattern} (output: {OutputPattern})",
            watchPath, opts.FilePattern, opts.OutputWatchPattern);

        if (opts.RescanIntervalSeconds > 0)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(opts.RescanIntervalSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                ScanAndEnqueue(watchPath, opts);
            }
        }
        else
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
    }

    private void ScanAndEnqueue(string watchPath, TOptions opts)
    {
        try
        {
            foreach (var file in Directory.GetFiles(watchPath, opts.FilePattern))
            {
                var outputPath = opts.ResolveOutputPath(file);
                if (!File.Exists(outputPath) && !queue.Contains(file))
                {
                    logger.LogInformation("Queuing: {File}", file);
                    queue.Enqueue(file);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scan failed for: {Path}", watchPath);
        }
    }

    private void CleanStaleTempFiles(string watchPath, TOptions opts)
    {
        try
        {
            foreach (var tmp in Directory.GetFiles(watchPath, opts.OutputWatchPattern + ".tmp"))
            {
                logger.LogInformation("Removing stale temp file: {File}", tmp);
                File.Delete(tmp);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean temp files in: {Path}", watchPath);
        }
    }

    private void OnFileDetected(string filePath, TOptions opts)
    {
        if (!File.Exists(opts.ResolveOutputPath(filePath)) && !queue.Contains(filePath))
        {
            logger.LogInformation("New file detected: {File}", filePath);
            queue.Enqueue(filePath);
        }
    }
}
