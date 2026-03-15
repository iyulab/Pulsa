using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Pulsa;
using TokenMeter;

namespace PulsaLLM.Workers;

public class LlmWorker(
    FileQueue queue,
    IReadOnlyList<LlmTaskOptions> tasks,
    ProviderOptions globalProvider,
    ChatClientFactory clientFactory,
    ITokenCounter tokenCounter,
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
        // Pre-load all prompts
        var prompts = new Dictionary<int, (string SystemPrompt, ProviderOptions ResolvedProvider)>();

        for (var i = 0; i < tasks.Count; i++)
        {
            var opts = tasks[i];
            var promptPath = ResolvePromptPath(opts.PromptFile);

            if (promptPath is null)
            {
                logger.LogError("[Task#{Index} {Name}] Prompt file not found: {Path}",
                    i, opts.Name ?? $"Task#{i}", opts.PromptFile);
                continue;
            }

            var promptData = await PromptLoader.LoadAsync(promptPath, stoppingToken);
            var resolvedProvider = PromptLoader.ApplyOverrides(globalProvider, promptData.Frontmatter);

            // Thinking model temperature adjustment
            if (resolvedProvider.Model.Contains("thinking", StringComparison.OrdinalIgnoreCase)
                && resolvedProvider.Temperature is 0.0f)
            {
                resolvedProvider.Temperature = 0.6f;
                logger.LogWarning("[Task#{Index} {Name}] Thinking model with temperature=0.0; adjusted to 0.6",
                    i, opts.Name ?? $"Task#{i}");
            }

            prompts[i] = (promptData.SystemPrompt, resolvedProvider);
        }

        logger.LogInformation("LLM worker started with {Count} task(s):", tasks.Count);
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            var status = prompts.ContainsKey(i) ? "ready" : "SKIPPED (no prompt)";
            logger.LogInformation("  [{Index}] {Name}: {Path} ({Pattern} → *.{Slug}.md) [{Status}]",
                i, t.Name ?? $"Task#{i}", t.WatchPath, t.FilePattern, t.PromptSlug, status);
        }

        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!prompts.TryGetValue(item.TaskIndex, out var promptInfo))
            {
                queue.Complete(item.FilePath, item.TaskIndex);
                continue;
            }

            var opts = tasks[item.TaskIndex];
            var label = opts.Name ?? $"Task#{item.TaskIndex}";
            try
            {
                await ProcessAsync(item.FilePath, promptInfo.SystemPrompt,
                    promptInfo.ResolvedProvider, opts, label, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[{Label}] LLM processing failed: {Path}", label, item.FilePath);
            }
            finally
            {
                queue.Complete(item.FilePath, item.TaskIndex);
            }
        }
    }

    private static string? ResolvePromptPath(string promptFile)
    {
        var path = Path.IsPathRooted(promptFile)
            ? promptFile
            : Path.Combine(AppContext.BaseDirectory, promptFile);
        if (File.Exists(path)) return path;

        path = Path.Combine(Directory.GetCurrentDirectory(), promptFile);
        return File.Exists(path) ? path : null;
    }

    private async Task ProcessAsync(
        string filePath, string systemPrompt, ProviderOptions providerOpts,
        LlmTaskOptions opts, string label, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            logger.LogWarning("[{Label}] File not found, skipping: {Path}", label, filePath);
            return;
        }

        var outputPath = opts.ResolveOutputPath(filePath);
        if (File.Exists(outputPath))
        {
            logger.LogDebug("[{Label}] Output already exists, skipping: {Path}", label, outputPath);
            return;
        }

        if (!await FileHelper.WaitUntilReadyAsync(
                filePath, opts.FileReadyRetries, opts.FileReadyRetryDelayMs, logger, ct))
            return;

        var tempPath = outputPath + ".tmp";
        logger.LogInformation("[{Label}] Processing: {Path}", label, filePath);
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);

            // Proactive truncation
            var contextWindow = providerOpts.ContextWindow;
            if (contextWindow > 0)
            {
                var configuredMaxTokens = providerOpts.MaxTokens;
                var outputReserve = configuredMaxTokens > 0
                    ? configuredMaxTokens
                    : Math.Max(contextWindow / 2, 2048);
                var inputBudget = contextWindow - outputReserve;
                var systemTokens = tokenCounter.CountTokens(systemPrompt);
                var userBudget = inputBudget - systemTokens - 64;

                if (userBudget > 0)
                {
                    var userTokens = tokenCounter.CountTokens(content);
                    if (userTokens > userBudget)
                    {
                        var keepRatio = (double)userBudget / userTokens;
                        var maxChars = (int)(content.Length * keepRatio);
                        content = content[..maxChars];
                        logger.LogWarning(
                            "[{Label}] Input truncated (context={Context}, outputReserve={Reserve}, " +
                            "system={System}, userBudget={Budget}): {UserTokens}→~{Budget2} tokens ({Percent:F0}%): {Path}",
                            label, contextWindow, outputReserve, systemTokens, userBudget,
                            userTokens, userBudget, keepRatio * 100, filePath);
                    }
                }
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, content),
            };

            var maxTokens = providerOpts.MaxTokens;
            var temperature = providerOpts.Temperature;
            ChatOptions? chatOptions = null;
            if (maxTokens > 0 || temperature.HasValue || !string.IsNullOrEmpty(providerOpts.Model))
            {
                chatOptions = new ChatOptions();
                if (maxTokens > 0) chatOptions.MaxOutputTokens = maxTokens;
                if (temperature.HasValue) chatOptions.Temperature = temperature.Value;
                if (!string.IsNullOrEmpty(providerOpts.Model))
                    chatOptions.ModelId = providerOpts.Model;
            }

            var chatClient = clientFactory.GetOrCreate(providerOpts);
            var result = await CallAndValidateAsync(chatClient, messages, chatOptions, label, filePath, ct);

            if (result is null)
                return;

            await File.WriteAllTextAsync(tempPath, result, ct);
            File.Move(tempPath, outputPath, overwrite: true);

            logger.LogInformation("[{Label}] Done: {Path}", label, outputPath);
        }
        catch (OperationCanceledException)
        {
            FileHelper.TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Label}] LLM processing failed: {Path}", label, filePath);
            FileHelper.TryDelete(tempPath);
        }
    }

    private async Task<string?> CallAndValidateAsync(
        IChatClient chatClient, List<ChatMessage> messages, ChatOptions? chatOptions,
        string label, string filePath, CancellationToken ct)
    {
        var response = await CallWithRetryAsync(chatClient, messages, chatOptions, label, filePath, ct);
        var rawResult = (response.Text ?? "").Trim();

        if (rawResult.Length == 0)
        {
            logger.LogWarning("[{Label}] LLM returned empty response — skipping: {Path}", label, filePath);
            return null;
        }

        var result = TrimDuplicateSections(rawResult);

        if (result.Length < rawResult.Length)
        {
            logger.LogDebug(
                "[{Label}] Post-processing trimmed response from {RawLength} to {Length} chars: {Path}",
                label, rawResult.Length, result.Length, filePath);
        }

        return result;
    }

    // Regex to parse "maximum context length is X tokens ... Y input tokens" from API error
    private static readonly Regex ContextLimitRegex = new(
        @"maximum context length is (\d+) tokens.*?(\d+) input tokens",
        RegexOptions.Compiled);

    private async Task<ChatResponse> CallWithRetryAsync(
        IChatClient chatClient, List<ChatMessage> messages, ChatOptions? chatOptions,
        string label, string filePath, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await chatClient.GetResponseAsync(messages, chatOptions, ct);
            }
            catch (ClientResultException ex) when (ex.Status == 400
                && TryExtractContextInfo(ex, out var contextLength, out var inputTokens))
            {
                var available = contextLength - inputTokens;
                if (available > 0)
                {
                    chatOptions ??= new ChatOptions();
                    chatOptions.MaxOutputTokens = available;
                    logger.LogWarning(
                        "[{Label}] max_tokens exceeded context limit, retrying with {MaxTokens}: {Path}",
                        label, available, filePath);
                    return await chatClient.GetResponseAsync(messages, chatOptions, ct);
                }

                var userMsg = messages.LastOrDefault(m => m.Role == ChatRole.User);
                if (userMsg is null) throw;

                var userText = userMsg.Text ?? "";
                if (userText.Length == 0) throw;

                var outputReserve = chatOptions?.MaxOutputTokens ?? 1024;
                var targetInputTokens = contextLength - outputReserve;
                var systemText = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
                var totalChars = systemText.Length + userText.Length;
                var systemTokenEstimate = totalChars > 0
                    ? (int)((long)inputTokens * systemText.Length / totalChars)
                    : 0;
                var userTokenBudget = targetInputTokens - systemTokenEstimate - 64;
                if (userTokenBudget < 100) throw;

                var currentUserTokens = inputTokens - systemTokenEstimate;
                if (currentUserTokens <= 0) throw;
                var keepRatio = (double)userTokenBudget / currentUserTokens;
                var maxChars = (int)(userText.Length * keepRatio);
                if (maxChars <= 0 || maxChars >= userText.Length) throw;

                var truncated = userText[..maxChars];
                messages[messages.Count - 1] = new ChatMessage(ChatRole.User, truncated);

                logger.LogWarning(
                    "[{Label}] Input ({InputTokens} tokens) exceeds context ({ContextLength}), " +
                    "truncated to {KeepPercent:F0}% (~{MaxChars} chars): {Path}",
                    label, inputTokens, contextLength, keepRatio * 100, maxChars, filePath);
                return await chatClient.GetResponseAsync(messages, chatOptions, ct);
            }
            catch (ClientResultException ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                logger.LogWarning(
                    "[{Label}] Transient error (HTTP {Status}), retry {Attempt}/{Max} after {Delay}s: {Path}",
                    label, ex.Status, attempt + 1, MaxRetries, RetryDelays[attempt].TotalSeconds, filePath);
                await Task.Delay(RetryDelays[attempt], ct);
            }
            catch (ClientResultException ex)
            {
                var body = ex.GetRawResponse()?.Content?.ToString();
                logger.LogError(
                    "[{Label}] API error (HTTP {Status}): {Body} — {Path}",
                    label, ex.Status, body ?? ex.Message, filePath);
                throw;
            }
        }
    }

    private static bool TryExtractContextInfo(
        ClientResultException ex, out int contextLength, out int inputTokens)
    {
        contextLength = 0;
        inputTokens = 0;
        var body = ex.GetRawResponse()?.Content?.ToString();
        if (body is null) return false;

        var match = ContextLimitRegex.Match(body);
        if (!match.Success) return false;

        return int.TryParse(match.Groups[1].Value, out contextLength)
            && int.TryParse(match.Groups[2].Value, out inputTokens);
    }

    private static bool IsTransient(ClientResultException ex) =>
        ex.Status is 408 or 429 or (>= 500 and <= 599);

    // Regex to detect numbered markdown headings like "### 1." or "### 2."
    private static readonly Regex SectionHeadingRegex = new(
        @"^###\s+(\d+)\.", RegexOptions.Compiled | RegexOptions.Multiline);

    internal static string TrimDuplicateSections(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var seen = new HashSet<string>();
        foreach (Match match in SectionHeadingRegex.Matches(text))
        {
            var sectionNum = match.Groups[1].Value;
            if (!seen.Add(sectionNum))
            {
                return text[..match.Index].TrimEnd();
            }
        }

        return text;
    }
}
