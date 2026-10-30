using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog.Modules;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Hosting;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The connections a command opens its module databases on, explaining the module's own (osdu/docs/reference/cli/db.md, The
/// module database). A command reads the osdu schema through the catalog's connection (<c>--db</c>, else
/// <c>${env:SQLFLOW_CATALOG_DB}</c>), which it resolves only when a query first opens the module's database, from whichever
/// verb or check asked; a reference that does not resolve then fails with what the secret resolver said, and this says what
/// the connection is for and how to give it. Every other module database's connection is the host's, as it resolves it.
/// </summary>
internal sealed class CommandModuleConnections : IModuleDatabaseConnections
{
    /// <summary>What a command that could not open the module's database is told to do.</summary>
    public const string GiveCatalogConnection =
        "Give the command the catalog's connection with --db <conn-ref>, or set " + OsduModuleDatabase.CatalogConnectionVariable + ".";

    private readonly IModuleDatabaseConnections _host;

    public CommandModuleConnections(IModuleDatabaseConnections host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public string ConnectionString(ModuleDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!string.Equals(database.Module, OsduSchema.Module, StringComparison.Ordinal) || database.ConnectionReference is not null)
        {
            return _host.ConnectionString(database);
        }

        string connection;
        try
        {
            connection = _host.ConnectionString(database);
        }
        catch (SqlFlowException ex)
        {
            throw new SqlFlowException(
                $"The osdu schema is read through the catalog's connection, which did not resolve: {SecretHygiene.RedactedMessage(ex)} {GiveCatalogConnection}", ex);
        }

        return string.IsNullOrWhiteSpace(connection)
            ? throw new SqlFlowException($"The osdu schema is read through the catalog's connection, which resolved to nothing. {GiveCatalogConnection}")
            : connection;
    }

    /// <summary>
    /// Wraps the connections SQLFlow registered in <paramref name="services"/> for a command, which every module database's
    /// context factory reads, so the module's own is explained wherever it is opened.
    /// </summary>
    /// <exception cref="InvalidOperationException">No connections are registered: the host builds a command's services without SQLFlow's.</exception>
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = services.LastOrDefault(d => d.ServiceType == typeof(IModuleDatabaseConnections))
            ?? throw new InvalidOperationException(
                "A command's services hold no IModuleDatabaseConnections, which SQLFlow registers before its modules configure theirs; the osdu database could not be opened.");
        services.Remove(registered);
        services.AddSingleton<IModuleDatabaseConnections>(provider => new CommandModuleConnections(
            (IModuleDatabaseConnections)(registered.ImplementationInstance
                ?? registered.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!))));
    }
}
