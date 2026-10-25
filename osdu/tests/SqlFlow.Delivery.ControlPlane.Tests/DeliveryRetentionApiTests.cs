using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Maintenance;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Maintenance;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The <c>osdu</c> schema's optional cleanups on the Maintenance page, through SQLFlow's maintenance API: the delivery
/// attempts a later attempt of the same record superseded, and the captured logs of finished activities. Traceability is the
/// product, so what they may take is exactly bounded: a try older than the cut-off goes only where a later try of the same
/// record explains the record's outcome, and an activity's log is cleared without the audit row it belongs to ever being
/// deleted. These tests hold the cleanups to that bound, because one that took a row too many would quietly cost a
/// delivered record its history.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryRetentionApiTests
{
    private const string FlowName = "wells-retention";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_page_reports_the_osdu_schema_and_a_cleanup_needs_the_admin_scope_and_a_day()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var operate = await TokenAsync(client, ["read", "operate"]);
        var admin = await TokenAsync(client, ["admin"]);

        // Every table of the osdu schema is described: none of it is listed as a table nobody describes.
        using (var response = await SendAsync(client, operate, HttpMethod.Get, "/api/v1/maintenance/storage"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var report = await response.Content.ReadFromJsonAsync<StorageReportDto>(Web);
            Assert.NotNull(report);
            Assert.DoesNotContain(report.Undescribed, t => string.Equals(t.Schema, DeliveryModel.SchemaName, StringComparison.OrdinalIgnoreCase));
            var attempts = report.Areas.Single(a => a.Id == OsduStorageContributor.AttemptsArea);
            Assert.Equal("optional", attempts.Verdict);
            Assert.Equal("OSDU Delivery", attempts.Owner);
            Assert.Equal("attempts", attempts.Retention!.Unit);
            Assert.Equal("keep", report.Areas.Single(a => a.Id == "osdu.records").Verdict);
            var logs = report.Areas.Single(a => a.Id == OsduStorageContributor.ActivityLogsArea);
            Assert.Equal("Log", logs.Column!.Column);
        }

        // Operating the estate is not administering it: a cleanup deletes history, so it sits behind admin.
        using (var refused = await SendAsync(client, operate, HttpMethod.Post, $"/api/v1/maintenance/areas/{OsduStorageContributor.AttemptsArea}/cleanup", new StorageDaysDto(90)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // A cut-off below a day is refused before anything is deleted, and an area to keep takes no cleanup at all.
        foreach (var days in new[] { 0, -1 })
        {
            using var response = await SendAsync(client, admin, HttpMethod.Post, $"/api/v1/maintenance/areas/{OsduStorageContributor.AttemptsArea}/cleanup", new StorageDaysDto(days));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var response = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/maintenance/areas/osdu.records/cleanup", new StorageDaysDto(30)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>
    /// One cleanup of each area over a seeded history: what it takes, what it keeps, and what it reports. The record's last
    /// try and every try inside the window survive; every audit row survives with its actor, parameters and outcome, and only
    /// the logs of the settled activities outside the window are gone.
    /// </summary>
    [Fact]
    public async Task The_cleanups_age_out_old_tries_and_run_logs_and_keep_what_a_record_is_reconstructible_from()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName + "-" + Guid.NewGuid().ToString("N"));
        var key = Guid.NewGuid();
        var lonely = Guid.NewGuid();
        // Whole seconds, so what is read back out of datetime2 is exactly what was written and the assertions are about
        // retention rather than about clock precision.
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var ancient = now.AddDays(-400);
        var old = now.AddDays(-200);
        var recent = now.AddDays(-2);
        var log = new string('x', 4_000);

        try
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                osdu.DeliveryAttempts.AddRange(
                    Attempt(flowId, key, ancient, AttemptOutcomes.Failed),
                    Attempt(flowId, key, old, AttemptOutcomes.Retry),
                    Attempt(flowId, key, recent, AttemptOutcomes.Delivered),
                    // A record whose only try is older than the cut-off: its last outcome has to stay explainable, so
                    // the cleanup leaves it, however old it is.
                    Attempt(flowId, lonely, ancient, AttemptOutcomes.Delivered));
                osdu.DeliveryActivities.AddRange(
                    Activity(flowId, "deliver", old, completed: true, log),
                    Activity(flowId, "delete", old, completed: true, log),
                    // Outside the window but never finished: a run still going keeps everything it holds.
                    Activity(flowId, "deliver", old, completed: false, log),
                    // Inside the window: the operator asked for the last 30 days to stay readable.
                    Activity(flowId, "deliver", recent, completed: true, log));
                await osdu.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var admin = await TokenAsync(client, ["admin"]);

            // The preview counts at least this flow's two superseded tries and two settled logs; the database is shared, so
            // other suites' rows may count too.
            Assert.True(await PreviewAsync(client, admin, OsduStorageContributor.AttemptsArea, 30) >= 2);
            Assert.True(await PreviewAsync(client, admin, OsduStorageContributor.ActivityLogsArea, 30) >= 2);

            var attemptsCleanup = await CleanUpAsync(client, admin, OsduStorageContributor.AttemptsArea, 30);
            var logsCleanup = await CleanUpAsync(client, admin, OsduStorageContributor.ActivityLogsArea, 30);
            Assert.Null(attemptsCleanup.Error);
            Assert.Null(logsCleanup.Error);
            Assert.True(attemptsCleanup.Removed >= 2, $"the attempts cleanup removed {attemptsCleanup.Removed}");
            Assert.True(logsCleanup.Removed >= 2, $"the logs cleanup cleared {logsCleanup.Removed}");
            Assert.Equal("manual", attemptsCleanup.Trigger);

            await using (var osdu = SampleEstate.Context(cs))
            {
                // Both tries outside the window went, because a later try of that record explains its outcome; the
                // record's own last try stayed, and so did the only try of the record that has one, however old it is.
                var attempts = await osdu.DeliveryAttempts.AsNoTracking()
                    .Where(a => a.FlowId == flowId)
                    .OrderBy(a => a.StartedUtc)
                    .ToListAsync();
                Assert.Equal([recent], attempts.Where(a => a.DeliveryKey == key).Select(a => a.StartedUtc).ToList());
                var kept = Assert.Single(attempts, a => a.DeliveryKey == lonely);
                Assert.Equal(ancient, kept.StartedUtc);

                // Every audit row is still there: the cleanup deletes none of them.
                var activities = await osdu.DeliveryActivities.AsNoTracking()
                    .Where(a => a.FlowId == flowId)
                    .ToListAsync();
                Assert.Equal(4, activities.Count);

                // The settled activities outside the window lost their log and nothing else.
                var settledOutside = activities.Where(a => a.StartedUtc == old && a.CompletedUtc != null).ToList();
                Assert.Equal(2, settledOutside.Count);
                Assert.All(settledOutside, a =>
                {
                    Assert.Null(a.Log);
                    Assert.Equal("retention tests", a.Actor);
                    Assert.Equal("""{"force":false}""", a.ParametersJson);
                    Assert.Equal("completed", a.Outcome);
                    Assert.Equal("what the run did", a.Summary);
                });

                // The one still running and the one inside the window kept theirs.
                var keptLogs = activities.Where(a => a.CompletedUtc is null || a.StartedUtc == recent).ToList();
                Assert.Equal(2, keptLogs.Count);
                Assert.All(keptLogs, a => Assert.Equal(log, a.Log));
            }

            // And it is idempotent: a second cleanup at the same cut-off finds nothing of this flow left to take.
            await CleanUpAsync(client, admin, OsduStorageContributor.AttemptsArea, 30);
            await CleanUpAsync(client, admin, OsduStorageContributor.ActivityLogsArea, 30);
            await using (var osdu = SampleEstate.Context(cs))
            {
                Assert.Equal(2, await osdu.DeliveryAttempts.CountAsync(a => a.FlowId == flowId));
                Assert.Equal(4, await osdu.DeliveryActivities.CountAsync(a => a.FlowId == flowId));
                Assert.Equal(2, await osdu.DeliveryActivities.CountAsync(a => a.FlowId == flowId && a.Log != null));
            }
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await using var catalog = CatalogDatabase.Create(cs);
            await catalog.MaintenanceCleanups
                .Where(c => c.AreaId == OsduStorageContributor.AttemptsArea || c.AreaId == OsduStorageContributor.ActivityLogsArea)
                .ExecuteDeleteAsync();
        }
    }

    /// <summary>The outcomes a try settles as, as the ledger writes them.</summary>
    private static class AttemptOutcomes
    {
        public const string Delivered = "delivered";

        public const string Retry = "retry";

        public const string Failed = "failed";
    }

    private static DeliveryAttempt Attempt(Guid flowId, Guid key, DateTime startedUtc, string outcome) => new()
    {
        FlowId = flowId,
        DeliveryKey = key,
        Worker = "retention-tests",
        StartedUtc = startedUtc,
        CompletedUtc = startedUtc.AddSeconds(2),
        Outcome = outcome,
        Phase = "metadata",
    };

    private static DeliveryActivity Activity(Guid flowId, string kind, DateTime startedUtc, bool completed, string log) => new()
    {
        FlowId = flowId,
        FlowName = FlowName,
        Kind = kind,
        Actor = "retention tests",
        StartedUtc = startedUtc,
        CompletedUtc = completed ? startedUtc.AddMinutes(1) : null,
        Outcome = completed ? "completed" : "running",
        ParametersJson = """{"force":false}""",
        Summary = completed ? "what the run did" : null,
        Log = log,
    };

    private static ControlPlaneAppFactory Factory(string catalog)
        => new ControlPlaneAppFactory()
            .WithCatalog(catalog)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            // The automatic cleanup is off, so the only cleanups are the ones a test asks for.
            .WithSetting("ControlPlane:Maintenance:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task<string> TokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<long> PreviewAsync(HttpClient client, string token, string areaId, int days)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/maintenance/areas/{areaId}/preview", new StorageDaysDto(days));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<CleanupPreviewDto>(Web);
        Assert.NotNull(preview);
        return preview.Count;
    }

    /// <summary>Asks for a cleanup and follows its entry in the area's history until it closes.</summary>
    private static async Task<CleanupDto> CleanUpAsync(HttpClient client, string token, string areaId, int days)
    {
        CleanupDto started;
        using (var response = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/maintenance/areas/{areaId}/cleanup", new StorageDaysDto(days)))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            started = (await response.Content.ReadFromJsonAsync<CleanupDto>(Web))!;
        }

        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            using var response = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/maintenance/areas/{areaId}/cleanups");
            var history = await response.Content.ReadFromJsonAsync<List<CleanupDto>>(Web);
            var entry = history!.Single(h => h.Id == started.Id);
            if (entry.CompletedUtc is not null)
            {
                return entry;
            }

            Assert.True(DateTime.UtcNow < deadline, $"The cleanup of {areaId} did not finish in two minutes.");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }
}
