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

    // Strip <think>...</think> tags from thinking models (e.g. Qwen3, DeepSeek)
    private static readonly Regex ThinkTagsRegex = new(
        @"<think>[\s\S]*?</think>", RegexOptions.Compiled);

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
            if (maxTokens > 0 || temperature.HasValue)
            {
                chatOptions = new ChatOptions();
                if (maxTokens > 0) chatOptions.MaxOutputTokens = maxTokens;
                if (temperature.HasValue) chatOptions.Temperature = temperature.Value;
            }

            var response = await CallWithRetryAsync(messages, chatOptions, filePath, ct);
            var result = StripThinkTags(response.Text ?? "");

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

    private async Task<ChatResponse> CallWithRetryAsync(
        List<ChatMessage> messages, ChatOptions? chatOptions, string filePath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await chatClient.GetResponseAsync(messages, chatOptions, ct);
            }
            catch (ClientResultException ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                logger.LogWarning(
                    "Transient error (HTTP {Status}), retry {Attempt}/{Max} after {Delay}s: {Path}",
                    ex.Status, attempt + 1, MaxRetries, RetryDelays[attempt].TotalSeconds, filePath);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private static bool IsTransient(ClientResultException ex) =>
        ex.Status is 400 or 408 or 429 or (>= 500 and <= 599);

    private static string StripThinkTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Strip complete <think>...</think> blocks
        text = ThinkTagsRegex.Replace(text, "");

        // Handle orphaned </think> without matching <think>.
        // Some OpenAI SDK adapters strip the opening <think> tag, leaving:
        //   "thinking content</think>\n\nactual content"
        while (true)
        {
            var endIdx = text.IndexOf("</think>", StringComparison.Ordinal);
            if (endIdx < 0) break;
            text = text[(endIdx + "</think>".Length)..];
        }

        // Fallback: thinking models may output untagged reasoning before
        // the actual structured content (e.g. "Okay, let me..." in English).
        // Detect the first markdown heading that matches the expected output
        // format and strip everything before it.
        text = StripUntaggedThinking(text);

        return text.Trim();
    }

    // Pattern for markdown headings commonly used in structured output prompts
    private static readonly Regex MarkdownHeadingRegex = new(
        @"^###?\s+\d+[\.\)]\s", RegexOptions.Multiline | RegexOptions.Compiled);

    private static string StripUntaggedThinking(string text)
    {
        var match = MarkdownHeadingRegex.Match(text);
        if (match.Success && match.Index > 0)
        {
            // Only strip if there's substantial preamble before the heading
            var preamble = text[..match.Index];
            if (preamble.Length > 100)
            {
                text = text[match.Index..];
            }
        }

        return text;
    }
}
