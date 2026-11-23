using AwesomeAssertions;
using FFMpegCore;
using PulsaRedact;
using Xunit;

namespace Pulsa.Media.Tests.Redact;

public class PictureSizeTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pulsa-picturesize-test-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Parse_TakesTheSizeOfTheFirstStreamThatHasOne()
    {
        PictureSize.Parse("""{"streams":[{"index":0,"width":1920,"height":1080}]}""").Should().Be((1920, 1080));
        PictureSize.Parse("""{"streams":[{"codec_type":"audio"},{"width":640,"height":360}]}""").Should().Be((640, 360));
    }

    [Theory]
    [InlineData("""{"streams":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"streams":[{"width":0,"height":0}]}""")]
    [InlineData("""{"streams":[{"width":640}]}""")]
    public void Parse_AnswerWithoutAPicture_IsNull(string json) => PictureSize.Parse(json).Should().BeNull();

    [Fact]
    public async Task ReadAsync_GivesTheSizeOfARealPicture()
    {
        var path = Path.Combine(_tempDir, "size.png");
        var options = new FFOptions { BinaryFolder = Environment.GetEnvironmentVariable("PULSA_FFMPEG_PATH") ?? string.Empty };
        await FFMpegArguments
            .FromFileInput("color=c=green:s=123x45:d=1", verifyExists: false, opt => opt.WithCustomArgument("-f lavfi"))
            .OutputToFile(path, overwrite: true, opt => opt.WithCustomArgument("-frames:v 1"))
            .ProcessAsynchronously(ffMpegOptions: options);

        var size = await PictureSize.ReadAsync(path, options, TestContext.Current.CancellationToken);

        size.Should().Be((123, 45));
    }

    [Theory]
    [InlineData("""{"format":{"duration":"12.480000"}}""", 12.48)]
    [InlineData("""{"format":{"duration":"0.5"}}""", 0.5)]
    public void ParseDuration_TheContainersLength_IsInSeconds(string json, double seconds) =>
        PictureSize.ParseDuration(json).Should().Be(seconds);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"format":{}}""")]
    [InlineData("""{"format":{"duration":"N/A"}}""")]
    [InlineData("""{"format":{"duration":"0"}}""")]
    public void ParseDuration_NoUsableLength_IsNull(string json) => PictureSize.ParseDuration(json).Should().BeNull();

    [Fact]
    public async Task ReadAsync_AFileThatIsNoPicture_SaysSo()
    {
        var path = Path.Combine(_tempDir, "notes.txt");
        await File.WriteAllTextAsync(path, "not a picture", TestContext.Current.CancellationToken);
        var options = new FFOptions { BinaryFolder = Environment.GetEnvironmentVariable("PULSA_FFMPEG_PATH") ?? string.Empty };

        var act = () => PictureSize.ReadAsync(path, options, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidDataException>();
    }
}
