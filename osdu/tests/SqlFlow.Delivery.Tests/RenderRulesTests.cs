using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An ordinary scheduled run after the mapping it renders with was edited in place: the rows did not change, yet every record
/// has to be rendered again under the new rules, and only what renders differently is sent. An edit that cannot change a
/// record (a comment) leaves the run incremental. A record held at its render is rendered again under the new rules, so the
/// error it is held for is the one the mapping gives now; a record held keeping its document is left as it is.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RenderRulesTests : IDisposable
{
    /// <summary>The log whose contractor the estate leaves empty: held at its render, keeping no document.</summary>
    private const int EmptyContractor = 2;

    /// <summary>The log whose contractor the mapping's assertion refuses: held by the check before sending, keeping its document.</summary>
    private const int RefusedContractor = 1;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_mapping_fingerprint_moves_with_what_renders_and_not_with_comments()
    {
        var original = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        var fingerprint = MappingFingerprint.Of(original, "original");

        Assert.Equal(fingerprint, MappingFingerprint.Of("# A note.\n" + original.Replace("\r\n", "\n", StringComparison.Ordinal), "commented"));
        Assert.NotEqual(fingerprint, MappingFingerprint.Of(original.Replace("DeliveredBy: osdu-delivery", "DeliveredBy: someone-else", StringComparison.Ordinal), "tag"));
        Assert.Equal(fingerprint, new DeliveryDocumentLoader().ParseMapping(original).Fingerprint);
    }

    [Fact]
    public async Task A_scheduled_run_after_a_mapping_edit_renders_every_record_again_and_sends_only_what_changed()
    {
        var original = await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        using var estate = await EstateAsync(original);
        var logs = SampleEstate.Logs().Count;

        Assert.Equal(logs, (await estate.RunAsync()).Delivered);
        var quiet = await estate.RunAsync();
        Assert.Equal((0, 0L), (quiet.Delivered, quiet.RecordCount));

        // A comment says nothing about a record: the run stays incremental and reads nothing.
        await estate.WriteMappingAsync("# Reviewed.\n" + original);
        var commented = await estate.RunAsync();
        Assert.Equal((0, 0L), (commented.Delivered, commented.RecordCount));

        // A parameter's description moves the rules and renders the same records: every row is read again, nothing is sent.
        var described = original.Replace(
            "description: The legal tag every record this mapping renders carries (legal.legaltags).",
            "description: The legal tag on every record this mapping renders (legal.legaltags).",
            StringComparison.Ordinal);
        Assert.NotEqual(original, described);
        await estate.WriteMappingAsync(described);
        var reread = await estate.RunAsync();
        Assert.Equal((0, (long)logs), (reread.Delivered, reread.RecordCount));
        Assert.Equal(0, (await estate.RunAsync()).RecordCount);

        // A value every record carries: the next ordinary run sends every record again, and the one after it nothing.
        await estate.WriteMappingAsync(described.Replace("DeliveredBy: osdu-delivery", "DeliveredBy: osdu-delivery-rules", StringComparison.Ordinal));
        estate.Protocol.Deliveries.Clear();
        var edited = await estate.RunAsync();
        Assert.Equal(logs, edited.Delivered);
        Assert.Equal(logs, estate.Protocol.Deliveries.Count);
        Assert.All(estate.Protocol.Deliveries, d => Assert.Equal("osdu-delivery-rules", d.Document!["tags"]!["DeliveredBy"]!.GetValue<string>()));
        var after = await estate.RunAsync();
        Assert.Equal((0, 0L), (after.Delivered, after.RecordCount));
    }

    [Fact]
    public async Task A_record_held_at_its_render_is_held_again_with_the_error_of_the_mapping_that_replaced_its_own()
    {
        var v1 = WithContractorTag("lra");
        using var estate = await EstateAsync(v1, ContractorsOf);
        var logs = SampleEstate.Logs().Count;

        // Version 1: the log with no contractor is held at its render, naming the tag version 1 fills; the refused one is held
        // by the check before sending, keeping its document. Every other log is delivered.
        var first = await estate.RunAsync();
        Assert.Equal(logs - 2, first.Delivered);
        var heldUnderV1 = await estate.RecordAsync(EmptyContractor);
        Assert.Equal((RecordStatus.Held, true, (string?)null), (heldUnderV1.Status, heldUnderV1.Blocked, heldUnderV1.PendingDocumentRef));
        Assert.Contains("tags['lra.securityClassification']", heldUnderV1.LastError, StringComparison.Ordinal);
        Assert.NotNull(heldUnderV1.HeldRulesHash);
        var refused = await estate.RecordAsync(RefusedContractor);
        Assert.Equal((RecordStatus.Held, true), (refused.Status, refused.Blocked));
        Assert.NotNull(refused.PendingDocumentRef);
        Assert.StartsWith("assertion:", refused.LastError, StringComparison.Ordinal);
        Assert.Contains("tags['lra.securityClassification']", refused.LastError, StringComparison.Ordinal);

        // Nothing changed: the run reads nothing, and both stay held as they are.
        Assert.Equal(0, (await estate.RunAsync()).RecordCount);
        Assert.Equal(heldUnderV1.UpdatedUtc, (await estate.RecordAsync(EmptyContractor)).UpdatedUtc);

        // Version 2 renames the tag, as an estate renamed lra.* to dra.*. The run reads every row again under the new rules: the
        // record held at its render is rendered again and held for what version 2 says, under another issue; the record held
        // keeping its document keeps it, and the error it was held for, until a release sends it or its row changes.
        _clock.Advance(TimeSpan.FromMinutes(10));
        await estate.WriteMappingAsync(WithContractorTag("dra"));
        estate.Protocol.Deliveries.Clear();
        var second = await estate.RunAsync();
        Assert.Equal(logs, second.RecordCount);
        Assert.Equal(logs - 2, second.Delivered);
        Assert.DoesNotContain(estate.Protocol.Deliveries, d => d.Key == SampleEstate.Key(EmptyContractor) || d.Key == SampleEstate.Key(RefusedContractor));

        var heldUnderV2 = await estate.RecordAsync(EmptyContractor);
        Assert.Equal((RecordStatus.Held, true, (string?)null), (heldUnderV2.Status, heldUnderV2.Blocked, heldUnderV2.PendingDocumentRef));
        Assert.Contains("tags['dra.securityClassification']", heldUnderV2.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("lra.", heldUnderV2.LastError, StringComparison.Ordinal);
        Assert.NotEqual(heldUnderV1.HeldRulesHash, heldUnderV2.HeldRulesHash);
        Assert.NotEqual(heldUnderV1.ProblemHash, heldUnderV2.ProblemHash);
        Assert.True(heldUnderV2.UpdatedUtc > heldUnderV1.UpdatedUtc);
        var renders = (await estate.Ledger.ListAttemptsAsync(estate.FlowId, SampleEstate.Key(EmptyContractor), 20))
            .Where(a => a.Outcome == AttemptOutcome.Held)
            .ToList();
        Assert.Equal(2, renders.Count);

        var stillRefused = await estate.RecordAsync(RefusedContractor);
        Assert.Equal(
            (refused.Status, refused.Blocked, refused.PendingDocumentRef, refused.LastError, refused.UpdatedUtc),
            (stillRefused.Status, stillRefused.Blocked, stillRefused.PendingDocumentRef, stillRefused.LastError, stillRefused.UpdatedUtc));

        // Held under the rules the flow renders with now: the next run reads nothing and renders nothing again.
        Assert.Equal(0, (await estate.RunAsync()).RecordCount);
        Assert.Equal(heldUnderV2.UpdatedUtc, (await estate.RecordAsync(EmptyContractor)).UpdatedUtc);
    }

    [Fact]
    public async Task A_hold_made_before_the_ledger_kept_its_rules_is_rendered_again_by_a_replan_and_once_only()
    {
        using var estate = await EstateAsync(WithContractorTag("dra"), ContractorsOf);
        await estate.RunAsync();
        var held = await estate.RecordAsync(EmptyContractor);
        Assert.NotNull(held.HeldRulesHash);

        // A record held before module version 1.75.0 carries no rules.
        await using (var db = _db.CreateDbContext())
        {
            var key = SampleEstate.Key(EmptyContractor).Value;
            Assert.Equal(1, await db.DeliveryRecords
                .Where(r => r.FlowId == estate.FlowId && r.DeliveryKey == key)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HeldRulesHash, (string?)null)));
        }

        // An ordinary run reads only what changed, so it does not meet the row; a replan reads every row and renders the
        // record again, since no one knows the rules it was held under, and keeps the ones it is held under now.
        Assert.Equal(0, (await estate.RunAsync()).RecordCount);
        Assert.Null((await estate.RecordAsync(EmptyContractor)).HeldRulesHash);

        _clock.Advance(TimeSpan.FromMinutes(10));
        await estate.RunAsync(DeliveryOperations.Replan);
        var again = await estate.RecordAsync(EmptyContractor);
        Assert.Equal((RecordStatus.Held, true), (again.Status, again.Blocked));
        Assert.Equal(held.HeldRulesHash, again.HeldRulesHash);
        Assert.Equal(held.LastError, again.LastError);
        Assert.True(again.UpdatedUtc > held.UpdatedUtc);

        // The next replan meets it held under the rules it renders with, and leaves it.
        _clock.Advance(TimeSpan.FromMinutes(10));
        await estate.RunAsync(DeliveryOperations.Replan);
        Assert.Equal(again.UpdatedUtc, (await estate.RecordAsync(EmptyContractor)).UpdatedUtc);
    }

    /// <summary>
    /// The sample mapping with a tag whose name holds a dot (<paramref name="prefix"/><c>.securityClassification</c>), read from
    /// the log's contractor, a column the sample mapping does not read, and an assertion refusing one contractor.
    /// </summary>
    private static string WithContractorTag(string prefix)
    {
        var original = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string anchor = "\n    DeliveredBy: osdu-delivery\n";
        Assert.Contains(anchor, original, StringComparison.Ordinal);
        return original.Replace(
            anchor,
            anchor
                + $"    {prefix}.securityClassification:\n"
                + "      $from: logging_contractor\n"
                + "      $assert:\n"
                + "        - name: contractor-accepted\n"
                + "          notEquals: Rejected Co\n",
            StringComparison.Ordinal);
    }

    /// <summary>Every log a contractor, but one with none and one the assertion refuses.</summary>
    private static void ContractorsOf(MemoryIngestionTables tables)
    {
        for (var i = 0; i < tables.Records.Count; i++)
        {
            tables.Records[i].Row["logging_contractor"] = i switch
            {
                EmptyContractor => null,
                RefusedContractor => "Rejected Co",
                _ => "Acme Logging",
            };
        }
    }

    private async Task<Estate> EstateAsync(string mapping, Action<MemoryIngestionTables>? rows = null)
    {
        var mappings = Path.Combine(_root, "mappings");
        Directory.CreateDirectory(mappings);
        var file = Path.Combine(mappings, "WellLog@1.4.0.yaml");
        await File.WriteAllTextAsync(file, mapping);

        // Rows written an hour ago, well before the overlap below the watermark, so a run after the first reads none of them.
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddHours(-1), time: _clock);
        rows?.Invoke(tables);
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        var protocol = new FakeProtocol();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, new FakeProtocolFactory(protocol), cache: caches, sources: tables);
        var provider = new ServiceCollection().AddSingleton(engine).AddSingleton<PartitionLedgers>().BuildServiceProvider();
        var flow = Samples.InFolder(Samples.LocalFlow(_root), _root);
        flow = flow with { Name = "rules-welllog-" + _suffix, Render = flow.Render with { MappingsDirectory = mappings } };
        return new Estate(flow, file, protocol, ledger, provider);
    }

    /// <summary>The sample estate, delivered by a flow rendering with a mapping file of its own, run as a schedule runs it.</summary>
    private sealed class Estate(FlowDefinition flow, string mappingFile, FakeProtocol protocol, OsduLedger ledger, ServiceProvider provider) : IDisposable
    {
        private Guid? _flowId;

        public FakeProtocol Protocol => protocol;

        public OsduLedger Ledger => ledger;

        /// <summary>The flow's ledger, as its first run registered it.</summary>
        public Guid FlowId => _flowId ?? throw new InvalidOperationException("The flow has not run yet, so no ledger holds it.");

        public Task WriteMappingAsync(string yaml) => File.WriteAllTextAsync(mappingFile, yaml);

        public async Task<DeliverOutcome> RunAsync(string operation = DeliveryOperations.Deliver)
        {
            var result = await new DeliveryExecutor(provider).ExecuteAsync(
                new DeliveryFlowDocument { Source = SourceDefinition.Of(flow) },
                flow.SourcePath!,
                new DocumentExecutionOptions
                {
                    Parameters = new RunParameters { Operation = operation, Values = new Dictionary<string, string>(SampleEstate.Values, StringComparer.Ordinal) },
                },
                CancellationToken.None);
            Assert.True(result.Success, result.Error);
            _flowId ??= Assert.Single(await ledger.ListLedgersAsync(null), l => string.Equals(l.FlowName, flow.Name, StringComparison.Ordinal)).FlowId;
            return Assert.IsType<DeliverOutcome>(result.Result);
        }

        public async Task<RecordState> RecordAsync(int log)
            => await ledger.GetRecordAsync(FlowId, SampleEstate.Key(log)) ?? throw new InvalidOperationException($"The ledger holds no record of log {log}.");

        public void Dispose() => provider.Dispose();
    }
}
