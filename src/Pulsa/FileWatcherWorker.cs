using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pulsa;

public class FileWatcherWorker(
    FileQueue queue,
    IReadOnlyList<IPulsaOptions> tasks,
    ILogger<FileWatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (tasks.Count == 0)
        {
            logger.LogWarning("No tasks configured — file watcher idle");
            return;
        }

        var watchers = new List<FileSystemWatcher>();

        try
        {
            for (var i = 0; i < tasks.Count; i++)
            {
                var taskIndex = i;
                var opts = tasks[i];
                var watchPath = Path.GetFullPath(opts.WatchPath);
                Directory.CreateDirectory(watchPath);

                CleanStaleTempFiles(watchPath, opts);
                ScanAndEnqueue(watchPath, opts, taskIndex);

                var inputWatcher = new FileSystemWatcher(watchPath, opts.FilePattern)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };

                inputWatcher.Created += (_, e) => OnFileDetected(e.FullPath, opts, taskIndex);
                inputWatcher.Renamed += (_, e) =>
                {
                    if (opts.MatchesPattern(e.FullPath))
                        OnFileDetected(e.FullPath, opts, taskIndex);
                };
                inputWatcher.Error += (_, e) =>
                {
                    logger.LogWarning(e.GetException(),
                        "[Task#{Index} {Name}] FileSystemWatcher error, triggering rescan",
                        taskIndex, opts.Name ?? taskIndex.ToString());
                    ScanAndEnqueue(watchPath, opts, taskIndex);
                };
                watchers.Add(inputWatcher);

                var outputWatcher = new FileSystemWatcher(watchPath, opts.OutputWatchPattern)
                {
                    NotifyFilter = NotifyFilters.FileName,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };

                outputWatcher.Deleted += (_, e) =>
                {
                    logger.LogInformation(
                        "[Task#{Index} {Name}] Output removed, re-scanning: {File}",
                        taskIndex, opts.Name ?? taskIndex.ToString(), e.FullPath);
                    ScanAndEnqueue(watchPath, opts, taskIndex);
                };
                outputWatcher.Renamed += (_, e) =>
                {
                    logger.LogInformation(
                        "[Task#{Index} {Name}] Output renamed, re-scanning: {OldFile} → {NewFile}",
                        taskIndex, opts.Name ?? taskIndex.ToString(), e.OldFullPath, e.FullPath);
                    ScanAndEnqueue(watchPath, opts, taskIndex);
                };
                outputWatcher.Error += (_, e) =>
                {
                    logger.LogWarning(e.GetException(),
                        "[Task#{Index} {Name}] Output watcher error, triggering rescan",
                        taskIndex, opts.Name ?? taskIndex.ToString());
                    ScanAndEnqueue(watchPath, opts, taskIndex);
                };
                watchers.Add(outputWatcher);

                var label = opts.Name ?? $"Task#{taskIndex}";
                logger.LogInformation(
                    "[{Label}] Watching {Path} for {Pattern} (output: {OutputPattern})",
                    label, watchPath, opts.FilePattern, opts.OutputWatchPattern);
            }

            var minInterval = tasks
                .Where(t => t.RescanIntervalSeconds > 0)
                .Select(t => t.RescanIntervalSeconds)
                .DefaultIfEmpty(0)
                .Min();

            if (minInterval > 0)
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(minInterval));
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    for (var i = 0; i < tasks.Count; i++)
                    {
                        var opts = tasks[i];
                        var watchPath = Path.GetFullPath(opts.WatchPath);
                        ScanAndEnqueue(watchPath, opts, i);
                    }
                }
            }
            else
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var w in watchers) w.Dispose();
        }
    }

    private void ScanAndEnqueue(string watchPath, IPulsaOptions opts, int taskIndex)
    {
        try
        {
            foreach (var file in Directory.GetFiles(watchPath, opts.FilePattern))
            {
                var outputPath = opts.ResolveOutputPath(file);
                if (!File.Exists(outputPath) && !queue.Contains(file, taskIndex))
                {
                    logger.LogInformation("[Task#{Index} {Name}] Queuing: {File}",
                        taskIndex, opts.Name ?? taskIndex.ToString(), file);
                    queue.Enqueue(file, taskIndex);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Task#{Index} {Name}] Scan failed for: {Path}",
                taskIndex, opts.Name ?? taskIndex.ToString(), watchPath);
        }
    }

    private void CleanStaleTempFiles(string watchPath, IPulsaOptions opts)
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

    private void OnFileDetected(string filePath, IPulsaOptions opts, int taskIndex)
    {
        if (!File.Exists(opts.ResolveOutputPath(filePath)) && !queue.Contains(filePath, taskIndex))
        {
            logger.LogInformation("[Task#{Index} {Name}] New file detected: {File}",
                taskIndex, opts.Name ?? taskIndex.ToString(), filePath);
            queue.Enqueue(filePath, taskIndex);
        }
    }
}
