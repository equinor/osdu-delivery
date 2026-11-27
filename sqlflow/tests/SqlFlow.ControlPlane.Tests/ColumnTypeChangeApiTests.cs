using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A run's type changes and a column's type history, read through the API against the real catalog, and a run's warning
/// count wherever a run is read: its detail, the runs list and its pipeline's last run. Every seeded row sits under a
/// repository and an object key unique to the test, and is removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnTypeChangeApiTests
{
    private static readonly DateTime Anchor = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task ARunsTypeChanges_AndAColumnsHistory_AreRead_WithTheRunsWarnings()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("tcapi_" + suffix);
        var flowName = "tcapi_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var objectKey = $"${{env:tcapi_{suffix}}}|warehouse|arc|welllog";
        var earlier = Guid.CreateVersion7();
        var latest = Guid.CreateVersion7();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId,
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "ing",
                    RelativePath = $"flows/{flowName}.flow.yaml",
                    ContentHash = string.Empty,
                    Yaml = $"name: {flowName}\n",
                    DefinitionJson = "{}",
                    Active = true,
                    FirstSeenUtc = Anchor,
                    LastSeenUtc = Anchor,
                });
                db.Runs.Add(Run(earlier, repoId, flowName, Anchor, warnings: 0));
                db.Runs.Add(Run(latest, repoId, flowName, Anchor.AddDays(1), warnings: 2));
                db.ColumnTypeChanges.AddRange(
                    Change(earlier, repoId, pipelineId, objectKey, "Depth", "int", "bigint", "Widening", "Applied", Anchor.AddMinutes(1)),
                    Change(latest, repoId, pipelineId, objectKey, "Depth", "bigint", "decimal(28,0)", "Widening", "Applied", Anchor.AddDays(1).AddMinutes(1)),
                    Change(latest, repoId, pipelineId, objectKey, "Spud", "nvarchar(20)", "date", "CheckedConversion", "Refused", Anchor.AddDays(1).AddMinutes(1),
                        failing: 2, sample: """[{"value":"NA","rows":3}]""", steps: """["Clean the values.","Run again."]"""));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await AiContextTestKit.IssueTokenAsync(client, ["read"]);

            // The run's reports, in the order it reported them, each saying whether it needs an operator.
            var reported = await GetJsonAsync<PagedResult<ColumnTypeChangeDto>>(client, token, $"/api/v1/runs/{latest}/type-changes");
            Assert.Equal(2, reported.Total);
            Assert.Equal(["Depth", "Spud"], reported.Items.Select(c => c.Column));
            var applied = reported.Items[0];
            Assert.False(applied.NeedsAttention);
            Assert.Empty(applied.Sample);
            Assert.Empty(applied.ManualSteps);
            var refused = reported.Items[1];
            Assert.True(refused.NeedsAttention);
            Assert.Equal(("nvarchar(20)", "date", "CheckedConversion", "Refused"), (refused.FromType, refused.ToType, refused.Tier, refused.Status));
            Assert.Equal(2, refused.FailingValues);
            Assert.Equal(new TypeChangeSampleDto("NA", 3), Assert.Single(refused.Sample));
            Assert.Equal(["Clean the values.", "Run again."], refused.ManualSteps);
            Assert.Equal((objectKey, "[warehouse].[arc].[WellLog]", "target"), (refused.ObjectKey, refused.Table, refused.Layer));

            // A page of one says how many there are.
            var paged = await GetJsonAsync<PagedResult<ColumnTypeChangeDto>>(client, token, $"/api/v1/runs/{latest}/type-changes?pageSize=1&page=2");
            Assert.Equal((2L, "Spud"), (paged.Total, Assert.Single(paged.Items).Column));

            // An unknown run is a 404, not an empty page.
            using (var missing = await GetAsync(client, token, $"/api/v1/runs/{Guid.NewGuid()}/type-changes"))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            // A column's type history, newest first.
            var key = Uri.EscapeDataString(objectKey);
            var depth = await GetJsonAsync<PagedResult<ColumnTypeChangeDto>>(client, token, $"/api/v1/schema-changes/columns?objectKey={key}&column=Depth");
            Assert.Equal([latest, earlier], depth.Items.Select(c => c.RunId));
            Assert.Equal(["decimal(28,0)", "bigint"], depth.Items.Select(c => c.ToType));

            // Without a column, the history of every column of the object.
            var all = await GetJsonAsync<PagedResult<ColumnTypeChangeDto>>(client, token, $"/api/v1/schema-changes/columns?objectKey={key}");
            Assert.Equal(3, all.Total);

            using (var noKey = await GetAsync(client, token, "/api/v1/schema-changes/columns?column=Depth"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
            }

            // The run's warnings: on its detail, on the runs list, and on its pipeline's last run.
            Assert.Equal(2, (await GetJsonAsync<RunDetailDto>(client, token, $"/api/v1/runs/{latest}")).WarningCount);
            var listed = await GetJsonAsync<PagedResult<RunSummaryDto>>(client, token, $"/api/v1/runs?pipelineId={pipelineId}");
            Assert.Equal([2, 0], listed.Items.Select(r => r.WarningCount));
            var durations = await GetJsonAsync<PipelineRunDurationsDto>(client, token, $"/api/v1/pipelines/{pipelineId}/runs/durations");
            Assert.Equal((latest, 2), (durations.Last!.RunId, durations.Last.WarningCount));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ColumnTypeChanges.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogRun Run(Guid runId, Guid repoId, string flowName, DateTime startUtc, int warnings) => new()
    {
        RunId = runId,
        PipelineId = CatalogIdentity.Pipeline(repoId, flowName),
        RepoId = repoId,
        FlowName = flowName,
        FlowKind = "ing",
        Status = RunStatuses.Succeeded,
        Success = true,
        SchemaVersion = 1,
        EnqueuedUtc = startUtc,
        StartUtc = startUtc,
        EndUtc = startUtc.AddMinutes(2),
        DurationSeconds = 120,
        WrittenUtc = startUtc.AddMinutes(2),
        WarningCount = warnings,
    };

    private static CatalogColumnTypeChange Change(
        Guid runId, Guid repoId, Guid pipelineId, string objectKey, string column, string from, string to, string tier,
        string status, DateTime occurredUtc, long? failing = null, string? sample = null, string? steps = null) => new()
    {
        RepoId = repoId,
        RunId = runId,
        PipelineId = pipelineId,
        ObjectKey = objectKey,
        TableName = "[warehouse].[arc].[WellLog]",
        Layer = "target",
        ColumnName = column,
        FromType = from,
        ToType = to,
        Tier = tier,
        Status = status,
        FailingValues = failing,
        SampleJson = sample,
        ManualStepsJson = steps,
        Message = $"[{column}] {from} -> {to}: {status}.",
        OccurredUtc = occurredUtc,
    };

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await GetAsync(client, token, relativeUri);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
