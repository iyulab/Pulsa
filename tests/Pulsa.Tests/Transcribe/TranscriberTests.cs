using AwesomeAssertions;
using Microsoft.Extensions.AI;
using PulsaTranscribe;
using PulsaTranscript;
using Xunit;
using static Pulsa.Tests.Transcribe.Time;

#pragma warning disable MEAI001

namespace Pulsa.Tests.Transcribe;

// The contract is docs/transcribe.md. Every stage is a fake here: this project does not reference PulsaMedia, which is
// what "a stage that is not asked for does not need its module" means for the facade.
public sealed class TranscriberTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pulsa-transcribe-tests-").FullName;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TenSecondsWav() => Audio.WriteWav(_dir, Audio.Of(Audio.Silence(10)));

    // --- Failures checked before any audio is processed

    [Fact]
    public async Task ASpeakerCountWithoutADiarizer_IsRefusedBeforeRecognition()
    {
        var recognizer = new FakeRecognizer();
        var act = () => Transcriber.TranscribeAsync(recognizer,
            new TranscribeRequest(TenSecondsWav()) { Speakers = SpeakerCount.Exactly(2) }, cancellationToken: Ct);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*diarizer*");
        recognizer.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task AGlossaryWithoutAChatClient_IsRefusedBeforeRecognition()
    {
        var recognizer = new FakeRecognizer();
        var act = () => Transcriber.TranscribeAsync(recognizer,
            new TranscribeRequest(TenSecondsWav()) { Glossary = new Glossary(["Pulsa"]) }, cancellationToken: Ct);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*chat client*");
        recognizer.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task PreparationOptionsWithoutAPreparer_AreRefused()
    {
        var act = () => Transcriber.TranscribeAsync(new FakeRecognizer(),
            new TranscribeRequest(TenSecondsWav()) { Prepare = new PrepareOptions(NoiseReduction: true) }, cancellationToken: Ct);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*preparer*");
    }

    [Fact]
    public async Task AMissingAudioFile_IsRefused()
    {
        var act = () => Transcriber.TranscribeAsync(new FakeRecognizer(),
            new TranscribeRequest(Path.Combine(_dir, "nope.wav")), cancellationToken: Ct);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task WithoutAPreparer_AudioThatIsNot16kMonoWav_IsRefusedBeforeRecognition()
    {
        var path = Path.Combine(_dir, "song.mp3");
        await File.WriteAllBytesAsync(path, [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0], Ct);
        var recognizer = new FakeRecognizer();

        var act = () => Transcriber.TranscribeAsync(recognizer, new TranscribeRequest(path), cancellationToken: Ct);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*16 kHz mono*");
        recognizer.Received.Should().BeEmpty();
    }

    // --- Times are on the original audio's timeline

    [Fact]
    public async Task SegmentsFoundOnPreparedAudio_AreMappedBackToTheInputTimeline()
    {
        // The preparer trimmed 2 s from the front: prepared 0 s is input 2 s.
        var preparer = new FakePreparer(new PreparedAudio(Audio.Of(Audio.Silence(8)), S(10), TimeMap.Offset(S(2))));
        var stages = new TranscribeStages { Preparer = preparer, VoiceActivity = new FakeDetector(R(1, 2), R(3, 4)) };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer("one", "two"),
            new TranscribeRequest(TenSecondsWav()), stages, Ct);

        result.Cues.Select(c => (c.Start, c.End)).Should().Equal((S(3), S(4)), (S(5), S(6)));
        preparer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task EveryCueLiesWithinTheInputDuration()
    {
        // Prepared audio maps 1:1 from input 5 s; a segment at prepared 4–6 s would end at input 11 s of a 10 s input.
        var preparer = new FakePreparer(new PreparedAudio(Audio.Of(Audio.Silence(6)), S(10), TimeMap.Offset(S(5))));
        var stages = new TranscribeStages { Preparer = preparer, VoiceActivity = new FakeDetector(R(4, 6)) };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer("tail"), new TranscribeRequest(TenSecondsWav()), stages, Ct);

        result.Cues.Should().ContainSingle().Which.Should().Match<TranscriptCue>(c => c.Start == S(9) && c.End == S(10));
    }

    // --- Cues are in time order and do not overlap; timing is as fine as the segments

    [Fact]
    public async Task UnorderedOverlappingSpans_BecomeOrderedCuesThatDoNotOverlap()
    {
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(6, 8), R(1, 3), R(2, 4), R(8.5, 9)) };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer(), new TranscribeRequest(TenSecondsWav()), stages, Ct);

        result.Cues.Select(c => (c.Start, c.End)).Should().Equal((S(1), S(4)), (S(6), S(8)), (S(8.5), S(9)));
        result.Cues.Zip(result.Cues.Skip(1)).Should().OnlyContain(pair => pair.First.End <= pair.Second.Start);
    }

    [Fact]
    public async Task ASegmentLongerThanTheMaximum_IsCutIntoEqualPartsAndEachIsRecognizedAlone()
    {
        var wav = Audio.WriteWav(_dir, Audio.Of(Audio.Silence(80)));
        var recognizer = new FakeRecognizer();
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(5, 75)) };

        var result = await Transcriber.TranscribeAsync(recognizer, new TranscribeRequest(wav), stages, Ct);

        result.Cues.Should().HaveCount(3);
        result.Cues[0].Start.Should().Be(S(5));
        result.Cues[^1].End.Should().Be(S(75));
        recognizer.Received.Should().HaveCount(3).And.OnlyContain(d => d <= S(30));
    }

    [Fact]
    public async Task ASegmentWithNoRecognizedText_IsLeftOutAndNamedInWarnings()
    {
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 2), R(3, 4)) };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer("  ", "kept"), new TranscribeRequest(TenSecondsWav()), stages, Ct);

        result.Cues.Should().ContainSingle().Which.Text.Should().Be("kept");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("1–2 s");
    }

    // --- A speaker is one label for the whole file

    [Fact]
    public async Task TheDiarizerIsCalledOnceOnTheWholeAudio_AndLabelsFollowFirstAppearance()
    {
        var diarizer = new FakeDiarizer(
            new SpeakerTurn(S(0), S(2.5), "voice-b"),
            new SpeakerTurn(S(2.5), S(5), "voice-a"),
            new SpeakerTurn(S(5), S(10), "voice-b"));
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 2), R(3, 4), R(6, 7)), Diarizer = diarizer };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer(),
            new TranscribeRequest(TenSecondsWav()) { Speakers = new SpeakerCount(1, 3) }, stages, Ct);

        diarizer.Received.Should().Equal(S(10));
        result.Cues.Select(c => c.Speaker).Should().Equal("S1", "S2", "S1");
        result.Speakers.Should().Equal("S1", "S2");
    }

    [Fact]
    public async Task ACueTakesTheSpeakerWhoseTurnsCoverMostOfIt()
    {
        // 1–4 s: voice-a covers 1–1.8 (0.8 s), voice-b covers 1.8–4 (2.2 s).
        var diarizer = new FakeDiarizer(new SpeakerTurn(S(0), S(1.8), "voice-a"), new SpeakerTurn(S(1.8), S(4), "voice-b"));
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 4)), Diarizer = diarizer };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer(), new TranscribeRequest(TenSecondsWav()), stages, Ct);

        result.Cues.Should().ContainSingle().Which.Speaker.Should().Be("S1");
        result.Speakers.Should().Equal("S1");
    }

    [Fact]
    public async Task ACueNoTurnCovers_HasNoSpeaker_AndATurnOutsideEverySegmentIsNamed()
    {
        var diarizer = new FakeDiarizer(new SpeakerTurn(S(1), S(2), "voice-a"), new SpeakerTurn(S(8), S(9), "voice-z"));
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 2), R(4, 5)), Diarizer = diarizer };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer(), new TranscribeRequest(TenSecondsWav()), stages, Ct);

        result.Cues.Select(c => c.Speaker).Should().Equal("S1", null);
        result.Warnings.Should().HaveCount(2)
            .And.Contain(w => w.Contains("4–5 s"))
            .And.Contain(w => w.Contains("voice-z"));
    }

    // --- Only recognition puts words in; a stage not asked for does not run

    [Fact]
    public async Task OnlyTheStagesSuppliedRun_AndTheResultSaysWhich()
    {
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 2)) };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer("hello"), new TranscribeRequest(TenSecondsWav()), stages, Ct);

        result.Stages.Should().Equal(new TranscribeStage("segment", "fake-vad"), new TranscribeStage("recognize", "fake-stt"));
        result.Cues.Should().ContainSingle().Which.Should().Be(new TranscriptCue(S(1), S(2), "hello"));
        result.Speakers.Should().BeEmpty();
        result.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task Refinement_ChangesOnlyWords_NeverCountsOrTimes()
    {
        var chat = new ReplyingChatClient("[0] 참석자는 저 김민수 팀장");
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 2), R(3, 4)), Refiner = chat };

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer("참석제는 저 김인수 팀장", "then"),
            new TranscribeRequest(TenSecondsWav()) { Glossary = new Glossary(["김민수"]) }, stages, Ct);

        result.Cues.Select(c => (c.Start, c.End)).Should().Equal((S(1), S(2)), (S(3), S(4)));
        result.Cues.Select(c => c.Text).Should().Equal("참석자는 저 김민수 팀장", "then");
        result.Stages.Select(s => s.Stage).Should().Equal("segment", "recognize", "refine");
    }

    [Fact]
    public async Task RecognitionOptions_AreTheStartOfEveryCall_WithTheLanguageAndRateSet()
    {
        var recognizer = new FakeRecognizer();
        var template = new SpeechToTextOptions { ModelId = "my-model", AdditionalProperties = new() { ["vocabulary"] = "Pulsa" } };
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 2), R(3, 4)) };

        await Transcriber.TranscribeAsync(recognizer,
            new TranscribeRequest(TenSecondsWav()) { Language = "ko", Recognition = template }, stages, Ct);

        recognizer.Options.Should().HaveCount(2).And.OnlyContain(o =>
            o!.ModelId == "my-model" && (string)o.AdditionalProperties!["vocabulary"]! == "Pulsa"
            && o.SpeechLanguage == "ko" && o.SpeechSampleRate == 16_000);
        recognizer.Options[0].Should().NotBeSameAs(template).And.NotBeSameAs(recognizer.Options[1]);
        template.SpeechLanguage.Should().BeNull("the caller's template is not changed");
    }

    // --- Failures during the run

    [Fact]
    public async Task AnErrorFromAPort_EndsTheCallWithNoPartialTranscript()
    {
        var recognizer = new FakeRecognizer { FailOnCall = call => call == 1 ? new HttpRequestException("upstream down") : null };
        var stages = new TranscribeStages { VoiceActivity = new FakeDetector(R(1, 2), R(3, 4), R(5, 6)) };

        var act = () => Transcriber.TranscribeAsync(recognizer, new TranscribeRequest(TenSecondsWav()), stages, Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
        recognizer.Received.Should().HaveCount(2);
    }

    // --- Built-in segmentation

    [Fact]
    public async Task WithoutADetector_SilenceDetectionFindsTheSpeech()
    {
        var wav = Audio.WriteWav(_dir, Audio.Of(Audio.Silence(1), Audio.Tone(2), Audio.Silence(1), Audio.Tone(1), Audio.Silence(1)));

        var result = await Transcriber.TranscribeAsync(new FakeRecognizer(), new TranscribeRequest(wav), cancellationToken: Ct);

        result.Stages[0].Should().Be(new TranscribeStage("segment", "silence"));
        result.Cues.Select(c => (Math.Round(c.Start.TotalSeconds, 2), Math.Round(c.End.TotalSeconds, 2)))
            .Should().Equal((1.0, 3.0), (4.0, 5.0));
    }

    /// <summary>A chat client that always answers with the same text.</summary>
    private sealed class ReplyingChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
