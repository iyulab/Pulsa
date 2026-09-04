namespace PulsaVideoCompose;

/// <summary>Tunable render parameters. Defaults match spec §8's v1 decisions: 1920x1080 (16:9),
/// 25fps, a gentle default Ken-Burns zoom.</summary>
public sealed record VideoComposeOptions(
    int Width = 1920,
    int Height = 1080,
    int Fps = 25,
    double ZoomIncrementPerFrame = 0.0015,
    double MaxZoom = 1.5);
