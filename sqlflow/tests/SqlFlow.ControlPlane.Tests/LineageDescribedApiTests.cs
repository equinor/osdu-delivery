using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The described lineage end to end: an estate on disk is synced into the real catalog, then read through the API.
/// The estate is one landing flow, two lineage descriptions (<c>flowType: lin</c>) of the steps another system runs
/// before it, and one of a step that reads its table afterwards. The test holds the API to the rule the feature rests
/// on: every lineage answer is the same as before the descriptions existed unless the caller asks for the described
/// lineage, and with <c>described=true</c> the same answers continue past what SQLFlow manages, up to where the data
/// starts. It also covers the two endpoints that serve only described lineage (the steps, and the optional column
/// mappings), and that nothing can queue a run of a description.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LineageDescribedApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_linapi_" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public LineageDescribedApiTests() => Directory.CreateDirectory(_dir);

    private string Landing => "api_land_" + _suffix;

    private string Curate => "api_u1_curate_" + _suffix;

    private string Publish => "api_u2_publish_" + _suffix;

    private string Report => "api_d1_report_" + _suffix;

    private string CuratedKey => Described("lakehouse", "curated.orders_" + _suffix);

    private string OrdersKey => Described("erp", "sales.orders_" + _suffix);

    private string RatesKey => Described("erp", "sales.rates_" + _suffix);

    private string DashboardKey => Described("reporting", "sales_dashboard_" + _suffix);

    private static string Described(string system, string name)
        => NodeKey.For(LineageDescribedObject.ServerRef(system), null, null, name);

    private void Write(string relativePath, string yaml)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml.Replace("__S__", _suffix, StringComparison.Ordinal));
    }

    private void WriteLanding()
        => Write("flows/10-land.flow.yaml", """
            name: api_land___S__
            source:
              type: csv
              location: data/orders___S__
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: ApiLinOrders___S__
            """);

    private void WriteDescriptions()
    {
        Write("lineage/u1-curate.yaml", """
            flowType: lin
            name: api_u1_curate___S__
            description: Builds the curated orders table.
            system: lakehouse
            unit: job orders-daily, task curate
            owner: data-platform@example.com
            evidence:
              - { repo: lakehouse-jobs, path: jobs/curate.py, commit: 4f1c2ab9 }
            reads:
              - { system: erp, name: sales.orders___S__, as: src, origin: true }
              - { system: erp, name: sales.rates___S__, as: fx }
            writes:
              - name: curated.orders___S__
                columns:
                  order_id: src.order_no
                  customer_id: src.customer_no
                  amount_eur:
                    from: [src.amount, fx.rate]
                    note: amount * rate
            context: |
              Test orders are left out.
            """);
        Write("lineage/u2-publish.yaml", """
            flowType: lin
            name: api_u2_publish___S__
            system: lakehouse
            reads:
              - { name: curated.orders___S__, as: orders }
            writes:
              - feeds: api_land___S__
                columns:
                  amount: { from: orders.amount_eur, note: Rounded to two decimals. }
                  exported_at:
                  "*": orders.*
            """);
        Write("lineage/d1-report.yaml", """
            flowType: lin
            name: api_d1_report___S__
            description: The dashboard the finance team reads.
            system: reporting
            reads:
              - fedBy: api_land___S__
            writes:
              - name: sales_dashboard___S__
            """);
    }

    [SkippableFact]
    public async Task TheApi_AnswersAsBefore_UnlessAskedForTheDescribedLineage_AndThenGoesBackToTheSource()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = "api_lin_" + _suffix;
        var repoId = FlowIdentity.FromName(repo);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            // ---- The estate on its own: what every answer is before any description exists.
            WriteLanding();
            await SyncAsync(cs, repo);
            string tableKey;
            string fileKey;
            await using (var db = CatalogDatabase.Create(cs))
            {
                tableKey = (await db.LineageEdges.AsNoTracking().SingleAsync(
                    e => e.RepoId == repoId && e.Flow == Landing && e.Relation == nameof(LineageRelation.Writes))).ObjectKey;
                fileKey = (await db.LineageEdges.AsNoTracking().SingleAsync(
                    e => e.RepoId == repoId && e.Flow == Landing && e.Relation == nameof(LineageRelation.Reads))).ObjectKey;
            }

            var walkUri = $"/api/v1/lineage/objects/graph?key={Uri.EscapeDataString(tableKey)}&direction=both";
            var graphUri = $"/api/v1/lineage/project-graph?repoId={repoId}";
            var edgesUri = $"/api/v1/repos/{repoId}/lineage/edges?pageSize=200";
            var dossierUri = $"/api/v1/lineage/objects/dossier?key={Uri.EscapeDataString(tableKey)}";
            var wavesUri = $"/api/v1/repos/{repoId}/waves";

            var walkAlone = Comparable(await GetJsonAsync<ObjectLineageDto>(client, token, walkUri));
            var graphAlone = Comparable(await GetJsonAsync<ProjectGraphDto>(client, token, graphUri));
            var edgesAlone = Comparable(await GetJsonAsync<PagedResult<EdgeDto>>(client, token, edgesUri));
            var dossierAlone = Comparable(await GetJsonAsync<ObjectDossierDto>(client, token, dossierUri));
            var wavesAlone = await GetStringAsync(client, token, wavesUri);

            // ---- With the descriptions synced.
            WriteDescriptions();
            var synced = await SyncAsync(cs, repo);
            Assert.DoesNotContain(synced.Warnings, w => w.StartsWith("described lineage:", StringComparison.Ordinal));

            // Every answer that was not asked for the described lineage is what it was. The one difference a caller
            // can see is the flag saying the described lineage has more to tell.
            var walk = await GetJsonAsync<ObjectLineageDto>(client, token, walkUri);
            Assert.Equal(walkAlone, Comparable(walk));
            Assert.False(walk.DescribedIncluded);
            Assert.True(walk.DescribedAvailable);
            Assert.Null(walk.DescribedSteps);
            Assert.Equal(fileKey, Assert.Single(walk.Upstream).ObjectKey);
            Assert.Empty(walk.Downstream);

            var graph = await GetJsonAsync<ProjectGraphDto>(client, token, graphUri);
            Assert.Equal(graphAlone, Comparable(graph));
            Assert.False(graph.DescribedIncluded);
            Assert.True(graph.DescribedAvailable);
            Assert.Equal(Landing, Assert.Single(graph.Pipelines).Name);

            Assert.Equal(edgesAlone, Comparable(await GetJsonAsync<PagedResult<EdgeDto>>(client, token, edgesUri)));
            Assert.Equal(dossierAlone, Comparable(await GetJsonAsync<ObjectDossierDto>(client, token, dossierUri)));
            Assert.Equal(wavesAlone, await GetStringAsync(client, token, wavesUri));

            // The listings of pipelines are the flows that run, as before. A description is listed when asked for by
            // its kind, and a folder holding only descriptions is no project or scope of its own.
            var pipelinesUri = $"/api/v1/pipelines?repoId={repoId}";
            var listed = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(client, token, pipelinesUri);
            Assert.Equal(Landing, Assert.Single(listed.Items).Name);
            Assert.Equal(1, listed.Total);
            var descriptions = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(
                client, token, pipelinesUri + "&kind=" + LineageDescriptionFlow.FlowType);
            Assert.Equal([Report, Curate, Publish], descriptions.Items.Select(p => p.Name));
            Assert.All(descriptions.Items, p =>
            {
                Assert.Equal(-1, p.Wave);
                Assert.Equal(PipelineExecutionModes.Disabled, p.ExecutionMode);
            });
            Assert.Equal(
                ["flows"],
                await GetJsonAsync<IReadOnlyList<string>>(client, token, $"/api/v1/pipelines/projects?repoId={repoId}"));
            var batch = Assert.Single(await GetJsonAsync<IReadOnlyList<PipelineBatchDto>>(
                client, token, $"/api/v1/pipelines/batches?repoId={repoId}"));
            Assert.Equal(1, batch.FlowCount);
            var scopes = await GetJsonAsync<IReadOnlyList<LineageProjectDto>>(client, token, "/api/v1/lineage/projects");
            Assert.Equal("flows", Assert.Single(scopes, s => s.RepoId == repoId).Project);

            // ---- The walk, asked to follow the described lineage: back to where the data starts, and on to what
            // reads the table afterwards.
            var followed = await GetJsonAsync<ObjectLineageDto>(client, token, walkUri + "&described=true");
            Assert.True(followed.DescribedIncluded);

            var landed = followed.Upstream.Single(s => s.ObjectKey == fileKey);
            Assert.Equal(1, landed.Depth);
            Assert.False(landed.Described);
            Assert.Equal(Landing, landed.FlowName);

            var curated = followed.Upstream.Single(s => s.ObjectKey == CuratedKey);
            Assert.Equal(2, curated.Depth);
            Assert.True(curated.Described);
            Assert.Equal(Publish, curated.FlowName);
            Assert.Equal("lakehouse", curated.System);
            Assert.Equal("Described", curated.Kind);
            Assert.Equal("curated.orders_" + _suffix, curated.ObjectName);
            Assert.Null(curated.Boundary); // a description writes it, so the chain goes on

            var orders = followed.Upstream.Single(s => s.ObjectKey == OrdersKey);
            Assert.Equal(3, orders.Depth);
            Assert.Equal(Curate, orders.FlowName);
            Assert.Equal("erp", orders.System);
            Assert.Equal("origin", orders.Boundary); // marked as where the data starts

            var rates = followed.Upstream.Single(s => s.ObjectKey == RatesKey);
            Assert.Equal("open", rates.Boundary); // nothing describes what makes it, and nothing calls it the origin
            Assert.Equal(4, followed.Upstream.Count);

            var dashboard = Assert.Single(followed.Downstream);
            Assert.Equal(DashboardKey, dashboard.ObjectKey);
            Assert.True(dashboard.Described);
            Assert.Equal(Report, dashboard.FlowName);
            Assert.Equal("reporting", dashboard.System);

            Assert.NotNull(followed.DescribedSteps);
            Assert.Equal([Report, Curate, Publish], followed.DescribedSteps.Select(s => s.Name));
            var curateStep = followed.DescribedSteps.Single(s => s.Name == Curate);
            Assert.Equal("lakehouse", curateStep.System);
            Assert.Equal("job orders-daily, task curate", curateStep.Unit);
            Assert.Equal("Builds the curated orders table.", curateStep.Description);
            Assert.Equal("lineage/u1-curate.yaml", curateStep.File);

            // An object only descriptions name can be walked from, by a caller that asks for the described lineage.
            var fromCurated = $"/api/v1/lineage/objects/graph?key={Uri.EscapeDataString(CuratedKey)}&direction=downstream&depth=3";
            using (var refused = await SendAsync(client, token, HttpMethod.Get, fromCurated))
            {
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains("described=true", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            var down = await GetJsonAsync<ObjectLineageDto>(client, token, fromCurated + "&described=true");
            Assert.Equal("Described", down.Kind);
            Assert.Equal([fileKey, tableKey, DashboardKey], down.Downstream.OrderBy(s => s.Depth).Select(s => s.ObjectKey));
            Assert.False(down.Downstream.Single(s => s.ObjectKey == tableKey).Described); // the estate's own hop

            using (var unknown = await SendAsync(
                       client, token, HttpMethod.Get,
                       $"/api/v1/lineage/objects/graph?key={Uri.EscapeDataString(Described("erp", "no.such_" + _suffix))}&described=true"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            // ---- The graph, asked for the described lineage. Scoped to the project that holds the flow, it still
            // draws the descriptions upstream of it, which are kept in another folder.
            var drawn = await GetJsonAsync<ProjectGraphDto>(client, token, graphUri + "&project=flows&described=true");
            Assert.True(drawn.DescribedIncluded);
            Assert.False(drawn.DescribedAvailable);
            Assert.Equal(
                new[] { Landing, Curate, Publish, Report }.Order(StringComparer.Ordinal),
                drawn.Pipelines.Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.True(drawn.Pipelines.Single(p => p.Name == Landing).IsSeed);
            Assert.All(drawn.Pipelines.Where(p => p.Name != Landing), p =>
            {
                Assert.Equal(LineageDescriptionFlow.FlowType, p.Kind);
                Assert.Equal(-1, p.Wave);
                Assert.False(p.IsSeed);
            });
            Assert.Equal(1, drawn.Pipelines.Single(p => p.Name == Landing).Wave);

            Assert.True(drawn.Objects.Single(o => o.Key == CuratedKey).Described);
            Assert.True(drawn.Objects.Single(o => o.Key == OrdersKey).Described);
            Assert.False(drawn.Objects.Single(o => o.Key == fileKey).Described);
            Assert.False(drawn.Objects.Single(o => o.Key == tableKey).Described);

            var landingId = CatalogIdentity.Pipeline(repoId, Landing);
            Assert.All(drawn.FlowGraph, e => Assert.Equal(e.PipelineId != landingId, e.Described));
            Assert.Contains(drawn.FlowGraph, e => e.Described && e.Source == CuratedKey);
            Assert.Contains(drawn.Edges, e => e.Flow == Publish && e.Relation == nameof(LineageRelation.DescribedWrites) && e.ObjectKey == fileKey);
            Assert.Contains(drawn.Edges, e => e.Flow == Landing && e.Relation == nameof(LineageRelation.Reads) && e.ObjectKey == fileKey);

            // A jump onto an object only descriptions name draws the described lineage without being asked.
            var jumped = await GetJsonAsync<ProjectGraphDto>(
                client, token, $"/api/v1/lineage/project-graph?expand={Uri.EscapeDataString(CuratedKey)}");
            Assert.True(jumped.DescribedIncluded);
            Assert.Contains(jumped.Pipelines, p => p.Name == Curate);
            Assert.Contains(jumped.Pipelines, p => p.Name == Publish);

            // ---- The edges and the dossier, asked for the described lineage.
            var allEdges = await GetJsonAsync<PagedResult<EdgeDto>>(client, token, edgesUri + "&described=true");
            Assert.Equal(2 + 7, allEdges.Total);
            var writes = await GetJsonAsync<PagedResult<EdgeDto>>(
                client, token, edgesUri + "&relation=" + nameof(LineageRelation.DescribedWrites));
            Assert.Equal([Report, Curate, Publish], writes.Items.Select(e => e.Flow).Order(StringComparer.Ordinal));

            var dossier = await GetJsonAsync<ObjectDossierDto>(client, token, dossierUri + "&described=true");
            Assert.Contains(dossier.Edges, e => e.Flow == Report && e.Relation == nameof(LineageRelation.DescribedReads));

            // ---- The described steps, listed.
            var steps = await GetJsonAsync<PagedResult<DescribedStepDto>>(
                client, token, $"/api/v1/lineage/described/steps?repoId={repoId}");
            Assert.Equal([Report, Curate, Publish], steps.Items.Select(s => s.Name));
            var step = steps.Items.Single(s => s.Name == Curate);
            Assert.Equal(repo, step.RepoName);
            Assert.Equal("lakehouse", step.System);
            Assert.Equal("data-platform@example.com", step.Owner);
            Assert.True(step.HasContext);
            Assert.Equal(1, step.EvidenceCount);
            Assert.Equal(3, step.MappedColumns);
            Assert.Null(step.Problem);
            Assert.Equal([OrdersKey, RatesKey], step.Reads.Select(r => r.ObjectKey).Order(StringComparer.Ordinal));
            Assert.All(step.Reads, r =>
            {
                Assert.True(r.DescribedObject);
                Assert.Equal("erp", r.System);
            });
            Assert.Equal(CuratedKey, Assert.Single(step.Writes).ObjectKey);

            // A description that names a flow is connected to that flow's own object, which is no described object.
            var publishStep = steps.Items.Single(s => s.Name == Publish);
            var landedWrite = Assert.Single(publishStep.Writes);
            Assert.Equal(fileKey, landedWrite.ObjectKey);
            Assert.False(landedWrite.DescribedObject);
            Assert.Null(landedWrite.System);
            Assert.False(publishStep.HasContext);

            var searched = await GetJsonAsync<PagedResult<DescribedStepDto>>(
                client, token, $"/api/v1/lineage/described/steps?repoId={repoId}&search=finance");
            Assert.Equal(Report, Assert.Single(searched.Items).Name);

            // ---- Column mappings: the optional level of detail, served on request only.
            var columnsUri = $"/api/v1/lineage/described/columns?to={Uri.EscapeDataString(fileKey)}";
            var direct = await GetJsonAsync<ColumnMappingsDto>(client, token, columnsUri);
            Assert.False(direct.Composed);
            Assert.False(direct.Truncated);
            Assert.Equal(["amount", "exported_at", "*"], direct.Columns.Select(c => c.TargetColumn));
            var amount = direct.Columns[0];
            Assert.Equal(Publish, amount.Flow);
            Assert.Equal("Rounded to two decimals.", amount.Note);
            var amountSource = Assert.Single(amount.Sources);
            Assert.Equal(CuratedKey, amountSource.ObjectKey);
            Assert.Equal("amount_eur", amountSource.Column);
            Assert.Empty(direct.Columns[1].Sources); // the step makes this one itself
            Assert.Equal("*", Assert.Single(direct.Columns[2].Sources).Column);

            // A column that is not listed by name is answered by the entry standing for the rest, under its own name.
            var passed = await GetJsonAsync<ColumnMappingsDto>(client, token, columnsUri + "&column=customer_id");
            var customer = Assert.Single(passed.Columns);
            Assert.Equal("customer_id", customer.TargetColumn);
            Assert.Equal("customer_id", Assert.Single(customer.Sources).Column);

            // From the source system to the landed file: followed through the curated table in between.
            var composed = await GetJsonAsync<ColumnMappingsDto>(
                client, token, columnsUri + "&from=" + Uri.EscapeDataString(OrdersKey));
            Assert.True(composed.Composed);
            Assert.Equal("sales.orders_" + _suffix, composed.FromName);
            Assert.Equal(["amount", "*"], composed.Columns.Select(c => c.TargetColumn));
            var composedAmount = Assert.Single(composed.Columns[0].Sources);
            Assert.Equal(OrdersKey, composedAmount.ObjectKey);
            Assert.Equal("amount", composedAmount.Column);
            var hop = Assert.Single(composedAmount.Via);
            Assert.Equal(CuratedKey, hop.ObjectKey);
            Assert.Equal("amount_eur", hop.Column);
            Assert.Equal("amount * rate", hop.Note);
            Assert.Equal(
                ["amount_eur>amount", "customer_id>customer_no", "order_id>order_no"],
                composed.Columns[1].Sources.Select(s => $"{Assert.Single(s.Via).Column}>{s.Column}").Order(StringComparer.Ordinal));

            var one = await GetJsonAsync<ColumnMappingsDto>(
                client, token, columnsUri + "&column=order_id&from=" + Uri.EscapeDataString(OrdersKey));
            Assert.Equal("order_no", Assert.Single(Assert.Single(one.Columns).Sources).Column);

            // An object no description maps answers with nothing and says why, rather than failing.
            var unmapped = await GetJsonAsync<ColumnMappingsDto>(
                client, token, $"/api/v1/lineage/described/columns?to={Uri.EscapeDataString(tableKey)}");
            Assert.Empty(unmapped.Columns);
            Assert.Contains("optional level of detail", Assert.Single(unmapped.Notes), StringComparison.Ordinal);

            var notMapped = await GetJsonAsync<ColumnMappingsDto>(client, token, $"{columnsUri}&from={Uri.EscapeDataString(RatesKey)}&column=exported_at");
            Assert.Empty(notMapped.Columns);
            Assert.NotEmpty(notMapped.Notes);

            var byDescription = await GetJsonAsync<ColumnMappingsDto>(
                client, token, $"/api/v1/lineage/described/columns?pipelineId={CatalogIdentity.Pipeline(repoId, Curate)}");
            Assert.Equal(["order_id", "customer_id", "amount_eur"], byDescription.Columns.Select(c => c.TargetColumn));
            Assert.Equal(2, byDescription.Columns[2].Sources.Count);

            using (var noTarget = await SendAsync(client, token, HttpMethod.Get, "/api/v1/lineage/described/columns"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, noTarget.StatusCode);
            }

            // ---- Nothing queues a run of a description: not a trigger, not as the anchor of a node scope, and a
            // schedule does not take one as a member.
            var operate = await IssueTokenAsync(client, ["operate"]);
            using (var trigger = await SendAsync(
                       client, operate, HttpMethod.Post, "/api/v1/runs", new RunTriggerRequest(repoId, Curate)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, trigger.StatusCode);
                Assert.Contains("never executed", await trigger.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var node = await SendAsync(
                       client, operate, HttpMethod.Post, "/api/v1/runs", new RunTriggerRequest(repoId, Curate, Scope: "node")))
            {
                Assert.Equal(HttpStatusCode.BadRequest, node.StatusCode);
                Assert.Contains("never executed", await node.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var schedule = await SendAsync(
                       client, operate, HttpMethod.Post, "/api/v1/schedules",
                       new CreateScheduleRequest(repoId, [Landing, Curate], "0 1 * * *", null, null, true, Name: "lin_sched_" + _suffix)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, schedule.StatusCode);
                Assert.Contains("never executed", await schedule.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.Runs.AnyAsync(r => r.RepoId == repoId));
                Assert.False(await db.Schedules.AnyAsync(s => s.RepoId == repoId));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var objectKeys = await db.LineageEdges.Where(e => e.RepoId == repoId).Select(e => e.ObjectKey).Distinct().ToListAsync();
            await db.LineageColumnMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => objectKeys.Contains(o.Key)).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private async Task<CatalogSyncResult> SyncAsync(string cs, string repo)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
    }

    /// <summary>A walk as text that is the same for the same lineage: without the flag that says the described
    /// lineage has more, which is the one thing a description may change in an answer that did not ask for it.</summary>
    private static string Comparable(ObjectLineageDto walk)
        => JsonSerializer.Serialize(walk with { DescribedAvailable = false }, Web);

    /// <summary>A graph as text that is the same for the same lineage: without the row ids a sync mints anew, and
    /// without the flag that says the described lineage has more.</summary>
    private static string Comparable(ProjectGraphDto graph)
        => JsonSerializer.Serialize(
            graph with { Edges = [.. graph.Edges.Select(e => e with { Id = 0 })], DescribedAvailable = false }, Web);

    private static string Comparable(PagedResult<EdgeDto> edges)
        => JsonSerializer.Serialize(edges with { Items = [.. edges.Items.Select(e => e with { Id = 0 })] }, Web);

    /// <summary>A dossier as text that is the same for the same lineage: without the row ids a sync mints anew and
    /// the time the object was last seen, which every sync moves.</summary>
    private static string Comparable(ObjectDossierDto dossier)
        => JsonSerializer.Serialize(
            dossier with
            {
                Object = dossier.Object with { LastSeenUtc = default },
                Edges = [.. dossier.Edges.Select(e => e with { Id = 0 })],
            },
            Web);

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, HttpMethod method, string relativeUri, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> GetStringAsync(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, relativeUri);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, relativeUri);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {relativeUri} answered {(int)response.StatusCode}: {text}");
        var value = JsonSerializer.Deserialize<T>(text, Web);
        Assert.NotNull(value);
        return value;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
