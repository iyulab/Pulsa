using FluentAssertions;
using PulsaTranscript;
using Xunit;

namespace Pulsa.Transcript.SDK.Tests;

/// <summary>
/// The gate a proposed correction must pass: every changed stretch has to sound like what it replaces.
/// Cases are from a real run on a Korean meeting transcript — the accepted ones were right, the rejected
/// ones were plausible-looking inventions pulled in by the glossary.
/// </summary>
public class CorrectionGateTests
{
    private static readonly CorrectionGate Gate = new();

    [Theory]
    [InlineData("참석제는 저 김인수 팀장", "참석자는 저 김민수 팀장")]      // one consonant off
    [InlineData("이사 연 드리", "이서연 대리")]                            // re-spaced, vowels off
    [InlineData("지금부터 마케팅 팀 주간 회를 시작하겠습니다", "지금부터 마케팅 팀 주간 회의를 시작하겠습니다")]
    [InlineData("할인보다 사은 품 선호가", "할인보다 사은품 선호가")]        // spacing only
    [InlineData("같은 장수에서 진행하겠습니다.", "같은 장소에서 진행하겠습니다.")]
    [InlineData("the meeting on tusday", "the meeting on Tuesday")]
    public void Accepts_a_correction_that_sounds_like_the_original(string original, string refined)
    {
        Gate.Accepts(original, refined).Should().BeTrue();
    }

    [Theory]
    [InlineData("1주 금요일까지 이 센 승인을 받아 오겠습니다", "1주 금요일까지 배너 시안 승인을 받아 오겠습니다")]
    [InlineData("'유가 끝난 다음 줄아서 고객 반응이 가장 좋을 것 같습니다.'", "'가을 프로모션이 끝난 다음에 할인이 고객 반응이 가장 좋을 것 같습니다.'")]
    [InlineData("1주 금요일까지 승인을 받아 오겠습니다", "1주 후 금요일까지 승인을 받아 오겠습니다")] // inserted a word
    [InlineData("the budget is ten", "the budget is ten million won")]
    public void Rejects_a_correction_that_brings_in_words_the_original_does_not_sound_like(string original, string refined)
    {
        Gate.Accepts(original, refined).Should().BeFalse();
    }

    [Fact]
    public void An_unchanged_cue_passes()
    {
        Gate.Accepts("박준호 사원입니다", "박준호 사원입니다").Should().BeTrue();
    }
}
