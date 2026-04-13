using Microsoft.Extensions.Logging;

namespace Pulsa;

public static class FileHelper
{
    public static async Task<bool> WaitUntilReadyAsync(
        string path, int retries, int retryDelayMs,
        ILogger logger, CancellationToken ct)
    {
        for (var i = 0; i < retries; i++)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                logger.LogDebug("File not ready, retrying ({Attempt}/{Max}): {Path}", i + 1, retries, path);
                await Task.Delay(retryDelayMs, ct);
            }
        }
        logger.LogWarning("File still locked after {Max} attempts, skipping: {Path}", retries, path);
        return false;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
