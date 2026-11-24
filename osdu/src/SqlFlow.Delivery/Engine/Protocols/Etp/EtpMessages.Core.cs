// The messages of ETP 1.2 protocol Core (0), written from the pinned protocol
// (osdu/specs/reservoir-ddms/etp-1.2.avpr) as EtpMessages.cs says: one C# record per message, its properties in the
// schema's field order.

using System.Collections.ObjectModel;

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP 1.2 <c>Core.RequestSession</c> message (protocol 0, message type 1).
/// </summary>
public sealed record RequestSession : IEtpMessage
{
    public required string ApplicationName { get; init; }

    public required string ApplicationVersion { get; init; }

    public required Guid ClientInstanceId { get; init; }

    public required IReadOnlyList<SupportedProtocol> RequestedProtocols { get; init; }

    public required IReadOnlyList<SupportedDataObject> SupportedDataObjects { get; init; }

    public IReadOnlyList<string> SupportedCompression { get; init; } = [];

    public IReadOnlyList<string> SupportedFormats { get; init; } = ["xml"];

    public required long CurrentDateTime { get; init; }

    public required long EarliestRetainedChangeTime { get; init; }

    public bool ServerAuthorizationRequired { get; init; }

    public IReadOnlyDictionary<string, DataValue> EndpointCapabilities { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public int Protocol => 0;

    public int MessageType => 1;

    public string MessageName => "Core.RequestSession";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(ApplicationName);
        writer.WriteString(ApplicationVersion);
        writer.WriteUuid(ClientInstanceId);

        writer.WriteBlockHeader(RequestedProtocols.Count);
        foreach (var requestedProtocolsItem in RequestedProtocols)
        {
            requestedProtocolsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedDataObjects.Count);
        foreach (var supportedDataObjectsItem in SupportedDataObjects)
        {
            supportedDataObjectsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedCompression.Count);
        foreach (var supportedCompressionItem in SupportedCompression)
        {
            writer.WriteString(supportedCompressionItem);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedFormats.Count);
        foreach (var supportedFormatsItem in SupportedFormats)
        {
            writer.WriteString(supportedFormatsItem);
        }
        writer.WriteBlockEnd();

        writer.WriteLong(CurrentDateTime);
        writer.WriteLong(EarliestRetainedChangeTime);
        writer.WriteBoolean(ServerAuthorizationRequired);

        writer.WriteBlockHeader(EndpointCapabilities.Count);
        foreach (var (endpointCapabilitiesKey, endpointCapabilitiesValue) in EndpointCapabilities)
        {
            writer.WriteString(endpointCapabilitiesKey);
            endpointCapabilitiesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static RequestSession Read(ref EtpReader reader)
    {
        var applicationName = reader.ReadString();
        var applicationVersion = reader.ReadString();
        var clientInstanceId = reader.ReadUuid();

        var requestedProtocols = new List<SupportedProtocol>();
        for (var requestedProtocolsCount = reader.ReadBlockCount(); requestedProtocolsCount != 0; requestedProtocolsCount = reader.ReadBlockCount())
        {
            requestedProtocols.EnsureCapacity(requestedProtocols.Count + requestedProtocolsCount);
            for (var requestedProtocolsIndex = 0; requestedProtocolsIndex < requestedProtocolsCount; requestedProtocolsIndex++)
            {
                requestedProtocols.Add(SupportedProtocol.Read(ref reader));
            }
        }

        var supportedDataObjects = new List<SupportedDataObject>();
        for (var supportedDataObjectsCount = reader.ReadBlockCount(); supportedDataObjectsCount != 0; supportedDataObjectsCount = reader.ReadBlockCount())
        {
            supportedDataObjects.EnsureCapacity(supportedDataObjects.Count + supportedDataObjectsCount);
            for (var supportedDataObjectsIndex = 0; supportedDataObjectsIndex < supportedDataObjectsCount; supportedDataObjectsIndex++)
            {
                supportedDataObjects.Add(SupportedDataObject.Read(ref reader));
            }
        }

        var supportedCompression = new List<string>();
        for (var supportedCompressionCount = reader.ReadBlockCount(); supportedCompressionCount != 0; supportedCompressionCount = reader.ReadBlockCount())
        {
            supportedCompression.EnsureCapacity(supportedCompression.Count + supportedCompressionCount);
            for (var supportedCompressionIndex = 0; supportedCompressionIndex < supportedCompressionCount; supportedCompressionIndex++)
            {
                supportedCompression.Add(reader.ReadString());
            }
        }

        var supportedFormats = new List<string>();
        for (var supportedFormatsCount = reader.ReadBlockCount(); supportedFormatsCount != 0; supportedFormatsCount = reader.ReadBlockCount())
        {
            supportedFormats.EnsureCapacity(supportedFormats.Count + supportedFormatsCount);
            for (var supportedFormatsIndex = 0; supportedFormatsIndex < supportedFormatsCount; supportedFormatsIndex++)
            {
                supportedFormats.Add(reader.ReadString());
            }
        }

        var currentDateTime = reader.ReadLong();
        var earliestRetainedChangeTime = reader.ReadLong();
        var serverAuthorizationRequired = reader.ReadBoolean();

        var endpointCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var endpointCapabilitiesCount = reader.ReadBlockCount(); endpointCapabilitiesCount != 0; endpointCapabilitiesCount = reader.ReadBlockCount())
        {
            for (var endpointCapabilitiesIndex = 0; endpointCapabilitiesIndex < endpointCapabilitiesCount; endpointCapabilitiesIndex++)
            {
                var endpointCapabilitiesKey = reader.ReadString();
                endpointCapabilities[endpointCapabilitiesKey] = DataValue.Read(ref reader);
            }
        }

        return new RequestSession
        {
            ApplicationName = applicationName,
            ApplicationVersion = applicationVersion,
            ClientInstanceId = clientInstanceId,
            RequestedProtocols = requestedProtocols,
            SupportedDataObjects = supportedDataObjects,
            SupportedCompression = supportedCompression,
            SupportedFormats = supportedFormats,
            CurrentDateTime = currentDateTime,
            EarliestRetainedChangeTime = earliestRetainedChangeTime,
            ServerAuthorizationRequired = serverAuthorizationRequired,
            EndpointCapabilities = endpointCapabilities,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.OpenSession</c> message (protocol 0, message type 2).
/// </summary>
public sealed record OpenSession : IEtpMessage
{
    public required string ApplicationName { get; init; }

    public required string ApplicationVersion { get; init; }

    public required Guid ServerInstanceId { get; init; }

    public required IReadOnlyList<SupportedProtocol> SupportedProtocols { get; init; }

    public required IReadOnlyList<SupportedDataObject> SupportedDataObjects { get; init; }

    public string SupportedCompression { get; init; } = string.Empty;

    public IReadOnlyList<string> SupportedFormats { get; init; } = ["xml"];

    public required long CurrentDateTime { get; init; }

    public required long EarliestRetainedChangeTime { get; init; }

    public required Guid SessionId { get; init; }

    public IReadOnlyDictionary<string, DataValue> EndpointCapabilities { get; init; } = ReadOnlyDictionary<string, DataValue>.Empty;

    public int Protocol => 0;

    public int MessageType => 2;

    public string MessageName => "Core.OpenSession";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(ApplicationName);
        writer.WriteString(ApplicationVersion);
        writer.WriteUuid(ServerInstanceId);

        writer.WriteBlockHeader(SupportedProtocols.Count);
        foreach (var supportedProtocolsItem in SupportedProtocols)
        {
            supportedProtocolsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteBlockHeader(SupportedDataObjects.Count);
        foreach (var supportedDataObjectsItem in SupportedDataObjects)
        {
            supportedDataObjectsItem.Write(writer);
        }
        writer.WriteBlockEnd();

        writer.WriteString(SupportedCompression);

        writer.WriteBlockHeader(SupportedFormats.Count);
        foreach (var supportedFormatsItem in SupportedFormats)
        {
            writer.WriteString(supportedFormatsItem);
        }
        writer.WriteBlockEnd();

        writer.WriteLong(CurrentDateTime);
        writer.WriteLong(EarliestRetainedChangeTime);
        writer.WriteUuid(SessionId);

        writer.WriteBlockHeader(EndpointCapabilities.Count);
        foreach (var (endpointCapabilitiesKey, endpointCapabilitiesValue) in EndpointCapabilities)
        {
            writer.WriteString(endpointCapabilitiesKey);
            endpointCapabilitiesValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static OpenSession Read(ref EtpReader reader)
    {
        var applicationName = reader.ReadString();
        var applicationVersion = reader.ReadString();
        var serverInstanceId = reader.ReadUuid();

        var supportedProtocols = new List<SupportedProtocol>();
        for (var supportedProtocolsCount = reader.ReadBlockCount(); supportedProtocolsCount != 0; supportedProtocolsCount = reader.ReadBlockCount())
        {
            supportedProtocols.EnsureCapacity(supportedProtocols.Count + supportedProtocolsCount);
            for (var supportedProtocolsIndex = 0; supportedProtocolsIndex < supportedProtocolsCount; supportedProtocolsIndex++)
            {
                supportedProtocols.Add(SupportedProtocol.Read(ref reader));
            }
        }

        var supportedDataObjects = new List<SupportedDataObject>();
        for (var supportedDataObjectsCount = reader.ReadBlockCount(); supportedDataObjectsCount != 0; supportedDataObjectsCount = reader.ReadBlockCount())
        {
            supportedDataObjects.EnsureCapacity(supportedDataObjects.Count + supportedDataObjectsCount);
            for (var supportedDataObjectsIndex = 0; supportedDataObjectsIndex < supportedDataObjectsCount; supportedDataObjectsIndex++)
            {
                supportedDataObjects.Add(SupportedDataObject.Read(ref reader));
            }
        }

        var supportedCompression = reader.ReadString();

        var supportedFormats = new List<string>();
        for (var supportedFormatsCount = reader.ReadBlockCount(); supportedFormatsCount != 0; supportedFormatsCount = reader.ReadBlockCount())
        {
            supportedFormats.EnsureCapacity(supportedFormats.Count + supportedFormatsCount);
            for (var supportedFormatsIndex = 0; supportedFormatsIndex < supportedFormatsCount; supportedFormatsIndex++)
            {
                supportedFormats.Add(reader.ReadString());
            }
        }

        var currentDateTime = reader.ReadLong();
        var earliestRetainedChangeTime = reader.ReadLong();
        var sessionId = reader.ReadUuid();

        var endpointCapabilities = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        for (var endpointCapabilitiesCount = reader.ReadBlockCount(); endpointCapabilitiesCount != 0; endpointCapabilitiesCount = reader.ReadBlockCount())
        {
            for (var endpointCapabilitiesIndex = 0; endpointCapabilitiesIndex < endpointCapabilitiesCount; endpointCapabilitiesIndex++)
            {
                var endpointCapabilitiesKey = reader.ReadString();
                endpointCapabilities[endpointCapabilitiesKey] = DataValue.Read(ref reader);
            }
        }

        return new OpenSession
        {
            ApplicationName = applicationName,
            ApplicationVersion = applicationVersion,
            ServerInstanceId = serverInstanceId,
            SupportedProtocols = supportedProtocols,
            SupportedDataObjects = supportedDataObjects,
            SupportedCompression = supportedCompression,
            SupportedFormats = supportedFormats,
            CurrentDateTime = currentDateTime,
            EarliestRetainedChangeTime = earliestRetainedChangeTime,
            SessionId = sessionId,
            EndpointCapabilities = endpointCapabilities,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.CloseSession</c> message (protocol 0, message type 5).
/// </summary>
public sealed record CloseSession : IEtpMessage
{
    public string Reason { get; init; } = string.Empty;

    public int Protocol => 0;

    public int MessageType => 5;

    public string MessageName => "Core.CloseSession";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Reason);
    }

    public static CloseSession Read(ref EtpReader reader)
    {
        var reason = reader.ReadString();

        return new CloseSession
        {
            Reason = reason,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Authorize</c> message (protocol 0, message type 6).
/// </summary>
public sealed record Authorize : IEtpMessage
{
    public required string Authorization { get; init; }

    public required IReadOnlyDictionary<string, string> SupplementalAuthorization { get; init; }

    public int Protocol => 0;

    public int MessageType => 6;

    public string MessageName => "Core.Authorize";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString(Authorization);

        writer.WriteBlockHeader(SupplementalAuthorization.Count);
        foreach (var (supplementalAuthorizationKey, supplementalAuthorizationValue) in SupplementalAuthorization)
        {
            writer.WriteString(supplementalAuthorizationKey);
            writer.WriteString(supplementalAuthorizationValue);
        }
        writer.WriteBlockEnd();
    }

    public static Authorize Read(ref EtpReader reader)
    {
        var authorization = reader.ReadString();

        var supplementalAuthorization = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var supplementalAuthorizationCount = reader.ReadBlockCount(); supplementalAuthorizationCount != 0; supplementalAuthorizationCount = reader.ReadBlockCount())
        {
            for (var supplementalAuthorizationIndex = 0; supplementalAuthorizationIndex < supplementalAuthorizationCount; supplementalAuthorizationIndex++)
            {
                var supplementalAuthorizationKey = reader.ReadString();
                supplementalAuthorization[supplementalAuthorizationKey] = reader.ReadString();
            }
        }

        return new Authorize
        {
            Authorization = authorization,
            SupplementalAuthorization = supplementalAuthorization,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.AuthorizeResponse</c> message (protocol 0, message type 7).
/// </summary>
public sealed record AuthorizeResponse : IEtpMessage
{
    public required bool Success { get; init; }

    public required IReadOnlyList<string> Challenges { get; init; }

    public int Protocol => 0;

    public int MessageType => 7;

    public string MessageName => "Core.AuthorizeResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBoolean(Success);

        writer.WriteBlockHeader(Challenges.Count);
        foreach (var challengesItem in Challenges)
        {
            writer.WriteString(challengesItem);
        }
        writer.WriteBlockEnd();
    }

    public static AuthorizeResponse Read(ref EtpReader reader)
    {
        var success = reader.ReadBoolean();

        var challenges = new List<string>();
        for (var challengesCount = reader.ReadBlockCount(); challengesCount != 0; challengesCount = reader.ReadBlockCount())
        {
            challenges.EnsureCapacity(challenges.Count + challengesCount);
            for (var challengesIndex = 0; challengesIndex < challengesCount; challengesIndex++)
            {
                challenges.Add(reader.ReadString());
            }
        }

        return new AuthorizeResponse
        {
            Success = success,
            Challenges = challenges,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Ping</c> message (protocol 0, message type 8).
/// </summary>
public sealed record Ping : IEtpMessage
{
    public required long CurrentDateTime { get; init; }

    public int Protocol => 0;

    public int MessageType => 8;

    public string MessageName => "Core.Ping";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLong(CurrentDateTime);
    }

    public static Ping Read(ref EtpReader reader)
    {
        var currentDateTime = reader.ReadLong();

        return new Ping
        {
            CurrentDateTime = currentDateTime,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Pong</c> message (protocol 0, message type 9).
/// </summary>
public sealed record Pong : IEtpMessage
{
    public required long CurrentDateTime { get; init; }

    public int Protocol => 0;

    public int MessageType => 9;

    public string MessageName => "Core.Pong";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLong(CurrentDateTime);
    }

    public static Pong Read(ref EtpReader reader)
    {
        var currentDateTime = reader.ReadLong();

        return new Pong
        {
            CurrentDateTime = currentDateTime,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.ProtocolException</c> message (protocol 0, message type 1000).
/// </summary>
public sealed record ProtocolFailure : IEtpMessage
{
    public ErrorInfo? Error { get; init; }

    public IReadOnlyDictionary<string, ErrorInfo> Errors { get; init; } = ReadOnlyDictionary<string, ErrorInfo>.Empty;

    public int Protocol => 0;

    public int MessageType => 1000;

    public string MessageName => "Core.ProtocolException";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Error is { } errorValue)
        {
            writer.WriteUnion(1);
            errorValue.Write(writer);
        }
        else
        {
            writer.WriteUnion(0);
        }

        writer.WriteBlockHeader(Errors.Count);
        foreach (var (errorsKey, errorsValue) in Errors)
        {
            writer.WriteString(errorsKey);
            errorsValue.Write(writer);
        }
        writer.WriteBlockEnd();
    }

    public static ProtocolFailure Read(ref EtpReader reader)
    {
        ErrorInfo? error = reader.ReadUnion(2) == 1 ? ErrorInfo.Read(ref reader) : null;

        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);
        for (var errorsCount = reader.ReadBlockCount(); errorsCount != 0; errorsCount = reader.ReadBlockCount())
        {
            for (var errorsIndex = 0; errorsIndex < errorsCount; errorsIndex++)
            {
                var errorsKey = reader.ReadString();
                errors[errorsKey] = ErrorInfo.Read(ref reader);
            }
        }

        return new ProtocolFailure
        {
            Error = error,
            Errors = errors,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Core.Acknowledge</c> message (protocol 0, message type 1001).
/// </summary>
public sealed record Acknowledge : IEtpMessage
{
    public int Protocol => 0;

    public int MessageType => 1001;

    public string MessageName => "Core.Acknowledge";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
    }

    public static Acknowledge Read(ref EtpReader reader)
    {
        return new Acknowledge
        {
        };
    }
}
