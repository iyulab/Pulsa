namespace PulsaVideoCompose;

/// <summary>
/// Builds the ffmpeg -vf filter argument that burns an .srt file's captions into video, styled
/// with an opaque background band behind the text (ASS <c>BorderStyle=3</c>) instead of libass's
/// default outline-only SRT style. A plain outline is legible over empty background but collides
/// with — and is defeated by — whatever pixels already occupy the caption region of a
/// content-dense source image (e.g. a screenshot's own UI text near the frame edge); the opaque
/// band guarantees contrast regardless of what's underneath.
/// </summary>
public static class CaptionStyleFilterBuilder
{
    public static string Build(string srtPath) =>
        $"subtitles='{EscapeForFilterArgument(srtPath)}':force_style='BorderStyle=3,Outline=1,Shadow=0,BackColour=&H80000000'";

    // ffmpeg's filter-option parser treats ':' and '\' specially inside a filter's own option
    // string — a Windows path's drive-letter colon and every backslash must be escaped, or the
    // subtitles filter misreads the path as a run of filter options instead of a file path.
    private static string EscapeForFilterArgument(string path) =>
        path.Replace("\\", "\\\\").Replace(":", "\\:");
}
