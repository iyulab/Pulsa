using System.Buffers.Binary;

namespace PulsaTranscribe;

/// <summary>
/// Mono 16-bit PCM audio held in memory — the form every transcription stage after preparation works on. The facade
/// requires 16 kHz; <see cref="SampleRate"/> is carried so a slice keeps its own rate.
/// </summary>
public sealed class PcmAudio
{
    /// <summary>The rate the transcription stages expect.</summary>
    public const int TranscriptionSampleRate = 16_000;

    public PcmAudio(int sampleRate, ReadOnlyMemory<short> samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        Samples = samples;
    }

    public int SampleRate { get; }
    public ReadOnlyMemory<short> Samples { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>The samples from <paramref name="start"/> to <paramref name="end"/>, clamped to the audio.</summary>
    public PcmAudio Slice(TimeSpan start, TimeSpan end)
    {
        var from = Math.Clamp((int)Math.Round(start.TotalSeconds * SampleRate), 0, Samples.Length);
        var to = Math.Clamp((int)Math.Round(end.TotalSeconds * SampleRate), from, Samples.Length);
        return new PcmAudio(SampleRate, Samples[from..to]);
    }

    /// <summary>Writes the audio as a 16-bit mono PCM WAV file.</summary>
    public void WriteWav(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var dataBytes = Samples.Length * 2;
        var buffer = new byte[44 + dataBytes];
        var header = buffer.AsSpan();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);            // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);            // mono
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);            // block align
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);           // bits per sample
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        var samples = Samples.Span;
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(header[(44 + i * 2)..], samples[i]);
        destination.Write(buffer);
    }

    /// <summary>
    /// Reads a WAV file that is already 16 kHz mono 16-bit PCM. Anything else is refused with
    /// <see cref="InvalidDataException"/> — converting other audio is the preparation stage's work.
    /// </summary>
    public static PcmAudio ReadTranscriptionWav(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = new BinaryReader(source, System.Text.Encoding.ASCII, leaveOpen: true);
        try
        {
            if (new string(reader.ReadChars(4)) != "RIFF") throw NotTranscriptionWav("it is not a RIFF file");
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") throw NotTranscriptionWav("it is not a WAVE file");

            short? format = null, channels = null, bits = null;
            int? rate = null;
            while (true)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadInt32();
                if (id == "fmt ")
                {
                    format = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    rate = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadInt16();
                    bits = reader.ReadInt16();
                    reader.ReadBytes(size - 16 + (size & 1));
                }
                else if (id == "data")
                {
                    if (format != 1 || channels != 1 || bits != 16 || rate != TranscriptionSampleRate)
                        throw NotTranscriptionWav(
                            $"it is format {format?.ToString() ?? "?"}, {channels?.ToString() ?? "?"} channel(s), " +
                            $"{bits?.ToString() ?? "?"} bit, {rate?.ToString() ?? "?"} Hz");
                    var bytes = reader.ReadBytes(size);
                    var samples = new short[bytes.Length / 2];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2));
                    return new PcmAudio(TranscriptionSampleRate, samples);
                }
                else
                {
                    reader.ReadBytes(size + (size & 1));
                }
            }
        }
        catch (EndOfStreamException)
        {
            throw NotTranscriptionWav("it ends before its audio data");
        }
    }

    private static InvalidDataException NotTranscriptionWav(string why) =>
        new($"Without an audio preparer the audio must be 16 kHz mono 16-bit PCM WAV, but {why}.");
}
