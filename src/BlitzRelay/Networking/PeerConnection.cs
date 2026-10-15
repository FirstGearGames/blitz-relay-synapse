using BlitzRelay.Rooms;
using SynapseSocket.Connections;

namespace BlitzRelay.Networking;

internal sealed class PeerConnection(SynapseConnection connection)
{
	public SynapseConnection Connection { get; } = connection;

	public bool IsConnected { get; set; } = true;

	public bool IsAuthenticated { get; set; }

	public DateTimeOffset AuthenticationDeadlineUtc { get; set; }

	// The server side of a room sends every client's packets down this one connection, so a busy room crosses a per-peer rate
	// limit no single peer's own traffic comes near. The host is exempted from it as it takes the role, and loses the
	// exemption with the role.
	public PeerRole Role
	{
		get => _role;
		set
		{
			_role = value;

			Connection.IsRateLimitExempt = value == PeerRole.Host;
		}
	}

	private PeerRole _role;

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
