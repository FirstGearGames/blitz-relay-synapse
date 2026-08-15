using BlitzRelay.Protocol;
using Microsoft.Extensions.Logging;
using SynapseSocket.Core.Events;
using System.Net;

namespace BlitzRelay.Networking;

internal static partial class Log
{
	[LoggerMessage(LogLevel.Error, "Missing required connection key environment variable {EnvironmentVariableName}.")]
	public static partial void MissingRequiredConnectionKey(ILogger logger, string environmentVariableName);

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to start relay on UDP port {Port}.")]
	public static partial void FailedToStartRelay(ILogger logger, int port, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to bind UDP port {Port} for IPv6; falling back to IPv4 only.")]
	public static partial void FailedToBindDualStack(ILogger logger, int port, Exception exception);

	[LoggerMessage(Level = LogLevel.Information, Message = "Relay server started on UDP port {Port} with MaximumTransmissionUnit={MaximumTransmissionUnit}, KeepAliveInterval={KeepAliveInterval}ms, Timeout={Timeout}ms.")]
	public static partial void RelayServerStarted(ILogger logger, int port, uint maximumTransmissionUnit, uint keepAliveInterval, uint timeout);

	[LoggerMessage(Level = LogLevel.Information, Message = "Relay server stopping.")]
	public static partial void RelayServerStopping(ILogger logger);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Rejected connection from {RemoteEndPoint}: reason={Reason}, message={Message}.")]
	public static partial void ConnectionRejected(ILogger logger, IPEndPoint? remoteEndPoint, ConnectionRejectedReason reason, string? message);

	[LoggerMessage(Level = LogLevel.Information, Message = "Peer connected: signature=0x{PeerSignature:X16}, endpoint={Endpoint}.")]
	public static partial void PeerConnected(ILogger logger, ulong peerSignature, string endpoint);

	[LoggerMessage(Level = LogLevel.Information, Message = "Peer disconnected: signature=0x{PeerSignature:X16}, endpoint={Endpoint}, role={Role}.")]
	public static partial void PeerDisconnected(ILogger logger, ulong peerSignature, string endpoint, PeerRole role);

	[LoggerMessage(Level = LogLevel.Information, Message = "Room {RoomCode} was torn down because the host disconnected.")]
	public static partial void RoomTornDownBecauseHostDisconnected(ILogger logger, string roomCode);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Received relay payload from peer 0x{PeerSignature:X16}: bytes={Length}, reliable={IsReliable}.")]
	public static partial void RelayPayloadReceived(ILogger logger, ulong peerSignature, int length, bool isReliable);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} sent unknown relay message type 0x{MessageType:X2}.")]
	public static partial void UnknownRelayMessageType(ILogger logger, ulong peerSignature, byte messageType);

	[LoggerMessage(Level = LogLevel.Warning, Message = "SynapseSocket violation from {RemoteEndPoint} (peer 0x{PeerSignature:X16}): reason={Reason}, bytes={PacketSize}, details={Details}.")]
	public static partial void TransportViolationDetected(ILogger logger, IPEndPoint remoteEndPoint, ulong peerSignature, ViolationReason reason, int packetSize, string? details);

	[LoggerMessage(Level = LogLevel.Debug, Message = "SynapseSocket closed peer 0x{PeerSignature:X16} ({RemoteEndPoint}): reason={Reason}.")]
	public static partial void TransportPeerLifecycleNotice(ILogger logger, IPEndPoint remoteEndPoint, ulong peerSignature, ViolationReason reason);

	[LoggerMessage(Level = LogLevel.Error, Message = "Unhandled SynapseSocket exception.")]
	public static partial void TransportUnhandledException(ILogger logger, Exception exception);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Peer 0x{PeerSignature:X16} authenticated.")]
	public static partial void PeerAuthenticated(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} presented an invalid connection key.")]
	public static partial void InvalidConnectionKey(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} did not authenticate in time.")]
	public static partial void AuthenticationTimedOut(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Ignored {MessageType} from unauthenticated peer 0x{PeerSignature:X16}.")]
	public static partial void MessageBeforeAuthentication(ILogger logger, ulong peerSignature, MessageType messageType);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Invalid HostRegister from peer 0x{PeerSignature:X16}.")]
	public static partial void InvalidHostRegister(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Invalid HostRegister from peer 0x{PeerSignature:X16}: maximumClients={MaximumClients} must be at least 1.")]
	public static partial void InvalidHostRegisterMaximumClients(ILogger logger, ulong peerSignature, ushort maximumClients);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} attempted HostRegister while already assigned role {Role}.")]
	public static partial void HostRegisterWhileAlreadyAssignedRole(ILogger logger, ulong peerSignature, PeerRole role);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} attempted to create duplicate room {RoomCode}.")]
	public static partial void DuplicateRoomCreateAttempt(ILogger logger, ulong peerSignature, string roomCode);

	[LoggerMessage(Level = LogLevel.Information, Message = "Room created: roomCode={RoomCode}, hostSignature=0x{PeerSignature:X16}, maximumClients={MaximumClients}.")]
	public static partial void RoomCreated(ILogger logger, string roomCode, ulong peerSignature, int maximumClients);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Invalid ClientJoin from peer 0x{PeerSignature:X16}.")]
	public static partial void InvalidClientJoin(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} attempted ClientJoin while already assigned role {Role}.")]
	public static partial void ClientJoinWhileAlreadyAssignedRole(ILogger logger, ulong peerSignature, PeerRole role);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} attempted to join missing room {RoomCode}.")]
	public static partial void JoinMissingRoom(ILogger logger, ulong peerSignature, string roomCode);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} attempted to join full room {RoomCode}.")]
	public static partial void JoinFullRoom(ILogger logger, ulong peerSignature, string roomCode);

	[LoggerMessage(Level = LogLevel.Information, Message = "Peer 0x{PeerSignature:X16} joined room {RoomCode} as virtual client {VirtualClientId}.")]
	public static partial void ClientJoinedRoom(ILogger logger, ulong peerSignature, string roomCode, int virtualClientId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} sent relay data without a room role.")]
	public static partial void RelayDataWithoutRoomRole(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Invalid host data payload from peer 0x{PeerSignature:X16}.")]
	public static partial void InvalidHostDataPayload(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Host peer 0x{PeerSignature:X16} sent data without an assigned room.")]
	public static partial void HostDataWithoutAssignedRoom(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Host peer 0x{PeerSignature:X16} broadcast {PayloadLength} bytes on game channel {GameChannel} to {RecipientCount} clients.")]
	public static partial void HostBroadcast(ILogger logger, ulong peerSignature, int payloadLength, byte gameChannel, int recipientCount);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Host peer 0x{PeerSignature:X16} targeted unknown virtual client {VirtualClientId}.")]
	public static partial void HostTargetedUnknownVirtualClient(ILogger logger, ulong peerSignature, int virtualClientId);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Invalid client data payload from peer 0x{PeerSignature:X16}.")]
	public static partial void InvalidClientDataPayload(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Client peer 0x{PeerSignature:X16} sent data without an active host room.")]
	public static partial void ClientDataWithoutActiveHostRoom(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Peer 0x{PeerSignature:X16} attempted kick without host role.")]
	public static partial void KickWithoutHostRole(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Invalid kick payload from peer 0x{PeerSignature:X16}.")]
	public static partial void InvalidKickPayload(ILogger logger, ulong peerSignature);

	[LoggerMessage(Level = LogLevel.Debug, Message = "Host peer 0x{PeerSignature:X16} attempted to kick unknown virtual client {VirtualClientId}.")]
	public static partial void KickUnknownVirtualClient(ILogger logger, ulong peerSignature, int virtualClientId);

	[LoggerMessage(Level = LogLevel.Information, Message = "Host peer 0x{PeerSignature:X16} kicked virtual client {VirtualClientId} (peer 0x{ClientPeerSignature:X16}).")]
	public static partial void ClientKickedByHost(ILogger logger, ulong peerSignature, int virtualClientId, ulong clientPeerSignature);

	[LoggerMessage(Level = LogLevel.Information, Message = "Admin kicked virtual client {VirtualClientId} (peer 0x{ClientPeerSignature:X16}) from room {RoomCode}.")]
	public static partial void ClientKickedByAdmin(ILogger logger, int virtualClientId, ulong clientPeerSignature, string roomCode);

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to send relay payload to peer 0x{PeerSignature:X16} with reliable={IsReliable}.")]
	public static partial void FailedToSendRelayPayload(ILogger logger, ulong peerSignature, bool isReliable, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Dropped oversized unreliable relay payload to peer 0x{PeerSignature:X16}: bytes={PayloadLength}, maxUnreliableBytes={MaxUnreliablePayloadLength}.")]
	public static partial void OversizedUnreliableRelayPayloadDropped(ILogger logger, ulong peerSignature, int payloadLength, int maxUnreliablePayloadLength);

	[LoggerMessage(Level = LogLevel.Warning, Message = "SynapseSocket rejected relay payload to peer 0x{PeerSignature:X16}: bytes={PayloadLength}, maxReliableBytes={MaxReliablePayloadLength}.")]
	public static partial void RelayPayloadRejectedAsTooLarge(ILogger logger, ulong peerSignature, int payloadLength, int maxReliablePayloadLength);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Disconnecting peer 0x{PeerSignature:X16} because its reliable relay queue is backed up: bytes={PayloadLength}, threshold={Threshold}.")]
	public static partial void ReliableRelayRecipientDisconnectedDueToBackpressure(ILogger logger, ulong peerSignature, int payloadLength, uint threshold, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to disconnect peer 0x{PeerSignature:X16}.")]
	public static partial void FailedToDisconnectPeer(ILogger logger, ulong peerSignature, Exception exception);
}
