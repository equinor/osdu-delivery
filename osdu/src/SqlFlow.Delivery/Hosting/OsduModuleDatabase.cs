using SqlFlow.Catalog.Modules;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Hosting;

/// <summary>
/// The OSDU module's database as a host registers it with the platform (osdu/docs/reference/concepts/architecture.md,
/// The osdu schema): the <c>osdu</c> schema, its migrations and its version row, built from
/// <see cref="OsduDbContext"/>. One declaration serves every host, so the control plane, the CLI and a worker node
/// migrate, report and verify exactly the same database: <c>sqlflow db migrate</c> and <c>db status</c> cover it after
/// the catalog, a node verifies it before it takes work, and the control plane refuses to run over one that is missing,
/// behind or ahead.
/// </summary>
/// <remarks>
/// The <c>osdu</c> schema lives in the catalog's database, and every host reaches it through the catalog's one
/// connection: the control plane and a command on the connection they open the catalog on, a worker node through
/// <see cref="CatalogConnectionVariable"/>, which is the only database connection a node is given. The settings that once
/// gave the module a database of its own, <c>SQLFLOW_OSDU_DB</c> and <c>Osdu:Database:Connection</c>, are not read: a
/// deployment whose settings still carry one starts as if they did not.
/// </remarks>
public static class OsduModuleDatabase
{
    /// <summary>The environment variable holding the catalog's connection, and so the module's.</summary>
    public const string CatalogConnectionVariable = "SQLFLOW_CATALOG_DB";

    /// <summary>The reference a worker node opens the module's database with: the catalog's connection.</summary>
    public const string NodeReference = "${env:" + CatalogConnectionVariable + "}";

    /// <summary>
    /// What a host started without the module's database is told to do, completing each message that says it has none.
    /// </summary>
    public const string StartWithCatalogConnection =
        "The osdu schema lives in the catalog's database: start the host with the catalog's connection (" + CatalogConnectionVariable
        + ", or --db on the command line).";

    /// <summary>The module database on the catalog's own connection, as the control plane and a command open it.</summary>
    public static ModuleDatabase<OsduDbContext> Create() => Build(connectionReference: null);

    /// <summary>The module database a worker node opens, which has no catalog connection of its own: <see cref="NodeReference"/>.</summary>
    public static ModuleDatabase<OsduDbContext> CreateForNode() => Build(NodeReference);

    private static ModuleDatabase<OsduDbContext> Build(string? connectionReference)
        => new(
            OsduSchema.Module,
            DeliveryModel.SchemaName,
            OsduDbContext.ModuleVersion,
            connectionString => new OsduDbContext(OsduDbContext.SqlServerOptions(connectionString)),
            connection => new OsduDbContext(OsduDbContext.SqlServerOptions(connection)),
            connectionReference,
            OsduDbContext.MinimumCatalogMigration)
        {
            AfterMigrate = (context, applied, ct) => OsduSchema.RecordAsync(
                context,
                new OsduSchema.Applied(applied.Version, applied.LastMigration, applied.AppliedBy, applied.AppliedUtc, applied.MinimumCatalogMigration),
                ct),
            ReadRecordedVersion = async (context, ct) =>
                await OsduSchema.ReadAsync(context, ct).ConfigureAwait(false) is { } recorded
                    ? new ModuleRecordedVersion(recorded.ModuleVersion, recorded.LastMigration)
                    : null,
        };
}
