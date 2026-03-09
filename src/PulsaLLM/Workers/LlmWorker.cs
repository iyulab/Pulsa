using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pulsa;

namespace PulsaLLM.Workers;

public class LlmWorker(
    FileQueue queue,
    IOptions<LlmOptions> options,
    ProviderOptions providerOptions,
    IChatClient chatClient,
    ILogger<LlmWorker> logger) : BackgroundService
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Factory.StartNew(
            () => RunAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        var promptPath = Path.IsPathRooted(opts.PromptFile)
            ? opts.PromptFile
            : Path.Combine(AppContext.BaseDirectory, opts.PromptFile);

        if (!File.Exists(promptPath))
        {
            logger.LogError("Prompt file not found: {Path}", promptPath);
            return;
        }

        var promptData = await PromptLoader.LoadAsync(promptPath, stoppingToken);

        logger.LogInformation(
            "LLM worker started. Pattern: {Pattern} -> *.{Slug}.md",
            opts.FilePattern, opts.PromptSlug);

        await foreach (var filePath in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(filePath, promptData.SystemPrompt, opts, stoppingToken);
            }
            finally
            {
                queue.Complete(filePath);
            }
        }
    }

    private async Task ProcessAsync(
        string filePath, string systemPrompt, LlmOptions opts, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            logger.LogWarning("File not found, skipping: {Path}", filePath);
            return;
        }

        var outputPath = opts.ResolveOutputPath(filePath);
        if (File.Exists(outputPath))
        {
            logger.LogDebug("Output already exists, skipping: {Path}", outputPath);
            return;
        }

        if (!await FileHelper.WaitUntilReadyAsync(
                filePath, opts.FileReadyRetries, opts.FileReadyRetryDelayMs, logger, ct))
            return;

        var tempPath = outputPath + ".tmp";
        logger.LogInformation("Processing: {Path}", filePath);
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, content),
            };

            var maxTokens = providerOptions.MaxTokens;
            var temperature = providerOptions.Temperature;
            ChatOptions? chatOptions = null;
            if (maxTokens > 0 || temperature.HasValue || !string.IsNullOrEmpty(providerOptions.Model))
            {
                chatOptions = new ChatOptions();
                if (maxTokens > 0) chatOptions.MaxOutputTokens = maxTokens;
                if (temperature.HasValue) chatOptions.Temperature = temperature.Value;
                // ModelId is required for IndexThinking to activate reasoning
                // (enable_thinking, include_reasoning) and strip think tags from response.
                if (!string.IsNullOrEmpty(providerOptions.Model))
                    chatOptions.ModelId = providerOptions.Model;
            }

            var response = await CallWithRetryAsync(messages, chatOptions, filePath, ct);
            var result = StripLeadingThinking(response.Text ?? "").Trim();

            await File.WriteAllTextAsync(tempPath, result, ct);
            File.Move(tempPath, outputPath, overwrite: false);

            logger.LogInformation("Done: {Path}", outputPath);
        }
        catch (OperationCanceledException)
        {
            FileHelper.TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM processing failed: {Path}", filePath);
            FileHelper.TryDelete(tempPath);
        }
    }

    // Regex to parse "maximum context length is X tokens ... Y input tokens" from API error
    private static readonly Regex ContextLimitRegex = new(
        @"maximum context length is (\d+) tokens.*?(\d+) input tokens",
        RegexOptions.Compiled);

    private async Task<ChatResponse> CallWithRetryAsync(
        List<ChatMessage> messages, ChatOptions? chatOptions, string filePath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await chatClient.GetResponseAsync(messages, chatOptions, ct);
            }
            catch (ClientResultException ex) when (ex.Status == 400
                && TryExtractAvailableTokens(ex, out var availableTokens))
            {
                // max_tokens exceeds available context — cap and retry once
                chatOptions ??= new ChatOptions();
                chatOptions.MaxOutputTokens = availableTokens;
                logger.LogWarning(
                    "max_tokens exceeded context limit, retrying with {MaxTokens}: {Path}",
                    availableTokens, filePath);
                return await chatClient.GetResponseAsync(messages, chatOptions, ct);
            }
            catch (ClientResultException ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                logger.LogWarning(
                    "Transient error (HTTP {Status}), retry {Attempt}/{Max} after {Delay}s: {Path}",
                    ex.Status, attempt + 1, MaxRetries, RetryDelays[attempt].TotalSeconds, filePath);
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (ClientResultException ex)
            {
                var body = ex.GetRawResponse()?.Content?.ToString();
                logger.LogError(
                    "API error (HTTP {Status}): {Body} — {Path}",
                    ex.Status, body ?? ex.Message, filePath);
                throw;
            }
        }
    }

    private static bool TryExtractAvailableTokens(ClientResultException ex, out int availableTokens)
    {
        availableTokens = 0;
        var body = ex.GetRawResponse()?.Content?.ToString();
        if (body is null) return false;

        var match = ContextLimitRegex.Match(body);
        if (!match.Success) return false;

        if (int.TryParse(match.Groups[1].Value, out var contextLength)
            && int.TryParse(match.Groups[2].Value, out var inputTokens))
        {
            availableTokens = contextLength - inputTokens;
            return availableTokens > 0;
        }
        return false;
    }

    private static bool IsTransient(ClientResultException ex) =>
        ex.Status is 408 or 429 or (>= 500 and <= 599);

    // App-specific fallback: detect leading untagged thinking by finding the first
    // structured heading in the output. This relies on knowledge of the prompt format
    // (markdown headings or numbered Korean headings) and does NOT belong in IndexThinking.
    // Think tag stripping and trailing reasoning stripping are handled by IndexThinking.

    // Preferred: markdown headings like "### 1." or "## 1."
    private static readonly Regex MarkdownHeadingRegex = new(
        @"^#{1,3}\s+\d+[\.\)]\s", RegexOptions.Multiline | RegexOptions.Compiled);

    // Fallback: plain numbered headings with Korean text (e.g. "1. 핵심 주제")
    private static readonly Regex NumberedKoreanHeadingRegex = new(
        @"^\d+[\.\)]\s+[\uAC00-\uD7A3\u3131-\u318E]",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static string StripLeadingThinking(string text)
    {
        // If structured content starts after >100 chars of preamble,
        // the preamble is likely untagged thinking. Strip it.
        var match = MarkdownHeadingRegex.Match(text);
        if (match.Success && match.Index > 100)
        {
            return text[match.Index..];
        }

        match = NumberedKoreanHeadingRegex.Match(text);
        if (match.Success && match.Index > 100)
        {
            return text[match.Index..];
        }

        return text;
    }
}
