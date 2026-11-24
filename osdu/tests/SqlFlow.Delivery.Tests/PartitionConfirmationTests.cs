using SqlFlow.Delivery.Engine;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The partition an operator types back before an adoption, a removal, new OSDU ids or a ledger's deletion
/// (<see cref="PartitionConfirmation"/>), and the reach of a release or an adoption (<see cref="RecordReach"/>): one rule
/// for every client, so a confirmation the API takes the CLI takes too.
/// </summary>
public class PartitionConfirmationTests
{
    [Theory]
    [InlineData("dev", "dev")]
    [InlineData("  dev ", "dev")]
    [InlineData("DEV", "dev")]
    [InlineData("Opendes", "opendes")]
    public void A_partition_typed_back_is_read_trimmed_and_case_aside(string typed, string partition)
    {
        Assert.True(PartitionConfirmation.Names(typed, partition));
        Assert.Equal(typed.Trim(), PartitionConfirmation.Typed(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_confirms_nothing(string? typed)
    {
        Assert.Null(PartitionConfirmation.Typed(typed));
        Assert.False(PartitionConfirmation.Names(typed, "dev"));
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("dev2")]
    [InlineData("de v")]
    public void Another_partition_confirms_nothing(string typed) => Assert.False(PartitionConfirmation.Names(typed, "dev"));

    [Fact]
    public void An_empty_list_of_keys_reaches_no_record_and_every_record_is_a_reach_of_its_own()
    {
        Assert.Empty(Assert.IsType<RecordReach.NamedRecords>(RecordReach.Named([])).Keys);

        var named = Assert.IsType<RecordReach.NamedRecords>(RecordReach.Named([Identity.DeliveryKey.Derive("reach", ["W-1"])]));
        Assert.Single(named.Keys);
        Assert.IsType<RecordReach.EveryRecord>(RecordReach.Every);
    }
}
