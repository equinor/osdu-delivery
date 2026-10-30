using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog.Modules;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Hosting;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The connections a command opens the module's database on (osdu/docs/reference/cli/db.md, The module database): the
/// catalog's, resolved only when a query first opens it, so a reference that does not resolve is explained wherever that
/// happens, naming what the connection is for and how to give it; a module database with a reference of its own, and a
/// connection that resolves, are the host's as they are.
/// </summary>
public sealed class CommandModuleConnectionsTests
{
    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private static string Unset() => "OSDU_TEST_UNSET_" + Guid.NewGuid().ToString("N");

    private static CommandModuleConnections Over(Func<string> catalog) => new(new ModuleDatabaseConnections(Secrets, catalog));

    [Fact]
    public void A_catalog_connection_that_does_not_resolve_says_what_it_is_for_and_how_to_give_it()
    {
        var unset = Unset();
        var connections = Over(() => Secrets.Resolve("${env:" + unset + "}"));

        var refused = Assert.Throws<SqlFlowException>(() => connections.ConnectionString(OsduModuleDatabase.Create()));

        Assert.Equal(
            $"The osdu schema is read through the catalog's connection, which did not resolve: Environment variable '{unset}' is not set. Give the command the catalog's connection with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.",
            refused.Message);
        Assert.IsType<SqlFlowException>(refused.InnerException);
    }

    [Fact]
    public void A_catalog_connection_that_resolves_to_nothing_is_refused_and_one_that_resolves_is_handed_back()
    {
        var empty = Assert.Throws<SqlFlowException>(() => Over(() => " ").ConnectionString(OsduModuleDatabase.Create()));
        Assert.Equal(
            "The osdu schema is read through the catalog's connection, which resolved to nothing. Give the command the catalog's connection with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.",
            empty.Message);

        Assert.Equal("Server=nowhere;Database=OsduDelivery", Over(() => "Server=nowhere;Database=OsduDelivery").ConnectionString(OsduModuleDatabase.Create()));
    }

    [Fact]
    public void A_module_database_with_a_reference_of_its_own_fails_as_the_host_resolves_it()
    {
        var node = OsduModuleDatabase.CreateForNode(_ => null);
        Assert.NotNull(node.ConnectionReference);

        var refused = Assert.Throws<SqlFlowException>(() => new CommandModuleConnections(new Refusing()).ConnectionString(node));

        Assert.Equal(Refusing.Message, refused.Message);
    }

    [Fact]
    public void Registering_wraps_the_connections_a_command_holds_and_a_command_without_them_is_refused()
    {
        var host = new ModuleDatabaseConnections(Secrets, () => "Server=nowhere");
        var services = new ServiceCollection().AddSingleton<IModuleDatabaseConnections>(_ => host);

        CommandModuleConnections.Register(services);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<CommandModuleConnections>(provider.GetRequiredService<IModuleDatabaseConnections>());
        Assert.Single(services, d => d.ServiceType == typeof(IModuleDatabaseConnections));
        Assert.Contains("hold no IModuleDatabaseConnections", Assert.Throws<InvalidOperationException>(() => CommandModuleConnections.Register(new ServiceCollection())).Message, StringComparison.Ordinal);
    }

    /// <summary>A host whose every connection fails to resolve, the same way whatever it is asked for.</summary>
    private sealed class Refusing : IModuleDatabaseConnections
    {
        public const string Message = "the host could not resolve it";

        public string ConnectionString(ModuleDatabase database) => throw new SqlFlowException(Message);
    }
}
