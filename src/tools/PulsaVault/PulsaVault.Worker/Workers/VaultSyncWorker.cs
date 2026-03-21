using FluxIndex.Extensions.FileVault.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PulsaVault.Workers;

/// <summary>
/// Background service that registers watched folders from configuration
/// and performs an initial sync on startup.
/// After initialization, it stays alive while FileVault's own BackgroundService
/// handles ongoing file-system monitoring.
/// </summary>
public sealed partial class VaultSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<PulsaVaultOptions> _options;
    private readonly ILogger<VaultSyncWorker> _logger;

    public VaultSyncWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<PulsaVaultOptions> options,
        ILogger<VaultSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(_logger);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var vault = scope.ServiceProvider.GetRequiredService<IVault>();

            // 1. Register folders from configuration
            await RegisterFoldersAsync(vault, stoppingToken);

            // 2. Initial full sync — detects changes and queues memorize/refresh/remove jobs
            LogSyncStarting(_logger);
            var result = await vault.SyncAsync(stoppingToken);

            LogSyncCompleted(
                _logger,
                result.FoldersScanned,
                result.NewFilesDiscovered,
                result.TotalQueuedCount,
                result.Duration.TotalSeconds);

            if (result.ErrorCount > 0)
            {
                foreach (var error in result.Errors)
                {
                    LogSyncError(_logger, error.FilePath, error.ErrorMessage);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogCancelled(_logger);
            return;
        }
        catch (Exception ex)
        {
            LogFatalError(_logger, ex);
            throw;
        }

        LogRunning(_logger);

        // Stay alive — FileVault's BackgroundService handles file-system watching.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task RegisterFoldersAsync(IVault vault, CancellationToken ct)
    {
        var folders = _options.Value.Folders;
        if (folders.Count == 0)
        {
            LogNoFolders(_logger);
            return;
        }

        // Get already-registered folders to avoid duplicates on restart
        var existing = await vault.GetAllWatchedFoldersAsync(ct);
        var existingPaths = new HashSet<string>(
            existing.Select(f => f.Path),
            StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            var fullPath = Path.GetFullPath(folder.Path);

            if (!Directory.Exists(fullPath))
            {
                LogFolderNotFound(_logger, fullPath);
                continue;
            }

            if (existingPaths.Contains(fullPath))
            {
                LogFolderAlreadyRegistered(_logger, fullPath);
                continue;
            }

            var watched = await vault.AddWatchedFolderAsync(
                fullPath,
                isRecursive: folder.Recursive,
                includePatterns: folder.IncludePatterns.ToArray(),
                excludePatterns: folder.ExcludePatterns.ToArray(),
                ct: ct);

            LogFolderRegistered(_logger, watched.Path, watched.Id);
        }
    }

    #region LoggerMessage Definitions

    [LoggerMessage(Level = LogLevel.Information,
        Message = "VaultSyncWorker starting")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "VaultSyncWorker running — file-system monitoring is active")]
    private static partial void LogRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "VaultSyncWorker cancelled")]
    private static partial void LogCancelled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "No watched folders configured")]
    private static partial void LogNoFolders(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Folder does not exist, skipping: {Path}")]
    private static partial void LogFolderNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Folder already registered, skipping: {Path}")]
    private static partial void LogFolderAlreadyRegistered(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Registered watched folder: {Path} (ID: {FolderId})")]
    private static partial void LogFolderRegistered(ILogger logger, string path, Guid folderId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Starting initial sync")]
    private static partial void LogSyncStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Initial sync completed — scanned {FoldersScanned} folders, discovered {NewFiles} new files, queued {QueuedJobs} jobs in {DurationSec:F1}s")]
    private static partial void LogSyncCompleted(ILogger logger, int foldersScanned, int newFiles, int queuedJobs, double durationSec);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Sync error for {FilePath}: {ErrorMessage}")]
    private static partial void LogSyncError(ILogger logger, string filePath, string errorMessage);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "VaultSyncWorker failed with unhandled exception")]
    private static partial void LogFatalError(ILogger logger, Exception exception);

    #endregion
}
