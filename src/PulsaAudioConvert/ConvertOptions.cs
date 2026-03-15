using Pulsa;

namespace PulsaAudioConvert;

public class ConvertTaskOptions : IPulsaOptions
{
    public string? Name { get; set; }
    public string WatchPath { get; set; } = ".";
    public string FilePattern { get; set; } = "*.m4a";
    public string OutputExtension { get; set; } = ".mp3";
    public string AudioCodec { get; set; } = "libmp3lame";
    public int AudioBitrate { get; set; } = 192;
    public bool DeleteSource { get; set; } = false;
    public int FileReadyRetries { get; set; } = 10;
    public int FileReadyRetryDelayMs { get; set; } = 500;
    public int RescanIntervalSeconds { get; set; } = 60;

    public string OutputWatchPattern =>
        $"*{(OutputExtension.StartsWith('.') ? OutputExtension : $".{OutputExtension}")}";

    public string ResolveOutputPath(string filePath)
    {
        var ext = OutputExtension.StartsWith('.') ? OutputExtension : $".{OutputExtension}";
        return Path.ChangeExtension(filePath, ext);
    }

    public bool MatchesPattern(string filePath)
    {
        var patternExt = Path.GetExtension(FilePattern);
        return filePath.EndsWith(patternExt, StringComparison.OrdinalIgnoreCase);
    }
}
