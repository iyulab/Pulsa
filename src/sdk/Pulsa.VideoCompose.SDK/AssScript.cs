using System.Globalization;
using System.Text;

namespace PulsaVideoCompose;

/// <summary>Where burned-in text sits in the frame.</summary>
public enum CaptionPlacement
{
    /// <summary>Bottom centre, above <see cref="CaptionStyle.BottomMarginRatio"/>.</summary>
    Bottom,

    /// <summary>The middle of the frame — a title or outro card.</summary>
    Center,
}

/// <summary>
/// Builds an Advanced SubStation Alpha (.ass) script for burning text into a video: one cue per scene, each scene an equal,
/// fixed duration in sequence. The script's coordinate space is the output frame itself (<c>PlayResX</c>/<c>PlayResY</c> =
/// the frame's width and height), so every size in <see cref="CaptionStyle"/> is a size in the rendered video.
/// </summary>
/// <remarks>
/// Why not a SubRip file with <c>force_style</c>: ffmpeg's <c>subtitles</c> filter converts SubRip into a script whose
/// coordinate space is 384×288, so a font size written in video pixels is read in that space — 72 "pixels" become a quarter of
/// the frame height — and that conversion uses the legacy alignment numbering. Writing the script directly removes both.
/// The band behind the text is the opaque-box border style, which is painted with the outline colour (the back colour is the
/// shadow's), so <see cref="CaptionStyle.BoxColor"/> goes there.
/// </remarks>
public static class AssScript
{
    public static string Generate(
        IReadOnlyList<string> captions, double sceneDurationSeconds, CaptionStyle style, CaptionPlacement placement,
        int frameWidth, int frameHeight, double startOffsetSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (captions.Count == 0)
            throw new ArgumentException("At least one caption is required.", nameof(captions));
        if (sceneDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sceneDurationSeconds), "Scene duration must be positive.");
        if (frameWidth <= 0 || frameHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "The frame size must be positive.");
        style.Validate();

        var fontSize = Math.Max(1, (int)Math.Round(style.FontSizeRatio * frameHeight));
        var boxPadding = Math.Max(1, (int)Math.Round(fontSize / 16.0));
        var sideMargin = (int)Math.Round(frameWidth * 10.0 / 384);
        var bottomMargin = (int)Math.Round(style.BottomMarginRatio * frameHeight);
        // Alignment uses the numpad layout: 2 = bottom centre, 5 = middle centre.
        var alignment = placement == CaptionPlacement.Center ? 5 : 2;
        var text = CaptionStyle.ToAssColour(style.TextColor, nameof(style.TextColor));
        var box = CaptionStyle.ToAssColour(style.BoxColor, nameof(style.BoxColor));

        var sb = new StringBuilder();
        sb.Append("[Script Info]\r\n");
        sb.Append("ScriptType: v4.00+\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"PlayResX: {frameWidth}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"PlayResY: {frameHeight}\r\n");
        sb.Append("WrapStyle: 0\r\n");
        sb.Append("ScaledBorderAndShadow: yes\r\n");
        sb.Append("\r\n");
        sb.Append("[V4+ Styles]\r\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, ");
        sb.Append("Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"Style: Default,{style.FontName},{fontSize},{text},{text},{box},&H00000000,{(style.Bold ? -1 : 0)},0,0,0,100,100,0,0,3,{boxPadding},0,{alignment},{sideMargin},{sideMargin},{bottomMargin},1\r\n");
        sb.Append("\r\n");
        sb.Append("[Events]\r\n");
        sb.Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");
        for (var i = 0; i < captions.Count; i++)
        {
            var start = TimeSpan.FromSeconds(startOffsetSeconds + i * sceneDurationSeconds);
            var end = TimeSpan.FromSeconds(startOffsetSeconds + (i + 1) * sceneDurationSeconds);
            sb.Append(CultureInfo.InvariantCulture,
                $"Dialogue: 0,{FormatTimestamp(start)},{FormatTimestamp(end)},Default,,0,0,0,,{EscapeText(captions[i])}\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Caption text as it should read: a line break becomes the script's <c>\N</c>, braces (which would open an override
    /// block) are escaped, and a backslash that would form an escape with the next character is kept literal by a word joiner
    /// (U+2060, zero width) between the two.
    /// </summary>
    internal static string EscapeText(string caption)
    {
        var normalized = caption.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(normalized.Length + 8);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            switch (c)
            {
                case '\n':
                    sb.Append("\\N");
                    break;
                case '{':
                    sb.Append("\\{");
                    break;
                case '}':
                    sb.Append("\\}");
                    break;
                case '\\':
                    sb.Append('\\');
                    if (i + 1 < normalized.Length && normalized[i + 1] is 'n' or 'N' or 'h' or '{' or '}')
                        sb.Append('⁠');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // H:MM:SS.cc — centiseconds, rounded to the nearest.
    private static string FormatTimestamp(TimeSpan t)
    {
        var centiseconds = (long)Math.Round(t.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);
        var hours = centiseconds / 360_000;
        var minutes = centiseconds / 6_000 % 60;
        var seconds = centiseconds / 100 % 60;
        var cs = centiseconds % 100;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:D2}:{seconds:D2}.{cs:D2}");
    }
}
