using System.Globalization;
using FFMpegCore;

namespace PulsaTranscribe;

/// <summary>
/// The transcription facade's preparation stage, done with ffmpeg: any audio (or the audio track of a video) ffmpeg
/// reads → 16 kHz mono 16-bit PCM, optionally loudness-normalized (<c>loudnorm</c>) and noise-reduced (<c>afftdn</c>).
/// Decoding, filtering and resampling keep the timeline, so the map back to the input is the identity.
/// </summary>
public sealed class FfmpegAudioPreparer : IAudioPreparer
{
    private readonly FFOptions _ffOptions;

    /// <param name="ffmpegBinaryFolder">The folder holding <c>ffmpeg</c> and <c>ffprobe</c>; empty looks them up on PATH.</param>
    public FfmpegAudioPreparer(string ffmpegBinaryFolder)
    {
        ArgumentNullException.ThrowIfNull(ffmpegBinaryFolder);
        _ffOptions = new FFOptions { BinaryFolder = ffmpegBinaryFolder };
    }

    public string Name => "ffmpeg";

    public async Task<PreparedAudio> PrepareAsync(string inputPath, PrepareOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(options);

        IMediaAnalysis probe;
        try
        {
            probe = await FFProbe.AnalyseAsync(inputPath, _ffOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"ffmpeg cannot read '{inputPath}' as media: {ex.Message}", ex);
        }
        if (probe.PrimaryAudioStream is null)
            throw new InvalidDataException($"'{inputPath}' has no audio stream.");

        var filters = new List<string>();
        if (options.NoiseReduction) filters.Add("afftdn");
        if (options.NormalizeLoudness) filters.Add("loudnorm");

        var workDir = Directory.CreateTempSubdirectory("pulsa-transcribe-");
        try
        {
            var wavPath = Path.Combine(workDir.FullName, "prepared.wav");
            await FFMpegArguments
                .FromFileInput(inputPath, verifyExists: true)
                .OutputToFile(wavPath, overwrite: true, opt =>
                {
                    opt.WithCustomArgument("-vn -map 0:a:0");
                    if (filters.Count > 0) opt.WithCustomArgument($"-af {string.Join(',', filters)}");
                    opt.WithCustomArgument(string.Create(CultureInfo.InvariantCulture,
                        $"-ac 1 -ar {PcmAudio.TranscriptionSampleRate} -c:a pcm_s16le -f wav"));
                })
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously(throwOnError: true, ffMpegOptions: _ffOptions);

            PcmAudio audio;
            await using (var file = File.OpenRead(wavPath))
                audio = PcmAudio.ReadTranscriptionWav(file);

            var inputDuration = probe.Duration > TimeSpan.Zero ? probe.Duration : audio.Duration;
            return new PreparedAudio(audio, inputDuration, TimeMap.Identity);
        }
        finally
        {
            try { workDir.Delete(recursive: true); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}
