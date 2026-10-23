using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The children of a run's records looked up in storage (osdu/docs/reference/flow/mapping-child.md, How a run looks for a
/// child; openapi storage v2, <c>POST /query/records</c>): by id, in the same requests as the parents of the same round, each
/// id once; a child storage does not hand over is an answer, and a record of another entity type under a child's id fails
/// the read rather than passing for the child.
/// </summary>
public class ChildRecordReadTests
{
    private const string ChildKind = LinkedTemplates.CustomKind;

    private static ChildQuestion Question(string name) => new(LinkedTemplates.RecordId(name), ChildKind);

    private static JsonObject Child(string name, long version, string kind = ChildKind) => new()
    {
        ["id"] = LinkedTemplates.RecordId(name),
        ["kind"] = kind,
        ["version"] = version,
        ["data"] = new JsonObject { ["Big"] = new string('x', 1000) },
    };

    [Fact]
    public async Task A_child_is_looked_up_by_its_id_and_kept_as_whether_storage_holds_it_and_its_version()
    {
        var storage = new ParentRecordReadTests.Storage();
        storage.Records[LinkedTemplates.RecordId("x")] = Child("x", 9);
        var (search, runtime) = ParentRecordReadTests.Search(storage);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("x"), Question("y"), Question("x")]);

            Assert.True(search.TryRead(Question("x"), out var held));
            Assert.Equal((true, ChildKind, 9L), (held.Found, held.Kind, held.Version));
            Assert.True(search.TryRead(Question("y"), out var missing));
            Assert.False(missing.Found);
            Assert.Null(missing.Version);

            // Each id once, and an answer is not asked again: a child not there is an answer too.
            var call = Assert.Single(storage.Calls);
            Assert.Equal(
                new[] { LinkedTemplates.RecordId("x"), LinkedTemplates.RecordId("y") }.Order(StringComparer.Ordinal),
                JsonNode.Parse(call.Body!)!["records"]!.AsArray().Select(id => id!.GetValue<string>()).Order(StringComparer.Ordinal));
            await search.AnswerAsync([Question("x"), Question("y")]);
            Assert.Single(storage.Calls);
            OsduContracts.AssertConform(storage.Calls.ToList(), null, OsduContracts.Storage);
        }
    }

    [Fact]
    public async Task The_parents_and_children_of_one_round_are_read_in_the_same_request()
    {
        var storage = new ParentRecordReadTests.Storage();
        storage.Records[LinkedTemplates.RecordId("x")] = Child("x", 2);
        storage.Records[LinkedTemplates.ParentId("x")] = LinkedTemplates.ParentRecord("x", 5, new JsonObject { ["Nested"] = new JsonObject { ["Inner"] = "x" } });
        var (search, runtime) = ParentRecordReadTests.Search(storage);
        using (runtime)
        using (search)
        {
            var parent = new ParentQuestion(LinkedTemplates.ParentId("x"), LinkedTemplates.ParentKind, "data.Nested");
            await search.AnswerAsync([parent, Question("x")]);

            Assert.Single(storage.Calls);
            Assert.True(search.TryRead(parent, out var read) && read.Found);
            Assert.True(search.TryRead(Question("x"), out var looked) && looked.Found);
        }
    }

    [Fact]
    public async Task A_record_of_another_entity_type_under_a_childs_id_fails_the_read()
    {
        var storage = new ParentRecordReadTests.Storage();
        storage.Records[LinkedTemplates.RecordId("x")] = Child("x", 1, "test:wks:master-data--Wellbore:1.0.0");
        var (search, runtime) = ParentRecordReadTests.Search(storage);
        using (runtime)
        using (search)
        {
            var failed = await Assert.ThrowsAsync<DeliveryException>(() => search.AnswerAsync([Question("x")]));
            Assert.Equal(
                $"The storage service answered the child record {LinkedTemplates.RecordId("x")} with a record of test:wks:master-data--Wellbore:1.0.0, and a child of {ChildKind} is a work-product-component--CustomThing record.",
                failed.Message);
            Assert.False(search.TryRead(Question("x"), out _));
        }
    }

    [Fact]
    public async Task A_child_storage_asks_to_retry_twice_fails_the_read_and_is_looked_up_again_next_time()
    {
        var storage = new ParentRecordReadTests.Storage();
        storage.Records[LinkedTemplates.RecordId("slow")] = Child("slow", 1);
        storage.RetryTimes(LinkedTemplates.RecordId("slow"), 2);
        var (search, runtime) = ParentRecordReadTests.Search(storage);
        using (runtime)
        using (search)
        {
            var failed = await Assert.ThrowsAsync<DeliveryException>(() => search.AnswerAsync([Question("slow")]));
            Assert.Contains("or written without the link to a child it may hold", failed.Message, StringComparison.Ordinal);
            Assert.False(search.TryRead(Question("slow"), out _));

            await search.AnswerAsync([Question("slow")]);
            Assert.True(search.TryRead(Question("slow"), out var answer) && answer.Found);
        }
    }

    [Fact]
    public void The_unique_segment_of_an_id_is_everything_after_its_entity_type()
    {
        Assert.Equal("abc", TargetId.Unique("dev:work-product-component--WellLog:abc"));
        Assert.Equal("a:b", TargetId.Unique("dev:work-product-component--WellLog:a:b"));
        Assert.Null(TargetId.Unique("dev:work-product-component--WellLog"));
        Assert.Null(TargetId.Unique("dev::abc"));
        Assert.Null(TargetId.Unique(string.Empty));
    }
}
