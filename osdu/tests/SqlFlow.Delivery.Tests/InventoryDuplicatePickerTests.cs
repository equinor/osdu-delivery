using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How complete a copy is and which copy of a group of duplicates is kept (osdu/docs/reference/flow/inventory-duplicates.md, Which copy is
/// kept): the count of values under <c>data</c>, the copy holding the most kept, a ledger's copy before another, the rest picked
/// to remove where the flow may, and every case the rules leave to an operator, each with the reason a page shows.
/// </summary>
public sealed class InventoryDuplicatePickerTests
{
    private static readonly IReadOnlySet<string> Orphans = new HashSet<string>(StringComparer.Ordinal) { InventoryFindings.Orphan };

    private static DuplicateCopy Copy(long row, int attributes, string finding = InventoryFindings.Orphan, string state = InventoryDuplicates.Held, long? named = 0)
        => new(row, $"dev:work-product-component--WellLog:{row}", finding, state, 3, attributes, named);

    private static string RoleOf(InventoryDuplicateJudgement judgement, long row) => judgement.Copies.Single(c => c.InventoryRecordId == row).Role;

    private static string ReasonOf(InventoryDuplicateJudgement judgement, long row) => judgement.Copies.Single(c => c.InventoryRecordId == row).Reason!;

    [Fact]
    public void Every_value_under_data_counts_once_each_item_of_a_list_on_its_own_and_empty_text_none()
    {
        Assert.Equal(0, InventoryDuplicatePicker.Attributes(null));
        Assert.Equal(0, InventoryDuplicatePicker.Attributes(JsonNode.Parse("""{"A":null,"B":"  ","C":[],"D":{}}""")));
        Assert.Equal(6, InventoryDuplicatePicker.Attributes(JsonNode.Parse("""{"Name":"GR","Top":0,"Ok":false,"Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT","Unit":"m"}]}""")));

        // The link the Wellbore DDMS writes when it keeps a record's bulk data, and its dataset entry, are values like any other.
        var bare = JsonNode.Parse("""{"Name":"GR","WellboreID":"wb-1"}""");
        var withBulk = JsonNode.Parse("""{"Name":"GR","WellboreID":"wb-1","DDMSDatasets":["urn://wdms-1/uuid:1"],"ExtensionProperties":{"wdms":{"bulkURI":"urn://wdms-1/uuid:1"}}}""");
        Assert.Equal(InventoryDuplicatePicker.Attributes(bare) + 2, InventoryDuplicatePicker.Attributes(withBulk));
    }

    [Fact]
    public void The_copy_holding_the_most_values_is_kept_and_the_others_the_flow_may_remove_are_picked_with_why()
    {
        var judged = InventoryDuplicatePicker.Judge(1, [Copy(1, 5), Copy(2, 12), Copy(3, 7)], Orphans, chosen: null);

        Assert.Equal((InventoryDuplicates.Picked, 2L), (judged.Outcome, judged.KeptInventoryRecordId));
        Assert.Equal("it holds the most values: 12", judged.Reason);
        Assert.Equal(InventoryDuplicates.Keep, RoleOf(judged, 2));
        Assert.Equal((InventoryDuplicates.Remove, InventoryDuplicates.Remove), (RoleOf(judged, 1), RoleOf(judged, 3)));
        Assert.Equal("it holds 5 values, and the copy kept holds 12", ReasonOf(judged, 1));
    }

    [Fact]
    public void A_copy_the_inventory_may_not_remove_is_left_with_why()
    {
        var policy = new HashSet<string>(StringComparer.Ordinal) { InventoryFindings.Orphan, InventoryFindings.Stale };
        var judged = InventoryDuplicatePicker.Judge(
            1, [Copy(1, 9), Copy(2, 3, InventoryFindings.Foreign), Copy(3, 3, InventoryFindings.Forgotten), Copy(4, 3, InventoryFindings.Unreconciled), Copy(5, 3, InventoryFindings.Stale)],
            policy, chosen: null);

        Assert.Equal(InventoryDuplicates.Picked, judged.Outcome);
        Assert.StartsWith("another identity created it, so only its owner can remove it", ReasonOf(judged, 2), StringComparison.Ordinal);
        Assert.StartsWith("the flow allows removing orphan, stale ids, not forgotten ones", ReasonOf(judged, 3), StringComparison.Ordinal);
        Assert.StartsWith("the inventory finds it unreconciled, and only orphan, stale and forgotten ids", ReasonOf(judged, 4), StringComparison.Ordinal);
        Assert.Equal(
            [InventoryDuplicates.Keep, InventoryDuplicates.Leave, InventoryDuplicates.Leave, InventoryDuplicates.Leave, InventoryDuplicates.Remove],
            judged.Copies.Select(c => c.Role));

        var readOnly = InventoryDuplicatePicker.Judge(1, [Copy(1, 9), Copy(2, 3)], new HashSet<string>(StringComparer.Ordinal), chosen: null);
        Assert.StartsWith("the flow declares no removal, so it only reads OSDU", ReasonOf(readOnly, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void A_ledger_s_copy_is_kept_before_another_holding_as_many_values_and_left_for_review_when_another_holds_more()
    {
        var tie = InventoryDuplicatePicker.Judge(1, [Copy(1, 8), Copy(2, 8, InventoryFindings.Tracked)], Orphans, chosen: null);
        Assert.Equal((InventoryDuplicates.Picked, 2L), (tie.Outcome, tie.KeptInventoryRecordId));
        Assert.Equal("a ledger holds it (tracked), and no copy holds more values than its 8", tie.Reason);

        var fuller = InventoryDuplicatePicker.Judge(1, [Copy(1, 14), Copy(2, 8, InventoryFindings.Drifted)], Orphans, chosen: null);
        Assert.Equal((InventoryDuplicates.Review, (long?)null), (fuller.Outcome, fuller.KeptInventoryRecordId));
        Assert.StartsWith("the copy a ledger holds, dev:work-product-component--WellLog:2, holds 8 values and dev:work-product-component--WellLog:1 holds 14", fuller.Reason, StringComparison.Ordinal);
        Assert.All(fuller.Copies, c => Assert.Equal(InventoryDuplicates.ForReview, c.Role));

        var two = InventoryDuplicatePicker.Judge(1, [Copy(1, 8, InventoryFindings.Tracked), Copy(2, 8, InventoryFindings.Unconfirmed), Copy(3, 2)], Orphans, chosen: null);
        Assert.Equal(InventoryDuplicates.Review, two.Outcome);
        Assert.StartsWith("2 copies are held by a ledger", two.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_copies_holding_the_most_values_are_left_for_an_operator_and_nothing_of_the_group_is_removed()
    {
        var judged = InventoryDuplicatePicker.Judge(4, [Copy(1, 8), Copy(2, 8), Copy(3, 2)], Orphans, chosen: null);
        Assert.Equal((4, InventoryDuplicates.Review), (judged.GroupNumber, judged.Outcome));
        Assert.StartsWith("2 copies hold the most values (8 each:", judged.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(judged.Copies, c => c.Role == InventoryDuplicates.Remove);
    }

    [Fact]
    public void A_copy_picked_to_remove_that_other_records_name_or_that_could_not_be_counted_leaves_the_group_for_review()
    {
        var named = InventoryDuplicatePicker.Judge(1, [Copy(1, 9), Copy(2, 3, named: 4)], Orphans, chosen: null);
        Assert.Equal(InventoryDuplicates.Review, named.Outcome);
        Assert.StartsWith("dev:work-product-component--WellLog:2, picked to remove, is named by 4 record(s)", named.Reason, StringComparison.Ordinal);

        // The copy kept may be named as often as it is: it stays.
        Assert.Equal(InventoryDuplicates.Picked, InventoryDuplicatePicker.Judge(1, [Copy(1, 9, named: 30), Copy(2, 3)], Orphans, chosen: null).Outcome);

        var uncounted = InventoryDuplicatePicker.Judge(1, [Copy(1, 9), new DuplicateCopy(2, "dev:x:2", InventoryFindings.Orphan, InventoryDuplicates.Held, 1, 3, null, "the search refused it")], Orphans, chosen: null);
        Assert.Equal(InventoryDuplicates.Review, uncounted.Outcome);
        Assert.Contains("could not count the records naming dev:x:2, picked to remove (the search refused it)", uncounted.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Copies_storage_no_longer_holds_are_gone_a_group_left_with_one_is_settled_and_an_unanswered_copy_leaves_it_for_review()
    {
        var one = InventoryDuplicatePicker.Judge(1, [Copy(1, 9), Copy(2, 3, state: InventoryDuplicates.Missing)], Orphans, chosen: null);
        Assert.Equal((InventoryDuplicates.Settled, 1L), (one.Outcome, one.KeptInventoryRecordId));
        Assert.Equal((InventoryDuplicates.Keep, InventoryDuplicates.Gone), (RoleOf(one, 1), RoleOf(one, 2)));

        var none = InventoryDuplicatePicker.Judge(1, [Copy(1, 9, state: InventoryDuplicates.Missing), Copy(2, 3, state: InventoryDuplicates.Missing)], Orphans, chosen: null);
        Assert.Equal((InventoryDuplicates.Settled, (long?)null), (none.Outcome, none.KeptInventoryRecordId));

        var gone = InventoryDuplicatePicker.Judge(1, [Copy(1, 9), Copy(2, 3), Copy(3, 1, state: InventoryDuplicates.Missing)], Orphans, chosen: null);
        Assert.Equal((InventoryDuplicates.Picked, InventoryDuplicates.Gone), (gone.Outcome, RoleOf(gone, 3)));

        var unanswered = InventoryDuplicatePicker.Judge(1, [Copy(1, 9), Copy(2, 3, state: InventoryDuplicates.Unanswered)], Orphans, chosen: null);
        Assert.Equal(InventoryDuplicates.Review, unanswered.Outcome);
        Assert.Contains("storage did not say whether it holds", unanswered.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_s_choice_keeps_the_copy_chosen_whatever_the_rules_say_and_names_what_it_leaves_pointing_at_nothing()
    {
        var chosen = InventoryDuplicatePicker.Judge(1, [Copy(1, 8), Copy(2, 8), Copy(3, 2, named: 5), Copy(4, 9, InventoryFindings.Tracked)], Orphans, chosen: 2);
        Assert.Equal((InventoryDuplicates.Chosen, 2L), (chosen.Outcome, chosen.KeptInventoryRecordId));
        Assert.Equal("chosen by an operator", ReasonOf(chosen, 2));
        Assert.Equal((InventoryDuplicates.Remove, InventoryDuplicates.Remove), (RoleOf(chosen, 1), RoleOf(chosen, 3)));
        Assert.EndsWith("5 record(s) name it, and are left pointing at nothing once it goes", ReasonOf(chosen, 3), StringComparison.Ordinal);
        Assert.Equal(InventoryDuplicates.Leave, RoleOf(chosen, 4));
        Assert.StartsWith("a ledger holds it (tracked): its delivery flow writes it", ReasonOf(chosen, 4), StringComparison.Ordinal);

        // A copy storage no longer holds, or one of another group, cannot be chosen.
        var missing = InventoryDuplicatePicker.Judge(1, [Copy(1, 8), Copy(2, 3, state: InventoryDuplicates.Missing)], Orphans, chosen: 2);
        Assert.Equal(InventoryDuplicates.Review, missing.Outcome);
        Assert.Contains("is no longer held by storage; choose another", missing.Reason, StringComparison.Ordinal);
        Assert.Equal(InventoryDuplicates.Review, InventoryDuplicatePicker.Judge(1, [Copy(1, 8), Copy(2, 3)], Orphans, chosen: 99).Outcome);
    }

    [Fact]
    public void A_reason_is_cut_to_what_the_database_keeps()
    {
        Assert.Equal(Data.DeliveryModel.MaxDuplicateReasonLength, InventoryDuplicates.Reason(new string('x', 5000))!.Length);
        Assert.Null(InventoryDuplicates.Reason(null));
        Assert.Throws<ArgumentException>(() => InventoryDuplicatePicker.Judge(1, [], Orphans, chosen: null));
    }
}
