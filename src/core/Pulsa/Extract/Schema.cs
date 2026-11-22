namespace PulsaExtract;

/// <summary>The kind of value a field holds. Values are carried as text in a canonical form (see <see cref="ExtractedValue"/>).</summary>
public enum FieldType { String, Number, Date, Boolean, Enum }

/// <summary>One field the schema names.</summary>
/// <param name="Name">Unique within the schema.</param>
/// <param name="Type">The kind of value.</param>
public sealed record ExtractField(string Name, FieldType Type)
{
    /// <summary>A required field without a grounded value is reported as <see cref="RejectReason.Missing"/>.</summary>
    public bool Required { get; init; }

    /// <summary>The field may hold several values.</summary>
    public bool Many { get; init; }

    /// <summary>For <see cref="FieldType.Enum"/>: the allowed values.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>For <see cref="FieldType.String"/>: a regular expression the value must match in full.</summary>
    public string? Pattern { get; init; }

    /// <summary>Told to the model and used nowhere else.</summary>
    public string? Description { get; init; }
}

/// <summary>The fields to extract.</summary>
public sealed record ExtractSchema(IReadOnlyList<ExtractField> Fields);

/// <summary>A value someone proposes for a field — a model, a structured-output call, a person — with the words it was read from.</summary>
public sealed record Candidate(string Name, string? Value, string? Quote);

/// <summary>How a quote was found in the text.</summary>
public enum MatchKind
{
    /// <summary>Character for character.</summary>
    Exact,

    /// <summary>Equal once both are normalized (Unicode NFC, case, whitespace).</summary>
    Normalized,

    /// <summary>Close but not equal — only when <see cref="ExtractOptions.Fuzzy"/> is on.</summary>
    Fuzzy,
}

/// <summary>Where a quote is in the text: offsets counting Unicode scalar values from the start, end exclusive.</summary>
public readonly record struct TextSpan(int Start, int End);

/// <summary>
/// A grounded value. <see cref="Value"/> is canonical: a number in invariant form (<c>1234.5</c>), a date as
/// <c>yyyy-MM-dd</c>, a boolean as <c>true</c>/<c>false</c>, an enum as written in the schema, a string as proposed.
/// </summary>
public sealed record ExtractedValue(string Name, string Value, string Quote, TextSpan Span, MatchKind Match);

/// <summary>Why a value was not returned.</summary>
public enum RejectReason
{
    /// <summary>A required field has no grounded value.</summary>
    Missing,

    /// <summary>The value came without the words it was read from.</summary>
    NoQuote,

    /// <summary>The quote is not in the text.</summary>
    NotInText,

    /// <summary>The quote is in the text but does not carry the value.</summary>
    NotSupported,

    /// <summary>Not of the field's type, not matching its pattern, not one of its values, or one value too many.</summary>
    Format,

    /// <summary>A field the schema does not name.</summary>
    NotInSchema,
}

/// <summary>A value that was not returned, and why.</summary>
public sealed record RejectedValue(string Name, string? Value, string? Quote, RejectReason Reason, string Detail);

/// <summary>The grounded values, and everything refused.</summary>
public sealed record ExtractResult(IReadOnlyList<ExtractedValue> Fields, IReadOnlyList<RejectedValue> Rejected);

/// <summary>How lenient quote matching is.</summary>
public sealed record ExtractOptions
{
    /// <summary>Accept a quote close to the text but not equal to it. Off by default: a fuzzy quote is the one a person most needs to check.</summary>
    public bool Fuzzy { get; init; }

    /// <summary>For fuzzy matching: the share of the quote's words that must be found, in order, in a stretch of the text. Default 0.75.</summary>
    public double Threshold { get; init; } = 0.75;
}
