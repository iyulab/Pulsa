using System.Diagnostics;
using System.Text.Json;
using FFMpegCore;

namespace PulsaRedact;

/// <summary>
/// The pixel size of a picture, read from ffprobe's JSON with <see cref="JsonDocument"/> — no reflection, so it runs in
/// a trimmed or native-AOT host. (FFMpegCore's own <c>FFProbe.AnalyseAsync</c> reads the same output through a
/// reflection-based serializer, which a trimmed host switches off.)
/// </summary>
internal static class PictureSize
{
    public static async Task<(int Width, int Height)> ReadAsync(string path, FFOptions ffOptions, CancellationToken cancellationToken)
    {
        var json = await ProbeAsync(path, ffOptions, cancellationToken, "-select_streams", "v:0", "-show_entries", "stream=width,height");
        return Parse(json) ?? throw new InvalidDataException($"{path} has no picture to look at.");
    }

    /// <summary>The length of the media in seconds, as the container states it.</summary>
    public static async Task<double> ReadDurationAsync(string path, FFOptions ffOptions, CancellationToken cancellationToken)
    {
        var json = await ProbeAsync(path, ffOptions, cancellationToken, "-show_entries", "format=duration");
        return ParseDuration(json) ?? throw new InvalidDataException($"{path} does not state how long it is.");
    }

    private static async Task<string> ProbeAsync(string path, FFOptions ffOptions, CancellationToken cancellationToken, params string[] selection)
    {
        var start = new ProcessStartInfo(GlobalFFOptions.GetFFProbeBinaryPath(ffOptions))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-v", "error" }.Concat(selection).Concat(new[] { "-of", "json", path }))
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffprobe did not start.");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // already finished
        });
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidDataException($"ffprobe could not read {path}: {(await error).Trim()}");
        return await output;
    }

    /// <summary>The duration in ffprobe's JSON answer, or null when it is missing or not a positive number.</summary>
    internal static double? ParseDuration(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.String &&
            double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return seconds;
        return null;
    }

    /// <summary>The size of the first stream in ffprobe's JSON answer, or null when it has none.</summary>
    internal static (int Width, int Height)? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var stream in streams.EnumerateArray())
            if (stream.TryGetProperty("width", out var width) && width.TryGetInt32(out var w) &&
                stream.TryGetProperty("height", out var height) && height.TryGetInt32(out var h) && w > 0 && h > 0)
                return (w, h);
        return null;
    }
}
