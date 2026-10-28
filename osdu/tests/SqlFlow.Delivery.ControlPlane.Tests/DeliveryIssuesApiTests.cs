using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A flow's issues as the API serves them (osdu/docs/reference/concepts/record-lifecycle.md, Issues;
/// osdu/docs/reference/concepts/api.md): the blocked records grouped by the issue their errors share, one issue with
/// the files its records came from, the records listing narrowed to an issue, the release of an issue's records with
/// each record naming the release in its history, and one record released and tried at once.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryIssuesApiTests
{
    /// <summary>The hold of a record whose wellbore the cache does not hold: one issue, whatever the wellbore.</summary>
    private static string MissingWellbore(string wellbore) => $"osdu.data.WellboreID: '{wellbore}' matches no cached Wellbore, and the entry is required";

    private const string EmptyTag = "osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required";

    [Fact]
    public async Task A_flow_s_issues_are_listed_opened_narrowed_to_and_released_together()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (repoId, pipelineId, flowId, flowName) = await SeedFlowAsync(cs, suffix);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var wellbores = Enumerable.Range(1, 3)
            .Select(i => Held(flowId, $"W-{suffix}-{i}", MissingWellbore($"WB-{i}"), i < 3 ? "wells.csv" : "late.csv"))
            .ToList();
        var tag = Held(flowId, $"T-{suffix}", EmptyTag, "wells.csv");
        await ledger.MarkHeldAsync(flowId, [.. wellbores, tag]);
        var missing = ProblemSignature.Format(ProblemSignature.Of(wellbores[0].LastError));

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The issues, the most records first, each with its pattern, counts and example.
            var listing = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/issues");
            Assert.Equal((2L, 4L, 0L), (listing.GetProperty("totalIssues").GetInt64(), listing.GetProperty("totalRecords").GetInt64(), listing.GetProperty("unsorted").GetInt64()));
            var first = listing.GetProperty("issues")[0];
            Assert.Equal(missing, first.GetProperty("issue").GetString());
            Assert.Equal("osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required", first.GetProperty("pattern").GetString());
            Assert.Equal((3L, 3L, 0L), (first.GetProperty("records").GetInt64(), first.GetProperty("held").GetInt64(), first.GetProperty("failed").GetInt64()));
            // The wellbores the cache lacks name each its own row's value: row errors. The empty tag is the same everywhere.
            Assert.Equal("rows", first.GetProperty("shape").GetString());
            Assert.Equal("set", listing.GetProperty("issues")[1].GetProperty("shape").GetString());
            // What the records share: a placeholder where each row names its own value, the error itself where all say the same.
            Assert.Equal(first.GetProperty("pattern").GetString(), first.GetProperty("shared").GetString());
            Assert.Equal(EmptyTag, listing.GetProperty("issues")[1].GetProperty("shared").GetString());
            var example = first.GetProperty("example");
            Assert.Equal(missing, example.GetProperty("issue").GetString());
            Assert.Contains(example.GetProperty("deliveryKey").GetGuid(), wellbores.Select(w => w.DeliveryKey.Value));

            // One issue, with the files its records came from.
            var detail = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/issues/{missing}");
            Assert.Equal(
                [("wells.csv", 2L), ("late.csv", 1L)],
                detail.GetProperty("files").EnumerateArray().Select(f => (f.GetProperty("fileName").GetString(), f.GetProperty("records").GetInt64())).ToList());

            // Samples spread across it, each with the value its row names, which differ: row errors.
            var samples = detail.GetProperty("samples").EnumerateArray().ToList();
            Assert.Equal(3, samples.Count);
            Assert.Equal(
                ["WB-1", "WB-2", "WB-3"],
                samples.Select(x => Assert.Single(x.GetProperty("values").EnumerateArray().ToList()).GetString()).Order(StringComparer.Ordinal).ToList());
            Assert.Equal("rows", detail.GetProperty("issue").GetProperty("shape").GetString());
            Assert.Equal("osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required", detail.GetProperty("issue").GetProperty("shared").GetString());
            await StatusAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/issues/not-a-issue", HttpStatusCode.BadRequest);
            await StatusAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/issues/{new string('0', 16)}", HttpStatusCode.NotFound);

            // The records listing narrowed to the issue lists its records alone; an issue it cannot read is refused.
            var records = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?issue={missing}&pageSize=50");
            Assert.Equal(3, records.GetProperty("total").GetInt64());
            Assert.All(records.GetProperty("items").EnumerateArray(), r => Assert.Equal(missing, r.GetProperty("issue").GetString()));
            await StatusAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?issue=xyz", HttpStatusCode.BadRequest);

            // Released together: every record of the issue, the other issue's left blocked.
            var released = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/issues/{missing}/release", "{}");
            Assert.Equal((missing, 3, JsonValueKind.Null), (released.GetProperty("issue").GetString(), released.GetProperty("released").GetInt32(), released.GetProperty("runId").ValueKind));
            var after = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/issues");
            Assert.Equal((1L, 1L), (after.GetProperty("totalIssues").GetInt64(), after.GetProperty("totalRecords").GetInt64()));
            await StatusAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/issues/{missing}/release", HttpStatusCode.NotFound, "{}");

            // Each record of it names the release in its own history: who asked, for which issue.
            foreach (var wellbore in wellbores)
            {
                var activities = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{wellbore.DeliveryKey.Value:D}/activities");
                var release = Assert.Single(activities.EnumerateArray().ToList());
                Assert.Equal(("release", JsonValueKind.Null), (release.GetProperty("kind").GetString(), release.GetProperty("deliveryKey").ValueKind));
                Assert.Contains(missing, release.GetProperty("parametersJson").GetString(), StringComparison.Ordinal);
                Assert.StartsWith("released 3 record(s)", release.GetProperty("summary").GetString(), StringComparison.Ordinal);
            }

            // One record released and tried at once: the release here, and a deliver run scoped to it queued.
            var tried = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{flowId:D}/{tag.DeliveryKey.Value:D}/release", """{"run":true}""", HttpStatusCode.Accepted);
            Assert.Equal(1, tried.GetProperty("released").GetInt32());
            var runId = tried.GetProperty("runId").GetGuid();
            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal(pipelineId, run.PipelineId);
                Assert.Contains(tag.DeliveryKey.Value.ToString("D"), run.Payload, StringComparison.Ordinal);
            }

            Assert.Equal(0L, (await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/issues")).GetProperty("totalRecords").GetInt64());

            // Every blocked record of the flow released, with a deliver run queued to plan and send them all.
            await ledger.MarkHeldAsync(flowId, [tag]);
            var everything = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/release", """{"run":true}""", HttpStatusCode.Accepted);
            Assert.Equal(1, everything.GetProperty("released").GetInt32());
            var flowRun = everything.GetProperty("runId").GetGuid();
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(pipelineId, (await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == flowRun)).PipelineId);
            }
        }
        finally
        {
            await CleanAsync(cs, repoId, flowId);
        }
    }

    [Fact]
    public async Task Records_held_for_an_id_osdu_holds_are_asked_to_adopt_in_batches_with_the_partition_typed_back()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (repoId, pipelineId, flowId, flowName) = await SeedFlowAsync(cs, suffix);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);

        // Five records whose ids, made from their keys, OSDU already holds records at that no ledger claimed: one issue.
        var namespaces = Enumerable.Range(1, 5)
            .Select(i => Occupied(flowId, $"N-{suffix}-{i}", $"{TestLedgers.Partition}:reference-data--ExternalCatalogNamespace:N-{suffix}-{i}", 40 + i))
            .ToList();
        var tag = Held(flowId, $"T-{suffix}", EmptyTag, "wells.csv");
        await ledger.MarkHeldAsync(flowId, [.. namespaces, tag]);
        var issue = ProblemSignature.Format(ProblemSignature.Of(namespaces[0].LastError));
        Assert.Equal(issue, ProblemSignature.Format(ProblemSignature.Of(namespaces[4].LastError)));

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The record says which id OSDU holds a record at, and whether an adoption may be asked of it; the flow counts them.
            var first = namespaces[0];
            var shown = (await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{first.DeliveryKey.Value:D}")).GetProperty("record");
            Assert.Equal((first.OccupiedTargetId, 41L, true), (shown.GetProperty("occupiedTargetId").GetString(), shown.GetProperty("occupiedTargetVersion").GetInt64(), shown.GetProperty("adoptable").GetBoolean()));
            Assert.False((await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{tag.DeliveryKey.Value:D}")).GetProperty("record").GetProperty("adoptable").GetBoolean());
            Assert.Equal(5, (await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/stats")).GetProperty("adoptable").GetInt64());

            // One record, asked; asking again, or asking a record held for something else, is refused saying why.
            var nothing = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{flowId:D}/{tag.DeliveryKey.Value:D}/adopt", "{}", HttpStatusCode.Conflict);
            Assert.Contains("is not held because OSDU already holds a record at the OSDU id its key makes", nothing.GetProperty("detail").GetString(), StringComparison.Ordinal);
            var one = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{flowId:D}/{first.DeliveryKey.Value:D}/adopt", "{}");
            Assert.Equal((1, 1, JsonValueKind.Null), (one.GetProperty("selected").GetInt32(), one.GetProperty("asked").GetInt32(), one.GetProperty("runId").ValueKind));
            var asked = await ledger.GetRecordAsync(flowId, first.DeliveryKey);
            Assert.Equal(((string?)null, false), (asked!.ClaimedTargetId, asked.Blocked));
            Assert.NotNull(asked.AdoptRequestedUtc);
            var again = await JsonAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{flowId:D}/{first.DeliveryKey.Value:D}/adopt", "{}", HttpStatusCode.Conflict);
            Assert.Contains("its adoption is asked already", again.GetProperty("detail").GetString(), StringComparison.Ordinal);

            // Records named in a batch, the partition typed back: the one held for something else is left.
            var flowPath = $"/api/v1/delivery/flows/{pipelineId:D}/adopt";
            var named = $$"""{"keys":["{{namespaces[1].DeliveryKey.Value:D}}","{{tag.DeliveryKey.Value:D}}"]""";
            await StatusAsync(client, token, HttpMethod.Post, flowPath, HttpStatusCode.BadRequest, named + "}");
            var wrong = await JsonAsync(client, token, HttpMethod.Post, flowPath, named + ""","confirm":"prod"}""", HttpStatusCode.BadRequest);
            Assert.Contains($"are kept in '{TestLedgers.Partition}'. Nothing was adopted.", wrong.GetProperty("detail").GetString(), StringComparison.Ordinal);
            var batch = await JsonAsync(client, token, HttpMethod.Post, flowPath, named + $$""","confirm":"{{TestLedgers.Partition}}"}""");
            Assert.Equal((2, 1), (batch.GetProperty("selected").GetInt32(), batch.GetProperty("asked").GetInt32()));

            // Every record of the flow held so, in one batch with a deliver run queued: the batch an operator runs after a run.
            var every = await JsonAsync(client, token, HttpMethod.Post, flowPath, $$"""{"confirm":"{{TestLedgers.Partition.ToUpperInvariant()}}","run":true}""", HttpStatusCode.Accepted);
            Assert.Equal((3, 3), (every.GetProperty("selected").GetInt32(), every.GetProperty("asked").GetInt32()));
            var runId = every.GetProperty("runId").GetGuid();
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(pipelineId, (await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId)).PipelineId);
            }

            foreach (var record in namespaces)
            {
                Assert.NotNull((await ledger.GetRecordAsync(flowId, record.DeliveryKey))!.AdoptRequestedUtc);
                var activities = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{record.DeliveryKey.Value:D}/activities");
                Assert.Equal("adopt", Assert.Single(activities.EnumerateArray().ToList()).GetProperty("kind").GetString());
            }

            // Nothing is left to adopt: the count is zero, the issue keeps no record, and the other record is still held.
            Assert.Equal(0, (await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/stats")).GetProperty("adoptable").GetInt64());
            await StatusAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/issues/{issue}/adopt", HttpStatusCode.NotFound, $$"""{"confirm":"{{TestLedgers.Partition}}"}""");
            Assert.True((await ledger.GetRecordAsync(flowId, tag.DeliveryKey))!.Blocked);
        }
        finally
        {
            await CleanAsync(cs, repoId, flowId);
        }
    }

    [Fact]
    public async Task An_issue_s_held_records_are_asked_to_adopt_together()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (repoId, pipelineId, flowId, flowName) = await SeedFlowAsync(cs, suffix);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var namespaces = Enumerable.Range(1, 2)
            .Select(i => Occupied(flowId, $"I-{suffix}-{i}", $"{TestLedgers.Partition}:reference-data--ExternalCatalogNamespace:I-{suffix}-{i}", 7))
            .ToList();
        await ledger.MarkHeldAsync(flowId, namespaces);
        var issue = ProblemSignature.Format(ProblemSignature.Of(namespaces[0].LastError));

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var path = $"/api/v1/delivery/flows/{pipelineId:D}/issues/{issue}/adopt";
            await StatusAsync(client, token, HttpMethod.Post, path, HttpStatusCode.BadRequest, "{}");
            var together = await JsonAsync(client, token, HttpMethod.Post, path, $$"""{"confirm":"{{TestLedgers.Partition}}"}""");
            Assert.Equal((issue, 2, JsonValueKind.Null), (together.GetProperty("issue").GetString(), together.GetProperty("asked").GetInt32(), together.GetProperty("runId").ValueKind));
            foreach (var record in namespaces)
            {
                var activities = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{record.DeliveryKey.Value:D}/activities");
                var adoption = Assert.Single(activities.EnumerateArray().ToList());
                Assert.Equal("adopt", adoption.GetProperty("kind").GetString());
                Assert.StartsWith("asked 2 record(s) issue ", adoption.GetProperty("summary").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            await CleanAsync(cs, repoId, flowId);
        }
    }

    /// <summary>
    /// A record held because OSDU already holds a record at <paramref name="targetId"/>, the id its key makes, at
    /// <paramref name="version"/>, as the intake holds it.
    /// </summary>
    private static RecordState Occupied(Guid flowId, string sourceKey, string targetId, long version) => Held(
        flowId,
        sourceKey,
        $"OSDU already holds a record at {targetId} at version {version.ToString(CultureInfo.InvariantCulture)}, the OSDU id made from the key, and no record of the ledger claimed it: "
            + "another system wrote it, or a ledger this database does not hold, so nothing is sent rather than writing a new version of a record no ledger owns",
        "namespaces.json") with
    {
        OccupiedTargetId = targetId,
        OccupiedTargetVersion = version,
    };

    /// <summary>A delivery flow in a repository of its own in the catalog, named with <paramref name="suffix"/>.</summary>
    private static async Task<(Guid RepoId, Guid PipelineId, Guid FlowId, string FlowName)> SeedFlowAsync(string cs, string suffix)
    {
        var flowName = "api-issues-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-issues-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/issues
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
              headers: { data-partition-id: dev }
            """;

        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId, Name = "cp-issues-" + suffix, RemoteUrl = "https://example/cp-issues.git",
            RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
        });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = pipelineId,
            RepoId = repoId,
            Name = flowName,
            Kind = "delivery",
            RelativePath = "flows/" + flowName + ".yaml",
            ContentHash = new string('0', 64),
            Yaml = yaml,
            DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flowName}}","flowKind":"delivery"}"""),
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
        return (repoId, pipelineId, FlowId.Of(flowName), flowName);
    }

    /// <summary>Removes what a test of this class left: the flow's ledger rows, and the repository's catalog rows.</summary>
    private static async Task CleanAsync(string cs, Guid repoId, Guid flowId)
    {
        await using (var osdu = SampleEstate.Context(cs))
        {
            await osdu.DeliveryActivityRecords.Where(l => l.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static RecordState Held(Guid flowId, string sourceKey, string error, string file) => new()
    {
        DeliveryKey = DeliveryKey.Derive("issues", [sourceKey]),
        FlowId = flowId,
        SourceKey = sourceKey,
        MappingName = "Wellbore",
        LastSubmissionId = Guid.NewGuid(),
        PendingSourceFileName = file,
        PendingSourceRowNumber = 1,
        LastError = error,
    };

    private static async Task<JsonElement> JsonAsync(
        HttpClient client, string token, HttpMethod method, string path, string? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(client, token, method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task StatusAsync(HttpClient client, string token, HttpMethod method, string path, HttpStatusCode expected, string? body = null)
    {
        using var response = await SendAsync(client, token, method, path, body);
        Assert.True(response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
