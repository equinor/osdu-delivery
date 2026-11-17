using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Query;
using SqlFlow.Core.Reports;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Yaml;

/// <summary>
/// A report document (<c>flowType: rpt</c>). It takes the place every consumer gives a flow document (a pipeline row with
/// its text and definition, the editor, <c>validate</c>), and is not a flow: it is in no wave, takes no schedule, and
/// every run of it is refused (<see cref="YamlDocumentLoader.ValidateRunParameters"/>). Its queries are run one at a time
/// when a person opens the report, each as a read-only statement on a worker.
/// </summary>
public sealed record ReportFlowDocument : RegisteredFlowDocument
{
    /// <summary>The report as written.</summary>
    public required ReportFlow Flow { get; init; }

    /// <inheritdoc />
    public override string Name => Flow.Name;

    /// <inheritdoc />
    public override string Kind => ReportFlow.FlowType;

    /// <inheritdoc />
    public override string? Batch => Flow.Batch;

    /// <summary>The connection the report's queries read, so the report is listed among what uses that connection.</summary>
    public override string? SourceConnectionReference => Flow.Connection;

    /// <inheritdoc />
    public override string? SourceReference => Flow.Database;

    /// <inheritdoc />
    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences
        => Flow.Queries
            .Where(q => q.Connection is not null)
            .Select(q => new KeyValuePair<string, string>($"queries.{q.Name}.connection", q.Connection!))
            .Prepend(new KeyValuePair<string, string>("connection", Flow.Connection));

    /// <summary>False: a report moves no data, so the lineage graph holds no node, edge or wave for it.</summary>
    public override bool ParticipatesInLineage => false;
}

/// <summary>
/// The report kind (<c>flowType: rpt</c>). It parses the document and refuses every run: a report has no operation a
/// trigger, a schedule, a batch or the command line could start. Its queries are run from its page.
/// </summary>
public sealed class ReportFlowKind : IFlowDocumentKind
{
    /// <summary>What a caller is told when it asks for a report to be run as a flow.</summary>
    public const string NeverRuns =
        "a report (flowType: rpt) is a set of read-only queries a person runs on demand from the report's page; "
        + "it is not a flow, so there is nothing to run, schedule or trigger.";

    /// <inheritdoc />
    public string FlowType => ReportFlow.FlowType;

    /// <inheritdoc />
    public string Description => "a report: read-only queries run on demand and shown as figures, charts and tables (never run as a flow)";

    /// <inheritdoc />
    public IReadOnlyList<FlowKindOperation> Operations => [];

    /// <inheritdoc />
    public RegisteredFlowDocument Parse(string yaml, string source)
        => new ReportFlowDocument
        {
            Flow = YamlReportFlowLoader.Parse(yaml, source),
            Mode = ExecutionMode.Disabled,
        };

    /// <summary>Always refuses: see <see cref="NeverRuns"/>.</summary>
    public void ValidateParameters(RunParameters parameters) => throw new SqlFlowException(NeverRuns);
}

/// <summary>
/// Reads a report (<see cref="ReportFlow"/>). The reading is strict: an unknown key, a value of the wrong shape, a value
/// over its limit, a statement that refers to a parameter the report does not declare, or a view key its view type does
/// not take is a problem, since a misspelt key would otherwise leave a tile, a filter or a format out without a word.
/// Every problem names its line, and all of them are reported together so a document is fixed in one pass. Whether each
/// statement is one read-only <c>SELECT</c> is decided by the T-SQL parser, where the document is checked and again
/// where a statement runs.
/// </summary>
public static partial class YamlReportFlowLoader
{
    /// <summary>Reads <paramref name="yaml"/>; <paramref name="source"/> labels every problem (the file's path, or
    /// "&lt;inline&gt;"). Throws <see cref="FlowValidationException"/> listing every problem.</summary>
    public static ReportFlow Parse(string yaml, string source = "<inline>")
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
                : $"{source}: the file holds {stream.Documents.Count} YAML documents; a report holds one.");
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FlowValidationException($"{source}: a report must be a mapping that starts with 'flowType: rpt'.");
        }

        var flow = reader.Flow(root);

        // A report is stored with what reads as a credential taken out (as every document is), and it is the stored text
        // that is shown and whose statements run. The reader refuses a credential in any value, with its line; this
        // refuses one anywhere else (a comment, say), so the text that is stored is always the text that was written.
        if (reader.Problems.Count == 0 && !string.Equals(SecretHygiene.RedactedMessage(yaml), yaml, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{source}: the document carries what reads as a credential outside its values (in a comment, say): a "
                + "keyword such as 'password' or 'secret' followed by '=' and a value, or an access token. A report is "
                + "stored and shown as written, so it holds none; write the text without it.");
        }

        if (reader.Problems.Count > 0 || flow is null)
        {
            throw new FlowValidationException(reader.Problems.Count == 1
                ? reader.Problems[0]
                : $"{source}: {reader.Problems.Count} problems in the report:{Environment.NewLine}  "
                  + string.Join(Environment.NewLine + "  ", reader.Problems));
        }

        return flow;
    }

    /// <summary>Whether <paramref name="name"/> can name a query: a letter or underscore, then letters, digits,
    /// underscores or hyphens, at most <see cref="ReportFlow.MaxQueryNameLength"/> characters.</summary>
    public static bool IsValidQueryName(string name) => QueryNamePattern().IsMatch(name);

    private static int Line(YamlNode node) => (int)node.Start.Line;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex QueryNamePattern();

    private sealed class Reader(string source)
    {
        private const string Report = "the report";

        private static readonly string[] RootKeys =
        [
            "flowType", "name", "title", "description", "batch", "connection", "database", "pool", "timeoutSeconds",
            "maxRows", "parameters", "queries",
        ];

        /// <summary>Keys a flow that runs takes and a report cannot, each answered with the reason.</summary>
        private static readonly string[] RunKeys =
            ["schedule", "mode", "lifecycle", "connections", "server", "source", "target", "load", "onErrorResume"];

        private static readonly string[] ParameterKeys =
            ["label", "description", "type", "default", "required", "choices", "choicesFrom"];

        private static readonly string[] ChoiceKeys = ["value", "label"];

        private static readonly string[] QueryKeys =
        [
            "title", "description", "section", "connection", "database", "timeoutSeconds", "maxRows", "sql", "show",
            "columns",
        ];

        private static readonly string[] ColumnKeys = ["label", "format", "decimals", "unit"];

        private static readonly string[] ViewKeys =
            ["type", "title", "description", "width", "by", "values", "series", "across", "stacked", "top", "selects"];

        /// <summary>The keys each view type takes beside <c>type</c>, <c>title</c>, <c>description</c> and
        /// <c>width</c>.</summary>
        private static readonly Dictionary<string, string[]> ViewTypeKeys = new(StringComparer.Ordinal)
        {
            [ReportViewTypes.Auto] = [],
            [ReportViewTypes.Figure] = ["values"],
            [ReportViewTypes.Bar] = ["by", "values", "series", "stacked", "top", "selects"],
            [ReportViewTypes.Column] = ["by", "values", "series", "stacked", "selects"],
            [ReportViewTypes.Line] = ["by", "values", "series"],
            [ReportViewTypes.Share] = ["by", "values", "top", "selects"],
            [ReportViewTypes.Table] = ["by", "values", "selects"],
            [ReportViewTypes.Matrix] = ["by", "across", "values", "selects"],
            [ReportViewTypes.None] = [],
        };

        public List<string> Problems { get; } = [];

        /// <summary>Every parameter the document declares under a name a statement can write, whether or not the
        /// parameter itself loaded. A statement or a view that refers to one that did not load is not told the
        /// parameter is missing: what is wrong with it has been said once, where it is declared.</summary>
        private readonly List<string> _declared = [];

        private void Problem(int line, string message)
            => Problems.Add(line > 0 ? $"{source}:{line}: {message}" : $"{source}: {message}");

        public ReportFlow? Flow(YamlMappingNode root)
        {
            var map = Keys(root, RootKeys, Report, RunKeys);

            var name = Text(map, "name", root, Report, required: true, max: ReportFlow.MaxNameLength);
            var title = Text(map, "title", root, Report, max: ReportFlow.MaxTitleLength, prose: true);
            var description = Text(map, "description", root, Report, max: ReportFlow.MaxDescriptionLength, keepLines: true);
            var batch = Text(map, "batch", root, Report, max: ReportFlow.MaxBatchLength);
            var connection = Connection(map, root, Report, required: true);
            var database = Database(map, root, Report);
            var pool = Text(map, "pool", root, Report, max: ReportFlow.MaxPoolLength);
            var timeout = Number(map, "timeoutSeconds", Report, 1, ReportFlow.MaxTimeoutSeconds, "seconds a query may run");
            var maxRows = Number(map, "maxRows", Report, 1, ReportFlow.MaxMaxRows, "rows a query answers");

            var parameters = Parameters(map);
            var queries = Queries(map, root);

            // What a parameter's choices are read from, and whether every parameter is of use, can only be said once both
            // the parameters and the queries are read.
            foreach (var parameter in parameters)
            {
                if (parameter.Value.ChoicesFrom is not { } from)
                {
                    continue;
                }

                var feeding = queries.FirstOrDefault(q => string.Equals(q.Query.Name, from, StringComparison.OrdinalIgnoreCase));
                if (feeding.Query is null)
                {
                    Problem(parameter.Line, queries.Count == 0
                        ? $"'choicesFrom' of parameter '{parameter.Value.Name}' names the query '{Clip(from)}', and the report has no query that loaded."
                        : $"'choicesFrom' of parameter '{parameter.Value.Name}' names the query '{Clip(from)}', which the report does not have; its queries are: {string.Join(", ", queries.Select(q => q.Query.Name))}.");
                }
                else if (feeding.Query.Parameters.Contains(parameter.Value.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Problem(parameter.Line, $"'choicesFrom' of parameter '{parameter.Value.Name}' names the query '{feeding.Query.Name}', whose statement binds @{parameter.Value.Name} itself; the choices of a parameter cannot depend on its own value.");
                }
            }

            if (Problems.Count == 0)
            {
                foreach (var parameter in parameters)
                {
                    if (!queries.Any(q => q.Query.Parameters.Contains(parameter.Value.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        Problem(parameter.Line, $"parameter '{parameter.Value.Name}' is bound by no query, so setting it would change nothing; refer to it in a statement as @{parameter.Value.Name}, or remove it.");
                    }
                }

                if (queries.Count > 0 && queries.All(q => q.Query.Views.Count == 0))
                {
                    Problem(Line(root), "every query of the report has 'show: none', so the report shows nothing; show at least one query.");
                }
            }

            if (Problems.Count > 0 || name is null || connection is null)
            {
                return null;
            }

            // A choicesFrom is kept as the query spells its own name, so a request finds it whatever case the document used.
            var resolved = parameters
                .Select(p => p.Value.ChoicesFrom is { } from
                    ? p.Value with { ChoicesFrom = queries.First(q => string.Equals(q.Query.Name, from, StringComparison.OrdinalIgnoreCase)).Query.Name }
                    : p.Value)
                .ToList();

            return new ReportFlow
            {
                Name = name,
                Title = title,
                Description = description,
                Batch = batch,
                Connection = connection,
                Database = database,
                Pool = pool,
                TimeoutSeconds = timeout ?? ReportFlow.DefaultTimeoutSeconds,
                MaxRows = maxRows ?? ReportFlow.DefaultMaxRows,
                Parameters = resolved,
                Queries = [.. queries.Select(q => q.Query)],
            };
        }

        private List<(ReportParameter Value, int Line)> Parameters(Dictionary<string, YamlNode> root)
        {
            var parameters = new List<(ReportParameter, int)>();
            if (!root.TryGetValue("parameters", out var node) || IsNothing(node))
            {
                return parameters;
            }

            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), "'parameters' must be a mapping of parameter name to what the parameter is.");
                return parameters;
            }

            if (mapping.Children.Count > ReportFlow.MaxParameters)
            {
                Problem(Line(node), $"'parameters' declares {mapping.Children.Count} parameters; a report may declare at most {ReportFlow.MaxParameters}.");
                return parameters;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } raw } || raw.Trim() is not { Length: > 0 } name)
                {
                    Problem(Line(keyNode), "'parameters' has a parameter whose name is not text.");
                    continue;
                }

                if (!QueryParameter.IsValidName(name))
                {
                    Problem(Line(keyNode), $"parameter '{Clip(name)}' has a name a statement cannot refer to; a name is a letter or underscore followed by letters, digits or underscores, at most {QueryParameter.MaxNameLength} characters, and is written @name in a statement.");
                    continue;
                }

                if (!seen.Add(name))
                {
                    Problem(Line(keyNode), $"parameter '{name}' is declared twice; parameter names are compared without regard to case, as a statement compares them.");
                    continue;
                }

                _declared.Add(name);
                if (Parameter(name, keyNode, value) is { } parameter)
                {
                    parameters.Add((parameter, Line(keyNode)));
                }
            }

            return parameters;
        }

        private ReportParameter? Parameter(string name, YamlNode keyNode, YamlNode value)
        {
            var where = $"parameter '{name}'";

            // A parameter listed with nothing after it is a text value with no default and no fixed choices.
            if (IsNothing(value))
            {
                return new ReportParameter { Name = name };
            }

            if (value is not YamlMappingNode mapping)
            {
                Problem(Line(value), $"{where} must be a mapping with 'label', 'type', 'default', 'required' and 'choices' or 'choicesFrom', or be left empty for a text value.");
                return null;
            }

            var before = Problems.Count;
            var map = Keys(mapping, ParameterKeys, where);
            var label = Text(map, "label", mapping, where, max: ReportFlow.MaxLabelLength, prose: true);
            var description = Text(map, "description", mapping, where, max: ReportFlow.MaxDescriptionLength, keepLines: true);
            var type = Token(map, "type", where, QueryParameterTypes.All, "type") ?? QueryParameterTypes.Text;
            var required = Flag(map, "required", where) ?? false;
            var choicesFrom = Text(map, "choicesFrom", mapping, where, max: ReportFlow.MaxQueryNameLength);

            string? fallback = null;
            if (map.TryGetValue("default", out var defaultNode) && !IsNothing(defaultNode))
            {
                fallback = Value(defaultNode, type, $"'default' of {where}");
            }

            var choices = new List<ReportChoice>();
            if (map.TryGetValue("choices", out var choicesNode))
            {
                if (choicesFrom is not null)
                {
                    Problem(Line(choicesNode), $"{where} has both 'choices' and 'choicesFrom'; its choices are written in the document or read from a query, not both.");
                }
                else if (type == QueryParameterTypes.Flag)
                {
                    Problem(Line(choicesNode), $"{where} is a flag, which is on or off, so it takes no 'choices'.");
                }
                else
                {
                    choices = Choices(choicesNode, type, where);
                }
            }
            else if (choicesFrom is not null && type == QueryParameterTypes.Flag)
            {
                Problem(Line(map["choicesFrom"]), $"{where} is a flag, which is on or off, so it takes no 'choicesFrom'.");
            }

            if (fallback is not null && choices.Count > 0
                && !choices.Any(c => string.Equals(c.Value, fallback, StringComparison.Ordinal)))
            {
                Problem(Line(map["default"]), $"'default' of {where} is not one of its 'choices'.");
            }

            if (Problems.Count > before)
            {
                return null;
            }

            return new ReportParameter
            {
                Name = name,
                Label = label,
                Description = description,
                Type = type,
                Default = fallback,
                Required = required,
                Choices = choices,
                ChoicesFrom = choicesFrom,
            };
        }

        private List<ReportChoice> Choices(YamlNode node, string type, string where)
        {
            var choices = new List<ReportChoice>();
            var items = Sequence(node, $"'choices' of {where}", ReportFlow.MaxChoices);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                var what = $"choice {i + 1} of {where}";
                string? value;
                string? label = null;
                switch (items[i])
                {
                    case YamlScalarNode scalar when !IsNothing(scalar):
                        value = Value(scalar, type, what);
                        break;

                    case YamlMappingNode mapping:
                    {
                        var map = Keys(mapping, ChoiceKeys, what);
                        if (!map.TryGetValue("value", out var valueNode) || IsNothing(valueNode))
                        {
                            Problem(Line(mapping), $"{what} has no 'value'.");
                            continue;
                        }

                        value = Value(valueNode, type, $"'value' of {what}");
                        label = Text(map, "label", mapping, what, max: ReportFlow.MaxLabelLength, prose: true);
                        break;
                    }

                    default:
                        Problem(Line(items[i]), $"{what} must be a value, or a mapping with 'value' and 'label'.");
                        continue;
                }

                if (value is null)
                {
                    continue;
                }

                if (!seen.Add(value))
                {
                    Problem(Line(items[i]), $"{what} repeats a value an earlier choice has.");
                    continue;
                }

                choices.Add(new ReportChoice(value, label));
            }

            return choices;
        }

        /// <summary>One value of a parameter's type, as the type writes it (<see cref="QueryParameter.TryRead"/>).</summary>
        private string? Value(YamlNode node, string type, string what)
        {
            if (node is not YamlScalarNode { Value: { } raw })
            {
                Problem(Line(node), $"{what} must be a single value.");
                return null;
            }

            if (!QueryParameter.TryRead(type, raw, out _, out var canonical, out var problem))
            {
                Problem(Line(node), $"{what} is not {QueryParameterTypes.Named(type)}: {problem}.");
                return null;
            }

            if (type == QueryParameterTypes.Text && !Plain(canonical, node, what, prose: true))
            {
                return null;
            }

            return canonical;
        }

        private List<(ReportQuery Query, int Line)> Queries(
            Dictionary<string, YamlNode> root, YamlMappingNode owner)
        {
            var queries = new List<(ReportQuery, int)>();
            if (!root.TryGetValue("queries", out var node))
            {
                Problem(Line(owner), "the report has no 'queries', so it shows nothing; declare at least one.");
                return queries;
            }

            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), IsNothing(node)
                    ? "'queries' is empty, so the report shows nothing; declare at least one query."
                    : "'queries' must be a mapping of query name to the query.");
                return queries;
            }

            if (mapping.Children.Count == 0)
            {
                Problem(Line(node), "'queries' is empty, so the report shows nothing; declare at least one query.");
                return queries;
            }

            if (mapping.Children.Count > ReportFlow.MaxQueries)
            {
                Problem(Line(node), $"'queries' declares {mapping.Children.Count} queries; a report may declare at most {ReportFlow.MaxQueries}. Split it into several reports.");
                return queries;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } raw } || raw.Trim() is not { Length: > 0 } name)
                {
                    Problem(Line(keyNode), "'queries' has a query whose name is not text.");
                    continue;
                }

                if (!IsValidQueryName(name))
                {
                    Problem(Line(keyNode), $"query '{Clip(name)}' has a name an address cannot carry; a name is a letter or underscore followed by letters, digits, underscores or hyphens, at most {ReportFlow.MaxQueryNameLength} characters. Its heading is written under 'title'.");
                    continue;
                }

                if (!seen.Add(name))
                {
                    Problem(Line(keyNode), $"query '{name}' is declared twice; query names are compared without regard to case.");
                    continue;
                }

                if (Query(name, value) is { } query)
                {
                    queries.Add((query, Line(keyNode)));
                }
            }

            return queries;
        }

        private ReportQuery? Query(string name, YamlNode value)
        {
            var where = $"query '{name}'";
            if (value is not YamlMappingNode mapping)
            {
                Problem(Line(value), $"{where} must be a mapping with 'sql' and optionally 'title', 'description', 'section', 'show' and 'columns'.");
                return null;
            }

            var before = Problems.Count;
            var map = Keys(mapping, QueryKeys, where);
            var title = Text(map, "title", mapping, where, max: ReportFlow.MaxTitleLength, prose: true);
            var description = Text(map, "description", mapping, where, max: ReportFlow.MaxDescriptionLength, keepLines: true);
            var section = Text(map, "section", mapping, where, max: ReportFlow.MaxTitleLength, prose: true);
            var connection = Connection(map, mapping, where, required: false);
            var database = Database(map, mapping, where);
            var timeout = Number(map, "timeoutSeconds", where, 1, ReportFlow.MaxTimeoutSeconds, "seconds the query may run");
            var maxRows = Number(map, "maxRows", where, 1, ReportFlow.MaxMaxRows, "rows the query answers");

            var sql = Sql(map, mapping, where);
            var bound = new List<string>();
            if (sql is not null)
            {
                foreach (var referred in ReportSql.ParameterNames(sql))
                {
                    var declared = _declared.FirstOrDefault(p => string.Equals(p, referred, StringComparison.OrdinalIgnoreCase));
                    if (declared is null)
                    {
                        Problem(Line(map["sql"]), _declared.Count == 0
                            ? $"'sql' of {where} refers to @{Clip(referred)}, and the report declares no parameter; declare it under 'parameters', or write the value in the statement."
                            : $"'sql' of {where} refers to @{Clip(referred)}, which the report does not declare; its parameters are: {string.Join(", ", _declared)}.");
                        continue;
                    }

                    bound.Add(declared);
                }
            }

            var views = Views(map, where);
            var columns = Columns(map, where);

            if (Problems.Count > before || sql is null)
            {
                return null;
            }

            return new ReportQuery
            {
                Name = name,
                Title = title,
                Description = description,
                Section = section,
                Connection = connection,
                Database = database,
                TimeoutSeconds = timeout,
                MaxRows = maxRows,
                Sql = sql,
                Parameters = bound,
                Views = views,
                Columns = columns,
            };
        }

        private string? Sql(Dictionary<string, YamlNode> map, YamlMappingNode owner, string where)
        {
            if (!map.TryGetValue("sql", out var node))
            {
                Problem(Line(owner), $"{where} has no 'sql'.");
                return null;
            }

            if (node is not YamlScalarNode scalar || IsNothing(node) || scalar.Value?.Trim() is not { Length: > 0 } sql)
            {
                Problem(Line(node), $"'sql' of {where} must be one SELECT statement, written as text.");
                return null;
            }

            if (sql.Length > ReportFlow.MaxSqlLength)
            {
                Problem(Line(node), $"'sql' of {where} is {sql.Length} characters; a statement may be at most {ReportFlow.MaxSqlLength}.");
                return null;
            }

            if (!string.Equals(SecretHygiene.RedactedMessage(sql), sql, StringComparison.Ordinal))
            {
                // The statement is never echoed: it holds the secret.
                Problem(Line(node), $"'sql' of {where} appears to carry a credential (a keyword such as 'password' followed by '=' and a value); a report's statement is stored and shown to everyone who opens the report, so it holds no secret. A comparison with a column of such a name is written with spaces around the '='.");
                return null;
            }

            return sql;
        }

        private List<ReportView> Views(Dictionary<string, YamlNode> map, string where)
        {
            // Nothing said: the answer's shape decides how it is shown.
            if (!map.TryGetValue("show", out var node) || IsNothing(node))
            {
                return [new ReportView { Type = ReportViewTypes.Auto }];
            }

            var before = Problems.Count;
            var items = node is YamlSequenceNode ? Sequence(node, $"'show' of {where}", ReportFlow.MaxViews) : [node];
            if (node is YamlSequenceNode && items.Count == 0 && Problems.Count == before)
            {
                Problem(Line(node), $"'show' of {where} is an empty list; name how the answer is shown, or 'none' for a query that only feeds a parameter's choices.");
                return [];
            }

            var views = new List<ReportView>();
            var hidden = false;
            for (var i = 0; i < items.Count; i++)
            {
                var what = items.Count == 1 ? $"'show' of {where}" : $"view {i + 1} of {where}";
                if (View(items[i], what) is not { } view)
                {
                    continue;
                }

                if (view.Type == ReportViewTypes.None)
                {
                    hidden = true;
                    if (items.Count > 1)
                    {
                        Problem(Line(items[i]), $"{what} is 'none', which shows nothing, beside other views; a query is hidden or shown, not both.");
                    }

                    continue;
                }

                views.Add(view);
            }

            return hidden ? [] : views;
        }

        private ReportView? View(YamlNode node, string what)
        {
            // The short form names the type alone.
            if (node is YamlScalarNode scalar)
            {
                var only = scalar.Value?.Trim() ?? string.Empty;
                if (!ReportViewTypes.IsKnown(only))
                {
                    Problem(Line(node), UnknownToken(what, only, ReportViewTypes.All));
                    return null;
                }

                if (only == ReportViewTypes.Matrix)
                {
                    Problem(Line(node), $"{what} is a matrix, which needs 'across': write it as a mapping with 'type: matrix', 'by' (the rows) and 'across' (the columns).");
                    return null;
                }

                return new ReportView { Type = only };
            }

            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), $"{what} must be a view type ({string.Join(", ", ReportViewTypes.All)}) or a mapping with 'type'.");
                return null;
            }

            var before = Problems.Count;
            var map = Keys(mapping, ViewKeys, what);
            var type = Token(map, "type", what, ReportViewTypes.All, "view type");
            if (type is null)
            {
                if (!map.ContainsKey("type"))
                {
                    Problem(Line(mapping), $"{what} has no 'type'; the view types are: {string.Join(", ", ReportViewTypes.All)}.");
                }

                return null;
            }

            // A key the view's type does not take is said once, with the types that do take it.
            var takes = ViewTypeKeys[type];
            foreach (var key in map.Keys.Where(k => k is not ("type" or "title" or "description" or "width") && !takes.Contains(k, StringComparer.Ordinal)).ToList())
            {
                var types = ViewTypeKeys.Where(t => t.Value.Contains(key, StringComparer.Ordinal)).Select(t => t.Key);
                Problem(Line(map[key]), $"{what} is {Article(type)} '{type}' view, which takes no '{key}'; '{key}' goes with: {string.Join(", ", types)}.");
                map.Remove(key);
            }

            if (type == ReportViewTypes.None && map.Keys.Any(k => k != "type"))
            {
                Problem(Line(mapping), $"{what} is 'none', which shows nothing, so it takes no other key.");
            }

            var title = Text(map, "title", mapping, what, max: ReportFlow.MaxTitleLength, prose: true);
            var description = Text(map, "description", mapping, what, max: ReportFlow.MaxDescriptionLength, keepLines: true);
            var width = Token(map, "width", what, ReportWidths.All, "width");
            var by = ColumnName(map, "by", what);
            var series = ColumnName(map, "series", what);
            var across = ColumnName(map, "across", what);
            var values = ColumnNames(map, "values", what);
            var stacked = Flag(map, "stacked", what);
            var top = Number(map, "top", what, 1, ReportFlow.MaxTop, "categories kept");

            string? selects = null;
            if (map.TryGetValue("selects", out var selectsNode))
            {
                var named = Text(map, "selects", mapping, what, max: QueryParameter.MaxNameLength);
                if (named is not null)
                {
                    selects = _declared.FirstOrDefault(p => string.Equals(p, named, StringComparison.OrdinalIgnoreCase));
                    if (selects is null)
                    {
                        Problem(Line(selectsNode), _declared.Count == 0
                            ? $"'selects' of {what} names the parameter '{Clip(named)}', and the report declares no parameter."
                            : $"'selects' of {what} names the parameter '{Clip(named)}', which the report does not declare; its parameters are: {string.Join(", ", _declared)}.");
                    }
                    else if (by is null)
                    {
                        Problem(Line(selectsNode), $"'selects' of {what} sets a parameter to the value clicked, so the view names the column that value is read from under 'by'.");
                    }
                }
            }

            if (type == ReportViewTypes.Matrix && across is null && !map.ContainsKey("across"))
            {
                Problem(Line(mapping), $"{what} is a matrix, which lays one column's values out across; name that column under 'across'.");
            }

            if (type is ReportViewTypes.Share or ReportViewTypes.Matrix && values.Count > 1)
            {
                Problem(Line(map["values"]), $"'values' of {what} names {values.Count} columns; {Article(type)} '{type}' view shows one value.");
            }

            if (series is not null && values.Count > 1)
            {
                Problem(Line(map["series"]), $"{what} has 'series' and {values.Count} columns under 'values'; a series splits one value, so name one column under 'values', or leave 'series' out to set the columns side by side.");
            }

            if (Problems.Count > before)
            {
                return null;
            }

            return new ReportView
            {
                Type = type,
                Title = title,
                Description = description,
                Width = width,
                By = by,
                Values = values,
                Series = series,
                Across = across,
                Stacked = stacked,
                Top = top,
                Selects = selects,
            };
        }

        private List<ReportColumn> Columns(Dictionary<string, YamlNode> map, string where)
        {
            var columns = new List<ReportColumn>();
            if (!map.TryGetValue("columns", out var node) || IsNothing(node))
            {
                return columns;
            }

            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), $"'columns' of {where} must be a mapping of column name to how the column is labelled and written.");
                return columns;
            }

            if (mapping.Children.Count > ReportFlow.MaxColumns)
            {
                Problem(Line(node), $"'columns' of {where} names {mapping.Children.Count} columns; it may name at most {ReportFlow.MaxColumns}.");
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
                if (column.Length > ReportFlow.MaxColumnNameLength || HasControlCharacter(column))
                {
                    Problem(Line(keyNode), $"{what} has a name over {ReportFlow.MaxColumnNameLength} characters or one that holds a control character.");
                    continue;
                }

                if (!seen.Add(column))
                {
                    Problem(Line(keyNode), $"{what} is named twice; column names are compared without regard to case.");
                    continue;
                }

                // The short form names the format alone: `Size: bytes`.
                if (value is YamlScalarNode scalar && !IsNothing(value))
                {
                    var only = scalar.Value?.Trim() ?? string.Empty;
                    if (!ReportFormats.IsKnown(only))
                    {
                        Problem(Line(value), UnknownToken(what, only, ReportFormats.All));
                        continue;
                    }

                    columns.Add(new ReportColumn { Name = column, Format = only });
                    continue;
                }

                if (value is not YamlMappingNode entry)
                {
                    Problem(Line(value), $"{what} must be a format ({string.Join(", ", ReportFormats.All)}) or a mapping with 'label', 'format', 'decimals' and 'unit'.");
                    continue;
                }

                var before = Problems.Count;
                var entryMap = Keys(entry, ColumnKeys, what);
                var label = Text(entryMap, "label", entry, what, max: ReportFlow.MaxLabelLength, prose: true);
                var format = Token(entryMap, "format", what, ReportFormats.All, "format");
                var decimals = Number(entryMap, "decimals", what, 0, 6, "decimals shown");
                var unit = Text(entryMap, "unit", entry, what, max: ReportFlow.MaxUnitLength, prose: true);
                if (decimals is not null && format is not (null or ReportFormats.Number or ReportFormats.Percent or ReportFormats.Bytes or ReportFormats.Duration))
                {
                    Problem(Line(entryMap["decimals"]), $"'decimals' of {what} goes with a number, a percentage, a byte count or a duration, and its format is '{format}'.");
                }

                if (Problems.Count == before)
                {
                    columns.Add(new ReportColumn { Name = column, Label = label, Format = format, Decimals = decimals, Unit = unit });
                }
            }

            return columns;
        }

        /// <summary>A connection: one whole reference, never a connection string. A report's queries run on a worker, which
        /// is given a reference to resolve and nothing else.</summary>
        private string? Connection(Dictionary<string, YamlNode> map, YamlNode owner, string where, bool required)
        {
            if (!map.TryGetValue("connection", out var node))
            {
                if (required)
                {
                    Problem(Line(owner), $"{where} has no 'connection'; name the connection its queries run on as a reference, such as ${{env:SQLFLOW_DW}}.");
                }

                return null;
            }

            if (node is not YamlScalarNode scalar || IsNothing(node) || scalar.Value?.Trim() is not { Length: > 0 } value)
            {
                Problem(Line(node), $"'connection' of {where} must be a reference, such as ${{env:SQLFLOW_DW}}.");
                return null;
            }

            if (value.Length > ReportFlow.MaxConnectionLength)
            {
                Problem(Line(node), $"'connection' of {where} is {value.Length} characters; it may be at most {ReportFlow.MaxConnectionLength}.");
                return null;
            }

            if (!ComputeTaskPayload.IsWholeReference(value))
            {
                // The value is never echoed: a connection string may hold a credential.
                Problem(Line(node), $"'connection' of {where} must be one whole reference (${{env:NAME}}, ${{keyvault:vault/secret}}) or an @alias. A report's queries run on a worker, which resolves a reference and is never handed a connection string.");
                return null;
            }

            return value;
        }

        private string? Database(Dictionary<string, YamlNode> map, YamlNode owner, string where)
        {
            var database = Text(map, "database", owner, where, max: ReportFlow.MaxDatabaseLength);
            if (database is not null && HasControlCharacter(database))
            {
                Problem(Line(map["database"]), $"'database' of {where} holds a control character.");
                return null;
            }

            return database;
        }

        /// <summary>A column name a view refers to.</summary>
        private string? ColumnName(Dictionary<string, YamlNode> map, string key, string where)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node is not YamlScalarNode scalar || IsNothing(node) || scalar.Value?.Trim() is not { Length: > 0 } column)
            {
                Problem(Line(node), $"'{key}' of {where} must be the name of one column of the query's answer.");
                return null;
            }

            if (column.Length > ReportFlow.MaxColumnNameLength || HasControlCharacter(column))
            {
                Problem(Line(node), $"'{key}' of {where} is a name over {ReportFlow.MaxColumnNameLength} characters or one that holds a control character.");
                return null;
            }

            return column;
        }

        /// <summary>The columns a view names: one name, or a list of them, none repeated.</summary>
        private List<string> ColumnNames(Dictionary<string, YamlNode> map, string key, string where)
        {
            var columns = new List<string>();
            if (!map.TryGetValue(key, out var node) || IsNothing(node))
            {
                return columns;
            }

            var items = node is YamlScalarNode ? [node] : Sequence(node, $"'{key}' of {where}", ReportFlow.MaxColumns);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item is not YamlScalarNode scalar || IsNothing(item) || scalar.Value?.Trim() is not { Length: > 0 } column)
                {
                    Problem(Line(item), $"'{key}' of {where} has an entry that is not the name of a column.");
                    continue;
                }

                if (column.Length > ReportFlow.MaxColumnNameLength || HasControlCharacter(column))
                {
                    Problem(Line(item), $"'{key}' of {where} has a name over {ReportFlow.MaxColumnNameLength} characters or one that holds a control character.");
                    continue;
                }

                if (!seen.Add(column))
                {
                    Problem(Line(item), $"'{key}' of {where} names column '{Clip(column)}' twice.");
                    continue;
                }

                columns.Add(column);
            }

            return columns;
        }

        /// <summary>The mapping's entries by key, each unknown key reported once. A key of <paramref name="refused"/> is
        /// answered with why a report does not take it.</summary>
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
            "schedule" or "mode" or "lifecycle" or "onerrorresume"
                => $"{where} has '{key}', which a flow that runs takes; a report is never run as a flow, so remove it. Its queries run when a person opens the report.",
            "connections" or "server"
                => $"{where} has '{key}'; a report names the one connection its queries run on under 'connection', as a reference such as ${{env:SQLFLOW_DW}}.",
            _ => $"{where} has '{key}', which a flow that moves data takes; a report only reads, through the statements under 'queries'.",
        };

        /// <summary>
        /// One text value. <paramref name="prose"/> says the value is read by people (a title, a description, a label): it
        /// may quote anything but a credential. Every other value names something, and takes no <c>${...}</c> reference
        /// either. Left out, text over several lines is prose.
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

            if (!keepLines && HasControlCharacter(value))
            {
                Problem(Line(node), $"'{key}' of {where} holds a control character; it is plain text on one line.");
                return null;
            }

            return Plain(value, node, $"'{key}' of {where}", prose ?? keepLines) ? value : null;
        }

        /// <summary>
        /// Whether a value may stand in a report: it carries no credential, and unless it is prose, no <c>${...}</c>
        /// reference. A report is stored, shown and handed to an assistant as written, and the one thing it resolves is
        /// its connection. A value refused for a credential is never echoed: it is the secret.
        /// </summary>
        private bool Plain(string value, YamlNode node, string what, bool prose)
        {
            if (SecretHygiene.LooksLikeEmbeddedSecret(value)
                || !string.Equals(SecretHygiene.RedactedMessage(value), value, StringComparison.Ordinal))
            {
                Problem(Line(node), $"{what} appears to carry a credential; a report holds no secret, so write it without one.");
                return false;
            }

            if (!prose && value.Contains("${", StringComparison.Ordinal))
            {
                Problem(Line(node), $"{what} holds a reference ('${{...}}'); a report resolves its connection and nothing else, so write the value plainly.");
                return false;
            }

            return true;
        }

        /// <summary>One of a closed set of words, written exactly; null when the key is absent or the word is not one of
        /// them (which is reported).</summary>
        private string? Token(Dictionary<string, YamlNode> map, string key, string where, IReadOnlyList<string> allowed, string noun)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node is not YamlScalarNode scalar || IsNothing(node) || scalar.Value?.Trim() is not { Length: > 0 } value)
            {
                Problem(Line(node), $"'{key}' of {where} must be a {noun}: {string.Join(", ", allowed)}.");
                return null;
            }

            if (allowed.Contains(value, StringComparer.Ordinal))
            {
                return value;
            }

            Problem(Line(node), UnknownToken($"'{key}' of {where}", value, allowed));
            return null;
        }

        private static string UnknownToken(string what, string value, IReadOnlyList<string> allowed)
        {
            var near = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
            return near is not null
                ? $"{what} is '{Clip(value)}'; it is written '{near}', in lower case."
                : $"{what} is '{Clip(value)}'; it may be: {string.Join(", ", allowed)}.";
        }

        /// <summary>A whole number within bounds; null when the key is absent or the value is refused (which is reported).</summary>
        private int? Number(Dictionary<string, YamlNode> map, string key, string where, int min, int max, string what)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node is not YamlScalarNode { Value: { } raw }
                || !int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                Problem(Line(node), $"'{key}' of {where} must be a whole number from {min} to {max}: the {what}.");
                return null;
            }

            if (value < min || value > max)
            {
                Problem(Line(node), $"'{key}' of {where} is {value}; the {what} is from {min} to {max}.");
                return null;
            }

            return value;
        }

        private bool? Flag(Dictionary<string, YamlNode> map, string key, string where)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node is YamlScalarNode { Value: { } raw })
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "true":
                        return true;
                    case "false":
                        return false;
                }
            }

            Problem(Line(node), $"'{key}' of {where} must be true or false.");
            return null;
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

        /// <summary>Whether a value is absent as YAML writes it: nothing after the key, or an unquoted <c>~</c> or
        /// <c>null</c>.</summary>
        private static bool IsNothing(YamlNode node)
            => node is YamlScalarNode scalar
               && (string.IsNullOrWhiteSpace(scalar.Value)
                   || (scalar.Style == ScalarStyle.Plain && scalar.Value is "~" or "null" or "Null" or "NULL"));

        private static bool HasControlCharacter(string value) => value.Any(char.IsControl);

        /// <summary>The article a word takes in a sentence: "an" before a vowel.</summary>
        private static string Article(string word)
            => word.Length > 0 && "aeiou".Contains(word[0], StringComparison.OrdinalIgnoreCase) ? "an" : "a";

        private static string Clip(string value) => value.Length <= 60 ? value : value[..60] + "...";
    }
}
