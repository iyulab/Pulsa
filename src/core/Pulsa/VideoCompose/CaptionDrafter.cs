using Microsoft.Extensions.AI;

namespace PulsaVideoCompose;

public sealed record DraftCaptionsRequest(IReadOnlyList<string> ImagePaths, string IntroText);

/// <summary>
/// Drafts one short on-screen caption per scene from a video's intro/purpose text, using a
/// caller-supplied IChatClient. Never constructs its own client or holds provider credentials —
/// spec §3.2's injected-interface rule.
/// </summary>
public static class CaptionDrafter
{
    public static async Task<IReadOnlyList<string>> DraftAsync(
        IChatClient chatClient, DraftCaptionsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ImagePaths.Count == 0)
            throw new ArgumentException("At least one image is required.", nameof(request));

        var prompt = BuildPrompt(request.ImagePaths.Count, request.IntroText);
        var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        return ParseCaptions(response.Text, request.ImagePaths.Count);
    }

    private static string BuildPrompt(int sceneCount, string introText) =>
        $"""
        You are drafting short on-screen captions for a {sceneCount}-scene video slideshow.
        The video's intro/purpose: {introText}

        Write exactly {sceneCount} short captions, one per scene, in the order the scenes appear.
        Each caption must be a single short sentence or phrase (under 12 words), suitable for a
        bold on-screen text overlay. Reply with exactly {sceneCount} lines, one caption per line,
        no numbering, no extra commentary.
        """;

    private static IReadOnlyList<string> ParseCaptions(string responseText, int expectedCount)
    {
        var lines = responseText
            .Split('\n')
            .Select(l => l.Trim().TrimStart('-', '*', '.', ' ').TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < expectedCount)
        {
            throw new InvalidOperationException(
                $"Expected {expectedCount} captions, model returned {lines.Count}.");
        }

        return lines.Take(expectedCount).ToList();
    }
}
