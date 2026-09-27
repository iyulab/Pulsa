using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PulsaTranscript;

/// <summary>
/// One transcript cue: a time span, the words spoken in it, and — when the transcript names voices — who spoke them.
/// <see cref="Speaker"/> is the WebVTT voice span's annotation (<c>&lt;v Speaker 1&gt;</c>); it is kept apart from
/// <see cref="Text"/> so a step that edits words never sees the markup as words, and it is written back unchanged.
/// </summary>
public sealed record TranscriptCue(TimeSpan Start, TimeSpan End, string Text, string? Speaker = null);

/// <summary>
/// Minimal WebVTT reader/writer for transcripts: cue timings and text. Cue settings, styles, regions and
/// NOTE blocks are not preserved — a transcript file carries none of them.
/// </summary>
public static partial class WebVtt
{
    [GeneratedRegex(@"^\s*((?:\d+:)?\d{2}:\d{2}\.\d{3})\s+-->\s+((?:\d+:)?\d{2}:\d{2}\.\d{3})")]
    private static partial Regex TimingLine();

    // A cue that one voice speaks: `<v Name>words`, the closing `</v>` optional (WebVTT allows it to be omitted at the
    // end of the cue). Class names on the tag (`<v.loud Name>`) are allowed and dropped — a transcript carries none.
    [GeneratedRegex(@"^<v(?:\.[^\s>]+)*\s+([^>]+)>(.*?)(?:</v>)?$", RegexOptions.Singleline)]
    private static partial Regex VoiceSpan();

    public static IReadOnlyList<TranscriptCue> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || !lines[0].TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal))
            throw new FormatException("Not a WebVTT file: the first line must start with WEBVTT.");

        var cues = new List<TranscriptCue>();
        for (var i = 1; i < lines.Length; i++)
        {
            var timing = TimingLine().Match(lines[i]);
            if (!timing.Success) continue;
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length && lines[j].Length > 0; j++) body.Add(lines[j]);
            var (speaker, words) = SplitVoice(string.Join('\n', body));
            cues.Add(new TranscriptCue(ParseTime(timing.Groups[1].Value), ParseTime(timing.Groups[2].Value), words, speaker));
            i = j;
        }
        return cues;
    }

    public static string Write(IReadOnlyList<TranscriptCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var sb = new StringBuilder("WEBVTT\n");
        foreach (var cue in cues)
            sb.Append('\n').Append(FormatTime(cue.Start)).Append(" --> ").Append(FormatTime(cue.End)).Append('\n')
              .Append(string.IsNullOrWhiteSpace(cue.Speaker) ? cue.Text : $"<v {cue.Speaker.Trim()}>{cue.Text}").Append('\n');
        return sb.ToString();
    }

    /// <summary>A body that is one voice span → its speaker and words; anything else → no speaker, the body as is.</summary>
    private static (string? Speaker, string Words) SplitVoice(string body)
    {
        var voice = VoiceSpan().Match(body);
        if (!voice.Success) return (null, body);
        var words = voice.Groups[2].Value;
        // A second voice inside the cue is not "one speaker" — leave such a body untouched rather than half-split it.
        if (words.Contains("<v", StringComparison.Ordinal)) return (null, body);
        return (voice.Groups[1].Value.Trim(), words);
    }

    private static TimeSpan ParseTime(string value)
    {
        var parts = value.Split(':');
        var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var seconds = decimal.Parse(parts[^1], CultureInfo.InvariantCulture);
        return new TimeSpan(hours, minutes, 0) + TimeSpan.FromMilliseconds((double)(seconds * 1000m));
    }

    private static string FormatTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}");
}
