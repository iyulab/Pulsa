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
        return new TelegramChannelProvider(Token, new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, longPollTimeout: TimeSpan.FromSeconds(1), backlogPause: TimeSpan.FromMilliseconds(20));
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

    private static readonly (long Id, string Json)[] ServiceUpdates =
    [
        (10, """{"update_id":10,"message":{"message_id":1,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"hello"}}"""),
        (11, """{"update_id":11,"message":{"message_id":2,"from":{"id":42},"chat":{"id":-100,"type":"group"},"text":"to a group"}}"""),
        (12, """{"update_id":12,"message":{"message_id":3,"from":{"id":42},"chat":{"id":555,"type":"private"}}}"""),
        (13, """{"update_id":13,"edited_message":{"message_id":4}}"""),
    ];

    // The service as Telegram behaves: it answers at once with every update at or above the offset it is given, and forgets those below it.
    private static Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> Service(IEnumerable<(long Id, string Json)> updates, long[] floor)
    {
        var all = updates.ToArray();
        return async (_, body, token) =>
        {
            var offset = JsonNode.Parse(body)!["offset"]!.GetValue<long>();
            lock (floor) { floor[0] = Math.Max(floor[0], offset); }
            var remaining = all.Where(u => u.Id >= floor[0]).Select(u => u.Json).ToList();
            if (remaining.Count == 0) await Task.Delay(30, token);
            return Json(HttpStatusCode.OK, $$"""{"ok":true,"result":[{{string.Join(",", remaining)}}]}""");
        };
    }

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        return condition();
    }

    private static IEnumerable<long> Offsets(Handler handler) => handler.Requests.Where(r => r.Path.EndsWith("/getUpdates")).Select(r => JsonNode.Parse(r.Body)!["offset"]!.GetValue<long>());

    [Fact]
    public async Task Only_private_text_messages_become_events_and_the_service_is_told_only_after_the_consumer_has_read_past_them()
    {
        var provider = ProviderOver(Service(ServiceUpdates, [0]), out var handler);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        (await UntilAsync(() => Offsets(handler).Count() >= 4)).Should().BeTrue("the service keeps repeating what is unconfirmed");
        var read = (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).ToList();

        read.OfType<ConnectionChanged>().Select(e => e.State).Should().StartWith([ChannelState.Connecting, ChannelState.Connected]);
        var message = read.OfType<MessageReceived>().Should().ContainSingle("the repeats are not published again").Which;
        message.Should().BeEquivalentTo(new { Conversation = "555", Sender = "42", Text = "hello", MessageId = "555:1" }, options => options.ExcludingMissingMembers());
        Offsets(handler).Should().OnlyContain(o => o == 0, "nothing is confirmed while the consumer has not read the message");

        provider.Acknowledge(message.Seq);

        (await UntilAsync(() => Offsets(handler).Contains(14))).Should().BeTrue("the message is read, and the updates that became no event were waiting behind it");
        (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<MessageReceived>().Should().ContainSingle();
        await provider.DisposeAsync();
        provider.Status.State.Should().Be(ChannelState.Stopped);
    }

    [Fact]
    public async Task A_message_the_consumer_never_fetched_is_delivered_again_after_a_restart()
    {
        var floor = new long[1];
        var two = new (long Id, string Json)[]
        {
            (10, """{"update_id":10,"message":{"message_id":1,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"first"}}"""),
            (11, """{"update_id":11,"message":{"message_id":2,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"second"}}"""),
        };
        var service = Service(two, floor);
        var firstRun = ProviderOver(service, out var firstHandler);
        var firstEvents = new ChannelEventQueue();
        await firstRun.StartAsync(firstEvents, TestContext.Current.CancellationToken);
        (await UntilAsync(() => firstEvents.Last >= 4)).Should().BeTrue();
        var first = (await firstEvents.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<MessageReceived>().First(m => m.Text == "first");

        firstRun.Acknowledge(first.Seq);
        (await UntilAsync(() => Offsets(firstHandler).Contains(11))).Should().BeTrue();
        await firstRun.DisposeAsync();

        var secondRun = ProviderOver(service, out _);
        var secondEvents = new ChannelEventQueue();
        await secondRun.StartAsync(secondEvents, TestContext.Current.CancellationToken);
        (await UntilAsync(() => secondEvents.Last >= 3)).Should().BeTrue();
        var again = (await secondEvents.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<MessageReceived>().ToList();
        await secondRun.DisposeAsync();

        floor[0].Should().Be(11);
        again.Select(m => m.Text).Should().Equal(["second"], "the first was read before the restart, the second was not");
    }

    [Fact]
    public async Task An_update_with_a_lower_id_than_one_already_read_is_still_new_when_the_service_has_started_numbering_again()
    {
        // After a week without updates the service chooses the next update id at random, so "lower than the newest seen" does not mean "seen".
        var provider = ProviderOver(async (_, body, token) =>
        {
            var offset = JsonNode.Parse(body)!["offset"]!.GetValue<long>();
            if (offset == 0) return Json(HttpStatusCode.OK, """{"ok":true,"result":[{"update_id":900,"message":{"message_id":1,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"before the quiet week"}}]}""");
            if (offset == 901) return Json(HttpStatusCode.OK, """{"ok":true,"result":[{"update_id":7,"message":{"message_id":2,"from":{"id":42},"chat":{"id":555,"type":"private"},"text":"after the quiet week"}}]}""");
            await Task.Delay(30, token);
            return Json(HttpStatusCode.OK, """{"ok":true,"result":[]}""");
        }, out var handler);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        (await UntilAsync(() => events.Last >= 3)).Should().BeTrue();
        var first = (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<MessageReceived>().Single();
        provider.Acknowledge(first.Seq);
        (await UntilAsync(() => events.Last >= 4)).Should().BeTrue("the update numbered 7 is read although 900 was read before it");
        var texts = (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<MessageReceived>().Select(m => m.Text).ToList();
        await provider.DisposeAsync();

        texts.Should().Equal("before the quiet week", "after the quiet week");
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
        // The unreadable one became no event, so the cursor passes it (21) but not the good one, which the consumer has not read past (22).
        var offsets = handler.Requests.Where(r => r.Path.EndsWith("/getUpdates")).Select(r => JsonNode.Parse(r.Body)!["offset"]!.GetValue<long>()).ToList();
        offsets.Should().Contain(21).And.NotContain(22);
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
