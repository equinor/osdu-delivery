using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A cache flow's table type read out of a real ingestion table on SQL Server: the rows its ingestion flow has not marked
/// deleted, each keyed by its key column and kept as the text the delivery reader gives its values, and a table the cache
/// could not hold refused whole. Runs on the suites' test database (<see cref="OsduTestServer"/>); every test
/// works in a schema of its own, dropped afterwards, and keeps its cache in memory.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerTableCaptureTests : IAsyncLifetime, IDisposable
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly string _root = Samples.NewTempDirectory();
    private readonly OsduTestDatabase _db = new();

    private string Schema => "lk_" + _suffix;

    private string Variable => "SQLFLOW_LOOKUP_DB_" + _suffix;

    private static string Database => new SqlConnectionStringBuilder(OsduTestServer.ConnectionString).InitialCatalog;

    private string Table => $"{Database}.{Schema}.CurveDictionary";

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable(Variable, OsduTestServer.ConnectionString);
        await ExecuteAsync($"""
            CREATE SCHEMA [{Schema}];
            """);
        await ExecuteAsync($"""
            CREATE TABLE [{Schema}].[CurveDictionary] (
                mnemonic nvarchar(100) NULL,
                curve_family nvarchar(200) NULL,
                curve_version int NULL,
                sampled datetime2 NULL,
                DeletedDate_DW datetime2 NULL,
                notes ntext NULL,
                tags nvarchar(max) NULL);
            """);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"DROP TABLE IF EXISTS [{Schema}].[CurveDictionary]; DROP SCHEMA IF EXISTS [{Schema}];");
        Environment.SetEnvironmentVariable(Variable, null);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(OsduTestServer.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The cache flow reading the test's table, written beside the test's files.</summary>
    private CacheDefinition Flow(string key = "mnemonic", string fields = "[curve_family, { column: curve_version, as: version }, sampled]")
    {
        var path = Path.Combine(_root, "cache", "lookups.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            flowType: cache
            name: lookups-{{_suffix}}
            source:
              connection: ${env:{{Variable}}}
              headers: { data-partition-id: dev }
            types:
              - name: CurveClasses
                table: {{Table}}
                key: {{key}}
                fields: {{fields}}
            """);
        return new DeliveryDocumentLoader().LoadCache(path);
    }

    private CacheRefresher Refresher()
        => new(Samples.Engine(ledger: null, cache: _db.Caches()), Samples.Logger<CacheRefresher>());

    private Task<CacheRefreshOutcome> RefreshAsync(CacheDefinition flow)
        => Refresher().RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "chain tests", CancellationToken.None);

    [Fact]
    public async Task A_table_type_captures_every_live_row_keyed_by_its_key_with_its_values_as_the_reader_gives_them()
    {
        await ExecuteAsync($"""
            INSERT INTO [{Schema}].[CurveDictionary] (mnemonic, curve_family, curve_version, sampled, DeletedDate_DW) VALUES
                (N'GR', N'Gamma Ray', 2, '2026-09-01T10:15:30', NULL),
                (N' RHOB ', N'Bulk Density', NULL, NULL, NULL),
                (N'fT', N'femtotesla', 1, NULL, NULL),
                (N'ft', N'foot', 1, NULL, NULL),
                (N'OLD', N'Retired', 1, NULL, '2026-01-01');
            """);
        var flow = Flow();

        var plan = await Refresher().PlanAsync(flow, new Dictionary<string, string>(), CancellationToken.None);
        var planned = Assert.Single(plan.Types);
        Assert.Equal(4, planned.Records);
        Assert.Equal("table", planned.Origin);
        Assert.Equal(["mnemonic", "curve_family", "version", "sampled"], planned.Fields);

        var outcome = await RefreshAsync(flow);
        Assert.True(outcome.Written);
        var type = Assert.Single(outcome.Types);
        Assert.Equal("table", type.Origin);
        Assert.Equal($"table {Table}", type.Source);
        Assert.Equal(4, type.Items);

        var cached = (await _db.Caches().LoadAsync("dev", outcome.Version))!.Type("CurveClasses")!;
        Assert.True(cached.IsLookup);
        Assert.Equal("mnemonic", cached.Key);
        Assert.Equal(["GR", "RHOB", "fT", "ft"], cached.Items.Select(i => i.Id));

        // A key is trimmed, and keys that differ only by case are two rows; a deleted row is left out.
        var gr = cached.Match("mnemonic", "GR")!;
        Assert.Equal("Gamma Ray", cached.Value(gr, "curve_family")!.Text);
        Assert.Equal("2", cached.Value(gr, "version")!.Text);
        Assert.Equal("2026-09-01T10:15:30Z", cached.Value(gr, "sampled")!.Text);
        Assert.Null(cached.Value(cached.Match("mnemonic", "RHOB")!, "version"));
        Assert.Equal("femtotesla", cached.Value(cached.Match("mnemonic", "fT")!, "curve_family")!.Text);
        Assert.Null(cached.Match("mnemonic", "OLD"));
        Assert.Equal($"table {Table}", (await _db.Caches().ListVersionsAsync("dev")).Single().Origin);

        // The same rows again write no version; a changed row writes one.
        Assert.False((await RefreshAsync(flow)).Written);
        await ExecuteAsync($"UPDATE [{Schema}].[CurveDictionary] SET curve_family = N'Gamma Ray (total)' WHERE mnemonic = N'GR';");
        var changed = await RefreshAsync(flow);
        Assert.True(changed.Written);
        var now = (await _db.Caches().LoadAsync("dev", changed.Version))!.Type("CurveClasses")!;
        Assert.Equal("Gamma Ray (total)", now.Value(now.Match("mnemonic", "GR")!, "curve_family")!.Text);
    }

    [Fact]
    public async Task A_table_whose_rows_the_cache_could_not_key_captures_nothing_and_names_the_rows()
    {
        await ExecuteAsync($"""
            INSERT INTO [{Schema}].[CurveDictionary] (mnemonic, curve_family) VALUES
                (N'GR', N'Gamma Ray'), (N'GR ', N'Gamma Ray again'), (NULL, N'nameless'), (N'  ', N'blank');
            """);

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => RefreshAsync(Flow()));
        Assert.Contains("has rows type CurveClasses could not be keyed by mnemonic, so nothing was captured", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the key is empty", ex.Message, StringComparison.Ordinal);
        Assert.Contains("keys held by more than one row once trimmed: 'GR'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await _db.Caches().ListVersionsAsync("dev"));
    }

    [Fact]
    public async Task A_table_type_naming_what_the_table_does_not_hold_is_refused_with_the_tables_columns()
    {
        var missing = await Assert.ThrowsAsync<DeliveryException>(() => RefreshAsync(Flow(fields: "[curve_family, colour]")));
        Assert.Contains("keeps column 'colour', which table", missing.Message, StringComparison.Ordinal);
        Assert.Contains("Columns: DeletedDate_DW, curve_family, curve_version, mnemonic, notes, sampled, tags", missing.Message, StringComparison.Ordinal);

        var key = await Assert.ThrowsAsync<DeliveryException>(() => RefreshAsync(Flow(key: "code")));
        Assert.Contains("is keyed by column 'code', which table", key.Message, StringComparison.Ordinal);

        var uncomparable = await Assert.ThrowsAsync<DeliveryException>(() => RefreshAsync(Flow(key: "notes", fields: "[curve_family]")));
        Assert.Contains("whose type ntext cannot be compared as a key", uncomparable.Message, StringComparison.Ordinal);

        await ExecuteAsync($"DROP TABLE [{Schema}].[CurveDictionary];");
        var absent = await Assert.ThrowsAsync<DeliveryException>(() => RefreshAsync(Flow()));
        Assert.Contains("was not found on the source database, or the identity this node connects with cannot see it", absent.Message, StringComparison.Ordinal);
        Assert.Empty(await _db.Caches().ListVersionsAsync("dev"));
    }

    [Fact]
    public async Task A_column_decoded_as_json_is_kept_as_its_object_through_the_store_and_a_mapping_writes_it_whole()
    {
        await ExecuteAsync($$"""
            INSERT INTO [{{Schema}}].[CurveDictionary] (mnemonic, curve_family, tags) VALUES
                (N'GR', N'Gamma Ray', N'{ "lra.dataID": "LRA-17", "Source": "welldb", "Region": "North" }'),
                (N'RHOB', N'Bulk Density', NULL);
            """);
        var flow = Flow(fields: "[curve_family, { column: tags, as: Tags, decode: json }]");

        var outcome = await RefreshAsync(flow);
        Assert.True(outcome.Written);

        // Read through a store that has never seen it, so the object comes back from the database.
        var loaded = (await _db.Caches().LoadAsync("dev", outcome.Version))!;
        var cached = loaded.Type("CurveClasses")!;
        var tags = Assert.IsType<JsonObject>(cached.Value(cached.Match("mnemonic", "GR")!, "Tags")!.Node);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "Region": "North", "Source": "welldb", "lra.dataID": "LRA-17" }"""), tags));
        Assert.Equal("Gamma Ray", cached.Value(cached.Match("mnemonic", "GR")!, "curve_family")!.Text);
        Assert.Null(cached.Value(cached.Match("mnemonic", "RHOB")!, "Tags"));

        // The scenario the setting is for: the whole object written where the template takes one, its dotted name included.
        var mapping = TestSchema.Mapping(record: """
            tags:
              $cache: CurveClasses.Tags
              $findBy: mnemonic = name
            """);
        var rendered = new MappingRenderer(mapping, TestSchema.Build(), loaded, TestSchema.Context() with { CacheVersion = outcome.Version })
            .Render(new SourceRecord
            {
                Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "GR", ["depth"] = "1" }),
                Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
            });
        Assert.False(rendered.IsHeld, string.Join("; ", rendered.Holds));
        Assert.Equal("LRA-17", rendered.Document["tags"]!["lra.dataID"]!.GetValue<string>());

        // The same object again writes no version, however the column spells it.
        await ExecuteAsync($$"""UPDATE [{{Schema}}].[CurveDictionary] SET tags = N'{"Source":"welldb","lra.dataID":"LRA-17","Region":"North"}' WHERE mnemonic = N'GR';""");
        Assert.False((await RefreshAsync(flow)).Written);

        // A value that holds no object refuses the capture, naming the type, each row's key and the column.
        await ExecuteAsync($$"""
            UPDATE [{{Schema}}].[CurveDictionary] SET tags = N'["welldb"]' WHERE mnemonic = N'RHOB';
            INSERT INTO [{{Schema}}].[CurveDictionary] (mnemonic, curve_family, tags) VALUES (N'NPHI', N'Neutron', N'{ "Source": ');
            """);
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => RefreshAsync(flow));
        Assert.Contains("has 2 values type CurveClasses decodes as JSON that hold no JSON object, so nothing was captured: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("row 'RHOB', column 'tags' (kept as Tags): the value is a JSON array, not an object", refused.Message, StringComparison.Ordinal);
        Assert.Contains("row 'NPHI', column 'tags' (kept as Tags): the value is not valid JSON (", refused.Message, StringComparison.Ordinal);
        Assert.Single(await _db.Caches().ListVersionsAsync("dev"));

        // A column that holds no text holds no JSON object's text, and is refused before a row is read, by a plan too.
        var number = await Assert.ThrowsAsync<DeliveryException>(() => Refresher().PlanAsync(
            Flow(fields: "[{ column: curve_version, decode: json }]"), new Dictionary<string, string>(), CancellationToken.None));
        Assert.Contains("type CurveClasses decodes column 'curve_version' (int) of table", number.Message, StringComparison.Ordinal);
        Assert.Contains("only a text column holds the text of a JSON object", number.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_over_the_limit_a_lookup_table_holds_captures_nothing()
    {
        await ExecuteAsync($"""
            WITH n AS (SELECT TOP ({Snapshots.LookupKeys.MaxRows + 1}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO [{Schema}].[CurveDictionary] (mnemonic, curve_family) SELECT CONCAT(N'k', i), N'family' FROM n;
            """);

        var ex = await Assert.ThrowsAsync<DeliveryException>(() => RefreshAsync(Flow()));
        Assert.Contains($"holds more than {Snapshots.LookupKeys.MaxRows} rows for type CurveClasses", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await _db.Caches().ListVersionsAsync("dev"));
    }
}
