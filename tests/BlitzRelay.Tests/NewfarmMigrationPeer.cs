using BlitzRelay.Protocol;
using Newfarm.Client;
using Newfarm.Wire;
using System.Net;
using System.Text;

namespace BlitzRelay.Tests;

// One player: a relay transport and a newfarm client side by side, which is how a game would hold them. The relay
// transport is replaced rather than reused after a drop, because an ephemeral room takes its clients' connections down
// with it and a dropped peer comes back on a fresh socket.
internal sealed class NewfarmMigrationPeer : IDisposable
{
	public string Name { get; }

	public NewfarmClient Directory { get; }

	// Null until this peer has connected to the relay, and again between a drop and the next connection.
	public RelayTestPeer? Relay { get; private set; }

	public NewfarmSessionIdentity? CreatedIdentity { get; private set; }

	public int ElectionCount { get; private set; }

	// How many times newfarm has asked this peer to prove it is still hosting.
	public int ChallengeCount { get; private set; }

	public NewfarmCredential? Credential { get; private set; }

	public List<NewfarmRefusalReason> Refusals { get; } = [];

	private readonly TimeSpan _waitTimeout;

	public NewfarmMigrationPeer(string name, int newfarmPort, TimeSpan waitTimeout)
	{
		Name = name;

		_waitTimeout = waitTimeout;

		NewfarmClientConfig config = new(new IPEndPoint(IPAddress.Loopback, newfarmPort))
		{
			HostHeartbeatIntervalMilliseconds = 200,

			WaiterHeartbeatIntervalMilliseconds = 200,

			RequestRetryIntervalMilliseconds = 300,

			UnreachableReportIntervalMilliseconds = 300,
		};

		Directory = new NewfarmClient(config);

		Directory.SessionCreated += identity => CreatedIdentity = identity;

		Directory.ElectedToHost += _ => ElectionCount++;

		Directory.CredentialAvailable += credential => Credential = credential;

		// Deliberately left unanswered. A peer whose game has wedged has no room to publish and says nothing, which is
		// the case newfarm has to be able to act on.
		Directory.HostingChallenged += _ => ChallengeCount++;

		Directory.Refused += Refusals.Add;
	}

	// The room code the peer was last told the session lives at.
	public string CredentialRoomCode
	{
		get => Credential is null ? string.Empty : Credential.Value.Credential;
	}

	public void ConnectToRelay(int relayPort, string connectionKey)
	{
		Relay?.Dispose();

		Relay = new RelayTestPeer();

		Relay.Connect(relayPort, _waitTimeout);

		Relay.Authenticate(connectionKey);
	}

	// Creates a room and returns the code the relay assigned it, which is never a code the caller chose.
	public string CreateRoom(int maximumClients)
	{
		Relay!.Send(MessageCodec.CreateHostRegister(maximumClients), isReliable: true);

		byte[] roomCreated = Relay.WaitForMessage(MessageType.RoomCreated, _waitTimeout);

		if (!MessageCodec.TryReadRoomCreated(roomCreated, out string roomCode, out _))
			throw new InvalidOperationException($"[{Name}] could not read the room the relay created.");

		return roomCode;
	}

	public void JoinRoom(string roomCode)
	{
		Relay!.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);

		byte[] joinSuccess = Relay.WaitForMessage(MessageType.JoinSuccess, _waitTimeout);

		if (!MessageCodec.TryReadJoinSuccess(joinSuccess))
			throw new InvalidOperationException($"[{Name}] was not let into room [{roomCode}].");
	}

	// Waits for a client to arrive and returns the id the relay gave it, which is what the host addresses it by.
	public int AcceptClient()
	{
		byte[] connected = Relay!.WaitForMessage(MessageType.Connected, _waitTimeout);

		if (!MessageCodec.TryReadConnected(connected, out int virtualClientId))
			throw new InvalidOperationException($"[{Name}] could not read the client that joined.");

		return virtualClientId;
	}

	public void BroadcastToRoom(string text)
	{
		Relay!.Send(MessageCodec.CreateHostData(MessageCodec.BroadcastVirtualClientId, (byte)GameChannel.Reliable, Encoding.UTF8.GetBytes(text)), isReliable: true);
	}

	public void SendToHost(string text)
	{
		Relay!.Send(MessageCodec.CreateClientData((byte)GameChannel.Reliable, Encoding.UTF8.GetBytes(text)), isReliable: true);
	}

	public string WaitForRoomData()
	{
		byte[] message = Relay!.WaitForMessage(MessageType.Data, _waitTimeout);

		if (!MessageCodec.TryReadClientData(message, out _, out byte[] gamePayload))
			throw new InvalidOperationException($"[{Name}] could not read the data the host sent.");

		return Encoding.UTF8.GetString(gamePayload);
	}

	public string WaitForClientData()
	{
		byte[] message = Relay!.WaitForMessage(MessageType.Data, _waitTimeout);

		if (!MessageCodec.TryReadHostData(message, out _, out _, out byte[] gamePayload))
			throw new InvalidOperationException($"[{Name}] could not read the data a client sent.");

		return Encoding.UTF8.GetString(gamePayload);
	}

	// Kills the transport outright, with no goodbye, which is what a crashed or unplugged host looks like to the relay.
	// The relay hears nothing at all until its own timeout runs out.
	public void DropFromRelay()
	{
		Relay?.Dispose();

		Relay = null;
	}

	// Leaves properly, which the relay hears at once. This is a host that quit rather than one that died.
	public void LeaveRelay()
	{
		Relay?.Disconnect();

		// The peer pumps on its own thread, so give it a few turns to put the goodbye on the wire before the socket goes.
		Thread.Sleep(TimeSpan.FromMilliseconds(150));

		Relay?.Dispose();

		Relay = null;
	}

	public void Dispose()
	{
		Relay?.Dispose();

		Directory.Dispose();
	}
}
