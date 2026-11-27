using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The projection of a run's warnings and its changes to existing columns' types: the warning count the run row carries,
/// one type-history row per report the engine wrote into run.json (camelCase, enums by name, as the executor serializes
/// it), and the key each reported table is given, which must be the key the catalog holds the object's columns under.
/// Pure: every projection is a function of the document and of what it is told about the run's pipeline.
/// </summary>
public sealed class ColumnTypeChangeProjectionTests
{
    private static readonly Guid Repo = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Run = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Pipeline = new("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Ended = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    /// <summary>An ingestion run's artifact as the executor writes it: two reports, one applied and one refused, and the
    /// warning the refusal raised among the run's other events.</summary>
    private static string Artifact() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        flowKind = "ing",
        flowName = "wells_01_ing",
        runId = Run,
        success = true,
        writtenUtc = Ended,
        result = new
        {
            startTimeUtc = Ended.AddMinutes(-1),
            endTimeUtc = Ended,
            typeChanges = new TypeChangeReport[]
            {
                new()
                {
                    Layer = TypeChangeReport.TargetLayer,
                    Table = "[Warehouse].[arc].[WellLog]",
                    Column = "Depth",
                    FromType = "int",
                    ToType = "bigint",
                    Tier = TypeChangeTier.Widening,
                    Status = TypeChangeStatus.Applied,
                    RewritesTable = true,
                    Rows = 1200,
                    Message = "[Depth] int -> bigint: applied by the safe rewrite.",
                },
                new()
                {
                    Layer = TypeChangeReport.TargetLayer,
                    Table = "[Warehouse].[arc].[WellLog]",
                    Column = "Spud",
                    FromType = "nvarchar(20)",
                    ToType = "date",
                    Tier = TypeChangeTier.CheckedConversion,
                    Status = TypeChangeStatus.Refused,
                    RequiresOptIn = "schema.convertTypes",
                    Rows = 1200,
                    FailingValues = 2,
                    Sample = [new TypeChangeValueSample { Value = "NA", Rows = 3 }, new TypeChangeValueSample { Value = "31/02/2020", Rows = 1 }],
                    Message = "[Spud] nvarchar(20) -> date: 2 stored values do not convert; the column is untouched.",
                    ManualSteps = ["Clean the values that do not convert.", "Run the flow again."],
                },
            },
        },
        events = new object[]
        {
            new { timestampUtc = Ended.AddSeconds(-50), level = "info", step = "target.typechange", message = "applied" },
            new { timestampUtc = Ended.AddSeconds(-40), level = "warning", step = "target.typechange", message = "refused" },
            new { timestampUtc = Ended.AddSeconds(-30), level = "Warning", step = "staging", message = "a second warning" },
            // No message, no timestamp: not events the run's timeline projects, so not warnings either.
            new { timestampUtc = Ended.AddSeconds(-20), level = "warning", step = "x", message = "" },
            new { level = "warning", step = "x", message = "no time" },
        },
    }, ArtifactJson);

    /// <summary>How the executor writes run.json: camelCase, enums by name.</summary>
    private static readonly JsonSerializerOptions ArtifactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly CatalogProjection.WrittenObject[] Written =
    [
        new("${env:dwh}|warehouse|arc|welllog", "Warehouse", "arc", "WellLog"),
        new("${env:dwh}|warehouse|arc|v_welllog", "Warehouse", "arc", "v_WellLog"),
    ];

    [Fact]
    public void ARun_CountsTheWarningsItsTimelineHolds()
    {
        var root = Json(Artifact());

        Assert.Equal(2, CatalogProjection.WarningCount(root));
        var run = CatalogProjection.RunFromJson(root, Repo);
        Assert.NotNull(run);
        Assert.Equal(2, run.WarningCount);
        // A run that succeeded with warnings is still succeeded: the count tells it apart.
        Assert.Equal(RunStatuses.Succeeded, run.Status);
    }

    [Fact]
    public void ARunWithoutEvents_HasNoWarnings()
    {
        var root = Json("""{ "runId": "11111111-1111-1111-1111-111111111111", "flowName": "f", "flowKind": "ing", "success": true }""");

        Assert.Equal(0, CatalogProjection.WarningCount(root));
        Assert.Equal(0, CatalogProjection.RunFromJson(root, Repo)!.WarningCount);
    }

    [Fact]
    public void EveryReport_IsARow_WithWhatTheRunFound()
    {
        var root = Json(Artifact());
        Assert.Equal(["[Warehouse].[arc].[WellLog]"], CatalogProjection.TypeChangeTables(root));

        var rows = CatalogProjection.TypeChanges(root, Run, Repo, Pipeline, Ended, "alice", _ => "the-key");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal((Repo, Run, Pipeline, Ended, "alice", "the-key"), (r.RepoId, r.RunId, r.PipelineId, r.OccurredUtc, r.RequestedBy, r.ObjectKey));
            Assert.Equal("[Warehouse].[arc].[WellLog]", r.TableName);
            Assert.Equal("target", r.Layer);
        });

        var applied = rows[0];
        Assert.Equal(("Depth", "int", "bigint", "Widening", "Applied"), (applied.ColumnName, applied.FromType, applied.ToType, applied.Tier, applied.Status));
        Assert.True(applied.RewritesTable);
        Assert.Equal(1200, applied.Rows);
        Assert.Null(applied.SampleJson);
        Assert.Null(applied.ManualStepsJson);

        var refused = rows[1];
        Assert.Equal(("Spud", "CheckedConversion", "Refused", "schema.convertTypes"), (refused.ColumnName, refused.Tier, refused.Status, refused.RequiresOptIn));
        Assert.Equal(2, refused.FailingValues);
        Assert.Equal("""[{"value":"NA","rows":3},{"value":"31/02/2020","rows":1}]""", refused.SampleJson);
        Assert.Equal("""["Clean the values that do not convert.","Run the flow again."]""", refused.ManualStepsJson);
        Assert.StartsWith("[Spud] nvarchar(20) -> date", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportMissingWhatIdentifiesIt_IsSkipped_AndARunWithoutReports_ProjectsNothing()
    {
        var root = Json("""
            { "result": { "typeChanges": [
                { "layer": "target", "table": "[d].[s].[t]", "column": "c", "fromType": "int", "toType": "bigint", "tier": "Widening", "status": "Applied" },
                { "layer": "target", "table": "[d].[s].[t]", "column": "", "fromType": "int", "toType": "bigint", "tier": "Widening", "status": "Applied", "message": "m" },
                "not an object"
            ] } }
            """);

        Assert.Empty(CatalogProjection.TypeChanges(root, Run, Repo, Pipeline, Ended, null, _ => "k"));
        Assert.Empty(CatalogProjection.TypeChanges(Json("""{ "result": { "rowsStaged": 5 } }"""), Run, Repo, Pipeline, Ended, null, _ => "k"));
        Assert.Empty(CatalogProjection.TypeChangeTables(Json("""{ "result": {} }""")));
    }

    [Fact]
    public void AStoredMessage_IsRedacted_AndCutToItsColumns()
    {
        var longColumn = new string('c', CatalogProjection.TypeChangeColumnLength + 10);
        var root = Json($$"""
            { "result": { "typeChanges": [
                { "layer": "target", "table": "[d].[s].[t]", "column": "{{longColumn}}", "fromType": "int", "toType": "bigint",
                  "tier": "Widening", "status": "Applied", "message": "failed against Server=db;User ID=sa;Password=Hunter2;" }
            ] } }
            """);

        var row = Assert.Single(CatalogProjection.TypeChanges(root, Run, Repo, Pipeline, Ended, null, _ => "k"));
        Assert.Equal(CatalogProjection.TypeChangeColumnLength, row.ColumnName.Length);
        Assert.DoesNotContain("Hunter2", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AThreePartTable_TakesTheKeyOfTheObjectThePipelineWrites()
    {
        // The catalog spelled the database and table in their own case; the key is the object's, so the row joins to
        // the object's columns.
        Assert.Equal(
            "${env:dwh}|warehouse|arc|welllog",
            CatalogProjection.TypeChangeObjectKey("[WAREHOUSE].[arc].[welllog]", "${env:dwh}", Written));
    }

    [Fact]
    public void AThreePartTable_LineageDoesNotName_IsKeyedAsLineageKeysADeclaredObject()
    {
        Assert.Equal(
            "${env:dwh}|warehouse|arc|wellbore",
            CatalogProjection.TypeChangeObjectKey("[Warehouse].[arc].[Wellbore]", "${env:DWH}", Written));
        Assert.Equal(
            "|warehouse|arc|wellbore",
            CatalogProjection.TypeChangeObjectKey("[Warehouse].[arc].[Wellbore]", null, []));
    }

    [Fact]
    public void ATwoPartView_TakesTheOneDatabaseItsObjectsAgreeOn()
    {
        // A file flow's typed view is named [schema].[name]; the catalog resolved its database from the connection.
        Assert.Equal(
            "${env:dwh}|warehouse|arc|v_welllog",
            CatalogProjection.TypeChangeObjectKey("[arc].[v_WellLog]", "${env:dwh}", Written));

        // The same schema and name under two databases cannot be told apart from a two-part name: the key keeps none.
        var ambiguous = new CatalogProjection.WrittenObject[]
        {
            new("s|a|arc|v_x", "a", "arc", "v_x"),
            new("s|b|arc|v_x", "b", "arc", "v_x"),
        };
        Assert.Equal("s||arc|v_x", CatalogProjection.TypeChangeObjectKey("[arc].[v_x]", "s", ambiguous));

        // A database-less object of that name is the one lineage recorded.
        Assert.Equal(
            "s||arc|v_y",
            CatalogProjection.TypeChangeObjectKey("[arc].[v_y]", "s", [new("s||arc|v_y", null, "arc", "v_y")]));
    }

    [Fact]
    public void ABracketedName_KeepsItsDotsAndEscapes()
    {
        Assert.Equal(
            "s|d|my.schema|odd]name",
            CatalogProjection.TypeChangeObjectKey("[d].[my.schema].[odd]]name]", "s", []));
    }
}
