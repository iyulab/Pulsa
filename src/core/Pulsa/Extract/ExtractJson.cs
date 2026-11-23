using System.Text.Encodings.Web;
using System.Text.Json;

namespace PulsaExtract;

/// <summary>
/// The JSON forms of grounded extraction, as <c>docs/extract.md</c> gives them: the schema to read, the candidate values
/// to verify, and the result to write. A host that takes these from files or a wire (the command line does) uses them
/// instead of its own reading; the JSON is read and written by hand, so it needs no reflection.
/// </summary>
public static class ExtractJson
{
    /// <summary>
    /// A schema from <c>{"fields":[{"name":…,"type":…,"required"?:…,"many"?:…,"values"?:[…],"pattern"?:…,"description"?:…}]}</c>,
    /// or the bare list of fields. Throws <see cref="InvalidDataException"/> naming what is wrong; whether the schema is
    /// usable (a name used twice, an enum without values) is decided when it is used.
    /// </summary>
    public static ExtractSchema ReadSchema(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The schema is not valid JSON: {ex.Message}", ex);
        }

        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("fields", out var fields) ? fields : root;
        if (list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The schema must be a list of fields, or an object with a \"fields\" list.");

        var result = new List<ExtractField>();
        var position = 0;
        foreach (var item in list.EnumerateArray())
        {
            position++;
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Field {position} of the schema is not an object.");
            var name = Text(item, "name") ?? throw new InvalidDataException($"Field {position} of the schema has no name.");
            var typeText = Text(item, "type") ?? throw new InvalidDataException($"Field '{name}' has no type.");
            if (!Enum.TryParse<FieldType>(typeText, ignoreCase: true, out var type) || !Enum.IsDefined(type))
                throw new InvalidDataException($"Field '{name}' has the type '{typeText}'; it must be string, number, date, boolean or enum.");
            result.Add(new ExtractField(name, type)
            {
                Required = Flag(item, "required", name),
                Many = Flag(item, "many", name),
                Values = item.TryGetProperty("values", out var values) ? Strings(values, name) : null,
                Pattern = Text(item, "pattern"),
                Description = Text(item, "description"),
            });
        }
        return new ExtractSchema(result);
    }

    /// <summary>
    /// Candidate values to verify, from <c>{"fields":{"&lt;name&gt;":[{"value":…,"quote":…}]}}</c> — the shape the model is asked
    /// to answer in. Throws <see cref="InvalidDataException"/> when there is no such object.
    /// </summary>
    public static IReadOnlyList<Candidate> ReadCandidates(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Extractor.ParseAnswer(json) ?? throw new InvalidDataException(
            "The candidates are not a JSON object of the form {\"fields\": {\"<name>\": [{\"value\": …, \"quote\": …}]}}.");
    }

    /// <summary>The result as indented JSON: <c>{"fields":[…],"rejected":[…]}</c>, with the reasons in kebab case as the contract names them.</summary>
    public static string Write(ExtractResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("fields");
            foreach (var field in result.Fields)
            {
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteString("value", field.Value);
                writer.WriteString("quote", field.Quote);
                writer.WriteStartObject("span");
                writer.WriteNumber("start", field.Span.Start);
                writer.WriteNumber("end", field.Span.End);
                writer.WriteEndObject();
                writer.WriteString("match", field.Match.ToString().ToLowerInvariant());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("rejected");
            foreach (var rejected in result.Rejected)
            {
                writer.WriteStartObject();
                writer.WriteString("name", rejected.Name);
                if (rejected.Value is not null) writer.WriteString("value", rejected.Value);
                if (rejected.Quote is not null) writer.WriteString("quote", rejected.Quote);
                writer.WriteString("reason", Reason(rejected.Reason));
                writer.WriteString("detail", rejected.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Reason(RejectReason reason) => reason switch
    {
        RejectReason.Missing => "missing",
        RejectReason.NoQuote => "no-quote",
        RejectReason.NotInText => "not-in-text",
        RejectReason.NotSupported => "not-supported",
        RejectReason.Format => "format",
        RejectReason.NotInSchema => "not-in-schema",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Flag(JsonElement item, string name, string field)
    {
        if (!item.TryGetProperty(name, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Field '{field}': \"{name}\" must be true or false."),
        };
    }

    private static List<string> Strings(JsonElement list, string field)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Field '{field}': \"values\" must be a list of strings.");
        var values = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Field '{field}': \"values\" must be a list of strings.");
            values.Add(item.GetString()!);
        }
        return values;
    }
}
