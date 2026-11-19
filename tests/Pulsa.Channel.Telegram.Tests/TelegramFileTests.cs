using System.Net;
using System.Text;
using AwesomeAssertions;
using Xunit;
using PulsaChannel;
using PulsaChannel.Telegram;

namespace Pulsa.Channel.Telegram.Tests;

public class TelegramFileTests
{
    private const string Token = "123456:secret-token";

    private sealed record Seen(string Path, string ContentType, string Body);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.RequestUri!.AbsolutePath, request.Content?.Headers.ContentType?.MediaType ?? "", body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ChannelAttachment Attachment(string name, string mediaType, string content) =>
        new(name, mediaType, Encoding.UTF8.GetByteCount(content), () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static TelegramChannelProvider Over(Handler handler) => new(Token, new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") });

    [Fact]
    public async Task A_file_is_uploaded_as_a_document_with_the_text_as_its_caption_and_the_message_id_comes_back()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":91}}"""));
        var provider = Over(handler);

        var result = await provider.SendAsync(new ChannelSendRequest("555", "The Q3 numbers", new Uri("https://example.test/q3"), File: "q3.csv") { Attachment = Attachment("q3.csv", "text/csv", "a,b\n1,2\n") }, TestContext.Current.CancellationToken);

        result.Should().Be(ChannelSendResult.Ok("555:91"));
        var upload = handler.Requests.Single();
        upload.Path.Should().Be($"/bot{Token}/sendDocument");
        upload.ContentType.Should().Be("multipart/form-data");
        upload.Body.Should().Contain("name=chat_id").And.Contain("555")
            .And.Contain("name=caption").And.Contain("The Q3 numbers\nhttps://example.test/q3")
            .And.Contain("name=document").And.Contain("filename=q3.csv").And.Contain("Content-Type: text/csv").And.Contain("a,b\n1,2\n");
    }

    [Fact]
    public async Task A_file_with_choices_carries_the_keyboard_as_a_json_field()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":92}}"""));
        var provider = Over(handler);

        await provider.SendAsync(new ChannelSendRequest("555", "Keep it?", Choices: [new("yes", "Yes")], File: "a.txt") { Attachment = Attachment("a.txt", "text/plain", "x") }, TestContext.Current.CancellationToken);

        handler.Requests.Single().Body.Should().Contain("name=reply_markup").And.Contain("\"callback_data\":\"yes\"");
    }

    [Fact]
    public async Task What_the_service_answers_to_an_upload_becomes_a_code_without_the_token_or_the_file()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.BadRequest, """{"ok":false,"description":"chat not found"}"""));
        var provider = Over(handler);

        var result = await provider.SendAsync(new ChannelSendRequest("555", "caption", File: "a.txt") { Attachment = Attachment("a.txt", "text/plain", "secret content") }, TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.UnknownConversation);
        result.Error.Message.Should().NotContain(Token).And.NotContain("secret content").And.NotContain("chat not found");
    }

    [Fact]
    public async Task An_upload_that_cannot_reach_the_service_is_not_connected_and_retryable()
    {
        var handler = new Handler(_ => throw new HttpRequestException($"could not reach https://api.telegram.org/bot{Token}/sendDocument"));
        var provider = Over(handler);

        var result = await provider.SendAsync(new ChannelSendRequest("555", "caption", File: "a.txt") { Attachment = Attachment("a.txt", "text/plain", "x") }, TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.NotConnected);
        result.Error.Retryable.Should().BeTrue();
        result.Error.Message.Should().NotContain(Token);
    }
}
