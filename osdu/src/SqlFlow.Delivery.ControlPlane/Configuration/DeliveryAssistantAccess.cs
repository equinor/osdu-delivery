using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SqlFlow.ControlPlane.Security;

namespace SqlFlow.Delivery.ControlPlane.Configuration;

/// <summary>
/// The delivery endpoints an AI assistant run may call: exactly the ones the <c>delivery_*</c> tools that read
/// (<see cref="ReaderTools"/>) call, by endpoint name. The product's MCP server lists every tool to every client; this is
/// what the chat's delegated token may reach of them, and the operator actions (<see cref="ActionTools"/>) are not.
/// </summary>
/// <remarks>
/// <para>A chat run reaches the control plane with a token delegated from the person asking, which SQLFlow refuses on
/// every endpoint that has not been opened to it (<see cref="AssistantDelegation"/>). That fence is default-deny, so a
/// module's endpoints are closed to the assistant until the module opens them, and the token is held by the model's host,
/// a third party: what is opened here is what that host could read with it. So only the routes the allowed tools read are
/// opened, and nothing a tool kept from the assistant calls (an operator action), and nothing no tool calls (a record's
/// rendered document, a dimension's values).</para>
/// <para><c>DeliveryAssistantAccessTests</c> reads the routes each allowed tool calls out of the MCP server's sources and
/// fails when one is not opened here, when an endpoint opened here is not one an allowed tool calls, or when a name here
/// is not an endpoint's.</para>
/// </remarks>
public static class DeliveryAssistantAccess
{
    /// <summary>The delivery module's readers: the ledger, flows, the audit trail, documents, caches, tests and dimensions.
    /// Every route they call is opened to an assistant run.</summary>
    public static IReadOnlyList<string> ReaderTools { get; } =
    [
        "delivery_find_records", "delivery_record",
        "delivery_flow", "delivery_flow_records", "delivery_run", "delivery_submissions", "delivery_retrievals", "delivery_activities",
        "delivery_partitions", "delivery_config",
        "delivery_caches", "delivery_cache_versions", "delivery_cache_changes",
        "delivery_mappings", "delivery_check_mapping", "delivery_scaffold_mapping",
        "delivery_templates", "delivery_osdu_schemas", "delivery_osdu_schema_compare",
        "delivery_assertions", "delivery_assertion_runs",
        "delivery_dimensions",
    ];

    /// <summary>
    /// The delivery module's operator actions, each marked "(operator action)" in its description: every one starts
    /// work or changes what the ledger or OSDU holds, as SQLFlow's <c>trigger_run</c> does. Nothing they post to is
    /// opened to an assistant run, so in the chat the control plane refuses them and the assistant says where a person
    /// does it; a person's own MCP client, with the person's own token, can use them.
    /// </summary>
    public static IReadOnlyList<string> ActionTools { get; } =
    [
        "delivery_probe_target",
        "delivery_verify_record",
        "delivery_sync_with_source",
        "delivery_release_records",
        "delivery_redeliver_record",
        "delivery_decide_cache_changes",
    ];

    /// <summary>The endpoints opened to an assistant run for reading (GET, HEAD and OPTIONS).</summary>
    public static IReadOnlySet<string> Reads { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // delivery_find_records and delivery_record: the ledger's answer about a record and its trace.
        "LookupDeliveryRecords", "GetDeliveryRecord", "ListDeliveryRecordAttempts", "GetDeliveryRecordChain",
        "ListDeliveryRecordActivities",
        // delivery_flow, delivery_flow_records, delivery_run, delivery_submissions, delivery_retrievals.
        "GetDeliveryFlowStats", "ListDeliveryInterfaces", "GetDeliveryTarget", "ListDeliveryRecords", "LookupDeliveryRun",
        "ListDeliverySubmissions", "GetDeliverySubmission", "ListDeliverySubmissionBatches", "ListDeliverySubmissionAttempts",
        "ListDeliveryRetrievals",
        // delivery_activities: the audit trail.
        "ListDeliveryActivities", "GetDeliveryActivity",
        // delivery_partitions and delivery_config.
        "ListDeliveryPartitions", "ListDeliveryConfig", "GetEffectiveDeliveryConfig",
        // delivery_caches, delivery_cache_versions, delivery_cache_changes.
        "ListDeliveryCaches", "GetDeliveryCacheStreams", "ListDeliveryCacheVersions", "ListDeliveryCacheHistory",
        "ListDeliveryUpdateTags",
        // delivery_mappings, delivery_templates, delivery_osdu_schemas, delivery_osdu_schema_compare.
        "ListDeliveryMappings", "GetDeliveryMapping", "ListDeliveryMappingFlows",
        "ListDeliveryTemplates", "GetDeliveryTemplate",
        "ListDeliveryOsduReleases", "ListDeliveryOsduSchemas", "GetDeliveryOsduSchema", "CompareDeliveryOsduSchemas",
        // delivery_assertions and delivery_assertion_runs.
        "GetDeliveryAssertionBoard", "GetDeliveryAssertionFlowBoard", "ListDeliveryAssertionRuns", "GetDeliveryAssertionMatrix",
        "GetDeliveryAssertionRun",
        // delivery_dimensions: what a dimension is and how it was built, never its values.
        "GetDeliveryDimensionBoard", "GetDeliveryDimensionFlowBoard", "GetDeliveryDimension", "ListDeliveryDimensionBuilds",
    };

    /// <summary>
    /// The endpoints opened to an assistant run whatever the method: the mapping builder's previews, which
    /// <c>delivery_check_mapping</c> and <c>delivery_scaffold_mapping</c> post a draft to. Each computes an answer from the
    /// templates and documents and stores nothing.
    /// </summary>
    public static IReadOnlySet<string> Previews { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "CoverDeliveryMapping", "ShapeDeliveryMapping", "DraftDeliveryMapping", "ComposeDeliveryMapping",
    };

    /// <summary>
    /// Opens the endpoints of <paramref name="group"/> named in <see cref="Reads"/> or <see cref="Previews"/> to an
    /// assistant run. Applied as a final convention, so it sees each endpoint's own name, which the endpoint sets after
    /// the group's conventions have run.
    /// </summary>
    public static TBuilder OpenToAssistantRuns<TBuilder>(this TBuilder group)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(group);
        group.Finally(Open);
        return group;
    }

    private static void Open(EndpointBuilder endpoint)
    {
        var name = endpoint.Metadata.OfType<IEndpointNameMetadata>().LastOrDefault()?.EndpointName;
        if (name is null)
        {
            return;
        }

        if (Previews.Contains(name))
        {
            endpoint.Metadata.Add(AssistantAccess.AnyMethod);
        }
        else if (Reads.Contains(name))
        {
            endpoint.Metadata.Add(AssistantAccess.ReadOnly);
        }
    }
}
