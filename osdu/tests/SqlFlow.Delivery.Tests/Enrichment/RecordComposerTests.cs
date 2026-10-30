using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a fragment is added to a record (osdu/docs/reference/flow/mapping-enrichment.md, How an enrichment adds to the record): a value is
/// set where the record holds none, items are added to a list after the items it holds and each only once, objects are gone
/// into property by property, and anything that would take back what the record holds is a conflict that changes nothing.
/// Adding the same fragment again changes nothing, which is what lets a whole chain render again on every change.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class RecordComposerTests
{
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    private static (JsonObject Record, List<CompositionEffect> Effects, List<string> Conflicts) Compose(string record, string fragment)
    {
        var target = Json(record);
        var effects = new List<CompositionEffect>();
        var conflicts = new List<string>();
        RecordComposer.Compose(target, Json(fragment), effects, conflicts);
        return (target, effects, conflicts);
    }

    [Fact]
    public void A_value_is_set_where_the_record_holds_none_and_the_effect_names_its_path()
    {
        var (record, effects, conflicts) = Compose("""{"data":{"Name":"a"}}""", """{"data":{"Symbol":"x"}}""");

        Assert.Empty(conflicts);
        Assert.Equal("""{"data":{"Name":"a","Symbol":"x"}}""", CanonicalJson.ToString(record));
        var effect = Assert.Single(effects);
        Assert.Equal(("data.Symbol", CompositionEffectKind.Set, "\"x\""), (effect.Path, effect.Kind, effect.Value.ToJsonString()));
    }

    [Fact]
    public void An_object_the_record_holds_is_gone_into_and_one_it_lacks_is_set_whole()
    {
        var (record, effects, conflicts) = Compose(
            """{"data":{"Nested":{"Inner":"kept"}}}""",
            """{"data":{"Nested":{"Other":"added"},"ExtensionProperties":{"WellDb":{"Grade":"A"}}}}""");

        Assert.Empty(conflicts);
        Assert.Equal("kept", EnrichmentSuite.At(record, "data.Nested.Inner")!.GetValue<string>());
        Assert.Equal("added", EnrichmentSuite.At(record, "data.Nested.Other")!.GetValue<string>());
        Assert.Equal("A", EnrichmentSuite.At(record, "data.ExtensionProperties.WellDb.Grade")!.GetValue<string>());
        Assert.Equal(["data.Nested.Other", "data.ExtensionProperties"], effects.Select(e => e.Path));
    }

    [Fact]
    public void Items_are_added_after_the_items_the_list_holds_each_one_once()
    {
        var (record, effects, conflicts) = Compose(
            """{"data":{"Aliases":["a","b"]}}""",
            """{"data":{"Aliases":["b","c","c","d"]}}""");

        Assert.Empty(conflicts);
        Assert.Equal("""["a","b","c","d"]""", record["data"]!["Aliases"]!.ToJsonString());
        var effect = Assert.Single(effects);
        Assert.Equal((CompositionEffectKind.Add, """["c","d"]"""), (effect.Kind, effect.Value.ToJsonString()));
    }

    [Fact]
    public void Objects_in_a_list_are_the_same_item_when_they_are_the_same_whatever_the_order_of_their_properties()
    {
        var (record, effects, _) = Compose(
            """{"data":{"Curves":[{"CurveID":"GR","TopDepth":1}]}}""",
            """{"data":{"Curves":[{"TopDepth":1.0,"CurveID":"GR"},{"CurveID":"DT"}]}}""");

        Assert.Equal(2, record["data"]!["Curves"]!.AsArray().Count);
        Assert.Equal("""[{"CurveID":"DT"}]""", Assert.Single(effects).Value.ToJsonString());
    }

    [Fact]
    public void A_list_the_record_lacks_is_added_whole()
    {
        var (record, effects, conflicts) = Compose("""{"data":{}}""", """{"data":{"Aliases":["x"]}}""");

        Assert.Empty(conflicts);
        Assert.Equal("""["x"]""", record["data"]!["Aliases"]!.ToJsonString());
        Assert.Equal(CompositionEffectKind.Add, Assert.Single(effects).Kind);
    }

    [Fact]
    public void The_same_value_where_the_record_holds_it_is_no_change_and_no_conflict()
    {
        var (record, effects, conflicts) = Compose("""{"data":{"Depth":1}}""", """{"data":{"Depth":1.0}}""");

        Assert.Empty(conflicts);
        Assert.Empty(effects);
        Assert.Equal("""{"data":{"Depth":1}}""", CanonicalJson.ToString(record));
    }

    [Theory]
    [InlineData("""{"data":{"Depth":1}}""", """{"data":{"Depth":2}}""", "data.Depth: the step sets 2, and the record already holds 1 there")]
    [InlineData("""{"data":{"Name":"a"}}""", """{"data":{"Name":{"x":1}}}""", "data.Name: the step writes an object there, and the record holds the single value \"a\"")]
    [InlineData("""{"data":{"Name":"a"}}""", """{"data":{"Name":["a"]}}""", "data.Name: the step adds items to a list there, and the record holds the single value \"a\"")]
    [InlineData("""{"data":{"Aliases":["a"]}}""", """{"data":{"Aliases":"a"}}""", "data.Aliases: the step sets a single value there, and the record holds a list")]
    [InlineData("""{"data":{"Nested":{"Inner":"a"}}}""", """{"data":{"Nested":["x"]}}""", "data.Nested: the step adds items to a list there, and the record holds an object")]
    public void What_would_take_back_what_the_record_holds_is_a_conflict_naming_the_path_and_changes_nothing(string record, string fragment, string reason)
    {
        var (composed, effects, conflicts) = Compose(record, fragment);

        Assert.StartsWith(reason, Assert.Single(conflicts), StringComparison.Ordinal);
        Assert.Empty(effects);
        Assert.Equal(CanonicalJson.ToString(Json(record)), CanonicalJson.ToString(composed));
    }

    [Fact]
    public void One_conflicting_path_leaves_the_rest_of_the_fragment_added_and_names_only_itself()
    {
        var (record, effects, conflicts) = Compose("""{"data":{"Depth":1}}""", """{"data":{"Depth":2,"Symbol":"x"}}""");

        Assert.Single(conflicts);
        Assert.Equal("x", record["data"]!["Symbol"]!.GetValue<string>());
        Assert.Equal("data.Symbol", Assert.Single(effects).Path);
    }

    [Fact]
    public void Adding_a_fragment_twice_gives_the_record_it_gave_once()
    {
        const string fragment = """{"data":{"Aliases":["x","y"],"Nested":{"Inner":"i"},"Symbol":"s"},"tags":{"Step":"one"}}""";
        var (once, _, _) = Compose("""{"data":{"Aliases":["a"]}}""", fragment);
        var effects = new List<CompositionEffect>();
        var conflicts = new List<string>();
        var twice = (JsonObject)once.DeepClone();

        RecordComposer.Compose(twice, Json(fragment), effects, conflicts);

        Assert.Empty(conflicts);
        Assert.Empty(effects);
        Assert.Equal(CanonicalJson.ToString(once), CanonicalJson.ToString(twice));
    }

    [Fact]
    public void A_fragment_is_read_never_changed_and_the_record_takes_copies_of_its_values()
    {
        var fragment = Json("""{"data":{"Nested":{"Inner":"i"},"Aliases":["x"]}}""");
        var before = CanonicalJson.ToString(fragment);
        var record = Json("""{"data":{}}""");
        RecordComposer.Compose(record, fragment, [], []);

        record["data"]!["Nested"]!["Inner"] = "changed";
        record["data"]!["Aliases"]!.AsArray().Add("more");

        Assert.Equal(before, CanonicalJson.ToString(fragment));
    }
}
