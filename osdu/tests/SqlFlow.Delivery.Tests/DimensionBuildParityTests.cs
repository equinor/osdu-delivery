using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a dimension build keeps, whole, compared with what the suites keep for it (Fixtures/dimension-builds): every row of
/// every table a build writes of its dimensions (the dimension, its builds, values, keys, attributes and their names,
/// elements, collected texts, the records its keys were read through and the keys its records hold, the change log) and its
/// own table, each surrogate number written as what it stands for (a key by its text, a value by its own, an attribute by
/// its name, a build by its place among the dimension's builds). Each scenario reads one part of what a dimension does
/// (labels and attributes, cleaning into values, collected attributes, elements, an incremental load), so a change to how a
/// build reads or writes that leaves what it keeps the same passes, and one that moves any of it names the scenario and the
/// file holding what it keeps now. The flows build one dimension and ask one thing at a time, so even the order in which
/// keys arrive is the same build after build.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionBuildParityTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Wellbore = Samples.WellboreKind;

    private const string Head = """
        flowType: dimension
        name: parity-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 1 }

        """;

    /// <summary>The tables a build writes of a dimension, in the order a dump lists them.</summary>
    private static readonly string[] Tables =
    [
        "Dimension", "DimensionRun", "DimensionAttributeName", "DimensionMember", "DimensionValue", "DimensionAttribute", "DimensionElement",
        "DimensionCollectedText", "DimensionKeyRecord", "DimensionRecord", "DimensionChange",
    ];

    /// <summary>The columns a dump leaves out: where a row is, and the numbers that order a table's rows and name nothing.</summary>
    private static readonly HashSet<string> Unwritten = new(StringComparer.Ordinal)
    {
        "PartitionId", "DimensionId", "AttributeValueId", "ElementValueId", "KeyRecordId", "DimensionRecordId", "ChangeId", "TextId",
    };

    /// <summary>The columns holding the number of a build, written as the build's place among the dimension's builds.</summary>
    private static readonly HashSet<string> BuildColumns = new(StringComparer.Ordinal)
    {
        "DimensionRunId", "FirstSeenRunId", "RemovedRunId", "MemberSinceRunId", "LastRunId", "LastFullRunId",
    };

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    /// <summary>Where the suites keep what each scenario's builds keep.</summary>
    private static string KeptFolder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "dimension-builds");

    /// <summary>Where a scenario whose builds keep something else writes what they keep now.</summary>
    private static string NowFolder => Path.Combine(AppContext.BaseDirectory, "dimension-builds-now");

    private (DimensionRunner Runner, OsduLedger Ledger, DimensionFlowDefinition Flow) Runner(string yaml, string source = "flows/dims.yaml")
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, source).ForPartition("dev");
        return (new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true), ledger, flow);
    }

    private static async Task BuildAsync(DimensionRunner runner, int build)
    {
        // A run's id is the build's place, so the dump writes the same id whichever process ran it.
        var outcome = await runner.BuildAsync([], new Guid(build, 0, 0, new byte[8]), "parity", CancellationToken.None);
        Assert.Equal(0, outcome.Failed);
    }

    private JsonObject Record(string id, string kind, JsonObject data, DateTime? created = null)
    {
        var record = _platform.Add(id, kind, data);
        record["createTime"] = OsduSearch.LuceneTime(created ?? new DateTime(2026, 9, 7, 11, 0, 0, DateTimeKind.Utc));
        return record;
    }

    private void Change(string id, DateTime at, Action<JsonObject> change)
    {
        var record = _platform.Records.Single(r => r["id"]!.GetValue<string>() == id);
        change(record["data"]!.AsObject());
        record["modifyTime"] = OsduSearch.LuceneTime(at);
    }

    [Fact]
    public async Task A_dimension_valued_by_labels_and_attributes_read_through_the_records_its_keys_name_keeps_what_it_kept()
    {
        // Political entity types, each holding its code, which a filter through a reference compares.
        foreach (var type in new[] { "Country", "Region" })
        {
            Record($"dev:reference-data--GeoPoliticalEntityType:{type}", "osdu:wks:reference-data--GeoPoliticalEntityType:1.0.0", new JsonObject { ["Code"] = type });
        }

        JsonObject Entity(string name, string type) => new()
        {
            ["GeoPoliticalEntityName"] = name, ["GeoPoliticalEntityTypeID"] = $"dev:reference-data--GeoPoliticalEntityType:{type}:",
        };
        Record("dev:master-data--GeoPoliticalEntity:MX", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Mexico", "Country"));
        Record("dev:master-data--GeoPoliticalEntity:CA", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Canada", "Country"));
        Record("dev:master-data--GeoPoliticalEntity:NO", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Norway", "Country"));
        Record("dev:master-data--GeoPoliticalEntity:RA", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Region A", "Region"));
        Record("dev:master-data--Field:FIELD-A", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Field A", ["FieldID"] = "f-a" });
        Record("dev:master-data--Field:FIELD-B", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Field B", ["FieldID"] = "f-b" });
        // A field of another kind than lookupKinds names, which a step through a reference to it does not find.
        Record("dev:master-data--Field:FIELD-C", "osdu:wks:master-data--Field:2.0.0", new JsonObject { ["FieldName"] = "Field C", ["FieldID"] = "f-c" });

        string[] countries = ["MX", "CA", "NO"];
        for (var i = 0; i < 80; i++)
        {
            var contexts = new JsonArray(
                new JsonObject { ["GeoPoliticalEntityID"] = "dev:master-data--GeoPoliticalEntity:RA:", ["GeoTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Region:" },
                new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{countries[i % 3]}:", ["GeoTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Country:" });
            if (i % 3 == 0)
            {
                contexts.Add(new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-B:" });
                contexts.Add(new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-A:" });
            }
            else if (i % 3 == 1)
            {
                contexts.Add(new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-A:" });
                contexts.Add(new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-C:" });
            }

            var data = new JsonObject
            {
                // Spaces around and inside a name, which cleaning folds and an attribute kept untrimmed keeps.
                ["FacilityName"] = i == 7 ? "   " : $"  Wellbore  {i:D3} ",
                ["GeoContexts"] = contexts,
            };
            if (i % 4 == 0)
            {
                data["EscapedCode"] = $"A%2F{i:D3}";
            }

            Record($"dev:master-data--Wellbore:W%2F{i:D3}", Wellbore, data);
        }

        // Logs of 90 wellbores, ten of which the partition does not hold, and a log naming none.
        for (var i = 0; i < 150; i++)
        {
            Record($"dev:work-product-component--WellLog:{i:D3}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W%2F{i % 90:D3}:" });
        }

        Record("dev:work-product-component--WellLog:none", WellLog, new JsonObject { ["Name"] = "no wellbore" });

        var (runner, ledger, flow) = Runner(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                unlabelled: Not specified
                clean: [trim, collapseSpaces]
                lookupKinds: [osdu:wks:master-data--Field:1.0.0]
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
                  CountryByType: ['data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID', data.GeoPoliticalEntityName]
                  CountryID: { path: 'data.GeoContexts[GeoTypeID*=:Country:].GeoPoliticalEntityID', keep: id }
                  Region: ['data.GeoContexts[GeoTypeID$=:Region:].GeoPoliticalEntityID', data.GeoPoliticalEntityName]
                  Fields: { path: [data.GeoContexts.FieldID, data.FieldName], many: join }
                  FieldMax: { path: [data.GeoContexts.FieldID, data.FieldID], many: max }
                  FieldRefs: { path: data.GeoContexts.FieldID, many: all, keep: reference }
                  FirstField: { path: [data.GeoContexts.FieldID, data.FieldName], maxReferences: 1 }
                  Name: { path: data.FacilityName, trim: false }
                  Code: { path: data.EscapedCode, decode: url }
                  WellboreKey: { path: id, keep: key }
              - name: WellboreCode
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                countRecords: true
            """);

        await BuildAsync(runner, 1);

        await AssertKeptAsync("labels-and-attributes", ledger, flow);
    }

    [Fact]
    public async Task A_dimension_of_a_nested_array_cleaned_into_values_keeps_what_it_kept()
    {
        var root = Samples.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "dictionaries"));
        Directory.CreateDirectory(Path.Combine(root, "flows"));
        await File.WriteAllTextAsync(Path.Combine(root, "dictionaries", "Curves.yaml"), """
            documentType: dictionary
            name: Curves
            entries:
              GR-001: GAMMA
              GR-002: GAMMA
              OLD-1: ~
            """);

        // Logs of curves whose mnemonics are spelled three ways each, a group of keys one replace makes one value, a
        // mnemonic cleaning leaves nothing of, one a dictionary leaves out, and one no query can carry.
        var mnemonics = new List<string>();
        for (var n = 1; n <= 400; n++)
        {
            mnemonics.Add($"GR-{n:D3}");
            mnemonics.Add($"gr {n:D3}");
            mnemonics.Add($" Gr_{n:D3} ");
        }

        for (var n = 0; n < 1_200; n++)
        {
            mnemonics.Add($"ZZ-{n:D4}");
        }

        mnemonics.Add("   ");
        mnemonics.Add("OLD-1");
        mnemonics.Add("BAD\u0001CURVE");
        var log = 0;
        for (var at = 0; at < mnemonics.Count; at += 3)
        {
            var curves = new JsonArray(mnemonics.Skip(at).Take(3).Select(m => (JsonNode?)new JsonObject { ["Mnemonic"] = m }).ToArray());
            // Every fifth log holds its first curve twice, so a value's records are not its keys' counts summed.
            if (log % 5 == 0)
            {
                curves.Add(new JsonObject { ["Mnemonic"] = mnemonics[at] });
            }

            Record($"dev:work-product-component--WellLog:{log++:D4}", WellLog, new JsonObject { ["Curves"] = curves });
        }

        var (runner, ledger, flow) = Runner(Head + """
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
                countRecords: true
                clean:
                  - trim
                  - upper
                  - replace: { pattern: '[ _]', with: '-' }
                  - replace: { pattern: '^ZZ-[0-9]+$', with: 'ZZ' }
                  - map: { dictionary: Curves }
            """, Path.Combine(root, "flows", "dims.yaml"));

        await BuildAsync(runner, 1);

        await AssertKeptAsync("curves-cleaned", ledger, flow);
    }

    [Fact]
    public async Task Dimensions_collecting_values_of_their_own_records_keep_what_they_kept()
    {
        // Logs of sixty wellbores from three sources, one spelled with a trailing space and one no source, more records than
        // the few values take reads, so they are collected a read per value; and runs of seven hundred names, more than one
        // query can exclude, so they are collected in one pass.
        string?[] sources = ["WL", "MWD", "WL ", null];
        for (var i = 0; i < 3_100; i++)
        {
            var data = new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:WB-{i % 60:D2}:", ["LogRun"] = $"R{i % 700:D3}" };
            if (sources[(i / 60) % 4] is { } source)
            {
                data["Source"] = source;
            }

            Record($"dev:work-product-component--WellLog:{i:D3}", WellLog, data);
        }

        var (runner, ledger, flow) = Runner(Head + """
            dimensions:
              - name: WellboreSources
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: No source
                attributes:
                  Source: { collect: data.Source }
              - name: WellboreRuns
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  Run: { collect: data.LogRun }
            """);

        await BuildAsync(runner, 1);

        await AssertKeptAsync("collected", ledger, flow);
    }

    [Fact]
    public async Task Dimensions_of_elements_keep_what_they_kept()
    {
        // Logs of curves, each curve's unit a reference and its log's version and owners read up its path; a log holding no
        // curve, one holding a null among them, and a description longer than a field keeps.
        for (var i = 0; i < 120; i++)
        {
            var curves = new JsonArray();
            for (var c = 0; c < i % 4; c++)
            {
                curves.Add(new JsonObject
                {
                    ["Mnemonic"] = $"C{c}",
                    ["CurveUnit"] = $"dev:reference-data--UnitOfMeasure:{(c % 2 == 0 ? "m" : "ft")}:",
                    ["CurveDescription"] = i == 5 && c == 0 ? new string('d', 4_100) : $"curve {c} of log {i}",
                    ["TopDepth"] = 100.5 + c,
                    ["Valid"] = c % 2 == 0,
                });
            }

            if (i == 9)
            {
                curves.Add(null);
            }

            var record = Record($"dev:work-product-component--WellLog:{i:D3}", WellLog, new JsonObject { ["Name"] = $"Log {i}", ["LogVersion"] = $"v{i % 3}", ["Curves"] = curves });
            record["acl"] = new JsonObject { ["owners"] = new JsonArray("data.owners-a@dev", "data.owners-b@dev"), ["viewers"] = new JsonArray("data.viewers@dev") };
        }

        for (var i = 0; i < 30; i++)
        {
            Record($"dev:master-data--Wellbore:WB-{i:D2}", Wellbore, new JsonObject
            {
                ["GeoContexts"] = new JsonArray(
                    new JsonObject { ["FieldID"] = $"dev:master-data--Field:F{i % 4}:", ["GeoTypeID"] = "dev:reference-data--FieldType:Field:" },
                    new JsonObject { ["GeoPoliticalEntityID"] = "dev:master-data--GeoPoliticalEntity:NO:", ["GeoTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Country:" }),
            });
        }

        var (runner, ledger, flow) = Runner(Head + """
            dimensions:
              - name: LogCurve
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                label: data.Name
                columns: { key: WellLogID, value: WellLogName }
                elements:
                  path: data.Curves
                  nulls: keep
                  fields:
                    Mnemonic: Mnemonic
                    CurveUnitID: { path: CurveUnit, keep: id }
                    Description: CurveDescription
                    TopDepth: TopDepth
                    Valid: Valid
                    LogVersion: { path: LogVersion, up: 1 }
                    Owners: { path: acl.owners, up: 2, many: join }
              - name: WellboreField
                kind: "osdu:wks:master-data--Wellbore:*"
                path: id
                columns: { key: WellboreID, value: WellboreCode }
                elements:
                  path: 'data.GeoContexts[GeoTypeID$=:Field:]'
                  fields:
                    FieldID: { path: FieldID, keep: id }
            """);

        await BuildAsync(runner, 1);

        await AssertKeptAsync("elements", ledger, flow);
    }

    [Fact]
    public async Task An_incremental_load_after_a_full_one_keeps_what_it_kept()
    {
        static DateTime At(int hour, int minute = 0) => new(2026, 9, 7, hour, minute, 0, DateTimeKind.Utc);
        Record("dev:master-data--GeoPoliticalEntity:NO", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["Name"] = "Norway" }, At(11));
        Record("dev:master-data--GeoPoliticalEntity:MX", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["Name"] = "Mexico" }, At(11));
        foreach (var (code, name, country) in new[] { ("A", "A-1", "NO"), ("B", "B-1", "NO"), ("C", "C-1", "MX"), ("D", "D-1", "MX") })
        {
            Record($"dev:master-data--Wellbore:{code}", Wellbore,
                new JsonObject { ["FacilityName"] = name, ["CountryID"] = $"dev:master-data--GeoPoliticalEntity:{country}:" }, At(11));
        }

        foreach (var (id, wellbore, source) in new[] { ("1", "A", "WL"), ("2", "A", "MWD"), ("3", "B", "WL"), ("4", "D", "WL"), ("5", "D", "MWD") })
        {
            Record($"dev:work-product-component--WellLog:{id}", WellLog, new JsonObject
            {
                ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:",
                ["Source"] = source,
                ["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = $"GR{id}" }, new JsonObject { ["Mnemonic"] = $"DT{id}" }),
            }, At(11));
        }

        var (runner, ledger, flow) = Runner(Head + """
            incremental:
              keyColumns: [id]

            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: [data.CountryID, data.Name]
              - name: WellboreSources
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: No source
                attributes:
                  Source: { collect: data.Source }
              - name: LogCurve
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                columns: { key: WellLogID }
                elements:
                  path: data.Curves
                  fields:
                    Mnemonic: Mnemonic
            """);

        await BuildAsync(runner, 1);
        await AssertKeptAsync("incremental-full", ledger, flow);

        // At 12:30: log 3 moves from wellbore B to C; log 6 of wellbore A is created; wellbore D is renamed, which its keys'
        // labels were read through; Mexico is renamed; a curve of log 1 changes; log 2 leaves the index.
        Change("dev:work-product-component--WellLog:3", At(12, 30), data => data["WellboreID"] = "dev:master-data--Wellbore:C:");
        Record("dev:work-product-component--WellLog:6", WellLog, new JsonObject
        {
            ["WellboreID"] = "dev:master-data--Wellbore:A:", ["Source"] = "LWD", ["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = "GR6" }),
        }, At(12, 30));
        Change("dev:master-data--Wellbore:D", At(12, 30), data => data["FacilityName"] = "D-1 renamed");
        Change("dev:master-data--GeoPoliticalEntity:MX", At(12, 30), data => data["Name"] = "United Mexican States");
        Change("dev:work-product-component--WellLog:1", At(12, 30), data => data["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = "GR1-new" }));
        _platform.Records.RemoveAll(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:2");
        _clock.Advance(TimeSpan.FromHours(1));

        await BuildAsync(runner, 2);
        await AssertKeptAsync("incremental-then", ledger, flow);
    }

    /// <summary>
    /// Asserts that what the flow's dimensions keep now is what the suites keep for <paramref name="scenario"/>; when it is
    /// not, what they keep now is written beside the test binaries and the failure names the file, so a change that is
    /// meant is taken over by copying that file onto the kept one after reading what moved.
    /// </summary>
    private static async Task AssertKeptAsync(string scenario, OsduLedger ledger, DimensionFlowDefinition flow)
    {
        var now = await DumpAsync(ledger, flow);
        var kept = Path.Combine(KeptFolder, scenario + ".txt");
        var expected = File.Exists(kept) ? Normalized(await File.ReadAllTextAsync(kept)) : null;
        if (string.Equals(expected, now, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(NowFolder);
        var written = Path.Combine(NowFolder, scenario + ".txt");
        await File.WriteAllTextAsync(written, now);
        Assert.Fail(
            (expected is null
                ? $"The suites keep nothing for scenario {scenario} (Fixtures/dimension-builds/{scenario}.txt). "
                : $"The builds of scenario {scenario} no longer keep what the suites keep for it (Fixtures/dimension-builds/{scenario}.txt). ") +
            $"What they keep now is in {written}; when the change is meant, read what moved and copy that file over the kept one.");
    }

    /// <summary>A kept file as a dump compares it: its line endings those a dump writes, whatever a checkout made of them.</summary>
    private static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Every row the flow's dimensions keep, dimension by dimension in the order the flow declares them.</summary>
    private static async Task<string> DumpAsync(OsduLedger ledger, DimensionFlowDefinition flow)
    {
        var text = new StringBuilder();
        await using var connection = new SqlConnection(OsduTestServer.ConnectionString);
        await connection.OpenAsync();
        foreach (var declared in flow.Dimensions)
        {
            var dimension = await ledger.FindDimensionAsync(flow.LedgerId, declared.Name) ?? throw new InvalidOperationException($"No dimension {declared.Name}.");
            text.Append("# dimension ").Append(declared.Name).Append('\n');
            var names = await NamesAsync(connection, dimension.DimensionId);
            foreach (var table in Tables)
            {
                var rows = await RowsAsync(connection, table, dimension.DimensionId, names);
                text.Append("## ").Append(table).Append(" (").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                foreach (var row in rows)
                {
                    text.Append(row).Append('\n');
                }
            }

            if (dimension.TableName is { } table1)
            {
                var rows = await TableRowsAsync(connection, table1, names);
                text.Append("## dim table ").Append(table1).Append(" (").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                foreach (var row in rows)
                {
                    text.Append(row).Append('\n');
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>What each surrogate number of a dimension stands for: keys, values, attributes, builds and runs.</summary>
    private sealed record Names(
        IReadOnlyDictionary<long, string> Keys, IReadOnlyDictionary<long, string> KeyArrival, IReadOnlyDictionary<long, string> Members,
        IReadOnlyDictionary<long, string> Attributes, IReadOnlyDictionary<long, string> Builds, IReadOnlyDictionary<Guid, string> Runs);

    private static async Task<Names> NamesAsync(SqlConnection connection, int dimensionId)
    {
        var keys = new Dictionary<long, string>();
        var arrival = new Dictionary<long, string>();
        await foreach (var (id, text) in PairsAsync(connection, "SELECT [ValueId], [Original] FROM [osdu].[DimensionValue] WHERE [DimensionId] = @d ORDER BY [ValueId]", dimensionId))
        {
            keys[id] = "key " + Quoted(text);
            arrival[id] = "#" + (arrival.Count + 1).ToString(CultureInfo.InvariantCulture);
        }

        var members = new Dictionary<long, string>();
        await foreach (var (id, text) in PairsAsync(connection, "SELECT [MemberId], [Value] FROM [osdu].[DimensionMember] WHERE [DimensionId] = @d", dimensionId))
        {
            members[id] = "value " + Quoted(text);
        }

        var attributes = new Dictionary<long, string>();
        await foreach (var (id, text) in PairsAsync(connection, "SELECT [AttributeId], [Name] FROM [osdu].[DimensionAttributeName] WHERE [DimensionId] = @d", dimensionId))
        {
            attributes[id] = "attribute " + text;
        }

        var builds = new Dictionary<long, string>();
        var runs = new Dictionary<Guid, string>();
        await using (var command = new SqlCommand("SELECT [DimensionRunId], [RunId] FROM [osdu].[DimensionRun] WHERE [DimensionId] = @d ORDER BY [DimensionRunId]", connection))
        {
            command.Parameters.AddWithValue("@d", dimensionId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builds[reader.GetInt64(0)] = "build " + (builds.Count + 1).ToString(CultureInfo.InvariantCulture);
                var run = reader.GetGuid(1);
                if (!runs.ContainsKey(run))
                {
                    runs[run] = "run " + (runs.Count + 1).ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return new Names(keys, arrival, members, attributes, builds, runs);
    }

    private static async IAsyncEnumerable<(long Id, string Text)> PairsAsync(SqlConnection connection, string sql, int dimensionId)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@d", dimensionId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            yield return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1));
        }
    }

    /// <summary>
    /// Every row <paramref name="table"/> keeps of the dimension, each column written by what it holds: a key's rows in the
    /// order they arrived, a build's in the order they were made, and every other table's in the order of what they say.
    /// </summary>
    private static async Task<List<string>> RowsAsync(SqlConnection connection, string table, int dimensionId, Names names)
    {
        var ordered = table switch
        {
            "DimensionValue" => " ORDER BY [ValueId]",
            "DimensionRun" => " ORDER BY [DimensionRunId]",
            _ => string.Empty,
        };
        var rows = new List<string>();
        await using var command = new SqlCommand($"SELECT * FROM [osdu].[{table}] WHERE [DimensionId] = @d{ordered}", connection);
        command.Parameters.AddWithValue("@d", dimensionId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (Unwritten.Contains(name))
                {
                    continue;
                }

                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                columns.Add(name + "=" + Written(name, value, table, names));
            }

            rows.Add(string.Join(" | ", columns));
        }

        if (ordered.Length == 0)
        {
            rows.Sort(StringComparer.Ordinal);
        }

        return rows;
    }

    /// <summary>Every row the dimension's own table holds, in the order of the rows' numbers, each number written as its place.</summary>
    private static async Task<List<string>> TableRowsAsync(SqlConnection connection, string table, Names names)
    {
        var rows = new List<string>();
        await using var command = new SqlCommand($"SELECT * FROM [osdu].{DimensionTables.Quoted(table)} WHERE [partition] = N'dev' ORDER BY [id]", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var columns = new List<string> { "row #" + (rows.Count + 1).ToString(CultureInfo.InvariantCulture) };
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (name == "id")
                {
                    continue;
                }

                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                columns.Add(name + "=" + (name == "key_id" && value is not null ? names.Keys[Convert.ToInt64(value, CultureInfo.InvariantCulture)] : Plain(value)));
            }

            rows.Add(string.Join(" | ", columns));
        }

        return rows;
    }

    private static string Written(string column, object? value, string table, Names names)
    {
        if (value is null)
        {
            return "null";
        }

        if (BuildColumns.Contains(column))
        {
            return names.Builds.TryGetValue(Convert.ToInt64(value, CultureInfo.InvariantCulture), out var build) ? build : "build of another dimension";
        }

        return column switch
        {
            "ValueId" when table == "DimensionValue" => names.KeyArrival[(long)value],
            "ValueId" => names.Keys[(long)value],
            "MemberId" when table == "DimensionMember" => "-",
            "MemberId" or "FromMemberId" or "ToMemberId" => names.Members[(long)value],
            "AttributeId" when table == "DimensionAttributeName" => "-",
            "AttributeId" => names.Attributes[Convert.ToInt64(value, CultureInfo.InvariantCulture)],
            "RunId" => names.Runs[(Guid)value],
            _ => Plain(value),
        };
    }

    private static string Plain(object? value) => value switch
    {
        null => "null",
        string text => Quoted(text),
        byte[] bytes => Convert.ToHexString(bytes),
        DateTime at => at.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => Quoted(value.ToString() ?? string.Empty),
    };

    private static string Quoted(string text) => JsonSerializer.Serialize(text, Readable);
}
