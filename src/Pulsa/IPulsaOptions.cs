namespace Pulsa;

public interface IPulsaOptions
{
    string? Name { get; }
    string WatchPath { get; }
    string FilePattern { get; }
    string OutputWatchPattern { get; }
    int FileReadyRetries { get; }
    int FileReadyRetryDelayMs { get; }
    int RescanIntervalSeconds { get; }

    string ResolveOutputPath(string filePath);
    bool MatchesPattern(string filePath);
}
