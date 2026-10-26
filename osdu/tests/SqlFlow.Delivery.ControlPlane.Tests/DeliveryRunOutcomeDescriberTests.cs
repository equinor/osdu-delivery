using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.Delivery.ControlPlane.Background;
using Xunit;

namespace SqlFlow.Delivery.ControlPlane.Tests;

/// <summary>
/// What a trigger request's trace and a GitHub job's summary say a delivery run did: its records, read from the result its
/// run recorded, in the words the run page uses; and nothing for what it does not describe, so the row counts speak instead.
/// </summary>
public sealed class DeliveryRunOutcomeDescriberTests
{
    private static readonly DeliveryRunOutcomeDescriber Describer = new();

    [Fact]
    public void A_delivery_says_what_it_planned_and_what_became_of_the_records()
    {
        var text = Describer.Describe(Run(new
        {
            operation = "deliver", planned = 1840, skippedUnchanged = 12, delivered = 1828, held = 0, failed = 3, retried = 2, waiting = 0,
            nothingToDo = false,
        }));

        Assert.Equal("1,840 planned: 1,828 delivered, 12 unchanged, 2 retrying, 3 failed", text);
    }

    [Fact]
    public void A_delivery_with_nothing_to_do_says_so()
        => Assert.Equal("nothing to deliver", Describer.Describe(Run(new { operation = "deliver", delivered = 0, nothingToDo = true })));

    [Fact]
    public void A_drain_says_what_it_settled()
        => Assert.Equal("40 delivered, 5 unchanged, 1 held", Describer.Describe(Run(new { processed = 46, delivered = 40, unchanged = 5, held = 1, failed = 0 })));

    [Fact]
    public void A_source_adds_its_interfaces_up_and_says_how_many_completed()
    {
        var text = Describer.Describe(Run(new
        {
            operation = "deliver",
            source = "welldb",
            interfaces = new object[]
            {
                new { @interface = "wells", state = "completed", result = new { planned = 10, delivered = 10 } },
                new { @interface = "wellbores", state = "completed", result = new { planned = 20, delivered = 18, failed = 2 } },
                new { @interface = "logs", state = "skipped", result = (object?)null },
            },
        }));

        Assert.Equal("30 planned: 28 delivered, 2 failed; 2 of 3 interfaces completed", text);
    }

    [Theory]
    [InlineData("plan", "{\"records\":10,\"deliveries\":4}")]
    [InlineData("verify", "{\"checked\":10,\"matched\":10}")]
    public void Operations_that_deliver_nothing_are_left_to_the_row_counts(string operation, string json)
    {
        var run = new CatalogRun { FlowKind = "delivery", FlowName = operation, ResultJson = json };

        Assert.Null(Describer.Describe(run));
    }

    [Fact]
    public void Other_kinds_and_runs_without_a_result_are_left_to_the_row_counts()
    {
        Assert.Null(Describer.Describe(new CatalogRun { FlowKind = "ing", ResultJson = "{\"delivered\":3}" }));
        Assert.Null(Describer.Describe(new CatalogRun { FlowKind = "delivery", ResultJson = null }));
    }

    [Fact]
    public void A_result_that_is_not_JSON_throws_for_the_caller_to_log()
        => Assert.ThrowsAny<JsonException>(() => Describer.Describe(new CatalogRun { FlowKind = "delivery", ResultJson = "{not json" }));

    private static CatalogRun Run(object result)
        => new() { RunId = Guid.NewGuid(), FlowName = "welldb-well-03-delivery", FlowKind = "delivery", ResultJson = JsonSerializer.Serialize(result) };
}
