using AwesomeAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class FontSelectionLogTests
{
    // The renderer's log from one ffmpeg run on Windows (libass, DirectWrite): a family that is not installed, a family
    // named in Korean, a family that is the system fallback itself, and a family that cannot exist.
    private static readonly string[] RecordedLog =
    [
        "[Parsed_subtitles_0 @ 000002405bd5b480] fontselect: (IBM Plex Sans KR, 700, 0) -> Arial-BoldMT, 0, Arial-BoldMT",
        "[Parsed_subtitles_0 @ 000002405bd5b480] Glyph 0xC548 not found, selecting one more font for (IBM Plex Sans KR, 700, 0)",
        "[Parsed_subtitles_0 @ 000002405bd5b480] fontselect: (IBM Plex Sans KR, 700, 0) -> MalgunGothicBold, 0, MalgunGothicBold",
        "[Parsed_subtitles_0 @ 000002405bd5b480] fontselect: (맑은 고딕, 400, 0) -> MalgunGothic, 0, MalgunGothic",
        "[Parsed_subtitles_0 @ 000002405bd5b480] fontselect: (Arial, 400, 0) -> ArialMT, 0, ArialMT",
        "[Parsed_subtitles_0 @ 000002405bd5b480] Glyph 0xC548 not found, selecting one more font for (Arial, 400, 0)",
        "[Parsed_subtitles_0 @ 000002405bd5b480] fontselect: (Arial, 400, 0) -> MalgunGothic, 0, MalgunGothic",
        "frame=    1 fps=0.0 q=-0.0 Lsize=N/A time=00:00:00.04 bitrate=N/A speed=1.2x",
    ];

    private static readonly Dictionary<(int Weight, bool Italic), string> RecordedFallback = new()
    {
        [(400, false)] = "ArialMT",
        [(700, false)] = "Arial-BoldMT",
    };

    [Fact]
    public void PrimarySelections_TakesEachFamilysOwnFontAndSkipsSingleCharacterFallbacks()
    {
        var selections = FontSelectionLog.PrimarySelections(RecordedLog);

        selections.Should().Equal(
            new FontSelection("IBM Plex Sans KR", 700, false, "Arial-BoldMT"),
            new FontSelection("맑은 고딕", 400, false, "MalgunGothic"),
            new FontSelection("Arial", 400, false, "ArialMT"));
    }

    [Fact]
    public void PrimarySelections_ReadsAFontOpenedFromAFontsDirectory()
    {
        var selections = FontSelectionLog.PrimarySelections(
        [
            "[Parsed_subtitles_0 @ 0000019986e1a180] Loading font file 'fonts\\IBMPlexSansKR-Regular.ttf'",
            "[Parsed_subtitles_0 @ 0000019986e1a180] fontselect: (IBM Plex Sans KR, 700, 0) -> IBMPlexSansKR, 0, IBMPlexSansKR",
        ]);

        selections.Should().Equal(new FontSelection("IBM Plex Sans KR", 700, false, "IBMPlexSansKR"));
    }

    [Theory]
    [InlineData("Noto Sans KR", "NotoSansKR-Thin", true)]
    [InlineData("IBM Plex Sans KR", "IBMPlexSansKR-SmBld", true)]
    [InlineData("Arial", "ArialMT", true)]
    [InlineData("IBM Plex Sans KR", "Arial-BoldMT", false)]
    [InlineData("맑은 고딕", "MalgunGothic", false)]
    public void NameCarriesFamily_ComparesLettersAndDigitsOnly(string family, string postScriptName, bool expected) =>
        FontSelectionLog.NameCarriesFamily(family, postScriptName).Should().Be(expected);

    [Fact]
    public void Substitutions_ReportsOnlyAFamilyThatWasNotFound()
    {
        var substitutions = FontSelectionLog.Substitutions(FontSelectionLog.PrimarySelections(RecordedLog), RecordedFallback);

        // The Korean name is the installed Malgun Gothic (not the fallback font); Arial is the fallback font and its own.
        substitutions.Should().Equal(new FontSubstitution("IBM Plex Sans KR", Bold: true, UsedFont: "Arial-BoldMT"));
    }

    [Fact]
    public void Substitutions_WithoutAKnownFallbackForThatWeight_ReportsNothing()
    {
        var substitutions = FontSelectionLog.Substitutions(
            FontSelectionLog.PrimarySelections(RecordedLog), new Dictionary<(int Weight, bool Italic), string>());

        substitutions.Should().BeEmpty();
    }
}
