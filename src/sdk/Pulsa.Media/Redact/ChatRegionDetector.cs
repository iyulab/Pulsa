using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PulsaRedact;

/// <summary>
/// Finds regions to hide with a vision model the caller supplies (any <see cref="IChatClient"/> that takes image
/// input). The model sees the image and what to look for, and answers with boxes in pixels; this class parses the
/// answer and drops what is not a box. Whether a box is inside the picture and confident enough is checked by the
/// redactor, not trusted from the model. It never creates a client or holds a credential.
/// </summary>
public sealed class ChatRegionDetector : IRegionDetector
{
    private readonly IChatClient _chatClient;
    private readonly string _lookFor;

    /// <param name="lookFor">What to find, in plain words — "faces and licence plates", "people's names on screen".</param>
    public ChatRegionDetector(IChatClient chatClient, string lookFor)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(lookFor);
        _chatClient = chatClient;
        _lookFor = lookFor;
    }

    public string Name => "chat-vision";

    public async Task<IReadOnlyList<DetectedRegion>> DetectAsync(string imagePath, int width, int height, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var prompt = string.Create(CultureInfo.InvariantCulture, $$"""
            The image is {{width}} pixels wide and {{height}} pixels tall. Find every {{_lookFor}} in it.
            Answer with a JSON array only, one object per thing found, in pixels from the top-left corner:
            [{"label": "<what it is>", "x": <left>, "y": <top>, "width": <w>, "height": <h>, "confidence": <0 to 1>}]
            Answer [] if there is none. Cover the whole thing; a box a little too large is better than one too small.
            """);
        var message = new ChatMessage(ChatRole.User, [new TextContent(prompt), new DataContent(bytes, MediaType(imagePath))]);
        var response = await _chatClient.GetResponseAsync([message], cancellationToken: cancellationToken);
        return Parse(response.Text);
    }

    /// <summary>The boxes in a model's answer: the first JSON array in it, keeping only well-formed entries.</summary>
    internal static IReadOnlyList<DetectedRegion> Parse(string answer)
    {
        var start = answer.IndexOf('[');
        var end = answer.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        JsonElement array;
        try
        {
            array = JsonDocument.Parse(answer[start..(end + 1)]).RootElement;
        }
        catch (JsonException)
        {
            return [];
        }

        var regions = new List<DetectedRegion>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!TryInt(item, "x", out var x) || !TryInt(item, "y", out var y) ||
                !TryInt(item, "width", out var w) || !TryInt(item, "height", out var h) || w <= 0 || h <= 0)
                continue;
            var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : "region";
            var confidence = item.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? Math.Clamp(c.GetDouble(), 0, 1)
                : 0; // a box without a confidence is not trusted enough to hide on its own
            regions.Add(new DetectedRegion(new RedactRegion(x, y, w, h), label, confidence));
        }
        return regions;
    }

    private static bool TryInt(JsonElement item, string name, out int value)
    {
        value = 0;
        if (!item.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var d)) return false;
        if (double.IsNaN(d) || d < int.MinValue || d > int.MaxValue) return false;
        value = (int)Math.Round(d);
        return true;
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };
}
