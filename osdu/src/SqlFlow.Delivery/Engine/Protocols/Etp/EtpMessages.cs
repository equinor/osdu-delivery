// Written from the pinned ETP 1.2 protocol, osdu/specs/reservoir-ddms/etp-1.2.avpr: one C# record per message of the
// protocols this route speaks (Core 0, Discovery 3, Store 4, DataArray 9, Transaction 18, Dataspace 24 and
// DataspaceOSDU 2424), its properties in the schema's field order, in a file per protocol beside this one
// (EtpMessages.Core.cs and the others). EtpSchemaTests round-trips every one of them against a codec driven by that
// same file, so the records and the schema cannot drift apart.

namespace SqlFlow.Delivery.Engine.Protocols.Etp;

/// <summary>
/// Every ETP message this client sends or understands, by the protocol and message type its header carries.
/// A message outside this set is not an error: an ETP server may send one, and the session ignores it.
/// </summary>
public static class EtpMessages
{
    /// <summary>Reads a message body, or null when the header names one this client does not implement.</summary>
    public static IEtpMessage? Read(int protocol, int messageType, ref EtpReader reader) => (protocol, messageType) switch
    {
        (0, 1) => RequestSession.Read(ref reader),
        (0, 2) => OpenSession.Read(ref reader),
        (0, 5) => CloseSession.Read(ref reader),
        (0, 6) => Authorize.Read(ref reader),
        (0, 7) => AuthorizeResponse.Read(ref reader),
        (0, 8) => Ping.Read(ref reader),
        (0, 9) => Pong.Read(ref reader),
        (0, 1000) => ProtocolFailure.Read(ref reader),
        (0, 1001) => Acknowledge.Read(ref reader),
        (24, 1) => GetDataspaces.Read(ref reader),
        (24, 2) => GetDataspacesResponse.Read(ref reader),
        (24, 3) => PutDataspaces.Read(ref reader),
        (24, 6) => PutDataspacesResponse.Read(ref reader),
        (24, 4) => DeleteDataspaces.Read(ref reader),
        (24, 5) => DeleteDataspacesResponse.Read(ref reader),
        (2424, 1) => GetDataspaceInfo.Read(ref reader),
        (2424, 2) => GetDataspaceInfoResponse.Read(ref reader),
        (2424, 5) => LockDataspaces.Read(ref reader),
        (2424, 6) => LockDataspacesResponse.Read(ref reader),
        (4, 1) => GetDataObjects.Read(ref reader),
        (4, 4) => GetDataObjectsResponse.Read(ref reader),
        (4, 2) => PutDataObjects.Read(ref reader),
        (4, 9) => PutDataObjectsResponse.Read(ref reader),
        (4, 3) => DeleteDataObjects.Read(ref reader),
        (4, 10) => DeleteDataObjectsResponse.Read(ref reader),
        (4, 8) => Chunk.Read(ref reader),
        (9, 2) => GetDataArrays.Read(ref reader),
        (9, 1) => GetDataArraysResponse.Read(ref reader),
        (9, 3) => GetDataSubarrays.Read(ref reader),
        (9, 8) => GetDataSubarraysResponse.Read(ref reader),
        (9, 4) => PutDataArrays.Read(ref reader),
        (9, 10) => PutDataArraysResponse.Read(ref reader),
        (9, 5) => PutDataSubarrays.Read(ref reader),
        (9, 11) => PutDataSubarraysResponse.Read(ref reader),
        (9, 9) => PutUninitializedDataArrays.Read(ref reader),
        (9, 12) => PutUninitializedDataArraysResponse.Read(ref reader),
        (9, 6) => GetDataArrayMetadata.Read(ref reader),
        (9, 7) => GetDataArrayMetadataResponse.Read(ref reader),
        (18, 1) => StartTransaction.Read(ref reader),
        (18, 2) => StartTransactionResponse.Read(ref reader),
        (18, 3) => CommitTransaction.Read(ref reader),
        (18, 5) => CommitTransactionResponse.Read(ref reader),
        (18, 4) => RollbackTransaction.Read(ref reader),
        (18, 6) => RollbackTransactionResponse.Read(ref reader),
        (3, 1) => GetResources.Read(ref reader),
        (3, 4) => GetResourcesResponse.Read(ref reader),
        (3, 7) => GetResourcesEdgesResponse.Read(ref reader),
        _ => null,
    };

    /// <summary>The name of a message, for logs and errors, whether or not this client implements it.</summary>
    public static string Name(int protocol, int messageType) => (protocol, messageType) switch
    {
        (0, 1) => "Core.RequestSession",
        (0, 2) => "Core.OpenSession",
        (0, 5) => "Core.CloseSession",
        (0, 6) => "Core.Authorize",
        (0, 7) => "Core.AuthorizeResponse",
        (0, 8) => "Core.Ping",
        (0, 9) => "Core.Pong",
        (0, 1000) => "Core.ProtocolException",
        (0, 1001) => "Core.Acknowledge",
        (24, 1) => "Dataspace.GetDataspaces",
        (24, 2) => "Dataspace.GetDataspacesResponse",
        (24, 3) => "Dataspace.PutDataspaces",
        (24, 6) => "Dataspace.PutDataspacesResponse",
        (24, 4) => "Dataspace.DeleteDataspaces",
        (24, 5) => "Dataspace.DeleteDataspacesResponse",
        (2424, 1) => "DataspaceOSDU.GetDataspaceInfo",
        (2424, 2) => "DataspaceOSDU.GetDataspaceInfoResponse",
        (2424, 5) => "DataspaceOSDU.LockDataspaces",
        (2424, 6) => "DataspaceOSDU.LockDataspacesResponse",
        (4, 1) => "Store.GetDataObjects",
        (4, 4) => "Store.GetDataObjectsResponse",
        (4, 2) => "Store.PutDataObjects",
        (4, 9) => "Store.PutDataObjectsResponse",
        (4, 3) => "Store.DeleteDataObjects",
        (4, 10) => "Store.DeleteDataObjectsResponse",
        (4, 8) => "Store.Chunk",
        (9, 2) => "DataArray.GetDataArrays",
        (9, 1) => "DataArray.GetDataArraysResponse",
        (9, 3) => "DataArray.GetDataSubarrays",
        (9, 8) => "DataArray.GetDataSubarraysResponse",
        (9, 4) => "DataArray.PutDataArrays",
        (9, 10) => "DataArray.PutDataArraysResponse",
        (9, 5) => "DataArray.PutDataSubarrays",
        (9, 11) => "DataArray.PutDataSubarraysResponse",
        (9, 9) => "DataArray.PutUninitializedDataArrays",
        (9, 12) => "DataArray.PutUninitializedDataArraysResponse",
        (9, 6) => "DataArray.GetDataArrayMetadata",
        (9, 7) => "DataArray.GetDataArrayMetadataResponse",
        (18, 1) => "Transaction.StartTransaction",
        (18, 2) => "Transaction.StartTransactionResponse",
        (18, 3) => "Transaction.CommitTransaction",
        (18, 5) => "Transaction.CommitTransactionResponse",
        (18, 4) => "Transaction.RollbackTransaction",
        (18, 6) => "Transaction.RollbackTransactionResponse",
        (3, 1) => "Discovery.GetResources",
        (3, 4) => "Discovery.GetResourcesResponse",
        (3, 7) => "Discovery.GetResourcesEdgesResponse",
        _ => $"protocol {protocol} message {messageType}",
    };
}
