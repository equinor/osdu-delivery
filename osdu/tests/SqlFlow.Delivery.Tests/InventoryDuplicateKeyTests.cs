using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The key a duplicate check compares an inventory's records by (osdu/docs/reference/flow/inventory-duplicates.md, The key):
/// what a key may name and how it is refused; what a check downloads of a record at each property, and that two records'
/// values there hash alike exactly when they are equal (text as written, a number however it is written, a list in any order);
/// the columns of the check's table the properties are kept in; and how a pick tells that one copy's record names another.
/// </summary>
public sealed class InventoryDuplicateKeyTests
{
    private static InventoryDuplicateKey Key(params string[] paths)
    {
        var (key, problem) = InventoryDuplicateKey.Of(paths);
        Assert.Null(problem);
        return key!;
    }

    private static JsonObject Record(string data) => new() { ["id"] = "dev:work-product-component--WellLog:a", ["data"] = JsonNode.Parse(data) };

    private static byte[]? Hash(string path, string data) => InventoryDuplicateKey.ReadAttribute(Record(data), path)?.Hash;

    [Fact]
    public void A_key_names_one_to_a_hundred_paths_from_the_record_s_root_each_once_and_refuses_anything_else_with_why()
    {
        string? Refused(params string?[] paths) => InventoryDuplicateKey.Of(paths).Problem;

        Assert.Contains("Name at least one property", Refused(), StringComparison.Ordinal);
        Assert.Contains("Name at least one property", InventoryDuplicateKey.Of(null).Problem, StringComparison.Ordinal);
        Assert.Contains("key[0] names no property", Refused(" "), StringComparison.Ordinal);
        Assert.Contains("a list is crossed without [*]", Refused("data.Curves[*].Mnemonic"), StringComparison.Ordinal);
        Assert.Contains("no two records share it", Refused("id"), StringComparison.Ordinal);
        Assert.Contains("no two records share it", Refused("version"), StringComparison.Ordinal);
        Assert.Contains("a path starts at the record's root, one of data, meta", Refused("FacilityName"), StringComparison.Ordinal);
        Assert.Contains("key[1] names data.Name again", Refused("data.Name", "data.Name"), StringComparison.Ordinal);
        Assert.Contains($"at most {InventoryDuplicates.MaxKeyProperties} properties; 101 were named",
            Refused(Enumerable.Range(0, 101).Select(i => (string?)$"data.P{i}").ToArray()), StringComparison.Ordinal);

        // A hundred properties, and every root a record carries, are a key.
        Assert.Null(Refused(Enumerable.Range(0, 100).Select(i => (string?)$"data.P{i}").ToArray()));
        Assert.Null(Refused(InventoryDuplicateKey.Roots.Select(r => (string?)r).ToArray()));
    }

    [Fact]
    public void The_key_keeps_one_spelling_and_one_hash_and_is_read_back_from_it()
    {
        var written = Key(" data.WellboreID ", "data.Name");
        Assert.Equal("""[{"path":"data.WellboreID"},{"path":"data.Name"}]""", written.ToJson());
        Assert.Equal(Key("data.WellboreID", "data.Name").Hash(), written.Hash());
        Assert.NotEqual(Key("data.Name", "data.WellboreID").Hash(), written.Hash());
        Assert.Equal(64, written.Hash().Length);

        Assert.Equal(written.Paths, InventoryDuplicateKey.Parse(written.ToJson()).Paths);

        // A key written before keys compared equal values alone reads its paths, its comparisons let go.
        Assert.Equal(["data.Name"], InventoryDuplicateKey.Parse("""[{"path":"data.Name","compare":"loose"}]""").Paths);
        Assert.Contains("cannot be read: key[0] 'id'", Assert.Throws<DeliveryException>(() => InventoryDuplicateKey.Parse("""[{"path":"id"}]""")).Message, StringComparison.Ordinal);
        Assert.Contains("is not the list of properties a check keeps", Assert.Throws<DeliveryException>(() => InventoryDuplicateKey.Parse("{")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_equal_only_as_written_text_keeping_case_and_spaces_and_a_number_whatever_way_it_is_written()
    {
        Assert.Equal(Hash("data.Name", """{"Name":"Wellbore A-1"}"""), Hash("data.Name", """{"Name":"Wellbore A-1"}"""));
        Assert.NotEqual(Hash("data.Name", """{"Name":"Wellbore A-1"}"""), Hash("data.Name", """{"Name":"WELLBORE A-1"}"""));
        Assert.NotEqual(Hash("data.Name", """{"Name":"Wellbore A-1"}"""), Hash("data.Name", """{"Name":" Wellbore A-1"}"""));

        // A composed and a decomposed letter are two spellings.
        Assert.NotEqual(Hash("data.Name", "{\"Name\":\"é\"}"), Hash("data.Name", "{\"Name\":\"é\"}"));

        // A number is one number however it is written; text and a number are never equal; empty text is a value.
        Assert.Equal(Hash("data.TopDepth", """{"TopDepth":1520.50}"""), Hash("data.TopDepth", """{"TopDepth":1.5205e3}"""));
        Assert.NotEqual(Hash("data.TopDepth", """{"TopDepth":1520.5}"""), Hash("data.TopDepth", """{"TopDepth":"1520.5"}"""));
        Assert.NotNull(Hash("data.Name", """{"Name":""}"""));
        Assert.Null(Hash("data.Name", """{"Name":null}"""));
        Assert.Null(Hash("data.Name", """{"Other":"x"}"""));
    }

    [Fact]
    public void A_list_is_equal_as_its_values_in_any_order_and_two_values_never_run_into_one()
    {
        var a = Hash("data.Curves.Mnemonic", """{"Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT"}]}""");
        Assert.Equal(a, Hash("data.Curves.Mnemonic", """{"Curves":[{"Mnemonic":"DEPT"},{"Mnemonic":"GR"}]}"""));
        Assert.NotEqual(a, Hash("data.Curves.Mnemonic", """{"Curves":[{"Mnemonic":"GR"}]}"""));
        Assert.NotEqual(a, Hash("data.Curves.Mnemonic", """{"Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT"},{"Mnemonic":"GR"}]}"""));
        Assert.Null(Hash("data.Curves.Mnemonic", """{"Curves":[]}"""));
        Assert.NotEqual(Hash("data.Names", """{"Names":["ab","c"]}"""), Hash("data.Names", """{"Names":["a","bc"]}"""));
    }

    [Fact]
    public void A_download_keeps_each_property_s_values_as_the_record_writes_them_readable()
    {
        var read = InventoryDuplicateKey.ReadAttribute(Record("""{"Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT"}],"Name":"Brændé 1"}"""), "data.Curves.Mnemonic");
        Assert.NotNull(read);
        Assert.Equal("""["GR","DEPT"]""", read.ValuesJson);
        Assert.Equal(32, read.Hash!.Length);
        Assert.Equal("[\"Brændé 1\"]", InventoryDuplicateKey.ReadAttribute(Record("{\"Name\":\"Brændé 1\"}"), "data.Name")!.ValuesJson);
        Assert.Equal("""["1520.5","true"]""", InventoryDuplicateKey.ReadAttribute(Record("""{"Flags":[1520.5,true,null]}"""), "data.Flags")!.ValuesJson);
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

    [Fact]
    public void A_check_s_table_names_each_property_s_column_from_its_path_once_ignoring_case_and_reads_back_only_what_it_wrote()
    {
        var columns = InventoryDuplicateTable.AttributesOf(
            ["data.Name", "data.Curves.Mnemonic", "tags.Source", "data.Curves_Mnemonic", "data.version", "kind", "data.NAME", "data." + new string('x', 120)]);
        Assert.Equal(
            ["Name", "Curves_Mnemonic", "tags_Source", "Curves_Mnemonic_2", "version_2", "kind", "NAME_2", new string('x', 100)],
            columns.Select(c => c.Column));
        Assert.All(columns, c => Assert.True(InventoryDuplicateTable.HashColumn(c.Column).Length <= 128));
        Assert.Equal("Name__hash", InventoryDuplicateTable.HashColumn("Name"));
        Assert.Equal("dup_12", InventoryDuplicateTable.NameOf(12));

        Assert.Equal(columns, InventoryDuplicateTable.Parse(InventoryDuplicateTable.ToJson(columns)));
        Assert.Empty(InventoryDuplicateTable.Parse(null));
        Assert.Contains("name a column a check's table cannot have",
            Assert.Throws<DeliveryException>(() => InventoryDuplicateTable.Parse("""[{"Path":"data.Name","Column":"Name]; DROP TABLE x;--"}]""")).Message, StringComparison.Ordinal);
        Assert.Contains("name a column a check's table cannot have",
            Assert.Throws<DeliveryException>(() => InventoryDuplicateTable.Parse("""[{"Path":"data.Name","Column":"target_id"}]""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_names_another_only_where_its_record_holds_the_id_as_a_reference_does()
    {
        const string Named = "dev:work-product-component--WellLog:abc";
        static string Holding(string value) => "{\"data\":{\"Ref\":\"" + value + "\",\"Lineage\":[{\"ID\":\"x\"}]}}";
        Assert.True(InventoryDuplicateReferences.Names(Holding(Named + ":"), Named));
        Assert.True(InventoryDuplicateReferences.Names(Holding(Named + ":1700000000"), Named));
        Assert.True(InventoryDuplicateReferences.Names(Holding(Named), Named));
        Assert.False(InventoryDuplicateReferences.Names(Holding(Named + "def:"), Named));
        Assert.False(InventoryDuplicateReferences.Names(Holding("see " + Named + ":"), Named));
        Assert.False(InventoryDuplicateReferences.Names("""{"data":{}}""", Named));
    }
}
