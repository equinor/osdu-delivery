using System.Text.Json;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping's rules on the rows it renders from in the mapping builder (osdu/docs/reference/flow/mapping-dataset-assertions.md):
/// a mapping opened in the builder keeps every rule of <c>dataset.assert</c> and is written back as the same rules, in the
/// words an author writes them.
/// </summary>
public sealed class DatasetAssertionBuilderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DeliveryDocumentLoader Loader = new();

    private const string Rules = """
        - name: acl-read-whole
          description: The ACL column reads as a whole.
          column: AclUnreadable
          empty: true
        - rows: curves
          column: curve_unit
          in: [M, 'it''s', '5']
          ignoreCase: true
          values: any
          onFail: report
          where:
            - column: curve_id
              notEquals: MD
            - column: $dataset.kind
              equals: LOG
        - name: index-and-one-curve
          rows: curves
          count: { atLeast: 2, atMost: 500 }
        - rows: curves
          count: 3
        - rows: curves
          unique: [curve_id, run]
        - per: run
          unique: [log_name]
        - per: run
          count: { between: [1, 500] }
          onFail: report
        - column: depth
          atMost: 100
          tolerance: 0.5
        """;

    /// <summary>Every rule of a mapping as one line: where it is, what it judges over which rows, and how.</summary>
    private static List<string> Described(MappingDefinition mapping)
        => mapping.Dataset.Assertions
            .Select(r => string.Join(
                " | ",
                r.Location, r.Label, r.Named, r.Kind, r.Per, r.Rows, r.Column, string.Join(",", r.Columns), r.AnyRow, r.OnFail, r.Description,
                r.Condition?.Operator, r.Condition is null ? null : ConditionReader.Written(r.Condition), r.Condition?.IgnoreCase, r.Condition?.Tolerance,
                r.Count,
                string.Join(" & ", r.Where.Select(w => $"{w.Column} {w.Condition.Operator} {ConditionReader.Written(w.Condition)} {w.Condition.IgnoreCase}"))))
            .ToList();

    [Fact]
    public void A_mappings_rules_on_the_rows_opened_in_the_builder_are_written_back_as_the_same_rules()
    {
        var original = DatasetAssertionLoaderTests.Mapping(Rules);
        Assert.Equal(8, original.Dataset.Assertions.Count);

        var draft = MappingBuilder.FromDefinition(original);
        Assert.Equal(8, draft.Rules.Count);
        Assert.Empty(MappingBuilder.Incomplete(draft));
        var yaml = MappingBuilder.ToYaml(draft);
        var reread = Loader.ParseMapping(yaml, "thing.yaml");
        var again = MappingBuilder.FromDefinition(reread);

        Assert.Equal(Described(original), Described(reread));
        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again));
    }

    [Fact]
    public void A_drafts_rules_are_written_under_the_dataset_as_an_author_writes_them()
    {
        var yaml = MappingBuilder.ToYaml(MappingBuilder.FromDefinition(DatasetAssertionLoaderTests.Mapping(Rules))).ReplaceLineEndings("\n");

        Assert.Contains(
            """
              assert:
                - name: acl-read-whole
                  column: AclUnreadable
                  empty: true
                  description: The ACL column reads as a whole.
                - rows: curves
                  column: curve_unit
                  in: ['M', 'it''s', '5']
                  ignoreCase: true
                  values: any
                  onFail: report
                  where:
                    - column: curve_id
                      notEquals: 'MD'
                    - column: $dataset.kind
                      equals: 'LOG'
                - name: index-and-one-curve
                  rows: curves
                  count: { atLeast: 2, atMost: 500 }
                - rows: curves
                  count: 3
                - rows: curves
                  unique: [curve_id, run]
                - per: run
                  unique: [log_name]
                - per: run
                  count: { between: [1, 500] }
                  onFail: report
                - column: depth
                  atMost: 100
                  tolerance: 0.5
            """.ReplaceLineEndings("\n"),
            yaml,
            StringComparison.Ordinal);

        // The rules sit in the dataset block, before the parameters.
        Assert.True(yaml.IndexOf("  assert:\n", StringComparison.Ordinal) < yaml.IndexOf("\nparameters:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mapping_without_rules_writes_no_assert()
        => Assert.DoesNotContain("  assert:", MappingBuilder.ToYaml(MappingBuilder.FromDefinition(TestSchema.Mapping())), StringComparison.Ordinal);
}
