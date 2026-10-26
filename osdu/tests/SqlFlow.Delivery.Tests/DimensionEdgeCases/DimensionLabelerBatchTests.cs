using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The labeler reads in bounded passes, and a build reads a dimension's keys a batch at a time: what the batches read, added
/// up in order, is what one read of every key reads, its notes, their examples and their order included; a record many keys
/// reach past their own is asked for once; and a reference no query can carry leaves its key without a value rather than
/// failing the read.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public sealed class DimensionLabelerBatchTests : IDisposable
{
    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string Entity = "osdu:wks:master-data--GeoPoliticalEntity:1.0.0";
    private const string Field = "osdu:wks:master-data--Field:1.0.0";

    private readonly FakeDimensionPlatform _platform = new();

    public void Dispose() => _platform.Dispose();

    private DimensionLabeler Labeler(int concurrency = 2) => new(Search(), concurrency);

    private OsduSearch Search(int requestsAtOnce = 0)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), _platform, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, FakeDimensionPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return new OsduSearch(client, "/api/search/v2/query", "/api/search/v2/query_with_cursor", NullLogger.Instance, requestsAtOnce: requestsAtOnce);
    }

    /// <summary>
    /// Wellbores <paramref name="from"/> to <paramref name="to"/>, each naming <paramref name="fields"/> of the first fields
    /// there are, all named <c>Field n</c>; the fields themselves are added once.
    /// </summary>
    private List<string> Wellbores(int from, int to, int fields)
    {
        if (!_platform.Records.Any(r => r["kind"]!.GetValue<string>() == Field))
        {
            for (var f = 0; f < 1_000; f++)
            {
                _platform.Add($"dev:master-data--Field:F{f:D4}", Field, new JsonObject { ["FieldName"] = $"Field {f}" });
            }
        }

        var keys = new List<string>();
        for (var i = from; i < to; i++)
        {
            var contexts = new JsonArray(Enumerable.Range(0, fields).Select(f => (JsonNode?)new JsonObject { ["FieldID"] = $"dev:master-data--Field:F{f:D4}:" }).ToArray());
            _platform.Add($"dev:master-data--Wellbore:W{i:D5}", Wellbore, new JsonObject { ["FacilityName"] = $"Wellbore {i}", ["GeoContexts"] = contexts });
            keys.Add($"dev:master-data--Wellbore:W{i:D5}:");
        }

        return keys;
    }

    /// <summary>What a read keeps of each key, as one text, to compare two reads by.</summary>
    private static List<string> Kept(KeyLabels read)
        => read.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => a.Key + " = " + string.Join(" | ", a.Value.Select(v => $"{v.Name}:{v.Value}@{v.From}")) + " via " + string.Join(",", read.Records[a.Key]))
            .ToList();

    /// <summary>
    /// Three hundred wellbore keys meeting every reason a key has no value, at every step: no record for the key, a name
    /// missing, no context, a country the search does not hold, a country with no name, fields past those followed; and keys
    /// that name no record, or name one by a reference no query can carry.
    /// </summary>
    private List<string> Estate()
    {
        _platform.Add("dev:master-data--GeoPoliticalEntity:NO", Entity, new JsonObject { ["GeoPoliticalEntityName"] = "Norway" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:SE", Entity, new JsonObject { ["GeoPoliticalEntityName"] = "Sweden" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:NAMELESS", Entity, new JsonObject());
        for (var f = 0; f < 6; f++)
        {
            _platform.Add($"dev:master-data--Field:F{f}", Field, new JsonObject { ["FieldName"] = $"Field {f}" });
        }

        var keys = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var key = $"dev:master-data--Wellbore:W{i:D3}:";
            keys.Add(key);
            if (i % 10 == 0)
            {
                continue;
            }

            var data = new JsonObject();
            if (i % 10 != 1)
            {
                data["FacilityName"] = $"Wellbore {i:D3}";
            }

            var contexts = new JsonArray();
            var country = (i % 7) switch { 0 => null, 1 => "GONE", 2 => "NAMELESS", 3 => "SE", _ => "NO" };
            if (country is not null)
            {
                contexts.Add(new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{country}:" });
            }

            for (var f = 0; f < i % 5; f++)
            {
                contexts.Add(new JsonObject { ["FieldID"] = $"dev:master-data--Field:F{(i + f) % 6}:" });
            }

            data["GeoContexts"] = contexts;
            _platform.Add(key[..^1], Wellbore, data);
        }

        keys.Insert(17, "plain text");
        keys.Insert(140, "dev:master-data--Wellbore:bad\u0001:");
        return keys;
    }

    private static DimensionAttributeSpec[] Attributes(int maxReferences) =>
    [
        new("Country", ["data.GeoContexts.GeoPoliticalEntityID", "data.GeoPoliticalEntityName"]),
        new("Fields", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Join, MaxReferences: maxReferences),
    ];

    [Theory]
    [InlineData(2, 37)]
    [InlineData(1_000, 37)]
    [InlineData(2, 1)]
    public async Task Keys_read_a_batch_at_a_time_and_added_up_read_what_one_read_of_every_key_reads(int maxReferences, int batch)
    {
        var keys = Estate();
        var attributes = Attributes(maxReferences);
        IReadOnlyList<string> label = ["data.FacilityName"];

        var whole = await Labeler().ReadAsync(keys, label, attributes, null, CancellationToken.None);

        var labeler = Labeler();
        var labels = new Dictionary<string, KeyLabel>(StringComparer.Ordinal);
        var values = new Dictionary<string, IReadOnlyList<DimensionAttributeState>>(StringComparer.Ordinal);
        var records = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        LabelTally? tally = null;
        long labelled = 0;
        foreach (var part in keys.Chunk(batch))
        {
            var read = await labeler.ReadAsync(part, label, attributes, null, CancellationToken.None);
            foreach (var (key, found) in read.Labels)
            {
                labels[key] = found;
            }

            foreach (var (key, found) in read.Attributes)
            {
                values[key] = found;
            }

            foreach (var (key, through) in read.Records)
            {
                records[key] = through;
            }

            labelled += read.Labelled;
            if (tally is null)
            {
                tally = read.Tally;
            }
            else
            {
                tally.Add(read.Tally!);
            }
        }

        Assert.Equal(whole.Labels.OrderBy(l => l.Key, StringComparer.Ordinal), labels.OrderBy(l => l.Key, StringComparer.Ordinal));
        Assert.Equal(whole.Labelled, labelled);
        Assert.Equal(whole.Attributes.Keys.Order(StringComparer.Ordinal), values.Keys.Order(StringComparer.Ordinal));
        Assert.All(whole.Attributes, a => Assert.Equal(a.Value, values[a.Key]));
        Assert.Equal(whole.Records.Keys.Order(StringComparer.Ordinal), records.Keys.Order(StringComparer.Ordinal));
        Assert.All(whole.Records, r => Assert.Equal(r.Value, records[r.Key]));
        Assert.Equal(whole.Notes, tally!.Notes());

        // Every reason was met, at the step it is met at, so the notes order them as one read meets them.
        Assert.Contains(whole.Notes, n => n.Contains("have no label, since the search holds no record they name", StringComparison.Ordinal));
        Assert.Contains(whole.Notes, n => n.Contains("have no label, since the record they name holds nothing at the path", StringComparison.Ordinal));
        Assert.Contains(whole.Notes, n => n.Contains("have no label, since it names no OSDU record: 'plain text'", StringComparison.Ordinal));
        Assert.Contains(whole.Notes, n => n.Contains("have no Country, since", StringComparison.Ordinal));
        Assert.Equal(maxReferences < 4, whole.Notes.Any(n => n.Contains("name more than 2 record(s) at a step of Fields", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_record_many_keys_reach_past_their_own_is_asked_for_once_whatever_batch_reaches_it()
    {
        var keys = Estate();
        var labeler = Labeler();
        IReadOnlyList<string> none = [];

        await labeler.ReadAsync(keys.Take(150).ToList(), none, Attributes(20), null, CancellationToken.None);
        var before = _platform.Calls.Count;
        var later = await labeler.ReadAsync(keys.Skip(150).ToList(), none, Attributes(20), null, CancellationToken.None);

        // The second batch asks for its own wellbores only: every country and field they reach was read by the first, the
        // country the search does not hold included.
        var asked = _platform.Calls.Skip(before).Select(c => c.Body!).ToList();
        Assert.Single(asked);
        Assert.Contains("master-data--Wellbore", asked[0], StringComparison.Ordinal);
        Assert.Equal("Norway", later.Attributes["dev:master-data--Wellbore:W151:"].Single(a => a.Name == "Country").Value);
        Assert.Contains("dev:master-data--GeoPoliticalEntity:GONE", later.Records["dev:master-data--Wellbore:W162:"]);
    }

    [Fact]
    public async Task A_reference_no_query_can_carry_leaves_its_key_without_a_value_and_says_why()
    {
        _platform.Add("dev:master-data--Wellbore:OK", Wellbore, new JsonObject
        {
            ["FacilityName"] = "OK",
            ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = "dev:master-data--GeoPoliticalEntity:bad\u0001:" }),
        });

        var read = await Labeler().ReadAsync(
            ["dev:master-data--Wellbore:bad\u0001:", "dev:master-data--Wellbore:OK:"], ["data.FacilityName"], Attributes(20), null, CancellationToken.None);

        Assert.Equal("the reference it holds is no text a query can carry", read.Labels["dev:master-data--Wellbore:bad\u0001:"].Problem);
        Assert.Equal("OK", read.Labels["dev:master-data--Wellbore:OK:"].Label);
        Assert.Contains(read.Notes, n => n.StartsWith("1 key(s) have no label, since the reference it holds is no text a query can carry", StringComparison.Ordinal));
        Assert.Contains(read.Notes, n => n.Contains("have no Country, since the record they name holds only references no query can carry", StringComparison.Ordinal));
        Assert.DoesNotContain(_platform.Calls, c => c.Body!.Contains("bad", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pass_reads_fewer_keys_the_more_records_its_later_steps_may_reach()
    {
        // A label and attributes of one step reach no record past the key's own.
        Assert.Equal(DimensionLabeler.MaxKeysPerPass, DimensionLabeler.PassSize(["data.Name"], [new("Code", ["data.Code"])]));

        // A label of two steps follows twenty records a key, and an attribute of two steps as many as it says.
        Assert.Equal(DimensionLabeler.MaxReachedPerPass / 50, DimensionLabeler.PassSize(["data.WellID", "data.Name"], [new("Fields", ["data.F", "data.N"], MaxReferences: 30)]));
        Assert.Equal(DimensionLabeler.MaxReachedPerPass / 20, DimensionLabeler.PassSize(["data.WellID", "data.Name"], []));
        Assert.Equal(DimensionLabeler.MaxReachedPerPass / 1_020, DimensionLabeler.PassSize(["data.WellID", "data.Name"], [new("Fields", ["data.F", "data.N"], MaxReferences: 1_000)]));
        Assert.Equal(DimensionLabeler.MaxKeysPerPass, DimensionLabeler.PassSize([], [new("Fields", ["data.F", "data.N"], MaxReferences: 1)]));
    }

    [Fact]
    public async Task Passes_grow_to_as_many_keys_as_the_references_their_keys_follow_fit_so_each_search_asks_five_hundred_ids()
    {
        // Each wellbore names one field, though its attributes may follow a thousand each: the declaration bounds the first
        // pass to 25 keys, and the one reference each key follows lets every later pass read four thousand.
        var keys = Wellbores(0, 3_000, 1);
        DimensionAttributeSpec[] attributes =
        [
            new("Field", ["data.GeoContexts.FieldID", "data.FieldName"], MaxReferences: 1_000),
            new("Fields", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Join, MaxReferences: 1_000),
        ];
        Assert.Equal(25, DimensionLabeler.PassSize([], attributes));
        var labeler = Labeler(4);

        var read = await labeler.ReadAsync(keys, [], attributes, null, CancellationToken.None);

        // The first pass asks for its 25 wellbores and their field; the second for its 2,975 wellbores, five hundred a search,
        // the field being read already. A pass of 25 keys at a time asked 120 searches for the wellbores alone.
        Assert.Equal(3_000, read.Attributes.Count);
        Assert.Equal("Field 0", read.Attributes["dev:master-data--Wellbore:W02999:"].Single(a => a.Name == "Field").Value);
        Assert.Equal(DimensionLabeler.MaxKeysPerPass, labeler.PassSizeFor([], attributes));
        var wellbores = _platform.Calls.Select(c => IdsIn(c.Body!, "master-data--Wellbore")).Where(n => n > 0).Order().ToList();
        Assert.Equal([25, 475, 500, 500, 500, 500, 500], wellbores);
        Assert.Equal(8, read.Queries);
    }

    [Fact]
    public async Task A_pass_whose_keys_reach_more_than_a_pass_holds_is_read_again_in_smaller_passes_and_reads_what_a_key_at_a_time_reads()
    {
        // Wellbores naming one field each, then wellbores naming three hundred: once the first passes have sized passes for
        // keys of one reference, a pass of the later ones reaches far more references than a pass holds.
        var keys = Wellbores(0, 400, 1);
        keys.AddRange(Wellbores(400, 600, 300));
        DimensionAttributeSpec[] attributes = [new("Fields", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Join, MaxReferences: 1_000)];

        var read = await Labeler(4).ReadAsync(keys, [], attributes, null, CancellationToken.None);

        // Some wellbores were asked for more than once: a pass that reached too many was let go and its keys read again.
        var asked = _platform.Calls.SelectMany(c => IdsOf(c.Body!, "master-data--Wellbore")).GroupBy(id => id).Count(g => g.Count() > 1);
        Assert.True(asked > 0, "No pass was let go, so the test did not read a pass that reached too many references.");

        // A key at a time, no pass holds more than one key's references; what the two read is the same, notes included.
        var single = Labeler(4);
        LabelTally? tally = null;
        var one = new Dictionary<string, IReadOnlyList<DimensionAttributeState>>(StringComparer.Ordinal);
        var through = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var part = await single.ReadAsync([key], [], attributes, null, CancellationToken.None);
            foreach (var (k, v) in part.Attributes)
            {
                one[k] = v;
            }

            foreach (var (k, v) in part.Records)
            {
                through[k] = v;
            }

            if (tally is null)
            {
                tally = part.Tally;
            }
            else
            {
                tally.Add(part.Tally!);
            }
        }

        Assert.Equal(Kept(read), Kept(new KeyLabels(new Dictionary<string, KeyLabel>(), one, 0, 0, 0, [], through)));
        Assert.Equal(read.Notes, tally!.Notes());
    }

    [Fact]
    public async Task Reads_side_by_side_ask_no_more_searches_at_once_than_the_labeler_has_places()
    {
        var keys = Wellbores(0, 6_000, 1);
        _platform.Latency = TimeSpan.FromMilliseconds(30);
        var labeler = Labeler(3);
        DimensionAttributeSpec[] attributes = [new("Name", ["data.FacilityName"])];

        var reads = await Task.WhenAll(
            labeler.ReadAsync(keys.Take(3_000).ToList(), [], attributes, null, CancellationToken.None),
            labeler.ReadAsync(keys.Skip(3_000).ToList(), [], attributes, null, CancellationToken.None));

        Assert.Equal(6_000, reads.Sum(r => r.Attributes.Count));
        Assert.Equal(3, _platform.MostAtOnce);
    }

    [Fact]
    public async Task Readers_sharing_a_search_that_bounds_its_requests_ask_no_more_at_once_together()
    {
        var keys = Wellbores(0, 6_000, 1);
        _platform.Latency = TimeSpan.FromMilliseconds(30);
        var search = Search(requestsAtOnce: 2);
        DimensionAttributeSpec[] attributes = [new("Name", ["data.FacilityName"])];

        await Task.WhenAll(
            new DimensionLabeler(search, 4).ReadAsync(keys.Take(3_000).ToList(), [], attributes, null, CancellationToken.None),
            new DimensionLabeler(search, 4).ReadAsync(keys.Skip(3_000).ToList(), [], attributes, null, CancellationToken.None));

        Assert.Equal(2, search.RequestsAtOnce);
        Assert.Equal(2, _platform.MostAtOnce);
    }

    /// <summary>The ids of <paramref name="type"/> a search's body asks for.</summary>
    private static List<string> IdsOf(string body, string type)
    {
        var query = JsonNode.Parse(body)?["query"]?.GetValue<string>() ?? string.Empty;
        return System.Text.RegularExpressions.Regex.Matches(query, "\"(dev:" + type + ":[^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
    }

    private static int IdsIn(string body, string type) => IdsOf(body, type).Count;
}
