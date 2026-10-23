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
/// <see cref="CatalogConnectionVariable"/>, which is the only database connection a node is given.
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

    /// <summary>
    /// The settings that once gave the module a database of its own, each refused with what replaced it: a configuration key
    /// and an environment variable. Ignoring one would move the ledger: a host would migrate an empty <c>osdu</c> schema in
    /// the catalog's database and deliver everything again. The local readiness check names them too, in the environment's
    /// spelling (<c>SqlFlow.Delivery.Setup</c>'s <c>DevValues.RetiredSettings</c>), and a test holds the two lists alike.
    /// </summary>
    public static IReadOnlyList<string> RetiredSettings { get; } = ["Osdu:Database:Connection", "SQLFLOW_OSDU_DB"];

    /// <summary>The module database on the catalog's own connection, as the control plane and a command open it.</summary>
    public static ModuleDatabase<OsduDbContext> Create() => Build(connectionReference: null);

    /// <summary>The module database a worker node opens, which has no catalog connection of its own: <see cref="NodeReference"/>.</summary>
    public static ModuleDatabase<OsduDbContext> CreateForNode() => Build(NodeReference);

    /// <summary>
    /// Refuses a host started with a setting that gave the module a database of its own, naming what replaced it. A
    /// setting that is present at all is refused, as the platform refuses its own retired settings, so a deployment never
    /// believes it configured something the host does not read.
    /// </summary>
    /// <param name="isSet">Whether the host was given the named setting: a configuration key (<c>Osdu:Database:Connection</c>,
    /// set in an environment as <c>Osdu__Database__Connection</c>) or an environment variable.</param>
    /// <exception cref="InvalidOperationException">A retired setting is present; the message names it and never echoes its value.</exception>
    public static void RefuseRetiredSettings(Func<string, bool> isSet)
    {
        ArgumentNullException.ThrowIfNull(isSet);
        if (RetiredSettings.FirstOrDefault(isSet) is { } retired)
        {
            throw new InvalidOperationException(
                $"{retired} is no longer a setting; the osdu schema lives in the catalog's database, which {CatalogConnectionVariable} "
                + "names. Remove it. If it named another database, that database holds the ledger: bring its osdu schema into the "
                + "catalog's database before the host starts, or the host would start an empty ledger there.");
        }
    }

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
