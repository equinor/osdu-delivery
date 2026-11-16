using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Yaml;

/// <summary>
/// A lineage description document (<c>flowType: lin</c>). It takes the place every consumer gives a flow document (a
/// pipeline row with its text and definition, the editor, <c>validate</c>), and nothing else: it is in no wave, takes
/// no schedule, declares no connection or endpoint, and every run of it is refused
/// (<see cref="YamlDocumentLoader.ValidateRunParameters"/>). What it adds to lineage is read from <see cref="Flow"/>
/// after the estate's own lineage is computed, never as part of it.
/// </summary>
public sealed record LineageDescriptionFlowDocument : RegisteredFlowDocument
{
    /// <summary>The description as written.</summary>
    public required LineageDescriptionFlow Flow { get; init; }

    /// <inheritdoc />
    public override string Name => Flow.Name;

    /// <inheritdoc />
    public override string Kind => LineageDescriptionFlow.FlowType;

    /// <inheritdoc />
    public override string? Batch => Flow.Batch;

    /// <summary>False: a description runs nothing, so the computed graph holds no node, edge or wave for it. Its place
    /// in lineage is the described part (<see cref="LineageReport.Described"/>).</summary>
    public override bool ParticipatesInLineage => false;
}

/// <summary>
/// The lineage description kind (<c>flowType: lin</c>). It parses the document and refuses every run: a description
/// has no operation, so there is nothing a trigger, a schedule, a batch or the command line could start.
/// </summary>
public sealed class LineageDescriptionFlowKind : IFlowDocumentKind
{
    /// <summary>What a caller is told when it asks for a lineage description to be run.</summary>
    public const string NeverRuns =
        "a lineage description (flowType: lin) describes a step another system runs and is never executed; "
        + "it is read for lineage only, so there is nothing to run, schedule or trigger.";

    /// <inheritdoc />
    public string FlowType => LineageDescriptionFlow.FlowType;

    /// <inheritdoc />
    public string Description => "a lineage description of a step another system runs (metadata only, never executed)";

    /// <inheritdoc />
    public IReadOnlyList<FlowKindOperation> Operations => [];

    /// <inheritdoc />
    public RegisteredFlowDocument Parse(string yaml, string source)
        => new LineageDescriptionFlowDocument
        {
            Flow = YamlLineageDescriptionFlowLoader.Parse(yaml, source),
            Mode = ExecutionMode.Disabled,
        };

    /// <summary>Always refuses: see <see cref="NeverRuns"/>.</summary>
    public void ValidateParameters(RunParameters parameters) => throw new SqlFlowException(NeverRuns);
}

/// <summary>
/// Reads a lineage description (<see cref="LineageDescriptionFlow"/>). The reading is strict: an unknown key, a value of
/// the wrong shape, a value over its limit or a repeated entry is a problem, since a misspelt key would otherwise leave a
/// connection or a column mapping out of lineage without a word. Every problem names its line, and all of them are
/// reported together so a document is fixed in one pass.
/// </summary>
public static partial class YamlLineageDescriptionFlowLoader
{
    /// <summary>Reads <paramref name="yaml"/>; <paramref name="source"/> labels every problem (the file's path, or
    /// "&lt;inline&gt;"). Throws <see cref="FlowValidationException"/> listing every problem.</summary>
    public static LineageDescriptionFlow Parse(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(source);
        var reader = new Reader(source);

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var text = new StringReader(yaml);
            stream.Load(text);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // YamlDotNet reports a duplicate mapping key and some scanner failures this way rather than as a YamlException.
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (stream.Documents.Count != 1)
        {
            throw new FlowValidationException(stream.Documents.Count == 0
                ? $"{source}: the document is empty."
                : $"{source}: the file holds {stream.Documents.Count} YAML documents; a lineage description holds one.");
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FlowValidationException(
                $"{source}: a lineage description must be a mapping that starts with 'flowType: lin'.");
        }

        var flow = reader.Flow(root);
        if (reader.Problems.Count > 0 || flow is null)
        {
            throw new FlowValidationException(reader.Problems.Count == 1
                ? reader.Problems[0]
                : $"{source}: {reader.Problems.Count} problems in the lineage description:{Environment.NewLine}  "
                  + string.Join(Environment.NewLine + "  ", reader.Problems));
        }

        return flow;
    }

    /// <summary>Whether <paramref name="system"/> is a valid system name: a lower-case letter, then lower-case letters,
    /// digits or hyphens, at most <see cref="LineageDescriptionFlow.MaxSystemLength"/> characters.</summary>
    public static bool IsValidSystem(string system) => SystemPattern().IsMatch(system);

    /// <summary>Whether <paramref name="alias"/> is a valid alias: a letter or underscore, then letters, digits or
    /// underscores, at most <see cref="LineageDescriptionFlow.MaxAliasLength"/> characters.</summary>
    public static bool IsValidAlias(string alias) => AliasPattern().IsMatch(alias);

    private static int Line(YamlNode node) => (int)node.Start.Line;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SystemPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    /// <summary>A credential written into an address: <c>scheme://user:secret@host</c>.</summary>
    [GeneratedRegex("://[^/@\\s]*:[^/@\\s]*@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    private sealed class Reader(string source)
    {
        private static readonly string[] RootKeys =
        [
            "flowType", "name", "description", "batch", "system", "unit", "owner", "url", "evidence", "reads", "writes",
            "context",
        ];

        /// <summary>Keys every running flow takes that a description cannot, each answered with the reason.</summary>
        private static readonly string[] RunKeys =
            ["schedule", "mode", "lifecycle", "connections", "parameters", "source", "target", "onErrorResume"];

        private static readonly string[] EvidenceKeys = ["repo", "path", "commit", "note"];
        private static readonly string[] ReadKeys = ["system", "name", "fedBy", "at", "as", "note", "origin"];
        private static readonly string[] WriteKeys = ["system", "name", "feeds", "at", "note", "columns"];
        private static readonly string[] ColumnKeys = ["from", "note"];

        public List<string> Problems { get; } = [];

        /// <summary>The columns and the sources mapped so far over the whole document, held to its limits.</summary>
        private int _columns;
        private int _sources;
        private bool _overColumns;
        private bool _overSources;

        private void Problem(int line, string message)
            => Problems.Add(line > 0 ? $"{source}:{line}: {message}" : $"{source}: {message}");

        public LineageDescriptionFlow? Flow(YamlMappingNode root)
        {
            var map = Keys(root, RootKeys, "the lineage description", RunKeys);

            var name = Text(map, "name", root, "the lineage description", required: true, max: LineageDescriptionFlow.MaxNameLength);
            if (name is not null && HasControlCharacter(name))
            {
                Problem(Line(map["name"]), "'name' holds a control character; a name is plain text on one line.");
                name = null;
            }

            var system = SystemName(map, root, "the lineage description", required: true);
            var description = Text(map, "description", root, "the lineage description", max: LineageDescriptionFlow.MaxDescriptionLength, prose: true);
            var batch = Text(map, "batch", root, "the lineage description", max: LineageDescriptionFlow.MaxBatchLength);
            var unit = Text(map, "unit", root, "the lineage description", max: LineageDescriptionFlow.MaxUnitLength, prose: true);
            var owner = Text(map, "owner", root, "the lineage description", max: LineageDescriptionFlow.MaxOwnerLength, prose: true);
            var url = Address(map, "url", "the lineage description", LineageDescriptionFlow.MaxUrlLength);
            var context = Text(map, "context", root, "the lineage description", max: LineageDescriptionFlow.MaxContextLength, keepLines: true);

            var evidence = new List<LineageDescriptionEvidence>();
            if (map.TryGetValue("evidence", out var evidenceNode))
            {
                var items = Sequence(evidenceNode, "evidence", LineageDescriptionFlow.MaxEvidence);
                for (var i = 0; i < items.Count; i++)
                {
                    if (Evidence(items[i], $"evidence entry {i + 1}") is { } entry)
                    {
                        evidence.Add(entry);
                    }
                }
            }

            var reads = new List<LineageDescriptionEndpoint>();
            var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (map.TryGetValue("reads", out var readsNode))
            {
                var items = Sequence(readsNode, "reads", LineageDescriptionFlow.MaxEndpoints);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < items.Count; i++)
                {
                    var where = $"reads entry {i + 1}";
                    if (Endpoint(items[i], where, system, isRead: true, aliases: null) is not { } read)
                    {
                        continue;
                    }

                    if (!seen.Add(Identity(read)))
                    {
                        Problem(Line(items[i]), $"{where} names {Shown(read)}, which an earlier reads entry already names.");
                        continue;
                    }

                    if (read.Alias is { } alias && !aliases.TryAdd(alias, i + 1))
                    {
                        Problem(Line(items[i]), $"{where} takes the alias '{alias}', which reads entry {aliases[alias]} already has.");
                        continue;
                    }

                    reads.Add(read);
                }
            }

            var writes = new List<LineageDescriptionEndpoint>();
            if (map.TryGetValue("writes", out var writesNode))
            {
                var items = Sequence(writesNode, "writes", LineageDescriptionFlow.MaxEndpoints);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < items.Count; i++)
                {
                    var where = $"writes entry {i + 1}";
                    if (Endpoint(items[i], where, system, isRead: false, aliases) is not { } write)
                    {
                        continue;
                    }

                    if (!seen.Add(Identity(write)))
                    {
                        Problem(Line(items[i]), $"{where} names {Shown(write)}, which an earlier writes entry already names.");
                        continue;
                    }

                    writes.Add(write);
                }
            }

            if (!map.ContainsKey("reads") && !map.ContainsKey("writes"))
            {
                Problem(Line(root), "the lineage description has neither 'reads' nor 'writes', so it connects nothing; name at least one object.");
            }
            else if (Problems.Count == 0 && reads.Count == 0 && writes.Count == 0)
            {
                Problem(Line(root), "the lineage description's 'reads' and 'writes' are both empty, so it connects nothing; name at least one object.");
            }

            if (Problems.Count > 0 || name is null || system is null)
            {
                return null;
            }

            return new LineageDescriptionFlow
            {
                Name = name,
                Description = description,
                Batch = batch,
                System = system,
                Unit = unit,
                Owner = owner,
                Url = url,
                Evidence = evidence,
                Reads = reads,
                Writes = writes,
                Context = context,
            };
        }

        private LineageDescriptionEvidence? Evidence(YamlNode node, string where)
        {
            // The short form names the path alone.
            if (node is YamlScalarNode scalar)
            {
                var only = scalar.Value?.Trim();
                if (string.IsNullOrEmpty(only))
                {
                    Problem(Line(node), $"{where} is blank; name a path, or remove the entry.");
                    return null;
                }

                if (only.Length > LineageDescriptionFlow.MaxPathLength)
                {
                    Problem(Line(node), $"{where} is {only.Length} characters; a path may be at most {LineageDescriptionFlow.MaxPathLength}.");
                    return null;
                }

                return Plain(only, node, where, prose: false) ? new LineageDescriptionEvidence { Path = only } : null;
            }

            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), $"{where} must be a path, or a mapping with 'path' and optionally 'repo', 'commit' and 'note'.");
                return null;
            }

            var before = Problems.Count;
            var map = Keys(mapping, EvidenceKeys, where);
            var path = Text(map, "path", mapping, where, required: true, max: LineageDescriptionFlow.MaxPathLength);
            var repo = Address(map, "repo", where, LineageDescriptionFlow.MaxRepoLength);
            var commit = Text(map, "commit", mapping, where, max: LineageDescriptionFlow.MaxCommitLength);
            var note = Text(map, "note", mapping, where, max: LineageDescriptionFlow.MaxNoteLength, keepLines: true);
            return Problems.Count > before || path is null
                ? null
                : new LineageDescriptionEvidence { Repo = repo, Path = path, Commit = commit, Note = note };
        }

        private LineageDescriptionEndpoint? Endpoint(
            YamlNode node, string where, string? stepSystem, bool isRead, Dictionary<string, int>? aliases)
        {
            var flowKey = isRead ? "fedBy" : "feeds";
            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), $"{where} must be a mapping naming an object ('name', with 'system' when it is not the description's own) or a flow of the estate ('{flowKey}').");
                return null;
            }

            var before = Problems.Count;
            var map = Keys(mapping, isRead ? ReadKeys : WriteKeys, where, isRead ? ["feeds", "columns"] : ["fedBy", "as", "origin"]);
            var beforeNames = Problems.Count;
            var name = Text(map, "name", mapping, where, max: LineageDescriptionFlow.MaxObjectNameLength);
            var flow = Text(map, flowKey, mapping, where, max: LineageDescriptionFlow.MaxNameLength);
            if (name is null && flow is null)
            {
                // A blank or absent name is said once; a name that was refused above has already been reported.
                if (Problems.Count == beforeNames)
                {
                    Problem(Line(mapping), $"{where} names nothing; give it a 'name', or a '{flowKey}' naming a flow of the estate.");
                }

                return null;
            }

            var at = Text(map, "at", mapping, where, max: LineageDescriptionFlow.MaxAtLength);
            var note = Text(map, "note", mapping, where, max: LineageDescriptionFlow.MaxNoteLength, keepLines: true);
            var alias = isRead ? Text(map, "as", mapping, where, max: LineageDescriptionFlow.MaxAliasLength) : null;
            var origin = isRead && Flag(map, "origin", where);

            if (name is not null && flow is not null)
            {
                Problem(Line(mapping), $"{where} has both 'name' and '{flowKey}'; an object is named one way: by its own name, or by the flow of the estate that {(isRead ? "writes" : "reads")} it.");
                return null;
            }

            string? system = null;
            if (name is not null)
            {
                system = SystemName(map, mapping, where, required: false) ?? stepSystem;
                if (name.Contains('|', StringComparison.Ordinal) || HasControlCharacter(name))
                {
                    Problem(Line(map["name"]), $"'name' of {where} holds a '|' or a control character, which an object name cannot carry.");
                }

                if (at is not null)
                {
                    Problem(Line(map["at"]), $"'at' of {where} narrows a flow's objects, so it goes with '{flowKey}', not with 'name'.");
                }
            }
            else
            {
                if (map.TryGetValue("system", out var systemNode))
                {
                    Problem(Line(systemNode), $"'system' of {where} goes with 'name'; an object named by '{flowKey}' is the estate's own.");
                }

                if (origin)
                {
                    Problem(Line(map["origin"]), $"'origin' of {where} marks where described data starts, which an object a flow of the estate writes is not.");
                }

                if (HasControlCharacter(flow!))
                {
                    Problem(Line(map[flowKey]), $"'{flowKey}' of {where} holds a control character; a flow name is plain text on one line.");
                }
            }

            if (alias is not null && !IsValidAlias(alias))
            {
                Problem(Line(map["as"]), $"'as' of {where} is '{Clip(alias)}'; an alias is a letter or underscore followed by letters, digits or underscores, at most {LineageDescriptionFlow.MaxAliasLength} characters.");
            }

            var columns = new List<LineageDescriptionColumn>();
            if (!isRead && map.TryGetValue("columns", out var columnsNode))
            {
                columns = Columns(columnsNode, where, aliases ?? []);
            }

            if (Problems.Count > before)
            {
                return null;
            }

            return new LineageDescriptionEndpoint
            {
                System = system,
                Name = name,
                Flow = flow,
                At = at,
                Alias = alias,
                Note = note,
                Origin = origin,
                Columns = columns,
            };
        }

        private List<LineageDescriptionColumn> Columns(YamlNode node, string where, Dictionary<string, int> aliases)
        {
            var columns = new List<LineageDescriptionColumn>();
            if (IsNothing(node))
            {
                return columns;
            }

            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), $"'columns' of {where} must be a mapping of column name to where the column comes from.");
                return columns;
            }

            if (mapping.Children.Count > LineageDescriptionFlow.MaxColumns)
            {
                Problem(Line(node), $"'columns' of {where} maps {mapping.Children.Count} columns; it may map at most {LineageDescriptionFlow.MaxColumns}. Use a '*' entry for columns carried across by name.");
                return columns;
            }

            // The document's own limit, counted before anything is read, so a mapping repeated under many writes (an
            // anchor, say) costs no more than the limit allows.
            _columns += mapping.Children.Count;
            if (_columns > LineageDescriptionFlow.MaxDocumentColumns)
            {
                if (!_overColumns)
                {
                    _overColumns = true;
                    Problem(Line(node), $"the lineage description maps more than {LineageDescriptionFlow.MaxDocumentColumns} columns over all it writes; use '*' entries for columns carried across by name, or describe the step in several documents.");
                }

                return columns;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } raw } || raw.Trim() is not { Length: > 0 } column)
                {
                    Problem(Line(keyNode), $"'columns' of {where} has a column whose name is not text.");
                    continue;
                }

                var what = $"column '{Clip(column)}' of {where}";
                if (column.Length > LineageDescriptionFlow.MaxColumnNameLength || HasControlCharacter(column))
                {
                    Problem(Line(keyNode), column.Length > LineageDescriptionFlow.MaxColumnNameLength
                        ? $"{what} has a name of {column.Length} characters; a column name may be at most {LineageDescriptionFlow.MaxColumnNameLength}."
                        : $"{what} has a control character in its name.");
                    continue;
                }

                if (!seen.Add(column))
                {
                    Problem(Line(keyNode), $"{what} is mapped twice; column names are compared without regard to case.");
                    continue;
                }

                var stars = column.Count(c => c == '*');
                if (stars > 1)
                {
                    Problem(Line(keyNode), $"{what} has {stars} '*' in its name; a name carries at most one, standing for the columns not listed by name.");
                    continue;
                }

                if (!Plain(column, keyNode, $"the name of a column of {where}", prose: false))
                {
                    continue;
                }

                YamlNode? fromNode = null;
                string? note = null;
                var before = Problems.Count;
                switch (value)
                {
                    case YamlScalarNode when IsNothing(value):
                        // A column listed with nothing after it is one the step makes itself.
                        break;
                    case YamlScalarNode or YamlSequenceNode:
                        fromNode = value;
                        break;
                    case YamlMappingNode entry:
                    {
                        var map = Keys(entry, ColumnKeys, what);
                        map.TryGetValue("from", out fromNode);
                        note = Text(map, "note", entry, what, max: LineageDescriptionFlow.MaxNoteLength, keepLines: true);
                        break;
                    }

                    default:
                        Problem(Line(value), $"{what} must be a source column ('alias.column'), a list of them, or a mapping with 'from' and 'note'.");
                        break;
                }

                var sources = fromNode is null ? [] : Sources(fromNode, what, aliases, stars == 1);
                if (Problems.Count == before)
                {
                    columns.Add(new LineageDescriptionColumn { Name = column, From = sources, Note = note });
                }
            }

            return columns;
        }

        private List<LineageDescriptionColumnSource> Sources(
            YamlNode node, string what, Dictionary<string, int> aliases, bool targetHasStar)
        {
            var sources = new List<LineageDescriptionColumnSource>();
            if (IsNothing(node))
            {
                return sources;
            }

            var items = node is YamlScalarNode ? [node] : Sequence(node, $"'from' of {what}", LineageDescriptionFlow.MaxColumnSources);
            _sources += items.Count;
            if (_sources > LineageDescriptionFlow.MaxDocumentColumnSources)
            {
                if (!_overSources)
                {
                    _overSources = true;
                    Problem(Line(node), $"the lineage description names more than {LineageDescriptionFlow.MaxDocumentColumnSources} source columns over all its mapped columns; use '*' entries for columns carried across by name, or describe the step in several documents.");
                }

                return sources;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item is not YamlScalarNode { Value: { } raw } || raw.Trim() is not { Length: > 0 } reference)
                {
                    Problem(Line(item), $"'from' of {what} has an entry that is not text; a source is written 'alias.column'.");
                    continue;
                }

                var dot = reference.IndexOf('.', StringComparison.Ordinal);
                if (dot <= 0 || dot == reference.Length - 1)
                {
                    Problem(Line(item), $"'from' of {what} has '{Clip(reference)}'; a source is written 'alias.column', where the alias is the 'as' of a reads entry.");
                    continue;
                }

                if (!Plain(reference, item, $"'from' of {what}", prose: false))
                {
                    continue;
                }

                var alias = reference[..dot].Trim();
                var column = reference[(dot + 1)..].Trim();

                // The alias is kept as its reads entry spells it, so a mapping finds its object whatever case it used.
                var declared = aliases.Keys.FirstOrDefault(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase));
                if (declared is null)
                {
                    Problem(Line(item), aliases.Count == 0
                        ? $"'from' of {what} names the alias '{Clip(alias)}', but no reads entry declares an alias; give the read object an 'as'."
                        : $"'from' of {what} names the alias '{Clip(alias)}', which no reads entry declares; the aliases are: {string.Join(", ", aliases.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
                    continue;
                }

                if (column.Length == 0 || column.Length > LineageDescriptionFlow.MaxColumnNameLength || HasControlCharacter(column))
                {
                    Problem(Line(item), $"'from' of {what} has '{Clip(reference)}', whose column name is blank, over {LineageDescriptionFlow.MaxColumnNameLength} characters, or holds a control character.");
                    continue;
                }

                var sourceStars = column.Count(c => c == '*');
                if (sourceStars > 1 || (sourceStars == 1 && !targetHasStar))
                {
                    Problem(Line(item), sourceStars > 1
                        ? $"'from' of {what} has '{Clip(reference)}', with {sourceStars} '*' in the column name; a name carries at most one."
                        : $"'from' of {what} has '{Clip(reference)}'; a '*' in a source stands for the column of the same name, so it goes with a mapped column that has a '*' too.");
                    continue;
                }

                if (!seen.Add($"{declared}.{column}"))
                {
                    Problem(Line(item), $"'from' of {what} lists '{Clip(reference)}' twice.");
                    continue;
                }

                sources.Add(new LineageDescriptionColumnSource(declared, column));
            }

            return sources;
        }

        /// <summary>The mapping's entries by key, each unknown key reported once. A key of <paramref name="refused"/> is
        /// answered with why a description does not take it.</summary>
        private Dictionary<string, YamlNode> Keys(YamlMappingNode mapping, string[] known, string where, string[]? refused = null)
        {
            var map = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } key })
                {
                    Problem(Line(keyNode), $"{where} has a key that is not text.");
                    continue;
                }

                if (known.Contains(key, StringComparer.Ordinal))
                {
                    map[key] = value;
                    continue;
                }

                var near = known.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (near is not null)
                {
                    Problem(Line(keyNode), $"{where} has key '{key}'; keys are case-sensitive, so write '{near}'.");
                }
                else if (refused is not null && refused.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    Problem(Line(keyNode), Refusal(key, where));
                }
                else if (mapping.Style == MappingStyle.Flow && IsNothing(value))
                {
                    // Text after a comma inside { } reads as a key of its own: the commonest slip in a one-line entry.
                    Problem(
                        Line(keyNode),
                        $"{where} reads '{Clip(key)}' as a key with no value; inside {{ }} a comma starts the next key, "
                        + "so put a value that holds a comma in quotes.");
                }
                else
                {
                    Problem(Line(keyNode), $"{where} has an unknown key '{Clip(key)}'; it may have: {string.Join(", ", known)}.");
                }
            }

            return map;
        }

        private static string Refusal(string key, string where) => key.ToLowerInvariant() switch
        {
            "schedule" or "mode" or "lifecycle" or "parameters" or "onerrorresume"
                => $"{where} has '{key}', which a flow that runs takes; a lineage description is never executed, so remove it.",
            "connections" or "source" or "target"
                => $"{where} has '{key}', which a flow that moves data takes; a lineage description connects to nothing and names objects under 'reads' and 'writes'.",
            "feeds" => $"{where} has 'feeds', which belongs to a writes entry; a read object the estate writes is named with 'fedBy'.",
            "fedby" => $"{where} has 'fedBy', which belongs to a reads entry; a written object the estate reads is named with 'feeds'.",
            "columns" => $"{where} has 'columns', which belongs to a writes entry: a mapping says where a written column comes from.",
            "as" => $"{where} has 'as', which belongs to a reads entry: an alias is how a column mapping names a read object.",
            "origin" => $"{where} has 'origin', which belongs to a reads entry: it marks where described data starts.",
            _ => $"{where} has '{key}', which a lineage description does not take.",
        };

        /// <summary>
        /// One text value. <paramref name="prose"/> says the value is read by people (a description, a note, the
        /// context): it may quote anything but a credential. Every other value names something (an object, a flow, a
        /// path), and takes no <c>${...}</c> reference either. Left out, text over several lines is prose.
        /// </summary>
        private string? Text(
            Dictionary<string, YamlNode> map, string key, YamlNode owner, string where, bool required = false,
            int max = int.MaxValue, bool keepLines = false, bool? prose = null)
        {
            if (!map.TryGetValue(key, out var node))
            {
                if (required)
                {
                    Problem(Line(owner), $"{where} has no '{key}'.");
                }

                return null;
            }

            if (node is not YamlScalarNode scalar)
            {
                Problem(Line(node), $"'{key}' of {where} must be text.");
                return null;
            }

            var value = IsNothing(node) ? null : scalar.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (required)
                {
                    Problem(Line(node), $"'{key}' of {where} is blank.");
                }

                return null;
            }

            if (value.Length > max)
            {
                Problem(Line(node), $"'{key}' of {where} is {value.Length} characters; it may be at most {max}.");
                return null;
            }

            if (!keepLines && (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)))
            {
                Problem(Line(node), $"'{key}' of {where} spans several lines; it is one line of text.");
                return null;
            }

            return Plain(value, node, $"'{key}' of {where}", prose ?? keepLines) ? value : null;
        }

        /// <summary>
        /// Whether a value may stand in a lineage description: it carries no credential, and unless it is prose, no
        /// <c>${...}</c> reference. A description is metadata that is stored, shown and handed to an assistant as
        /// written; it connects to nothing, so it has no use for a secret or for a value resolved at run time. A value
        /// refused for a credential is never echoed: it is the secret.
        /// </summary>
        private bool Plain(string value, YamlNode node, string what, bool prose)
        {
            if (SecretHygiene.LooksLikeEmbeddedSecret(value)
                || !string.Equals(SecretHygiene.RedactedMessage(value), value, StringComparison.Ordinal))
            {
                Problem(Line(node), $"{what} appears to carry a credential; a lineage description holds no secret, so write it without one.");
                return false;
            }

            if (!prose && value.Contains("${", StringComparison.Ordinal))
            {
                Problem(Line(node), $"{what} holds a reference ('${{...}}'); a lineage description resolves nothing, so write the value plainly, without what differs between environments.");
                return false;
            }

            return true;
        }

        /// <summary>A system name, accepted in any case and kept lower-case.</summary>
        private string? SystemName(Dictionary<string, YamlNode> map, YamlNode owner, string where, bool required)
        {
            var value = Text(map, "system", owner, where, required);
            if (value is null)
            {
                return null;
            }

            var system = value.ToLowerInvariant();
            if (!IsValidSystem(system))
            {
                Problem(Line(map["system"]), $"'system' of {where} is '{Clip(value)}'; a system is a letter followed by letters, digits or hyphens, at most {LineageDescriptionFlow.MaxSystemLength} characters (databricks, fabric, sap-erp).");
                return null;
            }

            return system;
        }

        /// <summary>An address shown to a reader: text without a credential in it.</summary>
        private string? Address(Dictionary<string, YamlNode> map, string key, string where, int max)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return null;
            }

            var value = Text(map, key, node, where, max: max);
            if (value is null)
            {
                return null;
            }

            if (UserInfoPattern().IsMatch(value) || SecretHygiene.LooksLikeEmbeddedSecret(value))
            {
                // The value is never echoed: it is the secret.
                Problem(Line(node), $"'{key}' of {where} appears to carry a credential; write the address without one.");
                return null;
            }

            return value;
        }

        private bool Flag(Dictionary<string, YamlNode> map, string key, string where)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return false;
            }

            if (node is YamlScalarNode { Value: { } raw })
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "true":
                        return true;
                    case "false" or "":
                        return false;
                }
            }

            Problem(Line(node), $"'{key}' of {where} must be true or false.");
            return false;
        }

        private List<YamlNode> Sequence(YamlNode node, string what, int max)
        {
            if (IsNothing(node))
            {
                return [];
            }

            if (node is not YamlSequenceNode sequence)
            {
                Problem(Line(node), $"{what} must be a list.");
                return [];
            }

            if (sequence.Children.Count > max)
            {
                Problem(Line(node), $"{what} holds {sequence.Children.Count} entries; it may hold at most {max}.");
                return [];
            }

            return [.. sequence.Children];
        }

        /// <summary>What makes two entries the same object: the system and name, or the flow and the part of it meant.</summary>
        private static string Identity(LineageDescriptionEndpoint endpoint)
            => endpoint.NamesFlow
                ? $"flow|{endpoint.Flow}|{endpoint.At}"
                : $"object|{endpoint.System}|{endpoint.Name}";

        private static string Shown(LineageDescriptionEndpoint endpoint)
            => endpoint.NamesFlow
                ? endpoint.At is null ? $"flow '{Clip(endpoint.Flow!)}'" : $"flow '{Clip(endpoint.Flow!)}' at '{Clip(endpoint.At)}'"
                : $"'{Clip(endpoint.Name!)}' of {endpoint.System}";

        /// <summary>Whether a value is absent as YAML writes it: nothing after the key, or an unquoted <c>~</c> or
        /// <c>null</c>.</summary>
        private static bool IsNothing(YamlNode node)
            => node is YamlScalarNode scalar
               && (string.IsNullOrWhiteSpace(scalar.Value)
                   || (scalar.Style == ScalarStyle.Plain && scalar.Value is "~" or "null" or "Null" or "NULL"));

        private static bool HasControlCharacter(string value) => value.Any(char.IsControl);

        private static string Clip(string value) => value.Length <= 60 ? value : value[..60] + "...";
    }
}
