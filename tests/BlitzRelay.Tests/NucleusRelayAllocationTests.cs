extern alias nucleus;

using System.Diagnostics;
using nucleus::Nucleus.Connections;
using nucleus::Nucleus.Managers.Server;
using nucleus::Nucleus.Transports;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

// What a session costs per tick once it is running. The transport frames every outbound message into a buffer it owns and copies
// every inbound one into an array rented from the shared pool, so carrying traffic should cost nothing that a collector will
// later have to take back.
public sealed class NucleusRelayAllocationTests(ITestOutputHelper output)
{
	private const string ConnectionKey = "nucleus-allocation-key";

	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

	// Enough ticks that anything which allocates once, a pool filling or a collection sizing itself, is lost in the average.
	private const int WarmupTickCount = 200;

	private const int MeasuredTickCount = 600;

	// The relay carries traffic on its own poll, so a tick loop that runs flat out sends far more than reaches the far end
	// inside the window. A pause per tick lets the relay actually deliver what is being measured.
	private const int TickPauseMilliseconds = 2;

	[Fact]
	public async Task CarryingTrafficThroughTheRelayAllocatesNothingPerMessage()
	{
		using RelayHostFixture relay = new(output, ConnectionKey);

		relay.WaitUntilStarted();

		NucleusRelayPeer host = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey);
		NucleusRelayPeer client = await NucleusRelayPeer.CreateAsync(relay.Port, ConnectionKey);

		Assert.True(await host.StartHostingAsync());

		Connection? clientConnection = null;
		host.CoreManager.ServerManager.ClientAuthenticated += connection => clientConnection = connection;

		bool isAuthenticated = false;
		client.CoreManager.ClientManager.LocalClientAuthenticated += _ => isAuthenticated = true;

		Assert.True(await client.JoinAsync(host.RoomCode));

		TickUntil(() => isAuthenticated && clientConnection is not null, WaitTimeout, "the session to form", host, client);

		int receivedCount = 0;
		client.CoreManager.MessageManager.RegisterMessageHandler<SessionDirectoryMessage>((channel, sender, message) => receivedCount++);

		/* Both measurements run on this thread, and every peer is driven by hand, so a thread-local count is the whole of what
		   the session costs. Warmed first: pools fill, queues size themselves, and the first message of a kind pays for things
		   every later one reuses. */

		Warm(host, client, clientConnection!, WarmupTickCount);

		long idleBytes = MeasureBytesPerTick(host, client, receiver: null, MeasuredTickCount);

		int receivedBeforeTraffic = receivedCount;

		long trafficBytes = MeasureBytesPerTick(host, client, clientConnection, MeasuredTickCount);

		int messagesDelivered = receivedCount - receivedBeforeTraffic;

		Assert.True(messagesDelivered > MeasuredTickCount / 4, $"Only [{messagesDelivered}] of [{MeasuredTickCount}] messages arrived, so this measured an idle session twice.");

		double idleBytesPerTick = idleBytes / (double)MeasuredTickCount;
		double trafficBytesPerTick = trafficBytes / (double)MeasuredTickCount;
		double bytesPerMessage = (trafficBytes - idleBytes) / (double)messagesDelivered;

		output.WriteLine($"Idle: [{idleBytesPerTick:0.00}] bytes a tick over [{MeasuredTickCount}] ticks.");
		output.WriteLine($"Carrying traffic: [{trafficBytesPerTick:0.00}] bytes a tick, [{messagesDelivered}] messages delivered.");
		output.WriteLine($"Marginal: [{bytesPerMessage:0.00}] bytes a message, both ways through the relay.");

		/* The number that matters is the marginal one. A session that costs a fixed amount at rest is a session whose cost does
		   not grow with what it carries; one that costs per message is one that hands the collector more work the busier it
		   gets. */
		Assert.True(bytesPerMessage < 16.0, $"Carrying a message costs [{bytesPerMessage:0.00}] bytes, so something on the send or receive path allocates per packet.");

		await client.ShutdownAsync();
		await host.ShutdownAsync();
	}

	// Runs the same work the measurement will, so nothing it pays for once is counted as a per-tick cost.
	private static void Warm(NucleusRelayPeer host, NucleusRelayPeer client, Connection receiver, int tickCount)
	{
		for (int i = 0; i < tickCount; i++)
		{
			receiver.SendMessage(Channel.Reliable, new SessionDirectoryMessage((ulong)i + 1));

			host.Tick();
			client.Tick();

			Thread.Sleep(TickPauseMilliseconds);
		}
	}

	// Drives both peers for a run of ticks and reports what was allocated across it, optionally sending a message each tick.
	private static long MeasureBytesPerTick(NucleusRelayPeer host, NucleusRelayPeer client, Connection? receiver, int tickCount)
	{
		// Settled first, so nothing left over from the previous run lands inside this one's count.
		for (int i = 0; i < 20; i++)
		{
			host.Tick();
			client.Tick();
		}

		long startedBytes = GC.GetAllocatedBytesForCurrentThread();

		for (int i = 0; i < tickCount; i++)
		{
			receiver?.SendMessage(Channel.Reliable, new SessionDirectoryMessage((ulong)i + 1));

			host.Tick();
			client.Tick();

			Thread.Sleep(TickPauseMilliseconds);
		}

		return GC.GetAllocatedBytesForCurrentThread() - startedBytes;
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

		if (!condition()) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for {description}.");
	}
}
