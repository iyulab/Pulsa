using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class CaptionDrafterTests
{
    [Fact]
    public async Task DraftAsync_ParsesOneCaptionPerLine_MatchingImageCount()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "First caption\nSecond caption\nThird caption")));

        var captions = await CaptionDrafter.DraftAsync(
            chatClient,
            new DraftCaptionsRequest(["a.png", "b.png", "c.png"], "A product demo video."));

        captions.Should().Equal("First caption", "Second caption", "Third caption");
    }

    [Fact]
    public async Task DraftAsync_StripsListMarkersAndBlankLines()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "- First\n\n* Second\n")));

        var captions = await CaptionDrafter.DraftAsync(
            chatClient, new DraftCaptionsRequest(["a.png", "b.png"], "intro"));

        captions.Should().Equal("First", "Second");
    }

    [Fact]
    public async Task DraftAsync_FewerLinesThanImages_Throws()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Only one line")));

        var act = () => CaptionDrafter.DraftAsync(chatClient, new DraftCaptionsRequest(["a.png", "b.png"], "intro"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DraftAsync_NoImages_Throws()
    {
        var chatClient = Substitute.For<IChatClient>();

        var act = () => CaptionDrafter.DraftAsync(chatClient, new DraftCaptionsRequest([], "intro"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DraftAsync_PassesImageCountAndIntroTextIntoThePrompt()
    {
        var chatClient = Substitute.For<IChatClient>();
        string? capturedPrompt = null;
        chatClient
            .GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedPrompt = msgs.Single().Text), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "a\nb")));

        await CaptionDrafter.DraftAsync(chatClient, new DraftCaptionsRequest(["a.png", "b.png"], "A Product Hunt launch video."));

        capturedPrompt.Should().Contain("2-scene");
        capturedPrompt.Should().Contain("A Product Hunt launch video.");
    }
}
