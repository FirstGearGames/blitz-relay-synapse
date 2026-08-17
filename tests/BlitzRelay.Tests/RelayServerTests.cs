using BlitzRelay.Networking;
using BlitzRelay.Protocol;
using BlitzRelay.Rooms;
using System.Diagnostics;
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
	public void HostAndClientExchangeDataThroughTheRelay()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		using RelayTestPeer host = new();

		using RelayTestPeer client = new();

		host.Connect(relay.Port, WaitTimeout);

		host.Authenticate(ConnectionKey);

		host.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		byte[] roomCreated = host.WaitForMessage(MessageType.RoomCreated, WaitTimeout);

		Assert.True(MessageCodec.TryReadRoomCreated(roomCreated, out string roomCode, out string roomHostToken));

		Assert.NotEmpty(roomCode);

		Assert.NotEmpty(roomHostToken);

		client.Connect(relay.Port, WaitTimeout);

		client.Authenticate(ConnectionKey);

		client.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);

		byte[] joinSuccess = client.WaitForMessage(MessageType.JoinSuccess, WaitTimeout);

		Assert.True(MessageCodec.TryReadJoinSuccess(joinSuccess));

		byte[] connected = host.WaitForMessage(MessageType.Connected, WaitTimeout);

		Assert.True(MessageCodec.TryReadConnected(connected, out int virtualClientId));

		/* Host to a single client, reliably. */

		byte[] reliablePayload = Encoding.UTF8.GetBytes("reliable-host-to-client");

		host.Send(MessageCodec.CreateHostData(virtualClientId, (byte)GameChannel.Reliable, reliablePayload), isReliable: true);

		AssertClientData(client, GameChannel.Reliable, reliablePayload);

		/* Host broadcast, unreliably. */

		byte[] unreliablePayload = Encoding.UTF8.GetBytes("unreliable-host-broadcast");

		host.Send(MessageCodec.CreateHostData(MessageCodec.BroadcastVirtualClientId, (byte)GameChannel.Unreliable, unreliablePayload), isReliable: false);

		AssertClientData(client, GameChannel.Unreliable, unreliablePayload);

		/* Client to host, reliably. */

		byte[] clientPayload = Encoding.UTF8.GetBytes("reliable-client-to-host");

		client.Send(MessageCodec.CreateClientData((byte)GameChannel.Reliable, clientPayload), isReliable: true);

		AssertHostData(host, virtualClientId, GameChannel.Reliable, clientPayload);

		/* A payload well past the MTU, which only arrives if segmentation survives the relay. */

		byte[] segmentedPayload = CreatePayload(8000);

		client.Send(MessageCodec.CreateClientData((byte)GameChannel.Reliable, segmentedPayload), isReliable: true);

		AssertHostData(host, virtualClientId, GameChannel.Reliable, segmentedPayload);

		host.Send(MessageCodec.CreateHostData(virtualClientId, (byte)GameChannel.Reliable, segmentedPayload), isReliable: true);

		AssertClientData(client, GameChannel.Reliable, segmentedPayload);

		/* The host is told when a client leaves. */

		client.Disconnect();

		byte[] disconnected = host.WaitForMessage(MessageType.Disconnected, WaitTimeout);

		Assert.True(MessageCodec.TryReadDisconnected(disconnected, out int disconnectedVirtualClientId));

		Assert.Equal(virtualClientId, disconnectedVirtualClientId);
	}

	[Fact]
	public void RelayRejectsAnInvalidConnectionKey()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		using RelayTestPeer peer = new();

		peer.Connect(relay.Port, WaitTimeout);

		peer.Authenticate("wrong-connection-key");

		byte[] error = peer.WaitForMessage(MessageType.Error, WaitTimeout);

		Assert.True(MessageCodec.TryReadError(error, out ErrorCode errorCode));

		Assert.Equal(ErrorCode.InvalidConnectionKey, errorCode);

		peer.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		Assert.False(peer.HasReceivedMessage(MessageType.RoomCreated, NegativeWaitTimeout));
	}

	[Fact]
	public void RelayIgnoresMessagesFromAnUnauthenticatedPeer()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		using RelayTestPeer peer = new();

		peer.Connect(relay.Port, WaitTimeout);

		peer.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		Assert.False(peer.HasReceivedMessage(MessageType.RoomCreated, NegativeWaitTimeout));

		/* The same peer is served as soon as it presents the key. */

		peer.Authenticate(ConnectionKey);

		peer.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		Assert.True(peer.HasReceivedMessage(MessageType.RoomCreated, WaitTimeout));
	}

	[Fact]
	public void PersistentRoomPromotesAClientToHost()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		Assert.True(relay.Server.TryCreateReservedRoom(4, "promotion-room", isPublic: true, metadata: null, out RoomSnapshot? snapshot, out ErrorCode _));

		string roomCode = snapshot!.Code;

		using RelayTestPeer joiningPeer = new();

		joiningPeer.Connect(relay.Port, WaitTimeout);

		joiningPeer.Authenticate(ConnectionKey);

		joiningPeer.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);

		/* A reserved room with no host promotes the first client to join it. */

		byte[] hostPromoted = joiningPeer.WaitForMessage(MessageType.HostPromoted, WaitTimeout);

		Assert.True(MessageCodec.TryReadHostPromoted(hostPromoted, out string promotedRoomCode, out int maximumClients, out string claimToken));

		Assert.Equal(roomCode, promotedRoomCode);

		Assert.Equal(4, maximumClients);

		/* Acknowledging the promotion drops the peer so it can come back as the host. */

		joiningPeer.Send(MessageCodec.CreateHostPromotionAck(promotedRoomCode, claimToken), isReliable: true);

		joiningPeer.WaitUntilClosed(WaitTimeout);

		using RelayTestPeer promotedHost = new();

		promotedHost.Connect(relay.Port, WaitTimeout);

		promotedHost.Authenticate(ConnectionKey);

		promotedHost.Send(MessageCodec.CreateHostClaim(promotedRoomCode, claimToken), isReliable: true);

		byte[] roomCreated = promotedHost.WaitForMessage(MessageType.RoomCreated, WaitTimeout);

		Assert.True(MessageCodec.TryReadRoomCreated(roomCreated, out string claimedRoomCode, out string _));

		Assert.Equal(roomCode, claimedRoomCode);

		Assert.True(relay.Server.GetRoomSnapshot(roomCode)!.HasHost);
	}

	// SynapseSocket only moves datagrams while Poll is running, so a relay that paced its poll loop on the thread pool
	// would stop relaying whenever the pool was busy, which in this process it always could be: the HTTP admin API runs
	// on the same pool. The poll loop owns a thread, and this pins that.
	[Fact]
	public void RelayKeepsRelayingWhileTheThreadPoolIsSaturated()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		using RelayTestPeer host = new();

		using RelayTestPeer client = new();

		host.Connect(relay.Port, WaitTimeout);

		host.Authenticate(ConnectionKey);

		host.Send(MessageCodec.CreateHostRegister(4), isReliable: true);

		Assert.True(MessageCodec.TryReadRoomCreated(host.WaitForMessage(MessageType.RoomCreated, WaitTimeout), out string roomCode, out string _));

		client.Connect(relay.Port, WaitTimeout);

		client.Authenticate(ConnectionKey);

		client.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);

		client.WaitForMessage(MessageType.JoinSuccess, WaitTimeout);

		Assert.True(MessageCodec.TryReadConnected(host.WaitForMessage(MessageType.Connected, WaitTimeout), out int virtualClientId));

		using ThreadPoolSaturation saturation = new();

		saturation.Occupy();

		/* Let any continuation that was already scheduled run, so what follows measures the saturated steady state
		 * rather than the tail of a pool that had not clogged yet. */
		Thread.Sleep(TimeSpan.FromMilliseconds(500));

		byte[] payload = Encoding.UTF8.GetBytes("relayed-under-a-saturated-pool");

		long sentTimestamp = Stopwatch.GetTimestamp();

		host.Send(MessageCodec.CreateHostData(virtualClientId, (byte)GameChannel.Reliable, payload), isReliable: true);

		AssertClientData(client, GameChannel.Reliable, payload);

		TimeSpan elapsed = Stopwatch.GetElapsedTime(sentTimestamp);

		output.WriteLine($"relayed in {elapsed.TotalMilliseconds:0} ms with {saturation.OccupiedWorkItems} work items occupying the pool");

		/* Measured on the poll thread: ~50 ms for the two hops. The same relay pacing itself with await Task.Delay on
		 * the saturated pool took ~680 ms, so this sits clear of both. */
		Assert.True(elapsed < TimeSpan.FromMilliseconds(250), $"Relaying took {elapsed.TotalMilliseconds:0} ms while the thread pool was saturated.");
	}

	// The poll loop owns a thread, so disposal has to end it rather than leave it running against a disposed engine.
	// The returned task completing is the loop's own report that it left its while loop and ran its finally.
	[Fact]
	public void DisposingTheRelayEndsThePollLoopWithoutCancellation()
	{
		using CancellationTokenSource neverCancelled = new();

		Server server = new(ReserveUdpPort(), ConnectionKey, new TestOutputLogger<Server>(output));

		Task<int> runTask = server.RunAsync(neverCancelled.Token);

		Thread.Sleep(TimeSpan.FromMilliseconds(250));

		Assert.False(runTask.IsCompleted);

		server.Dispose();

		Assert.True(runTask.Wait(TimeSpan.FromSeconds(5)), "The poll loop was still running after the relay was disposed.");
	}

	private static int ReserveUdpPort()
	{
		using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

		probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

		return ((IPEndPoint)probe.LocalEndPoint!).Port;
	}

	private static void AssertClientData(RelayTestPeer client, GameChannel gameChannel, byte[] expectedPayload)
	{
		byte[] message = client.WaitForMessage(MessageType.Data, WaitTimeout);

		Assert.True(MessageCodec.TryReadClientData(message, out byte receivedGameChannel, out byte[] receivedPayload));

		Assert.Equal((byte)gameChannel, receivedGameChannel);

		Assert.Equal(expectedPayload, receivedPayload);
	}

	private static void AssertHostData(RelayTestPeer host, int expectedVirtualClientId, GameChannel gameChannel, byte[] expectedPayload)
	{
		byte[] message = host.WaitForMessage(MessageType.Data, WaitTimeout);

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

	// Fills every worker the pool is willing to hand out without growing, so anything queued behind it waits on the
	// pool's injection rate of roughly one thread per second.
	private sealed class ThreadPoolSaturation : IDisposable
	{
		public int OccupiedWorkItems { get; private set; }

		private readonly ManualResetEventSlim _release = new(initialState: false);

		public void Occupy()
		{
			ThreadPool.GetMinThreads(out int minimumWorkerThreads, out int _);

			ThreadPool.GetAvailableThreads(out int availableWorkerThreads, out int _);

			OccupiedWorkItems = Math.Max(minimumWorkerThreads, availableWorkerThreads) + 8;

			CountdownEvent occupied = new(OccupiedWorkItems);

			for (int i = 0; i < OccupiedWorkItems; i++)
			{
				ThreadPool.UnsafeQueueUserWorkItem(_ =>
				{
					occupied.Signal();

					_release.Wait();
				}, null);
			}

			/* Only the items that got a worker signal, so wait for as many as the pool can seat rather than all of them. */
			occupied.Wait(TimeSpan.FromSeconds(2));
		}

		public void Dispose()
		{
			_release.Set();

			/* Give the released workers a moment to unwind before the next test measures the pool. */
			Thread.Sleep(TimeSpan.FromMilliseconds(50));

			_release.Dispose();
		}
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

			_runTask = _server.RunAsync(_cancellation.Token);
		}

		// The relay binds inside RunAsync, and a handshake that arrives before the bind is simply not heard.
		public void WaitUntilStarted()
		{
			Thread.Sleep(TimeSpan.FromMilliseconds(250));
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
	}
}
