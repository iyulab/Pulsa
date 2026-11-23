using System.Text.Json;
using AwesomeAssertions;
using PulsaExtract;
using Xunit;

namespace Pulsa.Tests.Extract;

public sealed class ExtractJsonTests
{
    private const string Text = "Invoice 2026-11-02: total 1,200 won, paid by card. 승인번호 77-12.";

    // --- The schema

    [Fact]
    public void ASchemaWithEveryKindOfField_IsRead()
    {
        var schema = ExtractJson.ReadSchema("""
            {"fields":[
              {"name":"total","type":"number","required":true,"description":"the amount due"},
              {"name":"paid","type":"Date"},
              {"name":"method","type":"enum","values":["card","cash"]},
              {"name":"ref","type":"string","many":true,"pattern":"[0-9-]+"},
              {"name":"settled","type":"boolean"}
            ]}
            """);

        schema.Fields.Select(f => (f.Name, f.Type)).Should().Equal(
            ("total", FieldType.Number), ("paid", FieldType.Date), ("method", FieldType.Enum), ("ref", FieldType.String), ("settled", FieldType.Boolean));
        schema.Fields[0].Should().Match<ExtractField>(f => f.Required && f.Description == "the amount due");
        schema.Fields[2].Values.Should().Equal("card", "cash");
        schema.Fields[3].Should().Match<ExtractField>(f => f.Many && f.Pattern == "[0-9-]+" && !f.Required);
    }

    [Fact]
    public void ABareListOfFields_IsAlsoASchema()
    {
        ExtractJson.ReadSchema("""[{"name":"total","type":"number"}]""").Fields.Should().ContainSingle().Which.Name.Should().Be("total");
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"other":1}""", "list of fields")]
    [InlineData("""[1]""", "not an object")]
    [InlineData("""[{"type":"number"}]""", "no name")]
    [InlineData("""[{"name":"a"}]""", "no type")]
    [InlineData("""[{"name":"a","type":"integer"}]""", "must be string, number, date, boolean or enum")]
    [InlineData("""[{"name":"a","type":"number","required":"yes"}]""", "true or false")]
    [InlineData("""[{"name":"a","type":"enum","values":"x"}]""", "list of strings")]
    [InlineData("""[{"name":"a","type":"enum","values":[1]}]""", "list of strings")]
    public void ASchemaThatCannotBeRead_SaysWhatIsWrong(string json, string expected)
    {
        var act = () => ExtractJson.ReadSchema(json);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{expected}*");
    }

    // --- The candidates

    [Fact]
    public void Candidates_AreReadInTheShapeTheModelIsAskedToAnswerIn()
    {
        var candidates = ExtractJson.ReadCandidates("""{"fields":{"total":[{"value":"1200","quote":"1,200 won"}],"method":{"value":"card","quote":"by card"}}}""");

        candidates.Should().Equal(new Candidate("total", "1200", "1,200 won"), new Candidate("method", "card", "by card"));
    }

    [Fact]
    public void Candidates_ThatAreNotAnObject_AreRefused()
    {
        var act = () => ExtractJson.ReadCandidates("no values here");

        act.Should().Throw<InvalidDataException>().WithMessage("*fields*");
    }

    // --- The result

    [Fact]
    public void TheResultIsWrittenAsTheContractGivesIt_WithKoreanLeftReadable()
    {
        var schema = ExtractJson.ReadSchema("""[{"name":"total","type":"number","required":true},{"name":"approval","type":"string"},{"name":"due","type":"date","required":true}]""");
        var result = Extractor.Verify(Text, schema, ExtractJson.ReadCandidates(
            """{"fields":{"total":[{"value":"1200","quote":"1,200 won"}],"approval":[{"value":"77-12","quote":"승인번호 77-12"}],"extra":[{"value":"x","quote":"y"}]}}"""));

        using var document = JsonDocument.Parse(ExtractJson.Write(result));
        var root = document.RootElement;

        var total = root.GetProperty("fields")[0];
        total.GetProperty("name").GetString().Should().Be("total");
        total.GetProperty("value").GetString().Should().Be("1200");
        total.GetProperty("quote").GetString().Should().Be("1,200 won");
        total.GetProperty("span").GetProperty("start").GetInt32().Should().Be(Text.IndexOf("1,200 won", StringComparison.Ordinal));
        total.GetProperty("match").GetString().Should().Be("exact");
        root.GetProperty("rejected").EnumerateArray().Select(r => (r.GetProperty("name").GetString(), r.GetProperty("reason").GetString()))
            .Should().BeEquivalentTo([("extra", "not-in-schema"), ("due", "missing")]);
        ExtractJson.Write(result).Should().Contain("승인번호 77-12").And.NotContain("\\u");
    }

    [Fact]
    public void AValueOrQuoteThatWasNotGiven_IsLeftOutOfARejectedEntry()
    {
        var schema = ExtractJson.ReadSchema("""[{"name":"due","type":"date","required":true}]""");
        var result = Extractor.Verify(Text, schema, []);

        using var document = JsonDocument.Parse(ExtractJson.Write(result));
        var missing = document.RootElement.GetProperty("rejected")[0];

        missing.TryGetProperty("value", out _).Should().BeFalse();
        missing.TryGetProperty("quote", out _).Should().BeFalse();
        missing.GetProperty("reason").GetString().Should().Be("missing");
    }
}
