using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a value read for an attribute or an element's field is decoded and chosen among several: percent-escapes decoded as
/// Spark's <c>try_url_decode</c> does once a <c>+</c> is escaped, and the greatest by code point, as a binary collation orders.
/// </summary>
public class DimensionKeepingTests
{
    [Theory]
    [InlineData("g%2Fcm3", "g/cm3")]
    [InlineData("v%2Fv", "v/v")]
    [InlineData("EQ-CPI%20Qual%20Flag", "EQ-CPI Qual Flag")]
    [InlineData("a+b", "a+b")]
    [InlineData("a%2Bb", "a+b")]
    [InlineData("deg%C2%B0C", "deg°C")]
    [InlineData("%e2%82%ac", "€")]
    [InlineData("no escape", "no escape")]
    [InlineData("", "")]
    [InlineData("%", "%")]
    [InlineData("50%", "50%")]
    [InlineData("100%2", "100%2")]
    [InlineData("%zz%2F", "%zz%2F")]
    [InlineData("ok%2F%", "ok%2F%")]
    [InlineData("%FF", "�")]
    [InlineData("æ%2Fø", "æ/ø")]
    public void A_value_is_decoded_from_percent_escapes_and_one_that_starts_no_escape_is_kept_as_it_is(string held, string decoded)
        => Assert.Equal(decoded, DimensionKeeping.UrlDecoded(held));

    [Fact]
    public void Decoding_none_keeps_the_value_as_the_record_holds_it()
        => Assert.Equal("g%2Fcm3", DimensionKeeping.Decoded("g%2Fcm3", DimensionValueDecode.None));

    [Fact]
    public void Text_is_ordered_by_code_point_so_a_character_past_U_FFFF_follows_one_below_it()
    {
        // UTF-16 ordinal order puts the surrogates of U+1F600 (0xD83D) before U+FF21; code point order, as UTF-8 bytes, after.
        Assert.True(string.CompareOrdinal("😀", "Ａ") < 0);
        Assert.True(DimensionKeeping.CompareCodePoints("😀", "Ａ") > 0);
        Assert.True(DimensionKeeping.CompareCodePoints("b", "B") > 0);
        Assert.True(DimensionKeeping.CompareCodePoints("Alpha", "Alpha2") < 0);
        Assert.Equal(0, DimensionKeeping.CompareCodePoints("same", "same"));
    }

    [Fact]
    public void Several_values_come_to_the_first_every_one_joined_or_the_greatest_and_say_where_it_was_read()
    {
        string[] texts = ["  ", "Bravo", "alpha", "Bravo", "Charlie"];

        Assert.Equal(("Bravo", 1), Picked(DimensionValueMany.First));
        Assert.Equal(("Bravo; alpha; Charlie", 1), Picked(DimensionValueMany.Join));
        Assert.Equal(("alpha", 2), Picked(DimensionValueMany.Max));
        Assert.Equal((null, -1, false, false), DimensionKeeping.KeepMany(["", " "], DimensionValueKeep.Value, DimensionValueMany.Max, DimensionValueDecode.None, 10));

        (string?, int) Picked(DimensionValueMany many)
        {
            var (value, from, _, _) = DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, many, DimensionValueDecode.None, 256);
            return (value, from);
        }
    }

    [Fact]
    public void A_build_keeps_how_an_attribute_and_a_field_choose_and_decode_and_writes_nothing_for_the_defaults()
    {
        IReadOnlyList<DimensionAttributeSpec> attributes =
        [
            new("Field", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Max),
            new("Code", ["data.Code"], Decode: DimensionValueDecode.Url),
            new("Plain", ["data.Name"]),
            new("Source", [], "data.Source"),
        ];
        var text = DimensionRunner.AttributesText(attributes)!;
        Assert.Equal(attributes, DimensionRunner.AttributesOf(text), new AttributeComparer());

        // A declaration that names neither is written as it was before either existed, so what older builds kept reads alike.
        Assert.Equal("""[{"name":"Plain","steps":["data.Name"]}]""", DimensionRunner.AttributesText([new("Plain", ["data.Name"])]));

        var elements = new DimensionElementsSpec("data.Curves", [new("Unit", "CurveUnit", Many: DimensionValueMany.Max, Decode: DimensionValueDecode.Url), new("M", "Mnemonic")]);
        Assert.Equal(elements.Fields, DimensionRunner.ElementsOf(DimensionRunner.ElementsText(elements))!.Fields);
        Assert.Equal("""{"path":"data.Curves","fields":[{"name":"M","path":"Mnemonic"}]}""", DimensionRunner.ElementsText(new DimensionElementsSpec("data.Curves", [new("M", "Mnemonic")])));
    }

    [Fact]
    public void The_greatest_is_taken_of_the_values_decoded_and_whole_and_then_cut_or_left_out_for_its_length()
    {
        // Decoded first: %7A is z, the greatest once decoded, though the text as held starts with %.
        Assert.Equal("z", DimensionKeeping.KeepMany(["y", "%7A"], DimensionValueKeep.Value, DimensionValueMany.Max, DimensionValueDecode.Url, 256).Value);

        // The longer of two values that agree up to the cut is the greatest, then cut; a key is left out rather than cut.
        Assert.Equal(("abc", true, false), Cut(DimensionValueKeep.Value));
        Assert.Equal(((string?)null, false, true), Cut(DimensionValueKeep.Key));

        (string?, bool, bool) Cut(DimensionValueKeep keep)
        {
            var (value, _, cut, tooLong) = DimensionKeeping.KeepMany(["abc", "abcd"], keep, DimensionValueMany.Max, DimensionValueDecode.None, 3);
            return (value, cut, tooLong);
        }
    }

    /// <summary>Attributes compared by what they declare, their steps by value rather than by list.</summary>
    private sealed class AttributeComparer : IEqualityComparer<DimensionAttributeSpec>
    {
        public bool Equals(DimensionAttributeSpec? x, DimensionAttributeSpec? y)
            => x is not null && y is not null && x.Steps.SequenceEqual(y.Steps)
                && (x.Name, x.Collect, x.Keep, x.Many, x.Decode) == (y.Name, y.Collect, y.Keep, y.Many, y.Decode);

        public int GetHashCode(DimensionAttributeSpec obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }
}
