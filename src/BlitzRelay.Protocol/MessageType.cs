using JetBrains.Annotations;

namespace BlitzRelay.Protocol;

[PublicAPI]
public enum MessageType : byte
{
	HostRegister = 0x01,

	ClientJoin = 0x02,

	Connected = 0x03,

	Disconnected = 0x04,

	Data = 0x05,

	Kick = 0x06,

	RoomCreated = 0x07,

	JoinSuccess = 0x08,

	Error = 0x09,

	HostClaim = 0x0A,

	HostPromoted = 0x0B,

	HostUnavailable = 0x0C,

	HostAvailable = 0x0D,

	HostPromotionAck = 0x0E,

	Authenticate = 0x0F,
}
