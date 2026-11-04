using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Delivery.Documents;

/// <summary>The shapes a node of a mapping's outline takes.</summary>
public static class MappingOutlineShape
{
    /// <summary>An object: the properties it holds.</summary>
    public const string Group = "group";

    /// <summary>A <c>$forEach</c>: one item per row of a child dataset, its children what each item holds.</summary>
    public const string Repeat = "repeat";

    /// <summary>A list whose items are not all literal: its children are the items, in order.</summary>
    public const string List = "list";

    /// <summary>A <c>$coalesce</c>: its children are the alternatives, tried in order.</summary>
    public const string Choice = "choice";

    /// <summary>One value, read by the <see cref="MappingOutlineNode.Flow"/> it carries.</summary>
    public const string Value = "value";
}

/// <summary>How a value node comes to its value, as the outline counts them.</summary>
public static class MappingOutlineHow
{
    /// <summary>A column, written as it is.</summary>
    public const string Copied = "copied";

    /// <summary>A column or an expression over the row, changed on its way.</summary>
    public const string Changed = "changed";

    /// <summary>Found in the partition's cache or by a search of the platform.</summary>
    public const string LookedUp = "lookedUp";

    /// <summary>The same for every record: a literal, or a parameter of the flow.</summary>
    public const string Fixed = "fixed";

    /// <summary>Read from another record: the parent, a child, the base record, an enrichment.</summary>
    public const string Linked = "linked";

    /// <summary>The record's id, which the engine makes from the key.</summary>
    public const string Key = "key";
}

/// <summary>What a source of a mapping's values is.</summary>
public static class MappingOutlineSourceKind
{
    /// <summary>The record table: one row per record.</summary>
    public const string Record = "record";

    /// <summary>A child dataset of the flow.</summary>
    public const string Child = "child";

    /// <summary>The values the flow supplies.</summary>
    public const string Parameters = "parameters";

    /// <summary>A type of the partition's cache.</summary>
    public const string Cache = "cache";

    /// <summary>A record set searched on the platform.</summary>
    public const string Search = "search";

    /// <summary>The record's parent.</summary>
    public const string Parent = "parent";

    /// <summary>A child record of a kind linked to the record's.</summary>
    public const string ChildRecord = "childRecord";

    /// <summary>The record an enrichment mapping renders into or beside.</summary>
    public const string Base = "base";

    /// <summary>The records an enrichment registers.</summary>
    public const string Link = "link";
}

/// <summary>
/// One property a source gives: a column, a parameter or a field of a cached record. A key column is listed whether or not a
/// node reads it, and so is a column only the label or the identity read.
/// </summary>
/// <param name="Name">The name, as the mapping writes it.</param>
/// <param name="Key">True for a column of the dataset's key.</param>
/// <param name="Display">True for a column the label or the identity reads, which never reaches the record.</param>
/// <param name="Match">True for a field a lookup compares rather than reads.</param>
/// <param name="Reads">How many value nodes read it.</param>
/// <param name="Description">What it is, where the document says.</param>
public sealed record MappingOutlineField(string Name, bool Key, bool Display, bool Match, int Reads, string? Description);

/// <summary>Something a mapping reads: a table, the flow's parameters, a cached type, a searched record set, another record.</summary>
/// <param name="Id">The name the outline's reads and lookups use for it (<c>dataset</c>, <c>dataset.curves</c>, <c>cache.UnitOfMeasure</c>).</param>
/// <param name="Kind">One of <see cref="MappingOutlineSourceKind"/>.</param>
/// <param name="Name">What the document calls it.</param>
/// <param name="Detail">
/// The kind searched, the kind of the parent or child record read (a parent's as the template links it, so none without the
/// template), or what the document says of it.
/// </param>
/// <param name="Fields">What the mapping reads of it, in the order the document first does.</param>
public sealed record MappingOutlineSource(string Id, string Kind, string Name, string? Detail, IReadOnlyList<MappingOutlineField> Fields);

/// <summary>A field a flow's value enters at, and what it does there.</summary>
/// <param name="Source">The <see cref="MappingOutlineSource.Id"/> it is read from.</param>
/// <param name="Field">The field.</param>
/// <param name="Role"><c>value</c> for what the value is made of, <c>match</c> for what a lookup compares, <c>condition</c> for what a <c>$when</c> or a <c>$where</c> tests.</param>
public sealed record MappingOutlineRead(string Source, string Field, string Role);

/// <summary>One line a lookup finds its record by: the field compared, and what it is compared with.</summary>
public sealed record MappingOutlineMatch(string Field, string Value);

/// <summary>A table a step of a flow reads: the field it takes, and the lines that find the record holding it.</summary>
/// <param name="Source">The <see cref="MappingOutlineSource.Id"/> looked in.</param>
/// <param name="Field">The field read; null where the cached type decides it (a lookup table's value).</param>
/// <param name="Matches">The lines tried, in order; empty for a table matched on its key.</param>
public sealed record MappingOutlineLookup(string Source, string? Field, IReadOnlyList<MappingOutlineMatch> Matches);

/// <summary>One thing done to a value on its way to the property.</summary>
/// <param name="Kind">The modifier (<c>split</c>, <c>replace</c>, <c>ref</c>...), or <c>find</c>, <c>expr</c> or <c>key</c>.</param>
/// <param name="Label">The word a diagram shows it by.</param>
/// <param name="Detail">What it does, in plain words.</param>
/// <param name="Text">How the mapping writes it.</param>
/// <param name="Lookups">The tables it reads.</param>
public sealed record MappingOutlineStep(string Kind, string Label, string Detail, string Text, IReadOnlyList<MappingOutlineLookup> Lookups);

/// <summary>How one value node comes to its value.</summary>
/// <param name="Origin">
/// Where it starts: <c>column</c>, <c>expression</c>, <c>find</c>, <c>literal</c>, <c>parameter</c>, <c>parent</c>, <c>childRecord</c>,
/// <c>base</c>, <c>link</c> or <c>key</c>.
/// </param>
/// <param name="Summary">The node on one line, as the record shape names it.</param>
/// <param name="Literal">What a literal writes, as JSON.</param>
/// <param name="Reads">What enters it.</param>
/// <param name="Steps">What is done to it, in the order a render does it.</param>
/// <param name="Unverified">True when an id it builds is written although the cache does not hold the record.</param>
public sealed record MappingOutlineFlow(
    string Origin, string Summary, string? Literal, IReadOnlyList<MappingOutlineRead> Reads, IReadOnlyList<MappingOutlineStep> Steps, bool Unverified);

/// <summary>A rule a node states on its value (<c>$assert</c>).</summary>
/// <param name="Label">What it is called.</param>
/// <param name="Expected">What the value has to be, as findings quote it.</param>
/// <param name="OnFail">What a record that breaks it does: <c>hold</c>, <c>report</c> or <c>omit</c>.</param>
/// <param name="Stage">The value judged: <c>record</c> or <c>incoming</c>.</param>
public sealed record MappingOutlineAssertion(string Label, string Expected, string OnFail, string Stage);

/// <summary>A property of the record, or a part of one (an alternative, an item), and how it is filled.</summary>
/// <param name="Path">Where it is in the record (<c>data.Curves[].CurveUnit</c>); an alternative or an item adds its place.</param>
/// <param name="Name">Its name: the last step of the path, or its place among its siblings.</param>
/// <param name="Shape">One of <see cref="MappingOutlineShape"/>.</param>
/// <param name="How">One of <see cref="MappingOutlineHow"/> for a value or a <c>$coalesce</c>; empty for what only holds others.</param>
/// <param name="Required">False for a node an empty value leaves out rather than holds the record.</param>
/// <param name="Type">The type the template gives it, when the template was at hand.</param>
/// <param name="When">Its <c>$when</c>.</param>
/// <param name="Where">Its <c>$where</c>.</param>
/// <param name="Dataset">The child dataset a <c>$forEach</c> repeats.</param>
/// <param name="Description">The node's <c>$description</c>, else what the template says of the property.</param>
/// <param name="Line">The line of the document it starts on, from 1; null where the document does not write it.</param>
/// <param name="EndLine">The line it ends on.</param>
/// <param name="Flow">How a value comes to its value; null for what only holds others.</param>
/// <param name="Assertions">The rules it states.</param>
/// <param name="Children">What it holds.</param>
public sealed record MappingOutlineNode(
    string Path,
    string Name,
    string Shape,
    string How,
    bool Required,
    string? Type,
    string? When,
    string? Where,
    string? Dataset,
    string? Description,
    int? Line,
    int? EndLine,
    MappingOutlineFlow? Flow,
    IReadOnlyList<MappingOutlineAssertion> Assertions,
    IReadOnlyList<MappingOutlineNode> Children);

/// <summary>What identifies a record of the dataset: the <c>dataset</c> block.</summary>
/// <param name="System">The source system; empty for an enrichment mapping.</param>
/// <param name="Key">The key columns, in order.</param>
/// <param name="Label">The display label's template.</param>
/// <param name="Identity">The columns the ledger indexes for a person to find the record by.</param>
/// <param name="IdFrom"><c>deliveryKey</c> or <c>key</c>.</param>
public sealed record MappingOutlineDataset(string System, IReadOnlyList<string> Key, string? Label, IReadOnlyList<string> Identity, string IdFrom);

/// <summary>An enrichment the mapping declares (<c>enrich:</c>), which adds to every record it renders.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Mapping">The enrichment mapping it renders, pinned; null for one naming the record's bulk data alone.</param>
/// <param name="Rows"><c>one</c>, <c>many</c>, or null for one reading no rows.</param>
/// <param name="Bulk">True when its files are the record's bulk data.</param>
/// <param name="Description">What the document says of it.</param>
public sealed record MappingOutlineEnrichment(string Name, string? Mapping, string? Rows, bool Bulk, string? Description);

/// <summary>
/// What a mapping does, laid out for a person to read (osdu/docs/reference/concepts/mapping-overview.md): everything it reads
/// and, for every property of the record in the order the document writes them, how the value gets there. It is read from
/// the definition a render reads, so every node of the language has its place, the lookups, lists and <c>$findAll</c> a draft
/// cannot hold among them. Nothing is rendered and no data is read.
/// </summary>
/// <param name="Reference">The mapping, as a flow pins it.</param>
/// <param name="Kind">The kind of the records it renders.</param>
/// <param name="Description">What the document says of it.</param>
/// <param name="Enrichment">True for an enrichment mapping, which renders into a record of another.</param>
/// <param name="Dataset">What identifies a record.</param>
/// <param name="Sources">What it reads: its dataset first, then its parameters, then what it looks in, as the document first reads each.</param>
/// <param name="Record">The record's properties, in the order the document writes them.</param>
/// <param name="Enrichments">What it adds to every record.</param>
public sealed record MappingOutline(
    string Reference,
    string Kind,
    string? Description,
    bool Enrichment,
    MappingOutlineDataset Dataset,
    IReadOnlyList<MappingOutlineSource> Sources,
    IReadOnlyList<MappingOutlineNode> Record,
    IReadOnlyList<MappingOutlineEnrichment> Enrichments)
{
    /// <summary>The id of the record table among <see cref="Sources"/>.</summary>
    public const string DatasetSource = DatasetColumn.Prefix;

    /// <summary>The id of the parameters among <see cref="Sources"/>.</summary>
    public const string ParametersSource = "parameters";

    /// <summary>
    /// The outline of <paramref name="mapping"/>. <paramref name="schema"/> is the template it fills, which gives each property
    /// its type and tells the entity type a <c>ref</c> names; without it those are left out. <paramref name="yaml"/> is the
    /// document's text, which gives each node its lines.
    /// </summary>
    public static MappingOutline Of(MappingDefinition mapping, SchemaSnapshot? schema = null, string? yaml = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new Builder(mapping, schema, yaml).Build();
    }

    /// <summary>A node of the tree as it is put together; ancestors are made as the first entry below them arrives.</summary>
    private sealed class Branch(string path, string name, string shape)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public string Shape { get; set; } = shape;

        public string How { get; set; } = string.Empty;

        public bool Required { get; set; } = true;

        public string? Type { get; set; }

        public string? When { get; set; }

        public string? Where { get; set; }

        public string? Dataset { get; set; }

        public string? Description { get; set; }

        public (int Line, int EndLine)? Lines { get; set; }

        public MappingOutlineFlow? Flow { get; set; }

        public IReadOnlyList<MappingOutlineAssertion> Assertions { get; set; } = [];

        public List<Branch> Children { get; } = [];

        public MappingOutlineNode Freeze() => new(
            Path, Name, Shape, How, Required, Type, When, Where, Dataset, Description, Lines?.Line, Lines?.EndLine, Flow, Assertions,
            Children.Select(child => child.Freeze()).ToList());
    }

    /// <summary>A source as the outline finds it, its fields in the order the document first reads them.</summary>
    private sealed class SourceDraft(string id, string kind, string name, string? detail)
    {
        public string Id { get; } = id;

        public string Kind { get; } = kind;

        public string Name { get; } = name;

        public string? Detail { get; } = detail;

        public List<FieldDraft> Fields { get; } = [];

        public FieldDraft Field(string name)
        {
            var field = Fields.Find(f => string.Equals(f.Name, name, StringComparison.Ordinal));
            if (field is null)
            {
                field = new FieldDraft(name);
                Fields.Add(field);
            }

            return field;
        }

        public MappingOutlineSource Freeze()
            => new(Id, Kind, Name, Detail, Fields.Select(f => new MappingOutlineField(f.Name, f.Key, f.Display, f.Match, f.Reads, f.Description)).ToList());
    }

    private sealed class FieldDraft(string name)
    {
        public string Name { get; } = name;

        public bool Key { get; set; }

        public bool Display { get; set; }

        public bool Match { get; set; }

        public int Reads { get; set; }

        public string? Description { get; set; }
    }

    private sealed class Builder
    {
        private const string ValueRole = "value";
        private const string MatchRole = "match";
        private const string ConditionRole = "condition";
        private const string RecordRoot = "record";
        private const string ItemWord = MappingMapper.ItemKey;

        private readonly MappingDefinition _mapping;
        private readonly SchemaSnapshot? _schema;
        private readonly Dictionary<string, TemplateVariable> _variables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Line, int EndLine)> _lines = new(StringComparer.Ordinal);
        private readonly List<SourceDraft> _sources = [];

        public Builder(MappingDefinition mapping, SchemaSnapshot? schema, string? yaml)
        {
            _mapping = mapping;
            _schema = schema;
            if (schema is not null)
            {
                foreach (var variable in OsduTemplate.From(schema).Variables)
                {
                    _variables[variable.Path.Text] = variable;
                }
            }

            ReadLines(yaml);
        }

        public MappingOutline Build()
        {
            DeclareDataset();
            var root = new List<Branch>();
            if (!_mapping.Fragment)
            {
                root.Add(Identity());
            }

            foreach (var entry in _mapping.Entries)
            {
                Place(root, entry);
            }

            var dataset = _mapping.Dataset;
            return new MappingOutline(
                _mapping.Reference,
                _mapping.Kind,
                _mapping.Description,
                _mapping.Fragment,
                new MappingOutlineDataset(
                    dataset.System, dataset.Key, dataset.Label, dataset.Identity,
                    dataset.IdFrom == MappingIdSource.Key ? "key" : "deliveryKey"),
                Ordered().Select(source => source.Freeze()).ToList(),
                root.Select(branch => branch.Freeze()).ToList(),
                _mapping.Enrich
                    .Select(e => new MappingOutlineEnrichment(e.Name, e.Mapping, e.Rows?.ToString().ToLowerInvariant(), e.Bulk, e.Description))
                    .ToList());
        }

        /// <summary>The dataset's tables first, then the parameters, then what is looked in, each as the document first reads it.</summary>
        private IEnumerable<SourceDraft> Ordered()
            => _sources
                .Select((source, index) => (source, index))
                .OrderBy(s => s.source.Kind switch
                {
                    MappingOutlineSourceKind.Record => 0,
                    MappingOutlineSourceKind.Child => 1,
                    MappingOutlineSourceKind.Parameters => 2,
                    _ => 3,
                })
                .ThenBy(s => s.index)
                .Select(s => s.source);

        /// <summary>The record table, with its key and what names a record to a person, and the parameters the document declares.</summary>
        private void DeclareDataset()
        {
            if (!_mapping.Fragment)
            {
                var table = Source(DatasetSource, MappingOutlineSourceKind.Record, _mapping.Dataset.System, null);
                foreach (var column in _mapping.Dataset.Key)
                {
                    table.Field(column).Key = true;
                }

                foreach (var column in _mapping.Dataset.ReadColumns().Concat(_mapping.Dataset.Identity))
                {
                    table.Field(column).Display = true;
                }
            }

            if (_mapping.Parameters.Count > 0)
            {
                var parameters = Source(ParametersSource, MappingOutlineSourceKind.Parameters, "Parameters", null);
                foreach (var (name, parameter) in _mapping.Parameters)
                {
                    parameters.Field(name).Description = parameter.Description;
                }
            }
        }

        private SourceDraft Source(string id, string kind, string name, string? detail)
        {
            var source = _sources.Find(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (source is null)
            {
                source = new SourceDraft(id, kind, name, detail);
                _sources.Add(source);
            }

            return source;
        }

        /// <summary>The record's id: no node fills it, the engine makes it from the key.</summary>
        private Branch Identity()
        {
            var dataset = _mapping.Dataset;
            var reads = dataset.Key.Select(column => Read(new DatasetColumn(null, column), ValueRole)).ToList();
            var fromKey = dataset.IdFrom == MappingIdSource.Key;
            var step = new MappingOutlineStep(
                "key",
                fromKey ? "key" : "delivery key",
                fromKey
                    ? "The engine writes the id from the key's own values, as a reference catalog names a record by its code."
                    : $"The engine makes the id from a hash of the source system ({dataset.System}) and the key, so a row is always the same record.",
                $"dataset.idFrom: {(fromKey ? "key" : "deliveryKey")}",
                []);
            var summary = string.Join(", ", dataset.Key.Select(column => new DatasetColumn(null, column).ToString()));
            return new Branch("id", "id", MappingOutlineShape.Value)
            {
                How = MappingOutlineHow.Key,
                Type = "string",
                Flow = new MappingOutlineFlow("key", summary, null, reads, [step], false),
            };
        }

        /// <summary>Puts an entry at its place, making the objects above it as the first entry below each arrives.</summary>
        private void Place(List<Branch> root, MappingEntry entry)
        {
            var segments = entry.Target.Segments;
            var level = root;
            for (var depth = 0; depth < segments.Count - 1; depth++)
            {
                var path = PathTo(segments, depth);
                var branch = level.Find(b => string.Equals(b.Path, path, StringComparison.Ordinal));
                if (branch is null)
                {
                    branch = new Branch(path, segments[depth].Name, MappingOutlineShape.Group) { Lines = LinesAt(LocationOf(segments, depth)) };
                    level.Add(branch);
                }

                level = branch.Children;
            }

            level.Add(Node(entry, PathTo(segments, segments.Count - 1), segments[^1].Name));
        }

        /// <summary>The path down to the step at <paramref name="depth"/>, the steps above it into an array marked as such.</summary>
        private static string PathTo(IReadOnlyList<TemplatePathSegment> segments, int depth)
            => string.Join('.', segments.Take(depth + 1).Select((segment, index) => index < depth ? segment.ToString() : segment.Name));

        /// <summary>Where the document writes the object at <paramref name="depth"/>, as a node's location names it.</summary>
        private static string LocationOf(IReadOnlyList<TemplatePathSegment> segments, int depth)
        {
            var location = RecordRoot;
            for (var index = 0; index <= depth; index++)
            {
                location += "." + segments[index].Name;
                if (index < depth && segments[index].IntoArray)
                {
                    location += "." + ItemWord;
                }
            }

            return location;
        }

        private Branch Node(MappingEntry entry, string path, string name)
        {
            if (entry.IsRepeater)
            {
                return Repeat(entry, path, name);
            }

            if (entry.IsCoalesce)
            {
                return Choice(entry, path, name);
            }

            return entry.IsList ? List(entry, path, name) : Value(entry, path, name);
        }

        /// <summary>A branch with what every shape of node says of itself.</summary>
        private Branch Described(MappingEntry entry, string path, string name, string shape)
        {
            var variable = _variables.GetValueOrDefault(entry.Target.Text);
            return new Branch(path, name, shape)
            {
                Required = entry.Required,
                When = entry.AppliesWhen?.Text,
                Where = entry.RowFilter?.Text,
                Description = entry.Description ?? variable?.Description ?? variable?.Title,
                Type = variable is null ? null : TypeOf(variable),
                Lines = LinesAt(entry.Location),
                Assertions = entry.Assertions
                    .Select(a => new MappingOutlineAssertion(a.Label, a.Expected, AssertionWords.Of(a.OnFail), AssertionWords.Of(a.Stage)))
                    .ToList(),
            };
        }

        private static string TypeOf(TemplateVariable variable)
            => variable.Relationships.Count > 0
                ? "reference"
                : variable.Format ?? (variable.ItemType is { } item ? item + "[]" : variable.Type);

        private Branch Repeat(MappingEntry entry, string path, string name)
        {
            var child = entry.Source!.Child!;
            var branch = Described(entry, path, name, MappingOutlineShape.Repeat);
            branch.Dataset = child;
            ChildTable(child);
            foreach (var column in entry.AppliesWhen?.Columns.Concat(entry.RowFilter?.Columns ?? []) ?? entry.RowFilter?.Columns ?? [])
            {
                Read(column, ConditionRole);
            }

            return branch;
        }

        private SourceDraft ChildTable(string child)
            => Source($"{DatasetSource}.{child}", MappingOutlineSourceKind.Child, child, null);

        /// <summary>A <c>$coalesce</c>: one child per alternative, in the order they are tried, each named by its place.</summary>
        private Branch Choice(MappingEntry entry, string path, string name)
        {
            var branch = Described(entry, path, name, MappingOutlineShape.Choice);
            var place = 1;
            foreach (var node in entry.ValueNodes)
            {
                var number = place.ToString(CultureInfo.InvariantCulture);
                var alternative = Value(node, $"{path}|{number}", number);
                alternative.Required = true;
                alternative.Type = null;
                alternative.Description = null;
                alternative.Lines = LinesAt($"{entry.Location}.{MappingMapper.CoalesceKey}[{place - 1}]") ?? branch.Lines;
                branch.Children.Add(alternative);
                place++;
            }

            branch.How = Strongest(branch.Children);
            return branch;
        }

        /// <summary>A list some of whose items read values: one child per item, an object item holding its properties.</summary>
        private Branch List(MappingEntry entry, string path, string name)
        {
            var branch = Described(entry, path, name, MappingOutlineShape.List);
            var place = 0;
            foreach (var part in entry.Parts)
            {
                var number = (place + 1).ToString(CultureInfo.InvariantCulture);
                var itemPath = $"{path}|{number}";
                if (part.IsObject)
                {
                    var item = new Branch(itemPath, number, MappingOutlineShape.Group) { Lines = LinesAt(part.Location) };
                    foreach (var property in part.Properties)
                    {
                        var within = string.Join('.', property.Target.WithinItem);
                        item.Children.Add(Node(property, $"{itemPath}.{within}", within));
                    }

                    branch.Children.Add(item);
                }
                else
                {
                    branch.Children.Add(Value(part, itemPath, number));
                }

                place++;
            }

            branch.How = Strongest(branch.Children);
            return branch;
        }

        /// <summary>What a node holding others counts as: the furthest any of them goes from a column written as it is.</summary>
        private static string Strongest(IEnumerable<Branch> children)
        {
            var hows = Leaves(children).Select(c => c.How).ToHashSet(StringComparer.Ordinal);
            foreach (var how in new[] { MappingOutlineHow.Linked, MappingOutlineHow.LookedUp, MappingOutlineHow.Changed, MappingOutlineHow.Copied })
            {
                if (hows.Contains(how))
                {
                    return how;
                }
            }

            return MappingOutlineHow.Fixed;
        }

        private static IEnumerable<Branch> Leaves(IEnumerable<Branch> branches)
            => branches.SelectMany(b => b.Children.Count == 0 ? [b] : Leaves(b.Children));

        private Branch Value(MappingEntry entry, string path, string name)
        {
            var branch = Described(entry, path, name, MappingOutlineShape.Value);
            branch.Flow = entry.Static is { } literal ? LiteralFlow(entry, literal) : ReadFlow(entry);
            branch.How = HowOf(branch.Flow);
            return branch;
        }

        private static string HowOf(MappingOutlineFlow flow) => flow.Origin switch
        {
            "literal" or "parameter" => MappingOutlineHow.Fixed,
            "parent" or "childRecord" or "base" or "link" => MappingOutlineHow.Linked,
            _ => flow.Steps.Any(step => step.Lookups.Count > 0)
                ? MappingOutlineHow.LookedUp
                : flow.Steps.Count > 0 ? MappingOutlineHow.Changed : MappingOutlineHow.Copied,
        };

        /// <summary>A literal: its text as written, entering only the parameters it names, the whole of a text that is one token being the parameter itself.</summary>
        private MappingOutlineFlow LiteralFlow(MappingEntry entry, JsonNode literal)
        {
            var texts = Texts(literal).ToList();
            var names = texts.SelectMany(MappingEntry.ParameterNames).Distinct(StringComparer.Ordinal).ToList();
            var reads = names.Select(name => ParameterRead(name, ValueRole)).ToList();
            reads.AddRange(ConditionReads(entry));
            var plain = texts.Count == 1 && names.Count == 1 && texts[0].Trim() == $"{{$param.{names[0]}}}";
            var text = literal.ToJsonString();
            return new MappingOutlineFlow(plain ? "parameter" : "literal", text, plain ? null : text, reads, [], false);
        }

        private static IEnumerable<string> Texts(JsonNode? node) => node switch
        {
            JsonArray array => array.SelectMany(Texts),
            JsonObject item => item.SelectMany(property => Texts(property.Value)),
            JsonValue value when value.TryGetValue<string>(out var text) => [text],
            _ => [],
        };

        private MappingOutlineFlow ReadFlow(MappingEntry node)
        {
            var source = node.Source!;
            var reads = new List<MappingOutlineRead>();
            var steps = new List<MappingOutlineStep>();
            var origin = source.Kind switch
            {
                MappingSourceKind.DatasetColumn => "column",
                MappingSourceKind.Expression => "expression",
                MappingSourceKind.Cache or MappingSourceKind.Search => "find",
                MappingSourceKind.Parent => "parent",
                MappingSourceKind.Child => "childRecord",
                MappingSourceKind.Base => "base",
                _ => "link",
            };

            switch (source.Kind)
            {
                case MappingSourceKind.DatasetColumn:
                    reads.Add(Read(source.Column!, ValueRole));
                    break;
                case MappingSourceKind.Expression:
                    var expression = source.Expression!;
                    reads.AddRange(expression.Columns.Select(column => Read(column, ValueRole)));
                    reads.AddRange(expression.Parameters.Select(name => ParameterRead(name, ValueRole)));
                    steps.Add(new MappingOutlineStep("expr", "expr", "Computes the value from the row.", expression.Text, []));
                    break;
                case MappingSourceKind.Parent:
                    // The parent's kind is the template's link to it, which a mapping read without its template cannot tell.
                    reads.Add(Other(MappingOutlineSourceKind.Parent, "parent", "Parent record", _schema?.Parent?.Kind, source.ParentPath!));
                    break;
                case MappingSourceKind.Child:
                    reads.Add(Other(MappingOutlineSourceKind.ChildRecord, $"child.{source.ChildKind}", ChildName(source.ChildKind!), source.ChildKind, "id"));
                    break;
                case MappingSourceKind.Base:
                    reads.Add(Other(MappingOutlineSourceKind.Base, "base", "Base record", null, source.BasePath!));
                    break;
                case MappingSourceKind.Link:
                    reads.Add(Other(MappingOutlineSourceKind.Link, $"link.{source.LinkName}", source.LinkName!, null, "ids"));
                    break;
            }

            // A node finding a record changes the value it compares, so what it changes comes before what it finds; the id a
            // node builds is always last.
            foreach (var modifier in node.Modifiers.Where(m => !source.Resolves || !m.BuildsId))
            {
                steps.Add(ModifierStep(node, modifier, reads));
            }

            if (source.Resolves)
            {
                steps.Add(Find(node, source, reads));
                foreach (var modifier in node.Modifiers.Where(m => m.BuildsId))
                {
                    steps.Add(ModifierStep(node, modifier, reads));
                }
            }

            reads.AddRange(ConditionReads(node));
            return new MappingOutlineFlow(origin, EntryValues.Origin(node), null, reads.Distinct().ToList(), steps, node.Unverified);
        }

        private static string ChildName(string kind)
        {
            var segments = kind.Split(':');
            return segments.Length > 2 ? SqlFlow.Delivery.Identity.TargetId.EntityTypeFromKind(kind) : kind;
        }

        /// <summary>A field of another record, a source of its own.</summary>
        private MappingOutlineRead Other(string kind, string id, string name, string? detail, string field)
        {
            Source(id, kind, name, detail).Field(field).Reads++;
            return new MappingOutlineRead(id, field, ValueRole);
        }

        private IEnumerable<MappingOutlineRead> ConditionReads(MappingEntry node)
            => node.AppliesWhen is { } condition
                ? condition.Columns.Select(column => Read(column, ConditionRole)).Concat(condition.Parameters.Select(name => ParameterRead(name, ConditionRole))).ToList()
                : [];

        /// <summary>A field read of a dataset's table, and the read that says so.</summary>
        private MappingOutlineRead Read(DatasetColumn column, string role)
        {
            var table = column.Child is null ? Source(DatasetSource, MappingOutlineSourceKind.Record, _mapping.Dataset.System, null) : ChildTable(column.Child);
            table.Field(column.Column).Reads++;
            return new MappingOutlineRead(table.Id, column.Column, role);
        }

        private MappingOutlineRead ParameterRead(string name, string role)
        {
            Source(ParametersSource, MappingOutlineSourceKind.Parameters, "Parameters", null).Field(name).Reads++;
            return new MappingOutlineRead(ParametersSource, name, role);
        }

        /// <summary>The step finding the record a cache, lookup or search node reads, and the values its lines compare entering the flow.</summary>
        private MappingOutlineStep Find(MappingEntry node, MappingSource source, List<MappingOutlineRead> reads)
        {
            var searched = source.Kind == MappingSourceKind.Search;
            var type = source.CacheType!;
            var id = searched ? $"{MappingSource.SearchPrefix}.{type}" : CacheId(type);
            var search = searched ? _mapping.Searches.GetValueOrDefault(type) : null;
            var draft = Source(
                id,
                searched ? MappingOutlineSourceKind.Search : MappingOutlineSourceKind.Cache,
                type,
                search?.Kind ?? (source.Lookup is null ? null : _mapping.Lookups.GetValueOrDefault(source.Lookup)?.Description));

            var matches = new List<MappingOutlineMatch>();
            string what;
            if (node.FindAll is { } all)
            {
                draft.Field(all.Field).Match = true;
                matches.Add(new MappingOutlineMatch(all.Field, all.Operand.ToString()));
                if (all.Operand.Column is { } operand)
                {
                    reads.Add(Read(operand, MatchRole));
                }

                foreach (var column in all.Operand.Lookup?.Columns ?? [])
                {
                    reads.Add(Read(column, MatchRole));
                }

                var empty = all.Empty.Count == 0 ? string.Empty : $", and nothing under {string.Join(" and ", all.Empty)}";
                what = $"Every row whose {all.Field} holds the value, or any of its values, ignoring case{empty}, adds its {source.CacheField} to the list.";
            }
            else
            {
                foreach (var line in node.FindBy)
                {
                    draft.Field(line.Field).Match = true;
                    matches.Add(new MappingOutlineMatch(line.Field, line.Column?.ToString() ?? $"'{line.Literal}'"));
                    if (line.Column is { } column)
                    {
                        reads.Add(Read(column, MatchRole));
                    }
                }

                // The lines and the field read are the lookup's own; this says how they decide, as a render does.
                var order = node.FindBy.Count > 1
                    ? "Each line is tried in turn, and the first to find exactly one record gives it."
                    : "The line has to find exactly one record.";
                var loose = searched
                    ? ", then ignoring case where the platform's index allows"
                    : node.IgnoreSeparators ? ", then ignoring case, then ignoring punctuation and spacing" : ", then ignoring case";
                var where = searched ? $" Searches {search?.Kind ?? type} on the platform." : string.Empty;
                var none = node.Required ? ", and so does finding none" : "; finding none gives no value";
                what = $"{order}{where} A value matches as written{loose}. Several matching records hold the record{none}.";
            }

            draft.Field(source.CacheField!).Reads++;
            return new MappingOutlineStep("find", searched ? "search" : "cache", what, source.ToString(), [new MappingOutlineLookup(id, source.CacheField, matches)]);
        }

        private static string CacheId(string type) => $"{MappingSource.CachePrefix}.{type}";

        /// <summary>One modifier as a step, what it reads of the row entering the flow and the tables it reads named.</summary>
        private MappingOutlineStep ModifierStep(MappingEntry node, Modifier modifier, List<MappingOutlineRead> reads)
        {
            var text = modifier.ToString();
            switch (modifier.Kind)
            {
                case ModifierKind.Trim:
                    return new MappingOutlineStep("trim", "trim", "Takes the spaces off both ends.", text, []);
                case ModifierKind.Upper:
                    return new MappingOutlineStep("upper", "upper", "Writes it in upper case.", text, []);
                case ModifierKind.Lower:
                    return new MappingOutlineStep("lower", "lower", "Writes it in lower case.", text, []);
                case ModifierKind.Split:
                    var part = modifier.Part.GetValueOrDefault().ToString(CultureInfo.InvariantCulture);
                    return new MappingOutlineStep("split", $"split {part}", $"Splits it on '{modifier.Separator}' and keeps part {part}.", text, []);
                case ModifierKind.Equals:
                    return new MappingOutlineStep("equals", "equals", $"True when the value is {modifier.Text}, otherwise false.", text, []);
                case ModifierKind.Date:
                    return new MappingOutlineStep("date", "date", modifier.Text is null ? "Reads it as a date." : $"Reads it as a date written {modifier.Text}.", text, []);
                case ModifierKind.Number:
                    return new MappingOutlineStep("number", "number", "Reads it as a number.", text, []);
                case ModifierKind.Replace when modifier.Table is { } table:
                    var cached = Source(CacheId(table.CacheType), MappingOutlineSourceKind.Cache, table.CacheType, null);
                    if (table.Match is { } match)
                    {
                        cached.Field(match).Match = true;
                    }

                    if (table.Field is { } field)
                    {
                        cached.Field(field).Reads++;
                    }

                    return new MappingOutlineStep(
                        "replace",
                        "replace",
                        $"Replaces the value by what {table.CacheType} holds for it.{Fallback(modifier)}",
                        text,
                        [new MappingOutlineLookup(cached.Id, table.Field, table.Match is null ? [] : [new MappingOutlineMatch(table.Match, "the value")])]);
                case ModifierKind.Replace:
                    var pairs = string.Join(", ", modifier.Replacements.Select(pair => $"{pair.Key} to {pair.Value ?? "no value"}"));
                    return new MappingOutlineStep("replace", "replace", $"Replaces {pairs}.{Fallback(modifier)}", text, []);
                case ModifierKind.Id:
                    return IdStep(modifier, reads, text);
                default:
                    var entityType = EntityTypeOf(node, modifier);
                    return new MappingOutlineStep(
                        "ref",
                        "ref",
                        (entityType is null ? "Makes the value a reference to the record it names." : $"Makes the value a reference to a {entityType} record.")
                            + (node.Unverified ? " It is written even when the cache holds no such record." : " A reference the cache cannot find holds the record."),
                        text,
                        []);
            }
        }

        private static string Fallback(Modifier modifier) => modifier.Otherwise.Kind switch
        {
            ReplaceFallbackKind.Empty => " A value it does not list gives no value.",
            ReplaceFallbackKind.Text => $" A value it does not list becomes {modifier.Otherwise.Text}.",
            _ => " A value it does not list stays as it is.",
        };

        private MappingOutlineStep IdStep(Modifier modifier, List<MappingOutlineRead> reads, string text)
        {
            var template = modifier.Id!;
            reads.AddRange(template.Columns.Select(column => Read(column, ValueRole)));
            var lookups = new List<MappingOutlineLookup>();
            foreach (var token in template.Tokens)
            {
                switch (token.Kind)
                {
                    case IdTokenKind.Cache:
                        var cached = Source(CacheId(token.CacheType!), MappingOutlineSourceKind.Cache, token.CacheType!, null);
                        cached.Field(token.CacheField!).Reads++;
                        lookups.Add(new MappingOutlineLookup(cached.Id, token.CacheField, []));
                        break;
                    case IdTokenKind.Parameter:
                        Source(ParametersSource, MappingOutlineSourceKind.Parameters, "Parameters", null).Field(token.Parameter!).Reads++;
                        break;
                }
            }

            var built = template.EntityType is { } entity ? $"Builds the id of a {entity} record." : "Builds an id from its template.";
            return new MappingOutlineStep("id", "id", built, text, lookups);
        }

        /// <summary>The entity type a <c>ref</c> names, as written or as the template tells it; null when neither does.</summary>
        private string? EntityTypeOf(MappingEntry node, Modifier modifier)
        {
            if (modifier.Id is { EntityType: { } written })
            {
                return written;
            }

            return _schema is null ? modifier.EntityType : MappingRenderer.ResolveReference(node, modifier, _schema).Template?.EntityType ?? modifier.EntityType;
        }

        private (int Line, int EndLine)? LinesAt(string? location)
            => location is not null && _lines.TryGetValue(location, out var lines) ? lines : null;

        /// <summary>
        /// Where each node of the document's <c>record</c> block is written, by the location the mapper names it with: a key by
        /// its dotted path, an item of a list by its place. A document that does not parse here gives no lines; it was read
        /// before it got this far, and the lines are only a convenience.
        /// </summary>
        private void ReadLines(string? yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return;
            }

            var stream = new YamlStream();
            try
            {
                using var reader = new StringReader(yaml);
                stream.Load(reader);
            }
            catch (YamlException)
            {
                return;
            }

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return;
            }

            var text = new DimensionYamlSource.Text(yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
            foreach (var (key, value) in root.Children)
            {
                if (key is YamlScalarNode { Value: RecordRoot })
                {
                    Walk(text, RecordRoot, key, value);
                }
            }
        }

        private void Walk(DimensionYamlSource.Text text, string location, YamlNode key, YamlNode value)
        {
            var span = text.Entry(location, key, value);
            _lines[location] = (span.Line, span.EndLine);
            switch (value)
            {
                case YamlMappingNode mapping:
                    foreach (var (childKey, child) in mapping.Children)
                    {
                        if (childKey is YamlScalarNode { Value: { } name })
                        {
                            Walk(text, $"{location}.{name}", childKey, child);
                        }
                    }

                    break;
                case YamlSequenceNode sequence:
                    var index = 0;
                    foreach (var item in sequence.Children)
                    {
                        Walk(text, string.Create(CultureInfo.InvariantCulture, $"{location}[{index}]"), item, item);
                        index++;
                    }

                    break;
            }
        }
    }
}
