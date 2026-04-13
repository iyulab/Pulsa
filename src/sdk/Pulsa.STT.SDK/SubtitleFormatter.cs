using System.Text;
using LMSupply.Transcriber;

namespace PulsaSTT;

public static class SubtitleFormatter
{
    public static string Format(TranscriptionResult result, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "vtt" => FormatVtt(result.Segments),
            "srt" => FormatSrt(result.Segments),
            _ => result.Text,
        };
    }

    public static string GetFileExtension(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "vtt" => ".vtt",
            "srt" => ".srt",
            _ => ".txt",
        };
    }

    private static string FormatVtt(IReadOnlyList<TranscriptionSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            sb.AppendLine($"{FormatVttTime(seg.Start)} --> {FormatVttTime(seg.End)}");
            sb.AppendLine(seg.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSrt(IReadOnlyList<TranscriptionSegment> segments)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine($"{FormatSrtTime(seg.Start)} --> {FormatSrtTime(seg.End)}");
            sb.AppendLine(seg.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatVttTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }
}
