using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The cache step of a finding's proof as the store runs it on SQL Server (osdu/docs/reference/concepts/value-proof.md): what a
/// version of a partition's cache holds of the ids and values a check looked for, exactly, once case is ignored and in any field;
/// the records a value named and the types the version holds. The script a person copies answers what the store answers, a
/// version is read as it was, and one that is gone or pruned is refused rather than answered as if it held nothing.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheProofStoreTests : IDisposable
{
    private const string Scope = "dev";
    private const string Flow = "welldb-osdu-00-reference-cache";
    private const string Units = "UnitOfMeasure";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
    private static readonly CacheCapture Capture = new(null, "tests", "seeded");

    private readonly OsduTestDatabase _catalog = new();

    public void Dispose() => _catalog.Dispose();

    [Fact]
    public async Task An_id_no_record_holds_reads_none_and_names_the_records_that_differ_only_by_case_or_hold_its_code()
    {
        var version = await CaptureAsync(0, Uom("ft", "foot"), Uom("fT", "femtotesla"), Metre(), Wellbore("A"));

        var proof = await ProveAsync(new CacheProofQuery { Kind = CacheProofKind.Ids, Scope = Scope, Version = version, Types = [Units], Looked = [Id("FT"), Id("ft") + ":", Id("it's")] });

        Assert.Equal(["id looked for", "records with this id", "records like it"], proof.Columns);
        Assert.Equal(
            [
                [Id("FT"), "0", $"{Id("fT")}, {Id("ft")}"],
                [Id("ft") + ":", "1", Id("fT")],
                [Id("it's"), "0", null],
            ],
            proof.Rows);
        Assert.False(proof.More);
    }

    [Fact]
    public async Task A_value_is_counted_at_the_fields_compared_exactly_and_ignoring_case_and_found_in_any_field()
    {
        var version = await CaptureAsync(0, Uom("ft", "foot"), Uom("fT", "femtotesla"), Metre(), Wellbore("A"));

        var byCode = await ProveAsync(new CacheProofQuery
        {
            Kind = CacheProofKind.Values, Scope = Scope, Version = version, Types = [Units], Fields = ["Code"], Looked = ["FT", "foot", "m", "meter", "it's"],
        });
        Assert.Equal(["value looked for", "at the fields", "ignoring case", "in any field"], byCode.Columns);
        Assert.Equal(
            [
                ["FT", "0", "2", $"{Id("fT")}, {Id("ft")}"],
                ["foot", "0", "0", Id("ft")],
                ["it's", "0", "0", null],
                ["m", "1", "1", Id("m")],
                ["meter", "0", "0", Id("m")],
            ],
            byCode.Rows);

        // A field holding a list is held by each of its values; compared by id, a record is found by its code too.
        var byAlias = await ProveAsync(new CacheProofQuery { Kind = CacheProofKind.Values, Scope = Scope, Version = version, Types = [Units], Fields = ["NameAlias"], Looked = ["meter"] });
        Assert.Equal([["meter", "1", "1", Id("m")]], byAlias.Rows);
        var byId = await ProveAsync(new CacheProofQuery { Kind = CacheProofKind.Values, Scope = Scope, Version = version, Types = [Units], Fields = ["id"], Looked = ["m", Id("m")] });
        Assert.Equal([["m", "1", "1", Id("m")], [Id("m"), "1", "1", null]], byId.Rows);
    }

    [Fact]
    public async Task A_version_is_read_as_it_was_and_one_gone_or_pruned_is_refused()
    {
        var first = await CaptureAsync(0, Uom("ft", "foot"));
        var second = await CaptureAsync(1, Uom("ft", "foot"), Uom("km", "kilometre"));

        async Task<string?> HeldAsync(string version)
            => (await ProveAsync(new CacheProofQuery { Kind = CacheProofKind.Ids, Scope = Scope, Version = version, Types = [Units], Looked = [Id("km")] })).Rows[0][1];

        Assert.Equal("0", await HeldAsync(first));
        Assert.Equal("1", await HeldAsync(second));

        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => HeldAsync("20200101T000000Z"));
        Assert.Contains("holds no such version, or its retention pruned its records", unknown.Message, StringComparison.Ordinal);

        await using (var connection = new SqlConnection(_catalog.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [osdu].[CacheVersion] SET [PrunedUtc] = SYSUTCDATETIME() WHERE [Scope] = @scope AND [Version] = @version;";
            command.Parameters.AddWithValue("@scope", Scope);
            command.Parameters.AddWithValue("@version", first);
            await command.ExecuteNonQueryAsync();
        }

        var pruned = await Assert.ThrowsAsync<DeliveryException>(() => HeldAsync(first));
        Assert.Contains($"Version {first} of the cache of partition '{Scope}'", pruned.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_records_named_and_the_types_held_are_listed_and_a_limit_says_there_are_more()
    {
        var version = await CaptureAsync(0, Uom("ft", "foot"), Uom("fT", "femtotesla"), Metre(), Wellbore("A"));

        var records = await ProveAsync(new CacheProofQuery { Kind = CacheProofKind.Records, Scope = Scope, Version = version, Types = [Units], Looked = [Id("fT"), Id("ft")] });
        Assert.Equal(["type", "record", "fields"], records.Columns);
        Assert.Equal([Id("fT"), Id("ft")], records.Rows.Select(r => r[1]));
        Assert.Equal("femtotesla", JsonNode.Parse(records.Rows[0][2]!)!["Name"]!.GetValue<string>());

        var types = await ProveAsync(new CacheProofQuery { Kind = CacheProofKind.Types, Scope = Scope, Version = version });
        Assert.Equal([["UnitOfMeasure", "reference-data--UnitOfMeasure", "3"], ["Wellbore", "master-data--Wellbore", "1"]], types.Rows);

        var one = await ProveAsync(new CacheProofQuery { Kind = CacheProofKind.Types, Scope = Scope, Version = version, Limit = 1 });
        Assert.Single(one.Rows);
        Assert.True(one.More);
    }

    /// <summary>What the store answers, checked against the script a person copies, run as it is.</summary>
    private async Task<SqlFlow.Delivery.Source.ProofRows> ProveAsync(CacheProofQuery query)
    {
        var answered = await _catalog.Caches().ReadProofAsync(query);
        if (!answered.More)
        {
            Assert.Equal(answered.Rows, await RunScriptAsync(CacheProofSql.Script(query, query.Limit)));
        }

        return answered;
    }

    private async Task<List<IReadOnlyList<string?>>> RunScriptAsync(string script)
    {
        await using var connection = new SqlConnection(_catalog.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<IReadOnlyList<string?>>();
        while (await reader.ReadAsync())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToList());
        }

        return rows;
    }

    private async Task<string> CaptureAsync(int day, params Item[] items)
    {
        var types = items.GroupBy(i => (i.Type, i.EntityType)).Select(g => new ReferenceType(g.Key.Type, g.Key.EntityType, g.Select(i => i.Record))).ToList();
        var write = await _catalog.Caches().MergeAsync(Scope, Flow, types, Capture, Start.AddDays(day));
        Assert.True(write.Written);
        return write.Snapshot.Version;
    }

    private static string Id(string code) => "test:reference-data--UnitOfMeasure:" + code;

    private static Item Uom(string code, string name)
        => new(Units, "reference-data--UnitOfMeasure", ReferenceItem.FromText(Id(code), new Dictionary<string, string> { ["Code"] = code, ["Name"] = name }));

    /// <summary>The metre, whose aliases are a list.</summary>
    private static Item Metre()
        => new(Units, "reference-data--UnitOfMeasure", new ReferenceItem(Id("m"), new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = ReferenceValue.Of("m"),
            ["Name"] = ReferenceValue.Of("metre"),
            ["NameAlias"] = ReferenceValue.From(new JsonArray("meter", "metres")),
        }));

    private static Item Wellbore(string key)
        => new("Wellbore", "master-data--Wellbore", ReferenceItem.FromText("test:master-data--Wellbore:" + key, new Dictionary<string, string> { ["FacilityName"] = "Wellbore 1/1-" + key }));

    private sealed record Item(string Type, string EntityType, ReferenceItem Record);
}
