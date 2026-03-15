using Pulsa;

namespace PulsaLLM;

public class LlmTaskOptions : IPulsaOptions
{
    public string? Name { get; set; }
    public string WatchPath { get; set; } = ".";
    public string FilePattern { get; set; } = "*.stt.txt";
    public string PromptFile { get; set; } = "SUMMARIZE-PROMPT.md";
    public int FileReadyRetries { get; set; } = 10;
    public int FileReadyRetryDelayMs { get; set; } = 500;
    public int RescanIntervalSeconds { get; set; } = 60;

    private string? _promptSlug;
    public string PromptSlug => _promptSlug ??= ExtractPromptSlug(PromptFile);

    public string OutputWatchPattern => $"*.{PromptSlug}.md";

    public string ResolveOutputPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var name = Path.GetFileName(filePath);
        var dotIndex = name.IndexOf('.');
        if (dotIndex > 0) name = name[..dotIndex];
        return Path.Combine(dir, $"{name}.{PromptSlug}.md");
    }

    public bool MatchesPattern(string filePath)
    {
        var patternExt = FilePattern.TrimStart('*');
        return filePath.EndsWith(patternExt, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPromptSlug(string promptFile)
    {
        var fileName = Path.GetFileNameWithoutExtension(promptFile);
        var idx = fileName.IndexOf("-PROMPT", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) fileName = fileName[..idx];
        return fileName.ToLowerInvariant();
    }
}

public class ProviderOptions
{
    public string Type { get; set; } = "local";
    public string Model { get; set; } = "default";
    public string ApiKey { get; set; } = "";
    public string Host { get; set; } = "";
    public string PathPrefix { get; set; } = "/v1";
    public int MaxTokens { get; set; }
    public int ContextWindow { get; set; }
    public float? Temperature { get; set; }
}
