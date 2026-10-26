using System.Globalization;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// What the read counted that its notes say, kept so the reads of a build's keys a batch at a time add up to the notes one
    /// read of them all would have (<see cref="LabelTally.Add"/>); null for a read of nothing.
    /// </summary>
    public LabelTally? Tally { get; init; }
}

/// <summary>
/// What reads of keys' labels and attributes found that their notes count: for the label and each attribute, the keys that
/// have none of it, by why, each reason with how many keys, where it was first met and the first keys met with it; the keys
/// naming more records at a step than it follows; and the values cut, or left out for their length. A read keeps its keys in
/// the order it was given them, and meets a key's reason at the step the key stops at, so the order of its notes is the
/// order of those steps and then of its keys. Reads added up in the order their keys came (<see cref="Add"/>) say what one
/// read of all of their keys would have said, a later read's keys after an earlier one's at the same step.
/// </summary>
public sealed class LabelTally
{
    /// <summary>The keys a note names as examples.</summary>
    private const int Examples = 3;

    private readonly List<ChainTally> _chains;

    /// <summary>The reads added up so far, which a later read's places are counted after.</summary>
    private int _reads;

    private LabelTally(List<ChainTally> chains)
    {
        _chains = chains;
        _reads = 1;
    }

    /// <summary>Label and attribute values longer than a dimension keeps, which were cut.</summary>
    public long Cut { get; private set; }

    /// <summary>Values that keep a key, longer than a dimension keeps, which were left out rather than cut.</summary>
    public long Uncut { get; private set; }

    /// <summary>An empty tally of one read of the chains given, in the order they are read.</summary>
    internal static LabelTally For(IEnumerable<(string Name, bool IsLabel, int MaxReferences)> chains)
        => new(chains.Select(c => new ChainTally(c.Name, c.IsLabel, c.MaxReferences)).ToList());

    /// <summary>A key of chain <paramref name="chain"/> that has no value for <paramref name="reason"/>, met at step <paramref name="step"/> as the read's key <paramref name="index"/>.</summary>
    internal void Lacks(int chain, string reason, int step, int index, string key) => _chains[chain].Lacks(reason, new Place(step, 0, index), key);

    /// <summary>A key of chain <paramref name="chain"/> that names more records at a step than the chain follows.</summary>
    internal void Bounded(int chain) => _chains[chain].Bounded++;

    internal void CutOne() => Cut++;

    internal void UncutOne() => Uncut++;

    /// <summary>
    /// Adds <paramref name="later"/>, the tally of the read of the keys that came after this one's, so this one says what one
    /// read of both sets of keys would have said. The two read the same label and attributes.
    /// </summary>
    /// <exception cref="ArgumentException">The two read other labels or attributes.</exception>
    public void Add(LabelTally later)
    {
        ArgumentNullException.ThrowIfNull(later);
        if (ReferenceEquals(later, this))
        {
            throw new ArgumentException("A tally is added to another, not to itself.", nameof(later));
        }

        if (later._chains.Count != _chains.Count || later._chains.Zip(_chains).Any(p => !string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The two reads read other labels or attributes, so their notes cannot be added up.", nameof(later));
        }

        for (var i = 0; i < _chains.Count; i++)
        {
            _chains[i].Add(later._chains[i], _reads);
        }

        Cut += later.Cut;
        Uncut += later.Uncut;
        _reads += later._reads;
    }

    /// <summary>
    /// What the reads have to say, a line each: why keys have no label, the commonest reason first; then, for each attribute,
    /// its two commonest reasons; then the keys bounded at a step; then the values cut and left out for their length.
    /// </summary>
    public IReadOnlyList<string> Notes()
    {
        var notes = new List<string>();
        foreach (var chain in _chains.Where(c => c.IsLabel))
        {
            foreach (var why in chain.Ordered())
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{why.Count} key(s) have no label, since {why.Reason}: {Named(why)}."));
            }
        }

        foreach (var chain in _chains.Where(c => !c.IsLabel))
        {
            foreach (var why in chain.Ordered().Take(2))
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{why.Count} key(s) have no {chain.Name}, since {why.Reason}: {Named(why)}."));
            }
        }

        foreach (var chain in _chains.Where(c => c.Bounded > 0))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{chain.Bounded} key(s) name more than {chain.MaxReferences} record(s) at a step of {(chain.IsLabel ? "the label" : chain.Name)}, and only the first {chain.MaxReferences} were followed{(chain.IsLabel ? string.Empty : $"; raise maxReferences (at most {DimensionAttributeSpec.MaxReferencesCeiling}) to follow more")}."));
        }

        if (Cut > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Cut} label(s) or attribute value(s) were longer than a dimension keeps ({DimensionLabeler.MaxLabelLength} characters for a label, {DimensionSpec.MaxAttributeValueLength} for an attribute) and were cut."));
        }

        if (Uncut > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Uncut} attribute value(s) that keep the key were longer than the {DimensionSpec.MaxAttributeValueLength} characters a dimension keeps, and are left out rather than cut, since a key cut joins to nothing."));
        }

        return notes;
    }

    /// <summary>The first few keys of a reason, quoted, for a note.</summary>
    private static string Named(ReasonTally why)
        => string.Join(", ", why.Examples.Take(Examples).Select(e => $"'{Shown(e.Key)}'")) + (why.Count > Examples ? ", ..." : string.Empty);

    private static string Shown(string value) => value.Length > 60 ? value[..60] + "..." : value;

    /// <summary>Where a key was met: the step it stopped at, the read it was in, and its place among that read's keys.</summary>
    private readonly record struct Place(int Step, int Read, int Index) : IComparable<Place>
    {
        public int CompareTo(Place other)
            => Step != other.Step ? Step.CompareTo(other.Step) : Read != other.Read ? Read.CompareTo(other.Read) : Index.CompareTo(other.Index);

        /// <summary>The place as counted after <paramref name="reads"/> earlier reads.</summary>
        public Place After(int reads) => this with { Read = Read + reads };
    }

    /// <summary>One reason keys have no value: how many, where it was first met, and the first keys met with it.</summary>
    private sealed class ReasonTally(string reason, Place first)
    {
        public string Reason { get; } = reason;

        public long Count { get; set; }

        public Place First { get; set; } = first;

        /// <summary>The first keys met with the reason, in the order they were met: as many as a note names, and one more.</summary>
        public List<(Place At, string Key)> Examples { get; } = [];

        public void Met(Place at, string key)
        {
            Count++;
            if (at.CompareTo(First) < 0)
            {
                First = at;
            }

            Keep(at, key);
        }

        /// <summary>Keeps <paramref name="key"/> among the examples when it was met before one of them, or there is room.</summary>
        public void Keep(Place at, string key)
        {
            if (Examples.Count > LabelTally.Examples && at.CompareTo(Examples[^1].At) >= 0)
            {
                return;
            }

            var before = Examples.FindIndex(e => at.CompareTo(e.At) < 0);
            Examples.Insert(before < 0 ? Examples.Count : before, (at, key));
            if (Examples.Count > LabelTally.Examples + 1)
            {
                Examples.RemoveAt(Examples.Count - 1);
            }
        }
    }

    /// <summary>What one label or attribute counted.</summary>
    private sealed class ChainTally(string name, bool isLabel, int maxReferences)
    {
        private readonly Dictionary<string, ReasonTally> _reasons = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public bool IsLabel { get; } = isLabel;

        public int MaxReferences { get; } = maxReferences;

        public long Bounded { get; set; }

        public void Lacks(string reason, Place at, string key)
        {
            if (!_reasons.TryGetValue(reason, out var tally))
            {
                tally = new ReasonTally(reason, at);
                _reasons[reason] = tally;
            }

            tally.Met(at, key);
        }

        public void Add(ChainTally later, int reads)
        {
            Bounded += later.Bounded;
            foreach (var (reason, theirs) in later._reasons)
            {
                var first = theirs.First.After(reads);
                if (!_reasons.TryGetValue(reason, out var mine))
                {
                    mine = new ReasonTally(reason, first);
                    _reasons[reason] = mine;
                }
                else if (first.CompareTo(mine.First) < 0)
                {
                    mine.First = first;
                }

                mine.Count += theirs.Count;
                foreach (var (at, key) in theirs.Examples)
                {
                    mine.Keep(at.After(reads), key);
                }
            }
        }

        /// <summary>The reasons, the commonest first, and of as common ones the one met first.</summary>
        public IEnumerable<ReasonTally> Ordered() => _reasons.Values.OrderByDescending(r => r.Count).ThenBy(r => r.First);
    }
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
/// start at the same records read them once. A key that names no record, or one by a reference no query can carry, a record
/// the search does not hold, and one whose path holds nothing, have no label (or no such attribute), and the result says how
/// many and why.
/// </summary>
/// <remarks>
/// <para>
/// What a read holds is bounded however many keys it is given. The keys are read in passes of at most
/// <see cref="MaxKeysPerPass"/>, each pass reading every step of its keys before the next begins, so what a key reads never
/// depends on another pass. A record is read as its search answers: each path the step reads of it is read there (with the
/// records its filters look up), and the record itself is let go, so a pass holds what its paths read of its records, never
/// the records. A build reads a dimension's keys a batch at a time, <see cref="PassesAtOnce"/> batches side by side, and
/// adds the batches up in the order of their keys (<see cref="LabelTally"/>); a key reads the same in any batch.
/// </para>
/// <para>
/// A pass holds the references its keys follow from one step to the next, at most <see cref="MaxReachedPerPass"/> of them.
/// How many keys that is depends on the records, not only on the declaration: a wellbore names one well although its label
/// may follow twenty. The first pass for a label and attributes reads as many keys as their declaration bounds
/// (<see cref="PassSize"/>); every later pass as many as the references the passes before it followed a key say fit, with
/// room to spare (<see cref="PassSizeFor"/>). A pass whose keys reach more is let go the moment they do, and its keys are read
/// again in passes half its size, so a pass never holds more however its records differ from those before. Searches stay
/// full that way: a pass of thousands of keys asks for each record type five hundred ids a search, where a pass sized for
/// the worst a declaration allows asked a few ids each.
/// </para>
/// <para>
/// A record a later step reaches (a wellbore's field, a field's country) is usually reached by many keys, so what each path
/// read of it, or that the search does not hold it, is kept between passes and batches, up to <see cref="CacheCapacity"/>
/// records, and so are the records a filter looked up. A key's own record is read once and never kept.
/// </para>
/// <para>
/// Every search the labeler asks, of every pass reading at once, takes one of its <c>concurrency</c> places, so passes read
/// side by side never ask more at once than one would.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim's wait handle is never accessed, so it holds no unmanaged resource to release.")]
public sealed class DimensionLabeler
{
    /// <summary>The ids one search finds: each is a clause, and the service allows 1024 in a query.</summary>
    public const int IdsPerQuery = 500;

    /// <summary>The longest label kept; a longer one is cut.</summary>
    public const int MaxLabelLength = 1024;

    /// <summary>The most keys one pass reads: a batch of a build's keys is read in one pass.</summary>
    public const int MaxKeysPerPass = 4_000;

    /// <summary>
    /// The most references a pass holds between its keys from one step to the next. A build reads <see cref="PassesAtOnce"/>
    /// passes side by side, so it holds at most twice as many.
    /// </summary>
    public const int MaxReachedPerPass = 50_000;

    /// <summary>How many batches of keys a build reads side by side, so the search is asked from one while another's answers are read.</summary>
    public const int PassesAtOnce = 2;

    /// <summary>
    /// How much room a pass sized by the references keys followed leaves: the references a key followed in the passes before
    /// it, times this, fit a pass, so a pass of keys a little more reaching than those before still fits.
    /// </summary>
    private const double Headroom = 1.25;

    /// <summary>The most records reached past a key's own, and records looked up, whose reading is kept between passes.</summary>
    public const int CacheCapacity = 50_000;

    /// <summary>The name the label's chain goes by among the attributes', which no attribute can take.</summary>
    private const string LabelChain = "label";

    /// <summary>Why a key has no label or attribute when the reference it holds cannot be asked for.</summary>
    private const string Uncarried = "the reference it holds is no text a query can carry";

    private static readonly OsduField Id = OsduField.Keyword("id");

    private readonly OsduSearch _search;
    private readonly int _concurrency;
    private readonly SemaphoreSlim _searches;
    private readonly BoundedCache<(string Path, string Id), Projection?> _reached = new(CacheCapacity);
    private readonly BoundedCache<(string Fields, string Id), JsonObject?> _lookedUp = new(CacheCapacity);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PassSizer> _sizers = new(StringComparer.Ordinal);

    /// <param name="search">The search the records are read from.</param>
    /// <param name="concurrency">
    /// The most searches asked at once, whatever passes read side by side: the records of a step are read five hundred ids a
    /// search, each on its own.
    /// </param>
    public DimensionLabeler(OsduSearch search, int concurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
        _concurrency = Math.Max(1, concurrency);
        _searches = new SemaphoreSlim(_concurrency, _concurrency);
    }

    /// <summary>The label of each of <paramref name="keys"/>, read through <paramref name="steps"/>.</summary>
    public Task<KeyLabels> LabelAsync(IReadOnlyCollection<string> keys, IReadOnlyList<string> steps, CancellationToken ct)
        => ReadAsync(keys, steps, [], null, ct);

    /// <summary>
    /// How many keys the first pass reads for <paramref name="label"/> and <paramref name="attributes"/>, before any pass has
    /// said how many references their keys follow: <see cref="MaxKeysPerPass"/>, fewer when the references their later steps
    /// may follow between that many keys, each as many as its <c>maxReferences</c> allows, are more than
    /// <see cref="MaxReachedPerPass"/>.
    /// </summary>
    public static int PassSize(IReadOnlyList<string> label, IReadOnlyList<DimensionAttributeSpec> attributes)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(attributes);
        var reached = (label.Count > 1 ? (long)DimensionAttributeSpec.DefaultMaxReferences : 0)
            + attributes.Where(a => a.Steps.Count > 1).Sum(a => (long)a.MaxReferences);
        return reached == 0 ? MaxKeysPerPass : (int)Math.Clamp(MaxReachedPerPass / reached, 1, MaxKeysPerPass);
    }

    /// <summary>
    /// How many keys this labeler's next pass reads for <paramref name="label"/> and <paramref name="attributes"/>: as many as
    /// the declaration bounds before any pass was read (<see cref="PassSize"/>); after, as many as the references the keys
    /// of the passes before followed say fit <see cref="MaxReachedPerPass"/>, with room to spare, up to
    /// <see cref="MaxKeysPerPass"/>. A build asks it for the size of each batch of keys it reads.
    /// </summary>
    public int PassSizeFor(IReadOnlyList<string> label, IReadOnlyList<DimensionAttributeSpec> attributes)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(attributes);
        return SizerOf(label, attributes).Size;
    }

    /// <summary>The sizer of the passes reading <paramref name="label"/> and <paramref name="attributes"/>, made with the declaration's bound the first time.</summary>
    private PassSizer SizerOf(IReadOnlyList<string> label, IReadOnlyList<DimensionAttributeSpec> attributes)
    {
        var declared = string.Join('\u001F', label) + '\u001E'
            + string.Join('\u001E', attributes.Where(a => a.Steps.Count > 0).Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Name}\u001F{a.MaxReferences}\u001F{string.Join('\u001F', a.Steps)}")));
        return _sizers.GetOrAdd(declared, _ => new PassSizer(PassSize(label, attributes)));
    }

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

        var labels = new Dictionary<string, KeyLabel>(chains[0].IsLabel ? keys.Count : 0, StringComparer.Ordinal);
        var values = new Dictionary<string, IReadOnlyList<DimensionAttributeState>>(keys.Count, StringComparer.Ordinal);
        var records = new Dictionary<string, IReadOnlyList<string>>(keys.Count, StringComparer.Ordinal);
        LabelTally? tally = null;
        long labelled = 0;
        long unlabelled = 0;
        var queries = 0;
        var sizer = SizerOf(label, attributes);
        var pending = keys.Distinct(StringComparer.Ordinal).ToList();

        // A pass of these keys that reached too many makes every later pass of them smaller, whatever a pass of other keys read
        // side by side says, so the keys are read however many times they are let go.
        var most = MaxKeysPerPass;
        for (var at = 0; at < pending.Count;)
        {
            var size = Math.Min(Math.Min(sizer.Size, most), pending.Count - at);
            var (read, asked) = await ReadPassAsync(pending.GetRange(at, size), chains, kinds, ct).ConfigureAwait(false);
            queries += asked;
            if (read is null)
            {
                // The pass's keys reached more references than a pass holds: they are read again in passes half its size.
                most = Math.Max(1, size / 2);
                sizer.TooMany(size);
                continue;
            }

            sizer.Read(size, read.MostReached);
            at += size;
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
            unlabelled += read.Unlabelled;
            if (tally is null)
            {
                tally = read.Tally;
            }
            else
            {
                tally.Add(read.Tally);
            }
        }

        return new KeyLabels(labels, values, labelled, unlabelled, queries, tally!.Notes(), records) { Tally = tally };
    }

    /// <summary>
    /// What one pass read of its keys, as <see cref="KeyLabels"/> holds it of all of them, and the most references its keys
    /// held between them from one step to the next.
    /// </summary>
    private sealed record PassRead(
        Dictionary<string, KeyLabel> Labels, Dictionary<string, IReadOnlyList<DimensionAttributeState>> Attributes, long Labelled, long Unlabelled,
        Dictionary<string, IReadOnlyList<string>> Records, LabelTally Tally, long MostReached);

    /// <summary>
    /// Reads every step of every chain for <paramref name="keys"/>, in order: each step reads the records the keys have reached,
    /// then each key goes on to the references they hold, or stops with its value or why it has none. A pass of more than one
    /// key whose keys go on to more than <see cref="MaxReachedPerPass"/> references between them stops there and reads nothing
    /// (null), so it never holds more; one key alone holds what its declaration lets it follow. Returns the searches asked
    /// either way.
    /// </summary>
    private async Task<(PassRead? Read, int Queries)> ReadPassAsync(IReadOnlyList<string> keys, List<Chain> chains, IReadOnlyDictionary<string, string> kinds, CancellationToken ct)
    {
        var place = new Dictionary<string, int>(keys.Count, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            place.TryAdd(key, place.Count);
        }

        // Where each key has got to in each chain: the records the next step reads (every one a step before reached, in the
        // order it reached them), or why it stopped. A reference no query can carry cannot be asked for, so the key stops.
        var start = new Dictionary<string, (IReadOnlyList<string> References, string? Problem)>(keys.Count, StringComparer.Ordinal);
        foreach (var key in place.Keys)
        {
            var trimmed = key.Trim();
            start[key] = !TargetId.IsRecordReference(trimmed) ? ([], "it names no OSDU record")
                : Carried(TargetId.WithoutVersion(trimmed)) ? ([TargetId.WithoutVersion(trimmed)], null)
                : ([], Uncarried);
        }

        var reached = chains.Select(_ => new Dictionary<string, (IReadOnlyList<string> References, string? Problem)>(start, StringComparer.Ordinal)).ToList();
        var found = chains.Select(_ => new Dictionary<string, KeyLabel>(keys.Count, StringComparer.Ordinal)).ToList();
        var readThrough = new Dictionary<string, List<string>>(keys.Count, StringComparer.Ordinal);
        var tally = LabelTally.For(chains.Select(c => (c.Name, c.IsLabel, c.MaxReferences)));
        var queries = 0;
        long mostReached = 0;

        void Stop(int chain, string key, KeyLabel label, int step)
        {
            found[chain][key] = label;
            if (label.Label is null)
            {
                tally.Lacks(chain, Reason(label.Problem!), step, place[key], key);
            }
        }

        for (var step = 0; step < chains.Max(c => c.Steps.Count); step++)
        {
            var at = step;
            var active = Enumerable.Range(0, chains.Count).Where(c => chains[c].Steps.Count > at).ToList();

            // What this step reads: each path, with the records it is read in. A path two chains read is read once.
            var needed = new Dictionary<string, (DimensionPath Path, HashSet<string> Ids)>(StringComparer.Ordinal);
            foreach (var c in active)
            {
                var path = chains[c].Paths[at];
                if (!needed.TryGetValue(path.Text, out var need))
                {
                    need = (path, new HashSet<string>(StringComparer.Ordinal));
                    needed[path.Text] = need;
                }

                foreach (var (key, (references, _)) in reached[c])
                {
                    var through = readThrough.TryGetValue(key, out var listed) ? listed : readThrough[key] = [];
                    foreach (var id in references)
                    {
                        if (!through.Contains(id, StringComparer.Ordinal))
                        {
                            through.Add(id);
                        }

                        need.Ids.Add(id);
                    }
                }
            }

            var (read, asked) = await ProjectAsync(needed, at, kinds, ct).ConfigureAwait(false);
            queries += asked;

            // A filter through a reference compares what the record each reference names holds: a key is read through those
            // records too.
            foreach (var c in active.Where(c => chains[c].Paths[at].HasLookups))
            {
                var path = chains[c].Paths[at];
                foreach (var (key, (references, _)) in reached[c])
                {
                    var through = readThrough[key];
                    foreach (var id in references)
                    {
                        if (read.TryGetValue((path.Text, id), out var projected))
                        {
                            foreach (var named in projected.LookedUp)
                            {
                                if (!through.Contains(named, StringComparer.Ordinal))
                                {
                                    through.Add(named);
                                }
                            }
                        }
                    }
                }
            }

            // The references the keys go on to, between them: a pass holds them until the next step has read them.
            long holding = 0;
            foreach (var c in active)
            {
                var chain = chains[c];
                var last = at == chain.Steps.Count - 1;
                var path = chain.Paths[at];
                var where = reached[c];
                foreach (var key in where.Keys.ToList())
                {
                    var (references, problem) = where[key];
                    if (references.Count == 0)
                    {
                        Stop(c, key, new KeyLabel(null, null, problem), at);
                        where.Remove(key);
                        continue;
                    }

                    var held = references.Where(id => read.ContainsKey((path.Text, id))).ToList();
                    if (held.Count == 0)
                    {
                        Stop(c, key, new KeyLabel(null, null, $"the search holds no record {references[0]}"), at);
                        where.Remove(key);
                        continue;
                    }

                    if (last)
                    {
                        Stop(c, key, Value(chain, path, held, read, tally), at);
                        where.Remove(key);
                        continue;
                    }

                    // Every reference the records reached hold at the path, in order, each once: the next step reads them all,
                    // up to as many as the chain follows, and a key naming more is counted for a note. A reference no query
                    // can carry cannot be read, so it is passed over.
                    var named = References(held.SelectMany(id => read[(path.Text, id)].Values)).Distinct(StringComparer.Ordinal).ToList();
                    var next = named.Where(Carried).ToList();
                    if (next.Count > chain.MaxReferences)
                    {
                        tally.Bounded(c);
                        next = next.Take(chain.MaxReferences).ToList();
                    }

                    where[key] = next.Count > 0 ? (next, null)
                        : named.Count > 0 ? ([], $"record {held[0]} holds only references no query can carry at {path.Text}")
                        : ([], $"record {held[0]} holds no record reference at {path.Text}");
                    holding += next.Count;
                    if (holding > MaxReachedPerPass && keys.Count > 1)
                    {
                        return (null, queries);
                    }
                }
            }

            mostReached = Math.Max(mostReached, holding);
        }

        var labels = chains[0].IsLabel ? found[0] : new Dictionary<string, KeyLabel>(StringComparer.Ordinal);
        var unlabelled = labels.Values.LongCount(l => l.Label is null);
        var attributes = new Dictionary<string, IReadOnlyList<DimensionAttributeState>>(keys.Count, StringComparer.Ordinal);
        for (var c = 0; c < chains.Count; c++)
        {
            if (chains[c].IsLabel)
            {
                continue;
            }

            foreach (var (key, value) in found[c])
            {
                if (value.Label is not null)
                {
                    var listed = attributes.TryGetValue(key, out var list) ? (List<DimensionAttributeState>)list : [];
                    listed.Add(new DimensionAttributeState(chains[c].Name, value.Label, value.From));
                    attributes[key] = listed;
                }
            }
        }

        var records = readThrough.ToDictionary(a => a.Key, a => (IReadOnlyList<string>)a.Value, StringComparer.Ordinal);
        return (new PassRead(labels, attributes, labels.Count - unlabelled, unlabelled, records, tally, mostReached), queries);
    }

    /// <summary>
    /// The value a chain's last step gives a key from the records it reached (<paramref name="held"/>, in the order reached):
    /// the first that holds one, or every one joined, or the greatest, kept as the chain keeps it; or why it has none.
    /// </summary>
    private static KeyLabel Value(Chain chain, DimensionPath path, List<string> held, Dictionary<(string Path, string Id), Projection> read, LabelTally tally)
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
                var value = read[(path.Text, id)].Values.FirstOrDefault(v => DimensionKeeping.IsValue(v, chain.Keep, chain.Trim));
                if (value is null)
                {
                    continue;
                }

                var (kept, wasCut, tooLong) = DimensionKeeping.Keep(DimensionKeeping.Decoded(value, chain.Decode), chain.Keep, longest, chain.Trim);
                over |= tooLong;
                if (kept is not null)
                {
                    (text, from) = (kept, id);
                    if (wasCut)
                    {
                        tally.CutOne();
                    }

                    break;
                }
            }
        }
        else
        {
            // Every value of every record reached, in the order they were reached: joined, or the greatest.
            var values = held.SelectMany(id => read[(path.Text, id)].Values.Select(v => (Id: id, Text: v))).ToList();
            var (kept, chosen, wasCut, tooLong) = DimensionKeeping.KeepMany(
                values.Select(v => v.Text).ToList(), chain.Keep, chain.Many, chain.Decode, longest, chain.Trim);
            over = tooLong;
            if (kept is not null)
            {
                (text, from) = (kept, values[chosen].Id);
                if (wasCut)
                {
                    tally.CutOne();
                }
            }
        }

        if (text is null && over)
        {
            tally.UncutOne();
        }

        return text is not null
            ? new KeyLabel(text, from, null)
            : new KeyLabel(null, held[0], over
                ? $"record {held[0]} holds more than the {longest} characters a key kept whole allows at {path.Text}"
                : $"record {held[0]} holds nothing at {path.Text}");
    }

    /// <summary>What a path read of one record: the values it reaches there, and the records its filters look up through it.</summary>
    private sealed record Projection(List<string> Values, List<string> LookedUp);

    /// <summary>
    /// What each path of <paramref name="needed"/> reads of each record it is read in, by the path's text and the record's id;
    /// a record the search does not hold has none. The records are asked for by entity type, <see cref="IdsPerQuery"/> ids a
    /// search, several searches at once; each answer is read as it comes (the records its filters look up asked for then) and
    /// let go. A step past the first reads what it can from what earlier passes read, and keeps what it reads for later ones.
    /// Returns the readings and the searches asked.
    /// </summary>
    private async Task<(Dictionary<(string Path, string Id), Projection> Read, int Queries)> ProjectAsync(
        Dictionary<string, (DimensionPath Path, HashSet<string> Ids)> needed, int step, IReadOnlyDictionary<string, string> kinds, CancellationToken ct)
    {
        var read = new Dictionary<(string Path, string Id), Projection>();
        var paths = new Dictionary<string, List<DimensionPath>>(StringComparer.Ordinal);
        var wanted = new Dictionary<string, (HashSet<string> Ids, HashSet<string> Fields)>(StringComparer.Ordinal);
        foreach (var (text, (path, ids)) in needed)
        {
            foreach (var id in ids)
            {
                if (step > 0 && _reached.TryGet((text, id), out var known))
                {
                    if (known is not null)
                    {
                        read[(text, id)] = known;
                    }

                    continue;
                }

                (paths.TryGetValue(id, out var of) ? of : paths[id] = []).Add(path);
                var type = EntityTypeOf(id);
                if (!wanted.TryGetValue(type, out var need))
                {
                    need = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                    wanted[type] = need;
                }

                need.Ids.Add(id);
                need.Fields.UnionWith(path.ReturnedFields);
            }
        }

        var asks = new List<(string Kind, List<string> Fields, string[] Ids)>();
        foreach (var (type, need) in wanted)
        {
            var fields = new List<string>(need.Fields.Count + 1) { "id" };
            fields.AddRange(need.Fields.Order(StringComparer.Ordinal));
            var kind = kinds.TryGetValue(type, out var pinned) ? pinned : $"*:*:{type}:*";
            asks.AddRange(need.Ids.Order(StringComparer.Ordinal).Chunk(IdsPerQuery).Select(chunk => (kind, fields, chunk)));
        }

        var queries = asks.Count;
        var gate = new Lock();
        await Parallel.ForEachAsync(
            asks,
            new ParallelOptions { MaxDegreeOfParallelism = _concurrency, CancellationToken = ct },
            async (ask, token) =>
            {
                var hits = await FindAsync(ask.Kind, ask.Ids, ask.Fields, token).ConfigureAwait(false);
                var records = new Dictionary<string, JsonObject>(hits.Count, StringComparer.Ordinal);
                foreach (var hit in hits)
                {
                    if (hit["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id) && paths.ContainsKey(id))
                    {
                        records[id] = hit;
                    }
                }

                var (looked, lookups) = await LookUpAsync(records, paths, kinds, token).ConfigureAwait(false);
                JsonObject? Lookup(string id) => looked.TryGetValue(id, out var record) ? record : null;
                var projected = new List<(string Path, string Id, Projection? Read)>(ask.Ids.Length);
                foreach (var id in ask.Ids)
                {
                    foreach (var path in paths[id])
                    {
                        projected.Add((path.Text, id, records.TryGetValue(id, out var record)
                            ? new Projection(path.Read(record, Lookup), path.HasLookups ? Referenced(record, path) : [])
                            : null));
                    }
                }

                lock (gate)
                {
                    queries += lookups;
                    foreach (var (text, id, projection) in projected)
                    {
                        if (projection is not null)
                        {
                            read[(text, id)] = projection;
                        }

                        if (step > 0)
                        {
                            _reached.Set((text, id), projection);
                        }
                    }
                }
            }).ConfigureAwait(false);

        return (read, queries);
    }

    /// <summary>
    /// The records the filters through a reference of the paths read look up from <paramref name="records"/>, by id, each
    /// holding only what the filters read of it; one an earlier answer looked up is not asked for again. Returns them, and
    /// the searches asked.
    /// </summary>
    private async Task<(Dictionary<string, JsonObject> Looked, int Queries)> LookUpAsync(
        Dictionary<string, JsonObject> records, Dictionary<string, List<DimensionPath>> paths, IReadOnlyDictionary<string, string> kinds, CancellationToken ct)
    {
        var wanted = new Dictionary<string, (HashSet<string> Ids, HashSet<string> Fields)>(StringComparer.Ordinal);
        foreach (var (id, record) in records)
        {
            foreach (var path in paths[id].Where(p => p.HasLookups))
            {
                foreach (var (referencesAt, readThere) in path.Lookups)
                {
                    foreach (var named in References(referencesAt.Read(record)).Where(Carried))
                    {
                        var type = EntityTypeOf(named);
                        if (!wanted.TryGetValue(type, out var need))
                        {
                            need = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                            wanted[type] = need;
                        }

                        need.Ids.Add(named);
                        need.Fields.UnionWith(readThere.ReturnedFields);
                    }
                }
            }
        }

        var looked = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var queries = 0;
        foreach (var (type, need) in wanted)
        {
            var fields = new List<string>(need.Fields.Count + 1) { "id" };
            fields.AddRange(need.Fields.Order(StringComparer.Ordinal));
            var signature = string.Join(',', fields);
            var missing = new List<string>();
            foreach (var id in need.Ids.Order(StringComparer.Ordinal))
            {
                if (_lookedUp.TryGet((signature, id), out var known))
                {
                    if (known is not null)
                    {
                        looked[id] = known;
                    }
                }
                else
                {
                    missing.Add(id);
                }
            }

            var kind = kinds.TryGetValue(type, out var pinned) ? pinned : $"*:*:{type}:*";
            foreach (var chunk in missing.Chunk(IdsPerQuery))
            {
                queries++;
                var hits = await FindAsync(kind, chunk, fields, ct).ConfigureAwait(false);
                var answered = new HashSet<string>(StringComparer.Ordinal);
                foreach (var hit in hits)
                {
                    if (hit["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id))
                    {
                        looked[id] = hit;
                        answered.Add(id);
                        _lookedUp.Set((signature, id), hit);
                    }
                }

                foreach (var id in chunk.Where(i => !answered.Contains(i)))
                {
                    _lookedUp.Set((signature, id), null);
                }
            }
        }

        return (looked, queries);
    }

    /// <summary>
    /// The records of <paramref name="kind"/> among <paramref name="ids"/>, each holding <paramref name="fields"/>: one search,
    /// asked once one of the labeler's places is free, so every pass reading at once shares them.
    /// </summary>
    private async Task<IReadOnlyList<JsonObject>> FindAsync(string kind, string[] ids, List<string> fields, CancellationToken ct)
    {
        await _searches.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (_, hits) = await _search.FirstAsync(
                new OsduSearchQuery { Kind = kind, Query = OsduQuery.AnyOf(Id, ids).Text, ReturnedFields = fields }, OsduSearch.MaxPage, ct).ConfigureAwait(false);
            return hits;
        }
        finally
        {
            _searches.Release();
        }
    }

    /// <summary>
    /// How many keys a pass reads for one label and set of attributes: the declaration's bound until a pass was read, then as
    /// many as the references the keys of the last pass read followed say fit a pass, with <see cref="Headroom"/> to spare;
    /// half the size of a pass whose keys reached too many. Passes read side by side each say what they found, and the last to
    /// say it sizes the next.
    /// </summary>
    private sealed class PassSizer(int first)
    {
        private int _size = Math.Clamp(first, 1, MaxKeysPerPass);

        /// <summary>The keys the next pass reads.</summary>
        public int Size => Volatile.Read(ref _size);

        /// <summary>A pass of <paramref name="keys"/> keys was read, its keys holding at most <paramref name="reached"/> references between them.</summary>
        public void Read(int keys, long reached)
        {
            if (keys < 1)
            {
                return;
            }

            var perKey = (double)reached / keys;
            var fits = perKey <= 0 ? MaxKeysPerPass : MaxReachedPerPass / (perKey * Headroom);
            Volatile.Write(ref _size, (int)Math.Clamp(Math.Floor(fits), 1, MaxKeysPerPass));
        }

        /// <summary>A pass of <paramref name="keys"/> keys reached more references than a pass holds: the next reads half as many.</summary>
        public void TooMany(int keys) => Volatile.Write(ref _size, Math.Max(1, keys / 2));
    }

    /// <summary>The records the filters through a reference of <paramref name="path"/> look up from <paramref name="record"/>, in order.</summary>
    private static List<string> Referenced(JsonObject record, DimensionPath path)
        => path.Lookups.SelectMany(l => References(l.References.Read(record))).ToList();

    /// <summary>The record references among <paramref name="texts"/>, each without its version, in order.</summary>
    private static IEnumerable<string> References(IEnumerable<string> texts)
        => texts.Select(v => v.Trim()).Where(TargetId.IsRecordReference).Select(TargetId.WithoutVersion);

    /// <summary>Whether a search can be asked for the record <paramref name="id"/> names: a query can carry the id.</summary>
    private static bool Carried(string id) => OsduQuery.EqualProblem(Id, id) is null;

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
            : problem.StartsWith("record ", StringComparison.Ordinal) && problem.Contains(" no query can carry ", StringComparison.Ordinal)
                ? "the record they name holds only references no query can carry"
            : problem.StartsWith("record ", StringComparison.Ordinal) ? "the record they name holds no reference where one is read"
            : problem;

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
