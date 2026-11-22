using System.Globalization;
using System.Text;

namespace PulsaVideoCompose;

/// <summary>
/// Builds SubRip (.srt) subtitle content from a flat list of scene captions, one caption per
/// scene, each scene occupying an equal, fixed duration in sequence starting at zero.
/// </summary>
public static class SrtGenerator
{
    /// <param name="startOffsetSeconds">Shifts every cue's start/end forward by this much — for a
    /// scene block that doesn't begin at the start of the final concatenated video (e.g. body
    /// captions following a title card).</param>
    /// <param name="startIndex">The cue number the first caption is numbered — for a scene block
    /// that isn't the first one in the final concatenated .srt (SubRip cue numbers must stay
    /// sequential across the whole file).</param>
    public static string Generate(
        IReadOnlyList<string> captions, double sceneDurationSeconds,
        double startOffsetSeconds = 0, int startIndex = 1)
    {
        if (captions.Count == 0)
            throw new ArgumentException("At least one caption is required.", nameof(captions));
        if (sceneDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sceneDurationSeconds), "Scene duration must be positive.");

        var sb = new StringBuilder();
        for (var i = 0; i < captions.Count; i++)
        {
            var start = TimeSpan.FromSeconds(startOffsetSeconds + i * sceneDurationSeconds);
            var end = TimeSpan.FromSeconds(startOffsetSeconds + (i + 1) * sceneDurationSeconds);
            sb.Append(startIndex + i).Append("\r\n");
            sb.Append(FormatTimestamp(start)).Append(" --> ").Append(FormatTimestamp(end)).Append("\r\n");
            sb.Append(captions[i]).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string FormatTimestamp(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}");
}
