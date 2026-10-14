using System.Globalization;

namespace PulsaVideoCompose;

/// <summary>
/// How burned-in text looks: the font, its size, the text colour and the band drawn behind it. Sizes are fractions of the
/// output frame's height, so one style reads the same at 16:9 and 9:16. Where the text sits (captions at the bottom, a
/// title/outro card in the middle) is the composer's, not the style's.
/// </summary>
/// <param name="FontName">A font installed where ffmpeg runs. Characters the font lacks are drawn from the system's
/// fallback fonts — name a font that covers the captions' script (for example a CJK font for Korean or Japanese) to choose
/// how they look. Must not contain a comma.</param>
/// <param name="FontSizeRatio">The font size as a fraction of the frame height (0.05 = 54 px on a 1080-pixel-high frame).</param>
/// <param name="Bold">Draws the text bold.</param>
/// <param name="TextColor">The text colour, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</param>
/// <param name="BoxColor">The band behind the text, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (<c>AA</c> = opacity, <c>00</c> is
/// invisible). The band keeps text legible over whatever the picture holds near the frame edge.</param>
/// <param name="BottomMarginRatio">For captions: the gap between the band and the bottom edge, as a fraction of the frame
/// height. Not used for a card, which is centred.</param>
public sealed record CaptionStyle(
    string FontName = "Arial",
    double FontSizeRatio = 16.0 / 288,
    bool Bold = false,
    string TextColor = "#FFFFFF",
    string BoxColor = "#00000080",
    double BottomMarginRatio = 10.0 / 288)
{
    /// <summary>Scene captions: white text on a translucent black band near the bottom.</summary>
    public static CaptionStyle DefaultCaptions { get; } = new();

    /// <summary>Title and outro cards: larger bold text on the same band, centred.</summary>
    public static CaptionStyle DefaultCards { get; } = new(FontSizeRatio: 1.0 / 15, Bold: true);

    /// <summary>Throws <see cref="ArgumentException"/> when a field cannot be rendered.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FontName) || FontName.IndexOfAny([',', '\r', '\n']) >= 0)
            throw new ArgumentException($"Font name '{FontName}' must be a non-empty name without commas or line breaks.", nameof(FontName));
        if (!(FontSizeRatio > 0 && FontSizeRatio < 1))
            throw new ArgumentException($"FontSizeRatio must be between 0 and 1 (got {FontSizeRatio}).", nameof(FontSizeRatio));
        if (!(BottomMarginRatio >= 0 && BottomMarginRatio < 1))
            throw new ArgumentException($"BottomMarginRatio must be at least 0 and below 1 (got {BottomMarginRatio}).", nameof(BottomMarginRatio));
        ToAssColour(TextColor, nameof(TextColor));
        ToAssColour(BoxColor, nameof(BoxColor));
    }

    /// <summary>
    /// <c>#RRGGBB</c> / <c>#RRGGBBAA</c> (AA = opacity) to the subtitle format's <c>&amp;HAABBGGRR</c> (AA = transparency).
    /// </summary>
    internal static string ToAssColour(string colour, string parameterName)
    {
        if (colour is null || colour.Length is not (7 or 9) || colour[0] != '#'
            || !uint.TryParse(colour.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"Colour '{colour}' must be #RRGGBB or #RRGGBBAA.", parameterName);

        var opacity = colour.Length == 9 ? value & 0xFF : 0xFF;
        var rgb = colour.Length == 9 ? value >> 8 : value;
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return string.Create(CultureInfo.InvariantCulture, $"&H{0xFF - opacity:X2}{b:X2}{g:X2}{r:X2}");
    }
}
