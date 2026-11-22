using AwesomeAssertions;
using Microsoft.Extensions.AI;
using PulsaRedact;
using Xunit;

namespace Pulsa.Media.Tests.Redact;

public class ChatRegionDetectorTests
{
    [Fact]
    public void Parse_ReadsTheBoxesInTheFirstJsonArray_AroundAnyProse()
    {
        var regions = ChatRegionDetector.Parse("""
            Here is what I found:
            [{"label": "face", "x": 10, "y": 12.6, "width": 40, "height": 50, "confidence": 0.92}]
            """);

        regions.Should().ContainSingle().Which.Should().Be(new DetectedRegion(new RedactRegion(10, 13, 40, 50), "face", 0.92));
    }

    [Fact]
    public void Parse_DropsEntriesThatAreNotBoxes_AndDistrustsAMissingConfidence()
    {
        var regions = ChatRegionDetector.Parse("""
            [{"label": "a", "x": 0, "y": 0, "width": 0, "height": 5, "confidence": 1},
             {"label": "b", "x": "left", "y": 0, "width": 5, "height": 5},
             "not an object",
             {"x": 1, "y": 2, "width": 3, "height": 4}]
            """);

        regions.Should().ContainSingle().Which.Should().Be(new DetectedRegion(new RedactRegion(1, 2, 3, 4), "region", 0));
    }

    [Theory]
    [InlineData("I cannot help with that.")]
    [InlineData("[not json")]
    [InlineData("[]")]
    public void Parse_AnAnswerWithNoBoxes_IsEmpty(string answer)
    {
        ChatRegionDetector.Parse(answer).Should().BeEmpty();
    }

    [Fact]
    public async Task DetectAsync_SendsTheImageAndItsSizeToTheModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsa-detect-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47], TestContext.Current.CancellationToken);
        var chat = new RecordingChatClient("""[{"label":"face","x":1,"y":2,"width":3,"height":4,"confidence":0.7}]""");
        try
        {
            var regions = await new ChatRegionDetector(chat, "faces").DetectAsync(path, 640, 480, TestContext.Current.CancellationToken);

            regions.Should().ContainSingle();
            var message = chat.Messages.Should().ContainSingle().Subject;
            message.Contents.OfType<DataContent>().Should().ContainSingle().Which.MediaType.Should().Be("image/png");
            message.Text.Should().Contain("640 pixels wide").And.Contain("480 pixels tall").And.Contain("faces");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingChatClient(string reply) : IChatClient
    {
        public List<ChatMessage> Messages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
