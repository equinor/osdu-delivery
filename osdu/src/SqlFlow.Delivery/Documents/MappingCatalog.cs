using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Locates pinned mappings on disk. A mapping <c>WellLog@1.4.0</c> is the file <c>WellLog@1.4.0.yaml</c> (or
/// <c>WellLog/1.4.0.yaml</c>) under the mappings directory. The file must declare the same name and version it is
/// filed under, so a reference always resolves to exactly the artifact that was reviewed.
/// </summary>
public sealed class MappingCatalog
{
    private readonly string _directory;
    private readonly DeliveryDocumentLoader _loader;
    private readonly FolderTemplates _templates;

    /// <param name="directory">The mappings directory.</param>
    /// <param name="loader">Reads each mapping document.</param>
    /// <param name="templates">
    /// The template folders a mapping that names its template's kind alone is settled from: those on the mapping file's
    /// path, up to the repository's top when the caller knows it (lineage reads a checkout), and otherwise up to the folder
    /// holding <c>.git</c>.
    /// </param>
    public MappingCatalog(string directory, DeliveryDocumentLoader loader, FolderTemplates? templates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(loader);
        _directory = directory;
        _loader = loader;
        _templates = templates ?? FolderTemplates.Default;
    }

    public string Directory => _directory;

    public MappingDefinition Load(string reference) => Load(reference, path => path);

    /// <summary>
    /// Loads the mapping <paramref name="reference"/> pins, naming every file and folder in its messages as
    /// <paramref name="shown"/> gives it (a path relative to the repository, say). The reference names a file under the
    /// directory and nothing else: a name or version carrying a path is refused before any file is looked at.
    /// </summary>
    public MappingDefinition Load(string reference, Func<string, string> shown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(shown);
        var at = reference.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == reference.Length - 1)
        {
            throw new FlowValidationException($"Mapping reference '{reference}' must be pinned as 'Name@version'.");
        }

        var name = reference[..at];
        var version = reference[(at + 1)..];
        if (NamesAPath(name) || NamesAPath(version))
        {
            throw new FlowValidationException(
                $"Mapping reference '{reference}' names a path; a mapping is named by its name and version alone, without '/', '\\' or '..'.");
        }

        var candidates = new[]
        {
            Path.Combine(_directory, $"{name}@{version}.yaml"),
            Path.Combine(_directory, $"{name}@{version}.yml"),
            Path.Combine(_directory, name, $"{version}.yaml"),
            Path.Combine(_directory, name, $"{version}.yml"),
        };

        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FlowValidationException(
                $"Mapping '{reference}' was not found under '{shown(_directory)}'. Expected one of: {string.Join(", ", candidates.Select(Path.GetFileName))}.");

        var mapping = _loader.LoadMapping(path, shown(path), _templates);
        if (!mapping.Name.Equals(name, StringComparison.Ordinal) || !mapping.Version.Equals(version, StringComparison.Ordinal))
        {
            throw new FlowValidationException(
                $"{shown(path)}: declares '{mapping.Reference}' but is filed as '{reference}'. The file name and the document must agree.");
        }

        return mapping;
    }

    private static bool NamesAPath(string part)
        => part.Contains('/', StringComparison.Ordinal) || part.Contains('\\', StringComparison.Ordinal) || part.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(part);

    public IReadOnlyList<string> List()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        return System.IO.Directory.EnumerateFiles(_directory, "*.y*ml", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n.Contains('@', StringComparison.Ordinal))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Resolves a flow's declared parameters against supplied values and substitutes tokens.</summary>
public static class FlowParameters
{
    /// <summary>The separator a location puts between the values of a parameter a run gives several (<see cref="WorkLocation"/>).</summary>
    public const char LocationListSeparator = '+';

    /// <summary>Which parameters of a delivery flow take several values, in the words a refusal names them by.</summary>
    public const string ScopeSeveralValuesRule = "a parameter source.record.scope binds";

    /// <summary>
    /// Returns the effective parameter values: supplied values, then defaults; throws when a required parameter is
    /// missing, an undeclared one is supplied, or a value is not one of a parameter's choices. A parameter
    /// <c>source.record.scope</c> binds may be given several values (a <see cref="RunValueList"/>), which select the rows
    /// holding any of them; they resolve to their set, in ordinal order and each once, so the same values named in another
    /// order are the same scope.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(FlowDefinition flow, IReadOnlyDictionary<string, string>? supplied)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, supplied, flow.Source.Record.Scope.Values);
    }

    /// <summary>
    /// The same resolution over any declared parameter set (a retrieval flow's, say); <paramref name="where"/> names the
    /// document in errors. Only the parameters <paramref name="takingSeveral"/> names may be given several values; a list
    /// given to any other is refused, since a query or a location it fills takes one. The rule is SQLFlow's one rule for a
    /// run's values (<see cref="RunValueResolution"/>), which a run form and a schedule's preview explain them with.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(
        IReadOnlyDictionary<string, FlowParameter> parameters, string where, IReadOnlyDictionary<string, string>? supplied, IEnumerable<string>? takingSeveral = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = RunValueResolution.Resolve(RunInputs(parameters, takingSeveral), supplied, where);
        return values.ToDictionary(v => v.Key, v => RunValueList.IsList(v.Value) ? SetOf(v.Value) : v.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// What a run of a flow declaring <paramref name="parameters"/> may be given for them, as SQLFlow describes it to a run
    /// form (<see cref="FlowRunInputs"/>): each parameter in the document's order, those <paramref name="takingSeveral"/>
    /// names taking several values, and <paramref name="lookup"/> saying where a parameter's values are looked up, when they
    /// are. The kind adds its own run values and settings.
    /// </summary>
    public static FlowRunInputs RunInputs(
        IReadOnlyDictionary<string, FlowParameter> parameters, IEnumerable<string>? takingSeveral = null, Func<string, FlowParameter, string?>? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var several = takingSeveral?.ToHashSet(StringComparer.Ordinal) ?? [];
        return new FlowRunInputs
        {
            Parameters = parameters.Select(p => new DeclaredRunParameter
            {
                Name = p.Key,
                Required = p.Value.Required,
                Default = p.Value.Default,
                Description = p.Value.Description,
                TakesSeveral = several.Contains(p.Key),
                Choices = p.Value.Choices ?? [],
                Lookup = lookup?.Invoke(p.Key, p.Value),
            }).ToList(),
            SeveralValuesRule = several.Count > 0 ? ScopeSeveralValuesRule : null,
        };
    }

    /// <summary>
    /// What a mapping's parameters take from the values its flow gives under <c>render.parameters</c>, by the same rule a
    /// run's values follow: the value given, else the mapping's default, else none, a required one without either and a
    /// value the mapping does not declare each naming its problem. A mapping parameter takes any text, a list's included.
    /// </summary>
    public static IReadOnlyList<ResolvedRunValue> ExplainMapping(MappingDefinition mapping, IReadOnlyDictionary<string, string> supplied)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(supplied);
        var inputs = new FlowRunInputs
        {
            Parameters = mapping.Parameters.Select(p => new DeclaredRunParameter
            {
                Name = p.Key,
                Required = p.Value.Required,
                Default = p.Value.Default,
                Description = p.Value.Description,
                TakesSeveral = true,
            }).ToList(),
        };
        return RunValueResolution.Explain(inputs, supplied);
    }

    /// <summary>
    /// The values a scope parameter is given as a set: each once, in ordinal order, so the scope a run reads (and the
    /// watermark it keeps) is the same whatever order the values were named in.
    /// </summary>
    private static string SetOf(string list)
        => RunValueList.Of(RunValueList.Items(list).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>
    /// Substitutes <c>{name}</c> tokens in a string from the resolved values. A token whose parameter the run gives several
    /// values is refused: the text it fills (a query, a location) takes one.
    /// </summary>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{(?<name>[A-Za-z0-9_]+)\}", m =>
        {
            var name = m.Groups["name"].Value;
            if (!values.TryGetValue(name, out var v))
            {
                return m.Value;
            }

            return RunValueList.IsList(v)
                ? throw new FlowValidationException(
                    $"Parameter '{name}' is given {RunValueList.Items(v).Count} values, and '{{{name}}}' fills '{text}', which takes one; run one value at a time.")
                : v;
        });
    }

    /// <summary>
    /// The work root the intake writes batches under: <c>source.work</c> with its tokens substituted, resolved against the
    /// flow file. A parameter the run gives several values fills its token with them joined by <see cref="LocationListSeparator"/>
    /// (<c>COMPOSITE+WIRELINE</c>): the root only keeps one run's batches apart from another's, and a set of values names a
    /// folder of its own as one value does.
    /// </summary>
    public static string WorkLocation(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var joined = values.ToDictionary(
            v => v.Key,
            v => RunValueList.IsList(v.Value) ? string.Join(LocationListSeparator, RunValueList.Items(v.Value)) : v.Value,
            StringComparer.Ordinal);
        return ResolvePath(flow, flow.Source.Work, joined, "source.work");
    }

    /// <summary>
    /// The scope predicate's values: each column <c>source.record.scope</c> names, with the value of the parameter it binds.
    /// Throws when a bound parameter has no value, which a resolved parameter set never lacks.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ScopeValues(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var scope = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (column, parameter) in flow.Source.Record.Scope)
        {
            scope[column] = values.TryGetValue(parameter, out var value)
                ? value
                : throw new FlowValidationException(
                    $"{flow.SourcePath ?? flow.Name}: source.record.scope binds column '{column}' to parameter '{parameter}', which has no value for this run.");
        }

        return scope;
    }

    /// <summary>
    /// A location a flow declares (a work root or a payload root), its tokens substituted and resolved: a
    /// storage URI as written, a rooted path as it is, a relative path against the flow file's folder. A parameter value that
    /// would climb out of the declared location (a separator or '..') is refused, because these locations bound what a run
    /// may read and write; so is a parameter the run gives several values, since the files a payload root names sit under one
    /// of them (<see cref="WorkLocation"/> joins them first).
    /// </summary>
    public static string ResolvePath(FlowDefinition flow, string declared, IReadOnlyDictionary<string, string> values, string what)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(declared);
        ArgumentNullException.ThrowIfNull(values);
        var where = flow.SourcePath ?? flow.Name;
        foreach (var token in FlowMapper.Tokens(declared))
        {
            if (values.TryGetValue(token, out var several) && RunValueList.IsList(several))
            {
                throw new FlowValidationException(
                    $"{where}: parameter '{token}' is given {RunValueList.Items(several).Count} values, and {what} is a location that takes one: run one value at a time.");
            }

            if (values.TryGetValue(token, out var value)
                && (value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal)))
            {
                throw new FlowValidationException(
                    $"{where}: parameter '{token}' is substituted into {what}, a location, so its value must not contain '/', '\\' or '..'.");
            }
        }

        var text = Substitute(declared, values).Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return text.TrimEnd('/');
        }

        if (Path.IsPathRooted(text))
        {
            return Path.GetFullPath(text);
        }

        var baseDirectory = flow.SourcePath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? System.IO.Directory.GetCurrentDirectory()
            : System.IO.Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDirectory, text));
    }
}
