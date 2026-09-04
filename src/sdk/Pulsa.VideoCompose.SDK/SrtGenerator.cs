using System.Globalization;
using System.Text;

namespace PulsaVideoCompose;

/// <summary>
/// Builds SubRip (.srt) subtitle content from a flat list of scene captions, one caption per
/// scene, each scene occupying an equal, fixed duration in sequence starting at zero.
/// </summary>
public static class SrtGenerator
{
    public static string Generate(IReadOnlyList<string> captions, double sceneDurationSeconds)
    {
        if (captions.Count == 0)
            throw new ArgumentException("At least one caption is required.", nameof(captions));
        if (sceneDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sceneDurationSeconds), "Scene duration must be positive.");

        var sb = new StringBuilder();
        for (var i = 0; i < captions.Count; i++)
        {
            var start = TimeSpan.FromSeconds(i * sceneDurationSeconds);
            var end = TimeSpan.FromSeconds((i + 1) * sceneDurationSeconds);
            sb.Append(i + 1).Append("\r\n");
            sb.Append(FormatTimestamp(start)).Append(" --> ").Append(FormatTimestamp(end)).Append("\r\n");
            sb.Append(captions[i]).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string FormatTimestamp(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}");
}
