using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>A key's label as the labeler read it: the text, the record it was read from, or why there is none.</summary>
/// <param name="Label">The text the last step's path holds in the record reached; null when none was read.</param>
/// <param name="From">The id of the record the label was read from; null when none was reached.</param>
/// <param name="Problem">Why the key has no label; null when it has one.</param>
public sealed record KeyLabel(string? Label, string? From, string? Problem);

/// <summary>What reading a dimension's keys' labels and attributes came to.</summary>
/// <param name="Labels">Each key's label, by key; empty for a dimension that reads none.</param>
/// <param name="Attributes">Each key's attributes that were read, by key, in the order the dimension declares them.</param>
/// <param name="Labelled">Keys that have a label.</param>
/// <param name="Unlabelled">Keys of a labelled dimension that have none.</param>
/// <param name="Queries">Searches asked.</param>
/// <param name="Notes">What reading had to say, a line each.</param>
/// <param name="Records">
/// The records each key's label and attributes were read through, by key, in the order they were asked for: the record the
/// key names and every record a step reached or looked for, found or not. A change of any of them can change what the key
/// reads, so an incremental load reads again the keys a changed record was read for.
/// </param>
public sealed record KeyLabels(
    IReadOnlyDictionary<string, KeyLabel> Labels, IReadOnlyDictionary<string, IReadOnlyList<DimensionAttributeState>> Attributes,
    long Labelled, long Unlabelled, int Queries, IReadOnlyList<string> Notes, IReadOnlyDictionary<string, IReadOnlyList<string>> Records)
{
    public static KeyLabels None { get; } = new(
        new Dictionary<string, KeyLabel>(StringComparer.Ordinal), new Dictionary<string, IReadOnlyList<DimensionAttributeState>>(StringComparer.Ordinal),
        0, 0, 0, [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}

/// <summary>
/// Reads the human-friendly label and the attributes of each key of a dimension (osdu/docs/reference/flow/dimension-attributes.md,
/// Labels and attributes): a key naming an OSDU record (<c>dev:master-data--Wellbore:WB-0001:</c>) is followed to the
/// record it names, and each path is read there (<c>data.FacilityName</c>); a label or an attribute of several steps
/// follows every reference a step reads to the next records (<c>data.GeoContexts.GeoPoliticalEntityID</c>, then
/// <c>data[GeoPoliticalEntityTypeID$=:Country:].GeoPoliticalEntityName</c>), and the first record reached that holds a value
/// at the last path gives it, so a filter on the last records picks the one that matters among several; an attribute that
/// says <c>many: join</c> or <c>many: max</c> is every value of every record reached, joined, or the greatest of them.
/// Records are found by id through the search service, <see cref="IdsPerQuery"/> ids a search, in the kind of their entity
/// type; every step reads all it needs of one entity type in the same searches, so a dimension's label and attributes that
/// start at the same records read them once. A key that names no record, a record the search does not hold, and one whose
/// path holds nothing, have no label (or no such attribute), and the result says how many and why.
/// </summary>
public sealed class DimensionLabeler
{
    /// <summary>The ids one search finds: each is a clause, and the service allows 1024 in a query.</summary>
    public const int IdsPerQuery = 500;

    /// <summary>The longest label kept; a longer one is cut.</summary>
    public const int MaxLabelLength = 1024;

    /// <summary>The keys a note names as examples.</summary>
    private const int Examples = 3;

    /// <summary>The name the label's chain goes by among the attributes', which no attribute can take.</summary>
    private const string LabelChain = "label";

    private static readonly OsduField Id = OsduField.Keyword("id");

    private readonly OsduSearch _search;
    private readonly ILogger _log;
    private readonly int _concurrency;

    /// <param name="search">The search the records are read from.</param>
    /// <param name="log">The run's log.</param>
    /// <param name="concurrency">The most searches asked at once: the records of a step are read a thousand ids a search, each on its own.</param>
    public DimensionLabeler(OsduSearch search, ILogger log, int concurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(log);
        _search = search;
        _log = log;
        _concurrency = Math.Max(1, concurrency);
    }

    /// <summary>The label of each of <paramref name="keys"/>, read through <paramref name="steps"/>.</summary>
    public Task<KeyLabels> LabelAsync(IReadOnlyCollection<string> keys, IReadOnlyList<string> steps, CancellationToken ct)
        => ReadAsync(keys, steps, [], null, ct);

    /// <summary>
    /// The label of each of <paramref name="keys"/>, read through <paramref name="label"/> (none when it is empty), and each
    /// of <paramref name="attributes"/>, read through its own steps, the records of an entity type looked up in the kind
    /// <paramref name="lookupKinds"/> names for it (<see cref="DimensionSpec.LookupKinds"/>), else in any of its kinds.
    /// </summary>
    public async Task<KeyLabels> ReadAsync(
        IReadOnlyCollection<string> keys, IReadOnlyList<string> label, IReadOnlyList<DimensionAttributeSpec> attributes,
        IReadOnlyDictionary<string, string>? lookupKinds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(attributes);
        var kinds = lookupKinds ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var chains = new List<Chain>(attributes.Count + 1);
        if (label.Count > 0)
        {
            chains.Add(new Chain(LabelChain, label, IsLabel: true));
        }

        chains.AddRange(attributes.Where(a => a.Steps.Count > 0)
            .Select(a => new Chain(a.Name, a.Steps, IsLabel: false, a.Keep, a.Many, a.Decode, a.MaxReferences, a.Trim)));
        if (chains.Count == 0 || keys.Count == 0)
        {
            return KeyLabels.None;
        }

        // Where each key has got to in each chain: the records the next step reads (every one a step before reached, in the
        // order it reached them), or why it stopped.
        var start = new Dictionary<string, (IReadOnlyList<string> References, string? Problem)>(keys.Count, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            start[key] = TargetId.IsRecordReference(key.Trim()) ? ([TargetId.WithoutVersion(key.Trim())], null) : ([], "it names no OSDU record");
        }

        var reached = chains.ToDictionary(c => c, _ => new Dictionary<string, (IReadOnlyList<string> References, string? Problem)>(start, StringComparer.Ordinal));
        var found = chains.ToDictionary(c => c, _ => new Dictionary<string, KeyLabel>(keys.Count, StringComparer.Ordinal));
        var readThrough = new Dictionary<string, List<string>>(keys.Count, StringComparer.Ordinal);
        var cut = 0;
        var uncut = 0;
        var queries = 0;
        var bounded = chains.ToDictionary(c => c, _ => 0);
        for (var step = 0; step < chains.Max(c => c.Steps.Count); step++)
        {
            var at = step;
            var active = chains.Where(c => c.Steps.Count > at).ToList();

            // What this step reads, by the entity type of the records it reads from: their ids, and every field a path of
            // this step needs returned there (a filter's property with its path).
            var wanted = new Dictionary<string, (HashSet<string> Ids, HashSet<string> Paths)>(StringComparer.Ordinal);
            foreach (var chain in active)
            {
                foreach (var (key, (references, _)) in reached[chain])
                {
                    var through = readThrough.TryGetValue(key, out var listed) ? listed : readThrough[key] = [];
                    foreach (var id in references)
                    {
                        if (!through.Contains(id, StringComparer.Ordinal))
                        {
                            through.Add(id);
                        }

                        var type = EntityTypeOf(id);
                        if (!wanted.TryGetValue(type, out var need))
                        {
                            need = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                            wanted[type] = need;
                        }

                        need.Ids.Add(id);
                        need.Paths.UnionWith(chain.Paths[at].ReturnedFields);
                    }
                }
            }

            var (records, asked) = await ReadAsync(wanted, kinds, ct).ConfigureAwait(false);
            queries += asked;

            // A filter through a reference compares what the record each reference names holds: those records are looked up
            // after the records the step reads, with only what the filters read of them, and a key is read through them too.
            var lookupWanted = new Dictionary<string, (HashSet<string> Ids, HashSet<string> Paths)>(StringComparer.Ordinal);
            foreach (var chain in active.Where(c => c.Paths[at].HasLookups))
            {
                foreach (var (key, (references, _)) in reached[chain])
                {
                    var through = readThrough[key];
                    foreach (var id in references.Where(records.ContainsKey))
                    {
                        foreach (var (referencesAt, readThere) in chain.Paths[at].Lookups)
                        {
                            foreach (var named in referencesAt.Read(records[id]).Select(v => v.Trim()).Where(TargetId.IsRecordReference).Select(TargetId.WithoutVersion))
                            {
                                if (!through.Contains(named, StringComparer.Ordinal))
                                {
                                    through.Add(named);
                                }

                                var type = EntityTypeOf(named);
                                if (!lookupWanted.TryGetValue(type, out var need))
                                {
                                    need = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                                    lookupWanted[type] = need;
                                }

                                need.Ids.Add(named);
                                need.Paths.UnionWith(readThere.ReturnedFields);
                            }
                        }
                    }
                }
            }

            var looked = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (lookupWanted.Count > 0)
            {
                (looked, var lookedUp) = await ReadAsync(lookupWanted, kinds, ct).ConfigureAwait(false);
                queries += lookedUp;
            }

            JsonObject? Lookup(string id) => looked.TryGetValue(id, out var record) ? record : null;
            foreach (var chain in active)
            {
                var last = at == chain.Steps.Count - 1;
                var path = chain.Paths[at];
                var where = reached[chain];
                foreach (var key in where.Keys.ToList())
                {
                    var (references, problem) = where[key];
                    if (references.Count == 0)
                    {
                        found[chain][key] = new KeyLabel(null, null, problem);
                        where.Remove(key);
                        continue;
                    }

                    var held = references.Where(records.ContainsKey).ToList();
                    if (held.Count == 0)
                    {
                        found[chain][key] = new KeyLabel(null, null, $"the search holds no record {references[0]}");
                        where.Remove(key);
                        continue;
                    }

                    if (last)
                    {
                        var longest = chain.IsLabel ? MaxLabelLength : DimensionSpec.MaxAttributeValueLength;
                        string? text = null;
                        string? from = null;
                        var over = false;
                        if (chain.Many == DimensionValueMany.First)
                        {
                            // The first record reached that holds a value at the path gives it, kept as the chain keeps it.
                            foreach (var id in held)
                            {
                                var read = path.Read(records[id], Lookup).FirstOrDefault(v => DimensionKeeping.IsValue(v, chain.Keep, chain.Trim));
                                if (read is null)
                                {
                                    continue;
                                }

                                var (kept, wasCut, tooLong) = DimensionKeeping.Keep(DimensionKeeping.Decoded(read, chain.Decode), chain.Keep, longest, chain.Trim);
                                over |= tooLong;
                                if (kept is not null)
                                {
                                    (text, from) = (kept, id);
                                    cut += wasCut ? 1 : 0;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            // Every value of every record reached, in the order they were reached: joined, or the greatest.
                            var values = held.SelectMany(id => path.Read(records[id], Lookup).Select(v => (Id: id, Text: v))).ToList();
                            var (kept, place, wasCut, tooLong) = DimensionKeeping.KeepMany(
                                values.Select(v => v.Text).ToList(), chain.Keep, chain.Many, chain.Decode, longest, chain.Trim);
                            over = tooLong;
                            if (kept is not null)
                            {
                                (text, from) = (kept, values[place].Id);
                                cut += wasCut ? 1 : 0;
                            }
                        }

                        if (text is null && over)
                        {
                            uncut++;
                        }

                        found[chain][key] = text is null
                            ? new KeyLabel(null, held[0], over
                                ? $"record {held[0]} holds more than the {longest} characters a key kept whole allows at {path.Text}"
                                : $"record {held[0]} holds nothing at {path.Text}")
                            : new KeyLabel(text, from, null);
                        where.Remove(key);
                        continue;
                    }

                    // Every reference the records reached hold at the path, in order, each once: the next step reads them all,
                    // up to as many as the chain follows, and a key naming more is counted for a note.
                    var next = held
                        .SelectMany(id => path.Read(records[id], Lookup))
                        .Select(v => v.Trim())
                        .Where(TargetId.IsRecordReference)
                        .Select(TargetId.WithoutVersion)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (next.Count > chain.MaxReferences)
                    {
                        bounded[chain]++;
                        next = next.Take(chain.MaxReferences).ToList();
                    }

                    where[key] = next.Count == 0 ? ([], $"record {held[0]} holds no record reference at {path.Text}") : (next, null);
                }
            }
        }

        var notes = new List<string>();
        var labels = chains.FirstOrDefault(c => c.IsLabel) is { } labelChain ? found[labelChain] : new Dictionary<string, KeyLabel>(StringComparer.Ordinal);
        var unlabelled = labels.Where(l => l.Value.Label is null).ToList();
        foreach (var why in unlabelled.GroupBy(l => Reason(l.Value.Problem!)).OrderByDescending(g => g.Count()))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{why.Count()} key(s) have no label, since {why.Key}: {Named(why.Select(l => l.Key))}."));
        }

        var attributeValues = new Dictionary<string, IReadOnlyList<DimensionAttributeState>>(keys.Count, StringComparer.Ordinal);
        foreach (var chain in chains.Where(c => !c.IsLabel))
        {
            var read = found[chain];
            foreach (var (key, value) in read)
            {
                if (value.Label is not null)
                {
                    var held = attributeValues.TryGetValue(key, out var list) ? (List<DimensionAttributeState>)list : [];
                    held.Add(new DimensionAttributeState(chain.Name, value.Label, value.From));
                    attributeValues[key] = held;
                }
            }

            // Why keys lack an attribute, the commonest reason first; a key that names no record is said once, by the label or here.
            foreach (var why in read.Where(r => r.Value.Label is null).GroupBy(r => Reason(r.Value.Problem!)).OrderByDescending(g => g.Count()).Take(2))
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{why.Count()} key(s) have no {chain.Name}, since {why.Key}: {Named(why.Select(r => r.Key))}."));
            }
        }

        foreach (var (chain, keysBounded) in bounded.Where(b => b.Value > 0))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{keysBounded} key(s) name more than {chain.MaxReferences} record(s) at a step of {(chain.IsLabel ? "the label" : chain.Name)}, and only the first {chain.MaxReferences} were followed{(chain.IsLabel ? string.Empty : $"; raise maxReferences (at most {DimensionAttributeSpec.MaxReferencesCeiling}) to follow more")}."));
        }

        if (cut > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{cut} label(s) or attribute value(s) were longer than a dimension keeps ({MaxLabelLength} characters for a label, {DimensionSpec.MaxAttributeValueLength} for an attribute) and were cut."));
        }

        if (uncut > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{uncut} attribute value(s) that keep the key were longer than the {DimensionSpec.MaxAttributeValueLength} characters a dimension keeps, and are left out rather than cut, since a key cut joins to nothing."));
        }

        _log.LogInformation(
            "labels and attributes: {Labelled} of {Keys} key(s) labelled, {Attributes} attribute(s) read through {Chains} chain(s) in {Queries} search(es)",
            labels.Count - unlabelled.Count, keys.Count, attributeValues.Values.Sum(a => a.Count), chains.Count, queries);
        var keyRecords = readThrough.ToDictionary(a => a.Key, a => (IReadOnlyList<string>)a.Value, StringComparer.Ordinal);
        return new KeyLabels(labels, attributeValues, labels.Count - unlabelled.Count, unlabelled.Count, queries, notes, keyRecords);
    }

    /// <summary>
    /// The records of <paramref name="wanted"/> the search finds, by id, each holding the paths asked of its entity type:
    /// the ids of one entity type searched in that type's kind, a chunk at a time.
    /// </summary>
    private async Task<(Dictionary<string, JsonObject> Records, int Queries)> ReadAsync(
        Dictionary<string, (HashSet<string> Ids, HashSet<string> Paths)> wanted, IReadOnlyDictionary<string, string> kinds, CancellationToken ct)
    {
        var asks = new List<(string Kind, List<string> Fields, string[] Ids)>();
        foreach (var (type, need) in wanted)
        {
            var fields = new List<string>(need.Paths.Count + 1) { "id" };
            fields.AddRange(need.Paths.Order(StringComparer.Ordinal));
            var kind = kinds.TryGetValue(type, out var pinned) ? pinned : $"*:*:{type}:*";
            asks.AddRange(need.Ids.Order(StringComparer.Ordinal).Chunk(IdsPerQuery).Select(chunk => (kind, fields, chunk)));
        }

        // Each search asks for records of its own, so they are asked several at a time and gathered as they answer.
        var records = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var gate = new Lock();
        await Parallel.ForEachAsync(
            asks,
            new ParallelOptions { MaxDegreeOfParallelism = _concurrency, CancellationToken = ct },
            async (ask, token) =>
            {
                var query = new OsduSearchQuery
                {
                    Kind = ask.Kind,
                    Query = OsduQuery.AnyOf(Id, ask.Ids).Text,
                    ReturnedFields = ask.Fields,
                };
                var (_, hits) = await _search.FirstAsync(query, OsduSearch.MaxPage, token).ConfigureAwait(false);
                lock (gate)
                {
                    foreach (var hit in hits)
                    {
                        if (hit["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id))
                        {
                            records[id] = hit;
                        }
                    }
                }
            }).ConfigureAwait(false);

        return (records, asks.Count);
    }

    /// <summary>
    /// <paramref name="text"/> as a value shows it: for the id of an OSDU record (<c>dev:reference-data--UnitOfMeasure:us%2Fft:</c>),
    /// the code the id ends with, its escapes decoded (<c>us/ft</c>), since an id escapes what it cannot hold; any other text
    /// as it is.
    /// </summary>
    public static string DisplayOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (!TargetId.IsRecordReference(trimmed))
        {
            return text;
        }

        var id = TargetId.WithoutVersion(trimmed);
        var code = id[(id.LastIndexOf(':') + 1)..];
        if (code.Length == 0)
        {
            return text;
        }

        // An escape that is not one (a lone %, or bytes that are no UTF-8) is kept as it is.
        try
        {
            return Uri.UnescapeDataString(code);
        }
        catch (UriFormatException)
        {
            return code;
        }
    }

    /// <summary>The entity type an id names: its second segment (<c>master-data--Wellbore</c>).</summary>
    private static string EntityTypeOf(string id) => id.Split(':')[1];

    /// <summary>A problem without the record it names, so keys stopped for the same reason are counted together.</summary>
    private static string Reason(string problem)
        => problem.StartsWith("the search holds no record", StringComparison.Ordinal) ? "the search holds no record they name"
            : problem.StartsWith("record ", StringComparison.Ordinal) && problem.Contains(" holds nothing at ", StringComparison.Ordinal)
                ? "the record they name holds nothing at the path it is read from"
            : problem.StartsWith("record ", StringComparison.Ordinal) ? "the record they name holds no reference where one is read"
            : problem;

    /// <summary>The first few keys of a group, quoted, for a note.</summary>
    private static string Named(IEnumerable<string> keys)
    {
        var list = keys.Take(Examples + 1).ToList();
        return string.Join(", ", list.Take(Examples).Select(k => $"'{Shown(k)}'")) + (list.Count > Examples ? ", ..." : string.Empty);
    }

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;

    /// <summary>
    /// One label or attribute: its name, the paths it is read through, whether it is the label, and how its value is kept,
    /// chosen among those the last step reaches, and decoded.
    /// </summary>
    private sealed record Chain(
        string Name, IReadOnlyList<string> Steps, bool IsLabel, DimensionValueKeep Keep = DimensionValueKeep.Value,
        DimensionValueMany Many = DimensionValueMany.First, DimensionValueDecode Decode = DimensionValueDecode.None,
        int MaxReferences = DimensionAttributeSpec.DefaultMaxReferences, bool Trim = true)
    {
        /// <summary>
        /// Each step's path, parsed; a step the document mapper would have refused reads nothing, never the wrong thing.
        /// </summary>
        public IReadOnlyList<DimensionPath> Paths { get; } = Steps
            .Select(step => DimensionPath.Parse(step).Path
                ?? throw new DeliveryException($"'{step}' is not a path a label or an attribute can be read through: {DimensionPath.Parse(step).Problem}."))
            .ToList();
    }
}
