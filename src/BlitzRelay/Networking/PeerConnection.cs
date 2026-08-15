using BlitzRelay.Rooms;
using SynapseSocket.Connections;

namespace BlitzRelay.Networking;

internal sealed class PeerConnection(SynapseConnection connection)
{
	public SynapseConnection Connection { get; } = connection;

	public bool IsConnected { get; set; } = true;

	public bool IsAuthenticated { get; set; }

	public DateTimeOffset AuthenticationDeadlineUtc { get; set; }

	public PeerRole Role { get; set; }

	public Room? Room { get; set; }

	public int VirtualClientId { get; set; }

	public long RoomJoinOrder { get; set; }

	public ulong Signature
	{
		get => Connection.Signature;
	}

	public string Endpoint
	{
		get => Connection.RemoteEndPoint.ToString();
	}

	public void Clear()
	{
		Role = PeerRole.None;

		Room = null;

		VirtualClientId = 0;

		RoomJoinOrder = 0;
	}
}
