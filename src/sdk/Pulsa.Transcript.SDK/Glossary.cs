namespace PulsaTranscript;

/// <summary>
/// The spellings a transcript should use — names, titles, team terms. A spelling list, not a source of
/// content: the refiner may only move a misheard word <i>toward</i> an entry it already sounds like.
/// </summary>
public sealed record Glossary(IReadOnlyList<string> Terms)
{
    public static Glossary Empty { get; } = new([]);

    /// <summary>
    /// Reads the list items of a markdown document (<c>- term</c> / <c>* term</c>). A note after a dash
    /// (<c>- 김민수 — 팀장</c>, <c>- 이서연 - 대리</c>) is dropped; headings and plain lines are ignored.
    /// </summary>
    public static Glossary FromMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var terms = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!(line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))) continue;
            var item = line[2..];
            foreach (var separator in new[] { " — ", " – ", " - " })
            {
                var at = item.IndexOf(separator, StringComparison.Ordinal);
                if (at > 0) { item = item[..at]; break; }
            }
            item = item.Trim();
            if (item.Length > 0) terms.Add(item);
        }
        return new Glossary(terms);
    }
}
