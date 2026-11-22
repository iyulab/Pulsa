using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PulsaExtract;

/// <summary>
/// Grounded extraction: values a schema names, each proven against the text by the words it was read from. The
/// contract is <c>docs/extract.md</c>. <see cref="Verify"/> checks any structured output already made; it is
/// deterministic and takes no model.
/// </summary>
public static partial class Extractor
{
    public static ExtractResult Verify(string text, ExtractSchema schema, IEnumerable<Candidate> output, ExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new ExtractOptions();
        var fields = ValidateSchema(schema, options);

        var located = new Locator(text);
        var accepted = new List<ExtractedValue>();
        var rejected = new List<RejectedValue>();
        var cursor = 0; // UTF-16 index after the previous accepted span

        foreach (var candidate in output)
        {
            if (!fields.TryGetValue(candidate.Name, out var field))
            {
                rejected.Add(new(candidate.Name, candidate.Value, candidate.Quote, RejectReason.NotInSchema, "The schema does not name this field."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(candidate.Value)) continue; // nothing proposed; a required field is reported as missing below

            if (Canonical(field, candidate.Value) is not { } value)
            {
                rejected.Add(new(field.Name, candidate.Value, candidate.Quote, RejectReason.Format, FormatProblem(field)));
                continue;
            }
            if (!field.Many && accepted.Any(a => a.Name == field.Name))
            {
                rejected.Add(new(field.Name, candidate.Value, candidate.Quote, RejectReason.Format, "The field takes one value and already has one."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(candidate.Quote))
            {
                rejected.Add(new(field.Name, candidate.Value, candidate.Quote, RejectReason.NoQuote, "The value came without the words it was read from."));
                continue;
            }
            if (located.Find(candidate.Quote, cursor, options) is not { } found)
            {
                rejected.Add(new(field.Name, candidate.Value, candidate.Quote, RejectReason.NotInText, "The quote is not in the text."));
                continue;
            }
            var quoted = text[found.Start..found.End];
            if (!Carries(field, value, quoted))
            {
                rejected.Add(new(field.Name, candidate.Value, candidate.Quote, RejectReason.NotSupported,
                    $"The quote is in the text but does not carry the {field.Type.ToString().ToLowerInvariant()} value."));
                continue;
            }

            accepted.Add(new ExtractedValue(field.Name, value, quoted, located.ScalarSpan(found.Start, found.End), found.Match));
            cursor = found.End;
        }

        foreach (var field in fields.Values)
            if (field.Required && !accepted.Any(a => a.Name == field.Name))
                rejected.Add(new(field.Name, null, null, RejectReason.Missing, "A required field has no grounded value."));

        return new ExtractResult(accepted, rejected);
    }

    internal static Dictionary<string, ExtractField> ValidateSchema(ExtractSchema schema, ExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Fields is null || schema.Fields.Count == 0)
            throw new ArgumentException("The schema names no fields.", nameof(schema));
        if (options.Threshold is < 0 or > 1)
            throw new ArgumentException("The fuzzy threshold must be between 0 and 1.", nameof(options));
        var byName = new Dictionary<string, ExtractField>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                throw new ArgumentException("Every field needs a name.", nameof(schema));
            if (!byName.TryAdd(field.Name, field))
                throw new ArgumentException($"Two fields are named '{field.Name}'.", nameof(schema));
            if (field.Type == FieldType.Enum && (field.Values is null || field.Values.Count == 0))
                throw new ArgumentException($"The enum field '{field.Name}' lists no values.", nameof(schema));
            if (field.Pattern is { } pattern)
            {
                try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"The pattern of '{field.Name}' is not a valid regular expression: {ex.Message}", nameof(schema));
                }
            }
        }
        return byName;
    }

    // --- values

    /// <summary>The value in canonical form, or null when it is not of the field's type.</summary>
    internal static string? Canonical(ExtractField field, string value)
    {
        var v = value.Trim();
        switch (field.Type)
        {
            case FieldType.String:
                if (field.Pattern is not { } pattern) return v;
                try
                {
                    return Regex.IsMatch(v, $"^(?:{pattern})$", RegexOptions.None, TimeSpan.FromSeconds(1)) ? v : null;
                }
                catch (RegexMatchTimeoutException)
                {
                    return null; // a pattern that cannot decide within a second does not accept the value
                }
            case FieldType.Number:
                return decimal.TryParse(v.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                    ? n.ToString(CultureInfo.InvariantCulture)
                    : null;
            case FieldType.Date:
                return DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null;
            case FieldType.Boolean:
                return bool.TryParse(v, out var b) ? (b ? "true" : "false") : null;
            case FieldType.Enum:
                return field.Values!.FirstOrDefault(allowed => string.Equals(allowed, v, StringComparison.OrdinalIgnoreCase));
            default:
                return null;
        }
    }

    private static string FormatProblem(ExtractField field) => field.Type switch
    {
        FieldType.String => "The value does not match the field's pattern.",
        FieldType.Number => "The value is not a number.",
        FieldType.Date => "The value is not a yyyy-MM-dd date.",
        FieldType.Boolean => "The value is not true or false.",
        FieldType.Enum => $"The value is not one of: {string.Join(", ", field.Values!)}.",
        _ => "The value is not of the field's type.",
    };

    /// <summary>Whether the quoted words carry the value — read by the code, not taken from the model.</summary>
    internal static bool Carries(ExtractField field, string value, string quote) => field.Type switch
    {
        FieldType.String => Normalize(quote).Contains(Normalize(value), StringComparison.Ordinal),
        FieldType.Number => NumbersIn(quote).Contains(decimal.Parse(value, CultureInfo.InvariantCulture)),
        FieldType.Date => DatesIn(quote).Contains(DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
        _ => true, // a boolean or enum is not read from the quote; the quote is the evidence a person checks
    };

    [GeneratedRegex(@"[-+]?\d{1,3}(?:,\d{3})+(?:\.\d+)?|[-+]?\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();

    internal static HashSet<decimal> NumbersIn(string quote)
    {
        var numbers = new HashSet<decimal>();
        foreach (Match m in NumberPattern().Matches(quote))
            if (decimal.TryParse(m.Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                numbers.Add(n);
        return numbers;
    }

    [GeneratedRegex(@"(?<y>\d{4})\s*[-./]\s*(?<m>\d{1,2})\s*[-./]\s*(?<d>\d{1,2})|(?<y>\d{4})\s*년\s*(?<m>\d{1,2})\s*월\s*(?<d>\d{1,2})\s*일")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"(?<mon>[A-Za-z]{3,9})\.?\s+(?<d>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<y>\d{4})|(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?<mon>[A-Za-z]{3,9})\.?,?\s+(?<y>\d{4})")]
    private static partial Regex NamedMonthDate();

    internal static HashSet<DateOnly> DatesIn(string quote)
    {
        var dates = new HashSet<DateOnly>();
        foreach (Match m in NumericDate().Matches(quote))
            Add(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture), m.Groups["d"].Value);
        foreach (Match m in NamedMonthDate().Matches(quote))
        {
            var month = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
                name => name.Length > 0 && m.Groups["mon"].Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)) + 1;
            if (month > 0) Add(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), month, m.Groups["d"].Value);
        }
        return dates;

        void Add(int year, int month, string day)
        {
            var d = int.Parse(day, CultureInfo.InvariantCulture);
            if (month is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(year, month)) dates.Add(new DateOnly(year, month, d));
        }
    }

    /// <summary>Unicode NFC, lower case, whitespace runs as one space, trimmed.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
