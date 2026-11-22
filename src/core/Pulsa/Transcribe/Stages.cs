namespace PulsaTranscribe;

/// <summary>A stretch of audio, measured from the start of the audio it was found in.</summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>Who spoke when, on the audio handed to the diarizer. <see cref="Speaker"/> is the diarizer's own label.</summary>
public sealed record SpeakerTurn(TimeSpan Start, TimeSpan End, string Speaker);

/// <summary>How many speakers to expect: an exact count, or a range. Only a diarizer reads it.</summary>
public sealed record SpeakerCount(int Minimum, int Maximum)
{
    public static SpeakerCount Exactly(int count) => new(count, count);
}

/// <summary>What the preparation stage is asked to do beyond converting to 16 kHz mono.</summary>
public sealed record PrepareOptions(bool NoiseReduction = false, bool NormalizeLoudness = true);

/// <summary>
/// Maps a time on the prepared audio back to the input's timeline — piecewise linear through
/// <see cref="Anchors"/> (prepared, input), extended with slope 1 before the first and after the last anchor. No
/// anchors is the identity: preparation that only decodes and resamples does not move time.
/// </summary>
public sealed class TimeMap
{
    public static TimeMap Identity { get; } = new([]);

    public TimeMap(IReadOnlyList<(TimeSpan Prepared, TimeSpan Input)> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        for (var i = 1; i < anchors.Count; i++)
            if (anchors[i].Prepared <= anchors[i - 1].Prepared || anchors[i].Input < anchors[i - 1].Input)
                throw new ArgumentException("Time map anchors must move forward on both timelines.", nameof(anchors));
        Anchors = anchors;
    }

    public IReadOnlyList<(TimeSpan Prepared, TimeSpan Input)> Anchors { get; }

    /// <summary>A map that only shifts: prepared time 0 is <paramref name="inputOffset"/> on the input.</summary>
    public static TimeMap Offset(TimeSpan inputOffset) => new([(TimeSpan.Zero, inputOffset)]);

    public TimeSpan ToInput(TimeSpan prepared)
    {
        if (Anchors.Count == 0) return prepared;
        if (prepared <= Anchors[0].Prepared) return Anchors[0].Input + (prepared - Anchors[0].Prepared);
        for (var i = 1; i < Anchors.Count; i++)
        {
            var (p0, i0) = Anchors[i - 1];
            var (p1, i1) = Anchors[i];
            if (prepared <= p1)
                return i0 + (i1 - i0) * ((prepared - p0) / (p1 - p0));
        }
        var last = Anchors[^1];
        return last.Input + (prepared - last.Prepared);
    }
}

/// <summary>The preparation stage's result: the audio the later stages work on, and how to get back to the input.</summary>
public sealed record PreparedAudio(PcmAudio Audio, TimeSpan InputDuration, TimeMap Map);

/// <summary>Preparation port: any audio the implementation reads → 16 kHz mono PCM, with the map back to the input.</summary>
public interface IAudioPreparer
{
    /// <summary>The name reported in the result's stages.</summary>
    string Name { get; }

    /// <summary>Throws <see cref="InvalidDataException"/> when the input is not audio it can read.</summary>
    Task<PreparedAudio> PrepareAsync(string inputPath, PrepareOptions options, CancellationToken cancellationToken);
}

/// <summary>Segmentation port: where in the audio someone is speaking.</summary>
public interface IVoiceActivityDetector
{
    string Name { get; }
    Task<IReadOnlyList<TimeRange>> DetectAsync(PcmAudio audio, CancellationToken cancellationToken);
}

/// <summary>Diarization port: who spoke when, over the whole audio in one call.</summary>
public interface IDiarizer
{
    string Name { get; }
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(PcmAudio audio, SpeakerCount? speakers, CancellationToken cancellationToken);
}
