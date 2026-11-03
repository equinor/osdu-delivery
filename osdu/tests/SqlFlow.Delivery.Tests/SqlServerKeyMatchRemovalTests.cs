using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// SQLFlow's key match beside a removal flow, on the chain's real SQL Server (osdu/docs/reference/flow/removal-marks.md):
/// the ingestion flow tags a row whose key left its source and un-tags it when the key is back, the delivery flow leaves
/// tagged rows out with a filter, and a removal flow that reads the tags alone takes their records out of OSDU. The flows
/// are the shipped chain's, run through the platform's document executor, with the key match, the filter and the removal
/// flow added to the generated estate.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerKeyMatchRemovalTests
{
    private const string IngestionFlow = "welldb-welllog-02-header-ing";

    private const string DeliveryFlow = "welldb-welllog-03-header-delivery";

    private const string RemovalFlow = "welldb-welllog-05-removal";

    [Fact]
    public async Task A_row_the_key_match_un_tags_is_delivered_again_by_the_next_delivery_and_the_removal_then_lifts_its_deletion()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        Rewrite(estate, IngestionFlow, "load:\n  keyColumns: [source_project, log_id]\n",
            "load:\n  keyColumns: [source_project, log_id]\n  matchKeysInSourceAndTarget: true\n\nmatchKeys:\n  action: tag\n  thresholdPercent: 100\n");
        Rewrite(estate, DeliveryFlow, "    primaryKey: RecId\n", "    primaryKey: RecId\n    filter: AND DeletedDate_DW IS NULL\n");
        await File.WriteAllTextAsync(estate.FlowFile(RemovalFlow), RemovalDocument(estate));

        var logs = SampleWellLogs.Logs();
        var gone = logs[0];
        await estate.WriteLogFileAsync("welllog_20260901.csv", logs);
        await estate.WriteCurveFileAsync("welllog_curves_20260901.csv", logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        Assert.Equal(logs.Count, estate.Protocol.Deliveries.Count);
        var target = (await estate.Ledger.GetRecordAsync(estate.FlowId, gone.Key))!.TargetId;

        // The source no longer holds the first log, as a landing loaded whole from the source's current cut would not: the
        // key match tags its row, the filtered delivery leaves it out, and the removal takes its record out of OSDU.
        await DeleteLandedAsync(estate, gone);
        await estate.RunIngestionChainAsync();
        Assert.NotNull(await estate.ValueAsync("WellLog", "DeletedDate_DW", gone.SourceProject, gone.LogId));
        var loaded = Assert.IsType<DateTime>(await estate.ValueAsync("WellLog", "UpdatedDate_DW", gone.SourceProject, gone.LogId));
        estate.Protocol.Deliveries.Clear();
        await estate.DeliverAsync();
        Assert.Empty(estate.Protocol.Deliveries);

        await estate.RunFlowAsync(RemovalFlow);
        Assert.Equal(target, Assert.Single(estate.Protocol.Deletes).TargetId);
        Assert.Equal(RecordStatus.Deleted, (await estate.Ledger.GetRecordAsync(estate.FlowId, gone.Key))!.Status);
        var deletion = Assert.Single(await estate.Ledger.ListRemovedKeysAsync(estate.FlowId, null, null, null, 10));
        Assert.Equal((gone.Key, SourceRemovals.Marked, SourceRemovals.Done), (deletion.Key, deletion.Reason, deletion.State));

        // The log is back in the source as it was. The upsert finds nothing changed in its row; the key match un-tags it and
        // stamps it updated, so the delivery's next run reads it, finds it newer than its removal and sends it again, whole,
        // under the id it had, before the removal flow runs. The removal's next run lifts the deletion the record outlived.
        await estate.WriteLogFileAsync("welllog_20260903.csv", logs);
        await estate.RunIngestionChainAsync();
        Assert.Null(await estate.ValueAsync("WellLog", "DeletedDate_DW", gone.SourceProject, gone.LogId));
        Assert.True(Assert.IsType<DateTime>(await estate.ValueAsync("WellLog", "UpdatedDate_DW", gone.SourceProject, gone.LogId)) > loaded);

        await estate.DeliverAsync();
        var sent = Assert.Single(estate.Protocol.Deliveries);
        Assert.Equal((gone.Key, target, (long?)null, true), (sent.Key, sent.TargetId, sent.ExistingVersion, sent.DeliverMetadata));
        Assert.Equal(RecordStatus.Delivered, (await estate.Ledger.GetRecordAsync(estate.FlowId, gone.Key))!.Status);

        await estate.RunFlowAsync(RemovalFlow);
        Assert.Empty(await estate.Ledger.ListRemovedKeysAsync(estate.FlowId, null, null, null, 10));
        Assert.Single(estate.Protocol.Deletes);
    }

    /// <summary>
    /// A removal of the tags alone over the chain's delivery flow, in its scope. The fake target answers no search, so the
    /// check for records still naming one taken out is left off.
    /// </summary>
    private static string RemovalDocument(SqlServerIngestionFixture estate) => $$"""
        flowType: removal
        name: {{estate.FlowPrefix}}-removal
        partitions: [{{Samples.SamplePartition}}]
        removals:
          - name: logs
            flow: {{estate.DeliveryFlowName}}
            values: { logSource: {{SampleWellLogs.LogSource}} }
            keepReferenced: false
        """;

    /// <summary>Rewrites one piece of a generated flow document, failing when the document no longer holds it.</summary>
    private static void Rewrite(SqlServerIngestionFixture estate, string flow, string old, string replacement)
    {
        var file = estate.FlowFile(flow);
        var text = File.ReadAllText(file);
        Assert.True(text.Contains(old, StringComparison.Ordinal), $"{flow}.yaml no longer holds the text the test rewrites: {old.Trim()}");
        File.WriteAllText(file, text.Replace(old, replacement, StringComparison.Ordinal));
    }

    /// <summary>Deletes a log's rows from the pre table, which then holds what a source that dropped the log lands.</summary>
    private static async Task DeleteLandedAsync(SqlServerIngestionFixture estate, SampleLog log)
    {
        await using var connection = new SqlConnection(estate.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET QUOTED_IDENTIFIER ON; DELETE FROM [{estate.PreSchema}].[WellLog] WHERE [source_project] = @project AND [log_id] = @log;";
        command.Parameters.AddWithValue("@project", log.SourceProject);
        command.Parameters.AddWithValue("@log", log.LogId);
        Assert.True(await command.ExecuteNonQueryAsync() > 0, $"the pre table held no row of log {log.LogId}");
    }
}
