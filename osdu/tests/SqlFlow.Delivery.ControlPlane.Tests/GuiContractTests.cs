using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.ControlPlane.Api;
using Xunit;

namespace SqlFlow.Delivery.ControlPlane.Tests;

/// <summary>
/// The GUI's API types against the control plane's (<c>osdu/gui/src/api</c> and its folders, such as <c>api/delivery/</c>):
/// every field a TypeScript interface declares is one the C# type of the same name, or of that name with <c>Dto</c>,
/// carries under the same JSON name. A field the GUI reads that the API never sends, or sends under another name, is
/// absent at run time and fails no type check on either side; this finds it. The GUI may leave out what it does not read,
/// so only its own fields are checked.
/// </summary>
public sealed partial class GuiContractTests
{
    /// <summary>The fewest types the check has to match: fewer means it is reading the wrong files, not that the API shrank.</summary>
    private const int FewestMatched = 150;

    [Fact]
    public void Every_field_a_gui_api_type_declares_is_one_its_control_plane_type_carries()
    {
        var interfaces = TypeScriptInterfaces(Path.Combine(RepositoryRoot(), "osdu", "gui", "src", "api"));
        var types = typeof(DeliveryReleaseRequest).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var matched = 0;
        var drift = new List<string>();
        foreach (var (name, _) in interfaces)
        {
            if (!types.TryGetValue(name + "Dto", out var type) && !types.TryGetValue(name, out type))
            {
                continue;
            }

            matched++;
            var carried = JsonNames(type);
            drift.AddRange(Fields(name, interfaces)
                .Where(field => !carried.Contains(field))
                .Select(field => $"{name}.{field}: {type.Name} carries no '{field}' (it carries {string.Join(", ", carried.Order(StringComparer.Ordinal))})"));
        }

        Assert.True(matched >= FewestMatched, $"Only {matched} GUI API types matched a control plane type; the check expects at least {FewestMatched}.");
        Assert.True(drift.Count == 0, "GUI API fields the control plane does not send:\n" + string.Join("\n", drift));
    }

    /// <summary>The JSON names a type's answer or request carries: its public properties, as the API's camel-case naming writes them.</summary>
    private static HashSet<string> JsonNames(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>An interface's fields with those of the interfaces it extends.</summary>
    private static IEnumerable<string> Fields(string name, IReadOnlyDictionary<string, (HashSet<string> Fields, IReadOnlyList<string> Extends)> interfaces)
    {
        if (!interfaces.TryGetValue(name, out var declared))
        {
            return [];
        }

        return declared.Fields.Concat(declared.Extends.SelectMany(parent => Fields(parent, interfaces))).Distinct(StringComparer.Ordinal);
    }

    /// <summary>Every <c>export interface</c> of the folder's files: its fields at its own level, and the interfaces it extends.</summary>
    private static Dictionary<string, (HashSet<string> Fields, IReadOnlyList<string> Extends)> TypeScriptInterfaces(string folder)
    {
        var found = new Dictionary<string, (HashSet<string>, IReadOnlyList<string>)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.ts", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match head in InterfaceHead().Matches(text))
            {
                var open = head.Index + head.Length - 1;
                var fields = new HashSet<string>(StringComparer.Ordinal);
                var depth = 0;
                var lineStart = open + 1;
                for (var i = open; i < text.Length; i++)
                {
                    if (text[i] == '{')
                    {
                        depth++;
                    }
                    else if (text[i] == '}' && --depth == 0)
                    {
                        break;
                    }
                    else if (text[i] == '\n')
                    {
                        if (depth == 1 && FieldLine().Match(text[lineStart..i]) is { Success: true } field)
                        {
                            fields.Add(field.Groups["name"].Value);
                        }

                        lineStart = i + 1;
                    }
                }

                var extends = head.Groups["extends"].Success
                    ? head.Groups["extends"].Value.Split(',').Select(p => p.Trim()).Where(p => Regex.IsMatch(p, @"^\w+$")).ToList()
                    : [];
                found[head.Groups["name"].Value] = (fields, extends);
            }
        }

        return found;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OsduDelivery.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No OsduDelivery.sln above {AppContext.BaseDirectory}: the check reads the GUI's sources beside it.");
    }

    [GeneratedRegex(@"export interface (?<name>\w+)(?:<[^>{]*>)?(?:\s+extends\s+(?<extends>[^{]+?))?\s*\{")]
    private static partial Regex InterfaceHead();

    [GeneratedRegex(@"^\s*(?:readonly\s+)?(?<name>[A-Za-z_$][\w$]*)\??\s*:")]
    private static partial Regex FieldLine();
}
