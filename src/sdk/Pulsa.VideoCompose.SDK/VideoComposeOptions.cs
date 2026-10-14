namespace PulsaVideoCompose;

/// <summary>Tunable render parameters. Defaults match spec §8's v1 decisions: 1920x1080 (16:9),
/// 25fps, a gentle default Ken-Burns zoom.</summary>
/// <param name="Captions">How scene captions look; <see cref="CaptionStyle.DefaultCaptions"/> when null.</param>
/// <param name="Cards">How the title and outro cards' text looks; <see cref="CaptionStyle.DefaultCards"/> when null.</param>
public sealed record VideoComposeOptions(
    int Width = 1920,
    int Height = 1080,
    int Fps = 25,
    double ZoomIncrementPerFrame = 0.0015,
    double MaxZoom = 1.5,
    CaptionStyle? Captions = null,
    CaptionStyle? Cards = null)
{
    internal CaptionStyle CaptionsOrDefault => Captions ?? CaptionStyle.DefaultCaptions;

    internal CaptionStyle CardsOrDefault => Cards ?? CaptionStyle.DefaultCards;
}
