using Pulsa;

namespace PulsaLLM;

public class LlmOptions : IPulsaOptions
{
    /// <summary>Watch directory path</summary>
    public string WatchPath { get; set; } = ".";

    /// <summary>Input file pattern (e.g. *.stt.txt)</summary>
    public string FilePattern { get; set; } = "*.stt.txt";

    /// <summary>Prompt file path (relative to app directory)</summary>
    public string PromptFile { get; set; } = "SUMMARIZE-PROMPT.md";

    /// <summary>LLM provider configuration</summary>
    public ProviderOptions Provider { get; set; } = new();

    /// <summary>File lock wait max retries</summary>
    public int FileReadyRetries { get; set; } = 10;

    /// <summary>File lock wait interval (ms)</summary>
    public int FileReadyRetryDelayMs { get; set; } = 500;

    /// <summary>Periodic rescan interval (seconds). 0 to disable.</summary>
    public int RescanIntervalSeconds { get; set; } = 60;

    // Derived from prompt file name: SUMMARIZE-PROMPT.md -> summarize
    private string? _promptSlug;
    public string PromptSlug => _promptSlug ??= ExtractPromptSlug(PromptFile);

    public string OutputWatchPattern => $"*.{PromptSlug}.md";

    public string ResolveOutputPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        // Strip all extensions from input (e.g. "foo.stt.txt" -> "foo")
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
    /// <summary>Provider type: local, openai, openai-compatible</summary>
    public string Type { get; set; } = "local";

    /// <summary>Model name or ID</summary>
    public string Model { get; set; } = "default";

    /// <summary>API key (for openai/openai-compatible)</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>API host (for openai-compatible)</summary>
    public string Host { get; set; } = "";

    /// <summary>Max tokens for generation</summary>
    public int MaxTokens { get; set; } = 4096;
}
