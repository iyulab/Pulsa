namespace PulsaVideoCompose;

/// <summary>
/// Builds the ffmpeg <c>-vf</c> argument that burns a subtitle script into video. The script carries its own styling
/// (<see cref="AssScript"/>), so the filter only names the file — and, when given, a folder of font files the script's
/// fonts may come from besides the installed ones.
/// </summary>
public static class SubtitlesFilter
{
    public static string Build(string subtitlePath, string? fontsDirectory = null) =>
        fontsDirectory is null
            ? $"subtitles='{EscapeForFilterArgument(subtitlePath)}'"
            : $"subtitles='{EscapeForFilterArgument(subtitlePath)}':fontsdir='{EscapeForFilterArgument(fontsDirectory)}'";

    // ffmpeg's filter-option parser treats ':' and '\' specially inside a filter's own option
    // string — a Windows path's drive-letter colon and every backslash must be escaped, or the
    // subtitles filter misreads the path as a run of filter options instead of a file path.
    private static string EscapeForFilterArgument(string path) =>
        path.Replace("\\", "\\\\").Replace(":", "\\:");
}
