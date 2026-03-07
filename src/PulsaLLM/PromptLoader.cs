namespace PulsaLLM;

/// <summary>
/// Loads a prompt file with optional YAML frontmatter.
/// </summary>
public static class PromptLoader
{
    public record PromptData(string SystemPrompt, Dictionary<string, string> Frontmatter);

    public static async Task<PromptData> LoadAsync(string path, CancellationToken ct = default)
    {
        var content = await File.ReadAllTextAsync(path, ct);
        return Parse(content);
    }

    public static PromptData Parse(string content)
    {
        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!content.StartsWith("---"))
            return new PromptData(content.Trim(), frontmatter);

        var endIndex = content.IndexOf("\n---", 3);
        if (endIndex < 0)
            return new PromptData(content.Trim(), frontmatter);

        var yamlBlock = content[3..endIndex].Trim();
        var body = content[(endIndex + 4)..].Trim();

        foreach (var line in yamlBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();
            frontmatter[key] = value;
        }

        return new PromptData(body, frontmatter);
    }

    /// <summary>
    /// Applies frontmatter overrides to provider options.
    /// </summary>
    public static ProviderOptions ApplyOverrides(ProviderOptions baseOptions, Dictionary<string, string> frontmatter)
    {
        var result = new ProviderOptions
        {
            Type = baseOptions.Type,
            Model = baseOptions.Model,
            ApiKey = baseOptions.ApiKey,
            Host = baseOptions.Host,
            MaxTokens = baseOptions.MaxTokens,
        };

        if (frontmatter.TryGetValue("model", out var model))
            result.Model = model;
        if (frontmatter.TryGetValue("max_tokens", out var maxTokens) && int.TryParse(maxTokens, out var mt))
            result.MaxTokens = mt;
        if (frontmatter.TryGetValue("provider", out var providerType))
            result.Type = providerType;
        if (frontmatter.TryGetValue("api_key", out var apiKey))
            result.ApiKey = apiKey;
        if (frontmatter.TryGetValue("host", out var host))
            result.Host = host;
        if (frontmatter.TryGetValue("path_prefix", out var pathPrefix))
            result.PathPrefix = pathPrefix;

        return result;
    }
}
