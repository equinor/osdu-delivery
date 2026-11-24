// The messages of ETP 1.2 protocol Discovery (3), written from the pinned protocol
// (osdu/specs/reservoir-ddms/etp-1.2.avpr) as EtpMessages.cs says: one C# record per message, its properties in the
// schema's field order.

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP 1.2 <c>Discovery.GetResources</c> message (protocol 3, message type 1).
/// </summary>
public sealed record GetResources : IEtpMessage
{
    public required ContextInfo Context { get; init; }

    public required ContextScopeKind Scope { get; init; }

    public bool CountObjects { get; init; }

    public long? StoreLastWriteFilter { get; init; }

    public ActiveStatusKind? ActiveStatusFilter { get; init; }

    public bool IncludeEdges { get; init; }

    public int Protocol => 3;

    public int MessageType => 1;

    public string MessageName => "Discovery.GetResources";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Context.Write(writer);
        writer.WriteEnum((int)Scope);
        writer.WriteBoolean(CountObjects);

        if (StoreLastWriteFilter is { } storeLastWriteFilterValue)
        {
            writer.WriteUnion(1);
            writer.WriteLong(storeLastWriteFilterValue);
        }
        else
        {
            writer.WriteUnion(0);
        }

        if (ActiveStatusFilter is { } activeStatusFilterValue)
        {
            writer.WriteUnion(1);
            writer.WriteEnum((int)activeStatusFilterValue);
        }
        else
        {
            writer.WriteUnion(0);
        }

        writer.WriteBoolean(IncludeEdges);
    }

    public static GetResources Read(ref EtpReader reader)
    {
        var context = ContextInfo.Read(ref reader);
        var scope = (ContextScopeKind)reader.ReadEnum(5);
        var countObjects = reader.ReadBoolean();
        long? storeLastWriteFilter = reader.ReadUnion(2) == 1 ? reader.ReadLong() : (long?)null;
        ActiveStatusKind? activeStatusFilter = reader.ReadUnion(2) == 1 ? (ActiveStatusKind)reader.ReadEnum(2) : (ActiveStatusKind?)null;
        var includeEdges = reader.ReadBoolean();

        return new GetResources
        {
            Context = context,
            Scope = scope,
            CountObjects = countObjects,
            StoreLastWriteFilter = storeLastWriteFilter,
            ActiveStatusFilter = activeStatusFilter,
            IncludeEdges = includeEdges,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Discovery.GetResourcesResponse</c> message (protocol 3, message type 4).
/// </summary>
public sealed record GetResourcesResponse : IEtpMessage
{
    public IReadOnlyList<Resource> Resources { get; init; } = [];

    public int Protocol => 3;

    public int MessageType => 4;

    public string MessageName => "Discovery.GetResourcesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Resources.Count);
        foreach (var resourcesItem in Resources)
        {
            resourcesItem.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetResourcesResponse Read(ref EtpReader reader)
    {
        var resources = new List<Resource>();
        for (var resourcesCount = reader.ReadBlockCount(); resourcesCount != 0; resourcesCount = reader.ReadBlockCount())
        {
            resources.EnsureCapacity(resources.Count + resourcesCount);
            for (var resourcesIndex = 0; resourcesIndex < resourcesCount; resourcesIndex++)
            {
                resources.Add(Resource.Read(ref reader));
            }
        }

        return new GetResourcesResponse
        {
            Resources = resources,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Discovery.GetResourcesEdgesResponse</c> message (protocol 3, message type 7).
/// </summary>
public sealed record GetResourcesEdgesResponse : IEtpMessage
{
    public required IReadOnlyList<Edge> Edges { get; init; }

    public int Protocol => 3;

    public int MessageType => 7;

    public string MessageName => "Discovery.GetResourcesEdgesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Edges.Count);
        foreach (var edgesItem in Edges)
        {
            edgesItem.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetResourcesEdgesResponse Read(ref EtpReader reader)
    {
        var edges = new List<Edge>();
        for (var edgesCount = reader.ReadBlockCount(); edgesCount != 0; edgesCount = reader.ReadBlockCount())
        {
            edges.EnsureCapacity(edges.Count + edgesCount);
            for (var edgesIndex = 0; edgesIndex < edgesCount; edgesIndex++)
            {
                edges.Add(Edge.Read(ref reader));
            }
        }

        return new GetResourcesEdgesResponse
        {
            Edges = edges,
        };
    }
}
