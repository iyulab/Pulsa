using AwesomeAssertions;
using PulsaTranscribe;
using PulsaTranscript;
using Xunit;

namespace Pulsa.Tests.Transcribe;

public class TranscriptScoreTests
{
    [Fact]
    public void WordErrorRate_CountsSubstitutionsAndDeletions()
    {
        var score = TranscriptScore.WordErrorRate("the cat sat on the mat", "the cat sit on mat");

        score.Should().Be(new ErrorRate(Substitutions: 1, Deletions: 1, Insertions: 0, ReferenceLength: 6));
        score.Rate.Should().BeApproximately(2.0 / 6, 1e-9);
    }

    [Fact]
    public void WordErrorRate_CountsInsertions()
    {
        TranscriptScore.WordErrorRate("on the mat", "on the mat today")
            .Should().Be(new ErrorRate(Substitutions: 0, Deletions: 0, Insertions: 1, ReferenceLength: 3));
    }

    [Fact]
    public void Formatting_IsNotCountedAsError()
    {
        TranscriptScore.WordErrorRate("Hello, World!", "hello   world").Errors.Should().Be(0);
    }

    [Fact]
    public void CharacterErrorRate_IgnoresSpacing_AsKoreanSpacingVaries()
    {
        // "복음" misheard as "보금": two of the reference's syllables, both changed; the spacing difference is free.
        var score = TranscriptScore.CharacterErrorRate("복음의 권능", "보금의권능");

        score.Should().Be(new ErrorRate(Substitutions: 2, Deletions: 0, Insertions: 0, ReferenceLength: 5));
    }

    [Fact]
    public void ComposedAndDecomposedHangul_AreTheSame()
    {
        var decomposed = "한국어".Normalize(System.Text.NormalizationForm.FormD);

        TranscriptScore.CharacterErrorRate("한국어", decomposed).Errors.Should().Be(0);
    }

    [Fact]
    public void AnEmptyReference_CountsEveryInsertedUnit()
    {
        TranscriptScore.WordErrorRate("", "two words").Rate.Should().Be(2);
        TranscriptScore.WordErrorRate("", "").Rate.Should().Be(0);
    }

    [Fact]
    public void Text_JoinsCuesInTimeOrder()
    {
        TranscriptCue[] cues =
        [
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), "second"),
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "first"),
        ];

        TranscriptScore.Text(cues).Should().Be("first second");
    }
}
