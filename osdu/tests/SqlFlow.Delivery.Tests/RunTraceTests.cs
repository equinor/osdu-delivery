using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a run's live trace carries below its phases (<see cref="RunTrace"/>): every line about the first records it meets
/// and none about the rest, the calls those records make, the first calls made outside any record, and the retries of a
/// call, each allowance saying once where it stops.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunTraceTests
{
    private static DeliveryKey Key(int i) => DeliveryKey.Derive("wells", [i.ToString(CultureInfo.InvariantCulture)]);

    [Fact]
    public void A_trace_describes_the_first_records_it_meets_every_time_and_says_once_where_it_stops()
    {
        var trace = new RunTrace(recordsDescribed: 2);

        Assert.True(trace.Describes(Key(1), out var leftOut));
        Assert.False(leftOut);
        Assert.True(trace.Describes(Key(2), out _));
        Assert.False(trace.Describes(Key(3), out leftOut));
        Assert.True(leftOut);
        Assert.False(trace.Describes(Key(4), out leftOut));
        Assert.False(leftOut);

        // A record already described stays described: its steps and its outcome follow the line that said it was sent.
        Assert.True(trace.Describes(Key(1), out leftOut));
        Assert.False(leftOut);
        Assert.False(trace.Describes(Key(3), out _));
    }

    [Fact]
    public void Whichever_part_of_a_run_meets_the_first_record_left_out_says_where_the_record_lines_stop()
    {
        var trace = new RunTrace(recordsDescribed: 1);
        var events = new RunEventCollector();
        var loggers = new RunLogLoggerFactory(new RunLogger(RunLogLevel.Debug), events, Guid.NewGuid(), "trace");
        var intake = loggers.CreateLogger("SqlFlow.Delivery.Engine.Intake.SubmissionIntake");
        var worker = loggers.CreateLogger("SqlFlow.Delivery.Engine.Worker.DeliveryWorker");

        Assert.True(trace.Describes(Key(1), intake));
        Assert.False(trace.Describes(Key(2), intake));
        Assert.False(trace.Describes(Key(2), worker));
        Assert.False(trace.Describes(Key(3), worker));

        var note = Assert.Single(events.Records);
        Assert.Equal("intake", note.Step);
        Assert.StartsWith("The first 1 records of this run are on its trace in full", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_admits_what_is_said_about_described_records_problems_within_their_allowance_and_each_step_within_its_own()
    {
        var trace = new RunTrace(linesPerStep: 2, problemsDescribed: 1);

        using (RunTrace.AboutRecords(described: true))
        {
            Assert.True(trace.Admit("target", LogLevel.Debug, default).Write);
            Assert.True(trace.Admit("target", LogLevel.Warning, default).Write);
        }

        using (RunTrace.AboutRecords(described: false))
        {
            // A record the trace does not describe says nothing on its own, and its first problem takes the allowance.
            Assert.False(trace.Admit("target", LogLevel.Information, default).Write);
            Assert.True(trace.Admit("deliver", LogLevel.Warning, default).Write);
            var over = trace.Admit("deliver", LogLevel.Warning, default);
            Assert.False(over.Write);
            Assert.StartsWith("The first 1 problems of this run are on its trace", over.Note, StringComparison.Ordinal);
            Assert.Equal(new TraceAdmission(false), trace.Admit("deliver", LogLevel.Warning, default));
        }

        // The records' problems never crowd out the run's own warnings, which take from the run step's lines.
        Assert.True(trace.Admit(RunTrace.RunStep, LogLevel.Warning, default).Write);

        // Outside any record each step has its own allowance, and says once where it stops.
        Assert.True(trace.Admit("intake", LogLevel.Information, default).Write);
        Assert.True(trace.Admit("intake", LogLevel.Debug, default).Write);
        var stop = trace.Admit("intake", LogLevel.Information, default);
        Assert.False(stop.Write);
        Assert.StartsWith("Step intake has written its 2 lines to the trace", stop.Note, StringComparison.Ordinal);
        Assert.Equal(new TraceAdmission(false), trace.Admit("intake", LogLevel.Information, default));
        Assert.True(trace.Admit("source", LogLevel.Information, default).Write);

        // A line its writer bounds and an error are always written.
        Assert.True(trace.Admit("intake", LogLevel.Information, RunTrace.Bounded).Write);
        Assert.True(trace.Admit("intake", LogLevel.Error, default).Write);
        using (RunTrace.AboutRecords(described: false))
        {
            Assert.True(trace.Admit("deliver", LogLevel.Error, default).Write);
        }
    }

    /// <summary>
    /// A run of millions of records logs millions of lines through its engine (a protocol line per record, a warning per
    /// failed record, a line per batch and per search), and what reaches the trace stays within the run's allowances.
    /// </summary>
    [Fact]
    public void A_run_of_millions_of_records_writes_a_bounded_trace()
    {
        var events = new RunEventCollector();
        var trace = new RunTrace();
        var gated = new RunTraceGate(new RunLogLoggerFactory(new RunLogger(RunLogLevel.Debug), events, Guid.NewGuid(), "trace"), trace);
        var protocol = gated.CreateLogger("SqlFlow.Delivery.Engine.Protocols.OsduDdmsProtocol");
        var worker = gated.CreateLogger("SqlFlow.Delivery.Engine.Worker.DeliveryWorker");
        var search = gated.CreateLogger("SqlFlow.Delivery.Engine.OsduRecordSearch");
        var http = gated.CreateLogger("SqlFlow.Delivery.Engine.RunHttpTrace");
        const int records = 2_000_000;

        for (var i = 0; i < records; i++)
        {
            var key = Key(i);
            using var about = RunTrace.AboutRecords(trace.Describes(key, worker));
            protocol.LogInformation("Posted the rows of {Record}", i);
            http.LogDebug("POST /records answered HTTP 200 for {Record}", i);
            if (i % 50 == 0)
            {
                worker.LogWarning("{Record} is held: a reference is missing", i);
            }
        }

        for (var batch = 0; batch < records / 500; batch++)
        {
            worker.LogInformation("Batch {Batch} done", batch);
            search.LogDebug("Searched for the values of batch {Batch}", batch);
        }

        // Two lines about each described record, and the warning of record 0, which is one of them; four notes where the
        // records, the problems and the two steps' lines stop.
        var lines = events.Records;
        var described = (RunTrace.RecordsDescribed * 2) + 1;
        var notes = 1 + 1 + 2;
        var ceiling = described + RunTrace.ProblemsDescribed + (2 * RunTrace.LinesPerStep) + notes;
        Assert.True(lines.Count <= ceiling, $"{lines.Count} lines reached the trace; the ceiling is {ceiling}.");
        Assert.Equal(RunTrace.RecordsDescribed, lines.Count(e => e.Step == "target"));
        Assert.Single(lines, e => e.Message.StartsWith("The first 20 records of this run are on its trace in full", StringComparison.Ordinal));
        Assert.Single(lines, e => e.Message.StartsWith("The first 100 problems of this run", StringComparison.Ordinal));
        Assert.Single(lines, e => e.Message.StartsWith("Step deliver has written its 100 lines", StringComparison.Ordinal));
        Assert.Single(lines, e => e.Message.StartsWith("Step search has written its 100 lines", StringComparison.Ordinal));
    }

    [Fact]
    public void Progress_is_said_less_often_as_a_run_goes_on()
    {
        var clock = new TestClock();
        var trace = new RunTrace(clock);

        // The first ask starts the clock; then every 15 seconds for two minutes.
        Assert.False(trace.ProgressDue("deliver", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(trace.ProgressDue("deliver", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(trace.ProgressDue("deliver", clock.GetUtcNow()));

        // Each thing a run does keeps its own pace.
        Assert.False(trace.ProgressDue("intake", clock.GetUtcNow()));

        // Past two minutes, every minute; past an hour, every five.
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(trace.ProgressDue("deliver", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(trace.ProgressDue("deliver", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(trace.ProgressDue("deliver", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(trace.ProgressDue("deliver", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(trace.ProgressDue("deliver", clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(trace.ProgressDue("deliver", clock.GetUtcNow()));
    }

    [Fact]
    public void Progress_counts_the_run_s_records_whichever_batch_settled_them()
    {
        var clock = new TestClock();
        var trace = new RunTrace(clock);
        trace.AddPlanned(1_500_000);
        trace.AddPlanned(500_000);
        for (var i = 0; i < 1000; i++)
        {
            trace.Settled(RecordStatus.Delivered, nothingSent: false);
        }

        trace.Settled(RecordStatus.Delivered, nothingSent: true);
        trace.Settled(RecordStatus.Pending, nothingSent: false);
        trace.Settled(RecordStatus.Held, nothingSent: false);
        trace.Settled(RecordStatus.Failed, nothingSent: false);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(
            "1,004 of 2,000,000 planned record(s) settled after 10 s (100.4 a second): 1,000 delivered, 1 already in OSDU, 1 to try again, 1 held, 1 failed",
            trace.DeliveryProgress(clock.GetUtcNow()));
    }

    [Fact]
    public void Retries_are_named_until_their_allowance_runs_out()
    {
        var trace = new RunTrace(retriesDescribed: 1);

        Assert.True(trace.DescribesRetry(out var leftOut));
        Assert.False(leftOut);
        Assert.False(trace.DescribesRetry(out leftOut));
        Assert.True(leftOut);
        Assert.False(trace.DescribesRetry(out leftOut));
        Assert.False(leftOut);
    }

    [Fact]
    public void A_record_reads_as_its_source_key_and_label()
    {
        Assert.Equal("welldb:PROJECT_A/1/1 [Wellbore A/1-19 A / COMPOSITE]", RunTrace.Record("welldb:PROJECT_A/1/1", "Wellbore A/1-19 A / COMPOSITE", Key(1)));
        Assert.Equal("welldb:PROJECT_A/1/1", RunTrace.Record("welldb:PROJECT_A/1/1", null, Key(1)));
        Assert.Equal(Key(1).ToString(), RunTrace.Record(null, null, Key(1)));
    }

    [Fact]
    public void What_the_engine_says_is_filed_under_what_it_is_doing()
    {
        var events = new RunEventCollector();
        var loggers = new RunLogLoggerFactory(new RunLogger(RunLogLevel.Debug), events, Guid.NewGuid(), "trace");
        foreach (var category in new[]
        {
            "SqlFlow.Delivery.Engine.Protocols.OsduDdmsProtocol", "SqlFlow.Delivery.Engine.Protocols.OsduWorkflowProtocol",
            "SqlFlow.Delivery.Engine.Protocols.Etp.EtpSession", "SqlFlow.Delivery.Engine.RunHttpTrace",
            "SqlFlow.Delivery.Engine.OsduRecordSearch", "SqlFlow.Delivery.Source.SqlServerIngestionSource",
        })
        {
            loggers.CreateLogger(category).LogInformation("said by {Category}", category);
        }

        Assert.Equal(["target", "target", "target", "http", "search", "source"], events.Records.Select(e => e.Step));
    }

    /// <summary>
    /// The HTTP stack tells its observer of every attempt with the status it got, and of every retry with why and how long
    /// it waits, naming the URL without its query string, where a signed URL carries its credential.
    /// </summary>
    [Fact]
    public async Task The_http_stack_reports_each_attempt_and_each_retry_to_its_observer()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Get, "/api/storage/v2/records/dev:x", call => FakeHttpHandler.Json(call == 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "{}"));
        var observer = new RecordingObserver();
        var reliability = new FlowReliability { Retry = new FlowRetry { Attempts = 3, BaseDelayMs = 1, MaxDelayMs = 1 } };
        using var http = new HttpRuntime(reliability, new SecretResolver([new EnvSecretProvider()]), handler: handler, allowLoopback: true, observer: observer);

        var result = await http.Data.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/storage/v2/records/dev:x?sig=secret-signature"));

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Same(observer, http.Observer);
        Assert.Equal([(1, (int?)503), (2, (int?)200)], observer.Attempts.Select(a => (a.Attempt, a.Status)));
        Assert.All(observer.Attempts, a => Assert.Equal("http://localhost/api/storage/v2/records/dev:x", a.Url));
        var retry = Assert.Single(observer.Retries);
        Assert.Equal(("GET", 1, 3, "HTTP 503"), (retry.Method, retry.Attempt, retry.MaxAttempts, retry.Cause));
        Assert.DoesNotContain(observer.Attempts, a => a.Url.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_writes_each_call_of_a_described_record_and_each_retry_to_its_trace()
    {
        var events = new RunEventCollector();
        var loggers = new RunLogLoggerFactory(new RunLogger(RunLogLevel.Debug), events, Guid.NewGuid(), "trace");
        var context = Samples.Engine(ledger: null).ForRun(loggers);
        var observer = context.HttpObserver ?? throw new InvalidOperationException("a run's context watches its calls");

        using (RunTrace.AboutRecords(described: true))
        {
            observer.Ended(new HttpAttempt("POST", "https://osdu.example/api/os-wellbore-ddms/ddms/v3/welllogs", 1, 200, null, TimeSpan.FromMilliseconds(312)));
        }

        using (RunTrace.AboutRecords(described: false))
        {
            observer.Ended(new HttpAttempt("POST", "https://osdu.example/api/os-wellbore-ddms/ddms/v3/welllogs", 1, 200, null, TimeSpan.FromMilliseconds(40)));
        }

        observer.Retrying(new HttpRetry("PUT", "https://blob.example/container/chunk_0000.parquet", 1, 3, "HTTP 429", TimeSpan.FromSeconds(2)));

        Assert.Equal(
            [
                ("http", RunEventLevels.Debug, "POST https://osdu.example/api/os-wellbore-ddms/ddms/v3/welllogs answered HTTP 200 in 312 ms."),
                ("http", RunEventLevels.Info, "PUT https://blob.example/container/chunk_0000.parquet: HTTP 429 on attempt 1 of 3; trying again in 2 s."),
            ],
            events.Records.Select(e => (e.Step, e.Level, e.Message)));
    }

    [Fact]
    public void A_context_that_serves_no_run_watches_no_calls()
        => Assert.Null(Samples.Engine(ledger: null).HttpObserver);

    private sealed class RecordingObserver : IHttpObserver
    {
        public List<HttpAttempt> Attempts { get; } = [];

        public List<HttpRetry> Retries { get; } = [];

        public void Ended(HttpAttempt attempt) => Attempts.Add(attempt);

        public void Retrying(HttpRetry retry) => Retries.Add(retry);
    }
}

/// <summary>
/// A run's trace as it streams: what it renders with, the route, the planning, each record it describes as it is sent,
/// each step and call it makes and how it ended, and nothing per record past the trace's allowance. The engine's own
/// parts (the protocols among them) write to the run's log, never to the host's.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RunTraceDeliveryTests : IDisposable
{
    private const string WellLogTable = "OsduData.silver.WellLog";
    private const string Root = "/api/os-wellbore-ddms";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero));
    private readonly string _root = Samples.NewTempDirectory();

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still open by a finished run is the temporary folder's to clean up.
        }
    }

    [Fact]
    public async Task A_delivery_run_streams_what_it_does_down_to_each_record_and_call()
    {
        var estate = new MemoryEstate().Add(WellLogTable, await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime, time: _clock));
        var handler = WellboreDdms();
        using var fake = new FakeOsduProtocols(handler);
        var protocols = new AnnouncingProtocols(fake);
        var engine = Samples.Engine(_db.Ledger(_clock), _clock, protocols: protocols, sources: estate);
        var events = new RunEventCollector();

        var result = await RunAsync(engine, Load(), events);

        Assert.True(result.Success, result.Error);
        var records = events.Records;
        string[] Lines(string step, string level) => records.Where(e => e.Step == step && e.Level == level).Select(e => e.Message).ToArray();
        var logs = SampleEstate.Logs().Count;

        // What the run renders with and how it delivers, before anything is read.
        Assert.Contains(Lines("run", RunEventLevels.Info), m => m.Contains("Rendering with mapping WellLog@1.4.0", StringComparison.Ordinal) && m.Contains("ids are minted in partition dev", StringComparison.Ordinal));
        Assert.Contains(Lines("run", RunEventLevels.Info), m => m.Contains("by the ddms route", StringComparison.Ordinal));

        // The protocol writes to the run's log with the loggers the runtime handed its factory.
        Assert.Contains(Lines("target", RunEventLevels.Info), m => m.EndsWith("protocol built for the run", StringComparison.Ordinal));

        // The planning names the read and each record it queues.
        Assert.Contains(Lines("intake", RunEventLevels.Info), m => m.Contains("Planning submission ", StringComparison.Ordinal) && m.Contains(string.Create(CultureInfo.InvariantCulture, $": {logs} candidate record(s) of "), StringComparison.Ordinal));
        Assert.Equal(logs, Lines("intake", RunEventLevels.Info).Count(m => m.Contains("create", StringComparison.Ordinal) && m.Contains("work-product-component--WellLog:", StringComparison.Ordinal)));

        // Each record says it is being sent, each step it completed, and how it ended.
        var sending = Lines("deliver", RunEventLevels.Info).Where(m => m.Contains("Sending ", StringComparison.Ordinal)).ToList();
        Assert.Equal(logs, sending.Count);
        Assert.All(sending, m => Assert.Contains("the record and its payload", m, StringComparison.Ordinal));
        // The record's write is named before it goes (bulk data follows it), then the write, then the bulk data: each once.
        var done = Lines("deliver", RunEventLevels.Debug).Where(m => m.Contains(" done", StringComparison.Ordinal)).ToList();
        Assert.Equal(logs * 3, done.Count);
        foreach (var step in new[] { ": metadata-intent done", ": metadata done", ": bulk done" })
        {
            Assert.Equal(logs, done.Count(m => m.Contains(step, StringComparison.Ordinal)));
        }

        Assert.All(done.Where(m => m.Contains(": metadata-intent done", StringComparison.Ordinal)), m => Assert.Contains("intent record dev:work-product-component--WellLog:", m, StringComparison.Ordinal));
        foreach (var record in done.Select(m => m[..m.IndexOf(": ", StringComparison.Ordinal)]).Distinct())
        {
            Assert.Equal(
                [": metadata-intent done", ": metadata done", ": bulk done"],
                done.Where(m => m.StartsWith(record + ": ", StringComparison.Ordinal)).Select(m => m[record.Length..(m.IndexOf(" done", StringComparison.Ordinal) + 5)]));
        }
        var delivered = Lines("deliver", RunEventLevels.Info).Where(m => m.Contains("Delivered ", StringComparison.Ordinal)).ToList();
        Assert.Equal(logs, delivered.Count);
        Assert.All(delivered, m => Assert.Matches(@"as dev:work-product-component--WellLog:[0-9a-f]{32} version \d+ \(metadata\+payload\) in [\d.]+ m?s; metadata HTTP 200 in [\d.]+ m?s; payload in [\d.]+ m?s, chunks 1\.$", m));

        // Every call a described record made is named with the status it got.
        var calls = Lines("http", RunEventLevels.Debug);
        Assert.Equal(logs, calls.Count(m => m.Contains("POST http://localhost" + Root + "/ddms/v3/welllogs answered HTTP 200", StringComparison.Ordinal)));
        Assert.Equal(logs, calls.Count(m => m.Contains("/data answered HTTP 200", StringComparison.Ordinal)));

        // Nothing the trace carries is a secret or a query string.
        Assert.DoesNotContain(records, e => e.Message.Contains('?', StringComparison.Ordinal) && e.Step == "http");
    }

    /// <summary>
    /// Past the trace's allowance a run writes no line per record: a worker describes the first records it meets, says once
    /// where its record lines stop, and keeps its batch lines.
    /// </summary>
    [Fact]
    public async Task Past_its_allowance_a_run_writes_no_line_per_record()
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var events = new RunEventCollector();
        var loggers = new RunLogLoggerFactory(new RunLogger(RunLogLevel.Debug), events, Guid.NewGuid(), "trace");
        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, loggers.CreateLogger<DeliveryWorker>(), "test-worker")
        {
            MaxWait = null,
            Trace = new RunTrace(recordsDescribed: 2),
        };

        var summary = await worker.DrainAsync(intake.Submission.SubmissionId);

        var logs = SampleEstate.Logs().Count;
        Assert.Equal(logs, summary.Delivered);
        var lines = events.Records.Select(e => e.Message).ToList();
        Assert.Equal(2, lines.Count(m => m.StartsWith("Sending ", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(m => m.StartsWith("Delivered ", StringComparison.Ordinal)));
        Assert.Single(lines, m => m.StartsWith("The first 2 records of this run are on its trace in full", StringComparison.Ordinal));
        Assert.Contains(lines, m => m.StartsWith("Claimed batch ", StringComparison.Ordinal));
        Assert.Contains(lines, m => m.StartsWith("Batch ", StringComparison.Ordinal) && m.Contains(" done in ", StringComparison.Ordinal));
    }

    /// <summary>A Wellbore DDMS and the legal service beside it, answering every call the route makes.</summary>
    private static FakeHttpHandler WellboreDdms()
    {
        var versions = 0;
        return new FakeHttpHandler()
            .On(HttpMethod.Post, "/api/legal/v1/legaltags:validate", HttpStatusCode.OK, """{"invalidLegalTags":[]}""")
            .On(HttpMethod.Get, Root + "/about", HttpStatusCode.OK, """{"service":"Wellbore DDMS"}""")
            .OnMatch(
                r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/data", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"))
            .OnMatch(
                r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith(Root + "/ddms/v3/", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"recordIdVersions\":[\"written:" + (++versions).ToString(CultureInfo.InvariantCulture) + "\"]}"))
            .OnMatch(
                r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.StartsWith(Root + "/ddms/v3/", StringComparison.Ordinal),
                _ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"version":1700000000000001}"""));
    }

    private SourceDefinition Load()
    {
        var root = _root.Replace('\\', '/');
        var mappings = Samples.FixtureMappings.Replace('\\', '/');
        var yaml = ($$"""
            flowType: delivery
            name: welllogs
            parameters:
              logSource: { required: true }
            source:
              connection: ${env:OSDU_DATA_DB}
              lastModified: update_date
              work: '{{root}}/work/{logSource}'
            render:
              mappings: '{{mappings}}'
              parameters:
                dataPartition: dev
                aclOwner: data.welldb.owners@dev.dataservices.energy
                aclViewer: data.welldb.viewers@dev.dataservices.energy
                legalTag: dev-osdu-default-legal
            target:
              endpoint: http://localhost
              headers:
                data-partition-id: {{Samples.SampleCacheScope}}
              ddms:
                wellbore:
                  root: {{Root}}
            reliability:
              retry: { attempts: 1 }
            parallelism:
              concurrency: 1
              renderers: 1
            interfaces:
              welllogs:
                record: { object: {{WellLogTable}}, key: [source_project, log_id], primaryKey: RecId, scope: { log_source: logSource } }
                datasets:
                  curves: { object: OsduData.silver.WellLogCurve, join: { source_project: source_project, log_id: log_id }, orderBy: [curve_ordinal] }
                bulk: { root: '{{root}}/curves', locationColumn: curve_folder, pattern: "chunk_*.parquet", hashColumn: payload_hash, chunkCountColumn: chunk_count }
                mapping: WellLog@1.4.0
            """).ReplaceLineEndings("\n");
        var path = Path.Combine(_root, "flows", "welllogs.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return new DeliveryDocumentLoader().LoadSource(path);
    }

    private static async Task<DocumentExecutionResult> RunAsync(EngineContext engine, SourceDefinition source, RunEventCollector events)
    {
        using var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        return await new DeliveryExecutor(provider).ExecuteAsync(
            new DeliveryFlowDocument { Source = source },
            source.SourcePath!,
            new DocumentExecutionOptions
            {
                RunId = Guid.CreateVersion7(),
                Actor = "test",
                EventSink = events,
                Parameters = new RunParameters
                {
                    Operation = DeliveryOperations.Deliver,
                    Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal),
                },
            },
            CancellationToken.None);
    }

    /// <summary>Builds the real protocols over the fake OSDU and says so through the loggers the runtime hands it.</summary>
    private sealed class AnnouncingProtocols(FakeOsduProtocols inner) : IProtocolFactory
    {
        public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
        {
            loggers.CreateLogger("SqlFlow.Delivery.Engine.Protocols.AnnouncingProtocol").LogInformation("protocol built for the run");
            return inner.CreateAsync(flow, http, loggers, ct);
        }
    }
}
