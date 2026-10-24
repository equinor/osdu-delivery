using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The key a duplicate check compares an inventory's records by (osdu/docs/reference/flow/inventory-duplicates.md, Finding duplicate
/// records): what a key may name and how it is refused, how each comparison reads a value, how a list and a missing value
/// compare, and that the key keeps one spelling and one hash however it was written.
/// </summary>
public sealed class InventoryDuplicateKeyTests
{
    private static InventoryDuplicateKey Key(params (string Path, string? Compare)[] properties)
    {
        var (key, problem) = InventoryDuplicateKey.Of(properties.Select(p => ((string?)p.Path, p.Compare)).ToList());
        Assert.Null(problem);
        return key!;
    }

    private static JsonObject Record(string data) => new() { ["id"] = "dev:work-product-component--WellLog:a", ["data"] = JsonNode.Parse(data) };

    private static byte[]? Hash(InventoryDuplicateKey key, string data) => key.Read(Record(data)).Hash;

    [Fact]
    public void A_key_names_one_to_a_hundred_paths_from_the_record_s_root_each_once_and_refuses_anything_else_with_why()
    {
        string? Refused(params (string? Path, string? Compare)[] properties) => InventoryDuplicateKey.Of(properties.ToList()).Problem;

        Assert.Contains("Name at least one property", Refused(), StringComparison.Ordinal);
        Assert.Contains("Name at least one property", InventoryDuplicateKey.Of(null).Problem, StringComparison.Ordinal);
        Assert.Contains("key[0] names no property", Refused((" ", null)), StringComparison.Ordinal);
        Assert.Contains("a list is crossed without [*]", Refused(("data.Curves[*].Mnemonic", null)), StringComparison.Ordinal);
        Assert.Contains("no two records share it", Refused(("id", null)), StringComparison.Ordinal);
        Assert.Contains("no two records share it", Refused(("version", null)), StringComparison.Ordinal);
        Assert.Contains("a path starts at the record's root, one of data, meta", Refused(("FacilityName", null)), StringComparison.Ordinal);
        Assert.Contains("key[1] names data.Name again", Refused(("data.Name", null), ("data.Name", "loose")), StringComparison.Ordinal);
        Assert.Contains("compares 'fuzzy'; a property compares exact, ignoreCase or loose", Refused(("data.Name", "fuzzy")), StringComparison.Ordinal);
        Assert.Contains($"at most {InventoryDuplicates.MaxKeyProperties} properties; 101 were named",
            Refused(Enumerable.Range(0, 101).Select(i => ((string?)$"data.P{i}", (string?)null)).ToArray()), StringComparison.Ordinal);

        // A hundred properties, and every root a record carries, are a key; a comparison is read ignoring case.
        Assert.Null(Refused(Enumerable.Range(0, 100).Select(i => ((string?)$"data.P{i}", (string?)null)).ToArray()));
        Assert.Null(Refused(InventoryDuplicateKey.Roots.Select(r => ((string?)r, (string?)"IGNORECASE")).ToArray()));
    }

    [Fact]
    public void The_key_keeps_one_spelling_and_one_hash_and_is_read_back_from_it()
    {
        var written = Key((" data.WellboreID ", null), ("data.Name", "Loose"));
        Assert.Equal("""[{"path":"data.WellboreID","compare":"exact"},{"path":"data.Name","compare":"loose"}]""", written.ToJson());
        Assert.Equal(Key(("data.WellboreID", "exact"), ("data.Name", "loose")).Hash(), written.Hash());
        Assert.NotEqual(Key(("data.Name", "loose"), ("data.WellboreID", "exact")).Hash(), written.Hash());
        Assert.Equal(64, written.Hash().Length);

        var read = InventoryDuplicateKey.Parse(written.ToJson());
        Assert.Equal(written.Properties, read.Properties);
        Assert.Contains("cannot be read: key[0] 'id'", Assert.Throws<DeliveryException>(() => InventoryDuplicateKey.Parse("""[{"path":"id"}]""")).Message, StringComparison.Ordinal);
        Assert.Contains("is not the list of properties a check keeps", Assert.Throws<DeliveryException>(() => InventoryDuplicateKey.Parse("{")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_text_keeps_case_and_spaces_ignore_case_folds_case_and_unicode_form_and_loose_also_folds_spacing_and_reads_numbers()
    {
        var exact = Key(("data.Name", "exact"));
        var ignoreCase = Key(("data.Name", "ignoreCase"));
        var loose = Key(("data.Name", "loose"));

        Assert.NotEqual(Hash(exact, """{"Name":"Wellbore A-1"}"""), Hash(exact, """{"Name":"WELLBORE A-1"}"""));
        Assert.Equal(Hash(ignoreCase, """{"Name":"Wellbore A-1"}"""), Hash(ignoreCase, """{"Name":"WELLBORE A-1"}"""));
        Assert.NotEqual(Hash(ignoreCase, """{"Name":"Wellbore A-1"}"""), Hash(ignoreCase, """{"Name":" Wellbore  A-1 "}"""));
        Assert.Equal(Hash(loose, """{"Name":"Wellbore A-1"}"""), Hash(loose, """{"Name":" wellbore \t A-1 "}"""));

        // A composed and a decomposed letter are one letter once case is folded; exactly, they are two spellings.
        Assert.Equal(Hash(ignoreCase, "{\"Name\":\"Brændé\"}"), Hash(ignoreCase, "{\"Name\":\"BRÆNDÉ\"}"));
        Assert.NotEqual(Hash(exact, "{\"Name\":\"é\"}"), Hash(exact, "{\"Name\":\"é\"}"));

        // Text and a number never meet but loose; loose takes empty text as no value.
        var depth = Key(("data.TopDepth", "exact"));
        var looseDepth = Key(("data.TopDepth", "loose"));
        Assert.Equal(Hash(depth, """{"TopDepth":1520.50}"""), Hash(depth, """{"TopDepth":1.5205e3}"""));
        Assert.NotEqual(Hash(depth, """{"TopDepth":1520.5}"""), Hash(depth, """{"TopDepth":"1520.5"}"""));
        Assert.Equal(Hash(looseDepth, """{"TopDepth":1520.5}"""), Hash(looseDepth, """{"TopDepth":" 1520.50 "}"""));
        Assert.Null(Hash(loose, """{"Name":"   "}"""));
        Assert.NotNull(Hash(exact, """{"Name":""}"""));
    }

    [Fact]
    public void A_list_compares_as_its_values_in_any_order_a_missing_value_matches_only_a_missing_one_and_no_value_at_all_is_no_key()
    {
        var key = Key(("data.WellboreID", "exact"), ("data.Curves.Mnemonic", "ignoreCase"));
        var a = Hash(key, """{"WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT"}]}""");
        Assert.Equal(a, Hash(key, """{"WellboreID":"wb-1","Curves":[{"Mnemonic":"dept"},{"Mnemonic":"gr"}]}"""));
        Assert.NotEqual(a, Hash(key, """{"WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"}]}"""));
        Assert.NotEqual(a, Hash(key, """{"WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT"},{"Mnemonic":"GR"}]}"""));

        // A property one record lacks matches only a record lacking it too; one holding nothing of the key is passed over.
        var lacking = Hash(key, """{"WellboreID":"wb-1"}""");
        Assert.NotNull(lacking);
        Assert.Equal(lacking, Hash(key, """{"WellboreID":"wb-1","Curves":[]}"""));
        Assert.NotEqual(lacking, a);
        var none = key.Read(Record("""{"Name":"x"}"""));
        Assert.Null(none.Hash);
        Assert.Null(none.ValuesJson);
        Assert.Empty(none.Held);

        // A value cannot run into the next: two values of one property are not one value holding both.
        var names = Key(("data.Names", "exact"));
        Assert.NotEqual(Hash(names, """{"Names":["ab","c"]}"""), Hash(names, """{"Names":["a","bc"]}"""));
    }

    [Fact]
    public void A_read_says_what_the_record_holds_at_each_property_as_written_and_which_properties_it_holds_a_value_at()
    {
        var key = Key(("data.WellboreID", "exact"), ("data.Name", "loose"), ("data.Curves.Mnemonic", "exact"));
        var read = key.Read(Record("""{"WellboreID":"wb-1","Name":" GR  run ","Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT"}]}"""));
        Assert.Equal([0, 1, 2], read.Held);
        Assert.Equal(32, read.Hash!.Length);
        var values = InventorySelectionValues.Parse(read.ValuesJson);
        Assert.Equal([" GR  run "], values["data.Name"]);
        Assert.Equal(["GR", "DEPT"], values["data.Curves.Mnemonic"]);
        Assert.Equal([1], key.Read(Record("""{"Name":"x"}""")).Held);
    }

    [Fact]
    public void A_number_is_written_one_way_whatever_way_it_was_written()
    {
        Assert.Equal("1520.5", InventoryDuplicateKey.Number("1520.50"));
        Assert.Equal("1520.5", InventoryDuplicateKey.Number("1.5205e3"));
        Assert.Equal("100", InventoryDuplicateKey.Number("100.000"));
        Assert.Equal("0", InventoryDuplicateKey.Number("-0.0"));
        Assert.Equal("-2.25", InventoryDuplicateKey.Number("-2.250"));
        Assert.Equal("12345678901234567890", InventoryDuplicateKey.Number("12345678901234567890"));
        Assert.NotEqual(InventoryDuplicateKey.Number("12345678901234567890"), InventoryDuplicateKey.Number("12345678901234567891"));
        Assert.Equal("1E+300", InventoryDuplicateKey.Number("1e300"));
        Assert.Null(InventoryDuplicateKey.Number("GR"));
        Assert.Null(InventoryDuplicateKey.Number("NaN"));
    }
}
