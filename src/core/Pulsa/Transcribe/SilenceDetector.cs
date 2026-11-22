namespace PulsaTranscribe;

/// <summary>
/// The built-in segmentation when no voice-activity detector is supplied: speech is where the level of 20 ms frames
/// rises above <see cref="ThresholdDbfs"/>; a quieter stretch shorter than the minimum silence does not split speech.
/// It finds pauses, not voices — music or noise above the threshold counts as speech.
/// </summary>
public static class SilenceDetector
{
    /// <summary>Frames below this level (dB relative to full scale) are silence.</summary>
    public const double ThresholdDbfs = -40;

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MinimumSpeech = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<TimeRange> Detect(PcmAudio audio, TimeSpan minimumSilence)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var samples = audio.Samples.Span;
        var frameLength = Math.Max(1, (int)(audio.SampleRate * Frame.TotalSeconds));
        var threshold = 32768 * Math.Pow(10, ThresholdDbfs / 20);

        var spans = new List<TimeRange>();
        int? speechStart = null;
        var lastSpeechEnd = 0;
        for (var offset = 0; offset < samples.Length; offset += frameLength)
        {
            var frame = samples.Slice(offset, Math.Min(frameLength, samples.Length - offset));
            double sum = 0;
            foreach (var s in frame) sum += (double)s * s;
            var loud = Math.Sqrt(sum / frame.Length) >= threshold;
            if (loud)
            {
                if (speechStart is { } start && Seconds(audio, offset - lastSpeechEnd) >= minimumSilence)
                {
                    Add(spans, audio, start, lastSpeechEnd);
                    speechStart = offset;
                }
                speechStart ??= offset;
                lastSpeechEnd = offset + frame.Length;
            }
        }
        if (speechStart is { } open) Add(spans, audio, open, lastSpeechEnd);
        return spans;
    }

    private static void Add(List<TimeRange> spans, PcmAudio audio, int from, int to)
    {
        var range = new TimeRange(Seconds(audio, from), Seconds(audio, to));
        if (range.Length >= MinimumSpeech) spans.Add(range);
    }

    private static TimeSpan Seconds(PcmAudio audio, int samples) => TimeSpan.FromSeconds((double)samples / audio.SampleRate);
}
