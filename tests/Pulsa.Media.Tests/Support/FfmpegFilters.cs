using System.Diagnostics;
using Xunit;

namespace Pulsa.Media.Tests.Support;

/// <summary>
/// Which filters the ffmpeg on <c>PATH</c> was built with, read once per test run. A live test that needs a filter an
/// ffmpeg build can leave out (<c>subtitles</c> needs libass) skips with the reason instead of failing, so a red test
/// always means the code, never the machine. An ffmpeg that cannot be run at all is not skipped: the suite requires one.
/// </summary>
internal static class FfmpegFilters
{
    private static readonly Lazy<IReadOnlySet<string>?> Available = new(ReadFilters);

    /// <summary>Skips the calling test unless every named filter is in the ffmpeg build.</summary>
    public static void Require(params string[] filters)
    {
        var missing = Missing(Available.Value, filters);
        Assert.SkipWhen(missing.Count > 0,
            $"the ffmpeg on PATH was built without the {string.Join(", ", missing)} filter(s) — use a build with them to run this test");
    }

    /// <summary>The named filters the build lacks; none when the build could not be read (the test then fails on the real error).</summary>
    internal static IReadOnlyList<string> Missing(IReadOnlySet<string>? available, IEnumerable<string> filters) =>
        available is null ? [] : filters.Where(f => !available.Contains(f)).ToArray();

    /// <summary>Filter names from <c>ffmpeg -filters</c>, or null when ffmpeg could not be run.</summary>
    internal static IReadOnlySet<string>? ParseFilterList(string output)
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

    private static IReadOnlySet<string>? ReadFilters()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -filters")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? ParseFilterList(output) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // not on PATH
        }
    }
}
