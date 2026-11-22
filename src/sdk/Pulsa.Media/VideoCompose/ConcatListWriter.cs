namespace PulsaVideoCompose;

/// <summary>Writes an ffmpeg concat-demuxer playlist file (`-f concat -safe 0 -i <this file>`).</summary>
public static class ConcatListWriter
{
    public static async Task<string> WriteAsync(
        IReadOnlyList<string> clipPaths, string listPath, CancellationToken cancellationToken = default)
    {
        var lines = clipPaths.Select(p => $"file '{EscapeSingleQuotes(p)}'");
        await File.WriteAllLinesAsync(listPath, lines, cancellationToken);
        return listPath;
    }

    // The concat demuxer's own list-file syntax quotes each path in single quotes; a literal
    // single quote inside the path must become '\'' (close-quote, escaped-quote, reopen-quote) —
    // the standard POSIX-shell-style escape ffmpeg's own demuxer documentation prescribes.
    private static string EscapeSingleQuotes(string path) => path.Replace("'", @"'\''");
}
