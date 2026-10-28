using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A fragment as its loader reads it (osdu/docs/reference/flow/enrich.md, Fragments): <c>fragment: true</c>, no dataset and no
/// envelope, written under <c>data</c> and <c>tags</c> only, reading its step's rows and the record with <c>$base</c>, and
/// refused where it would key, mint or envelope a record. A mapping of a whole record never reads <c>$base</c>.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class FragmentMappingTests
{
    private static FlowValidationException Refused(string record, string parameters = "")
        => Assert.Throws<FlowValidationException>(() => EnrichmentSuite.Fragment(record, parameters: parameters));

    [Fact]
    public void A_fragment_keys_nothing_carries_no_envelope_and_needs_no_partition_parameter()
    {
        var fragment = EnrichmentSuite.Fragment("""
            data:
              Symbol: { $from: grade }
            """);

        Assert.True(fragment.Fragment);
        Assert.Same(MappingDataset.None, fragment.Dataset);
        Assert.Empty(fragment.Envelope.Owners);
        Assert.Empty(fragment.Envelope.LegalTags);
        Assert.DoesNotContain(Snapshots.RenderContext.DataPartitionParameter, fragment.Parameters.Keys);
        Assert.Equal(EnrichmentSuite.FragmentReference, fragment.Reference);
    }

    [Fact]
    public void A_mapping_of_a_whole_record_is_not_a_fragment()
        => Assert.False(EnrichmentSuite.Owner().Fragment);

    [Fact]
    public void A_fragment_declaring_a_dataset_is_refused_naming_where_its_rows_come_from()
    {
        var document = EnrichmentSuite.FragmentDocument("""
            data:
              Symbol: { $from: grade }
            """).Replace("fragment: true\n", "fragment: true\ndataset:\n  system: test\n  key: [name]\n", StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "extra.yaml"));

        Assert.Contains("declares no dataset; its rows are the step's, declared under enrich.<step>.dataset", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acl:\n  owners: [o@x]\n  viewers: [v@x]")]
    [InlineData("legal:\n  legaltags: [t]\n  otherRelevantDataCountries: [US]")]
    [InlineData("kind: { $value: x }")]
    public void A_fragment_writing_outside_data_and_tags_is_refused(string record)
    {
        var refused = Refused(record);

        Assert.Contains("a fragment writes under record.data and record.tags only", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_writes_tags()
    {
        var fragment = EnrichmentSuite.Fragment("""
            tags:
              Grade: { $from: grade }
            """);

        Assert.Equal("osdu.tags.Grade", Assert.Single(fragment.Entries).Target.Text);
    }

    [Theory]
    [InlineData("$parent: data.Name", "$parent finds a record by the id the flow's own mapping makes from its key")]
    [InlineData("$child: test:wks:work-product-component--Other:1.0.0", "$child finds a record by the id the flow's own mapping makes from its key")]
    public void A_fragment_cannot_find_a_parent_or_a_child_by_an_id_it_does_not_have(string node, string reason)
    {
        var refused = Refused($"data:\n  Symbol: {{ {node} }}");

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_reads_the_record_with_base_by_its_path_from_the_root()
    {
        var fragment = EnrichmentSuite.Fragment("""
            data:
              Symbol: { $base: data.Name, $modifiers: [upper] }
              Description: { $coalesce: [{ $from: note }, { $base: data.Name }] }
            """);

        Assert.Equal(["data.Name"], fragment.BasePaths());
        var node = fragment.Entries[0].Source!;
        Assert.Equal((MappingSourceKind.Base, "data.Name", "base.data.Name"), (node.Kind, node.BasePath, node.ToString()));
    }

    [Theory]
    [InlineData("$base: 'data Name'", "names the path of a section of the record from its root")]
    [InlineData("$base: data.Name, $findBy: Code = x", "this node reads the record the fragment adds to")]
    public void A_base_node_that_is_no_path_of_the_record_is_refused(string node, string reason)
    {
        var refused = Refused($"data:\n  Symbol: {{ {node} }}");

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_node_stepping_into_an_array_is_refused_naming_the_array_to_read_whole()
    {
        var refused = Refused("data:\n  Symbol:\n    $base: data.Curves[].CurveID");

        Assert.Contains("steps into an array, and a section of the record is read whole: name the array itself", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_node_under_a_forEach_is_refused_since_the_record_is_one_for_every_row()
    {
        var refused = Refused("""
            data:
              Curves:
                $forEach: extra
                $item:
                  CurveID: { $base: data.Name }
            """);

        Assert.Contains("$base reads the record the fragment adds to, which is one for every row of extra", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_of_a_whole_record_reading_base_is_refused()
    {
        var document = TestSchema.MappingDocument("Symbol: { $base: data.Name }");

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml"));

        Assert.Contains("only a fragment (fragment: true), which a step of enrich: renders, reads one", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_of_a_DSPDM_row_is_refused()
    {
        var document = EnrichmentSuite.FragmentDocument("data:\n  UWI: { $from: uwi }")
            .Replace($"kind: {TestSchema.Kind}", "kind: test:dspdm:WELL:1.0.0", StringComparison.Ordinal);

        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(document, "extra.yaml"));

        Assert.Contains("which no step of enrich: extends", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mapping_builder_says_it_lays_out_whole_records_rather_than_misreading_a_fragment()
    {
        var fragment = EnrichmentSuite.Fragment("data:\n  Symbol: { $base: data.Name }");

        var refused = Assert.Throws<FlowValidationException>(() => MappingBuilder.FromDefinition(fragment));

        Assert.Contains("is a fragment (fragment: true)", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragments_shape_is_the_paths_it_adds_with_no_id_or_kind()
    {
        var fragment = EnrichmentSuite.Fragment("""
            data:
              Symbol: { $from: grade }
              Description: { $base: data.Name }
            """);

        var shape = MappingRenderer.Shape(fragment, TestSchema.Build(), new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Null(shape.Document["id"]);
        Assert.Null(shape.Document["kind"]);
        Assert.Contains("base.data.Name", shape.Document["data"]!["Description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(shape.Notes, note => note.Contains("is a fragment", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fragment_renders_whole_records_never()
    {
        var renderer = EnrichmentSuite.Renderer(EnrichmentSuite.Fragment("data:\n  Symbol: { $from: grade }"));

        Assert.Throws<InvalidOperationException>(() => renderer.Render(EnrichmentSuite.Record("a", 1)));
    }

    [Fact]
    public void A_whole_record_renders_no_fragment()
    {
        var renderer = EnrichmentSuite.Renderer(EnrichmentSuite.Owner());

        Assert.Throws<InvalidOperationException>(() => renderer.RenderFragment(EnrichmentSuite.Record("a", 1), new System.Text.Json.Nodes.JsonObject()));
    }
}
