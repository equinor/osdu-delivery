using System.Diagnostics.CodeAnalysis;

namespace SqlFlow.ControlPlane.Configuration;

/// <summary>
/// The environments an estate runs as separate deployments of the control plane (dev, test and prod, for instance), so a
/// person moves between them from the GUI's title bar rather than from bookmarks. Bound from
/// <c>ControlPlane:Environments</c>: <see cref="Current"/> names the environment this control plane is, and
/// <see cref="Links"/> lists every environment of the estate, this one among them, in the order the title bar's menu shows
/// them, each with the address of its GUI. Every deployment can be given the same list, so they differ only in
/// <see cref="Current"/>. Nothing is configured by default, and the title bar then names no environment.
/// <para>
/// The setting only labels and links, so a mistake in it never stops a control plane: <see cref="Read"/> leaves out what
/// it cannot show and says why, and the host logs each reason at start.
/// </para>
/// </summary>
public sealed class EnvironmentsOptions
{
    /// <summary>The most environments the title bar's menu lists; the rest of a longer list is left out.</summary>
    public const int MaxLinks = 20;

    /// <summary>The longest an environment's name may be.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest an environment's description may be.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The longest an environment's address may be.</summary>
    public const int MaxUrlLength = 2000;

    private const string NameRule =
        "letters, digits, '-', '_' and '.', starting with a letter or a digit, at most 64 characters";

    /// <summary>
    /// The name of the environment this control plane is: one of the names <see cref="Links"/> gives, when it lists any.
    /// Set alone, the title bar names the environment and offers no other.
    /// </summary>
    public string? Current { get; set; }

    /// <summary>Every environment of the estate, this one among them, in the order the title bar's menu lists them.</summary>
    public List<EnvironmentLinkOptions?> Links { get; set; } = [];

    /// <summary>
    /// What the title bar shows of the setting, and a sentence for each mistake it found, naming the setting and what was
    /// left out because of it. An entry the title bar cannot show (a name that is not one, an environment listed again, an
    /// address that is not an http(s) address of a GUI, one past the <see cref="MaxLinks"/>th) is left out of the menu,
    /// and a description it cannot show is dropped from its entry. When the environment this control plane is cannot be
    /// told (no <see cref="Current"/> beside a list, a name that is not one, or one the list does not give), the title bar
    /// shows no environment at all rather than a label that could be wrong. An address is never repeated in a sentence,
    /// since a mistyped one could carry a credential. Nothing here throws.
    /// </summary>
    public ConfiguredEnvironments Read()
    {
        var problems = new List<string>();
        var listed = new List<string>();
        var shown = new List<ConfiguredEnvironment>();
        for (var i = 0; i < Links.Count; i++)
        {
            if (i == MaxLinks)
            {
                problems.Add(
                    $"ControlPlane:Environments:Links lists {Links.Count} environments; the title bar's menu shows the first "
                    + $"{MaxLinks} and leaves out the rest.");
                break;
            }

            var setting = $"ControlPlane:Environments:Links:{i}";
            var link = Links[i];
            if (link is null)
            {
                problems.Add($"{setting} is empty, so the title bar's menu leaves it out.");
                continue;
            }

            var name = link.Name?.Trim();
            if (!IsName(name))
            {
                problems.Add(
                    $"{setting}:Name {Quoted(link.Name)} is not an environment name ({NameRule}), so the title bar's menu "
                    + "leaves the entry out.");
                continue;
            }

            if (listed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{setting}:Name '{name}' names an environment an earlier entry of ControlPlane:Environments:Links "
                    + "names, so the title bar's menu leaves this entry out.");
                continue;
            }

            listed.Add(name);
            if (AddressProblem(link.Url, setting + ":Url") is { } problem)
            {
                problems.Add($"{problem}, so the title bar's menu leaves '{name}' out.");
                continue;
            }

            var description = link.Description?.Trim();
            if (description is { Length: > 0 } && (description.Length > MaxDescriptionLength || description.Any(char.IsControl)))
            {
                problems.Add(
                    $"{setting}:Description is longer than {MaxDescriptionLength} characters or holds a control character, "
                    + $"so the title bar's menu shows '{name}' without it.");
                description = null;
            }

            shown.Add(new ConfiguredEnvironment(name, link.Url!.Trim(), string.IsNullOrEmpty(description) ? null : description));
        }

        var current = Current?.Trim();
        if (string.IsNullOrEmpty(current))
        {
            if (listed.Count > 0)
            {
                problems.Add(
                    $"ControlPlane:Environments:Links lists {string.Join(", ", listed)}, and ControlPlane:Environments:Current "
                    + "names none of them, so the title bar shows no environment: set it to the environment this control "
                    + "plane is.");
            }

            return new ConfiguredEnvironments(null, [], problems);
        }

        if (!IsName(current))
        {
            problems.Add(
                $"ControlPlane:Environments:Current {Quoted(Current)} is not an environment name ({NameRule}), so the title "
                + "bar shows no environment.");
            return new ConfiguredEnvironments(null, [], problems);
        }

        if (Links.Count > 0)
        {
            var spelled = listed.FirstOrDefault(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
            if (spelled is null)
            {
                var names = listed.Count > 0 ? string.Join(", ", listed) : "no environment it could read";
                problems.Add(
                    $"ControlPlane:Environments:Current is '{current}', which ControlPlane:Environments:Links does not list "
                    + $"({names}), so the title bar shows no environment: name one of them, or add this environment to the "
                    + "list with its address.");
                return new ConfiguredEnvironments(null, [], problems);
            }

            current = spelled;
        }

        return new ConfiguredEnvironments(current, shown, problems);
    }

    private static bool IsName([NotNullWhen(true)] string? name)
        => name is { Length: > 0 and <= MaxNameLength }
            && char.IsAsciiLetterOrDigit(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string Quoted(string? value)
        => string.IsNullOrWhiteSpace(value) ? "is missing, so it" : $"'{value.Trim()}'";

    /// <summary>Why <paramref name="url"/> is not the http(s) address of a GUI, naming <paramref name="setting"/>; null when it is.</summary>
    private static string? AddressProblem(string? url, string setting)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return $"{setting} is empty: it is the address of the environment's GUI, for example https://sqlflow-test.example.com";
        }

        var trimmed = url.Trim();
        if (trimmed.Length > MaxUrlLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            return $"{setting} is not an absolute http or https address of the environment's GUI (at most {MaxUrlLength} characters)";
        }

        if (address.UserInfo.Length > 0)
        {
            return $"{setting} carries a user name or a password, which the address of an environment's GUI never holds";
        }

        return address.Query.Length > 0 || address.Fragment.Length > 0
            ? $"{setting} carries a query or a fragment, where only the address of the environment's GUI belongs"
            : null;
    }
}

/// <summary>One environment of the estate, as <c>ControlPlane:Environments:Links:&lt;n&gt;</c> lists it.</summary>
public sealed class EnvironmentLinkOptions
{
    /// <summary>The environment's name, as the title bar shows it: <c>dev</c>, <c>test</c>, <c>prod</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The address of the environment's GUI, which the title bar's menu opens.</summary>
    public string? Url { get; set; }

    /// <summary>What the environment is for, in one line; shown under its name in the menu.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// What the title bar shows of <c>ControlPlane:Environments</c>, read once at start (<see cref="EnvironmentsOptions.Read"/>):
/// the environment this control plane is (null when it cannot be told, and then nothing is shown), the environments the
/// menu lists in order, and a sentence for each mistake the setting held.
/// </summary>
public sealed record ConfiguredEnvironments(
    string? Current,
    IReadOnlyList<ConfiguredEnvironment> Environments,
    IReadOnlyList<string> Problems);

/// <summary>One environment the title bar's menu lists: its name, the address of its GUI, and what it is for.</summary>
public sealed record ConfiguredEnvironment(string Name, string Url, string? Description);
