using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a value read for an attribute or an element's field is decoded, kept and chosen among several, at every edge:
/// percent-escapes as Spark's <c>try_url_decode</c> reads them once a <c>+</c> is escaped (multi-byte, malformed, at the end,
/// escaped twice), the order of code points (surrogate pairs against the characters just below them), the first, every
/// one and the greatest of several values with duplicates and white space, references kept as a key, an id or a
/// reference, and the cut at a length.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public class DimensionKeepingEdgeCaseTests
{
    [Theory]
    [InlineData("%41%42%43", "ABC")]
    [InlineData("%e2%82%ac%20%E2%82%AC", "€ €")]
    [InlineData("%F0%9F%98%80", "😀")]
    [InlineData("%C3%A6%C3%B8%C3%A5", "æøå")]
    [InlineData("%C3", "�")]
    [InlineData("%C3%28", "�(")]
    [InlineData("%C3x%A6", "�x�")]
    [InlineData("%00", "\0")]
    [InlineData("%3A", ":")]
    [InlineData("%25", "%")]
    [InlineData("%2525", "%25")]
    [InlineData("+", "+")]
    [InlineData("+%2B+", "+++")]
    [InlineData("%20+%20", " + ")]
    [InlineData("a%2", "a%2")]
    [InlineData("%%41", "%%41")]
    [InlineData("%41%", "%41%")]
    [InlineData("%4G", "%4G")]
    [InlineData("%-1", "%-1")]
    [InlineData("% 41", "% 41")]
    [InlineData("æ%2F", "æ/")]
    [InlineData("😀%2F", "😀/")]
    [InlineData("", "")]
    [InlineData("no escape at all", "no escape at all")]
    public void A_value_decodes_as_Spark_reads_it_once_a_plus_is_escaped_and_falls_back_to_itself(string held, string decoded)
        => Assert.Equal(decoded, DimensionKeeping.UrlDecoded(held));

    [Fact]
    public void A_long_value_of_escapes_decodes_whole()
    {
        var held = string.Concat(Enumerable.Repeat("%E2%82%AC", 2000));
        Assert.Equal(new string('€', 2000), DimensionKeeping.UrlDecoded(held));
    }

    [Theory]
    [InlineData("a", "b", -1)]
    [InlineData("b", "a", 1)]
    [InlineData("", "a", -1)]
    [InlineData("", "", 0)]
    [InlineData("ab", "a", 1)]
    [InlineData("A", "a", -1)]
    [InlineData("￿", "𐀀", -1)]
    [InlineData("", "😀", -1)]
    [InlineData("😀", "😁", -1)]
    [InlineData("😀", "😀", 0)]
    [InlineData("x😀", "x￿", 1)]
    [InlineData("é", "é", 1)]
    public void Text_is_ordered_by_code_point(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(DimensionKeeping.CompareCodePoints(a, b)));
        Assert.Equal(-sign, Math.Sign(DimensionKeeping.CompareCodePoints(b, a)));
    }

    public static TheoryData<string[], string?, string?, string?, string?> Several => new()
    {
        // texts, first, join, all, max
        { ["b", "a", "b", "c"], "b", "b; a; c", "b; a; b; c", "c" },
        { ["  ", "", "x"], "x", "x", "x", "x" },
        { ["", " "], null, null, null, null },
        { [], null, null, null, null },
        { ["Z", "a", "å"], "Z", "Z; a; å", "Z; a; å", "å" },
        { [" a ", "a"], "a", "a", "a; a", "a" },
    };

    [Theory]
    [MemberData(nameof(Several))]
    public void Several_values_come_to_the_first_every_one_once_every_one_or_the_greatest(string[] texts, string? first, string? join, string? all, string? max)
    {
        string? Many(DimensionValueMany many) => DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, many, DimensionValueDecode.None, 256).Value;
        Assert.Equal(first, Many(DimensionValueMany.First));
        Assert.Equal(join, Many(DimensionValueMany.Join));
        Assert.Equal(all, Many(DimensionValueMany.All));
        Assert.Equal(max, Many(DimensionValueMany.Max));
    }

    [Fact]
    public void The_place_a_value_was_read_from_is_the_first_it_was_read_at()
    {
        string[] texts = ["", "b", "c", "b", "c"];
        Assert.Equal(1, DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.First, DimensionValueDecode.None, 256).From);
        Assert.Equal(1, DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.Join, DimensionValueDecode.None, 256).From);
        Assert.Equal(1, DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.All, DimensionValueDecode.None, 256).From);
        Assert.Equal(2, DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.Max, DimensionValueDecode.None, 256).From);
        Assert.Equal(-1, DimensionKeeping.KeepMany(["", "  "], DimensionValueKeep.Value, DimensionValueMany.Max, DimensionValueDecode.None, 256).From);
    }

    [Fact]
    public void White_space_is_a_value_only_when_a_value_keeps_its_white_space()
    {
        string[] texts = ["   ", " b "];
        Assert.Equal("b", DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.First, DimensionValueDecode.None, 256, trim: true).Value);
        Assert.Equal("   ", DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.First, DimensionValueDecode.None, 256, trim: false).Value);
        Assert.Equal("   " + "; " + " b ", DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.Join, DimensionValueDecode.None, 256, trim: false).Value);
        Assert.Equal(" b ", DimensionKeeping.KeepMany(texts, DimensionValueKeep.Value, DimensionValueMany.Max, DimensionValueDecode.None, 256, trim: false).Value);

        // A key, an id and a reference are the text a search compares: always trimmed, white space alone no value.
        foreach (var keep in new[] { DimensionValueKeep.Key, DimensionValueKeep.Id, DimensionValueKeep.Reference })
        {
            Assert.Equal("b", DimensionKeeping.KeepMany(texts, keep, DimensionValueMany.First, DimensionValueDecode.None, 256, trim: false).Value);
        }

        Assert.True(DimensionKeeping.IsValue("  ", DimensionValueKeep.Value, trim: false));
        Assert.False(DimensionKeeping.IsValue("", DimensionValueKeep.Value, trim: false));
        Assert.False(DimensionKeeping.IsValue(null, DimensionValueKeep.Value, trim: false));
        Assert.False(DimensionKeeping.IsValue("  ", DimensionValueKeep.Key, trim: false));
    }

    [Theory]
    [InlineData("dev:master-data--Well:W1:", "W1", "dev:master-data--Well:W1:", "dev:master-data--Well:W1", "dev:master-data--Well:W1")]
    [InlineData("dev:master-data--Well:W1:1700000000", "W1", "dev:master-data--Well:W1:1700000000", "dev:master-data--Well:W1", "dev:master-data--Well:W1:1700000000")]
    [InlineData("dev:master-data--Well:W1", "W1", "dev:master-data--Well:W1", "dev:master-data--Well:W1", "dev:master-data--Well:W1")]
    [InlineData("  dev:reference-data--UnitOfMeasure:g%2Fcm3:  ", "g/cm3", "dev:reference-data--UnitOfMeasure:g%2Fcm3:", "dev:reference-data--UnitOfMeasure:g%2Fcm3", "dev:reference-data--UnitOfMeasure:g%2Fcm3")]
    // An id ending in a colon (a stray record of the partition): its code is empty, so its value is the text whole.
    [InlineData("dev:reference-data--UnitOfMeasure:degC::", "dev:reference-data--UnitOfMeasure:degC::", "dev:reference-data--UnitOfMeasure:degC::", "dev:reference-data--UnitOfMeasure:degC:", "dev:reference-data--UnitOfMeasure:degC:")]
    [InlineData("plain text:", "plain text:", "plain text:", "plain text:", "plain text:")]
    [InlineData("DEPT:", "DEPT:", "DEPT:", "DEPT:", "DEPT:")]
    public void A_reference_is_kept_as_its_code_exactly_as_its_id_or_as_a_reference_to_what_it_names(
        string held, string value, string key, string id, string reference)
    {
        Assert.Equal(value, DimensionKeeping.Keep(held, DimensionValueKeep.Value, 256).Value);
        Assert.Equal(key, DimensionKeeping.Keep(held, DimensionValueKeep.Key, 256).Value);
        Assert.Equal(id, DimensionKeeping.Keep(held, DimensionValueKeep.Id, 256).Value);
        Assert.Equal(reference, DimensionKeeping.Keep(held, DimensionValueKeep.Reference, 256).Value);
    }

    [Fact]
    public void A_value_untrimmed_keeps_its_white_space_and_a_reference_still_shows_its_code()
    {
        Assert.Equal("  A/1  ", DimensionKeeping.Keep("  A/1  ", DimensionValueKeep.Value, 256, trim: false).Value);
        Assert.Equal("A/1", DimensionKeeping.Keep("  A/1  ", DimensionValueKeep.Value, 256).Value);
        Assert.Equal("us/ft", DimensionKeeping.Keep(" dev:reference-data--UnitOfMeasure:us%2Fft: ", DimensionValueKeep.Value, 256, trim: false).Value);
        Assert.Equal("   ", DimensionKeeping.Keep("   ", DimensionValueKeep.Value, 256, trim: false).Value);
        Assert.Null(DimensionKeeping.Keep("   ", DimensionValueKeep.Value, 256).Value);
    }

    [Fact]
    public void A_value_is_cut_at_its_length_exactly_and_a_key_left_out_past_it()
    {
        var exactly = new string('x', 10);
        Assert.Equal((exactly, false, false), Kept(exactly, DimensionValueKeep.Value));
        Assert.Equal((exactly, true, false), Kept(exactly + "y", DimensionValueKeep.Value));
        Assert.Equal((exactly, false, false), Kept(exactly, DimensionValueKeep.Key));
        Assert.Equal(((string?)null, false, true), Kept(exactly + "y", DimensionValueKeep.Key));
        Assert.Equal(((string?)null, false, true), Kept(exactly + "y", DimensionValueKeep.Id));
        Assert.Equal(((string?)null, false, true), Kept(exactly + "y", DimensionValueKeep.Reference));

        // Joined values are cut as one value; the greatest is chosen whole and cut after.
        var joined = DimensionKeeping.KeepMany(["aaaa", "bbbb", "cccc"], DimensionValueKeep.Value, DimensionValueMany.Join, DimensionValueDecode.None, 10);
        Assert.Equal(("aaaa; bbbb", true), (joined.Value, joined.Cut));
        var greatest = DimensionKeeping.KeepMany(["zzzzzzzzzzA", "zzzzzzzzzzB"], DimensionValueKeep.Value, DimensionValueMany.Max, DimensionValueDecode.None, 10);
        Assert.Equal(("zzzzzzzzzz", 1, true), (greatest.Value, greatest.From, greatest.Cut));

        static (string?, bool, bool) Kept(string text, DimensionValueKeep keep) => DimensionKeeping.Keep(text, keep, 10);
    }

    [Fact]
    public void Values_are_decoded_before_they_are_kept_and_chosen_among()
    {
        // Decoded, %2F and / are one value; decoded, %7A is z, the greatest.
        Assert.Equal("a/b", DimensionKeeping.KeepMany(["a%2Fb", "a/b"], DimensionValueKeep.Value, DimensionValueMany.Join, DimensionValueDecode.Url, 256).Value);
        Assert.Equal("a%2Fb; a/b", DimensionKeeping.KeepMany(["a%2Fb", "a/b"], DimensionValueKeep.Value, DimensionValueMany.Join, DimensionValueDecode.None, 256).Value);
        Assert.Equal("z", DimensionKeeping.KeepMany(["y", "%7A"], DimensionValueKeep.Value, DimensionValueMany.Max, DimensionValueDecode.Url, 256).Value);

        // A value decoded to white space alone is no value when trimmed.
        Assert.Null(DimensionKeeping.KeepMany(["%20%20"], DimensionValueKeep.Value, DimensionValueMany.First, DimensionValueDecode.Url, 256).Value);
        Assert.Equal("  ", DimensionKeeping.KeepMany(["%20%20"], DimensionValueKeep.Value, DimensionValueMany.First, DimensionValueDecode.Url, 256, trim: false).Value);
    }

    [Fact]
    public void Keep_many_and_decode_are_named_as_a_document_writes_them_ignoring_case()
    {
        foreach (var keep in Enum.GetValues<DimensionValueKeep>())
        {
            Assert.Equal(keep, DimensionKeeping.Parse(DimensionKeeping.Named(keep).ToUpperInvariant()));
        }

        foreach (var many in Enum.GetValues<DimensionValueMany>())
        {
            Assert.Equal(many, DimensionKeeping.ParseMany(" " + DimensionKeeping.Named(many) + " "));
        }

        foreach (var decode in Enum.GetValues<DimensionValueDecode>())
        {
            Assert.Equal(decode, DimensionKeeping.ParseDecode(DimensionKeeping.Named(decode)));
        }

        Assert.Null(DimensionKeeping.Parse("ref"));
        Assert.Null(DimensionKeeping.ParseMany("every"));
        Assert.Null(DimensionKeeping.ParseDecode("base64"));
        Assert.Null(DimensionKeeping.ParseMany(null));
    }
}
