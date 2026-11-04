using AwesomeAssertions;
using Xunit;
using PulsaChannel;

namespace Pulsa.Channel.SDK.Tests;

public class ChannelConnectorTests
{
    private static async Task<(ChannelConnector Connector, FakeChannelProvider Provider)> StartedAsync(int maxTextLength = 50, IEnumerable<string?>? secrets = null)
    {
        var provider = new FakeChannelProvider(maxTextLength: maxTextLength);
        var connector = new ChannelConnector(provider, secrets);
        await connector.StartAsync(TestContext.Current.CancellationToken);
        return (connector, provider);
    }

    [Fact]
    public async Task A_valid_send_reaches_the_provider_and_returns_the_services_message_id()
    {
        var (connector, provider) = await StartedAsync();

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "hello"), TestContext.Current.CancellationToken);

        result.Delivered.Should().BeTrue();
        result.MessageId.Should().NotBeNullOrEmpty();
        provider.Sent.Should().ContainSingle().Which.Text.Should().Be("hello");
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("  ", "text")]
    [InlineData("conversation-1", "")]
    [InlineData("conversation-1", "   ")]
    public async Task An_empty_conversation_or_text_is_refused_before_the_provider_sees_it(string conversation, string text)
    {
        var (connector, provider) = await StartedAsync();

        var result = await connector.SendAsync(new ChannelSendRequest(conversation, text), TestContext.Current.CancellationToken);

        result.Delivered.Should().BeFalse();
        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
        result.Error.Retryable.Should().BeFalse();
        provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Text_longer_than_the_providers_limit_is_refused()
    {
        var (connector, provider) = await StartedAsync(maxTextLength: 10);

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", new string('a', 11)), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
        result.Error.Message.Should().Contain("10");
        provider.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://example.test/file")]
    public async Task A_link_must_be_an_absolute_http_or_https_address(string link)
    {
        var (connector, _) = await StartedAsync();

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "hi", new Uri(link, UriKind.RelativeOrAbsolute)), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
    }

    [Fact]
    public async Task An_unknown_conversation_is_not_retryable_and_a_dropped_connection_is()
    {
        var (connector, provider) = await StartedAsync();

        var unknown = await connector.SendAsync(new ChannelSendRequest("nobody", "hi"), TestContext.Current.CancellationToken);
        provider.Disconnect();
        var dropped = await connector.SendAsync(new ChannelSendRequest("conversation-1", "hi"), TestContext.Current.CancellationToken);

        unknown.Error!.Should().Be(new ChannelError(ChannelErrorCode.UnknownConversation, "The fake provider does not know that conversation.", false));
        dropped.Error!.Code.Should().Be(ChannelErrorCode.NotConnected);
        dropped.Error.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task A_provider_that_throws_becomes_a_service_error_that_carries_neither_the_exception_nor_the_credential()
    {
        var connector = new ChannelConnector(new ThrowingProvider("token-123"), ["token-123"]);

        var result = await connector.SendAsync(new ChannelSendRequest("c", "secret text"), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.ServiceError);
        result.Error.Message.Should().NotContain("token-123").And.NotContain("secret text");
    }

    [Fact]
    public async Task A_credential_in_a_providers_error_message_is_redacted()
    {
        var (connector, provider) = await StartedAsync(secrets: ["token-123"]);
        provider.FailNextSend(ChannelSendResult.Fail(ChannelErrorCode.ServiceError, "the service said no to token-123", retryable: false));

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "hi"), TestContext.Current.CancellationToken);

        result.Error!.Message.Should().Be("the service said no to ***");
    }

    [Fact]
    public async Task Status_follows_the_connection()
    {
        var (connector, provider) = await StartedAsync();
        connector.Status.State.Should().Be(ChannelState.Connected);

        provider.Disconnect();
        connector.Status.State.Should().Be(ChannelState.Reconnecting);

        provider.Reconnect();
        connector.Status.State.Should().Be(ChannelState.Connected);
        connector.Status.Provider.Should().Be("fake");
    }

    [Fact]
    public async Task Events_come_in_order_with_a_sequence_that_rises_by_one()
    {
        var (connector, provider) = await StartedAsync();
        provider.Receive("conversation-1", "person-a", "first");
        provider.Receive("conversation-1", "person-a", "second");

        var events = (await connector.PollAsync(0, null, 10, TimeSpan.Zero, TestContext.Current.CancellationToken)).Events;

        events.Select(e => e.Seq).Should().Equal(1, 2, 3);
        events[0].Should().BeOfType<ConnectionChanged>().Which.Type.Should().Be("connection_changed");
        events[1].Should().BeOfType<MessageReceived>().Which.Text.Should().Be("first");
        events[2].Should().BeOfType<MessageReceived>().Which.Type.Should().Be("message_received");
    }

    [Fact]
    public async Task Asking_again_with_the_last_seen_sequence_returns_only_what_came_after()
    {
        var (connector, provider) = await StartedAsync();
        provider.Receive("conversation-1", "person-a", "first");
        var firstRead = (await connector.PollAsync(0, null, 10, TimeSpan.Zero, TestContext.Current.CancellationToken)).Events;
        provider.Receive("conversation-1", "person-a", "second");

        var second = (await connector.PollAsync(firstRead[^1].Seq, null, 10, TimeSpan.Zero, TestContext.Current.CancellationToken)).Events;
        var again = (await connector.PollAsync(0, null, 10, TimeSpan.Zero, TestContext.Current.CancellationToken)).Events;

        second.Should().ContainSingle().Which.Should().BeOfType<MessageReceived>().Which.Text.Should().Be("second");
        again.Should().HaveCount(3, "a read does not consume — a lost reply loses nothing");
    }

    [Fact]
    public async Task A_long_poll_returns_when_an_event_arrives_and_an_empty_list_when_the_time_runs_out()
    {
        var (connector, provider) = await StartedAsync();
        var seen = connector.Events.Last;

        var empty = (await connector.PollAsync(seen, null, 10, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)).Events;
        var waiting = connector.PollAsync(seen, null, 10, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        provider.Receive("conversation-1", "person-a", "hello");
        var arrived = (await waiting).Events;

        empty.Should().BeEmpty();
        arrived.Should().ContainSingle().Which.Should().BeOfType<MessageReceived>();
    }

    [Fact]
    public async Task Poll_arguments_outside_the_documented_range_are_refused()
    {
        var (connector, _) = await StartedAsync();

        Func<Task> tooMany = () => connector.PollAsync(0, null, 101, TimeSpan.Zero, TestContext.Current.CancellationToken);
        Func<Task> tooLong = () => connector.PollAsync(0, null, 10, TimeSpan.FromSeconds(31), TestContext.Current.CancellationToken);

        await tooMany.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task The_queue_keeps_only_its_capacity_of_the_newest_events()
    {
        var queue = new ChannelEventQueue(capacity: 3);
        for (var i = 0; i < 5; i++) queue.Publish(new ConnectionChanged(ChannelState.Connected, i.ToString()));

        var kept = await queue.PollAsync(0, 10, TimeSpan.Zero, TestContext.Current.CancellationToken);

        kept.Select(e => e.Seq).Should().Equal(3, 4, 5);
    }

    [Fact]
    public async Task A_cursor_from_another_run_is_told_to_start_over_instead_of_silently_getting_nothing()
    {
        var (connector, provider) = await StartedAsync();
        provider.Receive("conversation-1", "person-a", "hello");
        var token = TestContext.Current.CancellationToken;

        var staleEpoch = await connector.PollAsync(2, "an-epoch-of-a-run-that-ended", 10, TimeSpan.Zero, token);
        var aheadOfThisRun = await connector.PollAsync(500, null, 10, TimeSpan.Zero, token);
        var current = await connector.PollAsync(2, staleEpoch.Epoch, 10, TimeSpan.Zero, token);

        staleEpoch.Reset.Should().BeTrue();
        staleEpoch.Events.Select(e => e.Seq).Should().Equal(1, 2);
        aheadOfThisRun.Reset.Should().BeTrue();
        aheadOfThisRun.Events.Should().HaveCount(2);
        current.Reset.Should().BeFalse();
        current.Events.Should().BeEmpty();
        current.Epoch.Should().Be(staleEpoch.Epoch);
    }

    [Fact]
    public async Task Events_dropped_because_too_many_arrived_unread_are_reported_as_a_gap()
    {
        var provider = new FakeChannelProvider();
        var connector = new ChannelConnector(provider, events: new ChannelEventQueue(capacity: 3));
        await connector.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++) provider.Receive("conversation-1", "person-a", $"message {i}");
        var token = TestContext.Current.CancellationToken;

        var behind = await connector.PollAsync(1, null, 10, TimeSpan.Zero, token);
        var fromStart = await connector.PollAsync(0, null, 10, TimeSpan.Zero, token);
        var caughtUp = await connector.PollAsync(5, null, 10, TimeSpan.Zero, token);

        behind.Gap.Should().BeTrue("events 2 to 3 were dropped before this read");
        behind.Events.Select(e => e.Seq).Should().Equal(4, 5, 6);
        fromStart.Gap.Should().BeTrue();
        caughtUp.Gap.Should().BeFalse();
        caughtUp.Events.Select(e => e.Seq).Should().Equal(6);
    }

    private sealed class ThrowingProvider(string credential) : IChannelProvider
    {
        public string Name => "throwing";

        public int MaxTextLength => 100;

        public ChannelStatus Status => new(Name, ChannelState.Connected, DateTimeOffset.UnixEpoch);

        public Task StartAsync(IChannelEventSink sink, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"call with {credential} and {request.Text} failed");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
