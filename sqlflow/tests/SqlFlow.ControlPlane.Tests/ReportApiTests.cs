using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Reports;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A report end to end: an estate on disk holding one flow and one report (<c>flowType: rpt</c>) is synced into the real
/// catalog, the report is read through the API, and its queries are run on the worker against a real database. The test
/// holds the feature to the rules it rests on: a report is a pipeline row that is no flow (left out of the listings of
/// pipelines, never run, in no schedule); the statement that runs is the synced document's and a request gives only
/// values, each bound with its declared type; a statement that is not one read-only SELECT never runs; the row limit is
/// kept and said; and an assistant run can neither queue a report's query nor read its answer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReportApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_rptapi_" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public ReportApiTests() => Directory.CreateDirectory(_dir);

    private string Landing => "rpt_land_" + _suffix;

    private string Report => "rpt_numbers_" + _suffix;

    private string EnvName => "SQLFLOW_RPT_" + _suffix;

    private string Reference => "${env:" + EnvName + "}";

    private void Write(string relativePath, string yaml)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml
            .Replace("__S__", _suffix, StringComparison.Ordinal)
            .Replace("__REF__", Reference, StringComparison.Ordinal));
    }

    private void WriteEstate()
    {
        Write("flows/10-land.flow.yaml", """
            name: rpt_land___S__
            source:
              type: csv
              location: data/orders___S__
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: RptOrders___S__
            """);

        // The statements read nothing but their own values, so their answers are the same whatever the database holds.
        Write("reports/numbers.yaml", """
            flowType: rpt
            name: rpt_numbers___S__
            title: Numbers
            description: A handful of numbers, to look at.
            batch: probes
            connection: __REF__
            maxRows: 100

            parameters:
              from:
                label: From
                type: integer
                default: 2
              factor:
                type: number
                default: 1.5
              day:
                type: date
              label:
                description: A word carried into every row.
              marked:
                type: flag
              kind:
                choices: [even, odd]

            queries:
              numbers:
                title: The numbers
                section: Overview
                sql: |
                  SELECT v.N, v.N * @factor AS Scaled, @day AS Day, @label AS Label, @marked AS Marked
                  FROM (VALUES (1), (2), (3), (4), (5)) AS v(N)
                  WHERE v.N >= @from
                    AND (@kind IS NULL OR (@kind = 'even' AND v.N % 2 = 0) OR (@kind = 'odd' AND v.N % 2 = 1))
                  ORDER BY v.N
                show:
                  - { type: bar, by: N, values: Scaled }
                  - table
                columns:
                  Scaled: { label: Times the factor, format: number, decimals: 1 }
              tables:
                title: Tables in the database
                sql: SELECT COUNT(*) AS Tables FROM sys.tables
                show: figure
              capped:
                maxRows: 2
                sql: SELECT v.N FROM (VALUES (1), (2), (3)) AS v(N) ORDER BY v.N
              broken:
                sql: SELECT 1 AS One; SELECT 2 AS Two
            """);
    }

    [SkippableFact]
    public async Task AReport_IsRead_AndItsQueriesRunOnTheWorker_WithNothingFromTheCallerButValues()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = "rpt_api_" + _suffix;
        var repoId = FlowIdentity.FromName(repo);
        var reportId = CatalogIdentity.Pipeline(repoId, Report);

        Environment.SetEnvironmentVariable(EnvName, cs);
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            WriteEstate();
            CatalogSyncResult synced;
            await using (var db = CatalogDatabase.Create(cs))
            {
                synced = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            // The scan says which statement cannot run, and keeps the report with its other queries.
            Assert.Contains(synced.Warnings, w =>
                w.Contains($"report '{Report}': query 'broken' cannot run", StringComparison.Ordinal)
                && w.Contains("exactly ONE statement", StringComparison.Ordinal));

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read", "operate"]);

            // ---- A report is a pipeline row and no flow: the listings hold the flows that run, and list it when asked
            // for by its kind, in no wave, with the connection it reads.
            var pipelinesUri = $"/api/v1/pipelines?repoId={repoId}";
            var listed = await GetJsonAsync<PagedResult<PipelineSummaryDto>>(client, token, pipelinesUri);
            Assert.Equal(Landing, Assert.Single(listed.Items).Name);
            Assert.Equal(1, listed.Total);
            var asKind = Assert.Single(
                (await GetJsonAsync<PagedResult<PipelineSummaryDto>>(client, token, pipelinesUri + "&kind=" + ReportFlow.FlowType)).Items);
            Assert.Equal((Report, -1, PipelineExecutionModes.Disabled), (asKind.Name, asKind.Wave, asKind.ExecutionMode));
            var batch = Assert.Single(await GetJsonAsync<IReadOnlyList<PipelineBatchDto>>(
                client, token, $"/api/v1/pipelines/batches?repoId={repoId}"));
            Assert.Equal(1, batch.FlowCount);
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(Reference, (await db.Pipelines.AsNoTracking().SingleAsync(p => p.Id == reportId)).SourceServer);
            }

            // ---- The list of reports, and the report as its page reads it.
            var summary = Assert.Single(await GetJsonAsync<IReadOnlyList<ReportSummaryDto>>(client, token, $"/api/v1/reports?repoId={repoId}"));
            Assert.Equal((reportId, Report, "Numbers", "probes", repo), (summary.Id, summary.Name, summary.Title, summary.Batch, summary.RepoName));
            Assert.Equal((4, 5, 6), (summary.Queries, summary.Views, summary.Parameters));
            Assert.Equal("reports/numbers.yaml", summary.RelativePath);
            Assert.Null(summary.Problem);

            var report = await GetJsonAsync<ReportDto>(client, token, $"/api/v1/reports/{reportId}");
            Assert.Equal(("Numbers", "A handful of numbers, to look at.", Reference), (report.Title, report.Description, report.Connection));
            Assert.Equal(["from", "factor", "day", "label", "marked", "kind"], report.Parameters.Select(p => p.Name));
            var from = report.Parameters[0];
            Assert.Equal(("From", "integer", "2"), (from.Label, from.Type, from.Default));
            Assert.Equal(["numbers"], from.UsedBy);
            Assert.Equal("label", report.Parameters[3].Label); // a parameter without a label is shown by its name
            Assert.Equal(["even", "odd"], report.Parameters[5].Choices.Select(c => c.Value));

            Assert.Equal(["numbers", "tables", "capped", "broken"], report.Queries.Select(q => q.Name));
            var numbers = report.Queries[0];
            Assert.Equal(("The numbers", "Overview", 100, ReportFlow.DefaultTimeoutSeconds), (numbers.Title, numbers.Section, numbers.MaxRows, numbers.TimeoutSeconds));
            Assert.Equal(["factor", "day", "label", "marked", "from", "kind"], numbers.Parameters);
            Assert.Equal([ReportViewTypes.Bar, ReportViewTypes.Table], numbers.Views.Select(v => v.Type));
            Assert.Equal("Times the factor", Assert.Single(numbers.Columns).Label);
            Assert.Null(numbers.Problem);
            Assert.Equal(2, report.Queries[2].MaxRows);
            Assert.Equal("capped", report.Queries[2].Title); // a query without a title is shown by its name
            Assert.Contains("exactly ONE statement", report.Queries[3].Problem, StringComparison.Ordinal);

            // ---- A run with nothing given binds the defaults, and NULL where there is none.
            var defaults = await RunAsync(client, token, reportId, "numbers", new { });
            Assert.Equal("numbers", defaults.Accepted.Query);
            Assert.Equal(
                ["factor=1.5", "day=", "label=", "marked=", "from=2", "kind="],
                defaults.Accepted.Parameters.Select(p => $"{p.Name}={p.Value}"));
            Assert.Equal(["N", "Scaled", "Day", "Label", "Marked"], defaults.Columns);
            Assert.Equal(["2", "3", "4", "5"], defaults.Rows.Select(r => r[0]));
            Assert.Equal(3.0m, decimal.Parse(defaults.Rows[0][1]!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.All(defaults.Rows, r => Assert.Equal([null, null, null], r.Skip(2)));
            Assert.False(defaults.Truncated);

            // ---- A run given values binds each with its declared type: a number multiplies, a date comes back a date, a
            // flag a bit, and the text is a value, never part of the statement.
            var given = await RunAsync(client, token, reportId, "numbers", new
            {
                values = new Dictionary<string, string?>
                {
                    ["FROM"] = "3",
                    ["factor"] = "2.25",
                    ["day"] = "2026-01-31",
                    ["label"] = "x'; DROP TABLE catalog.Pipeline; --",
                    ["marked"] = "true",
                    ["kind"] = "odd",
                },
            });
            Assert.Equal(["3", "5"], given.Rows.Select(r => r[0]));
            Assert.Equal(6.75m, decimal.Parse(given.Rows[0][1]!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.StartsWith("2026-01-31", given.Rows[0][2], StringComparison.Ordinal);
            Assert.Equal("x'; DROP TABLE catalog.Pipeline; --", given.Rows[0][3]);
            Assert.Equal("True", given.Rows[0][4]);

            await using (var db = CatalogDatabase.Create(cs))
            {
                // The queue row is the record of what ran and who asked: the document's statement, the values beside it.
                var task = await db.ComputeTasks.AsNoTracking().SingleAsync(t => t.TaskId == given.Accepted.TaskId);
                Assert.Equal("reportQuery", task.Operation);
                Assert.Equal(Reference, task.SourceRef);
                Assert.False(string.IsNullOrWhiteSpace(task.RequestedBy));
                Assert.Contains("FROM (VALUES (1), (2), (3), (4), (5)) AS v(N)", task.ArgumentsJson, StringComparison.Ordinal);
                Assert.Contains($"\"report\":\"{Report}\"", task.ArgumentsJson, StringComparison.Ordinal);
                Assert.Contains("\"reportQuery\":\"numbers\"", task.ArgumentsJson, StringComparison.Ordinal);
                Assert.DoesNotContain("@from = 3", task.ArgumentsJson, StringComparison.Ordinal);
                Assert.True(await db.Pipelines.AnyAsync(p => p.Id == reportId)); // and the table the text named is still there
            }

            // A value cleared by the person binds NULL, whatever the default.
            var cleared = await RunAsync(client, token, reportId, "numbers", new { values = new Dictionary<string, string?> { ["from"] = "" } });
            Assert.Empty(cleared.Rows); // N >= NULL is never true

            // The task history names the report and the query a task read.
            var history = await GetJsonAsync<PagedResult<ComputeTaskSummaryDto>>(
                client, token, $"/api/v1/datasources/tasks?operation=reportQuery&reference={Uri.EscapeDataString(Reference)}");
            Assert.All(history.Items, t => Assert.Equal($"{Report}: numbers", t.Target));
            Assert.Equal(3, history.Total);

            // ---- A declared statement may read the server's own catalog, and the row limit is kept and said.
            var tables = await RunAsync(client, token, reportId, "tables", new { });
            Assert.True(int.Parse(Assert.Single(tables.Rows)[0]!, System.Globalization.CultureInfo.InvariantCulture) > 0);
            var capped = await RunAsync(client, token, reportId, "CAPPED", new { });
            Assert.Equal(["1", "2"], capped.Rows.Select(r => r[0]));
            Assert.True(capped.Truncated);

            // ---- What cannot run is refused before anything is queued, each with what is wrong.
            await RefusedAsync(client, token, $"/api/v1/reports/{reportId}/queries/broken/run", new { }, HttpStatusCode.BadRequest, "exactly ONE statement");
            await RefusedAsync(
                client, token, $"/api/v1/reports/{reportId}/queries/numbers/run",
                new { values = new Dictionary<string, string?> { ["from"] = "three" } }, HttpStatusCode.BadRequest, "The value of parameter 'from' is not an integer");
            await RefusedAsync(
                client, token, $"/api/v1/reports/{reportId}/queries/numbers/run",
                new { values = new Dictionary<string, string?> { ["kind"] = "prime" } }, HttpStatusCode.BadRequest, "is not one of its choices: even, odd");
            await RefusedAsync(
                client, token, $"/api/v1/reports/{reportId}/queries/numbers/run",
                new { values = new Dictionary<string, string?> { ["sql"] = "SELECT 1" } }, HttpStatusCode.BadRequest, "declares no parameter 'sql'");
            await RefusedAsync(client, token, $"/api/v1/reports/{reportId}/queries/nowhere/run", new { }, HttpStatusCode.NotFound, "has no query 'nowhere'");
            await RefusedAsync(client, token, $"/api/v1/reports/{Guid.NewGuid()}/queries/numbers/run", new { }, HttpStatusCode.NotFound, "No report has the id");
            await RefusedAsync(
                client, token, $"/api/v1/reports/{CatalogIdentity.Pipeline(repoId, Landing)}/queries/numbers/run", new { },
                HttpStatusCode.NotFound, "No report has the id"); // a flow is not a report
            using (var missing = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/reports/{Guid.NewGuid()}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            // The compute task endpoint takes no statement for a report, as it takes none for an approved query.
            await RefusedAsync(
                client, token, "/api/v1/datasources/tasks",
                new { reference = Reference, operation = "reportQuery", sql = "SELECT 1 AS One" }, HttpStatusCode.BadRequest, "runs a query a report declares");

            // ---- An assistant run can neither queue a report's query nor read the answer of one a person queued.
            using (var queued = await SendAsync(
                       client, token, HttpMethod.Post, $"/api/v1/reports/{reportId}/queries/tables/run", new { }, assistant: true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, queued.StatusCode);
                Assert.Contains(AssistantDelegationMiddleware.DeniedTitle, await queued.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var read = await SendAsync(
                       client, token, HttpMethod.Get, $"/api/v1/datasources/tasks/{tables.Accepted.TaskId}", assistant: true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
                var body = await read.Content.ReadAsStringAsync();
                Assert.Contains(AssistantDelegationMiddleware.DeniedTitle, body, StringComparison.Ordinal);
                Assert.DoesNotContain("\"rows\"", body, StringComparison.Ordinal);
            }

            // The report itself is a document an assistant may read, as it reads every flow's.
            using (var document = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/reports/{reportId}", assistant: true))
            {
                Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            }

            // ---- Nothing queues a run of a report as a flow: not a trigger, and a schedule does not take one as a member.
            using (var trigger = await SendAsync(client, token, HttpMethod.Post, "/api/v1/runs", new RunTriggerRequest(repoId, Report)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, trigger.StatusCode);
                Assert.Contains("it is not a flow", await trigger.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var schedule = await SendAsync(
                       client, token, HttpMethod.Post, "/api/v1/schedules",
                       new CreateScheduleRequest(repoId, [Landing, Report], "0 1 * * *", null, null, true, Name: "rpt_sched_" + _suffix)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, schedule.StatusCode);
                Assert.Contains("it is not a flow", await schedule.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.Runs.AnyAsync(r => r.RepoId == repoId));
                Assert.False(await db.Schedules.AnyAsync(s => s.RepoId == repoId));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
            await using var db = CatalogDatabase.Create(cs);
            var objectKeys = await db.LineageEdges.Where(e => e.RepoId == repoId).Select(e => e.ObjectKey).Distinct().ToListAsync();
            await db.ComputeTasks.Where(t => t.SourceRef == Reference).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => objectKeys.Contains(o.Key)).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// A report never stands in a flow's place. A pipeline is identified by its name and the first document in path order
    /// becomes it, so a report sorted before a flow of its name would turn that flow into a report; it is left out, said
    /// so, and the flow stays the flow.
    /// </summary>
    [SkippableFact]
    public async Task AReport_ThatHasTheNameOfAFlow_OrOfAnEarlierReport_IsLeftOut_AndSaidSo()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = "rpt_clash_" + _suffix;
        var repoId = FlowIdentity.FromName(repo);

        try
        {
            WriteEstate();
            const string Clash = """
                flowType: rpt
                name: __NAME__
                connection: __REF__
                queries:
                  q:
                    sql: SELECT 1 AS One
                """;
            Write("_first/clash.yaml", Clash.Replace("__NAME__", Landing.ToUpperInvariant(), StringComparison.Ordinal));
            Write("zz/again.yaml", Clash.Replace("__NAME__", Report, StringComparison.Ordinal));

            await using var db = CatalogDatabase.Create(cs);
            var synced = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);

            Assert.Contains(synced.Warnings, w =>
                w.StartsWith("_first/clash.yaml: report", StringComparison.Ordinal)
                && w.Contains("has the name of the file flow in flows/10-land.flow.yaml", StringComparison.Ordinal));
            Assert.Contains(synced.Warnings, w =>
                w.StartsWith("zz/again.yaml: report", StringComparison.Ordinal)
                && w.Contains("has the name of the report in reports/numbers.yaml", StringComparison.Ordinal));
            Assert.DoesNotContain(synced.Warnings, w => w.Contains("their facts merge", StringComparison.Ordinal));

            var rows = await db.Pipelines.AsNoTracking().Where(p => p.RepoId == repoId).OrderBy(p => p.Kind).ToListAsync();
            Assert.Equal(["file", "rpt"], rows.Select(p => p.Kind));
            Assert.Equal("flows/10-land.flow.yaml", rows[0].RelativePath);
            Assert.Equal("reports/numbers.yaml", rows[1].RelativePath);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var objectKeys = await db.LineageEdges.Where(e => e.RepoId == repoId).Select(e => e.ObjectKey).Distinct().ToListAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => objectKeys.Contains(o.Key)).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>The answer of one query: what the run was acknowledged with, and the rows the worker read.</summary>
    private sealed record Answer(
        ReportRunAccepted Accepted, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated);

    /// <summary>Queues one query of the report and waits for the worker to answer it.</summary>
    private static async Task<Answer> RunAsync(HttpClient client, string token, Guid reportId, string query, object body)
    {
        ReportRunAccepted accepted;
        using (var response = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/reports/{reportId}/queries/{query}/run", body))
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"run of '{query}' answered {(int)response.StatusCode}: {text}");
            accepted = JsonSerializer.Deserialize<ReportRunAccepted>(text, Web)!;
            Assert.Equal($"/api/v1/datasources/tasks/{accepted.TaskId}", response.Headers.Location?.ToString());
        }

        // Long-poll the task to its terminal state (bounded so a stuck worker fails rather than hangs).
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var detail = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/datasources/tasks/{accepted.TaskId}?waitMs=2000");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var body2 = await detail.Content.ReadAsStringAsync();
            using var task = JsonDocument.Parse(body2);
            var status = task.RootElement.GetProperty("status").GetString();
            if (status is "queued" or "running")
            {
                continue;
            }

            Assert.True(status == "succeeded", $"the query '{query}' ended '{status}': {body2}");
            var result = task.RootElement.GetProperty("result");
            return new Answer(
                accepted,
                [.. result.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)],
                [.. result.GetProperty("rows").EnumerateArray()
                    .Select(r => (IReadOnlyList<string?>)[.. r.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? null : c.GetString())])],
                result.GetProperty("truncated").GetBoolean());
        }

        throw new Xunit.Sdk.XunitException($"the query '{query}' did not finish.");
    }

    private static async Task RefusedAsync(
        HttpClient client, string token, string relativeUri, object body, HttpStatusCode expected, string saying)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, relativeUri, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"POST {relativeUri} answered {(int)response.StatusCode}, not {(int)expected}: {text}");
        Assert.Contains(saying, text, StringComparison.Ordinal);
    }

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
        HttpClient client, string token, HttpMethod method, string relativeUri, object? body = null, bool assistant = false)
    {
        var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (assistant)
        {
            request.Headers.Add(AssistantScope.HeaderName, AssistantScope.AssistantValue);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await client.SendAsync(request);
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
