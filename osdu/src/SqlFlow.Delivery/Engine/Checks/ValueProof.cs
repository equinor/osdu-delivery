using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>
/// What a proof of a finding is asked for (osdu/docs/reference/concepts/value-proof.md): the attribute, what the finding comes
/// from and the values behind it, as the check that found it answered them, and the step to run, if any.
/// </summary>
public sealed record ValueProofRequest
{
    /// <summary>The attribute, as a template path.</summary>
    public required string Target { get; init; }

    /// <summary>What the finding comes from (<see cref="ValueCheckFinding.Causes"/>, the first).</summary>
    public required ValueCheckCause Cause { get; init; }

    /// <summary>What the source columns behind it hold in its rows (<see cref="ValueCheckFinding.SourceValues"/>).</summary>
    public IReadOnlyList<ValueCheckSourceValue> SourceValues { get; init; } = [];

    /// <summary>The values as the render compared them (<see cref="ValueCheckFinding.Values"/>): ids, codes, names.</summary>
    public IReadOnlyList<string> Looked { get; init; } = [];

    /// <summary>The cache version the check rendered with (<see cref="ValueCheckInputs.CacheVersion"/>).</summary>
    public string? CacheVersion { get; init; }

    /// <summary>The step to run (<see cref="ValueProofStep.Step"/>); null writes the steps and runs none.</summary>
    public string? Run { get; init; }

    /// <summary>The most rows a step run lists.</summary>
    public int Limit { get; init; } = ValueProver.DefaultLimit;
}

/// <summary>
/// The proof of a finding: the steps that show it from the data, each a statement a person runs, and which of them ran.
/// </summary>
/// <param name="Flow">The flow whose rows it reads.</param>
/// <param name="Interface">The interface of the flow.</param>
/// <param name="Mapping">The mapping the attribute is of.</param>
/// <param name="Target">The attribute.</param>
/// <param name="Steps">The steps, in the order to read them.</param>
/// <param name="Notes">What the proof leaves out, and why.</param>
public sealed record ValueProof(string Flow, string? Interface, string Mapping, string Target, IReadOnlyList<ValueProofStep> Steps, IReadOnlyList<string> Notes);

/// <summary>One step of a proof: what it shows, where it runs, the statement, and what it read when it was run.</summary>
/// <param name="Step">What it is: <c>values</c>, <c>records</c>, <c>cache</c> or <c>search</c>.</param>
/// <param name="Title">What it shows, in a few words.</param>
/// <param name="Proves">How to read what it answers.</param>
/// <param name="RunsOn">Where it runs: <c>source</c> (the flow's own tables), <c>catalog</c> (the cache) or <c>platform</c> (OSDU's search).</param>
/// <param name="Where">What it reads there, by name.</param>
/// <param name="Language">What the statement is written in: <c>sql</c>, or <c>json</c> for a search request.</param>
/// <param name="Statement">The statement, as a person copies it: T-SQL with its values declared, or a search request.</param>
/// <param name="Runnable">Whether the page can run it.</param>
/// <param name="Result">What it read, when it was run.</param>
public sealed record ValueProofStep(
    string Step, string Title, string Proves, string RunsOn, string Where, string Language, string Statement, bool Runnable, ProofRows? Result = null);

/// <summary>
/// Writes, and runs on request, the proof of a value check's finding: where the source holds the values behind it, the records
/// to fix by their keys, and what the cache (or the platform's search) holds of what was looked for. Each step is one read-only
/// statement, the one a person copies and the one run here, its values bound as parameters; nothing is written anywhere.
/// </summary>
public sealed class ValueProver
{
    /// <summary>The rows a step run lists unless asked for another number.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The most rows a step run lists.</summary>
    public const int MaxLimit = 1000;

    /// <summary>The rows a script lists, which the person running it may change.</summary>
    public const long ScriptRows = 100_000;

    /// <summary>The source values, and the values looked for, one request carries at most.</summary>
    public const int MaxValues = 500;

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions Indented = new(Readable) { WriteIndented = true };

    private readonly FlowRuntime _runtime;

    public ValueProver(FlowRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>The proof of the finding <paramref name="request"/> describes, with the step it names run.</summary>
    public async Task<ValueProof> ProveAsync(ValueProofRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flow = _runtime.Flow;
        var mapping = _runtime.Mapping.Mapping;
        if (!TemplatePath.TryParse(request.Target, out var target, out var error))
        {
            throw new DeliveryException($"'{request.Target}' is not an attribute a proof can name: {error}.");
        }

        if (request.Limit is < 1 or > MaxLimit)
        {
            throw new DeliveryException($"A proof lists between 1 and {MaxLimit} rows; {request.Limit} is outside that.");
        }

        if (request.SourceValues.Count > MaxValues || request.Looked.Count > MaxValues)
        {
            throw new DeliveryException($"A proof looks for at most {MaxValues} values.");
        }

        var cause = request.Cause;
        var reads = Reads(mapping, target!);
        var columns = cause.Columns.Select(text => Column(text, reads, target!.Text)).ToList();
        var notes = new List<string>();
        var steps = new List<ValueProofStep>();

        var source = await SourceStepsAsync(request, cause, columns, notes, ct).ConfigureAwait(false);
        steps.AddRange(source);
        if (CacheStep(request, cause, notes) is { } cache)
        {
            steps.Add(cache);
        }

        if (cause.Kind == ValueCheckCause.NotFound && SearchStep(request, cause, notes) is { } search)
        {
            steps.Add(search);
        }

        if (request.Run is { } run && !steps.Any(s => s.Step == run && s.Runnable))
        {
            throw new DeliveryException($"This proof has no step '{run}' to run; it has {string.Join(", ", steps.Where(s => s.Runnable).Select(s => s.Step))}.");
        }

        var ran = new List<ValueProofStep>(steps.Count);
        foreach (var step in steps)
        {
            ran.Add(step.Step == request.Run ? step with { Result = await RunAsync(step.Step, request, cause, columns, ct).ConfigureAwait(false) } : step);
        }

        return new ValueProof(flow.Label, flow.Interface, mapping.Reference, target!.Text, ran, notes);
    }

    /// <summary>The steps that read the flow's own tables: the values behind the finding and the records holding them.</summary>
    private async Task<IReadOnlyList<ValueProofStep>> SourceStepsAsync(
        ValueProofRequest request, ValueCheckCause cause, IReadOnlyList<DatasetColumn> columns, List<string> notes, CancellationToken ct)
    {
        if (columns.Count == 0)
        {
            notes.Add("Nothing in the source is behind this finding: the mapping, the cache or the platform is what to look at.");
            return [];
        }

        var empty = cause.Kind == ValueCheckCause.Empty;
        if (!empty && cause.Kind is ValueCheckCause.NoType or ValueCheckCause.FieldEmpty)
        {
            return [];
        }

        var description = await _runtime.Source.DescribeAsync(ct).ConfigureAwait(false);
        var (read, values) = ReadOf(description, columns, request.SourceValues, empty, notes);
        if (read is null)
        {
            return [];
        }

        var shown = string.Join(", ", read.Columns.Select(c => c.Shown));
        var where = (read.Child?.Object ?? description.Layout.Record).ToString();
        var title = empty ? "How many rows leave it empty" : "Where the source holds these values";
        var proves = empty
            ? $"How many rows of the scope a delivery reads leave {shown} empty: these are the rows to fill."
            : $"Each value the check met in {shown}, with how many rows of the scope a delivery reads hold it: these are the rows to fix.";
        var counts = new ValueProofStep(
            "values", title, proves, "source", where, "sql", SourceScript(_runtime.Flow.Label, title, proves, description, read, values, IngestionSql.ProofValues(description.Layout, read)), Runnable: true);
        var recordsProves = $"Each row to fix by its record key, with what it holds in {shown} and the file and row it was landed from.";
        var records = new ValueProofStep(
            "records", "The records to fix", recordsProves, "source", where, "sql",
            SourceScript(_runtime.Flow.Label, "The records to fix", recordsProves, description, read, values, IngestionSql.ProofRecords(description.Layout, read)), Runnable: true);
        return [counts, records];
    }

    /// <summary>
    /// What a proof of <paramref name="columns"/> reads: those of one table (the record table, or the one child dataset the first
    /// names), each with the values the check met in it, or with none for empty columns. Null when the check met no value to
    /// look for.
    /// </summary>
    internal static (ProofRead? Read, IReadOnlyList<IReadOnlyList<string>> Values) ReadOf(
        SourceDescription description, IReadOnlyList<DatasetColumn> columns, IReadOnlyList<ValueCheckSourceValue> held, bool empty, List<string> notes)
    {
        var child = columns[0].Child;
        var mine = columns.Where(c => string.Equals(c.Child, child, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count < columns.Count)
        {
            notes.Add($"The proof reads {string.Join(", ", mine)}; {string.Join(", ", columns.Except(mine))} {(columns.Count - mine.Count == 1 ? "is" : "are")} of another table and left out of it.");
        }

        var dataset = child is null ? null : description.Layout.Datasets.FirstOrDefault(d => string.Equals(d.Name, child, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeliveryException($"The flow reads no child dataset '{child}'.");
        var table = dataset?.Name ?? SourceDatasets.Record;
        var named = description.Columns.GetValueOrDefault(table) ?? [];
        var proofColumns = new List<ProofColumn>();
        var values = new List<IReadOnlyList<string>>();
        foreach (var column in mine)
        {
            var name = named.FirstOrDefault(n => n.Equals(column.Column, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"{column} is read by the mapping, and {(dataset?.Object ?? description.Layout.Record)} holds no column '{column.Column}'.");
            var mineHeld = held.Where(v => string.Equals(v.Column, column.ToString(), StringComparison.OrdinalIgnoreCase)).Select(v => v.Value).Distinct(StringComparer.Ordinal).ToList();
            if (!empty && mineHeld.Count == 0)
            {
                continue;
            }

            proofColumns.Add(new ProofColumn(name, description.TypeOf(table, name), column.ToString()));
            values.Add(empty ? [] : mineHeld);
        }

        if (proofColumns.Count == 0)
        {
            notes.Add("The check met no value in the source columns behind this finding, so there is nothing to look for there.");
            return (null, []);
        }

        // Where a row came from is said by the record table, and for a child row by its own table, when they carry the columns.
        var recordColumns = description.Columns.GetValueOrDefault(SourceDatasets.Record) ?? [];
        var origin = description.Origin.Where(o => recordColumns.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
        var childOrigin = dataset is null ? [] : description.Origin.Where(o => named.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList();
        return (new ProofRead(proofColumns, dataset, empty, origin, childOrigin), values);
    }

    /// <summary>The step that reads the partition's cache, or null when the finding is not about the cache or the cache is not one SQL reads.</summary>
    private ValueProofStep? CacheStep(ValueProofRequest request, ValueCheckCause cause, List<string> notes)
    {
        if (cause.Together && cause.Kind == ValueCheckCause.NoMatch)
        {
            // The cache step counts the records holding each value at the fields compared, and 0 proves none holds it. Values
            // a line compared together are each held by some record as often as not; what is missing is a record holding
            // them all, which a count of each does not show, so no step claims to prove it.
            notes.Add(
                $"The values were looked up together, on a findBy line comparing {string.Join(" and ", cause.Fields)}, so the proof has no step of the cache: a count of the records holding each value would not show that none holds them all.");
            return null;
        }

        if (CacheQuery(request, cause, ScriptRows) is not { } query)
        {
            if (cause.Kind is ValueCheckCause.IdNotHeld or ValueCheckCause.NoMatch or ValueCheckCause.Ambiguous or ValueCheckCause.NoType or ValueCheckCause.FieldEmpty)
            {
                notes.Add(_runtime.Mapping.Context.CacheScope is null
                    ? "The flow renders against a cache held in files, which no SQL reads, so the proof has no step of the cache."
                    : "The check named no cache version, so the proof has no step of the cache; check the attribute again.");
            }

            return null;
        }

        var type = string.Join(", ", query.Types);
        var (title, proves) = query.Kind switch
        {
            CacheProofKind.Ids => (
                "What the cache holds of these ids",
                $"For each id: how many {type} records the cache holds under it (0 proves it holds none), and, as records like it, those whose id differs only by case or that hold the code the id ends with, which may be the one meant."),
            CacheProofKind.Values => (
                "What the cache holds of these values",
                $"For each value: how many {type} records hold it at {string.Join(", ", query.Fields)} (0 proves the cache has none), how many once case is ignored, and the first records holding it in any field, case ignored."),
            CacheProofKind.Records when cause.Kind == ValueCheckCause.Ambiguous => (
                "The records the value matches",
                "Every record answering to the value, with what the cache holds of each: the source has to name one of them exactly."),
            CacheProofKind.Records => (
                "The record found",
                $"What the cache holds of the record found: {string.Join(", ", cause.Fields)} is not among its fields, or holds nothing."),
            _ => (
                "The types the cache holds",
                $"Every type the version holds, with its records: {type} is not among them."),
        };
        var script = new StringBuilder();
        Comment(script, $"{title}: {proves}");
        Comment(script, $"Runs on the catalog database, in version {query.Version} of the cache of partition {query.Scope}.");
        script.Append(CacheProofSql.Script(query, ScriptRows));
        return new ValueProofStep("cache", title, proves, "catalog", "osdu.CacheItem", "sql", script.ToString(), Runnable: _runtime.Context.Cache is not null);
    }

    /// <summary>What the cache step reads, or null when the finding is not about the cache or no version of it can be read.</summary>
    private CacheProofQuery? CacheQuery(ValueProofRequest request, ValueCheckCause cause, long limit)
    {
        if (_runtime.Mapping.Context.CacheScope is not { } scope || request.CacheVersion is not { Length: > 0 } version)
        {
            return null;
        }

        var kind = cause.Kind switch
        {
            ValueCheckCause.IdNotHeld => CacheProofKind.Ids,
            ValueCheckCause.NoMatch => CacheProofKind.Values,
            ValueCheckCause.Ambiguous or ValueCheckCause.FieldEmpty => CacheProofKind.Records,
            ValueCheckCause.NoType => CacheProofKind.Types,
            _ => (CacheProofKind?)null,
        };
        if (kind is not { } asked)
        {
            return null;
        }

        var looked = asked == CacheProofKind.Records ? cause.Candidates : request.Looked;
        return new CacheProofQuery
        {
            Kind = asked,
            Scope = scope,
            Version = version,
            Types = cause.Types,
            Fields = cause.Fields,
            Looked = looked.Take(MaxValues).ToList(),
            Limit = (int)Math.Min(limit, int.MaxValue),
        };
    }

    /// <summary>The search the render asked of the platform, as a request a person sends; null when the search cannot be told.</summary>
    private ValueProofStep? SearchStep(ValueProofRequest request, ValueCheckCause cause, List<string> notes)
    {
        var name = cause.Types.Count > 0 ? cause.Types[0] : null;
        var searches = _runtime.Mapping.Renderer.Searches;
        var value = request.Looked.Count > 0 ? request.Looked[0] : null;
        if (name is null || value is null)
        {
            return null;
        }

        var queries = new List<string>();
        string? kind = null;
        foreach (var path in cause.Fields)
        {
            if (searches.TryField(name, path, out var search, out var field))
            {
                kind = search!.Declaration.Kind;
                try
                {
                    queries.Add(OsduQuery.Equal(field!, value).Text);
                }
                catch (OsduQueryException ex)
                {
                    notes.Add($"{path} '{value}' cannot be asked of the search: {ex.Message}");
                }
            }
        }

        if (kind is null || queries.Count == 0)
        {
            return null;
        }

        var body = JsonSerializer.Serialize(new { kind, query = string.Join(" OR ", queries.Select(q => $"({q})")), limit = 10 }, Indented);
        if (request.Looked.Count > 1)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"The search asks for {value}, the first of the {request.Looked.Count} values; change the query for another."));
        }

        return new ValueProofStep(
            "search",
            "The search the render asked",
            "The request the platform's search was sent for the first value, as the flow's identity sends it: POST it to the search API's query route, where an empty answer proves the platform holds no such record the flow may read.",
            "platform",
            kind,
            "json",
            body,
            Runnable: false);
    }

    /// <summary>Runs the step named, as the statement it shows.</summary>
    private async Task<ProofRows> RunAsync(string step, ValueProofRequest request, ValueCheckCause cause, IReadOnlyList<DatasetColumn> columns, CancellationToken ct)
    {
        if (step == "cache")
        {
            var cache = _runtime.Context.Cache ?? throw new DeliveryException("This process holds no cache store, so the cache step cannot be run here; copy it into SSMS.");
            var query = CacheQuery(request, cause, request.Limit)!;
            return await cache.ReadProofAsync(query, ct).ConfigureAwait(false);
        }

        var notes = new List<string>();
        var description = await _runtime.Source.DescribeAsync(ct).ConfigureAwait(false);
        var (read, values) = ReadOf(description, columns, request.SourceValues, cause.Kind == ValueCheckCause.Empty, notes);
        if (read is null)
        {
            throw new DeliveryException($"The step '{step}' reads nothing: {string.Join(" ", notes)}");
        }

        return await _runtime.Source.ReadProofAsync(
            new SourceProofQuery { Read = read, Records = step == "records", Values = values, Limit = request.Limit }, ct).ConfigureAwait(false);
    }

    /// <summary>A source statement as a person runs it: what it shows, the scope's values, the rows to list and the values looked for, declared.</summary>
    internal static string SourceScript(string flow, string title, string proves, SourceDescription source, ProofRead read, IReadOnlyList<IReadOnlyList<string>> values, string statement)
    {
        var script = new StringBuilder();
        Comment(script, $"{title}: {proves}");
        Comment(script, $"Runs on the flow's source, {flow}: the rows a delivery reads in this scope, as the tables hold them now.");
        SourceQuery.DeclareScope(script, source);
        if (statement.Contains(IngestionSql.ProofRowsParameter, StringComparison.Ordinal))
        {
            Comment(script, "The most rows to list.");
            script.Append("DECLARE ").Append(IngestionSql.ProofRowsParameter).Append(" bigint = ").Append(ScriptRows.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
        }

        if (!read.Empty)
        {
            for (var i = 0; i < read.Columns.Count; i++)
            {
                Comment(script, $"The values the check met in {read.Columns[i].Shown}.");
                script.Append("DECLARE ").Append(IngestionSql.ProofValuesParameter(i)).Append(" nvarchar(max) = ")
                    .Append(IngestionSql.Literal(JsonSerializer.Serialize(values[i], Readable))).AppendLine(";");
            }
        }

        script.AppendLine();
        script.Append(statement);
        return script.ToString();
    }

    /// <summary>The source columns the entries reaching <paramref name="target"/> read, as the mapping names them.</summary>
    private static IReadOnlyList<DatasetColumn> Reads(MappingDefinition mapping, TemplatePath target)
    {
        var selection = EntrySelection.Of([target]);
        return mapping.Entries
            .Where(e => e.IsRepeater ? selection.Covers(e.Target.Text) : selection.Includes(e.Target.Text))
            .SelectMany(e => e.Columns)
            .Distinct()
            .ToList();
    }

    /// <summary>A column a request names, which has to be one the attribute reads: a proof reads nothing else of the tables.</summary>
    private static DatasetColumn Column(string text, IReadOnlyList<DatasetColumn> reads, string target)
        => reads.FirstOrDefault(c => string.Equals(c.ToString(), text, StringComparison.OrdinalIgnoreCase))
            ?? throw new DeliveryException($"'{text}' is not a source column {target} reads, so a proof of it reads nothing.");

    private static void Comment(StringBuilder script, string text) => script.Append("-- ").AppendLine(text.ReplaceLineEndings(" "));
}
