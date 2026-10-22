using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The parents of a run's records read from storage (osdu/docs/reference/flow/mapping-parent.md, How a run reads the parent;
/// openapi storage v2, <c>POST /query/records</c>): each id once, a hundred to a request, kept trimmed to the sections the
/// mapping reads; a parent storage does not hand over is an answer, and anything that keeps an answer from coming fails the
/// read rather than passing for a parent that is not there.
/// </summary>
public class ParentRecordReadTests
{
    private const string Path = "/api/storage/v2/query/records";

    /// <summary>
    /// Storage, answering each request by the ids it names: the records it holds, the ids it calls invalid, and the ids it
    /// asks to retry (as many times as given). Safe under the concurrent requests a run sends.
    /// </summary>
    private sealed class Storage : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _retries = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, JsonObject> Records { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<FakeHttpHandler.Request> Calls { get; } = new();

        /// <summary>An HTTP status every request is answered with instead, when set.</summary>
        public HttpStatusCode? Fails { get; set; }

        /// <summary>Waits before answering, so two readers asking at once overlap.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public void RetryTimes(string id, int times) => _retries[id] = times;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Enqueue(new FakeHttpHandler.Request(
                request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (Fails is { } status)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"storage is down\"}", Encoding.UTF8, "application/json") };
            }

            var asked = JsonNode.Parse(body!)!["records"]!.AsArray().Select(id => id!.GetValue<string>()).ToList();
            var found = new JsonArray();
            var invalid = new JsonArray();
            var retry = new JsonArray();
            foreach (var id in asked)
            {
                if (_retries.TryGetValue(id, out var left) && left > 0)
                {
                    _retries[id] = left - 1;
                    retry.Add(id);
                }
                else if (Records.TryGetValue(id, out var record))
                {
                    found.Add(record.DeepClone());
                }
                else
                {
                    invalid.Add(id);
                }
            }

            var answer = new JsonObject { ["records"] = found, ["invalidRecords"] = invalid, ["retryRecords"] = retry };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private static (OsduRecordSearch Search, HttpRuntime Runtime) Search(HttpMessageHandler handler, int attempts = 1)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = attempts, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (new OsduRecordSearch(_ => Task.FromResult(client), NullLogger.Instance), runtime);
    }

    private static ParentQuestion Question(string name, string sections = "data.Nested")
        => new(LinkedTemplates.ParentId(name), LinkedTemplates.ParentKind, sections);

    private static JsonObject Parent(string name, long version = 3)
        => LinkedTemplates.ParentRecord(name, version, new JsonObject
        {
            ["Nested"] = new JsonObject { ["Inner"] = name },
            ["Aliases"] = new JsonArray("A"),
            ["Big"] = new JsonObject { ["Payload"] = new string('x', 1000) },
        });

    [Fact]
    public async Task A_parent_is_read_by_its_id_and_kept_as_the_sections_the_mapping_reads()
    {
        var storage = new Storage();
        storage.Records[LinkedTemplates.ParentId("x")] = Parent("x", 12);
        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            var question = Question("x", "data.Aliases|data.Nested");
            await search.AnswerAsync([question]);

            Assert.True(search.TryRead(question, out var answer));
            Assert.Equal((true, LinkedTemplates.ParentKind, 12L), (answer.Found, answer.Kind, answer.Version));
            Assert.Equal("x", answer.At("data.Nested.Inner")!.GetValue<string>());
            Assert.Equal("A", answer.At("data.Aliases")![0]!.GetValue<string>());

            // Only the sections the mapping reads are kept: a run reading a parent per record never holds them whole.
            Assert.Null(answer.At("data.Big"));
            Assert.Null(answer.At("acl"));
            Assert.Equal(["data"], answer.Record!.Select(p => p.Key));

            var call = Assert.Single(storage.Calls);
            var body = JsonNode.Parse(call.Body!)!;
            Assert.Equal([LinkedTemplates.ParentId("x")], body["records"]!.AsArray().Select(id => id!.GetValue<string>()));
            Assert.Null(body["attributes"]);
            Assert.Equal("dev", call.Headers["data-partition-id"]);
            OsduContracts.AssertConform(storage.Calls.ToList(), null, OsduContracts.Storage);
        }
    }

    [Fact]
    public async Task Parents_are_read_a_hundred_to_a_request_and_each_once_however_many_records_ask()
    {
        var storage = new Storage();
        var names = Enumerable.Range(0, 150).Select(i => $"r{i}").ToList();
        foreach (var name in names)
        {
            storage.Records[LinkedTemplates.ParentId(name)] = Parent(name);
        }

        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            // Every record asking twice, and some for other sections: still one read of each parent.
            var questions = names.Select(n => Question(n)).Concat(names.Select(n => Question(n))).Concat(names.Take(10).Select(n => Question(n, "data.Aliases"))).ToList();
            await search.AnswerAsync(questions);
            await search.AnswerAsync(questions);

            var sizes = storage.Calls.Select(c => JsonNode.Parse(c.Body!)!["records"]!.AsArray().Count).OrderDescending().ToList();
            Assert.Equal([100, 50], sizes);
            Assert.All(questions, q => Assert.True(search.TryRead(q, out var answer) && answer.Found));
            Assert.True(search.TryRead(Question("r3", "data.Aliases"), out var aliases));
            Assert.Null(aliases.At("data.Nested"));
        }
    }

    [Fact]
    public async Task A_parent_storage_does_not_hand_over_is_an_answer_and_is_not_read_again()
    {
        var storage = new Storage();
        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("gone")]);
            await search.AnswerAsync([Question("gone")]);

            Assert.True(search.TryRead(Question("gone"), out var answer));
            Assert.Same(ParentAnswer.None, answer);
            Assert.Null(answer.At("data.Nested"));
            Assert.Single(storage.Calls);
        }
    }

    [Fact]
    public async Task Storage_answering_404_holds_none_of_the_parents_asked()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, Path, HttpStatusCode.NotFound, "{\"message\":\"none of these\"}");
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("a"), Question("b")]);

            Assert.True(search.TryRead(Question("a"), out var a) && !a.Found);
            Assert.True(search.TryRead(Question("b"), out var b) && !b.Found);
        }
    }

    [Fact]
    public async Task A_parent_storage_asks_to_retry_is_asked_again_once()
    {
        var storage = new Storage();
        storage.Records[LinkedTemplates.ParentId("slow")] = Parent("slow");
        storage.RetryTimes(LinkedTemplates.ParentId("slow"), 1);
        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            await search.AnswerAsync([Question("slow")]);

            Assert.True(search.TryRead(Question("slow"), out var answer) && answer.Found);
            Assert.Equal(2, storage.Calls.Count);
        }
    }

    [Fact]
    public async Task A_parent_storage_asks_to_retry_twice_fails_the_read_and_is_read_again_next_time()
    {
        var storage = new Storage();
        storage.Records[LinkedTemplates.ParentId("slow")] = Parent("slow");
        storage.RetryTimes(LinkedTemplates.ParentId("slow"), 2);
        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            var failed = await Assert.ThrowsAsync<DeliveryException>(() => search.AnswerAsync([Question("slow")]));
            Assert.Contains($"asked twice to retry reading the parent record {LinkedTemplates.ParentId("slow")}", failed.Message, StringComparison.Ordinal);
            Assert.False(search.TryRead(Question("slow"), out _));

            // Nothing was kept for it, so the next round reads it again, and storage answers this time.
            await search.AnswerAsync([Question("slow")]);
            Assert.True(search.TryRead(Question("slow"), out var answer) && answer.Found);
        }
    }

    [Fact]
    public async Task A_record_of_another_entity_type_under_the_parents_id_fails_the_read()
    {
        var storage = new Storage();
        var wrong = Parent("x");
        wrong["kind"] = "test:wks:master-data--Wellbore:1.0.0";
        storage.Records[LinkedTemplates.ParentId("x")] = wrong;
        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            var failed = await Assert.ThrowsAsync<DeliveryException>(() => search.AnswerAsync([Question("x")]));

            Assert.Equal(
                $"The storage service answered the parent record {LinkedTemplates.ParentId("x")} with a record of test:wks:master-data--Wellbore:1.0.0, and a parent of {LinkedTemplates.ParentKind} is a work-product-component--Thing record.",
                failed.Message);
            Assert.False(search.TryRead(Question("x"), out _));
        }
    }

    [Fact]
    public async Task A_failed_request_fails_the_read_and_never_passes_for_a_parent_that_is_not_there()
    {
        var storage = new Storage { Fails = HttpStatusCode.InternalServerError };
        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => search.AnswerAsync([Question("x")]));
            Assert.False(search.TryRead(Question("x"), out _));
        }

        var refused = new Storage { Fails = HttpStatusCode.Forbidden };
        var (denied, deniedRuntime) = Search(refused);
        using (deniedRuntime)
        using (denied)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => denied.AnswerAsync([Question("x")]));
            Assert.False(denied.TryRead(Question("x"), out _));
        }
    }

    [Fact]
    public async Task Two_rounds_asking_for_one_parent_at_once_read_it_once()
    {
        var storage = new Storage { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        storage.Records[LinkedTemplates.ParentId("x")] = Parent("x");
        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            var first = search.AnswerAsync([Question("x")]);
            var second = search.AnswerAsync([Question("x", "data.Aliases")]);
            await WaitUntil(() => !storage.Calls.IsEmpty);
            storage.Gate.SetResult();
            await Task.WhenAll(first, second);

            Assert.Single(storage.Calls);
            Assert.True(search.TryRead(Question("x"), out var nested) && nested.At("data.Nested") is not null);
            Assert.True(search.TryRead(Question("x", "data.Aliases"), out var aliases) && aliases.At("data.Aliases") is not null);
        }
    }

    [Fact]
    public async Task Searches_and_parents_asked_together_are_both_answered_in_the_round()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, "{\"results\":[{\"id\":\"dev:master-data--Wellbore:abc\"}],\"totalCount\":1}")
            .On(HttpMethod.Post, Path, HttpStatusCode.OK, new JsonObject { ["records"] = new JsonArray(Parent("x")), ["invalidRecords"] = new JsonArray() }.ToJsonString());
        var (search, runtime) = Search(handler);
        using (runtime)
        using (search)
        {
            var wellbore = new SearchQuestion("osdu:wks:master-data--Wellbore:*", "data.FacilityName", "WB", "data.FacilityName.keyword:\"WB\"");
            await search.AnswerAsync([wellbore, Question("x")]);

            Assert.True(search.TryAnswer(wellbore, out var found) && found.Outcome == SearchOutcome.Found);
            Assert.True(search.TryRead(Question("x"), out var parent) && parent.Found);
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task A_run_keeps_at_most_so_many_parents_and_lets_the_oldest_go_first()
    {
        var storage = new Storage();
        var count = OsduRecordSearch.MaxKeptParents + 100;
        for (var i = 0; i < count; i++)
        {
            storage.Records[LinkedTemplates.ParentId($"r{i}")] = LinkedTemplates.ParentRecord($"r{i}", 1, new JsonObject { ["Nested"] = new JsonObject() });
        }

        var (search, runtime) = Search(storage);
        using (runtime)
        using (search)
        {
            for (var batch = 0; batch < count; batch += 1_000)
            {
                await search.AnswerAsync(Enumerable.Range(batch, Math.Min(1_000, count - batch)).Select(i => Question($"r{i}")).ToList());
            }

            Assert.False(search.TryRead(Question("r0"), out _));
            Assert.True(search.TryRead(Question($"r{count - 1}"), out var newest) && newest.Found);
            Assert.Equal(OsduRecordSearch.MaxKeptParents, Enumerable.Range(0, count).Count(i => search.TryRead(Question($"r{i}"), out _)));
        }
    }

    [Fact]
    public void The_answer_reads_a_path_only_through_objects()
    {
        var answer = ParentAnswer.Of(new JsonObject { ["data"] = new JsonObject { ["List"] = new JsonArray(1, 2), ["Value"] = "v" } }, LinkedTemplates.ParentKind, 1);

        Assert.Equal("v", answer.At("data.Value")!.GetValue<string>());
        Assert.NotNull(answer.At("data.List"));
        Assert.Null(answer.At("data.List.0"));
        Assert.Null(answer.At("data.Value.Inner"));
        Assert.Null(answer.At("data.Missing"));
        Assert.Null(ParentAnswer.None.At("data.Value"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
