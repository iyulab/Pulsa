using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using PulsaTranscript;
using Xunit;

namespace Pulsa.Transcript.SDK.Tests;

public class TranscriptRefinerTests
{
    private static IReadOnlyList<TranscriptCue> Cues(params string[] texts) =>
        texts.Select((t, i) => new TranscriptCue(TimeSpan.FromSeconds(i * 5), TimeSpan.FromSeconds(i * 5 + 4), t)).ToList();

    private static IChatClient Replying(params string[] replies)
    {
        var client = Substitute.For<IChatClient>();
        var queue = new Queue<string>(replies);
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, queue.Dequeue())));
        return client;
    }

    [Fact]
    public async Task Applies_gated_corrections_and_keeps_every_timing()
    {
        var cues = Cues("참석제는 저 김인수 팀장", "박준호 사원입니다", "Volt enorme");
        var client = Replying("[0] 참석자는 저 김민수 팀장\n[2] [unclear]");

        var result = await TranscriptRefiner.RefineAsync(client, new RefineTranscriptRequest(cues, new Glossary(["김민수", "박준호"])));

        result.Cues.Select(c => c.Text).Should().Equal("참석자는 저 김민수 팀장", "박준호 사원입니다", TranscriptRefiner.Unclear);
        result.Cues.Select(c => (c.Start, c.End)).Should().Equal(cues.Select(c => (c.Start, c.End)));
        result.Applied.Should().HaveCount(2);
        result.Applied[0].Should().Be(new CueChange(0, "참석제는 저 김인수 팀장", "참석자는 저 김민수 팀장", CueChangeKind.Corrected));
        result.Applied[1].Kind.Should().Be(CueChangeKind.Unclear);
        result.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task A_correction_that_fails_the_gate_is_kept_as_a_rejected_suggestion_not_applied()
    {
        var cues = Cues("1주 금요일까지 이 센 승인을 받아 오겠습니다");
        var client = Replying("[0] 1주 금요일까지 배너 시안 승인을 받아 오겠습니다");

        var result = await TranscriptRefiner.RefineAsync(client, new RefineTranscriptRequest(cues, new Glossary(["배너 시안"])));

        result.Cues[0].Text.Should().Be("1주 금요일까지 이 센 승인을 받아 오겠습니다");
        result.Applied.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Refined.Should().Be("1주 금요일까지 배너 시안 승인을 받아 오겠습니다");
    }

    [Fact]
    public async Task Lines_for_unknown_indexes_and_chatter_are_ignored()
    {
        var cues = Cues("같은 장수에서 진행하겠습니다.");
        var client = Replying("Here are the corrections:\n[0] 같은 장소에서 진행하겠습니다.\n[7] invented\nDone.");

        var result = await TranscriptRefiner.RefineAsync(client, new RefineTranscriptRequest(cues, Glossary.Empty));

        result.Cues[0].Text.Should().Be("같은 장소에서 진행하겠습니다.");
        result.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task A_long_transcript_is_sent_in_batches_with_global_indexes()
    {
        var cues = Cues("a0", "a1", "a2", "a3", "a4");
        var client = Replying("[1] a1x", "[3] a3x", "");

        var result = await TranscriptRefiner.RefineAsync(client,
            new RefineTranscriptRequest(cues, Glossary.Empty), new RefineTranscriptOptions { BatchSize = 2 });

        await client.Received(3).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        result.Applied.Select(c => c.Index).Should().Equal(1, 3);
    }

    [Fact]
    public async Task The_prompt_carries_the_glossary_and_numbered_cues()
    {
        var cues = Cues("첫 문장", "둘째 문장");
        IEnumerable<ChatMessage>? sent = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => sent = m.ToList()), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

        await TranscriptRefiner.RefineAsync(client, new RefineTranscriptRequest(cues, new Glossary(["김민수"])));

        var text = string.Join("\n", sent!.Select(m => m.Text));
        text.Should().Contain("김민수").And.Contain("[0] 첫 문장").And.Contain("[1] 둘째 문장");
    }

    [Fact]
    public void Glossary_reads_a_markdown_list_taking_the_term_before_a_dash_note()
    {
        var glossary = Glossary.FromMarkdown("# Glossary\n## People\n- 김민수 — 팀장\n- 이서연 - 대리\n\n* 사은품\nplain line\n");

        glossary.Terms.Should().Equal("김민수", "이서연", "사은품");
    }
}
