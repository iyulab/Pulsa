using System.Globalization;
using System.Text;

namespace PulsaExtract;

/// <summary>Finds a quote in the text: exactly, then normalized, then (when asked) fuzzily — and reports where in the original text.</summary>
internal sealed class Locator
{
    private readonly string _text;
    private readonly string _normalized;
    private readonly int[] _startOf; // normalized char index → original UTF-16 index where it came from
    private readonly int[] _endOf;   // normalized char index → original UTF-16 index just after it

    public Locator(string text)
    {
        _text = text;
        (_normalized, _startOf, _endOf) = BuildNormalized(text);
    }

    public readonly record struct Found(int Start, int End, MatchKind Match);

    /// <summary>The quote's first occurrence at or after <paramref name="from"/> (UTF-16 index), else its first occurrence anywhere.</summary>
    public Found? Find(string quote, int from, ExtractOptions options)
    {
        var q = quote.Trim();
        var exact = IndexFrom(_text, q, from, StringComparison.Ordinal);
        if (exact >= 0) return new Found(exact, exact + q.Length, MatchKind.Exact);

        var nq = Extractor.Normalize(q);
        if (nq.Length == 0) return null;
        var normalizedFrom = Array.FindIndex(_startOf, i => i >= from);
        var n = IndexFrom(_normalized, nq, normalizedFrom < 0 ? _normalized.Length : normalizedFrom, StringComparison.Ordinal);
        if (n >= 0) return new Found(_startOf[n], _endOf[n + nq.Length - 1], MatchKind.Normalized);

        return options.Fuzzy ? FindFuzzy(nq, options.Threshold) : null;
    }

    /// <summary>UTF-16 indexes → offsets counting Unicode scalar values (a surrogate pair is one).</summary>
    public TextSpan ScalarSpan(int start, int end) => new(Scalars(start), Scalars(end));

    private int Scalars(int utf16Index)
    {
        var count = 0;
        for (var i = 0; i < utf16Index; i++)
            if (!char.IsLowSurrogate(_text[i])) count++;
        return count;
    }

    private static int IndexFrom(string haystack, string needle, int from, StringComparison comparison)
    {
        var at = from <= haystack.Length ? haystack.IndexOf(needle, from, comparison) : -1;
        return at >= 0 ? at : haystack.IndexOf(needle, comparison);
    }

    /// <summary>
    /// The stretch of the text's words, as long as the quote's, sharing the most words with it in order; found when that
    /// share is at least <paramref name="threshold"/>.
    /// </summary>
    private Found? FindFuzzy(string normalizedQuote, double threshold)
    {
        var quoteWords = normalizedQuote.Split(' ').Select(w => w[EdgeBounds(w)]).Where(w => w.Length > 0).ToArray();
        var words = Words();
        if (quoteWords.Length == 0 || words.Count == 0) return null;
        var width = Math.Min(quoteWords.Length, words.Count);
        (int First, double Score)? best = null;
        for (var first = 0; first + width <= words.Count; first++)
        {
            var window = words.Skip(first).Take(width).Select(w => w.Word).ToArray();
            var score = (double)CommonInOrder(quoteWords, window) / quoteWords.Length;
            if (best is null || score > best.Value.Score) best = (first, score);
        }
        if (best is not { } b || b.Score < threshold) return null;
        // A window as wide as the quote can carry a stray word at either end when the quote left one out; the span
        // runs from the first to the last word the two share.
        var shared = quoteWords.ToHashSet(StringComparer.Ordinal);
        var firstShared = b.First;
        var lastShared = b.First + width - 1;
        while (firstShared < lastShared && !shared.Contains(words[firstShared].Word)) firstShared++;
        while (lastShared > firstShared && !shared.Contains(words[lastShared].Word)) lastShared--;
        var startWord = words[firstShared];
        var endWord = words[lastShared];
        return new Found(_startOf[startWord.Start], _endOf[endWord.End - 1], MatchKind.Fuzzy);
    }

    private List<(string Word, int Start, int End)> Words()
    {
        var words = new List<(string, int, int)>();
        var start = 0;
        for (var i = 0; i <= _normalized.Length; i++)
        {
            if (i == _normalized.Length || _normalized[i] == ' ')
            {
                if (i > start)
                {
                    // Punctuation at the edges of a word ("due:", "won.") is not part of what is compared, nor of the span.
                    var word = _normalized[start..i];
                    var bounds = EdgeBounds(word);
                    var (offset, length) = bounds.GetOffsetAndLength(word.Length);
                    if (length > 0) words.Add((word.Substring(offset, length), start + offset, start + offset + length));
                }
                start = i + 1;
            }
        }
        return words;
    }

    /// <summary>The part of a word between its leading and trailing punctuation or symbols (none left: an empty range).</summary>
    private static Range EdgeBounds(string word)
    {
        var start = 0;
        var end = word.Length;
        while (start < end && IsEdge(word[start])) start++;
        while (end > start && IsEdge(word[end - 1])) end--;
        return start..end;
    }

    private static bool IsEdge(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    private static int CommonInOrder(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
        return table[a.Length, b.Length];
    }

    /// <summary>The text normalized as <see cref="Extractor.Normalize"/> does, with each normalized char mapped to the original stretch it came from.</summary>
    private static (string, int[], int[]) BuildNormalized(string text)
    {
        var sb = new StringBuilder(text.Length);
        var starts = new List<int>(text.Length);
        var ends = new List<int>(text.Length);
        var pendingSpace = -1;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var at = elements.ElementIndex;
            if (element.All(char.IsWhiteSpace))
            {
                if (sb.Length > 0 && pendingSpace < 0) pendingSpace = at;
                continue;
            }
            if (pendingSpace >= 0)
            {
                sb.Append(' ');
                starts.Add(pendingSpace);
                ends.Add(at);
                pendingSpace = -1;
            }
            // Each text element is normalized on its own, so every normalized char maps back to one element.
            foreach (var c in element.Normalize(NormalizationForm.FormC))
            {
                sb.Append(char.ToLowerInvariant(c));
                starts.Add(at);
                ends.Add(at + element.Length);
            }
        }
        return (sb.ToString(), starts.ToArray(), ends.ToArray());
    }
}
