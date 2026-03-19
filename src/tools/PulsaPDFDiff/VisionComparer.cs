using Microsoft.Extensions.AI;
using OpenAI;

namespace PulsaPDFDiff;

public class VisionComparer(ILogger<VisionComparer> logger)
{
    public async Task<string> CompareAsync(
        OpenAIOptions options,
        List<string> referenceImages,
        List<string> targetImages,
        string systemPrompt,
        CancellationToken ct = default)
    {
        var credential = new System.ClientModel.ApiKeyCredential(options.ApiKey);
        var client = new OpenAIClient(credential)
            .GetChatClient(options.Model)
            .AsIChatClient();

        var messages = new List<ChatMessage>();
        messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

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

        messages.Add(new ChatMessage(ChatRole.User, contentParts));

        logger.LogInformation(
            "Sending {RefPages} reference + {TargetPages} target pages to {Model}",
            referenceImages.Count, targetImages.Count, options.Model);

        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = 16384,
        };

        var response = await client.GetResponseAsync(messages, chatOptions, ct);
        return response.Text ?? "";
    }
}
