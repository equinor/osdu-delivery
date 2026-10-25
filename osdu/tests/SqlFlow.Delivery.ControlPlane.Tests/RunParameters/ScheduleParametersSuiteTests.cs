using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a schedule gives each flow of the module it runs (GET /schedules/{id}/parameters, osdu/docs/reference/flow/parameters.md,
/// What a schedule gives each flow), over a schedule running a flow of every one of the module's kinds and a built-in one:
/// each member's values and what would refuse its run are what its kind's own resolution of the same document and values
/// says, a member whose kind lacks the schedule's operation runs its default with its document's values and says so, and
/// a fire enqueues each member with exactly the operation and values the preview showed.
/// </summary>
[Trait(RunParameterSuite.Trait, RunParameterSuite.Name)]
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class ScheduleParametersSuiteTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    /// <summary>Every member's document declares this, so a schedule's values reach a declared parameter of each.</summary>
    private const string Site = "site: { required: true, choices: [north, south] }";

    public static TheoryData<string?, string[]> Schedules() => new()
    {
        // The schedule's operation, then its values as name=value (a value of several written a|b).
        { null, ["site=north", "logSource=COMPOSITE"] },
        { null, ["site=west", "logSource=COMPOSITE"] },
        { null, ["logSource=COMPOSITE|WIRELINE"] },
        { null, ["site=north|south", "logSource=COMPOSITE", "partition=dev"] },
        { "plan", ["site=north", "logSource=COMPOSITE", "partition=dev"] },
        { "verify", ["partition=dev"] },
        { "test", [] },
        { null, [] },
    };

    [Theory]
    [MemberData(nameof(Schedules))]
    public async Task Each_member_is_given_what_its_kind_would_take_and_a_fire_enqueues_exactly_that(string? operation, string[] valuesWritten)
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("repo/schedule-suite-" + suffix);
        var values = valuesWritten.Select(v => v.Split('=', 2)).ToDictionary(
            v => v[0], v => v[1].Contains('|', StringComparison.Ordinal) ? RunValueList.Of(v[1].Split('|')) : v[1], StringComparer.Ordinal);
        var members = FlowKindDocument.All.Select(k => (Name: $"{k.FlowType}-{suffix}", k.FlowType, Yaml: k.Document(Site))).ToList();
        var builtIn = $"ing-{suffix}";

        try
        {
            Guid scheduleId;
            var now = DateTime.UtcNow;
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "schedule-suite-" + suffix, RemoteUrl = "https://git.invalid/schedule-suite.git",
                    RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
                });
                var wave = 0;
                foreach (var (name, kind, yaml) in members.Append((builtIn, "ing", "flowType: ing\n")))
                {
                    db.Pipelines.Add(new CatalogPipeline
                    {
                        Id = CatalogIdentity.Pipeline(repoId, name), RepoId = repoId, Name = name, Kind = kind, RelativePath = $"flows/{name}.yaml",
                        Yaml = yaml, Active = true, Wave = wave++, ExecutionMode = PipelineExecutionModes.Auto, FirstSeenUtc = now, LastSeenUtc = now,
                    });
                }

                await db.SaveChangesAsync();
                await ScheduleStore.StageYamlUpsertAsync(
                    db, repoId, "suite_" + suffix, [.. members.Select(m => m.Name), builtIn], "0 4 * * *", null, "UTC", enabled: true, catchup: false,
                    maxConcurrency: null, now.AddHours(1), now, operation: operation, values: values);
                await db.SaveChangesAsync();
                scheduleId = (await db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == repoId)).Id;
            }

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            using var preview = await GetJsonAsync(client, await TokenAsync(client), $"/api/v1/schedules/{scheduleId:D}/parameters");
            var shown = preview.RootElement.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("flowName").GetString()!, m => m.Clone());

            var documents = YamlDocumentLoader.CreateDefault(FlowKindDocument.All.Select(k => k.Kind(Loader)).ToList());
            var fireRefusals = new List<string>();
            foreach (var (name, flowType, yaml) in members)
            {
                var member = shown[name];
                var kind = documents.FindKind(flowType)!;
                var takes = operation is null || kind.Operations.Any(o => o.Name == operation);
                Assert.Equal(takes, member.GetProperty("takesScheduleArguments").GetBoolean());
                var runs = takes ? operation ?? kind.Operations[0].Name : kind.Operations[0].Name;
                Assert.Equal(runs, member.GetProperty("operation").GetString());
                var problems = member.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).ToList();
                if (!takes)
                {
                    Assert.Equal(
                        $"Its kind has no operation '{operation}': this flow runs its default operation ({runs}) with its document's own values.",
                        member.GetProperty("note").GetString());
                }

                // What the kind's own check of a fire's arguments refuses fails the whole fire, and the preview names it.
                if (takes && KindCheck(documents, flowType, operation, values) is { } refused)
                {
                    Assert.Contains(refused, problems);
                    fireRefusals.Add(refused);
                }

                // What the kind's own run would refuse, resolved as its executor resolves it, from the values it is given.
                var given = takes ? values : new Dictionary<string, string>(StringComparer.Ordinal);
                var inputs = kind.DescribeRun(yaml, $"flows/{name}.yaml");
                var refusal = KindRefusal(FlowKindDocument.Of(flowType), yaml, given, inputs, runs);
                var parameterProblems = problems.Where(p => p.StartsWith("parameter '", StringComparison.Ordinal)).ToList();
                if (refusal is null)
                {
                    Assert.Empty(parameterProblems);
                }
                else
                {
                    Assert.Equal(refusal, parameterProblems.FirstOrDefault());
                }
            }

            var plain = shown[builtIn];
            Assert.False(plain.GetProperty("takesScheduleArguments").GetBoolean());
            Assert.Empty(plain.GetProperty("values").EnumerateArray());

            // The fire queues what the preview showed: each member that takes the schedule's arguments with them, every other
            // with none; a refusal of the kind's own check of the arguments would have failed the whole fire, and none is here.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var schedule = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == scheduleId);
                if (fireRefusals.Count > 0)
                {
                    var failed = await Assert.ThrowsAnyAsync<SqlFlowException>(() => ScheduleFire.EnqueueAsync(db, new StoreDispatcher(), documents, schedule, now, default));
                    Assert.Contains(fireRefusals, refused => failed.Message.Contains(refused, StringComparison.Ordinal));
                    Assert.Empty(await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync());
                    return;
                }

                await ScheduleFire.EnqueueAsync(db, new StoreDispatcher(), documents, schedule, now, default);
                var queued = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
                foreach (var (name, _, _) in members)
                {
                    var run = Assert.Single(queued, r => r.FlowName == name);
                    var member = shown[name];
                    if (member.GetProperty("takesScheduleArguments").GetBoolean())
                    {
                        Assert.Equal(operation, run.Operation);
                        var taken = run.ValuesJson is null ? new Dictionary<string, string>(StringComparer.Ordinal) : RunParameters.ValuesFromJson(run.ValuesJson);
                        Assert.Equal(values.OrderBy(v => v.Key, StringComparer.Ordinal), taken.OrderBy(v => v.Key, StringComparer.Ordinal));
                    }
                    else
                    {
                        Assert.Null(run.Operation);
                        Assert.Null(run.ValuesJson);
                    }
                }

                Assert.Null(Assert.Single(queued, r => r.FlowName == builtIn).ValuesJson);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>What the kind refuses of a fire's operation and values without its document, as the fire checks them; null when nothing.</summary>
    private static string? KindCheck(YamlDocumentLoader documents, string flowType, string? operation, IReadOnlyDictionary<string, string> values)
    {
        try
        {
            documents.ValidateRunParameters(flowType, RunParameters.None with { Operation = operation, Values = values });
            return null;
        }
        catch (SqlFlowException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// What a run of <paramref name="operation"/> refuses of the values, without the document's prefix: the partition taken
    /// out first when the kind reads it, as its executor does, and nothing for an operation that resolves no parameters.
    /// </summary>
    private static string? KindRefusal(FlowKindDocument kind, string yaml, IReadOnlyDictionary<string, string> given, FlowRunInputs inputs, string operation)
    {
        if (!inputs.TakesParameters(operation))
        {
            return null;
        }

        var supplied = inputs.KindValues.Count > 0
            ? given.Where(v => v.Key != PartitionNames.RunValue).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal)
            : given;
        try
        {
            kind.Resolve(Loader, yaml, supplied);
            return null;
        }
        catch (FlowValidationException ex)
        {
            const string Prefix = "flows/suite.yaml: ";
            return ex.Message.StartsWith(Prefix, StringComparison.Ordinal) ? ex.Message[Prefix.Length..] : ex.Message;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    /// <summary>A dispatcher that enqueues through the real store, without the hosted worker's signalling.</summary>
    private sealed class StoreDispatcher : IRunDispatcher
    {
        public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
            => (await RunQueueStore.EnqueueAsync(catalog, request, DateTime.UtcNow, ct)).RunId;

        public Task<RunGroupEnqueueResult> EnqueueGroupAsync(CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
            => RunQueueStore.EnqueueGroupAsync(catalog, request, DateTime.UtcNow, ct);

        public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
