using AwesomeAssertions;
using FFMpegCore;
using Microsoft.Extensions.AI;
using PulsaTranscribe;
using Xunit;

#pragma warning disable MEAI001

namespace Pulsa.Media.Tests.Transcribe;

// Live: needs a real ffmpeg on PATH (CI installs one).
public sealed class FfmpegAudioPreparerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pulsa-preparer-tests-").FullName;
    private readonly FfmpegAudioPreparer _preparer = new(string.Empty);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>44.1 kHz stereo audio: a tone from 1 s to 2 s and from 3 s to 4 s, silence elsewhere, 5 s in all.</summary>
    private async Task<string> TwoTonesAsync(string extension)
    {
        var path = Path.Combine(_dir, "two-tones" + extension);
        await FFMpegArguments
            .FromFileInput("aevalsrc='if(between(t,1,2)+between(t,3,4),0.3*sin(2*PI*440*t),0)|if(between(t,1,2)+between(t,3,4),0.3*sin(2*PI*440*t),0)':s=44100:d=5",
                verifyExists: false, opt => opt.ForceFormat("lavfi"))
            .OutputToFile(path, overwrite: true)
            .ProcessAsynchronously(ffMpegOptions: new FFOptions { BinaryFolder = string.Empty });
        return path;
    }

    [Fact]
    public async Task AnyAudio_BecomesSixteenKilohertzMono_OnTheSameTimeline()
    {
        var input = await TwoTonesAsync(".m4a");

        var prepared = await _preparer.PrepareAsync(input, new PrepareOptions(NormalizeLoudness: false), Ct);

        prepared.Audio.SampleRate.Should().Be(16_000);
        prepared.Audio.Duration.TotalSeconds.Should().BeApproximately(5, 0.1);
        prepared.InputDuration.TotalSeconds.Should().BeApproximately(5, 0.1);
        prepared.Map.ToInput(TimeSpan.FromSeconds(3)).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task NoiseReductionAndLoudness_KeepTheDuration()
    {
        var input = await TwoTonesAsync(".wav");

        var prepared = await _preparer.PrepareAsync(input, new PrepareOptions(NoiseReduction: true, NormalizeLoudness: true), Ct);

        prepared.Audio.Duration.TotalSeconds.Should().BeApproximately(5, 0.1);
    }

    [Fact]
    public async Task AFileThatIsNotMedia_IsRefusedAsInvalidData()
    {
        var path = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(path, "not audio", Ct);

        var act = () => _preparer.PrepareAsync(path, new PrepareOptions(), Ct);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ThroughTheFacade_SpeechIsFoundWhereTheTonesAre()
    {
        var input = await TwoTonesAsync(".m4a");
        var recognizer = new EchoRecognizer();

        var result = await Transcriber.TranscribeAsync(recognizer, new TranscribeRequest(input),
            new TranscribeStages { Preparer = _preparer }, Ct);

        result.Stages.Select(s => s.Stage).Should().Equal("prepare", "segment", "recognize");
        result.Cues.Should().HaveCount(2);
        result.Cues[0].Start.TotalSeconds.Should().BeApproximately(1, 0.1);
        result.Cues[1].End.TotalSeconds.Should().BeApproximately(4, 0.1);
    }

    private sealed class EchoRecognizer : ISpeechToTextClient
    {
        public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SpeechToTextResponse("speech"));

        public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
