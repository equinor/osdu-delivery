using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Data;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The rule in CLAUDE.md that the OSDU module's migrations touch only the <c>osdu</c> schema and that none of SQLFlow's
/// migrations touches it (osdu/docs/reference/cli/db.md, For contributors: adding a migration), checked on the two
/// models, on the migrations' operations and on the SQL scripts they generate, without a database.
/// </summary>
public sealed partial class MigrationScriptTests
{
    private const string Placeholder = "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    [Fact]
    public void Every_osdu_migration_operation_names_the_osdu_schema()
    {
        using var context = new OsduDbContext(OsduDbContext.SqlServerOptions(Placeholder));
        var migrations = Migrations(context);
        Assert.NotEmpty(migrations);

        var outside = new List<string>();
        foreach (var (id, migration) in migrations)
        {
            foreach (var (direction, operations) in new[] { ("up", migration.UpOperations), ("down", migration.DownOperations) })
            {
                foreach (var operation in operations)
                {
                    foreach (var schema in SchemasOf(operation))
                    {
                        if (!string.Equals(schema, DeliveryModel.SchemaName, StringComparison.Ordinal))
                        {
                            outside.Add($"{id} ({direction}) {operation.GetType().Name} names schema '{schema ?? "(default)"}'");
                        }
                    }
                }
            }
        }

        Assert.True(outside.Count == 0, "OSDU migrations that reach outside the osdu schema:\n" + string.Join("\n", outside));
    }

    [Fact]
    public void The_osdu_migration_script_writes_the_osdu_schema_alone()
    {
        using var context = new OsduDbContext(OsduDbContext.SqlServerOptions(Placeholder));
        var script = context.GetService<IMigrator>().GenerateScript();

        var targets = Targets(script).ToList();
        Assert.Contains($"[{DeliveryModel.SchemaName}].[{OsduDbContext.MigrationsHistoryTable}]", targets);
        var outside = targets.Where(t => !t.StartsWith($"[{DeliveryModel.SchemaName}].", StringComparison.Ordinal) && !t.StartsWith("[sys].", StringComparison.Ordinal)).Distinct().ToList();
        Assert.True(outside.Count == 0, "The OSDU migration script writes outside the osdu schema: " + string.Join(", ", outside));
        Assert.DoesNotContain("[catalog].", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[dbo].", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_catalog_migration_touches_the_osdu_schema()
    {
        using var catalog = CatalogDatabase.Create(Placeholder);
        var migrations = Migrations(catalog);
        Assert.NotEmpty(migrations);
        var touching = migrations
            .SelectMany(m => m.Migration.UpOperations.Concat(m.Migration.DownOperations).Select(o => (m.Id, Operation: o)))
            .Where(x => SchemasOf(x.Operation).Any(s => string.Equals(s, DeliveryModel.SchemaName, StringComparison.OrdinalIgnoreCase))
                || (x.Operation is SqlOperation sql && OsduReference().IsMatch(sql.Sql)))
            .Select(x => $"{x.Id} {x.Operation.GetType().Name}")
            .ToList();
        Assert.True(touching.Count == 0, "SQLFlow catalog migrations that touch the osdu schema:\n" + string.Join("\n", touching));

        var script = catalog.GetService<IMigrator>().GenerateScript();
        Assert.DoesNotMatch(OsduReference(), script);
    }

    [Fact]
    public void The_osdu_model_holds_the_module_s_own_entities_alone()
    {
        // A navigation to one of SQLFlow's entities would pull that entity into this model under the osdu schema, where the
        // schema checks would pass it: the entity's own type is what says the model reaches into SQLFlow's tables.
        using var context = new OsduDbContext(OsduDbContext.SqlServerOptions(Placeholder));
        var entities = context.GetService<IDesignTimeModel>().Model.GetEntityTypes().ToList();
        Assert.NotEmpty(entities);

        var module = typeof(OsduDbContext).Assembly;
        var foreign = entities.Where(e => e.ClrType.Assembly != module).Select(e => $"{e.DisplayName()} ({e.ClrType.FullName})").ToList();
        Assert.True(foreign.Count == 0, "The osdu model holds entities that are not the module's own: " + string.Join(", ", foreign));
    }

    [Fact]
    public void SQLFlow_s_catalog_model_holds_no_osdu_table()
    {
        using var catalog = CatalogDatabase.Create(Placeholder);
        var entities = catalog.GetService<IDesignTimeModel>().Model.GetEntityTypes().ToList();
        Assert.NotEmpty(entities);

        var module = typeof(OsduDbContext).Assembly;
        var osdu = entities
            .Where(e => string.Equals(e.GetSchema(), DeliveryModel.SchemaName, StringComparison.OrdinalIgnoreCase) || e.ClrType.Assembly == module)
            .Select(e => $"{e.DisplayName()} ([{e.GetSchema()}].[{e.GetTableName()}])")
            .ToList();
        Assert.True(osdu.Count == 0, "SQLFlow's catalog model holds OSDU tables: " + string.Join(", ", osdu));
    }

    [Fact]
    public void The_script_check_finds_every_write_to_another_schema_and_no_read()
    {
        const string script = """
            CREATE TABLE [dbo].[Stray] ([Id] int NOT NULL);
            INSERT INTO [catalog].[Pipeline] ([Id]) VALUES (1);
            CREATE UNIQUE NONCLUSTERED INDEX [IX_Stray] ON [dbo].[Stray] ([Id]);
            ALTER TABLE [osdu].[Record] ADD CONSTRAINT [FK_Record_Pipeline] FOREIGN KEY ([PipelineId]) REFERENCES [catalog].[Pipeline] ([Id]);
            EXEC sp_rename N'[catalog].[Run].[Old]', N'New', 'COLUMN';
            SELECT @var0 = [d].[name] FROM [sys].[default_constraints] [d] INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id];
            UPDATE r SET [r].[Status] = 1 FROM [osdu].[Record] AS r;
            """;

        Assert.Equal(
            ["[dbo].[Stray]", "[catalog].[Pipeline]", "[dbo].[Stray]", "[osdu].[Record]", "[catalog].[Pipeline]", "[catalog].[Run]"],
            Targets(script));
    }

    /// <summary>Every migration a context's assembly holds, by id, in order.</summary>
    private static List<(string Id, Migration Migration)> Migrations(DbContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        return assembly.Migrations
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => (m.Key, assembly.CreateMigration(m.Value, context.Database.ProviderName!)))
            .ToList();
    }

    /// <summary>The schemas a migration operation names: a table's, a referenced table's, a new schema, a sequence's.</summary>
    private static IEnumerable<string?> SchemasOf(MigrationOperation operation)
    {
        switch (operation)
        {
            case EnsureSchemaOperation ensure:
                yield return ensure.Name;
                yield break;
            case DropSchemaOperation drop:
                yield return drop.Name;
                yield break;
            case SqlOperation:
                // A raw statement names its objects in its text, which the script check reads.
                yield break;
        }

        foreach (var property in operation.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.PropertyType == typeof(string) && property.Name.EndsWith("Schema", StringComparison.Ordinal))
            {
                var value = (string?)property.GetValue(operation);

                // A rename or a foreign key names a second schema only when it has one.
                if (value is not null || property.Name == "Schema")
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>
    /// The objects a script creates, changes, drops, renames or writes rows to, as <c>[schema].[name]</c>: the statements a
    /// migration can reach another schema with. Reads inside a statement (a join's aliases, the system views EF consults)
    /// are not targets.
    /// </summary>
    private static IEnumerable<string> Targets(string script)
    {
        foreach (Match match in TargetStatement().Matches(script))
        {
            yield return $"[{match.Groups["schema"].Value}].[{match.Groups["name"].Value}]";
        }
    }

    [GeneratedRegex(@"\b(?:CREATE|ALTER|DROP)\s+(?:UNIQUE\s+)?(?:CLUSTERED\s+|NONCLUSTERED\s+)?(?:TABLE|VIEW|SEQUENCE|PROCEDURE|FUNCTION|TYPE|SYNONYM|TRIGGER)\s+\[(?<schema>[^\]]+)\]\.\[(?<name>[^\]]+)\]|\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO|MERGE|TRUNCATE\s+TABLE|REFERENCES)\s+\[(?<schema>[^\]]+)\]\.\[(?<name>[^\]]+)\]|\bINDEX\s+\[[^\]]+\]\s+ON\s+\[(?<schema>[^\]]+)\]\.\[(?<name>[^\]]+)\]|sp_rename\s+N'\[(?<schema>[^\]]+)\]\.\[(?<name>[^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetStatement();

    [GeneratedRegex(@"\[osdu\]|\bosdu\.|SCHEMA_ID\(N'osdu'\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OsduReference();
}
