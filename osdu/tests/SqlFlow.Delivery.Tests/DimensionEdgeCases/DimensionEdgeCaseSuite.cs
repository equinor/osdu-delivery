namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The dimension engine's edge-case suite: every setting of a dimension's attributes, elements, lookups and incremental
/// loads, and every rule of its views' grouping, aggregates and joins to views, at its boundaries and against SQL Server.
/// It is a suite of its own, run on demand (<c>bash tools/test.sh --suite DimensionEdgeCases</c>, CONTRIBUTING.md,
/// Tests): the unit and full tiers leave out every test that carries its trait, <c>[Trait("Suite", "DimensionEdgeCases")]</c>.
/// A class of it that needs SQL Server is an integration test of the <see cref="SqlServerSuite"/> as well, as every other is.
/// </summary>
public static class DimensionEdgeCaseSuite
{
    /// <summary>The trait an on-demand suite is named by, beside <see cref="TestTierTests.Category"/>.</summary>
    public const string Trait = "Suite";

    /// <summary>The value of <see cref="Trait"/> for this suite.</summary>
    public const string Name = "DimensionEdgeCases";
}
