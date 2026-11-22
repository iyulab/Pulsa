using System.Globalization;
using System.Text.RegularExpressions;

namespace PulsaVideoCompose;

/// <summary>
/// A font a style named that the renderer could not find, so the text was drawn with another one.
/// </summary>
/// <param name="RequestedFamily">The family the style named (<see cref="CaptionStyle.FontName"/>).</param>
/// <param name="Bold">Whether the style asked for bold text.</param>
/// <param name="UsedFont">The PostScript name of the font the text was drawn with instead.</param>
public sealed record FontSubstitution(string RequestedFamily, bool Bold, string UsedFont);

/// <summary>
/// Reads the subtitle renderer's font choices from ffmpeg's log and tells a font that was found from one that was
/// substituted. The renderer (libass) logs one line per font it opens — <c>fontselect: (family, weight, italic) -&gt;
/// path, index, postscript-name</c> — and the same line whether the family was found or a system fallback was taken
/// instead, so a missing font cannot be read from the line alone.
/// </summary>
internal static partial class FontSelectionLog
{
    /// <summary>A family no font can carry, used to learn which font the renderer falls back to.</summary>
    internal const string ProbeFamily = "Pulsa Missing Font Probe 2f6b9c";

    [GeneratedRegex(@"fontselect: \((?<family>.*), (?<weight>\d+), (?<italic>\d+)\) -> .*, -?\d+, (?<postscript>[^,]*)$")]
    private static partial Regex SelectionLine();

    [GeneratedRegex(@"Glyph 0x[0-9A-Fa-f]+ not found, selecting one more font for \((?<family>.*), (?<weight>\d+), (?<italic>\d+)\)")]
    private static partial Regex GlyphFallbackLine();

    /// <summary>
    /// The font opened for each family a run asked for. A character the font lacks makes the renderer open one more
    /// font for that family (announced by a «Glyph … not found» line) — those are fallbacks for single characters, not
    /// the family's own font, and are skipped.
    /// </summary>
    internal static IReadOnlyList<FontSelection> PrimarySelections(IEnumerable<string> logLines)
    {
        var selections = new List<FontSelection>();
        var seen = new HashSet<(string, int, bool)>();
        var glyphFallbackPending = new HashSet<(string, int, bool)>();
        foreach (var line in logLines)
        {
            if (GlyphFallbackLine().Match(line) is { Success: true } glyph)
            {
                glyphFallbackPending.Add(Key(glyph));
                continue;
            }
            if (SelectionLine().Match(line) is not { Success: true } selection)
                continue;

            var key = Key(selection);
            if (glyphFallbackPending.Remove(key) || !seen.Add(key))
                continue;
            selections.Add(new FontSelection(key.Item1, key.Item2, key.Item3, selection.Groups["postscript"].Value.Trim()));
        }
        return selections;
    }

    /// <summary>
    /// Whether a font's PostScript name carries the family that was asked for («Noto Sans KR» → «NotoSansKR-Bold»).
    /// A miss does not prove a substitution: a family can be asked for by a localised name its PostScript name does
    /// not spell.
    /// </summary>
    internal static bool NameCarriesFamily(string family, string postScriptName)
    {
        var wanted = Normalise(family);
        return wanted.Length > 0 && Normalise(postScriptName).StartsWith(wanted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The substitutions among <paramref name="selections"/>: a family whose font does not carry its name and is the
    /// very font the renderer falls back to for a family that does not exist (<paramref name="fallbackFor"/>, keyed
    /// by weight and italic). Both conditions are needed — a family named in another language passes the second, and
    /// a family that is itself the fallback font (Arial on many systems) passes the first.
    /// </summary>
    internal static IReadOnlyList<FontSubstitution> Substitutions(
        IEnumerable<FontSelection> selections, IReadOnlyDictionary<(int Weight, bool Italic), string> fallbackFor) =>
        selections
            .Where(s => s.Family != ProbeFamily
                && !NameCarriesFamily(s.Family, s.PostScriptName)
                && fallbackFor.TryGetValue((s.Weight, s.Italic), out var fallback)
                && string.Equals(fallback, s.PostScriptName, StringComparison.Ordinal))
            .Select(s => new FontSubstitution(s.Family, s.Weight >= BoldWeight, s.PostScriptName))
            .Distinct()
            .ToList();

    internal const int BoldWeight = 700;

    private static (string, int, bool) Key(Match match) =>
        (match.Groups["family"].Value,
         int.Parse(match.Groups["weight"].Value, CultureInfo.InvariantCulture),
         match.Groups["italic"].Value != "0");

    private static string Normalise(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>One font the renderer opened for a family: the request (family, weight, italic) and what it opened.</summary>
internal sealed record FontSelection(string Family, int Weight, bool Italic, string PostScriptName);
