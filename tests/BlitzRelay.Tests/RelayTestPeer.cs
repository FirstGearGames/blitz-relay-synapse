using BlitzRelay.Networking;
using BlitzRelay.Protocol;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using System.Diagnostics;
using System.Net;

namespace BlitzRelay.Tests;

// A real SynapseSocket peer on a real UDP socket. Nothing here is mocked: the relay under test sees exactly what a
// game client would put on the wire. The peer pumps on a thread of its own and every wait blocks rather than awaits,
// so a test can saturate the thread pool without stalling the peer that is supposed to be measuring the relay.
internal sealed class RelayTestPeer : IDisposable
{
	private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(3);

	private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(3);

	private readonly SynapseManager _synapseManager;

	private readonly CancellationTokenSource _cancellation;

	private readonly Thread _pumpThread;

	private readonly Lock _mutex = new();

	private readonly List<byte[]> _receivedMessages;

	// Thread.Sleep rounds to the same 15.6ms Windows tick the pump avoids, which would quantize every measurement this
	// peer takes, so the waits poll on the high-resolution waiter too.
	private readonly PollWaiter _waitPollWaiter;

	private SynapseConnection? _connection;

	private volatile bool _isConnected;

	private volatile bool _isClosed;

	public bool IsClosed
	{
		get => _isClosed;
	}

	public RelayTestPeer()
	{
		SynapseConfig synapseConfig = new()
		{
			BindEndPoints = [new IPEndPoint(IPAddress.Loopback, 0)],

			MaximumPacketSize = 1400,

			MaximumTransmissionUnit = 1200,

			CopyReceivedPayloads = true,

			Segment =
			{
				ReliableEnabled = true,

				UnreliableMode = UnreliableSegmentMode.Disabled,
			},

			Security =
			{
				MaximumPacketsPerSecond = SecurityConfig.DisabledMaximumPacketsPerSecond,

				MaximumBytesPerSecond = SecurityConfig.DisabledMaximumBytesPerSecond,
			},
		};

		_receivedMessages = [];

		_cancellation = new CancellationTokenSource();

		_waitPollWaiter = new PollWaiter(_cancellation.Token);

		_synapseManager = new SynapseManager(synapseConfig);

		_synapseManager.ConnectionEstablished += _ => _isConnected = true;

		_synapseManager.ConnectionClosed += _ => _isClosed = true;

		_synapseManager.PacketReceived += HandlePacketReceived;

		_synapseManager.Start();

		_pumpThread = new Thread(Pump)
		{
			IsBackground = true,

			Name = "RelayTestPeer-Pump",
		};

		_pumpThread.Start();
	}

	// A handshake is a single unacknowledged datagram, so a peer that hears nothing back sends another one.
	public void Connect(int relayPort, TimeSpan timeout)
	{
		IPEndPoint relayEndPoint = new(IPAddress.Loopback, relayPort);

		long startedTimestamp = Stopwatch.GetTimestamp();

		while (!_isConnected && Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
		{
			lock (_mutex)
			{
				_connection = _synapseManager.Connect(relayEndPoint);
			}

			WaitUntil(() => _isConnected, TimeSpan.FromSeconds(1));
		}

		if (!_isConnected) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for a connection to the relay on port {relayPort}.");
	}

	public void Authenticate(string connectionKey)
	{
		Send(MessageCodec.CreateAuthenticate(connectionKey), isReliable: true);

		// A successful authentication is answered with silence, so give the relay a poll or two to record it.
		Thread.Sleep(TimeSpan.FromMilliseconds(100));
	}

	public void Send(byte[] payload, bool isReliable)
	{
		lock (_mutex)
		{
			_synapseManager.Send(_connection!, payload, isReliable);
		}
	}

	public void Disconnect()
	{
		lock (_mutex)
		{
			if (_connection is null) return;

			_synapseManager.Disconnect(_connection);

			_connection = null;
		}
	}

	// Returns the first received message of the requested type, removing it so a later wait sees the next one.
	public byte[] WaitForMessage(MessageType messageType, TimeSpan timeout)
	{
		byte[]? message = null;

		if (!WaitUntil(() => TryTakeMessage(messageType, out message), timeout)) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for a {messageType} message.");

		return message!;
	}

	public bool HasReceivedMessage(MessageType messageType, TimeSpan within)
	{
		return WaitUntil(() => TryTakeMessage(messageType, out _), within);
	}

	public void WaitUntilClosed(TimeSpan timeout)
	{
		if (!WaitUntil(() => _isClosed, timeout)) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for the relay to close the connection.");
	}

	public void Dispose()
	{
		_cancellation.Cancel();

		_pumpThread.Join(TimeSpan.FromSeconds(5));

		_synapseManager.Dispose();

		_waitPollWaiter.Dispose();

		_cancellation.Dispose();
	}

	private bool WaitUntil(Func<bool> condition, TimeSpan timeout)
	{
		long startedTimestamp = Stopwatch.GetTimestamp();

		while (Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
		{
			if (condition()) return true;

			_waitPollWaiter.Wait(WaitPollInterval);
		}

		return condition();
	}

	private bool TryTakeMessage(MessageType messageType, out byte[]? message)
	{
		lock (_mutex)
		{
			for (int i = 0; i < _receivedMessages.Count; i++)
			{
				if (_receivedMessages[i][0] != (byte)messageType) continue;

				message = _receivedMessages[i];

				_receivedMessages.RemoveAt(i);

				return true;
			}
		}

		message = null;

		return false;
	}

	private void HandlePacketReceived(SynapseSocket.Core.Events.PacketReceivedEventArgs packetReceivedEventArgs)
	{
		if (packetReceivedEventArgs.Payload.Count == 0) return;

		lock (_mutex)
		{
			_receivedMessages.Add(packetReceivedEventArgs.Payload.ToArray());
		}
	}

	// Uses the relay's own waiter so the peer is not the slow stage: a plain wait would round this pump up to the
	// 15.6ms Windows tick and the harness, rather than the relay, would set the latency being measured.
	private void Pump()
	{
		using PollWaiter pollWaiter = new(_cancellation.Token);

		while (!_cancellation.IsCancellationRequested)
		{
			lock (_mutex)
			{
				_synapseManager.Poll();
			}

			pollWaiter.Wait(PumpInterval);
		}
	}
}
