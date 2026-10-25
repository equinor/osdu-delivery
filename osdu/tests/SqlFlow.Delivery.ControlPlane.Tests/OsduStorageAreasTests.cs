using Microsoft.EntityFrameworkCore;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Maintenance;
using SqlFlow.Delivery.ControlPlane.Maintenance;
using SqlFlow.Delivery.Data;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The <c>osdu</c> schema's storage areas against the module's model, with no database: every table of the model belongs to
/// exactly one area, so the Maintenance page accounts for the whole schema and a table added without an area fails here,
/// naming it; the areas and SQLFlow's follow the rules together; and only the three cleanups the traceability rule allows take
/// a retention.
/// </summary>
public sealed class OsduStorageAreasTests
{
    [Fact]
    public void The_module_and_sqlflow_areas_follow_the_rules_together()
    {
        var problems = StorageAreaRules.Check([new CatalogStorageContributor(new ControlPlaneOptions()), new OsduStorageContributor()]);
        Assert.Empty(problems);
    }

    [Fact]
    public void Every_table_of_the_model_belongs_to_exactly_one_area_and_every_area_table_exists()
    {
        using var db = new OsduDbContext(OsduDbContext.SqlServerOptions("Server=(local);Database=OsduStorageAreasTests;Integrated Security=true;TrustServerCertificate=true"));
        var modelTables = db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .Select(e => (Schema: e.GetSchema() ?? DeliveryModel.SchemaName, Name: e.GetTableName()!))
            .Append((Schema: DeliveryModel.SchemaName, Name: OsduDbContext.MigrationsHistoryTable))
            .Distinct()
            .ToList();
        var areas = new OsduStorageContributor().Areas;

        var claims = modelTables
            .Select(t => (Table: t, Areas: areas.Where(a => a.Tables.Any(c => c.Matches(t.Schema, t.Name))).Select(a => a.Id).ToList()))
            .ToList();
        var unclaimed = claims.Where(c => c.Areas.Count == 0).Select(c => $"{c.Table.Schema}.{c.Table.Name}").ToList();
        Assert.True(unclaimed.Count == 0, "Tables of the osdu schema no storage area holds: " + string.Join(", ", unclaimed));
        var doubled = claims.Where(c => c.Areas.Count > 1).Select(c => $"{c.Table.Schema}.{c.Table.Name} ({string.Join(", ", c.Areas)})").ToList();
        Assert.True(doubled.Count == 0, "Tables more than one area holds: " + string.Join(", ", doubled));

        var phantom = areas.SelectMany(a => a.Tables.Where(c => !c.IsPrefix).Select(c => (Area: a.Id, Claim: c)))
            .Where(x => !modelTables.Any(t => x.Claim.Matches(t.Schema, t.Name)))
            .ToList();
        Assert.True(phantom.Count == 0, "Areas holding tables the model does not have: " + string.Join(", ", phantom.Select(p => $"{p.Area}: {p.Claim}")));

        // The tables builds create at run time are claimed by prefix, and no table of the model starts with one.
        var prefixes = areas.SelectMany(a => a.Tables.Where(c => c.IsPrefix)).ToList();
        Assert.Equal(2, prefixes.Count);
        Assert.DoesNotContain(modelTables, t => prefixes.Any(p => p.Matches(t.Schema, t.Name)));
    }

    [Fact]
    public void Only_superseded_attempts_finished_activities_logs_and_superseded_assertion_runs_take_a_retention()
    {
        var areas = new OsduStorageContributor().Areas;
        var cleanable = areas.Where(a => a.Retention is not null).Select(a => a.Id).Order().ToList();
        Assert.Equal(
            [OsduStorageContributor.ActivityLogsArea, OsduStorageContributor.AssertionRunsArea, OsduStorageContributor.AttemptsArea],
            cleanable);
        Assert.All(areas.Where(a => a.Retention is not null), a => Assert.Equal(StorageVerdict.Optional, a.Verdict));

        // The ledger's own state and audit trail are never anything but kept.
        foreach (var id in new[] { "osdu.records", "osdu.submissions", "osdu.activities", "osdu.purged-records", "osdu.render-context", "osdu.artifacts" })
        {
            Assert.Equal(StorageVerdict.Keep, areas.Single(a => a.Id == id).Verdict);
        }
    }
}
