using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class ConcatListWriterTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pulsa-videocompose-test-").FullName;

    [Fact]
    public async Task WriteAsync_WritesOneFileLinePerClip_InOrder()
    {
        var listPath = Path.Combine(_tempDir, "concat.txt");

        await ConcatListWriter.WriteAsync(["a.mp4", "b.mp4", "c.mp4"], listPath);

        var lines = await File.ReadAllLinesAsync(listPath);
        lines.Should().Equal("file 'a.mp4'", "file 'b.mp4'", "file 'c.mp4'");
    }

    [Fact]
    public async Task WriteAsync_EscapesSingleQuotesInPaths()
    {
        var listPath = Path.Combine(_tempDir, "concat.txt");

        await ConcatListWriter.WriteAsync(["o'brien.mp4"], listPath);

        var content = await File.ReadAllTextAsync(listPath);
        content.Should().Contain(@"file 'o'\''brien.mp4'");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
