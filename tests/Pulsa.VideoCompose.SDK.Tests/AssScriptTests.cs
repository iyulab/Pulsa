using FluentAssertions;
using PulsaVideoCompose;
using Xunit;

namespace Pulsa.VideoCompose.SDK.Tests;

public class AssScriptTests
{
    private static string StyleLine(string script) =>
        script.Split("\r\n").Single(l => l.StartsWith("Style: ", StringComparison.Ordinal));

    // Style fields, in the Format line's order.
    private static string[] StyleFields(string script) => StyleLine(script)["Style: ".Length..].Split(',');

    [Fact]
    public void The_scripts_coordinate_space_is_the_output_frame()
    {
        var script = AssScript.Generate(["a"], 2, CaptionStyle.DefaultCaptions, CaptionPlacement.Bottom, 1080, 1920);

        script.Should().Contain("PlayResX: 1080\r\n").And.Contain("PlayResY: 1920\r\n");
    }

    [Theory]
    [InlineData(1080, 72)]  // 1080 / 15
    [InlineData(1920, 128)] // 1920 / 15
    public void A_cards_font_size_is_a_share_of_the_frame_height_in_frame_pixels(int height, int expected)
    {
        // The defect this replaces: 72 written into a 288-line space drew text a quarter of the frame high.
        var script = AssScript.Generate(["Title"], 2, CaptionStyle.DefaultCards, CaptionPlacement.Center, 1920, height);

        StyleFields(script)[2].Should().Be(expected.ToString());
    }

    [Fact]
    public void Default_captions_keep_the_size_they_had_as_subrip()
    {
        // SubRip's default 16 on a 288-line space is 60 px on a 1080-pixel frame.
        var script = AssScript.Generate(["a"], 2, CaptionStyle.DefaultCaptions, CaptionPlacement.Bottom, 1920, 1080);

        StyleFields(script)[2].Should().Be("60");
    }

    [Fact]
    public void The_band_is_painted_with_the_box_colour_in_the_outline_field()
    {
        // Border style 3 draws its box with OutlineColour; BackColour is the shadow. Translucent black #00000080.
        var fields = StyleFields(AssScript.Generate(["a"], 2, CaptionStyle.DefaultCaptions, CaptionPlacement.Bottom, 1920, 1080));

        fields[15].Should().Be("3", "border style 3 is the box behind the text");
        fields[5].Should().Be("&H7F000000", "the band colour, half transparent");
        fields[3].Should().Be("&H00FFFFFF", "the text colour, opaque white");
    }

    [Theory]
    [InlineData(CaptionPlacement.Bottom, "2")]
    [InlineData(CaptionPlacement.Center, "5")]
    public void Placement_uses_numpad_alignment(CaptionPlacement placement, string expected)
    {
        var fields = StyleFields(AssScript.Generate(["a"], 2, CaptionStyle.DefaultCaptions, placement, 1920, 1080));

        fields[18].Should().Be(expected);
    }

    [Fact]
    public void A_style_chooses_font_size_colours_bold_and_margin()
    {
        var style = new CaptionStyle(FontName: "Noto Sans KR", FontSizeRatio: 0.05, Bold: true,
            TextColor: "#FFD700", BoxColor: "#102030CC", BottomMarginRatio: 0.1);

        var fields = StyleFields(AssScript.Generate(["a"], 2, style, CaptionPlacement.Bottom, 1920, 1080));

        fields[1].Should().Be("Noto Sans KR");
        fields[2].Should().Be("54");
        fields[3].Should().Be("&H0000D7FF");
        fields[5].Should().Be("&H33302010");
        fields[7].Should().Be("-1");
        fields[21].Should().Be("108");
    }

    [Fact]
    public void Cues_follow_each_other_scene_by_scene_from_the_offset()
    {
        var script = AssScript.Generate(["one", "two"], 2.52, CaptionStyle.DefaultCaptions, CaptionPlacement.Bottom, 1920, 1080,
            startOffsetSeconds: 2.52);

        var cues = script.Split("\r\n").Where(l => l.StartsWith("Dialogue: ", StringComparison.Ordinal)).ToList();
        cues.Should().Equal(
            "Dialogue: 0,0:00:02.52,0:00:05.04,Default,,0,0,0,,one",
            "Dialogue: 0,0:00:05.04,0:00:07.56,Default,,0,0,0,,two");
    }

    [Theory]
    [InlineData("two\nlines", "two\\Nlines")]
    [InlineData("two\r\nlines", "two\\Nlines")]
    [InlineData("a {brace}", "a \\{brace\\}")]
    [InlineData("C:\\new", "C:\\\u2060new")]
    [InlineData("back\\slash", "back\\slash")]
    public void Caption_text_reads_as_written(string caption, string expected)
    {
        var script = AssScript.Generate([caption], 2, CaptionStyle.DefaultCaptions, CaptionPlacement.Bottom, 1920, 1080);

        var cue = script.Split("\r\n").Single(l => l.StartsWith("Dialogue: ", StringComparison.Ordinal));
        cue[(cue.IndexOf(",,", StringComparison.Ordinal) + ",,0,0,0,,".Length)..].Should().Be(expected);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#GG0000")]
    public void An_unreadable_colour_is_refused(string colour)
    {
        var act = () => (CaptionStyle.DefaultCaptions with { BoxColor = colour }).Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*#RRGGBB*");
    }

    [Fact]
    public void A_font_name_with_a_comma_is_refused()
    {
        var act = () => (CaptionStyle.DefaultCaptions with { FontName = "Arial, bold" }).Validate();

        act.Should().Throw<ArgumentException>();
    }
}
