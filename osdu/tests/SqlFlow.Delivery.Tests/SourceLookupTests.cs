using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The lookup tables a delivery flow joins into its reads (<c>lookups</c>, osdu/docs/reference/flow/delivery-lookups.md), as a
/// document declares them and as the SQL a read sends joins them: a LEFT OUTER JOIN on the lookup key, after the table's own
/// filter, under the key's collation, and a part of a window's candidates for each lookup table that changed.
/// </summary>
public sealed class SourceLookupTests
{
    private const string Flow = """
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [log_id]
            lookups:
              Region:
                object: OsduData.silver.Region
                join: { code: region_code }
                columns: [name, country]
          datasets:
            curves:
              object: OsduData.silver.WellLogCurve
              join: { log_id: log_id }
              lookups:
                CurveAlias:
                  object: OsduData.silver.CurveAlias
                  join: { source_mnemonic: curve_mnemonic }
                  columns: [curve_type]
          work: work
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/facade-api
          headers: { data-partition-id: dev }
          protocol: storage
        """;

    private static FlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseFlow(yaml.ReplaceLineEndings("\n"), "flow.yaml");

    private static string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml)).Message;

    private static string With(string from, string to)
    {
        var flow = Flow.ReplaceLineEndings("\n");
        var changed = flow.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(flow, changed);
        return changed;
    }

    [Fact]
    public void A_table_declares_the_lookup_tables_it_joins_each_by_its_key_the_column_it_matches_and_the_columns_it_gives()
    {
        var flow = Parse(Flow);
        var region = Assert.Single(flow.Source.Record.Lookups);
        Assert.Equal("Region", region.Key);
        Assert.Equal("OsduData.silver.Region", region.Value.Object);
        Assert.Equal([new("code", "region_code")], region.Value.Join);
        Assert.Equal(["name", "country"], region.Value.Columns);
        var alias = Assert.Single(flow.Source.Datasets["curves"].Lookups);
        Assert.Equal("CurveAlias", alias.Key);
        Assert.Equal([new("source_mnemonic", "curve_mnemonic")], alias.Value.Join);
    }

    [Fact]
    public void A_lookup_keyed_by_several_columns_matches_each_with_a_column_of_the_joining_table_in_the_order_written()
    {
        var flow = Parse(With("join: { code: region_code }", "join: { country: country_code, code: region_code }"));
        Assert.Equal(
            [new("country", "country_code"), new("code", "region_code")],
            Assert.Single(flow.Source.Record.Lookups).Value.Join);
    }

    [Fact]
    public void A_lookup_that_cannot_be_joined_as_written_is_refused_naming_what_to_write()
    {
        Assert.Contains("flow.yaml: source.record.lookups names a lookup 'Region 1'", Refused(With("      Region:\n", "      Region 1:\n")), StringComparison.Ordinal);
        Assert.Contains("'source.record.lookups.Region.object' is required", Refused(With("        object: OsduData.silver.Region\n", string.Empty)), StringComparison.Ordinal);
        Assert.Contains("must be a three-part name", Refused(With("object: OsduData.silver.Region", "object: silver.Region")), StringComparison.Ordinal);
        Assert.Contains("'source.record.lookups.Region.join' is required", Refused(With("        join: { code: region_code }\n", string.Empty)), StringComparison.Ordinal);
        Assert.Contains(
            "source.record.lookups.Region.join matches no column; match each column of OsduData.silver.Region's key with the column of the joining table it is found by",
            Refused(With("join: { code: region_code }", "join: {}")),
            StringComparison.Ordinal);
        Assert.Contains(
            "source.record.lookups.Region.join matches lookup column 'Code' twice once case is ignored",
            Refused(With("join: { code: region_code }", "join: { code: region_code, Code: country_code }")),
            StringComparison.Ordinal);
        Assert.Contains(
            "source.record.lookups.Region.join names a column '[code]'",
            Refused(With("join: { code: region_code }", "join: { '[code]': region_code }")),
            StringComparison.Ordinal);
        Assert.Contains(
            "source.record.lookups.Region.join.code names a column ''",
            Refused(With("join: { code: region_code }", "join: { code: '' }")),
            StringComparison.Ordinal);
        Assert.Contains(
            "source.record.lookups.Region.join matches 33 columns; a lookup matches at most 32",
            Refused(With("join: { code: region_code }", "join: { " + string.Join(", ", Enumerable.Range(1, 33).Select(i => $"c{i}: r{i}")) + " }")),
            StringComparison.Ordinal);
        Assert.Contains("source.record.lookups.Region.columns lists no column", Refused(With("columns: [name, country]", "columns: []")), StringComparison.Ordinal);
        Assert.Contains("source.record.lookups.Region.columns lists 'Name' twice", Refused(With("columns: [name, country]", "columns: [name, Name]")), StringComparison.Ordinal);
        Assert.Contains(
            "source.datasets.curves.lookups names lookup CurveAlias twice once case is ignored",
            Refused(With(
                "        CurveAlias:\n",
                "        curvealias:\n          object: OsduData.silver.CurveAlias\n          join: { source_mnemonic: curve_mnemonic }\n          columns: [curve_type]\n        CurveAlias:\n")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_that_joins_no_lookup_hashes_as_it_did_before_lookups_and_one_that_joins_one_hashes_apart()
    {
        var joined = Parse(Flow);
        var plain = joined with
        {
            Source = joined.Source with
            {
                Record = joined.Source.Record with { Lookups = new Dictionary<string, FlowSourceLookup>(StringComparer.Ordinal) },
                Datasets = joined.Source.Datasets.ToDictionary(
                    d => d.Key,
                    d => d.Value with { Lookups = new Dictionary<string, FlowSourceLookup>(StringComparer.Ordinal) },
                    StringComparer.Ordinal),
            },
        };

        var source = new SourceDefinition { Name = "demo", Interfaces = [plain] };
        var withLookups = new SourceDefinition { Name = "demo", Interfaces = [joined] };
        var none = new Dictionary<string, string>(StringComparer.Ordinal);
        // The sample flow's own hash, which DeliveryDocumentHashTests pins, is the proof a flow joining none hashes as before.
        Assert.NotEqual(DeliveryDocumentHash.Of(source, none), DeliveryDocumentHash.Of(withLookups, none));
    }

    private static IngestionLookup Lookup(
        string name, string on, string? collation = "SQL_Latin1_General_CP1_CI_AS", string? deleted = "DeletedDate_DW", IReadOnlyList<IngestionLookupMatch>? match = null)
        => new(
            name,
            SourceObjectName.Parse($"OsduData.silver.{name}"),
            match ?? [new IngestionLookupMatch("code", on, collation)],
            [new("name", $"{name}_name"), new("UpdatedDate_DW", $"{name}_UpdatedDate_DW")],
            "UpdatedDate_DW",
            $"{name}_UpdatedDate_DW",
            deleted);

    private static IngestionLayout Layout(string? filter = null) => new()
    {
        Record = SourceObjectName.Parse("OsduData.silver.WellLog"),
        Key = [new SourceKeyColumn("log_id", "nvarchar(50)")],
        Updated = "UpdatedDate_DW",
        Deleted = "DeletedDate_DW",
        Filter = filter,
        Lookups = [Lookup("Region", "region_code")],
        Datasets =
        [
            new IngestionDataset(
                "curves",
                SourceObjectName.Parse("OsduData.silver.WellLogCurve"),
                [new("log_id", "log_id")],
                [],
                "UpdatedDate_DW",
                "DeletedDate_DW",
                FlowSourceDataset.DefaultMaxRowsPerRecord,
                null,
                [Lookup("CurveAlias", "curve_mnemonic", collation: null, deleted: null)]),
        ],
    };

    [Fact]
    public void A_read_joins_each_lookup_table_after_the_tables_own_filter_and_gives_its_columns_under_their_prefixed_names()
    {
        var sql = IngestionSql.PageRows(Layout("AND DeletedDate_DW IS NULL"));

        // The filter is applied to the record table alone, so its columns resolve against it whatever the lookups join.
        Assert.Contains(
            "(SELECT t.*, j0.[name] AS [Region_name], j0.[UpdatedDate_DW] AS [Region_UpdatedDate_DW] FROM (SELECT * FROM [OsduData].[silver].[WellLog] WHERE 1 = 1 AND DeletedDate_DW IS NULL) t "
            + "LEFT OUTER JOIN [OsduData].[silver].[Region] j0 ON j0.[code] = t.[region_code] COLLATE SQL_Latin1_General_CP1_CI_AS AND j0.[DeletedDate_DW] IS NULL)",
            sql,
            StringComparison.Ordinal);

        // A key that holds no text is compared as it is, and a lookup table without a delete column joins every row.
        Assert.Contains(
            "(SELECT t.*, j0.[name] AS [CurveAlias_name], j0.[UpdatedDate_DW] AS [CurveAlias_UpdatedDate_DW] FROM [OsduData].[silver].[WellLogCurve] t "
            + "LEFT OUTER JOIN [OsduData].[silver].[CurveAlias] j0 ON j0.[code] = t.[curve_mnemonic])",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_window_reads_the_records_whose_lookup_rows_moved_or_were_marked_deleted_in_it_and_a_whole_read_needs_no_such_part()
    {
        var bounds = new IngestionSql.KeyBounds(After: false, From: false, To: false);
        var window = IngestionSql.CandidateSet(Layout(), SourceSelectionKind.Incremental, hasLower: true, bounds);
        Assert.Contains(
            "INNER JOIN [OsduData].[silver].[Region] l ON l.[code] = r.[region_code] COLLATE SQL_Latin1_General_CP1_CI_AS\n"
            + "WHERE ((l.[UpdatedDate_DW] > @lower AND l.[UpdatedDate_DW] <= @upper) OR (l.[DeletedDate_DW] > @lower AND l.[DeletedDate_DW] <= @upper))",
            window.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "INNER JOIN [OsduData].[silver].[WellLogCurve] c ON c.[log_id] = r.[log_id]\n"
            + "INNER JOIN [OsduData].[silver].[CurveAlias] l ON l.[code] = c.[curve_mnemonic]\n"
            + "WHERE (l.[UpdatedDate_DW] > @lower AND l.[UpdatedDate_DW] <= @upper)",
            window.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);

        var whole = IngestionSql.CandidateSet(Layout(), SourceSelectionKind.Full, hasLower: false, bounds);
        Assert.DoesNotContain("INNER JOIN [OsduData].[silver].[Region] l", whole, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lookup_keyed_by_several_columns_joins_on_all_of_them_each_text_column_under_its_own_collation()
    {
        var layout = Layout() with
        {
            Lookups =
            [
                Lookup("Region", "region_code", match:
                [
                    new IngestionLookupMatch("country", "country_code", "Latin1_General_100_CI_AS"),
                    new IngestionLookupMatch("code", "region_code", "SQL_Latin1_General_CP1_CI_AS"),
                    new IngestionLookupMatch("version", "region_version", null),
                ]),
            ],
        };

        Assert.Contains(
            "LEFT OUTER JOIN [OsduData].[silver].[Region] j0 ON j0.[country] = t.[country_code] COLLATE Latin1_General_100_CI_AS "
            + "AND j0.[code] = t.[region_code] COLLATE SQL_Latin1_General_CP1_CI_AS AND j0.[version] = t.[region_version] AND j0.[DeletedDate_DW] IS NULL)",
            IngestionSql.PageRows(layout),
            StringComparison.Ordinal);

        var bounds = new IngestionSql.KeyBounds(After: false, From: false, To: false);
        Assert.Contains(
            "INNER JOIN [OsduData].[silver].[Region] l ON l.[country] = r.[country_code] COLLATE Latin1_General_100_CI_AS "
            + "AND l.[code] = r.[region_code] COLLATE SQL_Latin1_General_CP1_CI_AS AND l.[version] = r.[region_version]\n",
            IngestionSql.CandidateSet(layout, SourceSelectionKind.Incremental, hasLower: true, bounds).ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_look_at_a_table_shows_the_columns_its_lookups_give_beside_its_own()
    {
        var layout = Layout();
        var record = IngestionSql.TableRows(layout, null, ["t.[log_id]", "t.[Region_name]"]);
        Assert.Contains("LEFT OUTER JOIN [OsduData].[silver].[Region] j0", record, StringComparison.Ordinal);
        var curves = IngestionSql.TableRows(layout, layout.Datasets[0], ["t.[CurveAlias_name]"]);
        Assert.Contains("LEFT OUTER JOIN [OsduData].[silver].[CurveAlias] j0", curves, StringComparison.Ordinal);
    }

    [Fact]
    public void A_records_fingerprint_moves_when_a_lookup_row_it_joined_moves_and_stays_for_a_flow_that_joins_none()
    {
        var datasets = new Dictionary<string, DatasetVersion>(StringComparer.Ordinal) { ["curves"] = new DatasetVersion(2, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var updated = new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);
        var plain = IngestionFingerprint.Of(updated, datasets);
        Assert.Equal(plain, IngestionFingerprint.Of(updated, datasets, new Dictionary<string, string>(StringComparer.Ordinal)));

        string Joined(params DateTime?[] moments)
            => IngestionFingerprint.Of(updated, datasets, new Dictionary<string, string>(StringComparer.Ordinal) { ["curves.CurveAlias"] = IngestionFingerprint.Moments(moments) });

        var before = Joined(updated, null);
        Assert.NotEqual(plain, before);
        Assert.Equal(before, Joined(updated, null));
        Assert.NotEqual(before, Joined(updated.AddHours(1), null));
        Assert.NotEqual(before, Joined(updated, updated));
        Assert.NotEqual(before, Joined(null, updated));
    }

    [Fact]
    public void Lineage_shows_the_flow_reading_each_lookup_table_after_the_table_that_joins_it_and_a_table_read_twice_once()
    {
        // An ingestion flow loading a lookup table writes the node these reads land on, so it is ordered before the delivery
        // (osdu/docs/reference/concepts/lineage.md, What each kind reads and writes).
        Assert.Equal(
            ["OsduData.silver.WellLog", "OsduData.silver.Region", "OsduData.silver.WellLogCurve", "OsduData.silver.CurveAlias"],
            DeliveryLineage.DeclaredObjects(Parse(Flow)).Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());
        Assert.All(DeliveryLineage.DeclaredObjects(Parse(Flow)), o => Assert.Equal(("${env:OSDU_DATA_DB}", Core.Lineage.LineageRelation.Reads), (o.ConnectionReference, o.Relation)));

        var twice = Parse(With("object: OsduData.silver.CurveAlias", "object: OsduData.silver.Region"));
        Assert.Equal(
            ["OsduData.silver.WellLog", "OsduData.silver.Region", "OsduData.silver.WellLogCurve"],
            DeliveryLineage.DeclaredObjects(twice).Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());

        var none = Parse(With("    lookups:\n      Region:\n        object: OsduData.silver.Region\n        join: { code: region_code }\n        columns: [name, country]\n", string.Empty));
        Assert.Equal(
            ["OsduData.silver.WellLog", "OsduData.silver.WellLogCurve", "OsduData.silver.CurveAlias"],
            DeliveryLineage.DeclaredObjects(none).Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());
    }
}
