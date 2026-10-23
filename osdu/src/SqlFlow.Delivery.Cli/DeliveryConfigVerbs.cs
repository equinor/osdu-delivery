using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow config</c>: the central configuration the control plane supplies to the runs it queues, read and written
/// from a deployment so an estate names where it delivers in one place rather than on every node.
/// </summary>
/// <remarks>
/// A property holds a non-secret value, or a <c>${env:...}</c> or <c>${keyvault:...}</c> reference the node resolves, so
/// a deployment can point a whole estate at a secret without this command, this database or a run payload ever holding
/// one. <c>--partition</c> sets, removes or reads the values of one OSDU partition, which a run bound to it resolves with
/// first (osdu/docs/reference/concepts/partitions.md, What a bound run is). What a property may hold is checked by
/// <see cref="DeliveryConfigStore"/>, exactly as for the API, and a change is recorded under the account the command runs
/// as (<see cref="RunActors.LocalAccount"/>).
/// </remarks>
public static class DeliveryConfigVerbs
{
    /// <summary>The catalog connection a command reads when <c>--db</c> names none, as SQLFlow's own database verbs do.</summary>
    private const string DefaultCatalog = "${env:SQLFLOW_CATALOG_DB}";

    public static Task<int> ConfigAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(ModuleCommand.Of(context));
    }

    /// <summary>The verb, over the command it runs for.</summary>
    internal static async Task<int> RunAsync(ModuleCommand context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var store = context.Services.GetRequiredService<DeliveryConfigStore>();
        if (!store.Available)
        {
            throw new FlowValidationException(
                "The central configuration lives in the module's database. Run 'sqlflow config' with --db <conn-ref>, or set the catalog variable.");
        }

        var repo = Repo(context);
        var partition = context.Arguments.GetOption("--partition");
        switch (context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty)
        {
            case "list":
            {
                var rows = await store.ListAsync(repo, partition, ct).ConfigureAwait(false);
                if (context.Json)
                {
                    context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray([.. rows.Select(Describe)])));
                    return 0;
                }

                if (rows.Count == 0)
                {
                    var narrowed = (repo is { } named ? $" for repository {named:D}" : string.Empty)
                        + (string.IsNullOrWhiteSpace(partition) ? string.Empty : $" in partition {partition.Trim()}");
                    context.Out.WriteLine(narrowed.Length == 0
                        ? "no configuration property is set; every reference a flow names is resolved on the node that runs it"
                        : $"no configuration property is set{narrowed}");
                    return 0;
                }

                foreach (var row in rows)
                {
                    var scope = row.RepoId is { } id ? id.ToString("D") : "(all repositories)";
                    var partitioned = row.Partition is { } own ? $", partition {own}" : string.Empty;
                    var configured = row.IsConfigured ? "  (configured)" : string.Empty;
                    context.Out.WriteLine(string.Create(
                        CultureInfo.InvariantCulture, $"{row.Name} = {row.Value}  [{scope}{partitioned}]{configured}  set by {row.UpdatedBy} at {row.UpdatedUtc:u}"));
                }

                return 0;
            }

            case "effective":
            {
                if (repo is not { } id)
                {
                    return context.UsageError("name the repository with --repo <id>: what a run is given depends on which estate it belongs to.");
                }

                var effective = await store.EffectiveAsync(id, partition, ct).ConfigureAwait(false);
                if (context.Json)
                {
                    var json = new JsonObject();
                    foreach (var (name, value) in effective.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        json[name] = value;
                    }

                    context.Out.WriteLine(CanonicalJson.Pretty(json));
                    return 0;
                }

                foreach (var (name, value) in effective.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    context.Out.WriteLine($"{name} = {value}");
                }

                return 0;
            }

            case "set":
            {
                if (context.Arguments.Positional(2) is not { } name)
                {
                    return context.UsageError("name the property to set.");
                }

                if (context.Arguments.GetOption("--value") is not { } value)
                {
                    return context.UsageError("give the value with --value <value>.");
                }

                // A property set for a repository is checked against the catalog's repositories, so the catalog is opened
                // only when one is named.
                await using var catalog = repo is null ? null : OpenCatalog(context);
                var row = await store
                    .SetAsync(repo, partition, name, value, context.Arguments.GetOption("--description"), RunActors.LocalAccount(), DateTime.UtcNow, catalog, ct)
                    .ConfigureAwait(false);
                context.Out.WriteLine(context.Json ? CanonicalJson.Pretty(Describe(row)) : $"{row.Name} set");
                return 0;
            }

            case "remove":
            {
                if (context.Arguments.Positional(2) is not { } name)
                {
                    return context.UsageError("name the property to remove.");
                }

                var removed = await store.RemoveAsync(repo, partition, name, ct).ConfigureAwait(false);
                context.Out.WriteLine(removed ? $"{name} removed" : $"{name} was not set");
                return 0;
            }

            default:
                return context.UsageError(
                    "use 'config list', 'config effective --repo <id>', 'config set <name> --value <value>' or 'config remove <name>', each with --partition <id> for one partition's values.");
        }
    }

    /// <summary>The repository the command works on, or null for the control plane's own properties.</summary>
    private static Guid? Repo(ModuleCommand context)
    {
        if (context.Arguments.GetOption("--repo") is not { } text)
        {
            return null;
        }

        return Guid.TryParse(text.Trim(), out var id)
            ? id
            : throw new FlowValidationException($"--repo '{text}' is not a repository id (a GUID; 'sqlflow repos show <name>' prints it).");
    }

    /// <summary>
    /// The catalog a repository is checked in: the connection the command line names (<c>--db</c>, else
    /// <c>${env:SQLFLOW_CATALOG_DB}</c>), resolved as SQLFlow's own database verbs resolve it.
    /// </summary>
    /// <exception cref="FlowValidationException">The reference does not resolve to a connection.</exception>
    private static CatalogDbContext OpenCatalog(ModuleCommand context)
    {
        var reference = context.Arguments.GetOption("--db") ?? DefaultCatalog;
        string connection;
        try
        {
            connection = context.Services.GetRequiredService<ISecretResolver>().Resolve(reference);
        }
        catch (SqlFlowException ex)
        {
            throw new FlowValidationException(
                $"--repo is checked against the catalog's repositories, and the catalog connection {SecretHygiene.RedactedMessage(reference)} did not resolve: {SecretHygiene.RedactedMessage(ex)} Name it with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.");
        }

        return string.IsNullOrWhiteSpace(connection)
            ? throw new FlowValidationException(
                $"--repo is checked against the catalog's repositories, and the catalog connection {SecretHygiene.RedactedMessage(reference)} resolved to nothing. Name it with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.")
            : CatalogDatabase.Create(connection);
    }

    private static JsonObject Describe(DeliveryConfigProperty row) => new()
    {
        ["name"] = row.Name,
        ["value"] = row.Value,
        ["repoId"] = row.RepoId?.ToString("D"),
        ["partition"] = row.Partition,
        ["description"] = row.Description,
        ["isConfigured"] = row.IsConfigured,
        ["updatedUtc"] = row.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture),
        ["updatedBy"] = row.UpdatedBy,
    };
}
