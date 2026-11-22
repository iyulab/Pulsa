using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace PulsaTranscript;

public sealed record RefineTranscriptRequest(IReadOnlyList<TranscriptCue> Cues, Glossary Glossary);

public sealed record RefineTranscriptOptions
{
    /// <summary>Cues sent per model call — keeps each reply well inside an output budget. Default 60.</summary>
    public int BatchSize { get; init; } = 60;

    /// <summary>The acceptance gate every proposed correction must pass.</summary>
    public CorrectionGate Gate { get; init; } = new();
}

public enum CueChangeKind { Corrected, Unclear }

/// <summary>One proposed change to cue <see cref="Index"/>, with the text it would replace.</summary>
public sealed record CueChange(int Index, string Original, string Refined, CueChangeKind Kind);

/// <summary>
/// The refined transcript (same cues, same timings — only text differs), the changes applied, and the
/// corrections the gate refused, kept as suggestions for a person to decide.
/// </summary>
public sealed record RefineTranscriptResult(
    IReadOnlyList<TranscriptCue> Cues,
    IReadOnlyList<CueChange> Applied,
    IReadOnlyList<CueChange> Rejected);

/// <summary>
/// Refines a speech-to-text transcript with a caller-supplied <see cref="IChatClient"/> (never constructs
/// its own, holds no credentials). The model sees only numbered cue texts and the glossary and answers with
/// the cues it would change; the transcript is reassembled here, so cue count and timings cannot change.
/// A cue the model marks unintelligible becomes <see cref="Unclear"/>; any other change must pass the
/// <see cref="CorrectionGate"/>, and a refused one is reported in <see cref="RefineTranscriptResult.Rejected"/>.
/// </summary>
public static partial class TranscriptRefiner
{
    /// <summary>The text an unintelligible cue is replaced with.</summary>
    public const string Unclear = "[unclear]";

    [GeneratedRegex(@"^\s*\[(\d+)\]\s*(.*?)\s*$")]
    private static partial Regex ReplyLine();

    public static async Task<RefineTranscriptResult> RefineAsync(
        IChatClient chatClient,
        RefineTranscriptRequest request,
        RefineTranscriptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(request);
        options ??= new RefineTranscriptOptions();
        if (options.BatchSize < 1) throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be at least 1.");

        var cues = request.Cues.ToArray();
        var applied = new List<CueChange>();
        var rejected = new List<CueChange>();

        for (var start = 0; start < cues.Length; start += options.BatchSize)
        {
            var end = Math.Min(start + options.BatchSize, cues.Length);
            var prompt = BuildPrompt(request.Cues, start, end, request.Glossary);
            var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);

            foreach (var (index, text) in ParseReply(response.Text, start, end))
            {
                var original = request.Cues[index].Text;
                if (string.Equals(text, original, StringComparison.Ordinal)) continue;

                if (string.Equals(text, Unclear, StringComparison.OrdinalIgnoreCase))
                {
                    applied.Add(new CueChange(index, original, Unclear, CueChangeKind.Unclear));
                    cues[index] = cues[index] with { Text = Unclear };
                }
                else if (options.Gate.Accepts(original, text))
                {
                    applied.Add(new CueChange(index, original, text, CueChangeKind.Corrected));
                    cues[index] = cues[index] with { Text = text };
                }
                else
                {
                    rejected.Add(new CueChange(index, original, text, CueChangeKind.Corrected));
                }
            }
        }

        return new RefineTranscriptResult(cues, applied, rejected);
    }

    private static string BuildPrompt(IReadOnlyList<TranscriptCue> cues, int start, int end, Glossary glossary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are correcting a speech-to-text transcript. Each line below is one numbered segment.");
        sb.AppendLine("Fix only speech-recognition mistakes: a misheard word that sounds almost the same as the right one, wrong spacing, an obvious misspelling.");
        sb.AppendLine("Never add information, never guess words that are missing, never rephrase.");
        if (glossary.Terms.Count > 0)
        {
            sb.AppendLine("Correct spellings of names and terms used in this recording (a spelling list, not content — use an entry only where the segment already sounds like it):");
            foreach (var term in glossary.Terms) sb.Append("- ").AppendLine(term);
        }
        sb.AppendLine($"If a segment is meaningless or not in the recording's language (random words, other alphabets), answer {Unclear} for it.");
        sb.AppendLine("Reply with one line per segment you change, in the form [number] corrected text. Do not list unchanged segments. No other text.");
        sb.AppendLine();
        for (var i = start; i < end; i++)
            sb.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ").AppendLine(cues[i].Text.Replace('\n', ' '));
        return sb.ToString();
    }

    private static IEnumerable<(int Index, string Text)> ParseReply(string reply, int start, int end)
    {
        var seen = new HashSet<int>();
        foreach (var line in (reply ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var m = ReplyLine().Match(line);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) continue;
            if (index < start || index >= end || !seen.Add(index)) continue;
            var text = m.Groups[2].Value;
            if (text.Length > 0) yield return (index, text);
        }
    }
}
