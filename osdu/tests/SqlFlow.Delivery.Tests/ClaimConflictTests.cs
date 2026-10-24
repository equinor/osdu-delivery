using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a record held for an OSDU id another record claimed says (osdu/docs/reference/concepts/submissions.md, One source,
/// several flows): whose the id is, and, when no repository declares that flow any more, the ledger a renamed flow keeps
/// with <c>ledger:</c> (osdu/docs/reference/flow/interfaces.md, Ledger identity).
/// </summary>
public sealed class ClaimConflictTests
{
    private static readonly Guid Owner = FlowId.Of("welldb-welllog");

    private static TargetIdConflict Conflict(string? flowName = "welldb-welllog") =>
        new(DeliveryKey.Derive("test", ["a"]), "dev:work-product-component--WellLog:a", Owner, flowName);

    [Fact]
    public void A_claim_of_a_declared_flow_says_how_to_part_the_two_flows()
    {
        Assert.Equal(
            $"OSDU id dev:work-product-component--WellLog:a is already claimed by flow 'welldb-welllog' ({Owner:D}), and one OSDU record belongs to one flow. "
            + "Deliver this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.",
            Conflict().Describe());
    }

    [Fact]
    public void A_claim_of_a_flow_no_repository_declares_names_the_ledger_a_renamed_flow_keeps()
    {
        var described = (Conflict() with { OwnerDeclared = false, OwnerLedger = "welldb-welllog" }).Describe();

        Assert.StartsWith(
            $"OSDU id dev:work-product-component--WellLog:a is already claimed by flow 'welldb-welllog' ({Owner:D}), which no repository declares any more, and one OSDU record belongs to one flow. ",
            described,
            StringComparison.Ordinal);
        Assert.Contains("keep its ledger with ledger: welldb-welllog in this flow's document (interfaces.<name>.ledger for an interface of a source)", described, StringComparison.Ordinal);
        Assert.EndsWith("Otherwise deliver this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.", described, StringComparison.Ordinal);

        // With no ledger name from the directory, the flow's name is the one to keep; with neither, nothing is suggested.
        Assert.Contains("ledger: welldb-welllog in", (Conflict() with { OwnerDeclared = false }).Describe(), StringComparison.Ordinal);
        var nameless = (Conflict(flowName: null) with { OwnerDeclared = false }).Describe();
        Assert.Contains($"already claimed by flow {Owner:D}, and one OSDU record belongs to one flow. Deliver this flow", nameless, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("welldb-welllog@test", "test", "welldb-welllog")]
    [InlineData("welldb-welllog@TEST", "test", "welldb-welllog")]
    [InlineData("welldb-welllog", "dev", "welldb-welllog")]
    [InlineData("petrel/logs@test", "test", "petrel/logs")]
    [InlineData("welldb-welllog@test", null, "welldb-welllog@test")]
    [InlineData("@test", "test", "@test")]
    public void A_ledger_is_kept_under_its_name_without_the_partition_a_binding_adds(string ledgerName, string? partition, string expected)
    {
        Assert.Equal(expected, LedgerEntry.OwnName(ledgerName, partition));
    }
}
