using Microsoft.Extensions.AI;
using OpenAI;

namespace PulsaPDFDiff;

public class VisionComparer(ILogger<VisionComparer> logger)
{
    /// <summary>
    /// Compare a single page pair (one reference page vs one target page).
    /// </summary>
    public async Task<CompareResult> ComparePageAsync(
        OpenAIOptions options,
        string referenceImage,
        string targetImage,
        int refPageNumber,
        int tgtPageNumber,
        string systemPrompt,
        CancellationToken ct = default)
    {
        var client = CreateClient(options);

        var contentParts = new List<AIContent>
        {
            new TextContent($"## 기준 문서 — 페이지 {refPageNumber}\n"),
            new DataContent(Convert.FromBase64String(referenceImage), "image/png"),
            new TextContent($"\n\n## 작업 문서 — 페이지 {tgtPageNumber}\n"),
            new DataContent(Convert.FromBase64String(targetImage), "image/png"),
            new TextContent("\n\n위의 기준 문서 페이지와 작업 문서 페이지를 비교하여 교정 리포트를 작성하세요.")
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, contentParts)
        };

        logger.LogInformation(
            "Comparing page {RefPage} (ref) vs {TgtPage} (tgt) using {Model}",
            refPageNumber, tgtPageNumber, options.Model);

        var chatOptions = new ChatOptions { MaxOutputTokens = 8192 };
        var response = await client.GetResponseAsync(messages, chatOptions, ct);

        var result = ExtractResult(response);

        logger.LogInformation(
            "Page {RefPage}↔{TgtPage} complete — prompt: {Prompt}, completion: {Completion}, total: {Total} tokens",
            refPageNumber, tgtPageNumber,
            result.PromptTokens, result.CompletionTokens, result.TotalTokens);

        return result;
    }

    /// <summary>
    /// Compare all pages at once (legacy full-document comparison).
    /// </summary>
    public async Task<CompareResult> CompareAsync(
        OpenAIOptions options,
        List<string> referenceImages,
        List<string> targetImages,
        string systemPrompt,
        CancellationToken ct = default)
    {
        var client = CreateClient(options);

        var contentParts = new List<AIContent>();
        contentParts.Add(new TextContent("## 기준 문서\n\n다음은 기준 문서의 각 페이지입니다:"));
        for (var i = 0; i < referenceImages.Count; i++)
        {
            contentParts.Add(new DataContent(
                Convert.FromBase64String(referenceImages[i]), "image/png"));
        }

        contentParts.Add(new TextContent("\n\n## 작업 문서\n\n다음은 작업 문서의 각 페이지입니다:"));
        for (var i = 0; i < targetImages.Count; i++)
        {
            contentParts.Add(new DataContent(
                Convert.FromBase64String(targetImages[i]), "image/png"));
        }

        contentParts.Add(new TextContent(
            "\n\n위의 기준 문서와 작업 문서를 비교하여 교정 리포트를 작성하세요."));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, contentParts)
        };

        logger.LogInformation(
            "Sending {RefPages} reference + {TargetPages} target pages to {Model}",
            referenceImages.Count, targetImages.Count, options.Model);

        var chatOptions = new ChatOptions { MaxOutputTokens = 16384 };
        var response = await client.GetResponseAsync(messages, chatOptions, ct);

        var result = ExtractResult(response);

        logger.LogInformation(
            "Full compare complete — prompt: {Prompt}, completion: {Completion}, total: {Total} tokens",
            result.PromptTokens, result.CompletionTokens, result.TotalTokens);

        return result;
    }

    private IChatClient CreateClient(OpenAIOptions options)
    {
        var credential = new System.ClientModel.ApiKeyCredential(options.ApiKey);
        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };
        return new OpenAIClient(credential, clientOptions)
            .GetChatClient(options.Model)
            .AsIChatClient();
    }

    private static CompareResult ExtractResult(ChatResponse response)
    {
        var result = new CompareResult
        {
            Text = response.Text ?? ""
        };

        if (response.Usage is { } usage)
        {
            result.PromptTokens = (int)(usage.InputTokenCount ?? 0);
            result.CompletionTokens = (int)(usage.OutputTokenCount ?? 0);
        }

        return result;
    }
}
