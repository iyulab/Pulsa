namespace PulsaVideoCompose;

public sealed record ComposeVideoRequest(
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> Captions,
    double SceneDurationSeconds,
    string OutputPath);

public sealed record ComposeVideoResult(bool Success, string? OutputPath, string? SrtPath, string? Error);
