using Microsoft.Extensions.AI;
using PulsaTranscribe;

#pragma warning disable MEAI001

namespace Pulsa.Tests.Transcribe;

/// <summary>Synthesized 16 kHz mono audio — no ffmpeg, no fixture files.</summary>
internal static class Audio
{
    public static short[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * PcmAudio.TranscriptionSampleRate))
            .Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / PcmAudio.TranscriptionSampleRate)))
            .ToArray();

    public static short[] Silence(double seconds) => new short[(int)(seconds * PcmAudio.TranscriptionSampleRate)];

    public static PcmAudio Of(params short[][] parts) => new(PcmAudio.TranscriptionSampleRate, parts.SelectMany(p => p).ToArray());

    /// <summary>Writes the audio as a WAV file in <paramref name="directory"/> and returns its path.</summary>
    public static string WriteWav(string directory, PcmAudio audio, string name = "input.wav")
    {
        var path = Path.Combine(directory, name);
        using var file = File.Create(path);
        audio.WriteWav(file);
        return path;
    }
}

/// <summary>Answers each call with the next text, and records the length of audio it was handed.</summary>
internal sealed class FakeRecognizer(params string[] texts) : ISpeechToTextClient
{
    public List<TimeSpan> Received { get; } = [];
    public List<SpeechToTextOptions?> Options { get; } = [];
    public Func<int, Exception?> FailOnCall { get; init; } = _ => null;

    public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        var audio = PcmAudio.ReadTranscriptionWav(audioSpeechStream);
        var call = Received.Count;
        Received.Add(audio.Duration);
        Options.Add(options);
        if (FailOnCall(call) is { } error) throw error;
        var text = call < texts.Length ? texts[call] : $"text {call + 1}";
        return Task.FromResult(new SpeechToTextResponse(text) { ModelId = "fake-stt" });
    }

    public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class FakePreparer(PreparedAudio prepared) : IAudioPreparer
{
    public int Calls { get; private set; }
    public string Name => "fake-preparer";

    public Task<PreparedAudio> PrepareAsync(string inputPath, PrepareOptions options, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(prepared);
    }
}

internal sealed class FakeDetector(params TimeRange[] spans) : IVoiceActivityDetector
{
    public string Name => "fake-vad";
    public Task<IReadOnlyList<TimeRange>> DetectAsync(PcmAudio audio, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TimeRange>>(spans);
}

internal sealed class FakeDiarizer(params SpeakerTurn[] turns) : IDiarizer
{
    public List<TimeSpan> Received { get; } = [];
    public string Name => "fake-diarizer";

    public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(PcmAudio audio, SpeakerCount? speakers, CancellationToken cancellationToken)
    {
        Received.Add(audio.Duration);
        return Task.FromResult<IReadOnlyList<SpeakerTurn>>(turns);
    }
}

internal static class Time
{
    public static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    public static TimeRange R(double start, double end) => new(S(start), S(end));
}
