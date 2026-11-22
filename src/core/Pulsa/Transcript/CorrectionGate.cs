namespace PulsaTranscript;

/// <summary>
/// Decides whether a proposed correction of one cue may be applied. A speech-recognition error sounds like
/// the words that were said, so a correction must too: the cue is diffed word by word, and every changed
/// stretch has to be close to what it replaces — compared letter by letter, with Hangul syllables split into
/// their jamo so <c>김인수 → 김민수</c> counts as the one-letter slip it is. Re-spacing alone always passes.
/// Words that appear from nothing (an insertion), vanish (a deletion) or replace a stretch they do not sound
/// like are rejected — that is what a model pulled in by a glossary or by its own guess looks like.
/// </summary>
public sealed class CorrectionGate
{
    /// <summary>Minimum similarity (0–1) between a changed stretch and its replacement. Default 0.6.</summary>
    public double MinimumSimilarity { get; init; } = 0.6;

    public bool Accepts(string original, string refined)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(refined);
        if (string.Equals(original, refined, StringComparison.Ordinal)) return true;
        if (string.Equals(Squash(original), Squash(refined), StringComparison.Ordinal)) return true;

        var a = Words(original);
        var b = Words(refined);
        foreach (var (from, to) in ChangedStretches(a, b))
        {
            var left = Squash(string.Concat(from));
            var right = Squash(string.Concat(to));
            if (left.Length == 0 || right.Length == 0) return false;
            if (Similarity(left, right) < MinimumSimilarity) return false;
        }
        return true;
    }

    private static string[] Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string Squash(string text) =>
        string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    /// <summary>The stretches between the words the two versions share (longest common subsequence).</summary>
    private static IEnumerable<(string[] From, string[] To)> ChangedStretches(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0, startX = 0, startY = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                if (x > startX || y > startY) yield return (a[startX..x], b[startY..y]);
                x++; y++;
                startX = x; startY = y;
            }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) y++;
            else x++;
        }
        if (x > startX || y > startY) yield return (a[startX..x], b[startY..y]);
    }

    private static double Similarity(string left, string right)
    {
        var p = Letters(left);
        var q = Letters(right);
        var longest = Math.Max(p.Count, q.Count);
        return longest == 0 ? 1 : 1 - (double)Levenshtein(p, q) / longest;
    }

    /// <summary>Letters to compare: a Hangul syllable becomes its lead, vowel and (if any) tail jamo.</summary>
    private static List<int> Letters(string text)
    {
        var letters = new List<int>(text.Length * 2);
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣')
            {
                var index = c - 0xAC00;
                letters.Add(0x1100 + index / 588);
                letters.Add(0x1161 + index % 588 / 28);
                if (index % 28 != 0) letters.Add(0x11A7 + index % 28);
            }
            else letters.Add(c);
        }
        return letters;
    }

    private static int Levenshtein(List<int> p, List<int> q)
    {
        var previous = new int[q.Count + 1];
        var current = new int[q.Count + 1];
        for (var j = 0; j <= q.Count; j++) previous[j] = j;
        for (var i = 1; i <= p.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= q.Count; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (p[i - 1] == q[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[q.Count];
    }
}
