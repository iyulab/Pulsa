using AwesomeAssertions;
using PulsaExtract;
using Xunit;

namespace Pulsa.Tests.Extract;

// The contract is docs/extract.md.
public class VerifyTests
{
    private const string Invoice = """
        Invoice INV-2048 from Hanbit Trading, issued 2026년 11월 3일.
        Total due: 1,234.50 KRW by November 30, 2026. Paid: no.
        Contact: Kim Minsu.
        """;

    private static readonly ExtractSchema Schema = new([
        new ExtractField("invoice_number", FieldType.String) { Required = true, Pattern = "INV-\\d+" },
        new ExtractField("vendor", FieldType.String) { Required = true },
        new ExtractField("issued", FieldType.Date),
        new ExtractField("due", FieldType.Date),
        new ExtractField("total", FieldType.Number) { Required = true },
        new ExtractField("paid", FieldType.Boolean),
        new ExtractField("currency", FieldType.Enum) { Values = ["KRW", "USD"] },
        new ExtractField("contacts", FieldType.String) { Many = true },
    ]);

    [Fact]
    public void GroundedValues_AreReturnedWithWhereTheyWereRead()
    {
        var result = Extractor.Verify(Invoice, Schema, [
            new("invoice_number", "INV-2048", "Invoice INV-2048"),
            new("vendor", "Hanbit Trading", "from Hanbit Trading"),
            new("issued", "2026-11-03", "issued 2026년 11월 3일"),
            new("due", "2026-11-30", "by November 30, 2026"),
            new("total", "1234.5", "Total due: 1,234.50 KRW"),
            new("paid", "false", "Paid: no."),
            new("currency", "krw", "1,234.50 KRW"),
        ]);

        result.Rejected.Should().BeEmpty();
        result.Fields.Select(f => (f.Name, f.Value)).Should().Equal(
            ("invoice_number", "INV-2048"), ("vendor", "Hanbit Trading"), ("issued", "2026-11-03"), ("due", "2026-11-30"),
            ("total", "1234.5"), ("paid", "false"), ("currency", "KRW"));
        var vendor = result.Fields[1];
        Invoice.Substring(vendor.Span.Start, vendor.Span.End - vendor.Span.Start).Should().Be("from Hanbit Trading");
        vendor.Match.Should().Be(MatchKind.Exact);
    }

    [Fact]
    public void AQuoteThatDiffersOnlyInCaseAndSpacing_IsFoundNormalized_AndItsSpanIsInTheOriginalText()
    {
        var result = Extractor.Verify(Invoice, Schema, [new("vendor", "Hanbit Trading", "FROM  hanbit\ntrading")]);

        var field = result.Fields.Should().ContainSingle().Subject;
        field.Match.Should().Be(MatchKind.Normalized);
        field.Quote.Should().Be("from Hanbit Trading");
    }

    [Fact]
    public void AValueTheQuoteDoesNotCarry_IsRefusedAsNotSupported()
    {
        var result = Extractor.Verify(Invoice, Schema, [
            new("total", "4321", "Total due: 1,234.50 KRW"),
            new("vendor", "Hanbit Holdings", "from Hanbit Trading"),
            new("due", "2026-12-30", "by November 30, 2026"),
        ]);

        result.Fields.Should().BeEmpty();
        result.Rejected.Where(r => r.Reason == RejectReason.NotSupported).Select(r => r.Name)
            .Should().Equal("total", "vendor", "due");
    }

    [Fact]
    public void AQuoteThatIsNotInTheText_IsRefused_AndARequiredFieldLeftWithoutAValueIsMissing()
    {
        var result = Extractor.Verify(Invoice, Schema, [new("total", "999", "Total due: 999 KRW")]);

        result.Rejected.Should().Contain(r => r.Name == "total" && r.Reason == RejectReason.NotInText);
        result.Rejected.Where(r => r.Reason == RejectReason.Missing).Select(r => r.Name)
            .Should().BeEquivalentTo(["invoice_number", "vendor", "total"]);
    }

    [Fact]
    public void AValueWithoutAQuote_IsRefused()
    {
        var result = Extractor.Verify(Invoice, Schema, [new("paid", "false", null)]);

        result.Rejected.Should().Contain(r => r.Name == "paid" && r.Reason == RejectReason.NoQuote);
    }

    [Theory]
    [InlineData("invoice_number", "INVOICE-2048")]   // pattern
    [InlineData("total", "a lot")]                    // number
    [InlineData("issued", "3 Nov 2026")]              // date must be yyyy-MM-dd
    [InlineData("paid", "maybe")]                     // boolean
    [InlineData("currency", "EUR")]                   // enum
    public void AValueNotOfTheFieldsType_IsRefusedAsFormat(string name, string value)
    {
        var result = Extractor.Verify(Invoice, Schema, [new(name, value, "Invoice INV-2048")]);

        result.Rejected.Should().Contain(r => r.Name == name && r.Reason == RejectReason.Format);
    }

    [Fact]
    public void AFieldTheSchemaDoesNotName_IsRefused()
    {
        var result = Extractor.Verify(Invoice, Schema, [new("tax_id", "123", "INV-2048")]);

        result.Rejected.Should().Contain(r => r.Name == "tax_id" && r.Reason == RejectReason.NotInSchema);
    }

    [Fact]
    public void ASingleValueField_KeepsTheFirstGroundedValue_AndAManyField_KeepsAll()
    {
        var text = "Contacts: Kim Minsu, Lee Jiwon. Vendor: Hanbit. Vendor again: Hanbit Trading.";
        var schema = new ExtractSchema([new ExtractField("vendor", FieldType.String), new ExtractField("contacts", FieldType.String) { Many = true }]);

        var result = Extractor.Verify(text, schema, [
            new("contacts", "Kim Minsu", "Kim Minsu"), new("contacts", "Lee Jiwon", "Lee Jiwon"),
            new("vendor", "Hanbit", "Vendor: Hanbit"), new("vendor", "Hanbit Trading", "Hanbit Trading"),
        ]);

        result.Fields.Select(f => f.Value).Should().Equal("Kim Minsu", "Lee Jiwon", "Hanbit");
        result.Rejected.Should().ContainSingle().Which.Should().Match<RejectedValue>(r => r.Name == "vendor" && r.Reason == RejectReason.Format);
    }

    [Fact]
    public void AQuoteThatOccursTwice_IsPlacedAfterThePreviousValue()
    {
        var text = "Kim signed. Later, Kim paid.";
        var schema = new ExtractSchema([new ExtractField("signed", FieldType.String), new ExtractField("payer", FieldType.String)]);

        var result = Extractor.Verify(text, schema, [new("signed", "signed", "Kim signed"), new("payer", "Kim", "Kim")]);

        result.Fields[1].Span.Should().Be(new TextSpan(19, 22));   // the second "Kim", not the first
    }

    [Fact]
    public void Spans_CountUnicodeScalarValues_NotUtf16Units()
    {
        var text = "😀 Total: 42";   // the emoji is two UTF-16 units, one scalar value
        var schema = new ExtractSchema([new ExtractField("total", FieldType.Number)]);

        var result = Extractor.Verify(text, schema, [new("total", "42", "Total: 42")]);

        result.Fields.Should().ContainSingle().Which.Span.Should().Be(new TextSpan(2, 11));
    }

    [Fact]
    public void FuzzyMatching_IsOffByDefault_AndWhenOnFindsAClosePhrase()
    {
        var text = "The meeting decided to move the launch to the second week of December.";
        var schema = new ExtractSchema([new ExtractField("decision", FieldType.String)]);
        Candidate[] output = [new("decision", "move the launch", "decided to move launch to second week of December")];

        Extractor.Verify(text, schema, output).Rejected.Should().ContainSingle().Which.Reason.Should().Be(RejectReason.NotInText);

        var fuzzy = Extractor.Verify(text, schema, output, new ExtractOptions { Fuzzy = true });
        fuzzy.Fields.Should().ContainSingle().Which.Match.Should().Be(MatchKind.Fuzzy);
    }

    [Fact]
    public void FuzzyMatching_DoesNotCountPunctuationAtTheEdgesOfWords_AndTheSpanLeavesItOut()
    {
        var text = "Invoice 2026-11-02. Total due: 1,200 won. Paid by card.";
        var schema = new ExtractSchema([new ExtractField("total", FieldType.Number)]);
        Candidate[] output = [new("total", "1200", "total due is 1,200 won")]; // the model dropped the colon and the full stop

        var result = Extractor.Verify(text, schema, output, new ExtractOptions { Fuzzy = true, Threshold = 0.75 });

        var field = result.Fields.Should().ContainSingle().Subject;
        field.Match.Should().Be(MatchKind.Fuzzy);
        text[field.Span.Start..field.Span.End].Should().Be("Total due: 1,200 won");
    }

    [Fact]
    public void Verification_IsDeterministic()
    {
        Candidate[] output = [new("vendor", "Hanbit Trading", "from Hanbit Trading"), new("total", "1234.5", "1,234.50")];

        Extractor.Verify(Invoice, Schema, output).Should().BeEquivalentTo(Extractor.Verify(Invoice, Schema, output));
    }

    [Fact]
    public void AnInvalidSchema_IsRefusedWithAnException()
    {
        var act1 = () => Extractor.Verify("x", new ExtractSchema([]), []);
        var act2 = () => Extractor.Verify("x", new ExtractSchema([new ExtractField("a", FieldType.String), new ExtractField("a", FieldType.Number)]), []);
        var act3 = () => Extractor.Verify("x", new ExtractSchema([new ExtractField("e", FieldType.Enum)]), []);
        var act4 = () => Extractor.Verify("x", new ExtractSchema([new ExtractField("p", FieldType.String) { Pattern = "(" }]), []);
        var act5 = () => Extractor.Verify("x", new ExtractSchema([new ExtractField("a", FieldType.String)]), [], new ExtractOptions { Threshold = 2 });

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>().WithMessage("*Two fields*");
        act3.Should().Throw<ArgumentException>().WithMessage("*lists no values*");
        act4.Should().Throw<ArgumentException>().WithMessage("*not a valid regular expression*");
        act5.Should().Throw<ArgumentException>().WithMessage("*threshold*");
    }
}
