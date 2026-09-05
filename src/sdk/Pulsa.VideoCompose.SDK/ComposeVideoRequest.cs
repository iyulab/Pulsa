namespace PulsaVideoCompose;

/// <param name="TitleImagePath">Optional. A caller-supplied image (a logo, a solid-color card,
/// anything — this SDK has no concept of what it depicts) rendered as a static-hold opening scene
/// with <paramref name="TitleText"/> burned in large and centered. Must be supplied together with
/// <see cref="TitleText"/>, or not at all.</param>
/// <param name="OutroImagePath">Same shape as <see cref="TitleImagePath"/>, rendered as the closing
/// scene instead. Must be supplied together with <see cref="OutroText"/>, or not at all.</param>
public sealed record ComposeVideoRequest(
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> Captions,
    double SceneDurationSeconds,
    string OutputPath,
    string AspectRatio = "16:9",
    string? TitleImagePath = null,
    string? TitleText = null,
    string? OutroImagePath = null,
    string? OutroText = null);

public sealed record ComposeVideoResult(bool Success, string? OutputPath, string? SrtPath, string? Error);
