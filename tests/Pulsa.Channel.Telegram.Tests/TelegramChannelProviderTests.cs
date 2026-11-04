using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Xunit;
using PulsaChannel;
using PulsaChannel.Telegram;

namespace Pulsa.Channel.Telegram.Tests;

public class TelegramChannelProviderTests
{
    private const string Token = "123456:secret-token";

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TelegramChannelProvider ProviderOver(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond, out Handler handler)
    {
        handler = new Handler(respond);
        return new TelegramChannelProvider(Token, new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, longPollTimeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_message_is_posted_to_send_message_and_its_id_comes_back()
    {
        var provider = ProviderOver((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":77}}""")), out var handler);

        var result = await provider.SendAsync(new ChannelSendRequest("555", "hello", new Uri("https://example.test/a")), TestContext.Current.CancellationToken);

        result.Should().Be(ChannelSendResult.Ok("555:77"));
        var sent = handler.Requests.Single();
        sent.Path.Should().Be($"/bot{Token}/sendMessage");
        JsonNode.Parse(sent.Body)!["chat_id"]!.GetValue<string>().Should().Be("555");
        JsonNode.Parse(sent.Body)!["text"]!.GetValue<string>().Should().Be("hello\nhttps://example.test/a");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, """{"ok":false,"parameters":{"retry_after":7}}""", ChannelErrorCode.RateLimited, true, "7 seconds")]
    [InlineData(HttpStatusCode.Forbidden, """{"ok":false,"description":"bot was blocked by the user"}""", ChannelErrorCode.UnknownConversation, false, "")]
    [InlineData(HttpStatusCode.BadRequest, """{"ok":false,"description":"chat not found"}""", ChannelErrorCode.UnknownConversation, false, "")]
    [InlineData(HttpStatusCode.Unauthorized, """{"ok":false}""", ChannelErrorCode.ServiceError, false, "")]
    [InlineData(HttpStatusCode.BadGateway, "not json", ChannelErrorCode.ServiceError, true, "")]
    public async Task What_the_service_answers_becomes_a_code_without_the_token_or_the_services_words(HttpStatusCode status, string body, ChannelErrorCode code, bool retryable, string mention)
    {
        var provider = ProviderOver((_, _, _) => Task.FromResult(Json(status, body)), out _);

        var result = await provider.SendAsync(new ChannelSendRequest("555", "hello"), TestContext.Current.CancellationToken);

        result.Delivered.Should().BeFalse();
        result.Error!.Code.Should().Be(code);
        result.Error.Retryable.Should().Be(retryable);
        result.Error.Message.Should().Contain(mention.Length > 0 ? mention : result.Error.Message).And.NotContain(Token).And.NotContain("blocked by the user");
    }

    [Fact]
    public async Task A_network_failure_is_not_connected_and_retryable_and_never_quotes_the_address()
    {
        var provider = ProviderOver((_, _, _) => throw new HttpRequestException($"could not reach https://api.telegram.org/bot{Token}/sendMessage"), out _);

        var result = await provider.SendAsync(new ChannelSendRequest("555", "hello"), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.NotConnected);
        result.Error.Retryable.Should().BeTrue();
        result.Error.Message.Should().NotContain(Token);
    }

    [Fact]
    public async Task Text_with_its_link_over_the_limit_is_refused_before_any_request()
    {
        var provider = ProviderOver((_, _, _) => throw new InvalidOperationException("no request expected"), out var handler);

        var result = await provider.SendAsync(new ChannelSendRequest("555", new string('a', 4090), new Uri("https://example.test/long-address")), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_private_text_messages_become_events_and_the_next_poll_asks_after_the_newest_update()
    {
        var calls = 0;
        var provider = ProviderOver(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Json(HttpStatusCode.OK, """
                    {"ok":true,"result":[
                      {"update_id":10,"message":{"message_id":1,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"hello"}},
                      {"update_id":11,"message":{"message_id":2,"from":{"id":42},"chat":{"id":-100,"type":"group"},"text":"to a group"}},
                      {"update_id":12,"message":{"message_id":3,"from":{"id":42},"chat":{"id":555,"type":"private"}}},
                      {"update_id":13,"edited_message":{"message_id":4}}
                    ]}
                    """);
            }

            await Task.Delay(30, token);
            return Json(HttpStatusCode.OK, """{"ok":true,"result":[]}""");
        }, out var handler);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        var read = await events.PollAsync(0, 10, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        while (read.Count < 3) read = await events.PollAsync(0, 10, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The second poll is made right after the first answer is handled; wait for it rather than assume it already happened.
        for (var i = 0; i < 100 && handler.Requests.Count(r => r.Path.EndsWith("/getUpdates")) < 2; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        await provider.DisposeAsync();

        read.OfType<ConnectionChanged>().Select(e => e.State).Should().StartWith([ChannelState.Connecting, ChannelState.Connected]);
        read.OfType<MessageReceived>().Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Conversation = "555", Sender = "42", Text = "hello", MessageId = "555:1" },
            options => options.ExcludingMissingMembers());
        var offsets = handler.Requests.Where(r => r.Path.EndsWith("/getUpdates")).Select(r => JsonNode.Parse(r.Body)!["offset"]!.GetValue<long>()).ToList();
        offsets[0].Should().Be(0);
        offsets[1].Should().Be(14);
        provider.Status.State.Should().Be(ChannelState.Stopped);
    }

    [Fact]
    public async Task A_refused_credential_stops_the_provider_and_says_so_without_the_token()
    {
        var provider = ProviderOver((_, _, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"ok":false}""")), out var handler);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        var read = await events.PollAsync(1, 10, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stopped = read.OfType<ConnectionChanged>().Single(e => e.State == ChannelState.Stopped);
        stopped.Detail.Should().Contain("credential").And.NotContain(Token);
        handler.Requests.Should().ContainSingle("a refused credential is not retried");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_dropped_poll_shows_reconnecting_and_a_later_answer_shows_connected_again()
    {
        var calls = 0;
        var provider = ProviderOver((_, _, _) =>
            Interlocked.Increment(ref calls) == 1
                ? throw new HttpRequestException("down")
                : Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true,"result":[]}""")), out _);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        var read = new List<ChannelEvent>();
        for (var i = 0; i < 10 && !read.OfType<ConnectionChanged>().Any(e => e.State == ChannelState.Connected); i++)
            read.AddRange(await events.PollAsync(read.Count == 0 ? 0 : read[^1].Seq, 20, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        await provider.DisposeAsync();

        read.OfType<ConnectionChanged>().Select(e => e.State).Should().ContainInOrder(ChannelState.Connecting, ChannelState.Reconnecting, ChannelState.Connected);
    }

    [Fact]
    public async Task An_update_the_provider_cannot_read_is_skipped_once_and_never_leaves_the_status_wrong_or_the_loop_stuck()
    {
        var calls = 0;
        var provider = ProviderOver(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Json(HttpStatusCode.OK, """
                    {"ok":true,"result":[
                      {"update_id":"not a number","message":{"message_id":1,"chat":{"id":555,"type":"private"},"text":"unreadable id"}},
                      {"update_id":20,"message":{"message_id":"x","chat":{"id":"555","type":"private"},"text":"wrong field types"}},
                      {"update_id":21,"message":{"message_id":3,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"good one"}}
                    ]}
                    """);
            }

            await Task.Delay(30, token);
            return Json(HttpStatusCode.OK, """{"ok":true,"result":[]}""");
        }, out var handler);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        var read = new List<ChannelEvent>();
        for (var i = 0; i < 10 && read.OfType<MessageReceived>().Count() < 2; i++)
            read.AddRange(await events.PollAsync(read.Count == 0 ? 0 : read[^1].Seq, 20, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var status = provider.Status.State;
        await provider.DisposeAsync();

        // The message with an unreadable update id is itself readable, so it is delivered; the one whose fields have the wrong types is not.
        read.OfType<MessageReceived>().Select(m => m.Text).Should().Equal("unreadable id", "good one");
        read.OfType<ConnectionChanged>().Select(e => e.State).Should().NotContain(ChannelState.Reconnecting);
        status.Should().Be(ChannelState.Connected);
        handler.Requests.Where(r => r.Path.EndsWith("/getUpdates")).Select(r => JsonNode.Parse(r.Body)!["offset"]!.GetValue<long>()).ElementAt(1).Should().Be(22, "the cursor passed every update that named itself");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"ok":true,"result":{"id":1}}""", ChannelState.Connected)]
    [InlineData(HttpStatusCode.Unauthorized, """{"ok":false}""", ChannelState.Stopped)]
    [InlineData(HttpStatusCode.BadGateway, "not json", ChannelState.Reconnecting)]
    public async Task A_probe_looks_at_the_connection_without_reading_messages(HttpStatusCode status, string body, ChannelState expected)
    {
        var provider = ProviderOver((_, _, _) => Task.FromResult(Json(status, body)), out var handler);

        var probed = await provider.ProbeAsync(TestContext.Current.CancellationToken);

        probed.State.Should().Be(expected);
        probed.Detail.Should().NotContain(Token);
        handler.Requests.Should().ContainSingle().Which.Path.Should().Be($"/bot{Token}/getMe", "getUpdates would confirm messages to the service");
    }

    [Fact]
    public async Task A_probe_that_cannot_reach_the_service_says_reconnecting_without_the_address()
    {
        var provider = ProviderOver((_, _, _) => throw new HttpRequestException($"could not reach https://api.telegram.org/bot{Token}/getMe"), out _);

        var probed = await provider.ProbeAsync(TestContext.Current.CancellationToken);

        probed.State.Should().Be(ChannelState.Reconnecting);
        probed.Detail.Should().NotContain(Token);
    }

    private sealed record Seen(string Path, string Body);

    private sealed class Handler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly List<Seen> requests = [];

        public IReadOnlyList<Seen> Requests
        {
            get { lock (requests) { return requests.ToList(); } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (requests) { requests.Add(new Seen(request.RequestUri!.AbsolutePath, body)); }
            return await respond(request, body, cancellationToken);
        }
    }
}
