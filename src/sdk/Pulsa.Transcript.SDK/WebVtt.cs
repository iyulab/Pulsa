using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PulsaTranscript;

/// <summary>One transcript cue: a time span and the words spoken in it.</summary>
public sealed record TranscriptCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Minimal WebVTT reader/writer for transcripts: cue timings and text. Cue settings, styles, regions and
/// NOTE blocks are not preserved — a transcript file carries none of them.
/// </summary>
public static partial class WebVtt
{
    [GeneratedRegex(@"^\s*((?:\d+:)?\d{2}:\d{2}\.\d{3})\s+-->\s+((?:\d+:)?\d{2}:\d{2}\.\d{3})")]
    private static partial Regex TimingLine();

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
            cues.Add(new TranscriptCue(ParseTime(timing.Groups[1].Value), ParseTime(timing.Groups[2].Value), string.Join('\n', body)));
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
              .Append(cue.Text).Append('\n');
        return sb.ToString();
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
