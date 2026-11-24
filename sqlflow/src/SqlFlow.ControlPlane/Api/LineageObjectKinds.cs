using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The word a drawing captions an object's kind with, shared by every drawing that names one (the lineage graph, a data
/// stream's path), so the same object reads the same wherever it is drawn: a dataset of an external system by its
/// system ("record type"), an object only lineage descriptions name by its system, a file location by what it reaches
/// (<c>api</c>, <c>sftp</c>, <c>file</c>), and anything else by the registry's kind in lower case.
/// </summary>
internal static class LineageObjectKinds
{
    /// <summary>The kind the key alone says, or null when only the object registry can tell.</summary>
    public static string? OfKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // A dataset of an external system is captioned by its system ("record type", "message queue"), which says
        // what the node is far better than the generic kind would.
        if (ServerIdentity.DatasetSystem(key) is { } datasetSystem)
        {
            return datasetSystem.Replace('-', ' ');
        }

        // An object only lineage descriptions name is captioned by its system for the same reason.
        if (LineageDescribedObject.SystemOf(key) is { } describedSystem)
        {
            return describedSystem.Replace('-', ' ');
        }

        if (!key.StartsWith("file|", StringComparison.Ordinal))
        {
            return null;
        }

        // A "file" node whose identity is a remote endpoint is the acquisition's external SOURCE, not a lake file:
        // caption it by what it is so a drawing reads "api -> flow -> file -> table".
        var name = key[(key.LastIndexOf('|') + 1)..];
        if (name.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "api";
        }

        return name.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) ? "sftp" : "file";
    }

    /// <summary>The kind the object registry records, in lower case, or null when it records none worth showing
    /// (blank, or <c>Unknown</c>).</summary>
    public static string? OfRegistry(string? registryKind)
        => string.IsNullOrEmpty(registryKind) || string.Equals(registryKind, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : registryKind.ToLowerInvariant();
}
