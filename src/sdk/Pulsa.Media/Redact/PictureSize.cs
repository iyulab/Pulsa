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
        var start = new ProcessStartInfo(GlobalFFOptions.GetFFProbeBinaryPath(ffOptions))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json", path })
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
        return Parse(await output) ?? throw new InvalidDataException($"{path} has no picture to look at.");
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
