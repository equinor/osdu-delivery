using System.Net;
using System.Text;
using System.Text.Json;
using SqlFlow.Delivery.Setup;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The tour over a scripted control plane: the order it fires in, that the assertions wait for the index, and above all what it
/// will not call a pass. A run that reports passed with every test skipped once looked exactly like a success, so a test not
/// evaluated counts against the verdict.
/// </summary>
public sealed class DevTourTests
{
    private const string User = "admin";
    private const string Password = "the-demo-password";

    /// <summary>A control plane answering the tour's calls, recording what it was asked to fire.</summary>
    private sealed class FakeControlPlane : HttpMessageHandler
    {
        public int NotUpFor { get; set; }

        public HttpStatusCode SignIn { get; set; } = HttpStatusCode.OK;

        public string SyncedSha { get; set; } = "0123456789abcdef";

        public string SyncError { get; set; } = string.Empty;

        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, HttpStatusCode> Refused { get; } = new(StringComparer.Ordinal);

        /// <summary>How many times the stand-in refuses before it answers.</summary>
        public int StandInRefusals { get; set; }

        /// <summary>What the Assertions page counts on each read, before <see cref="Totals"/>.</summary>
        public Queue<string> TotalsInTurn { get; } = new();

        /// <summary>What the lineage projects route answers.</summary>
        public HttpStatusCode LineageProjects { get; set; } = HttpStatusCode.OK;

        /// <summary>The project the lineage holds that the tour would point at.</summary>
        public string SalesProjectName { get; set; } = "sales";

        /// <summary>Schedules that fire one run rather than a group.</summary>
        public HashSet<string> Single { get; } = new(StringComparer.Ordinal);

        /// <summary>Schedules with a member that fails, and how.</summary>
        public Dictionary<string, string> Failing { get; } = new(StringComparer.Ordinal);

        /// <summary>Schedules whose failure leaves runs skipped behind it.</summary>
        public HashSet<string> Skipping { get; } = new(StringComparer.Ordinal);

        /// <summary>Groups that never finish.</summary>
        public HashSet<string> Stuck { get; } = new(StringComparer.Ordinal);

        public string Totals { get; set; } = """{ "tests": 21, "passed": 21, "failed": 0, "errored": 0, "notRun": 0 }""";

        public List<string> Fired { get; } = [];

        public bool ThrowsAfterSignIn { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/health")
            {
                Assert.Equal("127.0.0.1:5301", request.RequestUri.Authority);
                if (StandInRefusals > 0)
                {
                    StandInRefusals--;
                    throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
                }

                return Answer(HttpStatusCode.OK, """{ "status": "up" }""");
            }

            if (path == "/health/startup")
            {
                if (NotUpFor-- > 0)
                {
                    throw new HttpRequestException("connection refused");
                }

                return Answer(HttpStatusCode.OK, "{}");
            }

            if (path == "/api/v1/auth/login")
            {
                return Answer(SignIn, """{ "accessToken": "token" }""");
            }

            if (ThrowsAfterSignIn)
            {
                throw new HttpRequestException("the connection was closed");
            }

            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            if (path == "/api/v1/repos/sources")
            {
                var sources = DevTour.Repositories.Select(r => new { name = r, lastSyncedSha = SyncedSha, lastError = SyncError });
                return Answer(HttpStatusCode.OK, JsonSerializer.Serialize(sources));
            }

            if (path == "/api/v1/schedules")
            {
                var schedules = DevTour.Delivering.Concat(DevTour.Asserting).Select(s => s.Schedule).Distinct(StringComparer.Ordinal)
                    .Where(s => !Missing.Contains(s)).Select(s => new { name = s, id = s });
                return Answer(HttpStatusCode.OK, JsonSerializer.Serialize(schedules));
            }

            if (path.StartsWith("/api/v1/schedules/", StringComparison.Ordinal) && path.EndsWith("/run?chain=false", StringComparison.Ordinal))
            {
                var name = path.Split('/')[4];
                Fired.Add(name);
                return Refused.TryGetValue(name, out var refused)
                    ? Answer(refused, "no member is runnable")
                    : Answer(HttpStatusCode.Accepted, Single.Contains(name) ? $$"""{ "runId": "{{name}}" }""" : $$"""{ "runId": "r", "groupId": "{{name}}" }""");
            }

            if (path == "/api/v1/lineage/projects")
            {
                return Answer(LineageProjects, $$"""[{ "project": "formats", "repoId": "other" }, { "project": "{{SalesProjectName}}", "repoId": "sales-repo" }]""");
            }

            if (path == "/api/v1/delivery/assertions")
            {
                return Answer(HttpStatusCode.OK, $$"""{ "totals": {{(TotalsInTurn.TryDequeue(out var next) ? next : Totals)}} }""");
            }

            if (path.StartsWith("/api/v1/runs/groups/", StringComparison.Ordinal))
            {
                var name = path.Split('/')[5];
                var failed = Failing.ContainsKey(name) ? 1 : 0;
                var running = Stuck.Contains(name) ? 1 : 0;
                return Answer(HttpStatusCode.OK, $$"""{ "counts": { "total": 4, "queued": 0, "running": {{running}}, "succeeded": {{4 - failed - running}}, "failed": {{failed}} } }""");
            }

            if (path.StartsWith("/api/v1/runs?groupId=", StringComparison.Ordinal))
            {
                var name = path["/api/v1/runs?groupId=".Length..].Split('&')[0];
                var run = new { flowName = "the-flow-that-failed", status = "failed", error = Failing[name] };
                var fine = new { flowName = "a-flow", status = "succeeded", error = (string?)null };
                var skipped = new { flowName = "a-skipped-flow", status = "skipped", error = "skipped: an upstream dependency did not succeed." };
                object[] items = Skipping.Contains(name) ? [fine, run, skipped, skipped, skipped] : [fine, run];
                return Answer(HttpStatusCode.OK, JsonSerializer.Serialize(new { items }));
            }

            if (path.StartsWith("/api/v1/runs/", StringComparison.Ordinal))
            {
                var name = path.Split('/')[4];
                return Answer(HttpStatusCode.OK, Failing.TryGetValue(name, out var error)
                    ? JsonSerializer.Serialize(new { flowName = name, status = "failed", error })
                    : """{ "status": "succeeded" }""");
            }

            return Answer(HttpStatusCode.NotFound, "no route " + path);
        }

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status, string body)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Rig
    {
        public FakeControlPlane Platform { get; } = new();

        public ScriptedPrompter Prompter { get; } = new();

        public List<TimeSpan> Waits { get; } = [];

        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public async Task<bool> RunAsync()
        {
            using var http = new HttpClient(Platform) { BaseAddress = new Uri("http://localhost:5000") };
            var tour = new DevTour(
                http,
                Prompter,
                () => Now,
                (time, _) =>
                {
                    Waits.Add(time);
                    Now += time;
                    return Task.CompletedTask;
                },
                "http://127.0.0.1:5301");
            return await tour.RunAsync(User, Password, "http://localhost:5173", CancellationToken.None);
        }
    }

    [Fact]
    public async Task It_fires_every_schedule_in_order_each_holding_back_what_is_chained_behind_it()
    {
        var rig = new Rig();

        Assert.True(await rig.RunAsync());

        Assert.Equal(DevTour.Delivering.Concat(DevTour.Asserting).Select(s => s.Schedule), rig.Platform.Fired);
    }

    [Fact]
    public async Task A_stand_in_that_does_not_answer_ends_the_tour_before_anything_runs()
    {
        var rig = new Rig();
        rig.Platform.StandInRefusals = int.MaxValue;

        Assert.False(await rig.RunAsync());

        Assert.Empty(rig.Platform.Fired);
        Assert.Contains("The stand-in for OSDU does not answer at http://127.0.0.1:5301: No connection", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("dev.bat demo (./dev.sh demo) starts it again", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stand_in_still_starting_is_given_a_moment()
    {
        var rig = new Rig();
        rig.Platform.StandInRefusals = 2;

        Assert.True(await rig.RunAsync());

        Assert.DoesNotContain("does not answer", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assertions_left_unjudged_by_a_late_write_get_the_index_its_time_and_run_again()
    {
        var rig = new Rig();
        rig.Platform.TotalsInTurn.Enqueue("""{ "tests": 21, "passed": 2, "failed": 0, "errored": 0, "notRun": 19 }""");

        Assert.True(await rig.RunAsync());

        Assert.Equal([DevTour.IndexSettle, DevTour.IndexSettle], rig.Waits.Where(w => w == DevTour.IndexSettle));
        var asserting = DevTour.Asserting.Select(s => s.Schedule).ToList();
        Assert.Equal(DevTour.Delivering.Select(s => s.Schedule).Concat(asserting).Concat(asserting), rig.Platform.Fired);
        Assert.Contains("19 of 21 assertions were not judged", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("21 of 21 assertions passed", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_assertion_that_failed_is_not_run_again_in_the_hope_of_a_pass()
    {
        var rig = new Rig();
        rig.Platform.Totals = """{ "tests": 21, "passed": 18, "failed": 1, "errored": 0, "notRun": 2 }""";

        Assert.False(await rig.RunAsync());

        Assert.Equal([DevTour.IndexSettle], rig.Waits.Where(w => w == DevTour.IndexSettle));
        Assert.Contains("1 failed", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "elsewhere")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "sales")]
    public async Task A_lineage_it_cannot_name_leaves_the_bare_page_and_the_tour_green(HttpStatusCode answer, string project)
    {
        var rig = new Rig();
        rig.Platform.LineageProjects = answer;
        rig.Platform.SalesProjectName = project;

        Assert.True(await rig.RunAsync());

        Assert.Contains("from source file to KPI: http://localhost:5173/lineage\n", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_SQLFlow_estate_runs_before_the_OSDU_estate_and_its_chain_stays_in_order()
    {
        var names = DevTour.Delivering.Select(s => s.Schedule).ToList();

        Assert.Equal(["tour-load", "tour-facts", "tour-rollup", "tour-kpi", "tour-publish", "tour-proof"], names.Take(6));
        Assert.True(names.IndexOf("welldb-reference") < names.IndexOf("welldb-master"));
        Assert.True(names.IndexOf("welldb-master") < names.IndexOf("welldb-welllog"));
        Assert.True(names.IndexOf("welldb-welllog") < names.IndexOf("welldb-dimensions"));
        Assert.True(names.IndexOf("welldb-master") < names.IndexOf("welldb-removal"));
        Assert.True(names.IndexOf("welldb-welllog") < names.IndexOf("welldb-welllog-custom-kind"));

        // Each log names its record of the custom kind only once that exists, and a write after the index's wait would leave the
        // logs' assertions unjudged, so that delivery comes before it.
        Assert.True(names.IndexOf("welldb-welllog-custom-kind") < names.LastIndexOf("welldb-welllog"));
        Assert.Equal("welldb-inventory", names[^1]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_assertions_wait_out_the_index_after_the_last_delivery()
    {
        var rig = new Rig();

        await rig.RunAsync();

        Assert.Contains(DevTour.IndexSettle, rig.Waits);
        Assert.True(DevTour.IndexSettle >= TimeSpan.FromSeconds(300), "the estates' flows give the index 300 seconds");
        Assert.Contains("The search index gets 310 seconds", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pass_ends_with_where_to_go_and_how_to_sign_in()
    {
        var rig = new Rig();

        await rig.RunAsync();

        Assert.Contains("Everything held", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("21 of 21 assertions", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains($"Open http://localhost:5173 and sign in as {User}, password {Password}", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5173/lineage?repoId=sales-repo&project=sales", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sign_in_is_given_before_the_runs_start_so_they_can_be_watched()
    {
        var rig = new Rig();

        await rig.RunAsync();

        var signIn = rig.Prompter.Said.FindIndex(l => l.StartsWith("Watch it run", StringComparison.Ordinal));
        var firstRun = rig.Prompter.Said.FindIndex(l => l.Contains(" runs,", StringComparison.Ordinal));
        Assert.InRange(signIn, 0, firstRun - 1);
    }

    [Theory]
    [InlineData("""{ "tests": 21, "passed": 0, "failed": 0, "errored": 0, "notRun": 21 }""", "21 were not run")]
    [InlineData("""{ "tests": 21, "passed": 5, "failed": 0, "errored": 0, "notRun": 16 }""", "16 were not run")]
    [InlineData("""{ "tests": 21, "passed": 20, "failed": 1, "errored": 0, "notRun": 0 }""", "1 failed")]
    [InlineData("""{ "tests": 21, "passed": 20, "failed": 0, "errored": 1, "notRun": 0 }""", "1 could not run")]
    [InlineData("""{ "tests": 0, "passed": 0, "failed": 0, "errored": 0, "notRun": 0 }""", "of 0 assertions")]
    public async Task Assertions_that_were_not_evaluated_and_passed_are_not_a_pass(string totals, string says)
    {
        var rig = new Rig();
        rig.Platform.Totals = totals;

        Assert.False(await rig.RunAsync());

        Assert.Contains(says, rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("Everything held", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_run_is_named_with_its_error_and_the_rest_of_the_load_still_runs()
    {
        var rig = new Rig();
        rig.Platform.Failing["tour-proof"] = "1 of 109 checks failed: sales: the KPI counts the orders (expected 1417, got 1400)";

        Assert.False(await rig.RunAsync());

        Assert.Contains("the-flow-that-failed: 1 of 109 checks failed", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("a-flow", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Equal(DevTour.Delivering.Select(s => s.Schedule), rig.Platform.Fired);
        Assert.DoesNotContain("Everything held", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_load_does_not_wait_out_the_index_for_assertions_that_could_only_fail()
    {
        var rig = new Rig();
        rig.Platform.Failing["welldb-cache"] = "There is already an object named 'silver' in the database.";

        Assert.False(await rig.RunAsync());

        Assert.DoesNotContain(DevTour.IndexSettle, rig.Waits);
        Assert.DoesNotContain("welldb-welllog-assertions", rig.Platform.Fired);
        Assert.Contains("Runs page", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_is_one_short_line_without_a_nodes_cache_folders()
    {
        var rig = new Rig();
        rig.Platform.Failing["welldb-well"] = "unused";
        rig.Platform.Failing["welldb-master"] = """
            C:\Users\someone\AppData\Local\Temp\sqlflow\node-cache\a2bcfd64aa5515be\e165738a\welldb\flows\welldb-well-03-delivery.yaml: preflight failed with 1 error(s):
              - C:\Users\someone\AppData\Local\Temp\sqlflow\node-cache\a2bcfd64aa5515be\e165738a\welldb\mappings\Well@1.2.0.yaml: replace reads $cache.DepthUnitAlias
            """;

        await rig.RunAsync();

        Assert.Contains("the-flow-that-failed: welldb-well-03-delivery.yaml: preflight failed with 1 error(s):", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("node-cache", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("DepthUnitAlias", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_very_long_error_is_cut()
    {
        var rig = new Rig();
        rig.Platform.Failing["tour-load"] = new string('x', 900);

        await rig.RunAsync();

        Assert.All(rig.Prompter.Said, line => Assert.True(line.Length < 320, line));
    }

    [Fact]
    public async Task Runs_skipped_behind_a_failure_are_a_count_not_a_list()
    {
        var rig = new Rig();
        rig.Platform.Failing["welldb-master"] = "it failed";
        rig.Platform.Skipping.Add("welldb-master");

        await rig.RunAsync();

        Assert.Contains("3 runs behind it skipped", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("a-skipped-flow", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_schedule_of_one_run_is_followed_as_that_run()
    {
        var rig = new Rig();
        rig.Platform.Single.Add("tour-facts");
        rig.Platform.Single.Add("tour-kpi");
        rig.Platform.Failing["tour-kpi"] = "Invalid object name 'demo.Fact_CountryRollup'.";

        Assert.False(await rig.RunAsync());

        Assert.Contains("SQLFlow: the fact table: 1 run,", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("tour-kpi: Invalid object name", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_a_run_reports_is_redacted_before_it_is_shown()
    {
        var rig = new Rig();
        rig.Platform.Failing["tour-load"] = "Server=db;Password=hunter2;";

        await rig.RunAsync();

        Assert.DoesNotContain("hunter2", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_schedule_that_will_not_start_is_said_and_the_tour_goes_on()
    {
        var rig = new Rig();
        rig.Platform.Refused["welldb-cache"] = HttpStatusCode.Conflict;

        Assert.False(await rig.RunAsync());

        Assert.Contains("it did not start (HTTP 409)", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Contains("welldb-reference", rig.Platform.Fired);
    }

    [Fact]
    public async Task A_schedule_that_never_finishes_is_given_up_on_rather_than_waited_for_forever()
    {
        var rig = new Rig();
        rig.Platform.Stuck.Add("tour-load");

        Assert.False(await rig.RunAsync());

        Assert.Contains("not finished within 15 minutes", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_waits_for_a_control_plane_a_first_start_is_still_building()
    {
        var rig = new Rig();
        rig.Platform.NotUpFor = 90;

        Assert.True(await rig.RunAsync());

        Assert.Contains("Still waiting", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_control_plane_that_never_comes_up_is_given_up_on_with_where_to_look()
    {
        var rig = new Rig();
        rig.Platform.NotUpFor = int.MaxValue;

        Assert.False(await rig.RunAsync());

        Assert.Contains("did not come up within 30 minutes", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Fired);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_sign_in_says_how_to_put_it_right(HttpStatusCode refused)
    {
        var rig = new Rig();
        rig.Platform.SignIn = refused;

        Assert.False(await rig.RunAsync());

        Assert.Contains("demo reset", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estates_that_cannot_sync_say_why()
    {
        var rig = new Rig();
        rig.Platform.SyncedSha = string.Empty;
        rig.Platform.SyncError = "could not resolve host: github.com";

        Assert.False(await rig.RunAsync());

        Assert.Contains("could not resolve host: github.com", rig.Prompter.Transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_estate_missing_a_schedule_names_it()
    {
        var rig = new Rig();
        rig.Platform.Missing.Add("tour-proof");

        Assert.False(await rig.RunAsync());

        Assert.Contains("no schedule named tour-proof", rig.Prompter.Transcript, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Fired);
    }

    [Fact]
    public async Task The_command_says_a_control_plane_that_went_away_rather_than_printing_a_stack()
    {
        using var scope = new DevCheckoutScope();
        scope.WriteEnv("ControlPlane__Bootstrap__AdminUsername=admin", "SQLFLOW_ADMIN_PASSWORD=x");
        var prompter = new ScriptedPrompter();
        using var error = new StringWriter();
        using var output = new StringWriter();

        // Nothing listens on this port, and the tour's wait is real: it has to be cancelled rather than waited out.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevCommand.RunAsync(
            ["tour", "--url", "http://127.0.0.1:9"], "osdu-delivery-setup", scope.Machine(prompter), output, error, scope.Root, stop.Token));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task A_tour_with_nothing_to_sign_in_with_says_to_run_the_demo()
    {
        using var scope = new DevCheckoutScope();
        using var error = new StringWriter();
        using var output = new StringWriter();

        var exit = await DevCommand.RunAsync(["tour"], "osdu-delivery-setup", scope.Machine(new ScriptedPrompter()), output, error, scope.Root, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("run the demo first", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tour_refuses_an_address_that_is_not_one()
    {
        using var scope = new DevCheckoutScope();
        scope.WriteEnv("ControlPlane__Bootstrap__AdminUsername=admin", "SQLFLOW_ADMIN_PASSWORD=x");
        using var error = new StringWriter();
        using var output = new StringWriter();

        var exit = await DevCommand.RunAsync(["tour", "--url", "not a url"], "osdu-delivery-setup", scope.Machine(new ScriptedPrompter()), output, error, scope.Root, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("is not a URL", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "demo", "--json" }, "--json applies to 'check' alone")]
    [InlineData(new[] { "check", "--reset" }, "reset applies to 'demo' alone")]
    [InlineData(new[] { "tour", "--url" }, "--url needs the control plane's address")]
    public async Task A_command_it_cannot_run_is_a_usage_error(string[] args, string says)
    {
        using var scope = new DevCheckoutScope();
        using var error = new StringWriter();
        using var output = new StringWriter();

        var exit = await DevCommand.RunAsync(args, "osdu-delivery-setup", scope.Machine(new ScriptedPrompter()), output, error, scope.Root, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains(says, error.ToString(), StringComparison.Ordinal);
    }
}
