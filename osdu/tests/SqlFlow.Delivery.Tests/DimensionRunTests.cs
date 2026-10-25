using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension flow's build end to end (osdu/docs/reference/flow/dimension-build.md, What a build does): the field settled
/// from the templates of the flow's template folders or the record's own mapping, every distinct value read from a platform whose aggregation
/// returns few groups at a time, cleaned into members with their filters, and kept in the ledger on SQL Server; a
/// dimension that cannot be built failing alone; and the plan. Every request the build makes is held to the pinned
/// search contract.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionRunTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Wellbore = Samples.WellboreKind;

    private const string Head = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }

        """;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private async Task<(DimensionRunner Runner, OsduLedger Ledger, DimensionFlowDefinition Flow)> RunnerAsync(string yaml, string source = "flows/dims.yaml")
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, source).ForPartition("dev");
        return (new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true), ledger, flow);
    }

    /// <summary>Well logs, each holding curves whose mnemonics are spelled several ways.</summary>
    private void Logs(int logs, int curvesPerLog, int mnemonics)
    {
        var seen = new Dictionary<int, int>();
        for (var log = 0; log < logs; log++)
        {
            var curves = new JsonArray();
            for (var curve = 0; curve < curvesPerLog; curve++)
            {
                var n = ((log * curvesPerLog) + curve) % mnemonics;
                // Each mnemonic spelled in upper case, and every third one in lower case every other time it occurs.
                var occurrence = seen[n] = seen.GetValueOrDefault(n) + 1;
                var spelled = n % 3 == 0 && occurrence % 2 == 0 ? $"m{n:D4}" : $"M{n:D4}";
                curves.Add(new JsonObject { ["Mnemonic"] = spelled });
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["Curves"] = curves });
        }
    }

    private static async Task<IReadOnlyList<DimensionMemberState>> MembersAsync(OsduLedger ledger, int dimensionId)
    {
        var members = new List<DimensionMemberState>();
        DimensionMemberCursor? after = null;
        while (true)
        {
            var page = await ledger.ListDimensionMembersAsync(dimensionId, new DimensionMemberQuery(null, false, after, OsduLedger.MaxDimensionPage));
            members.AddRange(page);
            if (page.Count < OsduLedger.MaxDimensionPage)
            {
                return members;
            }

            after = new DimensionMemberCursor(page[^1].Value, page[^1].Records);
        }
    }

    [Fact]
    public async Task Every_value_of_a_nested_array_is_read_past_the_aggregation_limit_cleaned_and_kept_with_its_filter()
    {
        // More logs than one cursor page, so the first cut-off range is split rather than scanned.
        Logs(logs: 3_000, curvesPerLog: 3, mnemonics: 1_500);
        var (runner, ledger, flow) = await RunnerAsync(Head + $$"""
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
                clean: [trim, upper]
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var spellings = _platform.Records
            .SelectMany(r => r["data"]!["Curves"]!.AsArray().Select(c => c!["Mnemonic"]!.GetValue<string>()))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var built = Assert.Single(outcome.Dimensions);
        Assert.Equal(DimensionRunStatus.Completed, built.Status);
        Assert.Equal("nested(data.Curves, Mnemonic.keyword)", built.AggregateBy);
        Assert.Equal(1_500, built.Values);
        Assert.Equal(spellings.Count, built.Keys);
        Assert.True(spellings.Count > 1_500, "some mnemonics are spelled more than one way");
        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));

        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "CurveMnemonic"))!;
        var members = await MembersAsync(ledger, dimension.DimensionId);
        Assert.Equal(1_500, members.Count);
        var twice = spellings.Where(s => s[0] == 'm').Select(s => "M" + s[1..]).First(upper => spellings.Contains(upper));
        var member = members.Single(m => m.Value == twice);
        Assert.Equal(2, member.Originals);
        Assert.Equal(1, member.FilterParts);
        Assert.Equal(
            $"(nested(data.Curves, (Mnemonic.keyword:\"{twice}\"))) OR (nested(data.Curves, (Mnemonic.keyword:\"m{twice[1..]}\")))",
            member.Filter);
        Assert.False(member.RecordsExact);

        var run = (await ledger.ListDimensionRunsAsync(dimension.DimensionId, 1)).Single();
        Assert.True(run.Read.Splits > 0, "the values could only be read past the limit by splitting");
        Assert.Contains(WellLog, run.Read.Templates, StringComparison.Ordinal);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_second_build_keeps_what_did_not_change_and_logs_what_did()
    {
        Logs(logs: 20, curvesPerLog: 2, mnemonics: 10);
        var yaml = Head + """
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
            """;
        var (runner, ledger, flow) = await RunnerAsync(yaml);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        _platform.Records.RemoveAll(r => r["data"]!["Curves"]!.AsArray().Any(c => c!["Mnemonic"]!.GetValue<string>() is "M0000" or "m0000"));
        _platform.Add("dev:work-product-component--WellLog:new", WellLog, new JsonObject { ["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = "NEW" }) });
        var second = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var changes = Assert.Single(second.Dimensions).Changes;
        Assert.Equal(1, changes.KeysAdded);
        Assert.True(changes.KeysRemoved >= 1);
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "CurveMnemonic"))!;
        Assert.Contains(await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, null, null, null, 100)), c => c.Original == "NEW" && c.Change == DimensionChangeKinds.Added);
    }

    [Fact]
    public async Task A_key_naming_a_record_is_valued_by_the_label_read_there_and_every_key_and_value_carries_the_search_finding_it()
    {
        // Two wellbores in Mexico, one of them in Field A, and one with no name; logs of each, and a log of a wellbore the
        // partition does not hold.
        const string Mexico = "dev:master-data--GeoPoliticalEntity:Mexico";
        _platform.Add(Mexico, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Mexico" });
        _platform.Add("dev:master-data--Wellbore:A", Wellbore, new JsonObject
        {
            ["FacilityName"] = "A/1-A",
            ["GeoContexts"] = new JsonArray(
                new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-A:" },
                new JsonObject { ["GeoPoliticalEntityID"] = Mexico + ":" }),
        });
        _platform.Add("dev:master-data--Wellbore:B", Wellbore, new JsonObject
        {
            ["FacilityName"] = "A/1-B",
            ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = Mexico + ":" }),
        });
        _platform.Add("dev:master-data--Wellbore:C", Wellbore, new JsonObject { ["FacilityName"] = "  " });
        foreach (var (log, wellbore) in new[] { ("1", "A"), ("2", "A"), ("3", "B"), ("4", "C"), ("5", "Z") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
              - name: Country
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]
                unlabelled: Not specified
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((2, 0), (outcome.Built, outcome.Failed));
        Assert.Equal([2L, 2L], outcome.Dimensions.Select(d => d.Labelled));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original, StringComparer.Ordinal);

        // A key is the reference exactly as the index holds it; its value the name the wellbore holds.
        var a = keys["dev:master-data--Wellbore:A:"];
        Assert.Equal(("A/1-A", "dev:master-data--Wellbore:A", "A/1-A"), (a.Label, a.LabelFrom, a.MemberValue));

        // A key whose record the search does not hold, or whose record holds no name, is valued by the code its id ends with,
        // and the build says why.
        Assert.Equal((null, "Z"), (keys["dev:master-data--Wellbore:Z:"].Label, keys["dev:master-data--Wellbore:Z:"].MemberValue));
        Assert.Equal((null, "dev:master-data--Wellbore:C"), (keys["dev:master-data--Wellbore:C:"].Label, keys["dev:master-data--Wellbore:C:"].LabelFrom));
        var run = (await ledger.ListDimensionRunsAsync(wellbores.DimensionId, 1)).Single();
        Assert.Equal((2L, 2L, 1), (run.Read.Labelled, run.Read.Unlabelled, run.Read.LabelQueries));
        Assert.Contains(run.Read.Notes, n => n.Contains("the search holds no record they name", StringComparison.Ordinal));
        Assert.Contains(run.Read.Notes, n => n.Contains("holds nothing at the path it is read from", StringComparison.Ordinal));

        // Each key's filter finds exactly the records holding it, and so does its value's.
        Assert.Equal(["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2"], _platform.Find(WellLog, a.Filter!));
        var named = (await MembersAsync(ledger, wellbores.DimensionId)).Single(m => m.Value == "A/1-A");
        Assert.Equal(["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2"], _platform.Find(WellLog, named.Filter!));

        // A label read through two records: the wellbore's country reference, then the country's name.
        var countries = (await ledger.FindDimensionAsync(flow.LedgerId, "Country"))!;
        var mexico = (await MembersAsync(ledger, countries.DimensionId)).Single(m => m.Value == "Mexico");
        Assert.Equal((2, 3L), (mexico.Originals, mexico.Records));
        var country = (await ledger.ListDimensionRunsAsync(countries.DimensionId, 1)).Single();
        Assert.Equal((2L, 2L, 2), (country.Read.Labelled, country.Read.Unlabelled, country.Read.LabelQueries));
        var ofMexico = await ledger.ListDimensionValuesAsync(countries.DimensionId, new DimensionValueQuery(null, mexico.MemberId, false, false, null, 10));
        Assert.All(ofMexico, k => Assert.Equal(Mexico, k.LabelFrom));

        // The keys whose country is not read are one value a drop-down lists, as the dimension names it, and its filter
        // finds exactly their logs.
        var unspecified = (await MembersAsync(ledger, countries.DimensionId)).Single(m => m.Value == "Not specified");
        Assert.Equal(2, unspecified.Originals);
        Assert.Equal(["dev:work-product-component--WellLog:4", "dev:work-product-component--WellLog:5"], _platform.Find(WellLog, unspecified.Filter!));

        // The search across both: logs of a Mexican wellbore named A/1-A; across one, every log of Mexico.
        var picked = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(countries, [], ["Mexico"]), new DimensionPick(wellbores, [], ["A/1-A"])], null, null, CancellationToken.None);
        Assert.Equal(WellLog.Replace("1.4.0", "*", StringComparison.Ordinal), picked.Kind);
        Assert.Equal(["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2"], _platform.Find(WellLog, picked.Query));
        Assert.Equal(3, picked.Clauses);
        var all = await DimensionSearch.ComposeAsync(ledger, [new DimensionPick(countries, [], ["Mexico"])], null, null, CancellationToken.None);
        Assert.Equal(3, _platform.Find(WellLog, all.Query).Count);

        // Narrowed by a query of its own, the search still finds only what every part allows.
        var within = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(countries, [], ["Mexico"])], null, "id:\"dev:work-product-component--WellLog:3\"", CancellationToken.None);
        Assert.Equal(["dev:work-product-component--WellLog:3"], _platform.Find(WellLog, within.Query));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_key_s_attributes_are_read_through_the_records_it_names_kept_and_looked_up_and_a_search_is_picked_by_them()
    {
        // Wellbores whose ids escape a slash, each in a region and a country, one of them in a field; logs of each. A country
        // is the political entity whose own type is Country, as a facade service in front of the DDMS tells it, whatever
        // order a wellbore lists them in.
        const string Mexico = "dev:master-data--GeoPoliticalEntity:MX";
        const string Canada = "dev:master-data--GeoPoliticalEntity:CA";
        const string RegionA = "dev:master-data--GeoPoliticalEntity:RegionA";
        JsonObject Entity(string name, string type) => new()
        {
            ["GeoPoliticalEntityName"] = name, ["GeoPoliticalEntityTypeID"] = $"dev:reference-data--GeoPoliticalEntityType:{type}:",
        };
        _platform.Add(Mexico, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Mexico", "Country"));
        _platform.Add(Canada, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Canada", "Country"));
        _platform.Add(RegionA, "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", Entity("Region A", "Region"));
        _platform.Add("dev:master-data--Field:FIELD-A", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Field A" });
        JsonObject Context(string entity, string type) => new() { ["GeoPoliticalEntityID"] = entity + ":", ["GeoTypeID"] = $"dev:reference-data--GeoPoliticalEntityType:{type}:" };
        _platform.Add("dev:master-data--Wellbore:A%2F1-A", Wellbore, new JsonObject
        {
            ["FacilityName"] = "Wellbore A/1-A",
            ["GeoContexts"] = new JsonArray(Context(RegionA, "Region"), Context(Mexico, "Country"), new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-A:" }),
        });
        _platform.Add("dev:master-data--Wellbore:A%2F1-B", Wellbore, new JsonObject
        {
            ["FacilityName"] = "Wellbore A/1-B",
            // Contexts that do not say their type: the entities' own types tell the country.
            ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = RegionA + ":" }, new JsonObject { ["GeoPoliticalEntityID"] = Mexico + ":" }),
        });
        _platform.Add("dev:master-data--Wellbore:B%2F7-1", Wellbore, new JsonObject
        {
            ["FacilityName"] = "Wellbore B/7-1",
            ["GeoContexts"] = new JsonArray(Context(Canada, "Country")),
        });
        foreach (var (log, wellbore) in new[] { ("1", "A%2F1-A"), ("2", "A%2F1-A"), ("3", "A%2F1-B"), ("4", "B%2F7-1") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName']
                  Field: [data.GeoContexts.FieldID, data.FieldName]
                  Region: ['data.GeoContexts[GeoTypeID$=:Region:].GeoPoliticalEntityID', data.GeoPoliticalEntityName]
              - name: WellboreCode
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((2, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original, StringComparer.Ordinal);

        // Each key keeps its attributes: the country the entity whose own type is Country, not the region before it; the
        // region read through the context whose type ends so.
        var a = keys["dev:master-data--Wellbore:A%2F1-A:"];
        Assert.Equal(
            [("Country", "Mexico", (string?)Mexico), ("Field", "Field A", "dev:master-data--Field:FIELD-A"), ("Region", "Region A", RegionA)],
            a.Attributes.Select(x => (x.Name, x.Value, x.From)));
        Assert.Equal([("Country", "Mexico")], keys["dev:master-data--Wellbore:A%2F1-B:"].Attributes.Select(x => (x.Name, x.Value)));
        Assert.Equal([("Country", "Canada")], keys["dev:master-data--Wellbore:B%2F7-1:"].Attributes.Select(x => (x.Name, x.Value)));
        var run = (await ledger.ListDimensionRunsAsync(wellbores.DimensionId, 1)).Single();
        Assert.Contains(run.Read.Notes, n => n.Contains("key(s) have no Field", StringComparison.Ordinal));

        // A key naming a record with no label is valued by the code its id ends with, its escapes decoded.
        var codes = (await ledger.FindDimensionAsync(flow.LedgerId, "WellboreCode"))!;
        Assert.Equal(["A/1-A", "A/1-B", "B/7-1"], (await MembersAsync(ledger, codes.DimensionId)).Select(m => m.Value));

        // Values and keys are looked up by their attributes, and an attribute lists its values with their keys and records.
        var mexican = await ledger.ListDimensionMembersAsync(
            wellbores.DimensionId, new DimensionMemberQuery(null, false, null, 10, DimensionMemberOrder.Value, [new DimensionAttributeMatch("Country", ["Mexico"])]));
        Assert.Equal(["Wellbore A/1-A", "Wellbore A/1-B"], mexican.Select(m => m.Value));
        Assert.Contains(mexican[0].Attributes, x => x is { Name: "Country", Value: "Mexico", Keys: 1 });
        var both = await ledger.ListDimensionValuesAsync(
            wellbores.DimensionId,
            new DimensionValueQuery(null, null, false, false, null, 10, DimensionValueOrder.Arrival,
                [new DimensionAttributeMatch("Country", ["Mexico", "Canada"]), new DimensionAttributeMatch("Field", ["Field A"])]));
        Assert.Equal(["dev:master-data--Wellbore:A%2F1-A:"], both.Select(k => k.Original));
        Assert.Equal(
            [("Mexico", 2, 3L), ("Canada", 1, 1L)],
            (await ledger.ListDimensionAttributeValuesAsync(wellbores.DimensionId, new DimensionAttributeValueQuery("Country", null, 10))).Select(v => (v.Value, v.Keys, v.Records)));

        // A search picked by an attribute finds the logs of every wellbore holding it.
        var inMexico = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("country", ["Mexico"])])], null, null, CancellationToken.None);
        Assert.Equal(
            ["dev:work-product-component--WellLog:1", "dev:work-product-component--WellLog:2", "dev:work-product-component--WellLog:3"],
            _platform.Find(WellLog, inMexico.Query));
        Assert.Equal(["Wellbore A/1-A", "Wellbore A/1-B"], Assert.Single(inMexico.Parts).Values.Select(v => v.Value).Order(StringComparer.Ordinal));
        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("Basin", ["X"])])], null, null, CancellationToken.None));
        Assert.Contains("reads no attribute 'Basin'; it reads Country, Field, Region", unknown.Message, StringComparison.Ordinal);

        // A second build reads the attributes again: a country renamed is rewritten, a field no longer named is dropped.
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == Mexico)["data"]!["GeoPoliticalEntityName"] = "United Mexican States";
        var first = _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:master-data--Wellbore:A%2F1-A")["data"]!.AsObject();
        first["GeoContexts"] = new JsonArray(Context(RegionA, "Region"), Context(Mexico, "Country"));
        await runner.BuildAsync(["Wellbore"], Guid.NewGuid(), "tests", CancellationToken.None);
        var again = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .Single(k => k.Original == "dev:master-data--Wellbore:A%2F1-A:");
        Assert.Equal([("Country", "United Mexican States"), ("Region", "Region A")], again.Attributes.Select(x => (x.Name, x.Value)));
        var second = (await ledger.ListDimensionRunsAsync(wellbores.DimensionId, 1)).Single();
        Assert.Contains(second.Read.Notes, n => n.Contains("attribute value(s) of keys were added, rewritten or dropped", StringComparison.Ordinal));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task An_attribute_keeps_the_greatest_or_every_value_of_the_records_it_reaches_and_decodes_what_it_reads()
    {
        // A wellbore in two fields, a country and a block, and a basin; another in one field, its block context naming no
        // entity the search holds. A unit whose code the record holds escaped.
        JsonObject Context(string reference, string type)
            => reference.Contains("--Field:", StringComparison.Ordinal) ? new() { ["FieldID"] = reference + ":", ["GeoTypeID"] = type }
                : reference.Contains("--Basin:", StringComparison.Ordinal) ? new() { ["BasinID"] = reference + ":", ["GeoTypeID"] = type }
                : new() { ["GeoPoliticalEntityID"] = reference + ":", ["GeoTypeID"] = type };
        const string Country = "dev:reference-data--GeoPoliticalEntityType:Country:";
        const string Block = "dev:reference-data--GeoPoliticalEntityType:Block:";
        _platform.Add("dev:master-data--Field:ALPHA", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Alpha", ["FieldID"] = "f-2" });
        _platform.Add("dev:master-data--Field:BRAVO", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Bravo", ["FieldID"] = "f-1" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:NO", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Norway" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:B-15", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "15/9" });
        _platform.Add("dev:master-data--Basin:NS", "osdu:wks:master-data--Basin:1.2.0", new JsonObject { ["BasinName"] = "North Sea" });
        _platform.Add("dev:reference-data--BasinType:Rift", "osdu:wks:reference-data--BasinType:1.0.0", new JsonObject { ["Code"] = "Rift%2FSag" });
        var first = _platform.Add("dev:master-data--Wellbore:W1", Wellbore, new JsonObject
        {
            ["GeoContexts"] = new JsonArray(
                Context("dev:master-data--Field:ALPHA", "dev:reference-data--FieldType:Field:"),
                Context("dev:master-data--GeoPoliticalEntity:B-15", Block),
                Context("dev:master-data--Field:BRAVO", "dev:reference-data--FieldType:Field:"),
                Context("dev:master-data--GeoPoliticalEntity:NO", Country),
                Context("dev:master-data--Basin:NS", "dev:reference-data--BasinType:Rift:")),
        });
        first["createTime"] = "2024-03-01T10:15:00.123Z";
        _platform.Add("dev:master-data--Wellbore:W2", Wellbore, new JsonObject
        {
            ["GeoContexts"] = new JsonArray(
                Context("dev:master-data--Field:ALPHA", "dev:reference-data--FieldType:Field:"),
                Context("dev:master-data--GeoPoliticalEntity:GONE", Block)),
        });

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: WellboreContext
                kind: "osdu:wks:master-data--Wellbore:*"
                path: id
                columns: { key: WellboreID, value: WellboreCode }
                attributes:
                  FieldName: { path: [data.GeoContexts.FieldID, data.FieldName], many: max }
                  FieldUUID: { path: [data.GeoContexts.FieldID, data.FieldID], many: max }
                  Fields: { path: [data.GeoContexts.FieldID, data.FieldName], many: join }
                  FirstField: [data.GeoContexts.FieldID, data.FieldName]
                  Country: { path: ['data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityID', data.GeoPoliticalEntityName], many: max }
                  Block: { path: ['data.GeoContexts[GeoTypeID*=GeoPoliticalEntityType:Block:].GeoPoliticalEntityID', data.GeoPoliticalEntityName], many: max }
                  Basin: { path: [data.GeoContexts.BasinID, data.BasinName], many: max }
                  BasinTypeCode: { path: ['data.GeoContexts[GeoTypeID*=reference-data--BasinType:].GeoTypeID', data.Code], many: max, decode: url }
                  BasinTypeCodeRaw: { path: ['data.GeoContexts[GeoTypeID*=reference-data--BasinType:].GeoTypeID', data.Code] }
                  CreateTime: createTime
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "WellboreContext"))!;
        var keys = (await ledger.ListDimensionValuesAsync(dimension.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original, StringComparer.Ordinal);

        // max keeps the greatest of every record reached, each attribute on its own (the greatest name and the greatest
        // uuid come from two fields, as a MAX of each column does); join keeps them all in the order reached; the bare form
        // keeps the first. Filters pick the contexts of one type before the greatest is taken, and decode reads the code.
        // A key's attributes come back by name.
        Assert.Equal(
            [
                ("Basin", "North Sea", (string?)"dev:master-data--Basin:NS"), ("BasinTypeCode", "Rift/Sag", "dev:reference-data--BasinType:Rift"),
                ("BasinTypeCodeRaw", "Rift%2FSag", "dev:reference-data--BasinType:Rift"), ("Block", "15/9", "dev:master-data--GeoPoliticalEntity:B-15"),
                ("Country", "Norway", "dev:master-data--GeoPoliticalEntity:NO"), ("CreateTime", "2024-03-01T10:15:00.123Z", "dev:master-data--Wellbore:W1"),
                ("FieldName", "Bravo", "dev:master-data--Field:BRAVO"), ("Fields", "Alpha; Bravo", "dev:master-data--Field:ALPHA"),
                ("FieldUUID", "f-2", "dev:master-data--Field:ALPHA"), ("FirstField", "Alpha", "dev:master-data--Field:ALPHA"),
            ],
            keys["dev:master-data--Wellbore:W1"].Attributes.Select(x => (x.Name, x.Value, x.From)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));

        // A wellbore whose records reached hold nothing of an attribute has none of it, whatever many says.
        Assert.Equal(
            [("FieldName", "Alpha"), ("Fields", "Alpha"), ("FieldUUID", "f-2"), ("FirstField", "Alpha")],
            keys["dev:master-data--Wellbore:W2"].Attributes.Select(x => (x.Name, x.Value)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_wellbore_collects_its_logs_sources_and_its_table_feeds_cascading_selects()
    {
        // Two countries, two fields, and logs from two sources, one spelled with a trailing space; a log without a source,
        // and a wellbore no record describes.
        _platform.Add("dev:master-data--GeoPoliticalEntity:MX", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Mexico" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:CA", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Canada" });
        _platform.Add("dev:master-data--Field:FIELD-A", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Field A" });
        _platform.Add("dev:master-data--Field:FIELD-B", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Field B" });
        JsonObject Described(string name, string country, string? field)
        {
            var contexts = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{country}:" });
            if (field is not null)
            {
                contexts.Add(new JsonObject { ["FieldID"] = $"dev:master-data--Field:{field}:" });
            }

            return new JsonObject { ["FacilityName"] = name, ["GeoContexts"] = contexts };
        }

        _platform.Add("dev:master-data--Wellbore:A", Wellbore, Described("MX A", "MX", "FIELD-A"));
        _platform.Add("dev:master-data--Wellbore:B", Wellbore, Described("MX B", "MX", null));
        _platform.Add("dev:master-data--Wellbore:C", Wellbore, Described("CA C", "CA", "FIELD-B"));
        foreach (var (log, wellbore, source) in new[]
        {
            ("1", "A", "WELLDB"), ("2", "A", "WELLDB"), ("3", "A", "PETREL"), ("4", "B", "WELLDB"), ("5", "B", null), ("6", "C", "PETREL"),
            ("7", "D", null), ("8", "C", "WELLDB "),
        })
        {
            var data = new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" };
            if (source is not null)
            {
                data["Source"] = source;
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, data);
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                unlabelled: Not specified
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]
                  Field: [data.GeoContexts.FieldID, data.FieldName]
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original[(k.Original.LastIndexOf(':', k.Original.Length - 2) + 1)..^1], StringComparer.Ordinal);

        // Each wellbore collects the sources of its logs with how many hold each; a source spelled with a trailing space is
        // the same value; the logs holding none are Not specified, as an attribute a wellbore does not hold is.
        IEnumerable<(string, string, long?)> Of(string key) => keys[key].Attributes.Select(x => (x.Name, x.Value, x.Records));
        Assert.Equal([("Country", "Mexico", null), ("Field", "Field A", null), ("Source", "WELLDB", 2L), ("Source", "PETREL", 1L)], Of("A"));
        Assert.Equal([("Country", "Mexico", null), ("Field", "Not specified", null), ("Source", "Not specified", 1L), ("Source", "WELLDB", 1L)], Of("B"));
        Assert.Equal([("Country", "Canada", null), ("Field", "Field B", null), ("Source", "PETREL", 1L), ("Source", "WELLDB", 1L)], Of("C"));
        Assert.Equal([("Country", "Not specified", null), ("Field", "Not specified", null), ("Source", "Not specified", 1L)], Of("D"));
        Assert.Equal("Not specified", keys["D"].MemberValue);

        // Few logs: the sources were read in one pass of the cursor. The dimension keeps what each source value stands for,
        // which a search picking it asks for.
        Assert.Contains(_platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal));
        var collected = Assert.Single(DimensionRunner.CollectedOf(wellbores.CollectedJson));
        Assert.Equal(("Source", "data.Source", "Not specified"), (collected.Name, collected.Path, collected.Missing));
        Assert.Equal(
            [("PETREL", "PETREL", 2L), ("WELLDB", "WELLDB", 3L), ("WELLDB", "WELLDB ", 1L)],
            (await ledger.ListDimensionCollectedTextsAsync(wellbores.DimensionId, "Source", null, 10)).Select(v => (v.Value, v.Text, v.Records)));

        // Each list of a cascade lists what the other picks leave, its own pick aside.
        async Task<IEnumerable<(string, int, long)>> ListAsync(string name, IReadOnlyList<DimensionAttributeMatch>? picks = null, IReadOnlyCollection<long>? values = null)
            => (await ledger.ListDimensionAttributeValuesAsync(wellbores.DimensionId, new DimensionAttributeValueQuery(name, null, 10, picks, values)))
                .Select(v => (v.Value, v.Keys, v.Records));
        DimensionAttributeMatch Pick(string name, params string[] values) => new(name, values);
        Assert.Equal([("Field A", 1, 3L), ("Not specified", 1, 2L)], await ListAsync("Field", [Pick("Country", "Mexico")]));
        Assert.Equal([("WELLDB", 2, 3L), ("Not specified", 1, 1L), ("PETREL", 1, 1L)], await ListAsync("Source", [Pick("Country", "Mexico")]));
        Assert.Equal([("Mexico", 1, 3L), ("Canada", 1, 2L)], await ListAsync("Country", [Pick("Source", "PETREL")]));
        Assert.Equal(await ListAsync("Country", [Pick("Source", "PETREL")]), await ListAsync("Country", [Pick("Country", "Mexico"), Pick("Source", "PETREL")]));
        var a = (await ledger.GetDimensionMembersAsync(wellbores.DimensionId, [], ["MX A"])).Single();
        Assert.Equal([("WELLDB", 1, 2L), ("PETREL", 1, 1L)], await ListAsync("Source", null, [a.MemberId]));

        // The table: a row per wellbore and source, each with its country, field and records, what cascading selects read.
        // The export is the dimension's own table written out: each row under its number, with the key's number, in the
        // partition, and the key's filter at the end of the fixed columns.
        var table = new StringWriter();
        var rows = await DimensionExport.WriteAsync(ledger, wellbores, DimensionExportSet.Table, DimensionExportFormat.Csv, table, CancellationToken.None);
        var lines = table.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("id,partition,key_id,WellboreID,FacilityName,Country,Field,Source,records,filter", lines[0]);
        Assert.Equal(7, rows);
        string Filter(string key) => "\"" + keys[key].Filter!.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string Row(int id, string key, string rest) => $"{id},dev,{keys[key].ValueId},dev:master-data--Wellbore:{key}:,{rest}," + Filter(key);
        Assert.Equal(
            [
                Row(1, "C", "CA C,Canada,Field B,PETREL,1"),
                Row(2, "C", "CA C,Canada,Field B,WELLDB,1"),
                Row(3, "A", "MX A,Mexico,Field A,PETREL,1"),
                Row(4, "A", "MX A,Mexico,Field A,WELLDB,2"),
                Row(5, "B", "MX B,Mexico,Not specified,Not specified,1"),
                Row(6, "B", "MX B,Mexico,Not specified,WELLDB,1"),
                Row(7, "D", "Not specified,Not specified,Not specified,Not specified,1"),
            ],
            lines.Skip(1));
        var jsonl = new StringWriter();
        await DimensionExport.WriteAsync(ledger, wellbores, DimensionExportSet.Table, DimensionExportFormat.JsonLines, jsonl, CancellationToken.None);
        var firstRow = JsonNode.Parse(jsonl.ToString().Split("\n", StringSplitOptions.RemoveEmptyEntries)[0])!.AsObject();
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "Country", "Field", "Source", "records", "filter"], firstRow.Select(p => p.Key));
        Assert.Equal((1L, "dev", "dev:master-data--Wellbore:C:", "CA C", "PETREL", 1L), (
            firstRow["id"]!.GetValue<long>(), firstRow["partition"]!.GetValue<string>(), firstRow["WellboreID"]!.GetValue<string>(),
            firstRow["FacilityName"]!.GetValue<string>(), firstRow["Source"]!.GetValue<string>(), firstRow["records"]!.GetValue<long>()));

        // A search picking a source finds the logs holding it, however it is spelled, not every log of the wellbores holding
        // it; Not specified finds the logs holding none; and a source picked with a country, the country's logs holding it.
        async Task<IReadOnlyList<string>> FindAsync(params DimensionAttributeMatch[] picks)
        {
            var set = await DimensionSearch.ComposeAsync(ledger, [new DimensionPick(wellbores, [], [], picks)], null, null, CancellationToken.None);
            return _platform.Find(WellLog, set.Query).Select(id => id[(id.LastIndexOf(':') + 1)..]).ToList();
        }

        Assert.Equal(["1", "2", "4", "8"], await FindAsync(Pick("Source", "WELLDB")));
        Assert.Equal(["5", "7"], await FindAsync(Pick("Source", "Not specified")));
        Assert.Equal(["3", "5", "6", "7"], await FindAsync(Pick("Source", "PETREL", "Not specified")));
        Assert.Equal(["1", "2", "4"], await FindAsync(Pick("Country", "Mexico"), Pick("Source", "WELLDB")));
        var some = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [Pick("Source", "WELLDB", "NOPE")])], null, null, CancellationToken.None);
        Assert.Equal(["Wellbore.Source: NOPE"], some.Missing);
        var none = await Assert.ThrowsAsync<DeliveryException>(() => FindAsync(Pick("Source", "NOPE")));
        Assert.Contains("No value picked of attribute Source of dimension Wellbore is one it collects", none.Message, StringComparison.Ordinal);

        // A second build collects again: a wellbore whose last PETREL log became WELLDB holds WELLDB alone.
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:3")["data"]!["Source"] = "WELLDB";
        await runner.BuildAsync(["Wellbore"], Guid.NewGuid(), "tests", CancellationToken.None);
        var again = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .Single(k => k.Original == "dev:master-data--Wellbore:A:");
        Assert.Equal([("Source", "WELLDB", (long?)3L)], again.Attributes.Where(x => x.Name == "Source").Select(x => (x.Name, x.Value, x.Records)));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    /// <summary>The columns of a table in the module's schema, in order; none when it is not there.</summary>
    private async Task<IReadOnlyList<string>> ColumnsAsync(string table)
        => await SqlAsync($"SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(N'[osdu].[{table}]', N'U') ORDER BY c.[column_id];");

    /// <summary>
    /// The indexes of a table in the module's schema, the clustered one first and the rest by name, each as its name, its
    /// kind, whether it is the key, its columns and whether it holds the identity.
    /// </summary>
    private async Task<IReadOnlyList<string>> IndexesAsync(string table)
        => await SqlAsync($"""
            SELECT i.[name], i.[type_desc], CAST(i.[is_primary_key] AS int),
                STRING_AGG(c.[name], ', ') WITHIN GROUP (ORDER BY ic.[key_ordinal]), MAX(CAST(c.[is_identity] AS int))
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[osdu].[{table}]')
            GROUP BY i.[name], i.[type_desc], i.[type], i.[is_primary_key]
            ORDER BY CASE WHEN i.[type] = 1 THEN 0 ELSE 1 END, i.[name];
            """);

    /// <summary>The hash a table keeps of a key, as a view's join computes it: SHA-256 of the key's UTF-16 bytes, as SQL Server's HASHBYTES gives it.</summary>
    private static string KeyHash(string key) => "0x" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(key)));

    private async Task<List<string>> SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i)
                ? "(null)"
                : reader.GetValue(i) is byte[] bytes ? "0x" + Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    [Fact]
    public async Task An_attribute_that_keeps_the_key_joins_its_table_to_the_dimension_keyed_by_it()
    {
        foreach (var (name, country) in new[] { ("A", "Mexico"), ("B", "Canada") })
        {
            _platform.Add($"dev:master-data--Wellbore:{name}", Wellbore, new JsonObject { ["FacilityName"] = $"{country} {name}" });
        }

        foreach (var (log, wellbore) in new[] { ("1", "A"), ("2", "A"), ("3", "B") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject
            {
                ["Name"] = "LOG-" + log,
                ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:",
            });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: JoinedLog
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                label: data.Name
                columns: { key: WellLogID, value: WellLogName }
                attributes:
                  WellboreID: { path: data.WellboreID, keep: key }
                  WellboreUWI: [data.WellboreID, data.FacilityName]
              - name: JoinedWellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName

            """);

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // The kept key is the reference exactly as the index holds it, the text the other dimension keys by; the attribute
        // read through it is the value a person reads.
        Assert.Equal(
            ["dev:master-data--Wellbore:A:|Mexico A", "dev:master-data--Wellbore:A:|Mexico A", "dev:master-data--Wellbore:B:|Canada B"],
            await SqlAsync("SELECT [WellboreID], [WellboreUWI] FROM [osdu].[dim_JoinedLog] ORDER BY [WellLogName];"));

        // So the two tables join on it, with no normalising on either side.
        Assert.Equal(
            ["LOG-1|Mexico A", "LOG-2|Mexico A", "LOG-3|Canada B"],
            await SqlAsync("""
                SELECT l.[WellLogName], w.[FacilityName]
                FROM [osdu].[dim_JoinedLog] AS l
                INNER JOIN [osdu].[dim_JoinedWellbore] AS w ON w.[partition] = l.[partition] AND w.[WellboreID] = l.[WellboreID]
                ORDER BY l.[WellLogName];
                """));
    }

    [Fact]
    public async Task Elements_are_a_row_each_with_their_fields_together_and_join_to_the_dimension_keyed_by_the_id_they_name()
    {
        const string Unit = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";
        _platform.Add("dev:reference-data--UnitOfMeasure:m", Unit, new JsonObject { ["Code"] = "m" });
        _platform.Add("dev:reference-data--UnitOfMeasure:gAPI", Unit, new JsonObject { ["Code"] = "gAPI" });
        JsonObject Curve(string mnemonic, string unit, double? top = null)
        {
            var curve = new JsonObject { ["Mnemonic"] = mnemonic, ["CurveUnit"] = $"dev:reference-data--UnitOfMeasure:{unit}:" };
            if (top is { } depth)
            {
                curve["TopDepth"] = depth;
            }

            return curve;
        }

        _platform.Add("dev:work-product-component--WellLog:1", WellLog, new JsonObject
        {
            ["Name"] = "LOG-1", ["Curves"] = new JsonArray(Curve("MD", "m", 203.1), Curve("GR", "gAPI", 210)),
        });
        _platform.Add("dev:work-product-component--WellLog:2", WellLog, new JsonObject { ["Name"] = "LOG-2", ["Curves"] = new JsonArray(Curve("MD", "m")) });
        _platform.Add("dev:work-product-component--WellLog:3", WellLog, new JsonObject { ["Name"] = "LOG-3" });

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: TestCurve
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                label: data.Name
                columns: { key: WellLogID, value: WellLogName }
                elements:
                  path: data.Curves
                  fields:
                    Mnemonic: Mnemonic
                    CurveUnitID: { path: CurveUnit, keep: id }
                    TopDepth: TopDepth
              - name: TestUnit
                kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                path: id
                label: data.Code
                columns: { key: UnitID, value: UnitCode }

            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal((2, 0), (outcome.Built, outcome.Failed));

        // A row per object, numbered within its log, its fields side by side; a log holding none is one row with no element.
        Assert.Equal(
            [
                "LOG-1|1|MD|dev:reference-data--UnitOfMeasure:m|203.1",
                "LOG-1|2|GR|dev:reference-data--UnitOfMeasure:gAPI|210",
                "LOG-2|1|MD|dev:reference-data--UnitOfMeasure:m|(null)",
                "LOG-3|(null)|(null)|(null)|(null)",
            ],
            await SqlAsync("SELECT [WellLogName], [element], [Mnemonic], [CurveUnitID], [TopDepth] FROM [osdu].[dim_TestCurve] ORDER BY [WellLogName], [element];"));

        // A field kept as an id is the key of the dimension keyed by id, so the tables join with no normalising.
        Assert.Equal(
            ["LOG-1|GR|gAPI", "LOG-1|MD|m", "LOG-2|MD|m"],
            await SqlAsync("""
                SELECT c.[WellLogName], c.[Mnemonic], u.[UnitCode]
                FROM [osdu].[dim_TestCurve] AS c
                INNER JOIN [osdu].[dim_TestUnit] AS u ON u.[partition] = c.[partition] AND u.[UnitID] = c.[CurveUnitID]
                ORDER BY c.[WellLogName], c.[Mnemonic];
                """));

        // The table's page reads the fields as it reads attributes.
        var curves = (await ledger.FindDimensionAsync(flow.LedgerId, "TestCurve"))!;
        var page = await DimensionTable.ReadAsync(ledger, curves, new DimensionTableQuery(), CancellationToken.None);
        Assert.Equal(["Mnemonic", "CurveUnitID", "TopDepth"], page.Attributes);
        Assert.Equal(4L, page.Total);

        // A second build that finds one curve fewer drops its row and keeps the numbers of the others.
        var before = await SqlAsync("SELECT CONCAT([WellLogName], N'/', [element], N'=', [id]) FROM [osdu].[dim_TestCurve] WHERE [element] = 1 ORDER BY [WellLogName];");
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:1")["data"]!["Curves"] = new JsonArray(Curve("MD", "m", 203.1));
        await runner.BuildAsync(["TestCurve"], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(before, await SqlAsync("SELECT CONCAT([WellLogName], N'/', [element], N'=', [id]) FROM [osdu].[dim_TestCurve] WHERE [element] = 1 ORDER BY [WellLogName];"));
        Assert.Equal(
            ["LOG-1|1|MD", "LOG-2|1|MD", "LOG-3|(null)|(null)"],
            await SqlAsync("SELECT [WellLogName], [element], [Mnemonic] FROM [osdu].[dim_TestCurve] ORDER BY [WellLogName], [element];"));

        // The ledger keeps each field that holds a value, a row each: three of LOG-1's curve, two of LOG-2's.
        Assert.Equal(5, await ElementRowsAsync(curves.DimensionId));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    private async Task<int> ElementRowsAsync(int dimensionId)
        => int.Parse((await SqlAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"SELECT COUNT(*) FROM [osdu].[DimensionElement] WHERE [DimensionId] = {dimensionId};")))[0], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_dimension_is_one_table_whose_columns_follow_its_declaration_and_whose_rows_keep_their_numbers()
    {
        _platform.Add("dev:master-data--GeoPoliticalEntity:MX", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Mexico" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:CA", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Canada" });
        foreach (var (name, country) in new[] { ("A", "MX"), ("B", "MX"), ("C", "CA") })
        {
            _platform.Add($"dev:master-data--Wellbore:{name}", Wellbore, new JsonObject
            {
                ["FacilityName"] = $"{country} {name}",
                ["FacilityID"] = $"uuid-{name}",
                ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{country}:" }),
            });
        }

        foreach (var (log, wellbore, source) in new[] { ("1", "A", "WELLDB"), ("2", "A", "PETREL"), ("3", "A", "WELLDB"), ("4", "B", "WELLDB"), ("5", "C", "PETREL") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:", ["Source"] = source });
        }

        const string Declared = """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]

            """;
        const string Table = "dim_Wellbore";
        var (runner, ledger, flow) = await RunnerAsync(Head + Declared);

        // The first build makes the table: the dimension as one table, named after the dimension, keyed by an identity,
        // its rows kept by partition and key, with a column for each attribute it declares. Any SQL client reads it and
        // joins on its numbers.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(Table, wellbores.TableName);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "key_hash", "Country"], await ColumnsAsync(Table));
        Assert.Equal(
            [
                "IX_key|CLUSTERED|0|partition, key_id, id|1", "IX_key_hash|NONCLUSTERED|0|partition, key_hash|0",
                "IX_value|NONCLUSTERED|0|partition, FacilityName, id|1", "PK_" + Table + "|NONCLUSTERED|1|id|1",
            ],
            await IndexesAsync(Table));
        var keys = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10)))
            .ToDictionary(k => k.Original[^2..^1], StringComparer.Ordinal);
        Assert.Equal(
            [
                $"1|dev|{keys["C"].ValueId}|dev:master-data--Wellbore:C:|CA C|1|{keys["C"].Filter}|{KeyHash("dev:master-data--Wellbore:C:")}|Canada",
                $"2|dev|{keys["A"].ValueId}|dev:master-data--Wellbore:A:|MX A|3|{keys["A"].Filter}|{KeyHash("dev:master-data--Wellbore:A:")}|Mexico",
                $"3|dev|{keys["B"].ValueId}|dev:master-data--Wellbore:B:|MX B|1|{keys["B"].Filter}|{KeyHash("dev:master-data--Wellbore:B:")}|Mexico",
            ],
            await SqlAsync($"SELECT * FROM [osdu].[{Table}] ORDER BY [id];"));

        // A page of it, as the page and the CLI read it: searched, narrowed by an attribute, ordered by any column, counted.
        var page = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Limit: 2), CancellationToken.None);
        Assert.Equal(("osdu." + Table, 3L, true), (page.Table, page.Total, page.More));
        Assert.Equal(["Country"], page.Attributes);
        Assert.Equal([(1L, "CA C"), (2L, "MX A")], page.Rows.Select(r => (r.Id, r.Value)));
        var next = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Offset: 2, Limit: 2), CancellationToken.None);
        Assert.Equal((null, false, "MX B"), (next.Total, next.More, Assert.Single(next.Rows).Value));
        var byRecords = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "Records", Descending: true), CancellationToken.None);
        Assert.Equal([("MX A", 3L), ("CA C", 1L), ("MX B", 1L)], byRecords.Rows.Select(r => (r.Value, r.Records)));
        Assert.False(byRecords.More);
        var mexico = await DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("country", ["Mexico"])], OrderBy: "country"), CancellationToken.None);
        Assert.Equal(2L, mexico.Total);
        Assert.Equal(["MX A", "MX B"], mexico.Rows.Select(r => r.Value));
        Assert.Equal(("dev:master-data--Wellbore:A:", keys["A"].ValueId, keys["A"].Filter), (mexico.Rows[0].Key, mexico.Rows[0].KeyId, mexico.Rows[0].Filter));
        Assert.Equal(["Mexico"], mexico.Rows[0].Attributes);
        Assert.Empty((await DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("Country", ["mexico"])]), CancellationToken.None)).Rows);
        var searched = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "cana"), CancellationToken.None);
        Assert.Equal("CA C", Assert.Single(searched.Rows).Value);
        Assert.Equal(1L, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "wellbore:b"), CancellationToken.None)).Total);
        Assert.Empty((await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "%"), CancellationToken.None)).Rows);
        var noColumn = await Assert.ThrowsAsync<DeliveryException>(() => DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "Basin"), CancellationToken.None));
        Assert.Contains("has no column 'Basin' to order by; it has FacilityName, WellboreID, records, id, Country", noColumn.Message, StringComparison.Ordinal);
        var noAttribute = await Assert.ThrowsAsync<DeliveryException>(() => DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("Basin", ["X"])]), CancellationToken.None));
        Assert.Contains("has no attribute column 'Basin'; it has Country", noAttribute.Message, StringComparison.Ordinal);

        // A build that finds the same thing writes no row of the table: each keeps its number, and nothing was touched.
        var before = await SqlAsync($"SELECT [id], %%physloc%% FROM [osdu].[{Table}] ORDER BY [id];");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(before, await SqlAsync($"SELECT [id], %%physloc%% FROM [osdu].[{Table}] ORDER BY [id];"));
        Assert.False(await DimensionTable.EnsureAsync(ledger, wellbores, CancellationToken.None));

        // The flow declares more: the table gains a column for each new attribute on the next build, with no migration.
        // The attribute it collects makes a row for each value a key holds, so the rows are written again.
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + """
                  UUID: data.FacilityID
                  Source: { collect: data.Source }
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "key_hash", "Country", "UUID", "Source"], await ColumnsAsync(Table));
        Assert.Equal(
            ["CA C|Canada|uuid-C|PETREL|1", "MX A|Mexico|uuid-A|PETREL|1", "MX A|Mexico|uuid-A|WELLDB|2", "MX B|Mexico|uuid-B|WELLDB|1"],
            await SqlAsync($"SELECT [FacilityName], [Country], [UUID], [Source], [records] FROM [osdu].[{Table}] WHERE [partition] = N'dev' ORDER BY [FacilityName], [Source];"));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var welldb = await DimensionTable.ReadAsync(
            ledger, wellbores, new DimensionTableQuery(Attributes: [new DimensionAttributeMatch("Source", ["WELLDB"])], OrderBy: "records", Descending: true), CancellationToken.None);
        Assert.Equal(["Country", "UUID", "Source"], welldb.Attributes);
        Assert.Equal([("MX A", 2L), ("MX B", 1L)], welldb.Rows.Select(r => (r.Value, r.Records)));

        // What changed is rewritten where it is: a log that moves to another source changes two rows and no number.
        var ids = await SqlAsync($"SELECT [id] FROM [osdu].[{Table}] WHERE [FacilityName] = N'MX B' OR [FacilityName] = N'CA C' ORDER BY [id];");
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:2")["data"]!["Source"] = "WELLDB";
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(
            ["CA C|PETREL|1", "MX A|WELLDB|3", "MX B|WELLDB|1"],
            await SqlAsync($"SELECT [FacilityName], [Source], [records] FROM [osdu].[{Table}] ORDER BY [FacilityName], [Source];"));
        Assert.Equal(ids, await SqlAsync($"SELECT [id] FROM [osdu].[{Table}] WHERE [FacilityName] = N'MX B' OR [FacilityName] = N'CA C' ORDER BY [id];"));

        // An attribute the flow stops declaring keeps its column, emptied, so a query naming it still runs; a column
        // somebody added to the table is theirs and is left as it is. The page shows what the dimension declares.
        await SqlAsync($"ALTER TABLE [osdu].[{Table}] ADD [Notes] nvarchar(100) NULL;");
        await SqlAsync($"UPDATE [osdu].[{Table}] SET [Notes] = N'mine';");
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + """
                  Source: { collect: data.Source }
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "key_hash", "Country", "UUID", "Source", "Notes"], await ColumnsAsync(Table));
        Assert.Equal(["CA C|(null)|PETREL|mine", "MX A|(null)|WELLDB|mine", "MX B|(null)|WELLDB|mine"], await SqlAsync($"SELECT [FacilityName], [UUID], [Source], [Notes] FROM [osdu].[{Table}] ORDER BY [FacilityName];"));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(["Country", "Source"], (await DimensionTable.ShapeAsync(ledger, wellbores, CancellationToken.None)).Attributes);

        // A table the dimension wrote under an earlier name is left behind by no build: the next one writes the table
        // under its name and drops the other.
        await SqlAsync($"""
            EXEC sys.sp_rename N'osdu.{Table}', N'dim_wells_dimensions_Wellbore';
            EXEC sys.sp_rename N'osdu.PK_{Table}', N'PK_dim_wells_dimensions_Wellbore', N'OBJECT';
            UPDATE [osdu].[Dimension] SET [TableName] = N'dim_wells_dimensions_Wellbore';
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Empty(await ColumnsAsync("dim_wells_dimensions_Wellbore"));
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "key_hash", "Country", "Source"], await ColumnsAsync(Table));
        Assert.Equal(["CA C|PETREL", "MX A|WELLDB", "MX B|WELLDB"], await SqlAsync($"SELECT [FacilityName], [Source] FROM [osdu].[{Table}] ORDER BY [FacilityName];"));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(Table, wellbores.TableName);

        // A table dropped by hand is made again, with its rows, by whoever next reads it; so is the table of a dimension
        // built before dimensions had one, which names none.
        await SqlAsync($"DROP TABLE [osdu].[{Table}];");
        Assert.Equal(3, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None)).Total);
        Assert.Equal(["id", "partition", "key_id", "WellboreID", "FacilityName", "records", "filter", "key_hash", "Country", "Source"], await ColumnsAsync(Table));
        await SqlAsync($"DROP TABLE [osdu].[{Table}]; UPDATE [osdu].[Dimension] SET [TableName] = NULL;");
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Null(wellbores.TableName);
        var exported = new StringWriter();
        Assert.Equal(3, await DimensionExport.WriteAsync(ledger, wellbores, DimensionExportSet.Table, DimensionExportFormat.Csv, exported, CancellationToken.None));
        Assert.Equal(Table, (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!.TableName);

        // A key no build finds any more is no row of it.
        _platform.Records.RemoveAll(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:5");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["MX A", "MX B"], await SqlAsync($"SELECT [FacilityName] FROM [osdu].[{Table}] ORDER BY [FacilityName];"));

        // Removing the dimension drops its table with it.
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var removed = await DimensionRemoval.RemoveAsync(ledger, wellbores, "admin@example.test", _clock, CancellationToken.None);
        Assert.Equal(Table, removed.TableDropped);
        Assert.Contains("dropped the table osdu." + Table, removed.Describe(), StringComparison.Ordinal);
        Assert.Empty(await ColumnsAsync(Table));
        Assert.Empty(await SqlAsync("SELECT [Name] FROM [osdu].[DimensionAttributeName];"));
    }

    [Fact]
    public async Task A_table_made_before_its_rows_were_kept_by_partition_is_laid_out_again_keeping_every_row_and_its_number()
    {
        foreach (var (wellbore, name) in new[] { ("A", "MX A"), ("B", "MX B") })
        {
            _platform.Add($"dev:master-data--Wellbore:{wellbore}", Wellbore, new JsonObject { ["FacilityName"] = name });
        }

        foreach (var (log, wellbore) in new[] { ("1", "A"), ("2", "B") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" });
        }

        const string Table = "dim_Wellbore";
        var (runner, _, _) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName

            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var rows = await SqlAsync($"SELECT [id], [key_id], [FacilityName] FROM [osdu].[{Table}] ORDER BY [id];");
        Assert.Equal(2, rows.Count);

        // The table as a build made it before its rows were kept by partition: clustered on its id, a key's rows found
        // through an index of their own.
        await SqlAsync($"""
            DROP INDEX [IX_key_hash] ON [osdu].[{Table}];
            DROP INDEX [IX_value] ON [osdu].[{Table}];
            ALTER TABLE [osdu].[{Table}] DROP CONSTRAINT [PK_{Table}];
            DROP INDEX [IX_key] ON [osdu].[{Table}];
            ALTER TABLE [osdu].[{Table}] ADD CONSTRAINT [PK_{Table}] PRIMARY KEY CLUSTERED ([id]);
            CREATE INDEX [IX_key] ON [osdu].[{Table}] ([partition], [key_id]);
            CREATE INDEX [IX_value] ON [osdu].[{Table}] ([partition], [FacilityName], [id]);
            CREATE INDEX [IX_key_hash] ON [osdu].[{Table}] ([partition], [key_hash]);
            """);
        Assert.Equal(
            [
                "PK_" + Table + "|CLUSTERED|1|id|1", "IX_key|NONCLUSTERED|0|partition, key_id|0", "IX_key_hash|NONCLUSTERED|0|partition, key_hash|0",
                "IX_value|NONCLUSTERED|0|partition, FacilityName, id|1",
            ],
            await IndexesAsync(Table));

        // The next build lays it out by partition, once, and writes what it found into the same rows under the same numbers.
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:master-data--Wellbore:B")["data"]!["FacilityName"] = "MX B2";
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(
            [
                "IX_key|CLUSTERED|0|partition, key_id, id|1", "IX_key_hash|NONCLUSTERED|0|partition, key_hash|0",
                "IX_value|NONCLUSTERED|0|partition, FacilityName, id|1", "PK_" + Table + "|NONCLUSTERED|1|id|1",
            ],
            await IndexesAsync(Table));
        Assert.Equal([rows[0], rows[1].Replace("|MX B", "|MX B2", StringComparison.Ordinal)], await SqlAsync($"SELECT [id], [key_id], [FacilityName] FROM [osdu].[{Table}] ORDER BY [id];"));

        // And a build that finds it laid out leaves it as it is.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal("IX_key|CLUSTERED|0|partition, key_id, id|1", (await IndexesAsync(Table))[0]);
    }

    [Fact]
    public async Task The_key_and_value_columns_are_renamed_where_they_are_when_the_dimension_names_them_otherwise()
    {
        foreach (var name in new[] { "A", "B" })
        {
            _platform.Add($"dev:master-data--Wellbore:{name}", Wellbore, new JsonObject { ["FacilityName"] = $"MX {name}" });
            _platform.Add($"dev:work-product-component--WellLog:{name}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{name}:" });
        }

        const string Declared = """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName

            """;
        const string Table = "dim_Wellbore";
        const string ValueIndex = $"""
            SELECT STRING_AGG(c.[name], ', ') WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
            JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(N'[osdu].[{Table}]') AND i.[name] = N'IX_value';
            """;
        string Rows(string key, string value) => $"SELECT [id], [key_id], [{key}], [{value}], [records] FROM [osdu].[{Table}] ORDER BY [id];";
        string[] Columns(string key, string value, params string[] more) => ["id", "partition", "key_id", key, value, "records", "filter", "key_hash", .. more];

        // The columns are named after what the dimension reads: the property its path ends with, and its label's.
        var (runner, ledger, flow) = await RunnerAsync(Head + Declared);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(("WellboreID", "FacilityName"), (wellbores.KeyColumn, wellbores.ValueColumn));
        var rows = await SqlAsync(Rows("WellboreID", "FacilityName"));
        Assert.Equal(2, rows.Count);

        // A table made before dimensions named their columns holds them as key and value, and is read under those names.
        await SqlAsync($"""
            EXEC sys.sp_rename N'[osdu].[{Table}].[WellboreID]', N'key', N'COLUMN';
            EXEC sys.sp_rename N'[osdu].[{Table}].[FacilityName]', N'value', N'COLUMN';
            UPDATE [osdu].[Dimension] SET [KeyColumn] = N'key', [ValueColumn] = N'value';
            """);
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var before = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None);
        Assert.Equal(("key", "value", 2L), (before.KeyColumn, before.ValueColumn, before.Total));

        // Its next build renames the two where they are: every row keeps its number, and the index over the value follows.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("WellboreID", "FacilityName"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("WellboreID", "FacilityName")));
        Assert.Equal(["partition, FacilityName, id"], await SqlAsync(ValueIndex));

        // No row names them, as for a table an earlier version made and a build then failed on: they are told by their names.
        await SqlAsync($"""
            EXEC sys.sp_rename N'[osdu].[{Table}].[WellboreID]', N'key', N'COLUMN';
            EXEC sys.sp_rename N'[osdu].[{Table}].[FacilityName]', N'value', N'COLUMN';
            UPDATE [osdu].[Dimension] SET [KeyColumn] = NULL, [ValueColumn] = NULL;
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("WellboreID", "FacilityName"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("WellboreID", "FacilityName")));

        // The document names them: a name that changes only in case is a rename too, and the rows stay.
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + "    columns: { key: WellboreId, value: Wellbore }\n");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("WellboreId", "Wellbore"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("WellboreId", "Wellbore")));
        Assert.Equal(["partition, Wellbore, id"], await SqlAsync(ValueIndex));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(("WellboreId", "Wellbore"), (wellbores.KeyColumn, wellbores.ValueColumn));

        // A page says what the two columns are named, and is ordered by a column's name or by the words value and key.
        var byName = await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "wellbore", Descending: true), CancellationToken.None);
        Assert.Equal(("WellboreId", "Wellbore"), (byName.KeyColumn, byName.ValueColumn));
        Assert.Equal(["MX B", "MX A"], byName.Rows.Select(r => r.Value));
        Assert.Equal(["MX A", "MX B"], (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "value"), CancellationToken.None)).Rows.Select(r => r.Value));
        Assert.Equal(
            ["dev:master-data--Wellbore:B:", "dev:master-data--Wellbore:A:"],
            (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "key", Descending: true), CancellationToken.None)).Rows.Select(r => r.Key));
        Assert.Equal("MX B", Assert.Single((await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(Search: "wellbore:b"), CancellationToken.None)).Rows).Value);
        var shape = await DimensionTable.ShapeAsync(ledger, wellbores, CancellationToken.None);
        Assert.Equal(("WellboreId", "Wellbore"), (shape.KeyColumn, shape.ValueColumn));
        var noColumn = await Assert.ThrowsAsync<DeliveryException>(() => DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(OrderBy: "FacilityName"), CancellationToken.None));
        Assert.Contains("has no column 'FacilityName' to order by; it has Wellbore, WellboreId, records, id", noColumn.Message, StringComparison.Ordinal);

        // Each takes the other's name: neither name is free while the other column holds it, so both pass through another.
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + "    columns: { key: Wellbore, value: WellboreId }\n");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(Columns("Wellbore", "WellboreId"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("Wellbore", "WellboreId")));
        Assert.Equal(["partition, WellboreId, id"], await SqlAsync(ValueIndex));

        // A name another column of the table holds is not taken from it: the build fails saying so, and nothing is renamed.
        await SqlAsync($"ALTER TABLE [osdu].[{Table}] ADD [Name] nvarchar(10) NULL;");
        (runner, ledger, flow) = await RunnerAsync(Head + Declared + "    columns: { value: Name }\n");
        var taken = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains(
            "The dimension's table osdu.dim_Wellbore has a column Name already (an attribute the dimension declared before, or a column added by hand), so its value column WellboreId cannot be renamed to it, and nothing was written. Drop that column, or name the value's column otherwise in the flow: columns: { value: <name> }.",
            taken.Message,
            StringComparison.Ordinal);
        Assert.Equal(Columns("Wellbore", "WellboreId", "Name"), await ColumnsAsync(Table));
        Assert.Equal(rows, await SqlAsync(Rows("Wellbore", "WellboreId")));

        // A reader that finds the table gone makes it again under the names it had: only a build reads the document.
        await SqlAsync($"DROP TABLE [osdu].[{Table}];");
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(2, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None)).Total);
        Assert.Equal(Columns("Wellbore", "WellboreId"), await ColumnsAsync(Table));

        // And a dimension that never had a table takes the names it reads by.
        await SqlAsync($"DROP TABLE [osdu].[{Table}]; UPDATE [osdu].[Dimension] SET [TableName] = NULL, [KeyColumn] = NULL, [ValueColumn] = NULL;");
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(2, (await DimensionTable.ReadAsync(ledger, wellbores, new DimensionTableQuery(), CancellationToken.None)).Total);
        Assert.Equal(Columns("WellboreID", "FacilityName"), await ColumnsAsync(Table));
        wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal((Table, "WellboreID", "FacilityName"), (wellbores.TableName, wellbores.KeyColumn, wellbores.ValueColumn));
    }

    [Fact]
    public async Task A_dimension_another_flow_declares_by_the_same_name_is_refused_and_the_first_ones_table_left_alone()
    {
        _platform.Add("dev:work-product-component--WellLog:1", WellLog, new JsonObject { ["Source"] = "WELLDB" });
        const string Declared = """
            dimensions:
              - name: Source
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Source
            """;
        var (runner, ledger, flow) = await RunnerAsync(Head + Declared);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // The dimension is named after the property it reads: its value's column keeps the name, and its key's takes Key.
        Assert.Equal(["id", "partition", "key_id", "SourceKey", "Source", "records", "filter", "key_hash"], await ColumnsAsync("dim_Source"));

        // A table is named after its dimension alone, so a dimension's name is unique among flows: another flow declaring
        // one of the same name would write the first one's table.
        var (other, _, _) = await RunnerAsync(Head.Replace("name: wells-dimensions", "name: log-dimensions", StringComparison.Ordinal) + Declared, "flows/other.yaml");
        var refused = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => other.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains(
            "Dimension Source of log-dimensions would write the table osdu.dim_Source, which dimension Source of wells-dimensions writes: a dimension's table is named after the dimension alone, so a dimension's name is unique among the flows of a database. Rename one of them.",
            refused.Message,
            StringComparison.Ordinal);
        var failed = (await ledger.ListDimensionsAsync(null, null)).Single(d => d.FlowName == "log-dimensions");
        Assert.Equal((DimensionRunStatus.Failed, (string?)null), ((await ledger.ListDimensionRunsAsync(failed.DimensionId, 1)).Single().Status, failed.TableName));

        // The first flow's table is untouched, and the refused dimension goes without taking it.
        var removed = await DimensionRemoval.RemoveAsync(ledger, failed, "admin@example.test", _clock, CancellationToken.None);
        Assert.Null(removed.TableDropped);
        Assert.Equal(["dev|WELLDB|WELLDB|1"], await SqlAsync("SELECT [partition], [SourceKey], [Source], [records] FROM [osdu].[dim_Source];"));
        Assert.Equal("dim_Source", (await ledger.FindDimensionAsync(flow.LedgerId, "Source"))!.TableName);
    }

    [Fact]
    public void A_table_is_named_after_its_dimension_alone_in_a_name_that_needs_no_quoting()
    {
        Assert.Equal("dim_Wellbore", DimensionTables.NameOf("Wellbore"));
        Assert.Equal("dim_Well_Type", DimensionTables.NameOf("Well-Type"));
        Assert.Equal(DimensionTables.NameOf("Well.Type"), DimensionTables.NameOf("Well-Type"));
        Assert.Equal("osdu.dim_Wellbore", DimensionTables.Shown(DimensionTables.NameOf("Wellbore")));
        Assert.Equal("[osdu].[dim_Wellbore]", DimensionTables.Qualified(DimensionTables.NameOf("Wellbore")));

        // The longest name a dimension may have still names a table SQL Server takes, with room for its key's name.
        Assert.Equal(104, DimensionTables.NameOf(new string('d', 100)).Length);

        var table = DimensionTables.Of("D", "Code", "Name", [new DimensionAttributeSpec("Country", ["data.A"]), new DimensionAttributeSpec("Source", [], "data.Source")]);
        Assert.Equal(("dim_D", "Code", "Name"), (table.Name, table.KeyColumn, table.ValueColumn));
        Assert.Equal([("Country", false), ("Source", true)], table.Columns.Select(c => (c.Name, c.Collected)));
        var tooMany = Assert.Throws<DeliveryException>(() => DimensionTables.Of(
            "D", "Code", "Name", Enumerable.Range(0, DimensionSpec.MaxAttributes + 1).Select(i => new DimensionAttributeSpec($"A{i}", ["data.A"])).ToList()));
        Assert.Contains($"Dimension D declares {DimensionSpec.MaxAttributes + 1} attributes, and a dimension's table holds {DimensionSpec.MaxAttributes}", tooMany.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Few_values_over_many_logs_are_collected_a_read_per_value_and_count_as_one_pass_would()
    {
        // 3,600 logs of four wellbores, two sources and logs without one: three reads where one pass would take four pages.
        for (var log = 0; log < 3600; log++)
        {
            var data = new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 4}:" };
            if (log % 3 != 0)
            {
                data["Source"] = log % 3 == 1 ? "WELLDB" : "PETREL";
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, data);
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: Not specified
                attributes:
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        Assert.DoesNotContain(_platform.Calls, c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 10));
        Assert.Equal(4, keys.Count);
        Assert.All(keys, k => Assert.Equal(
            [("Not specified", (long?)300L), ("PETREL", 300L), ("WELLDB", 300L)],
            k.Attributes.Select(a => (a.Value, a.Records)).OrderBy(a => a.Value, StringComparer.Ordinal)));
        Assert.Equal(
            [("PETREL", 1200L), ("WELLDB", 1200L)],
            (await ledger.ListDimensionCollectedTextsAsync(wellbores.DimensionId, "Source", null, 10)).Select(v => (v.Value, v.Records)));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task Many_values_are_collected_in_one_pass_and_a_search_says_when_none_of_them_is_more_than_it_can_exclude()
    {
        // 1,100 sources over three wellbores, one log each, and two logs without a source.
        for (var log = 0; log < 1102; log++)
        {
            var data = new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 3}:" };
            if (log < 1100)
            {
                data["Source"] = $"S{log:D4}";
            }

            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, data);
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: Not specified
                attributes:
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(1100, (await ledger.ListDimensionCollectedTextsAsync(wellbores.DimensionId, "Source", null, 2000)).Count);
        // W2 holds every third source, and the log of 1,100 without one.
        var w2 = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery("W2", null, false, false, null, 10))).Single();
        Assert.Equal(366, w2.Attributes.Count(a => a is { Name: "Source", Records: 1 } && a.Value != "Not specified"));
        Assert.Contains(w2.Attributes, a => a is { Name: "Source", Value: "Not specified", Records: 1 });
        Assert.Equal(
            [("S0001", 1, 1L)],
            (await ledger.ListDimensionAttributeValuesAsync(wellbores.DimensionId, new DimensionAttributeValueQuery("Source", "S0001", 10))).Select(v => (v.Value, v.Keys, v.Records)));

        // A source picks its log; Not specified would have to exclude every one of 1,100 sources, which no search holds.
        var one = await DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("Source", ["S0001"])])], null, null, CancellationToken.None);
        Assert.Equal(["dev:work-product-component--WellLog:1"], _platform.Find(WellLog, one.Query));
        var none = await Assert.ThrowsAsync<DeliveryException>(() => DimensionSearch.ComposeAsync(
            ledger, [new DimensionPick(wellbores, [], [], [new DimensionAttributeMatch("Source", ["Not specified"])])], null, null, CancellationToken.None));
        Assert.Contains("more than the 1000 one search can exclude", none.Message, StringComparison.Ordinal);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_pass_over_many_records_is_read_in_ranges_of_keys_side_by_side_and_counts_every_record_once()
    {
        // 9,000 logs over 300 wellbores and 600 sources: more sources than a read per value is made for, so the logs are
        // passed over, and enough of them for two ranges of keys, each read through a cursor of its own.
        var expected = new Dictionary<(string Key, string Source), long>();
        for (var log = 0; log < 9_000; log++)
        {
            var wellbore = $"dev:master-data--Wellbore:W{log % 300:D3}:";
            var source = $"S{(log * 7) % 600:D3}";
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = wellbore, ["Source"] = source });
            expected[(wellbore, source)] = expected.GetValueOrDefault((wellbore, source)) + 1;
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000));
        Assert.Equal(300, keys.Count);
        Assert.Equal(
            expected.OrderBy(e => e.Key.Key, StringComparer.Ordinal).ThenBy(e => e.Key.Source, StringComparer.Ordinal).Select(e => (e.Key.Key, e.Key.Source, (long?)e.Value)).ToList(),
            keys.SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records)))
                .OrderBy(e => e.Original, StringComparer.Ordinal).ThenBy(e => e.Value, StringComparer.Ordinal).ToList());
        Assert.Equal(9_000L, keys.Sum(k => k.Attributes.Sum(a => a.Records ?? 0)));

        // The pass opened two cursors, each over a range of the wellbores' ids and each returning the key and the source,
        // and between them they read every log once.
        var opened = _platform.Calls
            .Where(c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal) && c.Body is not null)
            .Select(c => JsonNode.Parse(c.Body!)!.AsObject())
            .Where(body => body["cursor"] is null
                && body["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()).Intersect(["data.WellboreID", "data.Source"]).Count() == 2)
            .Select(body => body["query"]!.GetValue<string>())
            .ToList();
        Assert.Equal(2, opened.Count);
        Assert.All(opened, query => Assert.Contains("data.WellboreID.keyword:[", query, StringComparison.Ordinal));
        Assert.NotEqual(opened[0], opened[1]);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_page_lost_during_the_pass_is_read_again_and_every_record_is_still_counted_once()
    {
        // The same pass over 9,000 logs in two ranges, with the answer to one range's second page lost after its cursor moved
        // on: asked again with that cursor, the service would answer with the page after it, and the thousand logs of the
        // lost page would be counted nowhere. The range is read again from its first page, and every log counts once.
        var expected = new Dictionary<(string Key, string Source), long>();
        for (var log = 0; log < 9_000; log++)
        {
            var wellbore = $"dev:master-data--Wellbore:W{log % 300:D3}:";
            var source = $"S{(log * 7) % 600:D3}";
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = wellbore, ["Source"] = source });
            expected[(wellbore, source)] = expected.GetValueOrDefault((wellbore, source)) + 1;
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  Source: { collect: data.Source }
            """);
        var lost = 0;
        _platform.Lose = body => IsPassPage(body, continuing: true) && Interlocked.CompareExchange(ref lost, 1, 0) == 0;

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000));
        Assert.Equal(
            expected.OrderBy(e => e.Key.Key, StringComparer.Ordinal).ThenBy(e => e.Key.Source, StringComparer.Ordinal).Select(e => (e.Key.Key, e.Key.Source, (long?)e.Value)).ToList(),
            keys.SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records)))
                .OrderBy(e => e.Original, StringComparer.Ordinal).ThenBy(e => e.Value, StringComparer.Ordinal).ToList());
        Assert.Equal(9_000L, keys.Sum(k => k.Attributes.Sum(a => a.Records ?? 0)));

        // Two ranges and the range read again: three first pages, and the lost page's cursor was never asked again.
        var pages = _platform.Calls.Where(c => IsPassPage(c.Body, continuing: null)).Select(c => JsonNode.Parse(c.Body!)!.AsObject()).ToList();
        Assert.Equal(3, pages.Count(p => p["cursor"] is null));
        Assert.Equal(1, lost);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_pass_that_cannot_be_read_whole_fails_the_build_and_the_dimension_keeps_the_build_it_had()
    {
        for (var log = 0; log < 9_000; log++)
        {
            _platform.Add(
                $"dev:work-product-component--WellLog:{log}", WellLog,
                new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 300:D3}:", ["Source"] = $"S{(log * 7) % 600:D3}" });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  Source: { collect: data.Source }
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var built = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var before = (await ledger.ListDimensionValuesAsync(built.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000)))
            .SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records))).ToList();

        // Every page after the first of the pass is lost: each range's read and its second read both fail.
        _platform.Lose = body => IsPassPage(body, continuing: true);
        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Contains("could not be read whole in 2 reads", failed.Outcome.Dimensions[0].Error, StringComparison.Ordinal);
        var kept = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal(built.Members, kept.Members);
        Assert.Equal(
            before,
            (await ledger.ListDimensionValuesAsync(kept.DimensionId, new DimensionValueQuery(null, null, false, false, null, 1000)))
                .SelectMany(k => k.Attributes.Select(a => (k.Original, a.Value, a.Records))).ToList());
        var runs = await ledger.ListDimensionRunsAsync(kept.DimensionId, 2);
        Assert.Equal([DimensionRunStatus.Failed, DimensionRunStatus.Completed], runs.Select(r => r.Status));
    }

    /// <summary>
    /// Whether a request body is a page of the pass over the logs (a cursor search returning the key and the source), and,
    /// when <paramref name="continuing"/> says, one asked with a cursor or without one.
    /// </summary>
    private static bool IsPassPage(string? body, bool? continuing)
    {
        if (string.IsNullOrEmpty(body) || JsonNode.Parse(body) is not JsonObject request || request["returnedFields"] is not JsonArray fields)
        {
            return false;
        }

        var returned = fields.Select(f => f!.GetValue<string>()).ToList();
        return returned.Contains("data.WellboreID") && returned.Contains("data.Source")
            && (continuing is null || continuing == (request["cursor"] is not null));
    }

    [Fact]
    public async Task A_build_asks_the_platform_several_things_at_a_time_and_never_more_than_the_flow_allows()
    {
        // Wellbores with a name and a country, and logs of many sources: keys read in ranges, labels a thousand ids a
        // search, and a pass over the logs, each of which the build asks side by side.
        _platform.Add("dev:master-data--GeoPoliticalEntity:MX", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = "Mexico" });
        for (var wellbore = 0; wellbore < 1_200; wellbore++)
        {
            _platform.Add($"dev:master-data--Wellbore:W{wellbore:D4}", Wellbore, new JsonObject
            {
                ["FacilityName"] = $"MX {wellbore:D4}",
                ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = "dev:master-data--GeoPoliticalEntity:MX:" }),
            });
        }

        for (var log = 0; log < 2_400; log++)
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject
            {
                ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 1_200:D4}:", ["Source"] = $"S{log % 600:D3}",
            });
        }

        _platform.Latency = TimeSpan.FromMilliseconds(3);
        var (runner, ledger, flow) = await RunnerAsync(Head.Replace("recordsAtOnce: 2", "recordsAtOnce: 6", StringComparison.Ordinal) + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]
                  Source: { collect: data.Source }
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((1, 0), (outcome.Built, outcome.Failed));
        Assert.InRange(_platform.MostAtOnce, 3, 6);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal((1_200L, 1_200L), (wellbores.Members, wellbores.Originals));
        var first = (await ledger.ListDimensionValuesAsync(wellbores.DimensionId, new DimensionValueQuery("W0000", null, false, false, null, 1))).Single();
        Assert.Equal("MX 0000", first.MemberValue);
        Assert.Equal([("Country", "Mexico", null), ("Source", "S000", 2L)], first.Attributes.Select(a => (a.Name, a.Value, a.Records)));
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public void A_pass_is_cut_into_ranges_of_keys_holding_about_as_many_records_each()
    {
        var field = OsduField.Text("data.WellboreID");
        var order = DimensionValueText.Order(field.Index);

        // Few records are one range, the whole of them, whatever the readers.
        Assert.Equal([DistinctSlice.Whole], DimensionCollector.Ranges(field, new Dictionary<string, long> { ["a"] = 1_000, ["b"] = 2_000 }, order, 8));

        // 100 keys of 1,000 records and 8 readers: 25 ranges (no range under 4,000 records), meeting end to end, open at both ends.
        var keys = Enumerable.Range(0, 100).ToDictionary(i => $"K{i:D3}", _ => 1_000L);
        var ranges = DimensionCollector.Ranges(field, keys, order, 8);
        Assert.Equal(25, ranges.Count);
        Assert.Null(ranges[0].From);
        Assert.Null(ranges[^1].To);
        Assert.All(Enumerable.Range(1, ranges.Count - 1), i => Assert.Equal(ranges[i - 1].To, ranges[i].From));
        Assert.All(keys.Keys, key => Assert.Single(ranges, r => r.Contains(key, order)));
        Assert.All(ranges, r => Assert.Equal(4_000L, keys.Where(k => r.Contains(k.Key, order)).Sum(k => k.Value)));

        // One key holding most of the records takes a range of its own; the rest still fall in exactly one.
        var skewed = Enumerable.Range(0, 20).ToDictionary(i => $"K{i:D3}", i => i == 10 ? 50_000L : 500L);
        var uneven = DimensionCollector.Ranges(field, skewed, order, 2);
        Assert.InRange(uneven.Count, 2, 8);
        Assert.All(skewed.Keys, key => Assert.Single(uneven, r => r.Contains(key, order)));

        // One reader still reads in ranges, in turn; fewer readers ask for fewer ranges.
        Assert.Equal(4, DimensionCollector.Ranges(field, keys, order, 1).Count);
    }

    [Fact]
    public async Task A_dimension_no_longer_declared_is_removed_with_everything_kept_of_it_and_nothing_else()
    {
        foreach (var (log, wellbore, source) in new[] { ("1", "A", "WELLDB"), ("2", "A", "PETREL"), ("3", "B", "WELLDB") })
        {
            _platform.Add($"dev:work-product-component--WellLog:{log}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:", ["Source"] = source });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                unlabelled: Not specified
                attributes:
                  Source: { collect: data.Source }
              - name: Source
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Source
            """);

        // Two builds, the second finding a new wellbore, so the dimension has builds, keys, values, attributes, collected
        // texts and a change log to lose.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        _platform.Add("dev:work-product-component--WellLog:4", WellLog, new JsonObject { ["WellboreID"] = "dev:master-data--Wellbore:C:", ["Source"] = "WELLDB" });
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var wellbores = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var sources = (await ledger.FindDimensionAsync(flow.LedgerId, "Source"))!;
        string[] tables = ["DimensionRun", "DimensionMember", "DimensionValue", "DimensionAttribute", "DimensionCollectedText", "DimensionChange", "Dimension"];
        async Task<long[]> RowsAsync(int dimensionId)
        {
            await using var connection = new SqlConnection(_db.ConnectionString);
            await connection.OpenAsync();
            var counts = new long[tables.Length];
            for (var i = 0; i < tables.Length; i++)
            {
                await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM [osdu].[{tables[i]}] WHERE [DimensionId] = @d", connection);
                command.Parameters.AddWithValue("@d", dimensionId);
                counts[i] = (long)(await command.ExecuteScalarAsync())!;
            }

            return counts;
        }

        var before = await RowsAsync(wellbores.DimensionId);
        var others = await RowsAsync(sources.DimensionId);
        Assert.Equal([2L, 3L, 3L, 4L, 2L, 1L, 1L], before);

        // A cache flow capturing the dimension keeps it: its refresh would read a dimension that is gone.
        await using (var db = _db.CreateDbContext())
        {
            db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(), RepoId = Guid.NewGuid(), FlowName = "wells-cache", Scope = "dev", Origin = "dimension", SourceObject = $"{flow.Name}/Wellbore",
                RelativePath = "cache/wells-cache.yaml", Name = "Wellbores", EntityType = "lookup--Wellbores", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var captured = await Assert.ThrowsAsync<DeliveryException>(() => DimensionRemoval.RemoveAsync(ledger, wellbores, "user:admin", _clock, CancellationToken.None));
        Assert.Contains("captured by type Wellbores of cache flow wells-cache in partition dev", captured.Message, StringComparison.Ordinal);
        Assert.Equal(before, await RowsAsync(wellbores.DimensionId));
        await using (var db = _db.CreateDbContext())
        {
            db.DeliveryCacheDefinitions.RemoveRange(db.DeliveryCacheDefinitions.Where(c => c.FlowName == "wells-cache"));
            await db.SaveChangesAsync();
        }

        // Removed: every row of it, and nothing of the other dimension.
        var removed = await DimensionRemoval.RemoveAsync(ledger, wellbores, "user:admin", _clock, CancellationToken.None);
        Assert.Equal(
            (wellbores.DimensionId, "Wellbore", before[1], before[2], before[0], before[5], before[3], before[4]),
            (removed.DimensionId, removed.Name, removed.Values, removed.Keys, removed.Builds, removed.Changes, removed.Attributes, removed.Texts));
        Assert.All(await RowsAsync(wellbores.DimensionId), count => Assert.Equal(0L, count));
        Assert.Equal(others, await RowsAsync(sources.DimensionId));
        Assert.Null(await ledger.GetDimensionAsync(wellbores.DimensionId));

        // The removal is an activity of the flow, with who removed it and what went; one that finds nothing to remove fails.
        var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = flow.LedgerId, Kind = DimensionRemoval.ActivityKind }));
        Assert.Equal(("user:admin", "completed"), (activity.Actor, activity.Outcome));
        Assert.StartsWith("Removed dimension Wellbore of wells-dimensions in dev: 3 value(s), 3 key(s)", activity.Summary, StringComparison.Ordinal);
        var twice = await Assert.ThrowsAsync<DeliveryException>(() => DimensionRemoval.RemoveAsync(ledger, wellbores, "user:admin", _clock, CancellationToken.None));
        Assert.Contains("no longer in the ledger", twice.Message, StringComparison.Ordinal);
        Assert.Contains(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = flow.LedgerId, Kind = DimensionRemoval.ActivityKind }), a => a.Outcome == "failed");
    }

    [Fact]
    public async Task A_property_of_the_record_itself_is_read_for_every_kind_and_counted_exactly_when_asked()
    {
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject(), legalTags: ["dev-a", "dev-b"]);
        _platform.Add("dev:master-data--Wellbore:2", Wellbore, new JsonObject(), legalTags: ["dev-a"]);
        _platform.Add("dev:master-data--Wellbore:3", Wellbore, new JsonObject(), legalTags: ["DEV-A", "dev-b"]);
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: LegalTag
                kind: "osdu:wks:master-data--Wellbore:*"
                path: legal.legaltags
                countRecords: true
                clean: [lower]
            """);

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "LegalTag"))!;
        var members = await MembersAsync(ledger, dimension.DimensionId);
        var a = members.Single(m => m.Value == "dev-a");
        Assert.Equal((3L, true, 2), (a.Records, a.RecordsExact, a.Originals));
        var b = members.Single(m => m.Value == "dev-b");
        Assert.Equal((2L, true), (b.Records, b.RecordsExact));
        Assert.Equal("legal.legaltags:(\"DEV-A\" OR \"dev-a\")", a.Filter);
        Assert.Equal(new DimensionFieldState("keyword", null, "legal.legaltags", true), dimension.Field);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_dimension_that_cannot_be_built_fails_alone_and_says_why()
    {
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject { ["FacilityName"] = "A" });
        _platform.Add("dev:master-data--Field:1", "osdu:wks:master-data--Field:1.0.0", new JsonObject { ["FieldName"] = "Field A" });
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: WellboreName
                kind: "osdu:wks:master-data--Wellbore:*"
                path: data.FacilityName
              - name: FieldName
                kind: "osdu:wks:master-data--Field:*"
                path: data.FieldName
            """);

        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Equal((1, 1), (failed.Outcome.Built, failed.Outcome.Failed));
        var field = failed.Outcome.Dimensions.Single(d => d.Dimension == "FieldName");
        Assert.Contains("osdu:wks:master-data--Field:1.0.0 (1 record(s))", field.Error, StringComparison.Ordinal);
        Assert.Contains("sqlflow template capture", field.Error, StringComparison.Ordinal);
        var names = (await ledger.FindDimensionAsync(flow.LedgerId, "WellboreName"))!;
        Assert.Equal(1, names.Members);
        var fieldName = (await ledger.FindDimensionAsync(flow.LedgerId, "FieldName"))!;
        Assert.Null(fieldName.LastRunId);
        Assert.Equal(DimensionRunStatus.Failed, (await ledger.ListDimensionRunsAsync(fieldName.DimensionId, 1)).Single().Status);
    }

    [Fact]
    public async Task A_kind_the_partition_holds_no_record_of_builds_a_dimension_with_no_value_and_says_so()
    {
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: WellboreName
                kind: "osdu:wks:master-data--Wellbore:*"
                path: data.FacilityName
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var built = Assert.Single(outcome.Dimensions);
        Assert.Equal((DimensionRunStatus.Completed, 0L), (built.Status, built.Values));
        Assert.Contains(built.Notes, n => n.Contains("No record of kind", StringComparison.Ordinal));
        Assert.Null((await ledger.FindDimensionAsync(flow.LedgerId, "WellboreName"))!.Field);
    }

    [Fact]
    public async Task Values_are_mapped_through_a_dictionary_found_beside_the_flow()
    {
        var root = Samples.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "dictionaries"));
        Directory.CreateDirectory(Path.Combine(root, "flows"));
        await File.WriteAllTextAsync(Path.Combine(root, "dictionaries", "Operators.yaml"), """
            documentType: dictionary
            name: Operators
            entries:
              Oldco: Newco Ltd
              Newco: Newco Ltd
              Retired: ~
            """);
        foreach (var (id, name) in new[] { ("1", "Oldco"), ("2", "NEWCO"), ("3", "Acme Oil"), ("4", "Retired"), ("5", "  ") })
        {
            _platform.Add($"dev:master-data--Wellbore:{id}", Wellbore, new JsonObject { ["FacilityName"] = name });
        }

        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Operator
                kind: "osdu:wks:master-data--Wellbore:*"
                path: data.FacilityName
                clean:
                  - trim
                  - map: { dictionary: Operators }
            """, Path.Combine(root, "flows", "dims.yaml"));

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Operator"))!;
        Assert.Equal(["Acme Oil", "Newco Ltd"], (await MembersAsync(ledger, dimension.DimensionId)).Select(m => m.Value));
        var left = await ledger.ListDimensionValuesAsync(dimension.DimensionId, new DimensionValueQuery(null, null, LeftOutOnly: true, false, null, 10));
        Assert.Equal(
            [("  ", DimensionLeftOut.Empty), ("Retired", DimensionLeftOut.Dropped)],
            left.Select(v => (v.Original, v.LeftOut!)).OrderBy(v => v.Original, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_number_the_service_names_no_keys_for_is_read_by_scanning()
    {
        _platform.NumberKeysMissing = true;
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject()).AsObject()["version"] = 11;
        _platform.Add("dev:master-data--Wellbore:2", Wellbore, new JsonObject()).AsObject()["version"] = 12;
        _platform.Add("dev:master-data--Wellbore:3", Wellbore, new JsonObject()).AsObject()["version"] = 12;
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - name: Version
                kind: "osdu:wks:master-data--Wellbore:*"
                path: version
            """);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Contains(outcome.Dimensions[0].Notes, n => n.Contains("scanning", StringComparison.Ordinal));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Version"))!;
        var members = await MembersAsync(ledger, dimension.DimensionId);
        Assert.Equal([("11", 1L), ("12", 2L)], members.Select(m => (m.Value, m.Records)));
        Assert.Equal("version:\"12\"", members[1].Filter);
    }

    [Fact]
    public async Task A_run_builds_only_the_dimensions_it_names()
    {
        _platform.Add("dev:master-data--Wellbore:1", Wellbore, new JsonObject { ["FacilityName"] = "A" });
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - { name: Names, kind: "osdu:wks:master-data--Wellbore:*", path: data.FacilityName }
              - { name: Kinds, kind: "osdu:wks:*:*", path: kind }
            """);

        var outcome = await runner.BuildAsync(["kinds"], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(["Kinds"], outcome.Dimensions.Select(d => d.Dimension));
        Assert.Null(await ledger.FindDimensionAsync(flow.LedgerId, "Names"));
    }

    [Fact]
    public async Task A_plan_settles_each_field_and_counts_each_dimensions_records_and_keeps_nothing()
    {
        Logs(logs: 5, curvesPerLog: 2, mnemonics: 4);
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - { name: CurveMnemonic, kind: "osdu:wks:work-product-component--WellLog:*", path: data.Curves.Mnemonic }
              - { name: FieldName, kind: "osdu:wks:master-data--Field:*", path: data.FieldName }
            """);

        var plan = await runner.PlanAsync([], CancellationToken.None);

        var curves = plan.Dimensions.Single(d => d.Dimension == "CurveMnemonic");
        Assert.Equal((5L, "nested(data.Curves, Mnemonic.keyword)", true), (curves.Records!.Value, curves.AggregateBy, curves.Repeats!.Value));
        Assert.Equal(WellLog, Assert.Single(curves.Kinds).Kind);
        Assert.Empty(curves.Problems);
        var field = plan.Dimensions.Single(d => d.Dimension == "FieldName");
        Assert.Equal(0, field.Records);
        Assert.NotEmpty(field.Problems);
        Assert.Empty(await ledger.ListDimensionsAsync("dev", null));
    }

    [Fact]
    public async Task A_platform_that_fails_part_way_fails_the_dimension_and_writes_none_of_it()
    {
        Logs(logs: 100, curvesPerLog: 4, mnemonics: 300);
        var (runner, ledger, flow) = await RunnerAsync(Head + """
            dimensions:
              - { name: CurveMnemonic, kind: "osdu:wks:work-product-component--WellLog:*", path: data.Curves.Mnemonic }
            """);
        var calls = 0;
        _platform.Fail = _ => ++calls > 4 ? System.Net.HttpStatusCode.ServiceUnavailable : null;

        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Contains("503", failed.Outcome.Dimensions[0].Error, StringComparison.Ordinal);
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "CurveMnemonic"))!;
        Assert.Equal(0, dimension.Members);
        var run = (await ledger.ListDimensionRunsAsync(dimension.DimensionId, 1)).Single();
        Assert.Equal(DimensionRunStatus.Failed, run.Status);
        Assert.NotNull(run.CompletedUtc);
    }
}
