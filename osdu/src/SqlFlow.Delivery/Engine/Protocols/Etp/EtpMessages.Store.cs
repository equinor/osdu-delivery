// The messages of ETP 1.2 protocol Store (4), written from the pinned protocol
// (osdu/specs/reservoir-ddms/etp-1.2.avpr) as EtpMessages.cs says: one C# record per message, its properties in the
// schema's field order.

using System.Collections.ObjectModel;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP 1.2 <c>Store.GetDataObjects</c> message (protocol 4, message type 1).
/// </summary>
public sealed record GetDataObjects : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public string Format { get; init; } = "xml";

    public int Protocol => 4;

    public int MessageType => 1;

    public string MessageName => "Store.GetDataObjects";

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

        writer.WriteString(Format);
    }

    public static GetDataObjects Read(ref EtpReader reader)
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

        var format = reader.ReadString();

        return new GetDataObjects
        {
            Uris = uris,
            Format = format,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.GetDataObjectsResponse</c> message (protocol 4, message type 4).
/// </summary>
public sealed record GetDataObjectsResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataObject> DataObjects { get; init; } = ReadOnlyDictionary<string, DataObject>.Empty;

    public int Protocol => 4;

    public int MessageType => 4;

    public string MessageName => "Store.GetDataObjectsResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataObjects.Count);
        foreach (var (dataObjectsKey, dataObjectsValue) in DataObjects)
        {
            writer.WriteString(dataObjectsKey);
            dataObjectsValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataObjectsResponse Read(ref EtpReader reader)
    {
        var dataObjects = new Dictionary<string, DataObject>(StringComparer.Ordinal);
        for (var dataObjectsCount = reader.ReadBlockCount(); dataObjectsCount != 0; dataObjectsCount = reader.ReadBlockCount())
        {
            for (var dataObjectsIndex = 0; dataObjectsIndex < dataObjectsCount; dataObjectsIndex++)
            {
                var dataObjectsKey = reader.ReadString();
                dataObjects[dataObjectsKey] = DataObject.Read(ref reader);
            }
        }

        return new GetDataObjectsResponse
        {
            DataObjects = dataObjects,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.PutDataObjects</c> message (protocol 4, message type 2).
/// </summary>
public sealed record PutDataObjects : IEtpMessage
{
    public required IReadOnlyDictionary<string, DataObject> DataObjects { get; init; }

    public bool PruneContainedObjects { get; init; }

    public int Protocol => 4;

    public int MessageType => 2;

    public string MessageName => "Store.PutDataObjects";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataObjects.Count);
        foreach (var (dataObjectsKey, dataObjectsValue) in DataObjects)
        {
            writer.WriteString(dataObjectsKey);
            dataObjectsValue.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBoolean(PruneContainedObjects);
    }

    public static PutDataObjects Read(ref EtpReader reader)
    {
        var dataObjects = new Dictionary<string, DataObject>(StringComparer.Ordinal);
        for (var dataObjectsCount = reader.ReadBlockCount(); dataObjectsCount != 0; dataObjectsCount = reader.ReadBlockCount())
        {
            for (var dataObjectsIndex = 0; dataObjectsIndex < dataObjectsCount; dataObjectsIndex++)
            {
                var dataObjectsKey = reader.ReadString();
                dataObjects[dataObjectsKey] = DataObject.Read(ref reader);
            }
        }

        var pruneContainedObjects = reader.ReadBoolean();

        return new PutDataObjects
        {
            DataObjects = dataObjects,
            PruneContainedObjects = pruneContainedObjects,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.PutDataObjectsResponse</c> message (protocol 4, message type 9).
/// </summary>
public sealed record PutDataObjectsResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutResponse> Success { get; init; }

    public int Protocol => 4;

    public int MessageType => 9;

    public string MessageName => "Store.PutDataObjectsResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(Success.Count);
        foreach (var (successKey, successValue) in Success)
        {
            writer.WriteString(successKey);
            successValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataObjectsResponse Read(ref EtpReader reader)
    {
        var success = new Dictionary<string, PutResponse>(StringComparer.Ordinal);
        for (var successCount = reader.ReadBlockCount(); successCount != 0; successCount = reader.ReadBlockCount())
        {
            for (var successIndex = 0; successIndex < successCount; successIndex++)
            {
                var successKey = reader.ReadString();
                success[successKey] = PutResponse.Read(ref reader);
            }
        }

        return new PutDataObjectsResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.DeleteDataObjects</c> message (protocol 4, message type 3).
/// </summary>
public sealed record DeleteDataObjects : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Uris { get; init; }

    public bool PruneContainedObjects { get; init; }

    public int Protocol => 4;

    public int MessageType => 3;

    public string MessageName => "Store.DeleteDataObjects";

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

        writer.WriteBoolean(PruneContainedObjects);
    }

    public static DeleteDataObjects Read(ref EtpReader reader)
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

        var pruneContainedObjects = reader.ReadBoolean();

        return new DeleteDataObjects
        {
            Uris = uris,
            PruneContainedObjects = pruneContainedObjects,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.DeleteDataObjectsResponse</c> message (protocol 4, message type 10).
/// </summary>
public sealed record DeleteDataObjectsResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, ArrayOfString> DeletedUris { get; init; }

    public int Protocol => 4;

    public int MessageType => 10;

    public string MessageName => "Store.DeleteDataObjectsResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DeletedUris.Count);
        foreach (var (deletedUrisKey, deletedUrisValue) in DeletedUris)
        {
            writer.WriteString(deletedUrisKey);
            deletedUrisValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static DeleteDataObjectsResponse Read(ref EtpReader reader)
    {
        var deletedUris = new Dictionary<string, ArrayOfString>(StringComparer.Ordinal);
        for (var deletedUrisCount = reader.ReadBlockCount(); deletedUrisCount != 0; deletedUrisCount = reader.ReadBlockCount())
        {
            for (var deletedUrisIndex = 0; deletedUrisIndex < deletedUrisCount; deletedUrisIndex++)
            {
                var deletedUrisKey = reader.ReadString();
                deletedUris[deletedUrisKey] = ArrayOfString.Read(ref reader);
            }
        }

        return new DeleteDataObjectsResponse
        {
            DeletedUris = deletedUris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Store.Chunk</c> message (protocol 4, message type 8).
/// </summary>
public sealed record Chunk : IEtpMessage
{
    public required Guid BlobId { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }

    public required bool Final { get; init; }

    public int Protocol => 4;

    public int MessageType => 8;

    public string MessageName => "Store.Chunk";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(BlobId);
        writer.WriteBytes(Data.Span);
        writer.WriteBoolean(Final);
    }

    public static Chunk Read(ref EtpReader reader)
    {
        var blobId = reader.ReadUuid();
        var data = reader.ReadBytes();
        var final = reader.ReadBoolean();

        return new Chunk
        {
            BlobId = blobId,
            Data = data,
            Final = final,
        };
    }
}
