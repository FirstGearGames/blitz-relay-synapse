using BlitzRelay.Networking;
using BlitzRelay.Protocol;
using BlitzRelay.Rooms;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

// End-to-end tests over loopback UDP: a real relay server, real SynapseSocket peers, real datagrams. Nothing is
// stubbed, so a break anywhere between the relay's framing and SynapseSocket's wire format fails these.
public sealed class RelayServerTests(ITestOutputHelper output)
{
	private const string ConnectionKey = "test-connection-key";

	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

	private static readonly TimeSpan NegativeWaitTimeout = TimeSpan.FromSeconds(2);

	[Fact]
	public async Task HostAndClientExchangeDataThroughTheRelay()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		await relay.WaitUntilStartedAsync();

		using RelayTestPeer host = new();

		using RelayTestPeer client = new();

		await host.ConnectAsync(relay.Port, WaitTimeout);

		await host.AuthenticateAsync(ConnectionKey);

		host.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		byte[] roomCreated = await host.WaitForMessageAsync(MessageType.RoomCreated, WaitTimeout);

		Assert.True(MessageCodec.TryReadRoomCreated(roomCreated, out string roomCode, out string roomHostToken));

		Assert.NotEmpty(roomCode);

		Assert.NotEmpty(roomHostToken);

		await client.ConnectAsync(relay.Port, WaitTimeout);

		await client.AuthenticateAsync(ConnectionKey);

		client.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);

		byte[] joinSuccess = await client.WaitForMessageAsync(MessageType.JoinSuccess, WaitTimeout);

		Assert.True(MessageCodec.TryReadJoinSuccess(joinSuccess));

		byte[] connected = await host.WaitForMessageAsync(MessageType.Connected, WaitTimeout);

		Assert.True(MessageCodec.TryReadConnected(connected, out int virtualClientId));

		/* Host to a single client, reliably. */

		byte[] reliablePayload = Encoding.UTF8.GetBytes("reliable-host-to-client");

		host.Send(MessageCodec.CreateHostData(virtualClientId, (byte)GameChannel.Reliable, reliablePayload), isReliable: true);

		await AssertClientDataAsync(client, GameChannel.Reliable, reliablePayload);

		/* Host broadcast, unreliably. */

		byte[] unreliablePayload = Encoding.UTF8.GetBytes("unreliable-host-broadcast");

		host.Send(MessageCodec.CreateHostData(MessageCodec.BroadcastVirtualClientId, (byte)GameChannel.Unreliable, unreliablePayload), isReliable: false);

		await AssertClientDataAsync(client, GameChannel.Unreliable, unreliablePayload);

		/* Client to host, reliably. */

		byte[] clientPayload = Encoding.UTF8.GetBytes("reliable-client-to-host");

		client.Send(MessageCodec.CreateClientData((byte)GameChannel.Reliable, clientPayload), isReliable: true);

		await AssertHostDataAsync(host, virtualClientId, GameChannel.Reliable, clientPayload);

		/* A payload well past the MTU, which only arrives if segmentation survives the relay. */

		byte[] segmentedPayload = CreatePayload(8000);

		client.Send(MessageCodec.CreateClientData((byte)GameChannel.Reliable, segmentedPayload), isReliable: true);

		await AssertHostDataAsync(host, virtualClientId, GameChannel.Reliable, segmentedPayload);

		host.Send(MessageCodec.CreateHostData(virtualClientId, (byte)GameChannel.Reliable, segmentedPayload), isReliable: true);

		await AssertClientDataAsync(client, GameChannel.Reliable, segmentedPayload);

		/* The host is told when a client leaves. */

		client.Disconnect();

		byte[] disconnected = await host.WaitForMessageAsync(MessageType.Disconnected, WaitTimeout);

		Assert.True(MessageCodec.TryReadDisconnected(disconnected, out int disconnectedVirtualClientId));

		Assert.Equal(virtualClientId, disconnectedVirtualClientId);
	}

	[Fact]
	public async Task RelayRejectsAnInvalidConnectionKey()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		await relay.WaitUntilStartedAsync();

		using RelayTestPeer peer = new();

		await peer.ConnectAsync(relay.Port, WaitTimeout);

		await peer.AuthenticateAsync("wrong-connection-key");

		byte[] error = await peer.WaitForMessageAsync(MessageType.Error, WaitTimeout);

		Assert.True(MessageCodec.TryReadError(error, out ErrorCode errorCode));

		Assert.Equal(ErrorCode.InvalidConnectionKey, errorCode);

		peer.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		Assert.False(await peer.HasReceivedMessageAsync(MessageType.RoomCreated, NegativeWaitTimeout));
	}

	[Fact]
	public async Task RelayIgnoresMessagesFromAnUnauthenticatedPeer()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		await relay.WaitUntilStartedAsync();

		using RelayTestPeer peer = new();

		await peer.ConnectAsync(relay.Port, WaitTimeout);

		peer.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		Assert.False(await peer.HasReceivedMessageAsync(MessageType.RoomCreated, NegativeWaitTimeout));

		/* The same peer is served as soon as it presents the key. */

		await peer.AuthenticateAsync(ConnectionKey);

		peer.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		Assert.True(await peer.HasReceivedMessageAsync(MessageType.RoomCreated, WaitTimeout));
	}

	[Fact]
	public async Task PersistentRoomPromotesAClientToHost()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		await relay.WaitUntilStartedAsync();

		Assert.True(relay.Server.TryCreateReservedRoom(4, "promotion-room", isPublic: true, metadata: null, out RoomSnapshot? snapshot, out ErrorCode _));

		string roomCode = snapshot!.Code;

		using RelayTestPeer joiningPeer = new();

		await joiningPeer.ConnectAsync(relay.Port, WaitTimeout);

		await joiningPeer.AuthenticateAsync(ConnectionKey);

		joiningPeer.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);

		/* A reserved room with no host promotes the first client to join it. */

		byte[] hostPromoted = await joiningPeer.WaitForMessageAsync(MessageType.HostPromoted, WaitTimeout);

		Assert.True(MessageCodec.TryReadHostPromoted(hostPromoted, out string promotedRoomCode, out int maximumClients, out string claimToken));

		Assert.Equal(roomCode, promotedRoomCode);

		Assert.Equal(4, maximumClients);

		/* Acknowledging the promotion drops the peer so it can come back as the host. */

		joiningPeer.Send(MessageCodec.CreateHostPromotionAck(promotedRoomCode, claimToken), isReliable: true);

		await joiningPeer.WaitUntilClosedAsync(WaitTimeout);

		using RelayTestPeer promotedHost = new();

		await promotedHost.ConnectAsync(relay.Port, WaitTimeout);

		await promotedHost.AuthenticateAsync(ConnectionKey);

		promotedHost.Send(MessageCodec.CreateHostClaim(promotedRoomCode, claimToken), isReliable: true);

		byte[] roomCreated = await promotedHost.WaitForMessageAsync(MessageType.RoomCreated, WaitTimeout);

		Assert.True(MessageCodec.TryReadRoomCreated(roomCreated, out string claimedRoomCode, out string _));

		Assert.Equal(roomCode, claimedRoomCode);

		Assert.True(relay.Server.GetRoomSnapshot(roomCode)!.HasHost);
	}

	private static async Task AssertClientDataAsync(RelayTestPeer client, GameChannel gameChannel, byte[] expectedPayload)
	{
		byte[] message = await client.WaitForMessageAsync(MessageType.Data, WaitTimeout);

		Assert.True(MessageCodec.TryReadClientData(message, out byte receivedGameChannel, out byte[] receivedPayload));

		Assert.Equal((byte)gameChannel, receivedGameChannel);

		Assert.Equal(expectedPayload, receivedPayload);
	}

	private static async Task AssertHostDataAsync(RelayTestPeer host, int expectedVirtualClientId, GameChannel gameChannel, byte[] expectedPayload)
	{
		byte[] message = await host.WaitForMessageAsync(MessageType.Data, WaitTimeout);

		Assert.True(MessageCodec.TryReadHostData(message, out int receivedVirtualClientId, out byte receivedGameChannel, out byte[] receivedPayload));

		Assert.Equal(expectedVirtualClientId, receivedVirtualClientId);

		Assert.Equal((byte)gameChannel, receivedGameChannel);

		Assert.Equal(expectedPayload, receivedPayload);
	}

	private static byte[] CreatePayload(int length)
	{
		byte[] payload = new byte[length];

		for (int i = 0; i < length; i++)
		{
			payload[i] = (byte)(i % 251);
		}

		return payload;
	}

	private sealed class RelayHostFixture : IDisposable
	{
		public int Port { get; }

		public Server Server
		{
			get => _server;
		}

		private readonly Server _server;

		private readonly CancellationTokenSource _cancellation;

		private readonly Task<int> _runTask;

		public RelayHostFixture(ITestOutputHelper output, string connectionKey)
		{
			Port = ReserveUdpPort();

			_cancellation = new CancellationTokenSource();

			_server = new Server(Port, connectionKey, new TestOutputLogger<Server>(output));

			_runTask = Task.Run(() => _server.RunAsync(_cancellation.Token));
		}

		// The relay binds inside RunAsync, and a handshake that arrives before the bind is simply not heard.
		public async Task WaitUntilStartedAsync()
		{
			await Task.Delay(TimeSpan.FromMilliseconds(500));
		}

		public void Dispose()
		{
			_cancellation.Cancel();

			try
			{
				_runTask.Wait(TimeSpan.FromSeconds(5));
			}
			catch (AggregateException)
			{
				// The run loop was cancelled, which is the expected way for it to end.
			}

			_server.Dispose();

			_cancellation.Dispose();
		}

		private static int ReserveUdpPort()
		{
			using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

			probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

			return ((IPEndPoint)probe.LocalEndPoint!).Port;
		}
	}
}
