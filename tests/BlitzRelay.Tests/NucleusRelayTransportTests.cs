extern alias nucleus;

using System.Diagnostics;
using nucleus::Nucleus.Connections;
using nucleus::Nucleus.Managers.Server;
using nucleus::Nucleus.Transports;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

// A Nucleus session carried entirely by the relay: two engine instances, neither of them reachable, meeting in a room the relay
// named. Nothing is stubbed, so a break anywhere between the engine's packet and the relay's framing fails these.
public sealed class NucleusRelayTransportTests(ITestOutputHelper output)
{
	private const string ConnectionKey = "nucleus-relay-key";

	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

	[Fact]
	public async Task TwoEnginesFormASessionThroughTheRelay()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		NucleusRelayPeer host = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey);
		NucleusRelayPeer client = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey);

		/* The authority has no address for anybody to dial. It asks the relay for a room, and the name the relay gives back is
		   the whole of what another peer needs. */

		Assert.True(await host.StartHostingAsync(), "The relay would not open a room for the authority.");
		Assert.NotEmpty(host.RoomCode);

		output.WriteLine($"The relay named the room [{host.RoomCode}].");

		int authenticatedClientCount = 0;
		Connection? authenticatedClientConnection = null;
		host.CoreManager.ServerManager.ClientAuthenticated += connection =>
		{
			authenticatedClientCount++;
			authenticatedClientConnection = connection;
		};

		bool isLocalClientAuthenticated = false;
		client.CoreManager.ClientManager.LocalClientAuthenticated += _ => isLocalClientAuthenticated = true;

		Assert.True(await client.JoinAsync(host.RoomCode), "The relay would not admit the client to the room.");

		/* The handshake is the engine's own, and it rides the relay like everything else: the client is not authenticated until
		   its packets have been through the relay and back. */

		TickUntil(() => isLocalClientAuthenticated && authenticatedClientCount == 1, WaitTimeout, "the client to authenticate through the relay", host, client);

		Assert.Equal(1, authenticatedClientCount);
		Assert.True(isLocalClientAuthenticated);

		/* And now the thing the whole exercise is for: a message the game sends, addressed to a peer neither side could reach
		   directly. */

		const ulong SessionId = 0xA71F_39C4_5E82_10DB;

		ulong receivedSessionId = 0;
		client.CoreManager.MessageManager.RegisterMessageHandler<SessionDirectoryMessage>((channel, sender, message) => receivedSessionId = message.SessionId);

		Assert.NotNull(authenticatedClientConnection);

		authenticatedClientConnection!.SendMessage(Channel.Reliable, new SessionDirectoryMessage(SessionId));

		TickUntil(() => receivedSessionId != 0, WaitTimeout, "the message to arrive through the relay", host, client);

		Assert.Equal(SessionId, receivedSessionId);

		await client.ShutdownAsync();
		await host.ShutdownAsync();
	}

	[Fact]
	public async Task AClientLosesItsLinkWhenTheAuthoritysRoomGoes()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		NucleusRelayPeer host = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey);
		NucleusRelayPeer client = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey);

		Assert.True(await host.StartHostingAsync());

		bool isLocalClientAuthenticated = false;
		client.CoreManager.ClientManager.LocalClientAuthenticated += _ => isLocalClientAuthenticated = true;

		Assert.True(await client.JoinAsync(host.RoomCode));

		TickUntil(() => isLocalClientAuthenticated, WaitTimeout, "the client to authenticate", host, client);

		/* The authority leaves. The room belongs to it and goes with it, so the client's link goes too, and that is exactly the
		   signal a surviving peer has to act on: there is no channel left to be told anything on. */

		await host.ShutdownAsync();

		TickUntil(() => !IsClientConnected(client), WaitTimeout, "the client to lose its link with the room", client);

		Assert.False(IsClientConnected(client), "The client still believes it is connected to a room that has gone.");

		await client.ShutdownAsync();
	}

	private static bool IsClientConnected(NucleusRelayPeer peer)
	{
		return peer.Transport.TryGetConnection(Invoker.Client, out Connection connection) && connection.LocalState == LocalConnectionState.Connected;
	}

	private static void TickUntil(Func<bool> condition, TimeSpan timeout, string description, params NucleusRelayPeer[] peers)
	{
		long startedTimestamp = Stopwatch.GetTimestamp();

		while (Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
		{
			for (int i = 0; i < peers.Length; i++)
				peers[i].Tick();

			if (condition()) return;

			Thread.Sleep(5);
		}

		for (int i = 0; i < peers.Length; i++)
			peers[i].Tick();

		if (!condition()) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for {description}.");
	}
}
