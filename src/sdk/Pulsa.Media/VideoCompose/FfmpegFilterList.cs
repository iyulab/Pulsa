using System.Diagnostics;
using FFMpegCore;

namespace PulsaVideoCompose;

/// <summary>
/// Which filters an ffmpeg build has. Captions are burned with the <c>subtitles</c> filter, which needs libass and which
/// some builds (a stock Homebrew ffmpeg, for one) leave out; ffmpeg then answers a composition with a filter-graph parsing
/// error that does not name the cause, so the composer asks first.
/// </summary>
internal static class FfmpegFilterList
{
    /// <summary>Filter names from <c>ffmpeg -filters</c>, or null when the output holds no filter rows.</summary>
    internal static IReadOnlySet<string>? Parse(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            // " TS pixelize          V->V       Pixelize video." — flags, name, then the pads
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[2].Contains("->", StringComparison.Ordinal))
                names.Add(parts[1]);
        }
        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// The build's filters, or null when ffmpeg cannot be run, its answer cannot be read, or the call was cancelled (the
    /// real error, or the cancellation, then comes from the run itself).
    /// </summary>
    public static async Task<IReadOnlySet<string>?> ReadAsync(FFOptions ffOptions, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(GlobalFFOptions.GetFFMpegBinaryPath(ffOptions))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-filters");
            using var process = Process.Start(start);
            if (process is null) return null;
            using var cancellation = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // already finished
            });
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
            await error;
            return process.ExitCode == 0 ? Parse(await output) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null; // not runnable here
        }
    }

    /// <summary>The sentence to fail with when the build is known to lack the <c>subtitles</c> filter; null when it has it or is unknown.</summary>
    internal static string? SubtitlesProblem(IReadOnlySet<string>? filters) =>
        filters is not null && !filters.Contains("subtitles")
            ? "This ffmpeg has no 'subtitles' filter (it was built without libass), which captions need; use an ffmpeg build that includes it."
            : null;
}
