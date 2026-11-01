using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Setup;

/// <summary>One schedule of the tour: the estate it belongs to, its name, and what it does, as the tour says it.</summary>
internal sealed record TourStep(string Schedule, string Says);

/// <summary>
/// The local environment's proof (<c>tour</c>, osdu/docs/reference/cli/dev.md): it waits for a control plane to come up and for
/// the two sample estates to sync, fires their schedules in the order their records refer to each other, through the path a
/// person's Run now takes, and says whether every run succeeded, the SQLFlow estate's proof held and the OSDU estate's
/// assertions passed. An assertion that did not run is not a pass: the verdict counts what was evaluated.
/// </summary>
internal sealed partial class DevTour(HttpClient http, IPrompter prompter, Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> wait, string? standIn = null)
{
    /// <summary>The longest error the tour prints; the Runs page has the rest.</summary>
    private const int MostOfAnError = 200;

    /// <summary>How long a control plane may take to come up: a first start builds it.</summary>
    internal static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(30);

    /// <summary>How long the estates may take to sync, cloned for the first time.</summary>
    internal static readonly TimeSpan SyncLimit = TimeSpan.FromMinutes(5);

    /// <summary>How long one schedule may take.</summary>
    internal static readonly TimeSpan ScheduleLimit = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the assertions wait after the last delivery: their flows' <c>indexSettleSeconds</c>, which gives a real OSDU's
    /// search index the time it needs. They stand as a real estate writes them, so the tour waits as it would there.
    /// </summary>
    internal static readonly TimeSpan IndexSettle = TimeSpan.FromSeconds(310);

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    /// <summary>How many times the stand-in is asked, a poll apart: it starts just before the control plane does.</summary>
    private const int StandInAttempts = 5;

    /// <summary>How often a long wait for the control plane says it is still waiting.</summary>
    private static readonly TimeSpan StillWaiting = TimeSpan.FromMinutes(1);

    /// <summary>The project whose lineage the tour points at: files to an executive KPI, small enough to read.</summary>
    private const string SalesProject = "sales";

    /// <summary>The repositories the demo registers.</summary>
    internal static readonly IReadOnlyList<string> Repositories = ["osdu_estate", "sqlflow_estate"];

    /// <summary>The SQLFlow estate's chain, each link fired once the one before it has finished, and the OSDU estate's load.</summary>
    internal static readonly IReadOnlyList<TourStep> Delivering =
    [
        new("tour-load", "SQLFlow: files land, keyed tables load, every file format runs"),
        new("tour-facts", "SQLFlow: the fact table"),
        new("tour-rollup", "SQLFlow: the country rollup"),
        new("tour-kpi", "SQLFlow: the executive KPI"),
        new("tour-publish", "SQLFlow: exports, documents and the health check"),
        new("tour-proof", "SQLFlow: every expected value compared with the database"),
        new("welldb-cache", "OSDU: the lookup tables and the reference cache"),
        new("welldb-reference", "OSDU: reference data, into an empty partition"),
        new("welldb-master", "OSDU: wells, wellbores, surveys and formation tops"),
        new("welldb-removal", "OSDU: the wellbores set against the source's current cut, nothing to take out"),
        new("sqlflow-logtype", "OSDU: curated log types"),
        new("welldb-welllog", "OSDU: well logs, with their curves as bulk data"),
        new("welldb-welllog-custom-kind", "OSDU: the same logs as a kind of this estate's own"),
        new("welldb-welllog", "OSDU: the well logs again, each now naming its record of that kind"),
        new("welldb-dimensions", "OSDU: dimensions over what was delivered"),
        new("welldb-welllog-v150", "OSDU: the same logs as a newer schema version"),
        new("welldb-inventory", "OSDU: every id OSDU holds, set against the ledgers"),
    ];

    /// <summary>The assertions, once the index has settled: the proof values, and the flows that test what they deliver.</summary>
    internal static readonly IReadOnlyList<TourStep> Asserting =
    [
        new("welldb-master", "OSDU: the wells as OSDU holds them"),
        new("welldb-welllog", "OSDU: the well logs as OSDU holds them"),
        new("welldb-welllog-v150", "OSDU: the newer version as OSDU holds it"),
        new("welldb-welllog-assertions", "OSDU: every proof value, and every curve sample"),
    ];

    private int _runs;

    /// <summary>Runs the tour. True when everything held; false, having said what did not.</summary>
    public async Task<bool> RunAsync(string user, string password, string gui, CancellationToken ct)
    {
        prompter.Say(string.Empty);
        prompter.Say("OSDU Delivery: the tour", Tone.Heading);
        prompter.Say("Both sample estates run for real here: files to tables, tables to OSDU, and every value checked against its source.", Tone.Note);
        if (!await ComeUpAsync(ct).ConfigureAwait(false) || !await StandInAnswersAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        if (!await SignInAsync(user, password, ct).ConfigureAwait(false))
        {
            return false;
        }

        prompter.Say($"Watch it run: sign in at {gui} as {user}, password {password}", Tone.Heading);
        if (!await SyncedAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        var schedules = await SchedulesAsync(ct).ConfigureAwait(false);
        var missing = Delivering.Concat(Asserting).Select(s => s.Schedule).Distinct(StringComparer.Ordinal).Where(s => !schedules.ContainsKey(s)).ToList();
        if (missing.Count > 0)
        {
            prompter.Say($"The estates hold no schedule named {string.Join(", ", missing)}.", Tone.Bad);
            return false;
        }

        var started = now();
        var held = true;
        foreach (var step in Delivering)
        {
            held &= await FireAsync(step, schedules[step.Schedule], ct).ConfigureAwait(false);
        }

        if (!held)
        {
            // Assertions over a delivery that did not happen can only fail, so nobody waits five minutes to be told that.
            prompter.Say(string.Empty);
            prompter.Say("The tour did not hold: the lines above say what failed. Each run's whole trace is on the Runs page.", Tone.Bad);
            return false;
        }

        prompter.Say($"The search index gets {IndexSettle.TotalSeconds:0} seconds, as a real OSDU's does, before anything is asserted on what it lists ...", Tone.Note);
        await wait(IndexSettle, ct).ConfigureAwait(false);
        foreach (var step in Asserting)
        {
            held &= await FireAsync(step, schedules[step.Schedule], ct).ConfigureAwait(false);
        }

        var totals = await AssertionTotalsAsync(ct).ConfigureAwait(false);
        if (held && totals.OnlyNotJudged)
        {
            // Something wrote to what they read after the wait, such as a person pressing Redeliver. A test the index may not
            // have caught up with is skipped, which says nothing against the records, so the index gets its time again.
            prompter.Say($"{totals.NotRun} of {totals.Tests} assertions were not judged, because records they read changed moments ago. The index gets {IndexSettle.TotalSeconds:0} seconds more, then they run once again ...", Tone.Note);
            await wait(IndexSettle, ct).ConfigureAwait(false);
            foreach (var step in Asserting)
            {
                held &= await FireAsync(step, schedules[step.Schedule], ct).ConfigureAwait(false);
            }

            totals = await AssertionTotalsAsync(ct).ConfigureAwait(false);
        }

        if (!totals.Held)
        {
            prompter.Say($"OSDU: of {totals.Tests} assertions, {totals.Passed} passed, {totals.Failed} failed, {totals.Errored} could not run and {totals.NotRun} were not run.", Tone.Bad);
            held = false;
        }

        prompter.Say(string.Empty);
        if (!held)
        {
            prompter.Say("The tour did not hold: the lines above say what failed. Each run's whole trace is on the Runs page.", Tone.Bad);
            return false;
        }

        var took = now() - started;
        prompter.Say(string.Create(CultureInfo.InvariantCulture, $"Everything held: {_runs} runs in {took.TotalMinutes:0.#} minutes, and {totals.Passed} of {totals.Tests} assertions passed on what OSDU holds."), Tone.Good);
        prompter.Say(string.Empty);
        prompter.Say($"Open {gui} and sign in as {user}, password {password}", Tone.Heading);
        prompter.Say("  Schedules    the two estates, chained, and how long each link took");
        prompter.Say($"  Lineage      every table and flow, from source file to KPI: {await LineageAsync(gui, ct).ConfigureAwait(false)}");
        prompter.Say("  Assertions   every check of what OSDU holds, each with its evidence");
        prompter.Say("  Explorer     the records as OSDU holds them, every version of each");
        prompter.Say("  Mappings     how a source row became a record, field by field");
        return true;
    }

    /// <summary>
    /// Whether the stand-in for OSDU answers. Every OSDU step needs it, and without it each one fails on the same refused
    /// connection after its retries, so a closed window is said once, in a line, before minutes go by.
    /// </summary>
    private async Task<bool> StandInAnswersAsync(CancellationToken ct)
    {
        if (standIn is null)
        {
            return true;
        }

        var health = new Uri(new Uri(standIn), "/health");
        var why = string.Empty;
        for (var attempt = 0; attempt < StandInAttempts; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(health, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                why = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex)
            {
                why = SecretHygiene.RedactedMessage(ex);
            }

            await wait(Poll, ct).ConfigureAwait(false);
        }

        prompter.Say($"The stand-in for OSDU does not answer at {standIn}: {why}", Tone.Bad);
        prompter.Say("-> Its window may have been closed. dev.bat demo (./dev.sh demo) starts it again, and what was delivered is kept.");
        return false;
    }

    private async Task<bool> ComeUpAsync(CancellationToken ct)
    {
        prompter.Say("Waiting for the control plane; a first start builds it, which takes a few minutes ...", Tone.Note);
        var deadline = now() + StartLimit;
        var nextWord = now() + StillWaiting;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("/health/startup", UriKind.Relative), ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    prompter.Say("The control plane is up.", Tone.Good);
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet: a first start is still building it.
            }

            if (now() >= deadline)
            {
                prompter.Say($"The control plane did not come up within {StartLimit.TotalMinutes:0} minutes; its window says why.", Tone.Bad);
                return false;
            }

            if (now() >= nextWord)
            {
                prompter.Say("Still waiting for the control plane ...", Tone.Note);
                nextWord = now() + StillWaiting;
            }

            await wait(Poll, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> SignInAsync(string user, string password, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative), new { username = user, password }, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            prompter.Say($"The control plane refused {user} with the password in the env file, so it was changed after the demo set it.", Tone.Bad);
            prompter.Say("-> Give it back on the Users page, or start over: dev.bat demo reset (./dev.sh demo reset).");
            return false;
        }

        response.EnsureSuccessStatusCode();
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", answer.RootElement.GetProperty("accessToken").GetString());
        return true;
    }

    private async Task<bool> SyncedAsync(CancellationToken ct)
    {
        prompter.Say("Waiting for the sample estates to sync ...", Tone.Note);
        var deadline = now() + SyncLimit;
        while (true)
        {
            using var sources = await GetAsync("/api/v1/repos/sources", ct).ConfigureAwait(false);
            var listed = Items(sources.RootElement).Where(s => Repositories.Contains(Text(s, "name"), StringComparer.Ordinal)).ToList();
            if (listed.Count == Repositories.Count && listed.All(s => Text(s, "lastSyncedSha").Length > 0))
            {
                prompter.Say($"Synced: {string.Join(", ", listed.Select(s => $"{Text(s, "name")} at {Text(s, "lastSyncedSha")[..8]}"))}.", Tone.Good);
                return true;
            }

            if (now() >= deadline)
            {
                var errors = listed.Select(s => Text(s, "lastError")).Where(e => e.Length > 0).ToList();
                prompter.Say($"The sample estates did not sync within {SyncLimit.TotalMinutes:0} minutes{(errors.Count > 0 ? ": " + errors[0] : ".")}", Tone.Bad);
                return false;
            }

            await wait(Poll, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> SchedulesAsync(CancellationToken ct)
    {
        using var schedules = await GetAsync("/api/v1/schedules", ct).ConfigureAwait(false);
        return Items(schedules.RootElement).ToDictionary(s => Text(s, "name"), s => Text(s, "id"), StringComparer.Ordinal);
    }

    /// <summary>Fires one schedule as Run now does, holding back what is chained behind it, and waits for it to finish.</summary>
    private async Task<bool> FireAsync(TourStep step, string id, CancellationToken ct)
    {
        var started = now();
        using var response = await http.PostAsync(new Uri($"/api/v1/schedules/{id}/run?chain=false", UriKind.Relative), null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            prompter.Say($"{step.Says}: it did not start (HTTP {(int)response.StatusCode}): {SecretHygiene.RedactedMessage(body)}", Tone.Bad);
            return false;
        }

        using var fired = JsonDocument.Parse(body);
        var group = Text(fired.RootElement, "groupId");
        var run = Text(fired.RootElement, "runId");
        var deadline = now() + ScheduleLimit;
        while (true)
        {
            await wait(Poll, ct).ConfigureAwait(false);
            if (group.Length > 0)
            {
                using var state = await GetAsync($"/api/v1/runs/groups/{group}", ct).ConfigureAwait(false);
                var counts = state.RootElement.GetProperty("counts");
                if (Count(counts, "total") > 0 && Count(counts, "queued") + Count(counts, "running") == 0)
                {
                    return await ReportGroupAsync(step, group, Count(counts, "total"), Count(counts, "succeeded"), now() - started, ct).ConfigureAwait(false);
                }
            }
            else
            {
                using var state = await GetAsync($"/api/v1/runs/{run}", ct).ConfigureAwait(false);
                var status = Text(state.RootElement, "status");
                if (status is "succeeded" or "failed" or "cancelled" or "skipped")
                {
                    return Report(step, 1, status == "succeeded" ? 1 : 0, status == "succeeded" ? [] : [state.RootElement.Clone()], now() - started);
                }
            }

            if (now() >= deadline)
            {
                prompter.Say($"{step.Says}: not finished within {ScheduleLimit.TotalMinutes:0} minutes.", Tone.Bad);
                return false;
            }
        }
    }

    private async Task<bool> ReportGroupAsync(TourStep step, string group, int total, int succeeded, TimeSpan took, CancellationToken ct)
    {
        if (succeeded == total)
        {
            return Report(step, total, succeeded, [], took);
        }

        using var runs = await GetAsync($"/api/v1/runs?groupId={group}&pageSize=200", ct).ConfigureAwait(false);
        var failed = Items(runs.RootElement).Where(r => Text(r, "status") != "succeeded").Select(r => r.Clone()).ToList();
        return Report(step, total, succeeded, failed, took);
    }

    private bool Report(TourStep step, int total, int succeeded, IReadOnlyList<JsonElement> failed, TimeSpan took)
    {
        _runs += total;
        var seconds = took.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        if (succeeded == total)
        {
            prompter.Say($"{step.Says}: {Runs(total)}, {seconds} s", Tone.Good);
            return true;
        }

        prompter.Say($"{step.Says}: {total - succeeded} of {Runs(total)} did not succeed", Tone.Bad);
        var skipped = failed.Count(r => Text(r, "status") == "skipped");
        foreach (var run in failed.Where(r => Text(r, "status") != "skipped"))
        {
            prompter.Say($"  {Text(run, "flowName")}: {Short(Text(run, "error"), Text(run, "status"))}", Tone.Bad);
        }

        if (skipped > 0)
        {
            prompter.Say($"  {Runs(skipped)} behind it skipped", Tone.Note);
        }

        return false;
    }

    private static string Runs(int count) => count == 1 ? "1 run" : string.Create(CultureInfo.InvariantCulture, $"{count} runs");

    /// <summary>
    /// What went wrong, as one line someone can read: an error's first line, redacted, without the folders of a node's cache,
    /// and cut at a length a terminal keeps on one line or two. The whole of it is on the Runs page.
    /// </summary>
    private static string Short(string error, string status)
    {
        if (error.Length == 0)
        {
            return status;
        }

        var lines = SecretHygiene.RedactedMessage(error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = LocalFolders().Replace(lines[0], string.Empty);
        return first.Length > MostOfAnError ? first[..MostOfAnError] + " ..." : first;
    }

    /// <summary>Whether every assertion was evaluated and held: a test the run passed over counts against it, never for it.</summary>
    private async Task<AssertionTotals> AssertionTotalsAsync(CancellationToken ct)
    {
        using var board = await GetAsync("/api/v1/delivery/assertions", ct).ConfigureAwait(false);
        var totals = board.RootElement.GetProperty("totals");
        return new AssertionTotals(Count(totals, "tests"), Count(totals, "passed"), Count(totals, "failed"), Count(totals, "errored"), Count(totals, "notRun"));
    }

    /// <summary>What the Assertions page counts.</summary>
    private readonly record struct AssertionTotals(int Tests, int Passed, int Failed, int Errored, int NotRun)
    {
        /// <summary>Every assertion was judged and passed: one not judged is not one passed.</summary>
        public bool Held => Tests > 0 && Passed == Tests && Failed == 0 && Errored == 0 && NotRun == 0;

        /// <summary>Nothing is wrong, and some have no verdict yet.</summary>
        public bool OnlyNotJudged => Failed == 0 && Errored == 0 && NotRun > 0;
    }

    /// <summary>
    /// The address that draws the sales estate's lineage, since the page itself opens on a project to pick. Everything has held
    /// by now, so a graph it cannot name is no failure: the bare page is still the way there.
    /// </summary>
    private async Task<string> LineageAsync(string gui, CancellationToken ct)
    {
        var page = gui + "/lineage";
        using var response = await http.GetAsync(new Uri("/api/v1/lineage/projects", UriKind.Relative), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return page;
        }

        using var projects = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (var project in Items(projects.RootElement))
        {
            if (project.GetProperty("project").GetString() == SalesProject)
            {
                return $"{page}?repoId={project.GetProperty("repoId").GetString()}&project={SalesProject}";
            }
        }

        return page;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>A listing's rows, whichever way the endpoint wraps them.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement listing)
    {
        if (listing.ValueKind == JsonValueKind.Array)
        {
            return listing.EnumerateArray();
        }

        foreach (var name in new[] { "items", "sources", "schedules" })
        {
            if (listing.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                return rows.EnumerateArray();
            }
        }

        return [];
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Count(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;

    [GeneratedRegex(@"(?:[A-Za-z]:\\|/)(?:[^\\/\s:'""]+[\\/])+", RegexOptions.CultureInvariant)]
    private static partial Regex LocalFolders();
}
