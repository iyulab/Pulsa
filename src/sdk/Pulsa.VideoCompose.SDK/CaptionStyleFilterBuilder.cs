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

    /// <summary>
    /// A large, center-screen style for a title/outro card. Font size scales with
    /// <paramref name="videoHeight"/> (not a fixed pixel count) so it reads correctly at either a
    /// 16:9 or a 9:16 frame height rather than being sized for one and looking wrong on the other.
    /// </summary>
    /// <remarks>
    /// <c>Alignment=10</c>, not the ASS v4+ numpad value 5 that "middle center" would suggest.
    /// ffmpeg's <c>subtitles</c> filter converts a plain .srt input to the legacy SSA v4 format
    /// before burning it, and that format's <c>Alignment</c> field uses SSA's own older numbering
    /// (1/2/3 = bottom left/center/right, 5/6/7 = top left/center/right, 9/10/11 = middle
    /// left/center/right — 4/8 are unused), not ASS v4+'s numpad scheme. Confirmed empirically: a
    /// direct test render swept all nine numpad values (1-9) against this exact input path and none
    /// produced true middle-center — <c>Alignment=5</c> rendered top-left, <c>Alignment=10</c> is
    /// the value that actually centers.
    /// </remarks>
    public static string BuildTitleStyle(string srtPath, int videoHeight)
    {
        var fontSize = Math.Max(32, videoHeight / 15);
        return $"subtitles='{EscapeForFilterArgument(srtPath)}':force_style='Alignment=10,FontSize={fontSize},Bold=1,BorderStyle=3,Outline=1,Shadow=0,BackColour=&H80000000'";
    }

    // ffmpeg's filter-option parser treats ':' and '\' specially inside a filter's own option
    // string — a Windows path's drive-letter colon and every backslash must be escaped, or the
    // subtitles filter misreads the path as a run of filter options instead of a file path.
    private static string EscapeForFilterArgument(string path) =>
        path.Replace("\\", "\\\\").Replace(":", "\\:");
}
