using BlitzRelay.Protocol;
using SynapseSocket.Core.Configuration;
using SynapseSocket.Packets;

namespace BlitzRelay.Networking;

internal static class RelayBackpressurePolicy
{
	// SynapseSocket owns the reliable backlog: it refuses a reliable send once this many packets on a connection are
	// still awaiting acknowledgement. The relay turns that refusal into a disconnect of the lagging recipient.
	public const uint PendingReliablePacketsBeforeDisconnect = 2048;

	// The wire caps a segmented payload at 255 segments regardless of configuration.
	private const int ProtocolMaximumSegments = 255;

	// An unreliable relay payload is never segmented, so it has to fit in a single datagram. SynapseSocket sizes both
	// channels' unsegmented limit off the reliable header so the two channels share one threshold.
	public static int MaximumUnreliablePayloadLength(uint maximumTransmissionUnit)
	{
		return (int)maximumTransmissionUnit - PacketHeader.TypeSize - PacketHeader.SequenceSize;
	}

	public static bool ShouldDropUnreliableRelayPayload(int payloadLength, int maximumUnreliablePayloadLength, bool isReliable)
	{
		return !isReliable && payloadLength > maximumUnreliablePayloadLength;
	}

	// A reliable relay payload is segmented, and the recipient rejects an assembly that would exceed its configured
	// reassembly cap, so the relay refuses to emit one that large in the first place.
	public static int MaximumReliablePayloadLength(uint maximumTransmissionUnit, uint maximumReassembledPacketSize)
	{
		int maximumSegments = maximumReassembledPacketSize == SecurityConfig.DisabledMaximumReassembledPacketSize ? ProtocolMaximumSegments : (int)(maximumReassembledPacketSize / maximumTransmissionUnit);

		if (maximumSegments > ProtocolMaximumSegments) maximumSegments = ProtocolMaximumSegments;

		return maximumSegments * ((int)maximumTransmissionUnit - PacketHeader.ComputeHeaderSize(PacketType.ReliableSegmented));
	}

	public static int ClientRelayPayloadLength(int gamePayloadLength)
	{
		return MessageCodec.ClientDataHeaderSize + gamePayloadLength;
	}

	public static int HostRelayPayloadLength(int gamePayloadLength)
	{
		return MessageCodec.HostDataHeaderSize + gamePayloadLength;
	}
}
