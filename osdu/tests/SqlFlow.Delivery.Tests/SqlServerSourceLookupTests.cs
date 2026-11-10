using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Lookup tables joined into a delivery's reads (osdu/docs/reference/flow/delivery-lookups.md) on a real SQL Server, in the
/// chain fixture's database and ingestion schema: every row given the columns of the lookup row its values name, each key column
/// matched under its own collation, or nothing; a record whose lookup row moved or was marked deleted read by a window and read
/// as moved; and a lookup the join cannot rely on refused before a row is read. Runs on the suites' test database (<see cref="OsduTestServer"/>).
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerSourceLookupTests
{
    private static readonly DateTime Loaded = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Changed = new(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    private const string SystemColumns = "[UpdatedDate_DW] datetime NULL, [DeletedDate_DW] datetime NULL";

    [Fact]
    public async Task Each_row_is_given_the_columns_of_the_lookup_row_its_value_names_or_nothing_where_none_does()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var wells = await WellsAsync(estate);
        var regions = await RegionsAsync(estate);
        await ExecuteAsync(estate, $"""
            INSERT INTO {regions} ([code], [name], [UpdatedDate_DW], [DeletedDate_DW]) VALUES
                (N'NS', N'North Sea', @loaded, NULL), (N'GONE', N'Retired', @loaded, @loaded);
            INSERT INTO {wells} ([item_key], [region_code], [UpdatedDate_DW], [DeletedDate_DW]) VALUES
                (N'w1', N'NS', @loaded, NULL), (N'w2', N'ns', @loaded, NULL), (N'w3', N'XX', @loaded, NULL),
                (N'w4', N'GONE', @loaded, NULL), (N'w5', NULL, @loaded, NULL), (N'w6', N'NS', @loaded, @loaded);
            """);

        // The record table's filter names its own delete column, which the lookup table holds too: it is applied to the record
        // table alone, before the join, so the name is never ambiguous.
        var flow = WellFlow(estate, filter: "AND DeletedDate_DW IS NULL");
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var header = await source.OpenAsync(SourceSelection.Full(), null);
        Assert.Contains("Region_name", header.Columns[SourceDatasets.Record]);
        Assert.Contains("Region_UpdatedDate_DW", header.Columns[SourceDatasets.Record]);

        var read = await ReadAsync(source, header);
        Assert.Equal(["w1", "w2", "w3", "w4", "w5"], read.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("North Sea", read["w1"].Row.GetString("Region_name"));

        // The key is compared under its own collation, which ignores case here, as the unique index keeping it does.
        Assert.Equal("North Sea", read["w2"].Row.GetString("Region_name"));
        Assert.Null(read["w3"].Row.Get("Region_name"));
        Assert.Null(read["w4"].Row.Get("Region_name"));
        Assert.Null(read["w5"].Row.Get("Region_name"));
        Assert.NotNull(read["w1"].Row.Get("Region_UpdatedDate_DW"));
    }

    [Fact]
    public async Task A_record_whose_lookup_row_moved_or_was_marked_deleted_is_read_by_the_window_and_reads_as_moved()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var wells = await WellsAsync(estate);
        var regions = await RegionsAsync(estate);
        await ExecuteAsync(estate, $"""
            INSERT INTO {regions} ([code], [name], [UpdatedDate_DW], [DeletedDate_DW]) VALUES
                (N'NS', N'North Sea', @loaded, NULL), (N'BS', N'Barents Sea', @loaded, NULL), (N'NO', N'Norwegian Sea', @loaded, NULL);
            INSERT INTO {wells} ([item_key], [region_code], [UpdatedDate_DW], [DeletedDate_DW]) VALUES
                (N'w1', N'NS', @loaded, NULL), (N'w2', N'BS', @loaded, NULL), (N'w3', N'NO', @loaded, NULL);
            """);
        var flow = WellFlow(estate);
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var before = await ReadAsync(source, await source.OpenAsync(SourceSelection.Full(), null));

        // Nothing of the wells moves; one region is corrected and another is marked deleted by its ingestion flow.
        await ExecuteAsync(estate, $"""
            UPDATE {regions} SET [name] = N'The North Sea', [UpdatedDate_DW] = @changed WHERE [code] = N'NS';
            UPDATE {regions} SET [DeletedDate_DW] = @changed WHERE [code] = N'BS';
            """);
        var window = await source.OpenAsync(SourceSelection.Incremental(Loaded), null);
        Assert.Equal(2, window.EstimatedCandidates);
        var read = await ReadAsync(source, window);
        Assert.Equal(["w1", "w2"], read.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("The North Sea", read["w1"].Row.GetString("Region_name"));
        Assert.Null(read["w2"].Row.Get("Region_name"));

        // Each reads as a record that moved, so the plan renders it again rather than skip it; the one untouched does not.
        Assert.NotEqual(before["w1"].Version, read["w1"].Version);
        Assert.NotEqual(before["w2"].Version, read["w2"].Version);
        var whole = await ReadAsync(source, await source.OpenAsync(SourceSelection.Full(), null));
        Assert.Equal(before["w3"].Version, whole["w3"].Version);
    }

    [Fact]
    public async Task A_child_row_is_given_its_lookup_rows_columns_and_its_record_is_read_when_one_of_them_moves()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var wells = await WellsAsync(estate);
        var curves = await CreateTableAsync(estate, "Curve", $"[curve_id] bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY, [item_key] nvarchar(50) NOT NULL, [mnemonic] nvarchar(20) NULL, [ordinal] int NOT NULL, {SystemColumns}", string.Empty);
        var aliases = await CreateTableAsync(
            estate,
            "CurveAlias",
            $"[source_mnemonic] nvarchar(20) NOT NULL, [curve_type] nvarchar(50) NULL, {SystemColumns}",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([source_mnemonic]);");
        await ExecuteAsync(estate, $"""
            INSERT INTO {aliases} ([source_mnemonic], [curve_type], [UpdatedDate_DW]) VALUES (N'GR', N'Gamma Ray', @loaded), (N'DT', N'Sonic', @loaded);
            INSERT INTO {wells} ([item_key], [region_code], [UpdatedDate_DW]) VALUES (N'w1', NULL, @loaded), (N'w2', NULL, @loaded);
            INSERT INTO {curves} ([item_key], [mnemonic], [ordinal], [UpdatedDate_DW]) VALUES
                (N'w1', N'GR', 1, @loaded), (N'w1', N'RHOB', 2, @loaded), (N'w2', N'DT', 1, @loaded);
            """);
        var plain = WellFlow(estate, regions: false);
        var flow = plain with
        {
            Source = plain.Source with
            {
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal)
                {
                    ["curves"] = new FlowSourceDataset
                    {
                        Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[Curve]",
                        Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["item_key"] = "item_key" },
                        OrderBy = ["ordinal"],
                        Lookups = new Dictionary<string, FlowSourceLookup>(StringComparer.Ordinal)
                        {
                            ["CurveAlias"] = new FlowSourceLookup
                            {
                                Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[CurveAlias]",
                                Join = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["source_mnemonic"] = "mnemonic" },
                                Columns = ["curve_type"],
                            },
                        },
                    },
                },
            },
        };
        var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
        var header = await source.OpenAsync(SourceSelection.Full(), null);
        Assert.Contains("CurveAlias_curve_type", header.Columns["curves"]);
        var before = await ReadAsync(source, header);
        Assert.Equal(
            ["Gamma Ray", null],
            before["w1"].Scopes["curves"].Select(row => row.GetString("CurveAlias_curve_type")));

        // One alias is corrected: the well whose curve names it is read by the window, and the other is not.
        await ExecuteAsync(estate, $"UPDATE {aliases} SET [curve_type] = N'Sonic (compressional)', [UpdatedDate_DW] = @changed WHERE [source_mnemonic] = N'DT';");
        var window = await source.OpenAsync(SourceSelection.Incremental(Loaded), null);
        var read = await ReadAsync(source, window);
        Assert.Equal(["w2"], read.Keys);
        Assert.Equal("Sonic (compressional)", Assert.Single(read["w2"].Scopes["curves"]).GetString("CurveAlias_curve_type"));
        Assert.NotEqual(before["w2"].Version, read["w2"].Version);
    }

    [Fact]
    public async Task A_lookup_the_join_cannot_rely_on_is_refused_before_a_row_is_read_saying_what_to_change()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        await WellsAsync(estate);
        await RegionsAsync(estate);
        await CreateTableAsync(estate, "Unkeyed", $"[code] nvarchar(20) NOT NULL, [name] nvarchar(50) NULL, {SystemColumns}", string.Empty);
        await CreateTableAsync(estate, "Undated", "[code] nvarchar(20) NOT NULL, [name] nvarchar(50) NULL", "CREATE UNIQUE NONCLUSTERED INDEX [UX_Undated] ON {0} ([code]);");
        await CreateTableAsync(estate, "Numbered", $"[code] int NOT NULL, [name] nvarchar(50) NULL, {SystemColumns}", "CREATE UNIQUE NONCLUSTERED INDEX [UX_Numbered] ON {0} ([code]);");

        async Task<string> RefusedAsync(FlowSourceLookup lookup)
        {
            var flow = WellFlow(estate, lookup: lookup);
            var source = estate.Engine.Sources.Open(flow, NoValues, NullLoggerFactory.Instance);
            return (await Assert.ThrowsAsync<FlowValidationException>(() => source.OpenAsync(SourceSelection.Full(), null))).Message;
        }

        FlowSourceLookup Region(string table = "Region", string key = "code", string on = "region_code", string column = "name")
            => new()
            {
                Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[{table}]",
                Join = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = on },
                Columns = [column],
            };

        Assert.Contains(
            "no statement reads across two databases (Azure SQL Database runs none)",
            await RefusedAsync(Region() with { Object = $"[master].[{estate.SilverSchema}].[Region]" }),
            StringComparison.Ordinal);
        Assert.Contains(".join matches column 'code' of", await RefusedAsync(Region("Unkeyed")), StringComparison.Ordinal);
        Assert.Contains("which no unique index without a filter keeps to one row per value", await RefusedAsync(Region("Unkeyed")), StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX [UX_Unkeyed_code]", await RefusedAsync(Region("Unkeyed")), StringComparison.Ordinal);
        Assert.Contains("which holds no UpdatedDate_DW date and time column", await RefusedAsync(Region("Undated")), StringComparison.Ordinal);
        Assert.Contains("they have to be two text columns, two whole numbers, or of one type", await RefusedAsync(Region("Numbered")), StringComparison.Ordinal);
        Assert.Contains(".join.code names column 'nowhere', which the table", await RefusedAsync(Region(on: "nowhere")), StringComparison.Ordinal);
        Assert.Contains(".join names column 'nowhere', which the table", await RefusedAsync(Region(key: "nowhere")), StringComparison.Ordinal);
        Assert.Contains("names column 'colour', which the table", await RefusedAsync(Region(column: "colour")), StringComparison.Ordinal);

        // A column a lookup gives that the row already has: the record table holds a column named as the lookup's would be.
        await ExecuteAsync(estate, $"ALTER TABLE [{estate.SilverSchema}].[Well] ADD [Region_name] nvarchar(50) NULL;");
        Assert.Contains("gives each row column 'Region_name', which the row already has", await RefusedAsync(Region()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lookup_keyed_by_several_columns_joins_on_all_of_them_each_under_its_own_collation_and_any_unique_index_among_them_keys_it()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var wells = await WellsAsync(estate);
        await ExecuteAsync(estate, $"ALTER TABLE {wells} ADD [country_code] nvarchar(10) NULL, [region_version] int NULL;");
        var areas = await CreateTableAsync(
            estate,
            "Area",
            $"[country] nvarchar(10) COLLATE Latin1_General_100_CS_AS NOT NULL, [code] nvarchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [name] nvarchar(50) NULL, {SystemColumns}",
            "CREATE UNIQUE NONCLUSTERED INDEX [UX_Area] ON {0} ([country], [code]);");
        await ExecuteAsync(estate, $"""
            INSERT INTO {areas} ([country], [code], [name], [UpdatedDate_DW]) VALUES
                (N'NO', N'NS', N'Norwegian North Sea', @loaded), (N'UK', N'NS', N'UK North Sea', @loaded);
            INSERT INTO {wells} ([item_key], [country_code], [region_code], [region_version], [UpdatedDate_DW]) VALUES
                (N'w1', N'NO', N'NS', 1, @loaded), (N'w2', N'UK', N'ns', 1, @loaded), (N'w3', N'no', N'NS', 1, @loaded), (N'w4', N'NO', N'XX', 1, @loaded);
            """);

        FlowSourceLookup Area(string table, params (string Key, string On)[] join)
            => new()
            {
                Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[{table}]",
                Join = join.ToDictionary(j => j.Key, j => j.On, StringComparer.OrdinalIgnoreCase),
                Columns = ["name"],
            };

        IIngestionSource Open(FlowSourceLookup lookup) => estate.Engine.Sources.Open(WellFlow(estate, lookup: lookup), NoValues, NullLoggerFactory.Instance);

        async Task<string> RefusedAsync(FlowSourceLookup lookup)
            => (await Assert.ThrowsAsync<FlowValidationException>(() => Open(lookup).OpenAsync(SourceSelection.Full(), null))).Message;

        // A row is given the lookup row both of its values name. Each column is compared under its own collation: the region
        // code ignores case and the country code does not, so 'ns' finds the UK row and 'no' finds nothing.
        var source = Open(Area("Area", ("country", "country_code"), ("code", "region_code")));
        var read = await ReadAsync(source, await source.OpenAsync(SourceSelection.Full(), null));
        Assert.Equal("Norwegian North Sea", read["w1"].Row.GetString("Region_name"));
        Assert.Equal("UK North Sea", read["w2"].Row.GetString("Region_name"));
        Assert.Null(read["w3"].Row.Get("Region_name"));
        Assert.Null(read["w4"].Row.Get("Region_name"));

        // A corrected lookup row reaches the one well joined to it through both columns, read again by the window.
        await ExecuteAsync(estate, $"UPDATE {areas} SET [name] = N'UK Central North Sea', [UpdatedDate_DW] = @changed WHERE [country] = N'UK';");
        var window = await ReadAsync(source, await source.OpenAsync(SourceSelection.Incremental(Loaded), null));
        Assert.Equal(["w2"], window.Keys);
        Assert.Equal("UK Central North Sea", window["w2"].Row.GetString("Region_name"));
        Assert.NotEqual(read["w2"].Version, window["w2"].Version);

        // A unique index on some of the join's columns keys it too: rows apart on the code are apart on code and country.
        await CreateTableAsync(
            estate,
            "Coded",
            $"[country] nvarchar(10) NOT NULL, [code] nvarchar(20) NOT NULL, [name] nvarchar(50) NULL, {SystemColumns}",
            "CREATE UNIQUE NONCLUSTERED INDEX [UX_Coded] ON {0} ([code]);");
        var coded = await Open(Area("Coded", ("country", "country_code"), ("code", "region_code"))).OpenAsync(SourceSelection.Full(), null);
        Assert.Contains("Region_name", coded.Columns[SourceDatasets.Record]);

        // An index on a column the join does not match keeps nothing unique for it, and the refusal gives the index that would.
        await CreateTableAsync(
            estate,
            "Versioned",
            $"[country] nvarchar(10) NOT NULL, [code] nvarchar(20) NOT NULL, [version] int NOT NULL, [name] nvarchar(50) NULL, {SystemColumns}",
            "CREATE UNIQUE NONCLUSTERED INDEX [UX_Versioned] ON {0} ([country], [code], [version]);");
        var unkeyed = await RefusedAsync(Area("Versioned", ("country", "country_code"), ("code", "region_code")));
        Assert.Contains(".join matches columns 'country', 'code' of", unkeyed, StringComparison.Ordinal);
        Assert.Contains("which no unique index without a filter, on all of them or on some, keeps to one row per combination of values", unkeyed, StringComparison.Ordinal);
        Assert.Contains("load.keyColumns: [country, code]", unkeyed, StringComparison.Ordinal);
        Assert.Contains(
            $"CREATE UNIQUE INDEX [UX_Versioned_country_code] ON [{estate.DatabaseName}].[{estate.SilverSchema}].[Versioned] ([country], [code]);",
            unkeyed,
            StringComparison.Ordinal);

        // Matched on the version as well, the same index keys it.
        var versioned = await Open(Area("Versioned", ("country", "country_code"), ("code", "region_code"), ("version", "region_version"))).OpenAsync(SourceSelection.Full(), null);
        Assert.Contains("Region_name", versioned.Columns[SourceDatasets.Record]);

        // Each pair is compared as it is, so a whole number matched with text is refused, naming the pair.
        var mismatched = await RefusedAsync(Area("Versioned", ("country", "country_code"), ("code", "region_code"), ("version", "item_key")));
        Assert.Contains("column 'item_key' (nvarchar(50)) with column 'version' of", mismatched, StringComparison.Ordinal);
        Assert.Contains("they have to be two text columns, two whole numbers, or of one type", mismatched, StringComparison.Ordinal);
    }

    private static FlowDefinition WellFlow(SqlServerIngestionFixture estate, string? filter = null, bool regions = true, FlowSourceLookup? lookup = null)
    {
        var sample = estate.DeliveryFlow();
        var lookups = new Dictionary<string, FlowSourceLookup>(StringComparer.Ordinal);
        if (lookup is not null)
        {
            lookups["Region"] = lookup;
        }
        else if (regions)
        {
            lookups["Region"] = new FlowSourceLookup
            {
                Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[Region]",
                Join = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["code"] = "region_code" },
                Columns = ["name"],
            };
        }

        return sample with
        {
            Source = sample.Source with
            {
                Record = new FlowSourceTable
                {
                    Object = $"[{estate.DatabaseName}].[{estate.SilverSchema}].[Well]",
                    Key = ["item_key"],
                    PrimaryKey = "RecId",
                    Filter = filter,
                    Lookups = lookups,
                },
                Datasets = new Dictionary<string, FlowSourceDataset>(StringComparer.Ordinal),
            },
        };
    }

    private static Task<string> WellsAsync(SqlServerIngestionFixture estate)
        => CreateTableAsync(
            estate,
            "Well",
            $"[RecId] bigint IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_Well] PRIMARY KEY CLUSTERED, [item_key] nvarchar(50) NOT NULL, [region_code] nvarchar(20) NULL, {SystemColumns}",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([item_key]);");

    private static Task<string> RegionsAsync(SqlServerIngestionFixture estate)
        => CreateTableAsync(
            estate,
            "Region",
            $"[code] nvarchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [name] nvarchar(50) NULL, {SystemColumns}",
            "CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {0} ([code]);");

    private static async Task<Dictionary<string, SourceRecord>> ReadAsync(IIngestionSource source, SourceHeader header)
    {
        var read = new Dictionary<string, SourceRecord>(StringComparer.Ordinal);
        await foreach (var record in source.ReadAsync(header, null))
        {
            read[record.Row.GetString("item_key")!] = record;
        }

        return read;
    }

    /// <summary>Creates a table in the fixture's ingestion schema, with the statements that follow it; <c>{0}</c> names the table.</summary>
    private static async Task<string> CreateTableAsync(SqlServerIngestionFixture estate, string name, string columns, string then)
    {
        var table = $"[{estate.SilverSchema}].[{name}]";
        await ExecuteAsync(estate, $"CREATE TABLE {table} ({columns});");
        if (then.Length > 0)
        {
            await ExecuteAsync(estate, string.Format(CultureInfo.InvariantCulture, then, table));
        }

        return table;
    }

    private static async Task ExecuteAsync(SqlServerIngestionFixture estate, string sql)
    {
        await using var connection = new SqlConnection(estate.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        command.Parameters.Add(new SqlParameter("@loaded", System.Data.SqlDbType.DateTime) { Value = Loaded });
        command.Parameters.Add(new SqlParameter("@changed", System.Data.SqlDbType.DateTime) { Value = Changed });
        await command.ExecuteNonQueryAsync();
    }
}
