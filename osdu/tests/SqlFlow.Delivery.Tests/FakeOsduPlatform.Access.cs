using System.Net;
using System.Text.Json.Nodes;

namespace SqlFlow.Delivery.Tests;

// Storage's metadata patch of records' ACL and entitlements' list of the caller's groups, as the services answer them
// (osdu/specs/core/INTEGRATION.md, 2.3 and 2.8; storage BulkUpdateRecordServiceImpl and PatchApi): owner access is checked per
// record by the group's name before '@' (a data manager may change any), the ACL is replaced in place on the record and every
// version a read returns, no version is written, and a request some ids of which were not changed answers 206 naming them.
public sealed partial class FakeOsduPlatform
{
    /// <summary>The flow's identity as entitlements names it.</summary>
    public string MemberEmail { get; set; } = "delivery@dev.example.com";

    /// <summary>The groups the flow's identity is a member of, by email.</summary>
    public List<string> CallerGroups { get; } =
    [
        "data.welldb.owners@dev.dataservices.energy", "data.welldb.viewers@dev.dataservices.energy", "data.restricted.owners@dev.dataservices.energy",
        "data.default.owners@dev.example.com", "data.default.viewers@dev.example.com", "service.storage.creator@dev.dataservices.energy",
    ];

    /// <summary>Every metadata patch storage took: the ids and the operations.</summary>
    public List<(IReadOnlyList<string> Ids, JsonArray Ops)> AclPatches { get; } = [];

    /// <summary>A status every metadata patch is refused with, whole, before anything is changed; null takes them.</summary>
    public HttpStatusCode? AclPatchRefused { get; set; }

    /// <summary>When true, every metadata patch is applied and then answered 503, as a gateway that lost the answer would.</summary>
    public bool AclPatchAnswerLost { get; set; }

    /// <summary>When set, entitlements answers the groups list with this status.</summary>
    public HttpStatusCode? GroupsFail { get; set; }

    /// <summary>
    /// Ids the batched read leaves out of every list of its answer, as storage does a record it holds that the caller may not
    /// read (storage BatchServiceImpl.getMultipleRecords keeps only the records that pass its ACL check).
    /// </summary>
    public HashSet<string> ReadWithheld { get; } = new(StringComparer.Ordinal);

    /// <summary>Ids the batched read names under retryRecords every time, as storage does a record whose content it could not read.</summary>
    public HashSet<string> ReadRetried { get; } = new(StringComparer.Ordinal);

    private HttpResponseMessage? AccessRoute(string method, string path, string? body)
    {
        if (path == "/api/entitlements/v2/groups" && method == "GET")
        {
            return GroupsFail is { } failing
                ? Error(failing, "entitlements unavailable")
                : Json(HttpStatusCode.OK, new JsonObject
                {
                    ["desId"] = MemberEmail,
                    ["memberEmail"] = MemberEmail,
                    ["groups"] = new JsonArray([.. CallerGroups.Select(g => (JsonNode?)new JsonObject { ["name"] = g[..g.IndexOf('@', StringComparison.Ordinal)], ["email"] = g, ["description"] = string.Empty })]),
                });
        }

        if (path != "/api/storage/v2/records" || method != "PATCH")
        {
            return null;
        }

        if (AclPatchRefused is { } refused)
        {
            return Error(refused, "Invalid ACLs provided in acl path.");
        }

        var request = JsonNode.Parse(body!)!.AsObject();
        var ids = request["query"]!["ids"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
        var ops = request["ops"]!.AsArray();
        AclPatches.Add((ids, (JsonArray)ops.DeepClone()));
        var changed = new JsonArray();
        var notFound = new JsonArray();
        var unauthorized = new JsonArray();
        var dataManager = CallerGroups.Any(g => GroupName(g) == "users.data.root");
        foreach (var id in ids)
        {
            // Storage reads the record's metadata whatever its state: a soft-deleted record's ACL is changed too.
            if (!Records.TryGetValue(id, out var record))
            {
                notFound.Add(id);
                continue;
            }

            var owners = record["acl"]!["owners"]!.AsArray().Select(o => GroupName(o!.GetValue<string>())).ToHashSet(StringComparer.Ordinal);
            if (!dataManager && !CallerGroups.Any(g => owners.Contains(GroupName(g))))
            {
                unauthorized.Add(id);
                continue;
            }

            foreach (var op in ops)
            {
                var list = op!["path"]!.GetValue<string>() switch
                {
                    "/acl/viewers" => "viewers",
                    "/acl/owners" => "owners",
                    _ => throw new InvalidOperationException("the fake applies ACL operations only"),
                };
                var value = (JsonArray)op["value"]!.DeepClone();
                record["acl"]![list] = value.DeepClone();
                foreach (var version in History.GetValueOrDefault(id) ?? [])
                {
                    version["acl"]![list] = value.DeepClone();
                }
            }

            changed.Add(id);
        }

        if (AclPatchAnswerLost)
        {
            return Error(HttpStatusCode.ServiceUnavailable, "upstream connect error");
        }

        var answer = new JsonObject
        {
            ["recordCount"] = changed.Count,
            ["recordIds"] = changed,
            ["notFoundRecordIds"] = notFound,
            ["unAuthorizedRecordIds"] = unauthorized,
            ["lockedRecordIds"] = new JsonArray(),
        };
        return Json(notFound.Count + unauthorized.Count == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent, answer);
    }

    private static string GroupName(string group)
    {
        var at = group.IndexOf('@', StringComparison.Ordinal);
        return (at < 0 ? group : group[..at]).ToLowerInvariant();
    }
}
