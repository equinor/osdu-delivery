// The messages of ETP 1.2 protocol DataArray (9), written from the pinned protocol
// (osdu/specs/reservoir-ddms/etp-1.2.avpr) as EtpMessages.cs says: one C# record per message, its properties in the
// schema's field order.

using System.Collections.ObjectModel;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArrays</c> message (protocol 9, message type 2).
/// </summary>
public sealed record GetDataArrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, DataArrayIdentifier> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 2;

    public string MessageName => "DataArray.GetDataArrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArrays Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, DataArrayIdentifier>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = DataArrayIdentifier.Read(ref reader);
            }
        }

        return new GetDataArrays
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArraysResponse</c> message (protocol 9, message type 1).
/// </summary>
public sealed record GetDataArraysResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataArray> DataArrays { get; init; } = ReadOnlyDictionary<string, DataArray>.Empty;

    public int Protocol => 9;

    public int MessageType => 1;

    public string MessageName => "DataArray.GetDataArraysResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArraysResponse Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, DataArray>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = DataArray.Read(ref reader);
            }
        }

        return new GetDataArraysResponse
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataSubarrays</c> message (protocol 9, message type 3).
/// </summary>
public sealed record GetDataSubarrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, GetDataSubarraysType> DataSubarrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 3;

    public string MessageName => "DataArray.GetDataSubarrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataSubarrays.Count);
        foreach (var (dataSubarraysKey, dataSubarraysValue) in DataSubarrays)
        {
            writer.WriteString(dataSubarraysKey);
            dataSubarraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataSubarrays Read(ref EtpReader reader)
    {
        var dataSubarrays = new Dictionary<string, GetDataSubarraysType>(StringComparer.Ordinal);
        for (var dataSubarraysCount = reader.ReadBlockCount(); dataSubarraysCount != 0; dataSubarraysCount = reader.ReadBlockCount())
        {
            for (var dataSubarraysIndex = 0; dataSubarraysIndex < dataSubarraysCount; dataSubarraysIndex++)
            {
                var dataSubarraysKey = reader.ReadString();
                dataSubarrays[dataSubarraysKey] = GetDataSubarraysType.Read(ref reader);
            }
        }

        return new GetDataSubarrays
        {
            DataSubarrays = dataSubarrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataSubarraysResponse</c> message (protocol 9, message type 8).
/// </summary>
public sealed record GetDataSubarraysResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataArray> DataSubarrays { get; init; } = ReadOnlyDictionary<string, DataArray>.Empty;

    public int Protocol => 9;

    public int MessageType => 8;

    public string MessageName => "DataArray.GetDataSubarraysResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataSubarrays.Count);
        foreach (var (dataSubarraysKey, dataSubarraysValue) in DataSubarrays)
        {
            writer.WriteString(dataSubarraysKey);
            dataSubarraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataSubarraysResponse Read(ref EtpReader reader)
    {
        var dataSubarrays = new Dictionary<string, DataArray>(StringComparer.Ordinal);
        for (var dataSubarraysCount = reader.ReadBlockCount(); dataSubarraysCount != 0; dataSubarraysCount = reader.ReadBlockCount())
        {
            for (var dataSubarraysIndex = 0; dataSubarraysIndex < dataSubarraysCount; dataSubarraysIndex++)
            {
                var dataSubarraysKey = reader.ReadString();
                dataSubarrays[dataSubarraysKey] = DataArray.Read(ref reader);
            }
        }

        return new GetDataSubarraysResponse
        {
            DataSubarrays = dataSubarrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataArrays</c> message (protocol 9, message type 4).
/// </summary>
public sealed record PutDataArrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutDataArraysType> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 4;

    public string MessageName => "DataArray.PutDataArrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataArrays Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, PutDataArraysType>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = PutDataArraysType.Read(ref reader);
            }
        }

        return new PutDataArrays
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataArraysResponse</c> message (protocol 9, message type 10).
/// </summary>
public sealed record PutDataArraysResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 9;

    public int MessageType => 10;

    public string MessageName => "DataArray.PutDataArraysResponse";

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

    public static PutDataArraysResponse Read(ref EtpReader reader)
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

        return new PutDataArraysResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataSubarrays</c> message (protocol 9, message type 5).
/// </summary>
public sealed record PutDataSubarrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutDataSubarraysType> DataSubarrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 5;

    public string MessageName => "DataArray.PutDataSubarrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataSubarrays.Count);
        foreach (var (dataSubarraysKey, dataSubarraysValue) in DataSubarrays)
        {
            writer.WriteString(dataSubarraysKey);
            dataSubarraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutDataSubarrays Read(ref EtpReader reader)
    {
        var dataSubarrays = new Dictionary<string, PutDataSubarraysType>(StringComparer.Ordinal);
        for (var dataSubarraysCount = reader.ReadBlockCount(); dataSubarraysCount != 0; dataSubarraysCount = reader.ReadBlockCount())
        {
            for (var dataSubarraysIndex = 0; dataSubarraysIndex < dataSubarraysCount; dataSubarraysIndex++)
            {
                var dataSubarraysKey = reader.ReadString();
                dataSubarrays[dataSubarraysKey] = PutDataSubarraysType.Read(ref reader);
            }
        }

        return new PutDataSubarrays
        {
            DataSubarrays = dataSubarrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutDataSubarraysResponse</c> message (protocol 9, message type 11).
/// </summary>
public sealed record PutDataSubarraysResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 9;

    public int MessageType => 11;

    public string MessageName => "DataArray.PutDataSubarraysResponse";

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

    public static PutDataSubarraysResponse Read(ref EtpReader reader)
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

        return new PutDataSubarraysResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutUninitializedDataArrays</c> message (protocol 9, message type 9).
/// </summary>
public sealed record PutUninitializedDataArrays : IEtpMessage
{
    public required IReadOnlyDictionary<string, PutUninitializedDataArrayType> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 9;

    public string MessageName => "DataArray.PutUninitializedDataArrays";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static PutUninitializedDataArrays Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, PutUninitializedDataArrayType>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = PutUninitializedDataArrayType.Read(ref reader);
            }
        }

        return new PutUninitializedDataArrays
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.PutUninitializedDataArraysResponse</c> message (protocol 9, message type 12).
/// </summary>
public sealed record PutUninitializedDataArraysResponse : IEtpMessage
{
    public required IReadOnlyDictionary<string, string> Success { get; init; }

    public int Protocol => 9;

    public int MessageType => 12;

    public string MessageName => "DataArray.PutUninitializedDataArraysResponse";

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

    public static PutUninitializedDataArraysResponse Read(ref EtpReader reader)
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

        return new PutUninitializedDataArraysResponse
        {
            Success = success,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArrayMetadata</c> message (protocol 9, message type 6).
/// </summary>
public sealed record GetDataArrayMetadata : IEtpMessage
{
    public required IReadOnlyDictionary<string, DataArrayIdentifier> DataArrays { get; init; }

    public int Protocol => 9;

    public int MessageType => 6;

    public string MessageName => "DataArray.GetDataArrayMetadata";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(DataArrays.Count);
        foreach (var (dataArraysKey, dataArraysValue) in DataArrays)
        {
            writer.WriteString(dataArraysKey);
            dataArraysValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArrayMetadata Read(ref EtpReader reader)
    {
        var dataArrays = new Dictionary<string, DataArrayIdentifier>(StringComparer.Ordinal);
        for (var dataArraysCount = reader.ReadBlockCount(); dataArraysCount != 0; dataArraysCount = reader.ReadBlockCount())
        {
            for (var dataArraysIndex = 0; dataArraysIndex < dataArraysCount; dataArraysIndex++)
            {
                var dataArraysKey = reader.ReadString();
                dataArrays[dataArraysKey] = DataArrayIdentifier.Read(ref reader);
            }
        }

        return new GetDataArrayMetadata
        {
            DataArrays = dataArrays,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>DataArray.GetDataArrayMetadataResponse</c> message (protocol 9, message type 7).
/// </summary>
public sealed record GetDataArrayMetadataResponse : IEtpMessage
{
    public IReadOnlyDictionary<string, DataArrayMetadata> ArrayMetadata { get; init; } = ReadOnlyDictionary<string, DataArrayMetadata>.Empty;

    public int Protocol => 9;

    public int MessageType => 7;

    public string MessageName => "DataArray.GetDataArrayMetadataResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBlockHeader(ArrayMetadata.Count);
        foreach (var (arrayMetadataKey, arrayMetadataValue) in ArrayMetadata)
        {
            writer.WriteString(arrayMetadataKey);
            arrayMetadataValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static GetDataArrayMetadataResponse Read(ref EtpReader reader)
    {
        var arrayMetadata = new Dictionary<string, DataArrayMetadata>(StringComparer.Ordinal);
        for (var arrayMetadataCount = reader.ReadBlockCount(); arrayMetadataCount != 0; arrayMetadataCount = reader.ReadBlockCount())
        {
            for (var arrayMetadataIndex = 0; arrayMetadataIndex < arrayMetadataCount; arrayMetadataIndex++)
            {
                var arrayMetadataKey = reader.ReadString();
                arrayMetadata[arrayMetadataKey] = DataArrayMetadata.Read(ref reader);
            }
        }

        return new GetDataArrayMetadataResponse
        {
            ArrayMetadata = arrayMetadata,
        };
    }
}
