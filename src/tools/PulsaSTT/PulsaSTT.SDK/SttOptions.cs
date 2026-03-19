using Pulsa;

namespace PulsaSTT;

public class SttTaskOptions : IPulsaOptions
{
    public string? Name { get; set; }
    public string WatchPath { get; set; } = ".";
    public string FilePattern { get; set; } = "*.mp3";
    public string OutputPattern { get; set; } = "{name}.stt.txt";
    public string OutputFormat { get; set; } = "text";
    public string Model { get; set; } = "large";
    public string Language { get; set; } = "";
    public float NoSpeechThreshold { get; set; } = 0.8f;
    public int FileReadyRetries { get; set; } = 20;
    public int FileReadyRetryDelayMs { get; set; } = 500;
    public int RescanIntervalSeconds { get; set; } = 60;

    private static readonly string[] AllFormats = ["text", "vtt", "srt"];

    public IReadOnlyList<string> OutputFormats => ParseFormats();

    public string OutputWatchPattern =>
        ResolvePatternForFormat(OutputFormats[0]).Replace("{name}", "*").Replace("{ext}", "*");

    public string ResolveOutputPath(string filePath)
        => ResolveOutputPath(filePath, OutputFormats[0]);

    public string ResolveOutputPath(string filePath, string format)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).TrimStart('.');
        var fileName = ResolvePatternForFormat(format)
            .Replace("{name}", name)
            .Replace("{ext}", ext);
        return Path.Combine(dir, fileName);
    }

    private static string ResolvePatternForFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "vtt" => "{name}.vtt",
            "srt" => "{name}.srt",
            _ => "{name}.stt.txt",
        };
    }

    private IReadOnlyList<string> ParseFormats()
    {
        var raw = OutputFormat.Trim().ToLowerInvariant();
        if (raw is "all" or "*")
            return AllFormats;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => AllFormats.Contains(f))
            .Distinct()
            .ToArray() is { Length: > 0 } result ? result : ["text"];
    }

    public bool MatchesPattern(string filePath)
    {
        var patternExt = Path.GetExtension(FilePattern);
        return filePath.EndsWith(patternExt, StringComparison.OrdinalIgnoreCase);
    }
}
