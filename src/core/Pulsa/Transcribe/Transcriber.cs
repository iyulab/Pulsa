using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using PulsaTranscript;

#pragma warning disable MEAI001 // ISpeechToTextClient is an evaluation API; the public surface below carries the same diagnostic.

namespace PulsaTranscribe;

/// <summary>What to transcribe and how. Each option belongs to a stage; asking for an option whose stage is not supplied is refused.</summary>
public sealed record TranscribeRequest(string AudioPath)
{
    /// <summary>Language hint for recognition (BCP 47); none lets the model detect it.</summary>
    public string? Language { get; init; }

    /// <summary>For the preparation stage. Only read when a preparer is supplied.</summary>
    public PrepareOptions? Prepare { get; init; }

    /// <summary>A segment longer than this is cut into equal parts. Default 30 seconds.</summary>
    public TimeSpan MaximumSegment { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A pause at least this long splits two segments (built-in silence detection). Default 0.5 seconds.</summary>
    public TimeSpan MinimumSilence { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>For the diarization stage: the speakers to expect.</summary>
    public SpeakerCount? Speakers { get; init; }

    /// <summary>For the refinement stage: names and terms the words are checked against.</summary>
    public Glossary? Glossary { get; init; }
}

/// <summary>
/// The stages beyond recognition. A stage left null does not run: no preparer means the audio must already be 16 kHz
/// mono PCM WAV, no detector means built-in silence detection, no diarizer means no speakers, no chat client means no
/// refinement.
/// </summary>
public sealed record TranscribeStages
{
    public IAudioPreparer? Preparer { get; init; }
    public IVoiceActivityDetector? VoiceActivity { get; init; }
    public IDiarizer? Diarizer { get; init; }
    public IChatClient? Refiner { get; init; }
}

/// <summary>A stage that ran, and the model or implementation it ran with as that stage reports it.</summary>
public sealed record TranscribeStage(string Stage, string? Model);

/// <summary>
/// The transcript — cues on the input audio's timeline, in order and not overlapping — with the speaker labels used
/// (<c>S1</c>, <c>S2</c>, … in order of first appearance), the stages that ran, refinement's refused corrections and
/// what was skipped.
/// </summary>
public sealed record TranscribeResult(
    IReadOnlyList<TranscriptCue> Cues,
    IReadOnlyList<string> Speakers,
    IReadOnlyList<TranscribeStage> Stages,
    IReadOnlyList<CueChange> Rejected,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Recorded speech → a transcript with speakers and times. Recognition is the caller's model; this facade prepares,
/// segments, recognizes per segment, puts speakers on the segments and refines the words, and keeps the timing and
/// structure invariants whichever implementations fill the stages. It never creates a client or holds a credential.
/// </summary>
public static class Transcriber
{
    [Experimental("MEAI001", UrlFormat = "https://aka.ms/dotnet-extensions-warnings/{0}")]
    public static async Task<TranscribeResult> TranscribeAsync(
        ISpeechToTextClient recognizer,
        TranscribeRequest request,
        TranscribeStages? stages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(request);
        stages ??= new TranscribeStages();
        Validate(request, stages);

        var ran = new List<TranscribeStage>();
        var warnings = new List<string>();

        // prepare
        PreparedAudio prepared;
        if (stages.Preparer is { } preparer)
        {
            prepared = await preparer.PrepareAsync(request.AudioPath, request.Prepare ?? new PrepareOptions(), cancellationToken);
            if (prepared.Audio.SampleRate != PcmAudio.TranscriptionSampleRate)
                throw new InvalidOperationException(
                    $"The preparer '{preparer.Name}' returned {prepared.Audio.SampleRate} Hz audio; transcription needs {PcmAudio.TranscriptionSampleRate} Hz.");
            ran.Add(new("prepare", preparer.Name));
        }
        else
        {
            PcmAudio audio;
            await using (var file = File.OpenRead(request.AudioPath))
                audio = PcmAudio.ReadTranscriptionWav(file);
            prepared = new PreparedAudio(audio, audio.Duration, TimeMap.Identity);
        }

        // segment
        IReadOnlyList<TimeRange> found;
        if (stages.VoiceActivity is { } detector)
        {
            found = await detector.DetectAsync(prepared.Audio, cancellationToken);
            ran.Add(new("segment", detector.Name));
        }
        else
        {
            found = SilenceDetector.Detect(prepared.Audio, request.MinimumSilence);
            ran.Add(new("segment", "silence"));
        }
        var segments = Segments.Normalize(found, prepared.Audio.Duration, request.MaximumSegment);

        // recognize — one call per segment; the cue is the segment
        var recognized = new List<(TimeRange Segment, string Text)>();
        string? recognitionModel = null;
        foreach (var segment in segments)
        {
            using var stream = new MemoryStream();
            prepared.Audio.Slice(segment.Start, segment.End).WriteWav(stream);
            stream.Position = 0;
            var response = await recognizer.GetTextAsync(stream, new SpeechToTextOptions
            {
                SpeechLanguage = request.Language,
                SpeechSampleRate = PcmAudio.TranscriptionSampleRate,
            }, cancellationToken);
            recognitionModel ??= response.ModelId;
            var text = response.Text.Trim();
            if (text.Length == 0)
                warnings.Add($"No text was recognized for {Format(segment)}; the segment is left out.");
            else
                recognized.Add((segment, text));
        }
        ran.Add(new("recognize", recognitionModel ?? recognizer.GetService<SpeechToTextClientMetadata>()?.DefaultModelId));

        // diarize — once, over the whole audio, so one voice keeps one label
        var rawSpeaker = new string?[recognized.Count];
        if (stages.Diarizer is { } diarizer)
        {
            var turns = await diarizer.DiarizeAsync(prepared.Audio, request.Speakers, cancellationToken);
            ran.Add(new("diarize", diarizer.Name));
            for (var i = 0; i < recognized.Count; i++)
            {
                rawSpeaker[i] = Speakers.Dominant(recognized[i].Segment, turns);
                if (rawSpeaker[i] is null)
                    warnings.Add($"No speaker turn covers {Format(recognized[i].Segment)}; the cue has no speaker.");
            }
            foreach (var turn in turns)
                if (!segments.Any(s => s.Start < turn.End && turn.Start < s.End))
                    warnings.Add($"A turn of speaker '{turn.Speaker}' at {Format(new TimeRange(turn.Start, turn.End))} falls outside every speech segment.");
        }

        // back to the input's timeline; speakers relabelled S1, S2, … by first appearance among the cues kept
        var cues = new List<TranscriptCue>(recognized.Count);
        var relabel = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = new List<string>();
        for (var i = 0; i < recognized.Count; i++)
        {
            var (segment, text) = recognized[i];
            var start = Clamp(prepared.Map.ToInput(segment.Start), prepared.InputDuration);
            var end = Clamp(prepared.Map.ToInput(segment.End), prepared.InputDuration);
            if (end <= start)
            {
                warnings.Add($"The segment at {Format(segment)} maps outside the input audio; it is left out.");
                continue;
            }
            string? label = null;
            if (rawSpeaker[i] is { } raw && !relabel.TryGetValue(raw, out label))
            {
                label = $"S{relabel.Count + 1}";
                relabel.Add(raw, label);
                labels.Add(label);
            }
            cues.Add(new TranscriptCue(start, end, text, label));
        }

        // refine
        IReadOnlyList<CueChange> rejected = [];
        if (stages.Refiner is { } refiner && cues.Count > 0)
        {
            var refined = await TranscriptRefiner.RefineAsync(
                refiner, new RefineTranscriptRequest(cues, request.Glossary ?? Glossary.Empty), cancellationToken: cancellationToken);
            cues = [.. refined.Cues];
            rejected = refined.Rejected;
            ran.Add(new("refine", refiner.GetService<ChatClientMetadata>()?.DefaultModelId));
        }

        return new TranscribeResult(cues, labels, ran, rejected, warnings);
    }

    private static void Validate(TranscribeRequest request, TranscribeStages stages)
    {
        if (request.Speakers is not null && stages.Diarizer is null)
            throw new ArgumentException("A speaker count was given but no diarizer was supplied.", nameof(request));
        if (request.Speakers is { } count && (count.Minimum < 1 || count.Maximum < count.Minimum))
            throw new ArgumentException("A speaker count needs a minimum of at least 1 and a maximum no smaller than it.", nameof(request));
        if (request.Glossary is not null && stages.Refiner is null)
            throw new ArgumentException("A glossary was given but no chat client for refinement was supplied.", nameof(request));
        if (request.Prepare is not null && stages.Preparer is null)
            throw new ArgumentException("Preparation options were given but no audio preparer was supplied.", nameof(request));
        if (request.MaximumSegment <= TimeSpan.Zero)
            throw new ArgumentException("The maximum segment length must be positive.", nameof(request));
        if (request.MinimumSilence < TimeSpan.Zero)
            throw new ArgumentException("The minimum silence must not be negative.", nameof(request));
        if (!File.Exists(request.AudioPath))
            throw new FileNotFoundException("The audio file does not exist.", request.AudioPath);
    }

    private static TimeSpan Clamp(TimeSpan t, TimeSpan duration) =>
        t < TimeSpan.Zero ? TimeSpan.Zero : t > duration ? duration : t;

    private static string Format(TimeRange r) => $"{r.Start.TotalSeconds:0.###}–{r.End.TotalSeconds:0.###} s";
}

/// <summary>Turns what a detector found into the segments recognition is called on.</summary>
internal static class Segments
{
    /// <summary>Clamped to the audio, in order, overlaps merged, empty ones dropped, long ones cut into equal parts.</summary>
    public static IReadOnlyList<TimeRange> Normalize(IReadOnlyList<TimeRange> found, TimeSpan duration, TimeSpan maximum)
    {
        var merged = new List<TimeRange>();
        foreach (var r in found
                     .Select(r => new TimeRange(Max(r.Start, TimeSpan.Zero), Min(r.End, duration)))
                     .Where(r => r.End > r.Start)
                     .OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Max(merged[^1].End, r.End) };
            else
                merged.Add(r);
        }

        var result = new List<TimeRange>();
        foreach (var r in merged)
        {
            var parts = (int)Math.Ceiling(r.Length / maximum);
            var step = r.Length / parts;
            for (var p = 0; p < parts; p++)
                result.Add(new TimeRange(r.Start + step * p, p == parts - 1 ? r.End : r.Start + step * (p + 1)));
        }
        return result;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>Puts a speaker on a segment.</summary>
internal static class Speakers
{
    /// <summary>The speaker whose turns cover most of <paramref name="segment"/>; null when no turn touches it.</summary>
    public static string? Dominant(TimeRange segment, IReadOnlyList<SpeakerTurn> turns)
    {
        string? best = null;
        var bestCover = TimeSpan.Zero;
        foreach (var group in turns.GroupBy(t => t.Speaker, StringComparer.Ordinal))
        {
            var cover = TimeSpan.Zero;
            foreach (var t in group)
            {
                var overlap = (t.End < segment.End ? t.End : segment.End) - (t.Start > segment.Start ? t.Start : segment.Start);
                if (overlap > TimeSpan.Zero) cover += overlap;
            }
            if (cover > bestCover)
            {
                best = group.Key;
                bestCover = cover;
            }
        }
        return best;
    }
}
