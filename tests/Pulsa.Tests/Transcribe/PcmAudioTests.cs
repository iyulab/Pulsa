using AwesomeAssertions;
using PulsaTranscribe;
using Xunit;

namespace Pulsa.Tests.Transcribe;

public class PcmAudioTests
{
    [Fact]
    public void AWavWrittenByPcmAudio_ReadsBackTheSameSamples()
    {
        var audio = Audio.Of(Audio.Tone(0.25));
        using var stream = new MemoryStream();
        audio.WriteWav(stream);
        stream.Position = 0;

        var read = PcmAudio.ReadTranscriptionWav(stream);

        read.SampleRate.Should().Be(16_000);
        read.Samples.ToArray().Should().Equal(audio.Samples.ToArray());
    }

    [Fact]
    public void AWavAtAnotherRate_IsRefusedWithItsFormat()
    {
        using var stream = new MemoryStream();
        new PcmAudio(44_100, new short[441]).WriteWav(stream);
        stream.Position = 0;

        var act = () => PcmAudio.ReadTranscriptionWav(stream);

        act.Should().Throw<InvalidDataException>().WithMessage("*44100 Hz*");
    }

    [Fact]
    public void ATimeMapWithAnchors_IsPiecewiseLinear()
    {
        // prepared 0–10 s is input 0–20 s (a stretched stretch), then slope 1 after.
        var map = new TimeMap([(TimeSpan.Zero, TimeSpan.Zero), (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))]);

        map.ToInput(TimeSpan.FromSeconds(5)).Should().Be(TimeSpan.FromSeconds(10));
        map.ToInput(TimeSpan.FromSeconds(12)).Should().Be(TimeSpan.FromSeconds(22));
    }

    [Fact]
    public void ATimeMapThatGoesBackwards_IsRefused()
    {
        var act = () => new TimeMap([(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)), (TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4))]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ADataChunkThatClaimsMoreThanTheStreamHolds_ReadsOnlyWhatIsThere()
    {
        var bytes = WavBytes(Audio.Of(Audio.Tone(0.1)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), uint.MaxValue);

        var read = PcmAudio.ReadTranscriptionWav(new MemoryStream(bytes));

        read.Samples.Length.Should().Be(1600);
    }

    [Fact]
    public void AnUnknownChunkThatClaimsMoreThanTheStreamHolds_IsRefused()
    {
        var bytes = WavBytes(Audio.Of(Audio.Tone(0.1)));
        // Rename the fmt chunk so the reader skips it as unknown, with a size past the end.
        "junk"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), uint.MaxValue - 1);

        var act = () => PcmAudio.ReadTranscriptionWav(new MemoryStream(bytes));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void AFormatChunkWithAnImpossibleSize_IsRefused()
    {
        var bytes = WavBytes(Audio.Of(Audio.Tone(0.1)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 8);

        var act = () => PcmAudio.ReadTranscriptionWav(new MemoryStream(bytes));

        act.Should().Throw<InvalidDataException>().WithMessage("*format chunk*");
    }

    private static byte[] WavBytes(PcmAudio audio)
    {
        using var stream = new MemoryStream();
        audio.WriteWav(stream);
        return stream.ToArray();
    }
}
