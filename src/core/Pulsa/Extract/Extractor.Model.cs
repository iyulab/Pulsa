using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PulsaExtract;

public static partial class Extractor
{
    /// <summary>
    /// Asks the caller's chat model for the schema's fields — each value with the words it was read from, copied as
    /// written — then verifies the answer exactly as <see cref="Verify"/> does. Schema errors are refused before the
    /// model is called; an error from the model ends the call. An answer that cannot be read is not an error: the
    /// required fields are reported missing, saying so.
    /// </summary>
    public static async Task<ExtractResult> ExtractAsync(
        IChatClient chatClient, string text, ExtractSchema schema, ExtractOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(text);
        options ??= new ExtractOptions();
        ValidateSchema(schema, options);

        var response = await chatClient.GetResponseAsync(BuildPrompt(text, schema), cancellationToken: cancellationToken);
        var candidates = ParseAnswer(response.Text);
        var result = Verify(text, schema, candidates ?? [], options);
        if (candidates is not null) return result;

        const string unreadable = "The model's answer could not be read as values.";
        return result with
        {
            Rejected = [.. result.Rejected.Select(r => r.Reason == RejectReason.Missing ? r with { Detail = unreadable } : r)],
        };
    }

    internal static string BuildPrompt(string text, ExtractSchema schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extract these fields from the text below.");
        foreach (var f in schema.Fields)
        {
            sb.Append("- ").Append(f.Name).Append(" (").Append(f.Type.ToString().ToLowerInvariant());
            if (f.Type == FieldType.Date) sb.Append(", as yyyy-MM-dd");
            if (f.Values is { Count: > 0 } values) sb.Append(", one of: ").Append(string.Join(", ", values));
            if (f.Many) sb.Append(", may have several values");
            if (f.Required) sb.Append(", required");
            sb.Append(')');
            if (!string.IsNullOrWhiteSpace(f.Description)) sb.Append(": ").Append(f.Description);
            sb.AppendLine();
        }
        sb.AppendLine("""

            For every value give the words of the text it was read from, copied exactly as written (same spelling, same
            numbers) — not a paraphrase. Leave out a field the text does not state; never guess.
            Answer with JSON only, in this shape:
            {"fields": {"<field name>": [{"value": "<value>", "quote": "<words copied from the text>"}]}}

            Text:
            """);
        sb.Append(text);
        return sb.ToString();
    }

    /// <summary>The candidates in a model's answer, or null when the answer holds no readable object.</summary>
    internal static IReadOnlyList<Candidate>? ParseAnswer(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(answer[start..(end + 1)]).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
        var fields = root.TryGetProperty("fields", out var f) ? f : root;
        if (fields.ValueKind != JsonValueKind.Object) return null;

        var candidates = new List<Candidate>();
        foreach (var field in fields.EnumerateObject())
        {
            var items = field.Value.ValueKind == JsonValueKind.Array ? field.Value.EnumerateArray().ToArray() : [field.Value];
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                candidates.Add(new Candidate(field.Name, Text(item, "value"), Text(item, "quote")));
            }
        }
        return candidates;

        static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;
    }
}
