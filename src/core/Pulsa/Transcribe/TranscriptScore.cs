using System.Globalization;
using System.Text;
using PulsaTranscript;

namespace PulsaTranscribe;

/// <summary>
/// How far a transcript is from a reference: the edits (substitutions, deletions, insertions) that turn the reference
/// into the hypothesis, over the reference's length. <see cref="Rate"/> can exceed 1 when the hypothesis adds much.
/// </summary>
public sealed record ErrorRate(int Substitutions, int Deletions, int Insertions, int ReferenceLength)
{
    public int Errors => Substitutions + Deletions + Insertions;

    /// <summary>Errors over the reference length; 0 for two empty texts, and 1 per inserted unit when the reference is empty.</summary>
    public double Rate => ReferenceLength == 0 ? Errors : (double)Errors / ReferenceLength;
}

/// <summary>
/// The measures the transcription contract names: word error rate, and character error rate for languages written
/// without spaces between words (such as Korean). Both texts are normalized the same way first — Unicode NFC, lower
/// case, punctuation dropped, whitespace collapsed — so formatting is not counted as recognition error.
/// </summary>
public static class TranscriptScore
{
    public static ErrorRate WordErrorRate(string reference, string hypothesis) =>
        Score(Words(reference), Words(hypothesis));

    /// <summary>Over characters with spaces removed: spacing differences, common in Korean, are not errors.</summary>
    public static ErrorRate CharacterErrorRate(string reference, string hypothesis) =>
        Score(Characters(reference), Characters(hypothesis));

    /// <summary>The cues' words joined in time order — the transcript as a text to score.</summary>
    public static string Text(IEnumerable<TranscriptCue> cues) =>
        string.Join(' ', cues.OrderBy(c => c.Start).Select(c => c.Text));

    internal static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune)) sb.Append(' ');
            else if (Rune.IsLetterOrDigit(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                sb.Append(Rune.ToLowerInvariant(rune).ToString());
            else sb.Append(' '); // punctuation and symbols separate words but are not words
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] Words(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Characters(string text) =>
        Normalize(text).Replace(" ", string.Empty, StringComparison.Ordinal).EnumerateRunes().Select(r => r.ToString()).ToArray();

    /// <summary>Levenshtein alignment, counting each kind of edit on one optimal path.</summary>
    private static ErrorRate Score(string[] reference, string[] hypothesis)
    {
        var n = reference.Length;
        var m = hypothesis.Length;
        var cost = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) cost[i, 0] = i;
        for (var j = 0; j <= m; j++) cost[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var same = string.Equals(reference[i - 1], hypothesis[j - 1], StringComparison.Ordinal);
                cost[i, j] = Math.Min(cost[i - 1, j - 1] + (same ? 0 : 1), Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1));
            }

        int s = 0, d = 0, ins = 0;
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && cost[i, j] == cost[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1))
            {
                if (reference[i - 1] != hypothesis[j - 1]) s++;
                i--; j--;
            }
            else if (i > 0 && cost[i, j] == cost[i - 1, j] + 1) { d++; i--; }
            else { ins++; j--; }
        }
        return new ErrorRate(s, d, ins, n);
    }
}
