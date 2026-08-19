using BlitzRelay.Networking;
using BlitzRelay.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

// The two things the fast suite measures separately, together: a room full of peers under sustained broadcast while
// the thread pool is held saturated for the whole run. The fast suite proves the poll thread is immune to starvation
// with two peers, and the scale runs prove the relay carries hundreds of peers with a quiet pool. This is the case
// neither covers, and the only way to saturate the relay's own pool is to host it in this process.
//
// Long by design, so it does not run with the normal suite. Enable it with BLITZ_RELAY_SOAK=1:
//   $env:BLITZ_RELAY_SOAK=1; dotnet test --filter "FullyQualifiedName~RelaySoakTests"
// BLITZ_RELAY_SOAK_SECONDS and BLITZ_RELAY_SOAK_CLIENTS override the duration and peer count.
public sealed class RelaySoakTests(ITestOutputHelper output)
{
	private const string ConnectionKey = "soak-connection-key";

	private const int PeersPerPumpThread = 64;

	private const int BroadcastHertz = 30;

	private const int HeartbeatSeconds = 5;

	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

	private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

	private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

	[Fact]
	public void RelayCarriesAFullRoomUnderSustainedLoadAndASaturatedThreadPool()
	{
		if (Environment.GetEnvironmentVariable("BLITZ_RELAY_SOAK") != "1")
		{
			output.WriteLine("Skipped: set BLITZ_RELAY_SOAK=1 to run the soak.");

			return;
		}

		int clientCount = ReadSetting("BLITZ_RELAY_SOAK_CLIENTS", 512);

		TimeSpan duration = TimeSpan.FromSeconds(ReadSetting("BLITZ_RELAY_SOAK_SECONDS", 600));

		using RelaySoakFixture relay = new(output, ConnectionKey);

		using RelayTestPeer host = new();

		using RelayTestPeer measuredClient = new();

		List<RelayTestPeer> backgroundClients = [];

		List<Thread> pumpThreads = [];

		using CancellationTokenSource pumpCancellation = new();

		try
		{
			for (int i = 0; i < clientCount; i++)
			{
				backgroundClients.Add(new RelayTestPeer(ownsPumpThread: false) { CountDataOnly = true });
			}

			for (int i = 0; i < backgroundClients.Count; i += PeersPerPumpThread)
			{
				List<RelayTestPeer> shard = backgroundClients.Skip(i).Take(PeersPerPumpThread).ToList();

				pumpThreads.Add(StartShardPump(shard, pumpCancellation.Token));
			}

			host.Connect(relay.Port, WaitTimeout);

			host.Authenticate(ConnectionKey);

			host.Send(MessageCodec.CreateHostRegister((ushort)(clientCount + 8)), isReliable: true);

			Assert.True(MessageCodec.TryReadRoomCreated(host.WaitForMessage(MessageType.RoomCreated, WaitTimeout), out string roomCode, out string _));

			measuredClient.Connect(relay.Port, WaitTimeout);

			measuredClient.Authenticate(ConnectionKey);

			measuredClient.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);

			measuredClient.WaitForMessage(MessageType.JoinSuccess, WaitTimeout);

			Assert.True(MessageCodec.TryReadConnected(host.WaitForMessage(MessageType.Connected, WaitTimeout), out int measuredVirtualClientId));

			long joinStartedTimestamp = Stopwatch.GetTimestamp();

			foreach (RelayTestPeer client in backgroundClients)
			{
				client.Connect(relay.Port, WaitTimeout);

				/* Both reliable, so the relay sees them in order and neither needs waiting on. */
				client.Send(MessageCodec.CreateAuthenticate(ConnectionKey), isReliable: true);

				client.Send(MessageCodec.CreateClientJoin(roomCode), isReliable: true);
			}

			foreach (RelayTestPeer client in backgroundClients)
			{
				client.WaitForMessage(MessageType.JoinSuccess, WaitTimeout);
			}

			// The host is fed a heartbeat by every client from here on, and has no reason to keep any of them.
			host.CountDataOnly = true;

			output.WriteLine($"{clientCount + 1} clients joined room {roomCode} in {Stopwatch.GetElapsedTime(joinStartedTimestamp).TotalSeconds:0.0}s");

			/* Held saturated for the whole run, not just at the start. */

			using ThreadPoolSaturation saturation = new();

			saturation.Occupy();

			output.WriteLine($"thread pool saturated with {saturation.OccupiedWorkItems} blocked work items; broadcasting {BroadcastHertz} Hz to {clientCount + 1} clients for {duration.TotalMinutes:0.#} minutes");

			List<double> probeLatencies = [];

			int probesTimedOut = 0;

			byte[] broadcastPayload = new byte[200];

			byte[] heartbeatPayload = new byte[16];

			/* Real clients send input, and a client that never sends is worth testing on purpose: keep-alive scheduling
			 * decides whether a listen-only peer survives at all. Each sending client sends once every heartbeatSeconds,
			 * spread across ticks so the upstream is a trickle rather than a burst. Set BLITZ_RELAY_SOAK_HEARTBEAT_SECONDS
			 * to 0 for a room of pure listeners. */
			int heartbeatSeconds = ReadSetting("BLITZ_RELAY_SOAK_HEARTBEAT_SECONDS", HeartbeatSeconds);

			int heartbeatsPerTick = heartbeatSeconds == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(backgroundClients.Count / (heartbeatSeconds * (double)BroadcastHertz)));

			int nextHeartbeatClient = 0;

			long startedTimestamp = Stopwatch.GetTimestamp();

			long nextProbeTimestamp = startedTimestamp + (long)(Stopwatch.Frequency * ProbeInterval.TotalSeconds);

			long nextProgressTimestamp = startedTimestamp + (long)(Stopwatch.Frequency * ProgressInterval.TotalSeconds);

			long broadcastTicks = 0;

			while (Stopwatch.GetElapsedTime(startedTimestamp) < duration)
			{
				host.Send(MessageCodec.CreateHostData(MessageCodec.BroadcastVirtualClientId, (byte)GameChannel.Unreliable, broadcastPayload), isReliable: false);

				broadcastTicks++;

				for (int i = 0; i < heartbeatsPerTick && backgroundClients.Count > 0; i++)
				{
					backgroundClients[nextHeartbeatClient].Send(MessageCodec.CreateClientData((byte)GameChannel.Unreliable, heartbeatPayload), isReliable: false);

					nextHeartbeatClient = (nextHeartbeatClient + 1) % backgroundClients.Count;
				}

				long nowTimestamp = Stopwatch.GetTimestamp();

				if (nowTimestamp >= nextProbeTimestamp)
				{
					nextProbeTimestamp = nowTimestamp + (long)(Stopwatch.Frequency * ProbeInterval.TotalSeconds);

					measuredClient.ClearReceived();

					byte[] probePayload = Encoding.UTF8.GetBytes($"soak-probe-{probeLatencies.Count}");

					long probeSentTimestamp = Stopwatch.GetTimestamp();

					host.Send(MessageCodec.CreateHostData(measuredVirtualClientId, (byte)GameChannel.Reliable, probePayload), isReliable: true);

					try
					{
						measuredClient.WaitForMessage(MessageType.Data, TimeSpan.FromSeconds(5));

						probeLatencies.Add(Stopwatch.GetElapsedTime(probeSentTimestamp).TotalMilliseconds);
					}
					catch (TimeoutException)
					{
						probesTimedOut++;
					}
				}

				if (nowTimestamp >= nextProgressTimestamp)
				{
					nextProgressTimestamp = nowTimestamp + (long)(Stopwatch.Frequency * ProgressInterval.TotalSeconds);

					ReportProgress(output, startedTimestamp, broadcastTicks, backgroundClients, probeLatencies, probesTimedOut);
				}

				long dueTimestamp = startedTimestamp + (long)(Stopwatch.Frequency * (broadcastTicks / (double)BroadcastHertz));

				while (Stopwatch.GetTimestamp() < dueTimestamp) Thread.SpinWait(200);
			}

			Thread.Sleep(TimeSpan.FromSeconds(1));

			long delivered = backgroundClients.Sum(client => (long)client.DataMessageCount);

			long expected = broadcastTicks * backgroundClients.Count;

			double deliveredPercent = expected == 0 ? 100 : delivered * 100.0 / expected;

			probeLatencies.Sort();

			int closedClients = backgroundClients.Count(client => client.IsClosed);

			output.WriteLine($"SOAK ticks={broadcastTicks} delivered={delivered}/{expected} ({deliveredPercent:0.00}%) probes={probeLatencies.Count} timedOut={probesTimedOut} medianMs={probeLatencies[probeLatencies.Count / 2]:0.0} p99Ms={probeLatencies[(int)(probeLatencies.Count * 0.99)]:0.0} maxMs={probeLatencies[^1]:0.0} closedClients={closedClients}");

			Assert.Equal(0, probesTimedOut);

			Assert.Equal(0, closedClients);

			/* Unreliable traffic may legitimately drop; a relay that had stalled would lose far more than a fraction. */
			Assert.True(deliveredPercent > 99.0, $"Only {deliveredPercent:0.00}% of broadcasts were delivered.");

			Assert.True(probeLatencies[^1] < TimeSpan.FromSeconds(1).TotalMilliseconds, $"Worst probe took {probeLatencies[^1]:0} ms.");
		}
		finally
		{
			pumpCancellation.Cancel();

			foreach (Thread pumpThread in pumpThreads)
			{
				pumpThread.Join(TimeSpan.FromSeconds(5));
			}

			foreach (RelayTestPeer client in backgroundClients)
			{
				client.Dispose();
			}
		}
	}

	private static void ReportProgress(ITestOutputHelper output, long startedTimestamp, long broadcastTicks, List<RelayTestPeer> backgroundClients, List<double> probeLatencies, int probesTimedOut)
	{
		long delivered = backgroundClients.Sum(client => (long)client.DataMessageCount);

		long expected = broadcastTicks * backgroundClients.Count;

		List<double> sorted = [.. probeLatencies];

		sorted.Sort();

		string latency = sorted.Count == 0 ? "n/a" : $"{sorted[sorted.Count / 2]:0.0}/{sorted[^1]:0.0}";

		output.WriteLine($"  t={Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds,5:0}s ticks={broadcastTicks,6} delivered={(expected == 0 ? 100 : delivered * 100.0 / expected):0.00}% probeMedian/MaxMs={latency} timedOut={probesTimedOut} closed={backgroundClients.Count(client => client.IsClosed)}");
	}

	private static int ReadSetting(string environmentVariableName, int defaultValue)
	{
		string? value = Environment.GetEnvironmentVariable(environmentVariableName);

		return int.TryParse(value, out int parsed) ? parsed : defaultValue;
	}

	private static Thread StartShardPump(List<RelayTestPeer> shard, CancellationToken cancellationToken)
	{
		Thread thread = new(() =>
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				for (int i = 0; i < shard.Count; i++)
				{
					shard[i].Poll();
				}

				cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(5));
			}
		})
		{
			IsBackground = true,

			Name = "RelaySoak-ShardPump",
		};

		thread.Start();

		return thread;
	}

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

			occupied.Wait(TimeSpan.FromSeconds(2));
		}

		public void Dispose()
		{
			_release.Set();

			Thread.Sleep(TimeSpan.FromMilliseconds(200));

			_release.Dispose();
		}
	}

	private sealed class RelaySoakFixture : IDisposable
	{
		public int Port { get; }

		private readonly Server _server;

		private readonly CancellationTokenSource _cancellation;

		private readonly Task<int> _runTask;

		public RelaySoakFixture(ITestOutputHelper output, string connectionKey)
		{
			Port = ReserveUdpPort();

			_cancellation = new CancellationTokenSource();

			// Warnings and above only: a soak at 30 Hz would otherwise bury the run in per-payload debug lines.
			_server = new Server(Port, connectionKey, new TestOutputLogger<Server>(output, Microsoft.Extensions.Logging.LogLevel.Warning));

			_runTask = _server.RunAsync(_cancellation.Token);

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
