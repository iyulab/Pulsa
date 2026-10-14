namespace PulsaVideoCompose;

/// <summary>Tunable render parameters. Defaults match spec §8's v1 decisions: 1920x1080 (16:9),
/// 25fps, a gentle default Ken-Burns zoom.</summary>
/// <param name="Captions">How scene captions look; <see cref="CaptionStyle.DefaultCaptions"/> when null.</param>
/// <param name="Cards">How the title and outro cards' text looks; <see cref="CaptionStyle.DefaultCards"/> when null.</param>
/// <param name="FontsDirectory">A folder of font files (<c>.ttf</c>, <c>.otf</c>, <c>.ttc</c>) that
/// <see cref="CaptionStyle.FontName"/> can name besides the fonts installed where ffmpeg runs — a font that travels with
/// a project instead of being installed on every machine that renders it. A font is named by its family name, not its
/// file name. The folder must exist.</param>
public sealed record VideoComposeOptions(
    int Width = 1920,
    int Height = 1080,
    int Fps = 25,
    double ZoomIncrementPerFrame = 0.0015,
    double MaxZoom = 1.5,
    CaptionStyle? Captions = null,
    CaptionStyle? Cards = null,
    string? FontsDirectory = null)
{
    internal CaptionStyle CaptionsOrDefault => Captions ?? CaptionStyle.DefaultCaptions;

    internal CaptionStyle CardsOrDefault => Cards ?? CaptionStyle.DefaultCards;
}
