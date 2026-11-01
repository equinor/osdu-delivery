using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dry run (<see cref="ValueCheckRequest.DryRun"/>): what a flow's staged rows come to when they are taken through everything a
/// delivery does short of sending them, over the sample well logs and a fake platform whose Schema service answers. Every
/// row is said, whatever became of it; each document is held to the template and to the service's schema, and a service that
/// cannot answer leaves that unjudged rather than failing the run; what is listed is bounded and every count exact; and
/// nothing is written anywhere.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DryRunTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeOsduPlatform _platform = new();
    private readonly FakeOsduProtocols _protocols;
    private readonly TargetClients _clients;

    public DryRunTests()
    {
        _protocols = new FakeOsduProtocols(_platform);
        _clients = new TargetClients(_clock, _platform, allowLoopback: true);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _clients.Dispose();
        _protocols.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed assertion left open is removed with the temporary folder by the operating system.
        }
    }

    /// <summary>The sample estate over the fake platform, whose Schema service holds the template the mapping pins unless <paramref name="registered"/> says otherwise.</summary>
    private async Task<(MemoryIngestionTables Tables, FlowRuntime Runtime)> RuntimeAsync(
        Action<MemoryIngestionTables>? change = null, bool registered = true, Func<FlowDefinition, FlowDefinition>? adjust = null)
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        change?.Invoke(tables);
        var engine = Samples.Engine(ledger: null, _clock, protocols: _protocols, sources: tables, searches: FixedRecordSearchFactory.SampleWellbores()) with { Clients = _clients };
        var flow = Samples.LocalFlow(_root);
        flow = flow with { Target = flow.Target with { Endpoint = FakeOsduPlatform.Endpoint } };
        var runtime = await FlowRuntime.CreateAsync(engine, adjust is null ? flow : adjust(flow), SampleEstate.Values);
        if (registered)
        {
            _platform.Schemas[runtime.Mapping.Mapping.Kind] = (JsonObject)runtime.Mapping.Schema.Root.DeepClone();
        }

        return (tables, runtime);
    }

    /// <summary>Puts on the platform the records the sample rows refer to, so a row is judged by what it holds rather than by what the fake platform lacks.</summary>
    private async Task HoldReferencesAsync(FlowRuntime runtime)
    {
        foreach (var log in SampleEstate.Logs())
        {
            var key = System.Text.Json.JsonSerializer.Serialize(new[] { log.SourceProject, log.LogId });
            var preview = await new RecordPreviewer(runtime).PreviewAsync(key);
            foreach (var reference in preview.References)
            {
                var id = reference.Id.TrimEnd(':');
                _platform.Put(FakeOsduPlatform.Record(id, "osdu:wks:" + id.Split(':')[1] + ":1.0.0"));
            }
        }
    }

    private static Task<ValueCheck> DryRunAsync(FlowRuntime runtime, ValueCheckLimits? limits = null, long maxRows = ValueCheckLimits.DefaultRows)
        => new ValueChecker(runtime, limits).CheckAsync(new ValueCheckRequest { DryRun = true, MaxRows = maxRows });

    [Fact]
    public async Task Every_row_is_said_each_document_held_to_the_template_and_the_service_and_nothing_is_written()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            await HoldReferencesAsync(runtime);
            var logs = SampleEstate.Logs().Count;
            var check = await DryRunAsync(runtime);
            var run = check.DryRun!;

            Assert.Equal(logs, run.Outcomes.Rows);
            Assert.Equal(logs, run.Records.Count);
            Assert.False(run.Cut);
            Assert.Equal(logs, run.Schema.Valid + run.Schema.Unverified);
            Assert.Equal(logs, run.Template.Valid + run.Template.Unverified);
            Assert.Equal(SchemaMatchStates.Matches, run.SchemaMatch.State);
            Assert.Equal(SchemaServiceReader.Published, run.SchemaMatch.Status);
            Assert.Equal(logs, run.NextRun.Create);

            // The same read says the same rows attribute by attribute.
            Assert.Equal(logs, check.Rows.Checked);
            Assert.NotEmpty(check.Variables);

            var first = run.Records[0];
            Assert.Equal(DryRunOutcomes.Ready, first.Outcome);
            Assert.Null(first.Reason);
            Assert.Equal("create", first.NextRun);
            Assert.Equal(SampleEstate.Key(0).Value, first.DeliveryKey);
            Assert.Equal(SampleEstate.FileName, first.File);
            Assert.NotNull(first.KeyJson);

            // Nothing went to the platform but questions: a schema, a listing, and what the records refer to.
            Assert.DoesNotContain(_platform.Calls, call => call.Method == HttpMethod.Put || call.Method == HttpMethod.Delete || call.Method == HttpMethod.Patch);
            Assert.DoesNotContain(_platform.Calls, call => call.Method == HttpMethod.Post
                && !call.Uri.AbsolutePath.EndsWith("/query/records", StringComparison.Ordinal)
                && !call.Uri.AbsolutePath.EndsWith("/legaltags:validate", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_kind_the_service_does_not_hold_is_said_and_its_documents_are_left_unjudged_not_passed()
    {
        var (_, runtime) = await RuntimeAsync(registered: false);
        using (runtime)
        {
            var run = (await DryRunAsync(runtime)).DryRun!;

            Assert.Equal(SchemaMatchStates.Unregistered, run.SchemaMatch.State);
            Assert.Contains("holds no schema", run.SchemaMatch.Why, StringComparison.Ordinal);
            Assert.Equal(0, run.Schema.Valid);
            Assert.Equal(run.Outcomes.Rows, run.Schema.NotChecked);
            Assert.All(run.Records, record => Assert.Null(record.Schema));

            // What the template says is still said: the service's silence takes nothing else away.
            Assert.All(run.Records, record => Assert.NotNull(record.Template));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_service_that_refuses_or_fails_is_said_and_the_run_goes_on(HttpStatusCode status)
    {
        var (_, runtime) = await RuntimeAsync(registered: false);
        using (runtime)
        {
            _platform.SchemaFailures[runtime.Mapping.Mapping.Kind] = status;

            var run = (await DryRunAsync(runtime)).DryRun!;

            Assert.Equal(SchemaMatchStates.Unavailable, run.SchemaMatch.State);
            Assert.StartsWith("The Schema service could not be read", run.SchemaMatch.Why, StringComparison.Ordinal);
            Assert.Equal(run.Outcomes.Rows, run.Records.Count);
            Assert.Equal(run.Outcomes.Rows, run.Schema.NotChecked);
        }
    }

    [Fact]
    public async Task A_rule_only_the_service_states_asks_for_attention_and_the_difference_is_named()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            await HoldReferencesAsync(runtime);
            var kind = runtime.Mapping.Mapping.Kind;
            var served = _platform.Schemas[kind];
            var data = served["properties"]!["data"]!.AsObject();
            data["properties"] ??= new JsonObject();
            data["properties"]!["OnlyTheServiceKnows"] = new JsonObject { ["type"] = "string" };
            var required = data["required"] as JsonArray ?? [];
            data["required"] = required;
            required.Add("OnlyTheServiceKnows");

            var run = (await DryRunAsync(runtime)).DryRun!;

            Assert.Equal(SchemaMatchStates.Differs, run.SchemaMatch.State);
            Assert.Contains(run.SchemaMatch.Differences, d => d.Path.EndsWith("OnlyTheServiceKnows", StringComparison.Ordinal) && d.Change == "onlyInService");
            Assert.True(run.SchemaMatch.BreakingCount > 0);

            // The template the mapping was written for finds nothing; the partition would.
            Assert.Equal(0, run.Template.Invalid);
            Assert.Equal(run.Outcomes.Rows, run.Schema.Invalid);
            Assert.Equal(run.Outcomes.Rows, run.Outcomes.Attention);
            var first = run.Records[0];
            Assert.Equal(DryRunOutcomes.Attention, first.Outcome);
            Assert.Contains("OnlyTheServiceKnows", first.Reason, StringComparison.Ordinal);
            Assert.True(first.SchemaProblems > 0);
            Assert.Equal(0, first.TemplateProblems);
            var rule = Assert.Single(run.Rules, r => r.Rule == "required");
            Assert.Equal(run.Outcomes.Rows, rule.Records);
        }
    }

    [Fact]
    public async Task Rows_a_delivery_would_not_send_are_said_with_why_and_every_count_adds_up()
    {
        var (_, runtime) = await RuntimeAsync(tables =>
        {
            tables.Records[0].DeletedUtc = Now.AddMinutes(-1);
            tables.Records[1].Row["wellbore_uwi"] = "NOWHERE 1";
            tables.Records[2].Row["log_id"] = "   ";
        });
        using (runtime)
        {
            var run = (await DryRunAsync(runtime)).DryRun!;
            var byOutcome = run.Records.ToLookup(r => r.Outcome);

            var deleted = Assert.Single(byOutcome[DryRunOutcomes.PassedOver]);
            Assert.Contains("deleted", deleted.Reason, StringComparison.Ordinal);

            Assert.Equal(2, byOutcome[DryRunOutcomes.Held].Count());
            Assert.All(byOutcome[DryRunOutcomes.Held], held => Assert.False(string.IsNullOrWhiteSpace(held.Reason)));
            Assert.Contains(byOutcome[DryRunOutcomes.Held], held => held.Reason!.Contains("NOWHERE 1", StringComparison.Ordinal));

            // A row with an empty key part has no record to name: nothing can be previewed by it.
            Assert.Contains(byOutcome[DryRunOutcomes.Held], held => held.KeyJson is null && held.DeliveryKey is null);

            var outcomes = run.Outcomes;
            Assert.Equal(outcomes.Rows, outcomes.Ready + outcomes.Attention + outcomes.Held + outcomes.PassedOver);
            Assert.Equal(outcomes.Rows, run.NextRun.Create + run.NextRun.Update + run.NextRun.Unchanged + run.NextRun.Hold);
            Assert.Equal(SampleEstate.Logs().Count, outcomes.Rows);
        }
    }

    [Fact]
    public async Task What_is_listed_is_bounded_rows_with_something_to_say_first_and_every_count_stays_exact()
    {
        var (_, runtime) = await RuntimeAsync(tables => tables.Records[1].Row["wellbore_uwi"] = "NOWHERE 1");
        using (runtime)
        {
            var limits = new ValueCheckLimits { DryRun = new DryRunLimits { MaxListed = 2 } };
            var run = (await DryRunAsync(runtime, limits)).DryRun!;

            Assert.Equal(SampleEstate.Logs().Count, run.Outcomes.Rows);
            Assert.Equal(2, run.Records.Count);
            Assert.True(run.Cut);
            Assert.Contains(run.Records, r => r.Outcome == DryRunOutcomes.Held);
            Assert.Contains(run.Notes, note => note.Contains("are listed", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_row_budget_is_kept_and_the_rows_read_are_the_rows_said()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var check = await DryRunAsync(runtime, maxRows: 1);

            Assert.Equal(1, check.DryRun!.Outcomes.Rows);
            Assert.Single(check.DryRun.Records);
            Assert.False(check.Rows.Complete);
        }
    }

    [Fact]
    public async Task An_answer_too_large_keeps_the_rows_with_something_to_say_and_every_count()
    {
        var (_, runtime) = await RuntimeAsync(tables => tables.Records[1].Row["wellbore_uwi"] = "NOWHERE 1");
        using (runtime)
        {
            var check = await DryRunAsync(runtime);
            var rows = check.DryRun!.Outcomes.Rows;

            var fitted = ValueChecker.Fit(check, 1000, c => c.DryRun!.Records.Count * 400);

            Assert.True(fitted.DryRun!.Records.Count * 400 <= 1000);
            Assert.True(fitted.DryRun.Cut);
            Assert.Equal(DryRunOutcomes.Held, fitted.DryRun.Records[0].Outcome);
            Assert.Equal(rows, fitted.DryRun.Outcomes.Rows);
        }
    }

    [Fact]
    public async Task Legal_tags_the_service_refuses_are_said_and_stop_nothing()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var tag = MappingEntry.ExpandParameters(runtime.Mapping.Mapping.Envelope.LegalTags[0], runtime.Mapping.Renderer.ParameterValue);
            _platform.RefusedLegalTags[tag] = "the tag has expired";

            var run = (await DryRunAsync(runtime)).DryRun!;

            Assert.Equal(DryRunLegalStates.Refused, run.LegalTags.State);
            Assert.Contains("the tag has expired", run.LegalTags.Why, StringComparison.Ordinal);
            Assert.Equal(SampleEstate.Logs().Count, run.Records.Count);
        }
    }

    [Fact]
    public async Task The_list_and_a_dry_run_name_a_row_alike_so_what_became_of_it_finds_its_line()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var page = await runtime.Source.ReadStagedAsync(new StagedRowsQuery { Limit = 500 });
            var system = runtime.Mapping.Mapping.Dataset.System;

            var run = (await DryRunAsync(runtime)).DryRun!;

            Assert.Equal(page.Rows.Select(row => SourceKey.Display(system, row.Key.Values)), run.Records.Select(record => record.SourceKey));
            Assert.Equal(page.Rows.Select(row => row.Key.Json), run.Records.Select(record => record.KeyJson));
        }
    }

    [Theory]
    [InlineData("acme:dspdm:well_test:1.0.0", "DSPDM")]
    [InlineData("energistics:etp:obj_Grid2dRepresentation:2.0.1", "Energistics")]
    public async Task A_kind_no_osdu_schema_describes_is_said_to_be_none_and_the_service_is_not_asked(string kind, string named)
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var check = await SchemaCrossCheck.OpenAsync(runtime);

            var found = await check.KindAsync(kind, null, default);

            Assert.Equal(SchemaMatchStates.NotApplicable, found.Match.State);
            Assert.Contains(named, found.Match.Why, StringComparison.Ordinal);
            Assert.Null(found.Rules);
            Assert.Equal(0, _platform.SchemaReads(kind));
        }
    }

    [Fact]
    public async Task A_record_an_enrichment_adds_is_held_to_the_schema_of_its_own_kind_and_one_unregistered_says_so()
    {
        const string held = "osdu:wks:dataset--File.Generic:1.0.0";
        const string unheld = "acme:custom:dataset--Own:1.0.0";
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            _platform.Schemas[held] = new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("kind", "data"),
                ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["type"] = "string" }, ["data"] = new JsonObject { ["type"] = "object" } },
            };
            var document = new JsonObject { ["kind"] = held };
            var render = new RenderResult
            {
                SourceKey = "welldb:x",
                Document = new JsonObject(),
                Canonical = "{}",
                MetadataHash = "h",
                Holds = [],
                Enrichment =
                [
                    new StepContribution
                    {
                        Step = "files",
                        Records = [new EnrichRecord("dev:dataset--File.Generic:1", held, 0, document), new EnrichRecord("dev:dataset--Own:1", unheld, 1, new JsonObject { ["kind"] = unheld })],
                    },
                ],
            };
            var check = await SchemaCrossCheck.OpenAsync(runtime);

            var added = await check.AddedOfAsync(render, default);

            Assert.Equal(2, added.Count);
            Assert.Equal(SchemaMatchStates.Read, added[0].Schema.Match.State);
            Assert.True(added[0].Findings!.ProblemCount > 0);
            Assert.Equal(SchemaMatchStates.Unregistered, added[1].Schema.Match.State);
            Assert.Null(added[1].Findings);
            Assert.Equal([unheld, held], check.Added.Select(match => match.Kind));
        }
    }

    [Fact]
    public async Task A_row_whose_id_osdu_already_holds_is_held_as_an_intake_would_hold_it()
    {
        var mappings = Path.Combine(_root, "mappings-key");
        Directory.CreateDirectory(mappings);
        var yaml = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        var marked = System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^  key: \[source_project, log_id\]", "$0\n  idFrom: key", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.NotEqual(yaml, marked);
        File.WriteAllText(Path.Combine(mappings, "WellLog@1.4.0.yaml"), marked);
        var (_, runtime) = await RuntimeAsync(adjust: flow => flow with { Render = flow.Render with { MappingsDirectory = mappings } });
        using (runtime)
        {
            var log = SampleEstate.Logs()[0];
            var id = TargetId.ComposeFromKey(Samples.SamplePartition, "work-product-component--WellLog", [log.SourceProject, log.LogId], out _)!;
            _platform.Put(FakeOsduPlatform.Record(id, runtime.Mapping.Mapping.Kind));

            var run = (await DryRunAsync(runtime)).DryRun!;

            Assert.Equal(1, run.Occupied);
            var taken = Assert.Single(run.Records, record => record.Occupied);
            Assert.Equal(DryRunOutcomes.Held, taken.Outcome);
            Assert.Contains("already holds a record", taken.Reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("DEVELOPMENT", "INTERNAL")]
    [InlineData("OBSOLETE", "SHARED")]
    public async Task A_kinds_status_and_scope_are_read_from_the_listing_whichever_they_are(string status, string scope)
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            _platform.SchemaInfos[runtime.Mapping.Mapping.Kind] = (status, scope);

            var match = (await SchemaCrossCheck.OpenAsync(runtime)).Match;

            Assert.Equal((status, scope), (match.Status, match.Scope));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_listing_that_fails_leaves_the_status_unsaid_and_takes_nothing_else_away(HttpStatusCode status)
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            foreach (var state in SchemaServiceReader.Statuses)
            {
                foreach (var scope in SchemaServiceReader.Scopes)
                {
                    _platform.SchemaListingRefusals[$"{state} {scope}"] = status;
                }
            }

            var match = (await SchemaCrossCheck.OpenAsync(runtime)).Match;

            Assert.Equal(SchemaMatchStates.Matches, match.State);
            Assert.Null(match.Status);
        }
    }

    [Fact]
    public async Task A_dry_run_of_one_attribute_is_refused()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var refused = await Assert.ThrowsAsync<DeliveryException>(() => new ValueChecker(runtime).CheckAsync(
                new ValueCheckRequest { DryRun = true, Targets = ["osdu.data.Name"] }));

            Assert.Contains("every attribute", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_preview_holds_its_document_to_the_service_and_says_what_the_template_says()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null, schemaCheck: true);

            var check = preview.SchemaCheck!;
            Assert.Equal(SchemaMatchStates.Matches, check.Schema.State);
            Assert.NotNull(check.Verdict);
            Assert.Equal("schema-service", check.Verdict!["schema"]!["source"]!.GetValue<string>());
            Assert.NotNull(check.Guidance);
            Assert.NotNull(check.TemplateOutcome);
            Assert.Null(check.Problem);
        }
    }

    [Fact]
    public async Task A_preview_says_why_a_held_record_is_not_judged()
    {
        var (_, runtime) = await RuntimeAsync(tables => tables.Records[0].Row["wellbore_uwi"] = "NOWHERE 1");
        using (runtime)
        {
            var key = System.Text.Json.JsonSerializer.Serialize(new[] { SampleEstate.Logs()[0].SourceProject, SampleEstate.Logs()[0].LogId });
            var preview = await new RecordPreviewer(runtime).PreviewAsync(key, schemaCheck: true);

            Assert.Null(preview.SchemaCheck!.Verdict);
            Assert.Contains("held before it is whole", preview.SchemaCheck.Problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_preview_without_the_check_asks_the_service_nothing()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var kind = runtime.Mapping.Mapping.Kind;
            var preview = await new RecordPreviewer(runtime).PreviewAsync(null);

            Assert.Null(preview.SchemaCheck);
            Assert.Equal(0, _platform.SchemaReads(kind));
        }
    }

    [Fact]
    public async Task Going_through_records_one_at_a_time_reads_the_schema_once()
    {
        var (_, runtime) = await RuntimeAsync();
        using (runtime)
        {
            var kind = runtime.Mapping.Mapping.Kind;
            for (var i = 0; i < 3; i++)
            {
                await new RecordPreviewer(runtime).PreviewAsync(null, schemaCheck: true);
            }

            Assert.Equal(1, _platform.SchemaReads(kind));

            // A schema changes under its own status: what is kept goes stale after a few minutes, and is read again.
            _clock.Advance(SchemaServiceReads.Keep + TimeSpan.FromSeconds(1));
            await new RecordPreviewer(runtime).PreviewAsync(null, schemaCheck: true);
            Assert.Equal(2, _platform.SchemaReads(kind));
        }
    }

    [Fact]
    public async Task A_kind_just_registered_is_seen_within_moments()
    {
        var (_, runtime) = await RuntimeAsync(registered: false);
        using (runtime)
        {
            var before = await new RecordPreviewer(runtime).PreviewAsync(null, schemaCheck: true);
            Assert.Equal(SchemaMatchStates.Unregistered, before.SchemaCheck!.Schema.State);

            _platform.Schemas[runtime.Mapping.Mapping.Kind] = (JsonObject)runtime.Mapping.Schema.Root.DeepClone();
            _clock.Advance(SchemaServiceReads.KeepAbsent + TimeSpan.FromSeconds(1));

            var after = await new RecordPreviewer(runtime).PreviewAsync(null, schemaCheck: true);
            Assert.Equal(SchemaMatchStates.Matches, after.SchemaCheck!.Schema.State);
        }
    }
}
