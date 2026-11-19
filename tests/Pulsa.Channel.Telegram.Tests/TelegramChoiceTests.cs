using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Xunit;
using PulsaChannel;
using PulsaChannel.Telegram;

namespace Pulsa.Channel.Telegram.Tests;

public class TelegramChoiceTests
{
    private const string Token = "123456:secret-token";

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Press(long updateId, string data, string chatType = "private", long chat = 555, long messageNo = 77) => new JsonObject
    {
        ["update_id"] = updateId,
        ["callback_query"] = new JsonObject
        {
            ["id"] = $"q{updateId}",
            ["from"] = new JsonObject { ["id"] = 42 },
            ["data"] = data,
            ["message"] = new JsonObject { ["message_id"] = messageNo, ["chat"] = new JsonObject { ["id"] = chat, ["type"] = chatType } },
        },
    }.ToJsonString();

    // The service as it behaves for one run: it hands over every update at or above the offset it is given, once each id is read it stays until confirmed.
    private sealed class Service(IEnumerable<(long Id, string Json)> updates, bool failEdit = false)
    {
        private readonly (long Id, string Json)[] all = updates.ToArray();
        private readonly List<(string Path, string Body)> seen = [];

        public IReadOnlyList<(string Path, string Body)> Seen
        {
            get { lock (seen) { return seen.ToList(); } }
        }

        public IEnumerable<JsonNode> Bodies(string method) => Seen.Where(r => r.Path.EndsWith("/" + method)).Select(r => JsonNode.Parse(r.Body)!);

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, string body, CancellationToken token)
        {
            lock (seen) { seen.Add((request.RequestUri!.AbsolutePath, body)); }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/sendMessage")) return Json(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":77}}""");
            if (path.EndsWith("/editMessageReplyMarkup")) return failEdit ? Json(HttpStatusCode.BadRequest, """{"ok":false}""") : Json(HttpStatusCode.OK, """{"ok":true,"result":true}""");
            if (path.EndsWith("/answerCallbackQuery")) return Json(HttpStatusCode.OK, """{"ok":true,"result":true}""");
            var offset = JsonNode.Parse(body)!["offset"]!.GetValue<long>();
            var remaining = all.Where(u => u.Id >= offset).Select(u => u.Json).ToList();
            if (remaining.Count == 0) await Task.Delay(30, token);
            return Json(HttpStatusCode.OK, $$"""{"ok":true,"result":[{{string.Join(",", remaining)}}]}""");
        }
    }

    private sealed class Handler(Service service) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return await service.RespondAsync(request, body, cancellationToken);
        }
    }

    private static TelegramChannelProvider ProviderOver(Service service) =>
        new(Token, new HttpClient(new Handler(service)) { BaseAddress = new Uri("https://api.telegram.org/") }, longPollTimeout: TimeSpan.FromSeconds(1), backlogPause: TimeSpan.FromMilliseconds(20));

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        return condition();
    }

    [Fact]
    public async Task A_message_with_choices_is_sent_with_one_button_per_row_whose_data_is_the_choice_id()
    {
        var service = new Service([]);
        var provider = ProviderOver(service);

        var result = await provider.SendAsync(new ChannelSendRequest("555", "Send it?", Choices: [new("yes", "Yes, send"), new("no", "Not now")]), TestContext.Current.CancellationToken);

        result.Should().Be(ChannelSendResult.Ok("555:77"));
        var keyboard = service.Bodies("sendMessage").Single()["reply_markup"]!["inline_keyboard"]!.AsArray();
        keyboard.Should().HaveCount(2);
        keyboard[0]![0]!["text"]!.GetValue<string>().Should().Be("Yes, send");
        keyboard[0]![0]!["callback_data"]!.GetValue<string>().Should().Be("yes");
        keyboard[1]![0]!["callback_data"]!.GetValue<string>().Should().Be("no");
    }

    [Fact]
    public async Task A_plain_message_carries_no_keyboard()
    {
        var service = new Service([]);

        await ProviderOver(service).SendAsync(new ChannelSendRequest("555", "FYI"), TestContext.Current.CancellationToken);

        service.Bodies("sendMessage").Single().AsObject().ContainsKey("reply_markup").Should().BeFalse();
    }

    [Fact]
    public async Task The_first_press_is_one_event_the_buttons_are_taken_off_and_a_second_press_is_answered_already_chosen_and_never_published()
    {
        var service = new Service([(20, Press(20, "yes")), (21, Press(21, "no"))]);
        var provider = ProviderOver(service);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        (await UntilAsync(() => service.Bodies("answerCallbackQuery").Count() >= 2)).Should().BeTrue("both presses are answered");
        var picks = (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<ChoiceSelected>().ToList();

        picks.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Conversation = "555", Sender = "42", MessageId = "555:77", ChoiceId = "yes", Closed = true });
        var edits = service.Bodies("editMessageReplyMarkup").ToList();
        edits.Should().ContainSingle().Which["message_id"]!.GetValue<long>().Should().Be(77);
        edits[0]["reply_markup"]!["inline_keyboard"]!.AsArray().Should().BeEmpty();
        var answers = service.Bodies("answerCallbackQuery").ToList();
        answers.Single(a => a["callback_query_id"]!.GetValue<string>() == "q20").AsObject().ContainsKey("text").Should().BeFalse();
        answers.Single(a => a["callback_query_id"]!.GetValue<string>() == "q21")["text"]!.GetValue<string>().Should().Be("Already chosen.");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task When_the_buttons_cannot_be_taken_off_the_event_says_so()
    {
        var service = new Service([(30, Press(30, "yes"))], failEdit: true);
        var provider = ProviderOver(service);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        (await UntilAsync(() => service.Bodies("answerCallbackQuery").Any())).Should().BeTrue();

        (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<ChoiceSelected>().Should().ContainSingle().Which.Closed.Should().BeFalse();
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_press_in_a_group_or_without_data_is_answered_so_the_spinner_stops_and_is_no_event()
    {
        var service = new Service([(40, Press(40, "yes", chatType: "group", chat: -100)), (41, Press(41, ""))]);
        var provider = ProviderOver(service);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        (await UntilAsync(() => service.Bodies("answerCallbackQuery").Count() >= 2)).Should().BeTrue();

        (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<ChoiceSelected>().Should().BeEmpty();
        service.Bodies("editMessageReplyMarkup").Should().BeEmpty();
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_press_is_confirmed_to_the_service_only_after_the_consumer_has_read_past_its_event()
    {
        var service = new Service([(50, Press(50, "yes"))]);
        var provider = ProviderOver(service);
        var events = new ChannelEventQueue();

        await provider.StartAsync(events, TestContext.Current.CancellationToken);
        (await UntilAsync(() => service.Bodies("answerCallbackQuery").Any())).Should().BeTrue();
        var pick = (await events.PollAsync(0, 20, TimeSpan.Zero, TestContext.Current.CancellationToken)).OfType<ChoiceSelected>().Single();
        service.Bodies("getUpdates").Select(b => b["offset"]!.GetValue<long>()).Should().OnlyContain(o => o == 0);

        provider.Acknowledge(pick.Seq);

        (await UntilAsync(() => service.Bodies("getUpdates").Any(b => b["offset"]!.GetValue<long>() == 51))).Should().BeTrue();
        service.Bodies("getUpdates").Last()["allowed_updates"]!.AsArray().Select(a => a!.GetValue<string>()).Should().BeEquivalentTo("message", "callback_query");
        await provider.DisposeAsync();
    }
}
