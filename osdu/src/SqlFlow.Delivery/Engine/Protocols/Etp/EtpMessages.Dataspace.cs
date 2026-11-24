// The messages of ETP 1.2 protocols Dataspace (24) and DataspaceOSDU (2424), written from the pinned protocol
// (osdu/specs/reservoir-ddms/etp-1.2.avpr) as EtpMessages.cs says: one C# record per message, its properties in the
// schema's field order.

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP 1.2 <c>Dataspace.GetDataspaces</c> message (protocol 24, message type 1).
/// </summary>
public sealed record GetDataspaces : IEtpMessage
{
    public long? StoreLastWriteFilter { get; init; }

    public int Protocol => 24;

    public int MessageType => 1;

    public string MessageName => "Dataspace.GetDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (StoreLastWriteFilter is { } storeLastWriteFilterValue)
        {
            writer.WriteUnion(1);
            writer.WriteLong(storeLastWriteFilterValue);
        }
        else
        {
            writer.WriteUnion(0);
        }
    }

    public static GetDataspaces Read(ref EtpReader reader)
    {
        long? storeLastWriteFilter = reader.ReadUnion(2) == 1 ? reader.ReadLong() : (long?)null;

        return new GetDataspaces
        {
            StoreLastWriteFilter = storeLastWriteFilter,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.GetDataspacesResponse</c> message (protocol 24, message type 2).
/// </summary>
public sealed record GetDataspacesResponse : IEtpMessage
{
    public IReadOnlyList<Dataspace> Dataspaces { get; init; } = [];

    public int Protocol => 24;

    public int MessageType => 2;

    public string MessageName => "Dataspace.GetDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Dataspaces.Count);
        foreach (var dataspacesItem in Dataspaces)
        {
            dataspacesItem.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataspacesResponse Read(ref EtpReader reader)
    {
        var dataspaces = new List<Dataspace>();
        for (var dataspacesCount = reader.ReadBlockCount(); dataspacesCount != 0; dataspacesCount = reader.ReadBlockCount())
        {
            dataspaces.EnsureCapacity(dataspaces.Count + dataspacesCount);
            for (var dataspacesIndex = 0; dataspacesIndex < dataspacesCount; dataspacesIndex++)
            {
                dataspaces.Add(Dataspace.Read(ref reader));
            }
        }

        return new GetDataspacesResponse
        {
            Dataspaces = dataspaces,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.PutDataspaces</c> message (protocol 24, message type 3).
/// </summary>
public sealed record PutDataspaces : IEtpMessage
{
    public required IReadOnlyDictionary<string, Dataspace> Dataspaces { get; init; }

    public int Protocol => 24;

    public int MessageType => 3;

    public string MessageName => "Dataspace.PutDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Dataspaces.Count);
        foreach (var (dataspacesKey, dataspacesValue) in Dataspaces)
        {
            writer.WriteString(dataspacesKey);
            dataspacesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataspaces Read(ref EtpReader reader)
    {
        var dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal);
        for (var dataspacesCount = reader.ReadBlockCount(); dataspacesCount != 0; dataspacesCount = reader.ReadBlockCount())
        {
            for (var dataspacesIndex = 0; dataspacesIndex < dataspacesCount; dataspacesIndex++)
            {
                var dataspacesKey = reader.ReadString();
                dataspaces[dataspacesKey] = Dataspace.Read(ref reader);
            }
        }

        return new PutDataspaces
        {
            Dataspaces = dataspaces,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.PutDataspacesResponse</c> message (protocol 24, message type 6).
/// </summary>
public sealed record PutDataspacesResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 24;

    public int MessageType => 6;

    public string MessageName => "Dataspace.PutDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataspacesResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new PutDataspacesResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.DeleteDataspaces</c> message (protocol 24, message type 4).
/// </summary>
public sealed record DeleteDataspaces : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public int Protocol => 24;

    public int MessageType => 4;

    public string MessageName => "Dataspace.DeleteDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();
    }

    public static DeleteDataspaces Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        return new DeleteDataspaces
        {
            Uris = uris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Dataspace.DeleteDataspacesResponse</c> message (protocol 24, message type 5).
/// </summary>
public sealed record DeleteDataspacesResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 24;

    public int MessageType => 5;

    public string MessageName => "Dataspace.DeleteDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static DeleteDataspacesResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new DeleteDataspacesResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.GetDataspaceInfo</c> message (protocol 2424, message type 1).
/// </summary>
public sealed record GetDataspaceInfo : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public int Protocol => 2424;

    public int MessageType => 1;

    public string MessageName => "DataspaceOSDU.GetDataspaceInfo";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataspaceInfo Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        return new GetDataspaceInfo
        {
            Uris = uris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.GetDataspaceInfoResponse</c> message (protocol 2424, message type 2).
/// </summary>
public sealed record GetDataspaceInfoResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, Dataspace> Dataspaces { get; init; }

    public int Protocol => 2424;

    public int MessageType => 2;

    public string MessageName => "DataspaceOSDU.GetDataspaceInfoResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Dataspaces.Count);
        foreach (var (dataspacesKey, dataspacesValue) in Dataspaces)
        {
            writer.WriteString(dataspacesKey);
            dataspacesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataspaceInfoResponse Read(ref EtpReader reader)
    {
        var dataspaces = new Dictionary<string, Dataspace>(StringComparer.Ordinal);
        for (var dataspacesCount = reader.ReadBlockCount(); dataspacesCount != 0; dataspacesCount = reader.ReadBlockCount())
        {
            for (var dataspacesIndex = 0; dataspacesIndex < dataspacesCount; dataspacesIndex++)
            {
                var dataspacesKey = reader.ReadString();
                dataspaces[dataspacesKey] = Dataspace.Read(ref reader);
            }
        }

        return new GetDataspaceInfoResponse
        {
            Dataspaces = dataspaces,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.LockDataspaces</c> message (protocol 2424, message type 5).
/// </summary>
public sealed record LockDataspaces : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public required bool Lock { get; init; }

    public int Protocol => 2424;

    public int MessageType => 5;

    public string MessageName => "DataspaceOSDU.LockDataspaces";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Uris.Count);
        foreach (var (urisKey, urisValue) in Uris)
        {
            writer.WriteString(urisKey);
            writer.WriteString(urisValue);
        }
        writer.WriteBlockEnd();

        writer.WriteBoolean(Lock);
    }

    public static LockDataspaces Read(ref EtpReader reader)
    {
        var uris = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var urisCount = reader.ReadBlockCount(); urisCount != 0; urisCount = reader.ReadBlockCount())
        {
            for (var urisIndex = 0; urisIndex < urisCount; urisIndex++)
            {
                var urisKey = reader.ReadString();
                uris[urisKey] = reader.ReadString();
            }
        }

        var @lock = reader.ReadBoolean();

        return new LockDataspaces
        {
            Uris = uris,
            Lock = @lock,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataspaceOSDU.LockDataspacesResponse</c> message (protocol 2424, message type 6).
/// </summary>
public sealed record LockDataspacesResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 2424;

    public int MessageType => 6;

    public string MessageName => "DataspaceOSDU.LockDataspacesResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            writer.WriteString(successValue);
        }
        writer.WriteBlockEnd();
    }

    public static LockDataspacesResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = reader.ReadString();
            }
        }

        return new LockDataspacesResponse
        {
            Success = success,
        };
    }
}
