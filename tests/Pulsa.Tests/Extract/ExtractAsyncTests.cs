using AwesomeAssertions;
using Microsoft.Extensions.AI;
using PulsaExtract;
using Xunit;

namespace Pulsa.Tests.Extract;

public class ExtractAsyncTests
{
    private const string Minutes = "Decision: ship the beta on 2026-12-08. Owner: Lee Jiwon. Budget approved: 30,000,000 won.";

    private static readonly ExtractSchema Schema = new([
        new ExtractField("ship_date", FieldType.Date) { Required = true, Description = "the agreed release date" },
        new ExtractField("owner", FieldType.String) { Required = true },
        new ExtractField("budget", FieldType.Number),
    ]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheModelsAnswer_IsVerifiedValueByValue()
    {
        // The model gets the budget wrong (3,000,000): it is refused, not returned.
        var chat = new ReplyingChatClient("""
            ```json
            {"fields": {
              "ship_date": [{"value": "2026-12-08", "quote": "ship the beta on 2026-12-08"}],
              "owner": [{"value": "Lee Jiwon", "quote": "Owner: Lee Jiwon"}],
              "budget": [{"value": 3000000, "quote": "Budget approved: 30,000,000 won"}]
            }}
            ```
            """);

        var result = await Extractor.ExtractAsync(chat, Minutes, Schema, cancellationToken: Ct);

        result.Fields.Select(f => (f.Name, f.Value)).Should().Equal(("ship_date", "2026-12-08"), ("owner", "Lee Jiwon"));
        result.Rejected.Should().ContainSingle().Which.Should().Match<RejectedValue>(r => r.Name == "budget" && r.Reason == RejectReason.NotSupported);
    }

    [Fact]
    public async Task ThePromptCarriesTheSchemaAndTheText_AndAsksForWordsCopiedAsWritten()
    {
        var chat = new ReplyingChatClient("{}");

        await Extractor.ExtractAsync(chat, Minutes, Schema, cancellationToken: Ct);

        var prompt = chat.Prompts.Should().ContainSingle().Subject;
        prompt.Should().Contain("ship_date (date, as yyyy-MM-dd, required): the agreed release date")
            .And.Contain("copied exactly as written").And.EndWith(Minutes);
    }

    [Fact]
    public async Task AnAnswerThatCannotBeRead_ReportsTheRequiredFieldsMissing_SayingWhy()
    {
        var result = await Extractor.ExtractAsync(new ReplyingChatClient("Sorry, I could not find that."), Minutes, Schema, cancellationToken: Ct);

        result.Fields.Should().BeEmpty();
        result.Rejected.Should().HaveCount(2).And.OnlyContain(r => r.Reason == RejectReason.Missing && r.Detail.Contains("could not be read"));
    }

    [Fact]
    public async Task AnErrorFromTheModel_EndsTheCall()
    {
        var act = () => Extractor.ExtractAsync(new ReplyingChatClient(new HttpRequestException("down")), Minutes, Schema, cancellationToken: Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ASchemaError_IsRefusedBeforeTheModelIsCalled()
    {
        var chat = new ReplyingChatClient("{}");

        var act = () => Extractor.ExtractAsync(chat, Minutes, new ExtractSchema([]), cancellationToken: Ct);

        await act.Should().ThrowAsync<ArgumentException>();
        chat.Prompts.Should().BeEmpty();
    }

    [Fact]
    public void ParseAnswer_AcceptsAFlatObjectAndSingleValues()
    {
        var candidates = Extractor.ParseAnswer("""{"owner": {"value": "Lee", "quote": "Lee"}, "paid": [{"value": true, "quote": "paid"}]}""");

        candidates.Should().Equal(new Candidate("owner", "Lee", "Lee"), new Candidate("paid", "true", "paid"));
    }

    private sealed class ReplyingChatClient : IChatClient
    {
        private readonly string? _reply;
        private readonly Exception? _error;

        public ReplyingChatClient(string reply) => _reply = reply;
        public ReplyingChatClient(Exception error) => _error = error;

        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
            if (_error is not null) throw _error;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
