using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pulsa;
using TokenMeter;

namespace PulsaLLM.Workers;

public class LlmWorker(
    FileQueue queue,
    IOptions<LlmOptions> options,
    ProviderOptions providerOptions,
    IChatClient chatClient,
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
        var opts = options.Value;

        var promptPath = Path.IsPathRooted(opts.PromptFile)
            ? opts.PromptFile
            : Path.Combine(AppContext.BaseDirectory, opts.PromptFile);
        if (!File.Exists(promptPath))
            promptPath = Path.Combine(Directory.GetCurrentDirectory(), opts.PromptFile);

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

            // Proactive truncation: if context window is known, trim input before calling API
            var contextWindow = providerOptions.ContextWindow;
            if (contextWindow > 0)
            {
                // Reserve output budget: use configured max_tokens if set.
                var configuredMaxTokens = providerOptions.MaxTokens;
                var outputReserve = configuredMaxTokens > 0
                    ? configuredMaxTokens
                    : Math.Max(contextWindow / 2, 2048);
                var inputBudget = contextWindow - outputReserve;
                var systemTokens = tokenCounter.CountTokens(systemPrompt);
                var userBudget = inputBudget - systemTokens - 64; // margin

                if (userBudget > 0)
                {
                    var userTokens = tokenCounter.CountTokens(content);
                    if (userTokens > userBudget)
                    {
                        var keepRatio = (double)userBudget / userTokens;
                        var maxChars = (int)(content.Length * keepRatio);
                        content = content[..maxChars];
                        logger.LogWarning(
                            "Input truncated (context={Context}, outputReserve={Reserve}, system={System}, userBudget={Budget}): " +
                            "{UserTokens}→~{Budget2} tokens ({Percent:F0}%): {Path}",
                            contextWindow, outputReserve, systemTokens, userBudget,
                            userTokens, userBudget, keepRatio * 100, filePath);
                    }
                }
            }

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

            var result = await CallAndValidateAsync(messages, chatOptions, filePath, ct);

            if (result is null)
                return;

            await File.WriteAllTextAsync(tempPath, result, ct);
            File.Move(tempPath, outputPath, overwrite: true);

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

    // Regex to detect the first section heading "### 1."
    private static readonly Regex FirstSectionRegex = new(
        @"^###\s+1\.", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Calls the LLM and validates the response has all expected sections.
    /// If section 1 is missing (common when thinking tokens consume the output budget),
    /// synthesizes it from section 3 (핵심 논지 전개).
    /// </summary>
    private async Task<string?> CallAndValidateAsync(
        List<ChatMessage> messages, ChatOptions? chatOptions, string filePath, CancellationToken ct)
    {
        var response = await CallWithRetryAsync(messages, chatOptions, filePath, ct);
        var result = (response.Text ?? "").Trim();
        result = TrimDuplicateSections(result);
        result = StripInterSectionNoise(result);

        if (result.Length == 0 || !SectionHeadingRegex.IsMatch(result))
        {
            logger.LogWarning(
                "LLM returned no usable content (length={Length}) — skipping: {Path}",
                result.Length, filePath);
            return null;
        }

        if (FirstSectionRegex.IsMatch(result))
            return result;

        // Section 1 missing — synthesize from first available section's content
        var section1 = SynthesizeSection1(result);
        if (section1 is not null)
        {
            logger.LogInformation(
                "Section 1 synthesized from existing content: {Path}", filePath);
            return section1 + "\n\n" + result;
        }

        logger.LogWarning(
            "Section 1 missing, could not synthesize: {Path}", filePath);
        return result;
    }

    // Regex to strip leading list markers: "- ", "* ", "1. ", "**bold**: "
    private static readonly Regex ListMarkerRegex = new(
        @"^(?:[-*]\s*)?(?:\d+\.\s*)?(?:\*\*[^*]+\*\*:?\s*)?",
        RegexOptions.Compiled);

    /// <summary>
    /// Synthesizes section 1 (핵심 주제) from the first available section's content.
    /// Extracts the first CJK-containing content line from the first section heading found.
    /// </summary>
    internal static string? SynthesizeSection1(string text)
    {
        var headingMatch = SectionHeadingRegex.Match(text);
        if (!headingMatch.Success) return null;

        // Scan lines after the first heading for a CJK-containing content line
        var afterHeading = text[(headingMatch.Index + headingMatch.Length)..];
        foreach (var line in afterHeading.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (SectionHeadingRegex.IsMatch(trimmed)) break; // hit next section

            // Strip list markers and bold labels
            var content = ListMarkerRegex.Replace(trimmed, "").Trim();
            if (content.Length >= 10 && HasAnyCjk(content))
                return "### 1. 핵심 주제\n- " + content;
        }

        return null;
    }

    /// <summary>
    /// Removes non-CJK lines between section headings (e.g. "Then section 5:").
    /// These are artifacts from thinking models that leak reasoning into output.
    /// </summary>
    internal static string StripInterSectionNoise(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Keep blank lines, section headings, and lines with CJK
            if (trimmed.Length == 0 || SectionHeadingRegex.IsMatch(trimmed) || HasAnyCjk(trimmed))
                result.Add(line);
        }

        return string.Join('\n', result);
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
                && TryExtractContextInfo(ex, out var contextLength, out var inputTokens))
            {
                var available = contextLength - inputTokens;
                if (available > 0)
                {
                    // max_tokens exceeds available context — cap and retry once
                    chatOptions ??= new ChatOptions();
                    chatOptions.MaxOutputTokens = available;
                    logger.LogWarning(
                        "max_tokens exceeded context limit, retrying with {MaxTokens}: {Path}",
                        available, filePath);
                    return await chatClient.GetResponseAsync(messages, chatOptions, ct);
                }

                // Input itself exceeds context window — truncate user content and retry
                var userMsg = messages.LastOrDefault(m => m.Role == ChatRole.User);
                if (userMsg is null) throw;

                var userText = userMsg.Text ?? "";
                if (userText.Length == 0) throw;

                // Use the API's own token count to derive the actual chars/token ratio
                // for this model, then calculate how much user text to keep.
                var outputReserve = chatOptions?.MaxOutputTokens ?? 1024;
                var targetInputTokens = contextLength - outputReserve;
                // Estimate system prompt tokens proportionally from total input
                var systemText = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";
                var totalChars = systemText.Length + userText.Length;
                var systemTokenEstimate = totalChars > 0
                    ? (int)((long)inputTokens * systemText.Length / totalChars)
                    : 0;
                var userTokenBudget = targetInputTokens - systemTokenEstimate - 64; // margin
                if (userTokenBudget < 100) throw; // too small to be useful

                // Derive ratio from actual API token count (model-agnostic)
                var currentUserTokens = inputTokens - systemTokenEstimate;
                if (currentUserTokens <= 0) throw;
                var keepRatio = (double)userTokenBudget / currentUserTokens;
                var maxChars = (int)(userText.Length * keepRatio);
                if (maxChars <= 0 || maxChars >= userText.Length) throw;

                var truncated = userText[..maxChars];
                messages[messages.Count - 1] = new ChatMessage(ChatRole.User, truncated);

                logger.LogWarning(
                    "Input ({InputTokens} tokens) exceeds context ({ContextLength}), " +
                    "truncated to {KeepPercent:F0}% (~{MaxChars} chars): {Path}",
                    inputTokens, contextLength, keepRatio * 100, maxChars, filePath);
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

    /// <summary>
    /// Detects duplicate numbered sections (e.g. "### 3." appearing twice) caused by
    /// continuation overlap and truncates at the first repeated heading.
    /// Also trims trailing non-CJK content after the last section (reasoning noise).
    /// </summary>
    internal static string TrimDuplicateSections(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var seen = new HashSet<string>();
        var lastSectionEnd = -1;
        foreach (Match match in SectionHeadingRegex.Matches(text))
        {
            var sectionNum = match.Groups[1].Value;
            if (!seen.Add(sectionNum))
            {
                // Found a duplicate — truncate at this position
                return text[..match.Index].TrimEnd();
            }
            lastSectionEnd = match.Index;
        }

        // Trim trailing non-CJK noise after the last section block
        if (lastSectionEnd >= 0)
        {
            text = TrimTrailingNonCjk(text);
        }

        return text;
    }

    /// <summary>
    /// Scans backwards from the end of text; if trailing paragraphs contain no CJK
    /// characters (e.g. leaked English reasoning), trims them.
    /// </summary>
    private static string TrimTrailingNonCjk(string text)
    {
        // Find the last blank-line boundary
        var searchFrom = text.Length;
        while (true)
        {
            var blankLine = text.LastIndexOf("\n\n", searchFrom - 1, StringComparison.Ordinal);
            if (blankLine < 0 || blankLine < text.Length / 2) break;

            var trailing = text[(blankLine + 2)..];
            if (trailing.Length < 10) break;

            if (HasAnyCjk(trailing)) break;

            // Trailing block has no CJK — remove it
            text = text[..blankLine].TrimEnd();
            searchFrom = text.Length;
        }

        return text;
    }

    private static bool HasAnyCjk(string text)
    {
        foreach (var c in text)
        {
            if (c is (>= '\uAC00' and <= '\uD7A3')   // Korean Hangul
                  or (>= '\u3131' and <= '\u318E')     // Korean Jamo
                  or (>= '\u4E00' and <= '\u9FFF')     // CJK Ideographs
                  or (>= '\u3040' and <= '\u30FF'))     // Hiragana/Katakana
                return true;
        }
        return false;
    }
}
