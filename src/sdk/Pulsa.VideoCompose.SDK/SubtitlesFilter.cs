namespace PulsaVideoCompose;

/// <summary>
/// Builds the ffmpeg <c>-vf</c> argument that burns a subtitle script into video. The script carries its own styling
/// (<see cref="AssScript"/>), so the filter only names the file.
/// </summary>
public static class SubtitlesFilter
{
    public static string Build(string subtitlePath) => $"subtitles='{EscapeForFilterArgument(subtitlePath)}'";

    // ffmpeg's filter-option parser treats ':' and '\' specially inside a filter's own option
    // string — a Windows path's drive-letter colon and every backslash must be escaped, or the
    // subtitles filter misreads the path as a run of filter options instead of a file path.
    private static string EscapeForFilterArgument(string path) =>
        path.Replace("\\", "\\\\").Replace(":", "\\:");
}
