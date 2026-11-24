// The messages of ETP 1.2 protocol Transaction (18), written from the pinned protocol
// (osdu/specs/reservoir-ddms/etp-1.2.avpr) as EtpMessages.cs says: one C# record per message, its properties in the
// schema's field order.

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// The ETP 1.2 <c>Transaction.StartTransaction</c> message (protocol 18, message type 1).
/// </summary>
public sealed record StartTransaction : IEtpMessage
{
    public required bool ReadOnly { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<string> DataspaceUris { get; init; } = [""];

    public int Protocol => 18;

    public int MessageType => 1;

    public string MessageName => "Transaction.StartTransaction";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBoolean(ReadOnly);
        writer.WriteString(Message);

        writer.WriteBlockHeader(DataspaceUris.Count);
        foreach (var dataspaceUrisItem in DataspaceUris)
        {
            writer.WriteString(dataspaceUrisItem);
        }
        writer.WriteBlockEnd();
    }

    public static StartTransaction Read(ref EtpReader reader)
    {
        var readOnly = reader.ReadBoolean();
        var message = reader.ReadString();

        var dataspaceUris = new List<string>();
        for (var dataspaceUrisCount = reader.ReadBlockCount(); dataspaceUrisCount != 0; dataspaceUrisCount = reader.ReadBlockCount())
        {
            dataspaceUris.EnsureCapacity(dataspaceUris.Count + dataspaceUrisCount);
            for (var dataspaceUrisIndex = 0; dataspaceUrisIndex < dataspaceUrisCount; dataspaceUrisIndex++)
            {
                dataspaceUris.Add(reader.ReadString());
            }
        }

        return new StartTransaction
        {
            ReadOnly = readOnly,
            Message = message,
            DataspaceUris = dataspaceUris,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.StartTransactionResponse</c> message (protocol 18, message type 2).
/// </summary>
public sealed record StartTransactionResponse : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public bool Successful { get; init; } = true;

    public string FailureReason { get; init; } = string.Empty;

    public int Protocol => 18;

    public int MessageType => 2;

    public string MessageName => "Transaction.StartTransactionResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
        writer.WriteBoolean(Successful);
        writer.WriteString(FailureReason);
    }

    public static StartTransactionResponse Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();
        var successful = reader.ReadBoolean();
        var failureReason = reader.ReadString();

        return new StartTransactionResponse
        {
            TransactionUuid = transactionUuid,
            Successful = successful,
            FailureReason = failureReason,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.CommitTransaction</c> message (protocol 18, message type 3).
/// </summary>
public sealed record CommitTransaction : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public int Protocol => 18;

    public int MessageType => 3;

    public string MessageName => "Transaction.CommitTransaction";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
    }

    public static CommitTransaction Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();

        return new CommitTransaction
        {
            TransactionUuid = transactionUuid,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.CommitTransactionResponse</c> message (protocol 18, message type 5).
/// </summary>
public sealed record CommitTransactionResponse : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public bool Successful { get; init; } = true;

    public string FailureReason { get; init; } = string.Empty;

    public int Protocol => 18;

    public int MessageType => 5;

    public string MessageName => "Transaction.CommitTransactionResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
        writer.WriteBoolean(Successful);
        writer.WriteString(FailureReason);
    }

    public static CommitTransactionResponse Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();
        var successful = reader.ReadBoolean();
        var failureReason = reader.ReadString();

        return new CommitTransactionResponse
        {
            TransactionUuid = transactionUuid,
            Successful = successful,
            FailureReason = failureReason,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.RollbackTransaction</c> message (protocol 18, message type 4).
/// </summary>
public sealed record RollbackTransaction : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public int Protocol => 18;

    public int MessageType => 4;

    public string MessageName => "Transaction.RollbackTransaction";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
    }

    public static RollbackTransaction Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();

        return new RollbackTransaction
        {
            TransactionUuid = transactionUuid,
        };
    }
}

/// <summary>
/// The ETP 1.2 <c>Transaction.RollbackTransactionResponse</c> message (protocol 18, message type 6).
/// </summary>
public sealed record RollbackTransactionResponse : IEtpMessage
{
    public required Guid TransactionUuid { get; init; }

    public bool Successful { get; init; } = true;

    public string FailureReason { get; init; } = string.Empty;

    public int Protocol => 18;

    public int MessageType => 6;

    public string MessageName => "Transaction.RollbackTransactionResponse";

    public void Write(EtpWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteUuid(TransactionUuid);
        writer.WriteBoolean(Successful);
        writer.WriteString(FailureReason);
    }

    public static RollbackTransactionResponse Read(ref EtpReader reader)
    {
        var transactionUuid = reader.ReadUuid();
        var successful = reader.ReadBoolean();
        var failureReason = reader.ReadString();

        return new RollbackTransactionResponse
        {
            TransactionUuid = transactionUuid,
            Successful = successful,
            FailureReason = failureReason,
        };
    }
}
