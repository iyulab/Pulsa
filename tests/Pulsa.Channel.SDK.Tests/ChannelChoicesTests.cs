using AwesomeAssertions;
using Xunit;
using PulsaChannel;

namespace Pulsa.Channel.SDK.Tests;

public class ChannelChoicesTests
{
    private static readonly ChannelChoice[] YesNo = [new("yes", "Yes"), new("no", "No")];

    private static async Task<(ChannelConnector Connector, FakeChannelProvider Provider)> StartedAsync(ChannelChoiceSupport? choices = null)
    {
        var provider = new FakeChannelProvider(choices: choices);
        var connector = new ChannelConnector(provider);
        await connector.StartAsync(TestContext.Current.CancellationToken);
        return (connector, provider);
    }

    private static async Task<List<ChoiceSelected>> PicksAsync(ChannelConnector connector) =>
        (await connector.Events.PollAsync(0, 100, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<ChoiceSelected>().ToList();

    [Fact]
    public async Task A_pick_of_an_offered_choice_is_one_event_that_names_the_message_and_the_choice_and_a_second_pick_is_not_taken()
    {
        var (connector, provider) = await StartedAsync();
        var sent = await connector.SendAsync(new ChannelSendRequest("conversation-1", "Send it?", Choices: YesNo), TestContext.Current.CancellationToken);

        provider.Choose("person-1", sent.MessageId!, "yes").Should().BeTrue();
        provider.Choose("person-1", sent.MessageId!, "no").Should().BeFalse("a message takes one pick");

        (await PicksAsync(connector)).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Conversation = "conversation-1", Sender = "person-1", MessageId = sent.MessageId, ChoiceId = "yes", Closed = true });
    }

    [Fact]
    public async Task A_choice_the_message_did_not_offer_or_a_message_that_offered_none_takes_no_pick()
    {
        var (connector, provider) = await StartedAsync();
        var withChoices = await connector.SendAsync(new ChannelSendRequest("conversation-1", "Send it?", Choices: YesNo), TestContext.Current.CancellationToken);
        var plain = await connector.SendAsync(new ChannelSendRequest("conversation-1", "FYI"), TestContext.Current.CancellationToken);

        provider.Choose("person-1", withChoices.MessageId!, "maybe").Should().BeFalse();
        provider.Choose("person-1", plain.MessageId!, "yes").Should().BeFalse();
        provider.Choose("person-1", "no-such-message", "yes").Should().BeFalse();

        (await PicksAsync(connector)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_service_that_takes_no_choices_refuses_the_send_with_a_code_and_sends_nothing_not_even_as_plain_text()
    {
        var provider = new NoChoicesProvider();
        var connector = new ChannelConnector(provider);

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "Send it?", Choices: YesNo), TestContext.Current.CancellationToken);

        result.Delivered.Should().BeFalse();
        result.Error.Should().BeEquivalentTo(new { Code = ChannelErrorCode.NotSupported, Retryable = false }, options => options.ExcludingMissingMembers());
        provider.Sends.Should().Be(0);
    }

    [Fact]
    public async Task An_empty_list_of_choices_is_a_plain_message_even_for_a_service_that_takes_none()
    {
        var provider = new NoChoicesProvider();
        var connector = new ChannelConnector(provider);

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "FYI", Choices: []), TestContext.Current.CancellationToken);

        result.Delivered.Should().BeTrue();
        provider.Sends.Should().Be(1);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("empty id")]
    [InlineData("empty label")]
    [InlineData("long id")]
    [InlineData("long label")]
    [InlineData("duplicate id")]
    public async Task Choices_outside_what_the_service_takes_are_refused_before_the_provider_sees_them(string fault)
    {
        var (connector, provider) = await StartedAsync(new ChannelChoiceSupport(MaxChoices: 3, MaxIdBytes: 8, MaxLabelLength: 10));
        ChannelChoice[] choices = fault switch
        {
            "count" => [new("a", "A"), new("b", "B"), new("c", "C"), new("d", "D")],
            "empty id" => [new(" ", "A")],
            "empty label" => [new("a", "")],
            "long id" => [new("123456789", "A")],
            "long label" => [new("a", "12345678901")],
            _ => [new("a", "A"), new("a", "B")],
        };

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "Pick", Choices: choices), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest);
        result.Error.Retryable.Should().BeFalse();
        provider.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_choice_id_is_measured_in_bytes_because_that_is_what_the_service_limits()
    {
        var (connector, _) = await StartedAsync(new ChannelChoiceSupport(MaxChoices: 3, MaxIdBytes: 4, MaxLabelLength: 10));

        var result = await connector.SendAsync(new ChannelSendRequest("conversation-1", "Pick", Choices: [new("ééé", "A")]), TestContext.Current.CancellationToken);

        result.Error!.Code.Should().Be(ChannelErrorCode.InvalidRequest, "three two-byte characters are six bytes");
    }

    private sealed class NoChoicesProvider : IChannelProvider
    {
        public int Sends { get; private set; }

        public string Name => "plain";

        public int MaxTextLength => 100;

        public ChannelStatus Status => new(Name, ChannelState.Connected, DateTimeOffset.UnixEpoch);

        public Task StartAsync(IChannelEventSink sink, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ChannelSendResult> SendAsync(ChannelSendRequest request, CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(ChannelSendResult.Ok("plain-1"));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
