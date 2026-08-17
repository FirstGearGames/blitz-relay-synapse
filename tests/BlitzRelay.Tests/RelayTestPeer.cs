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
	private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(5);

	private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(5);

	private readonly SynapseManager _synapseManager;

	private readonly CancellationTokenSource _cancellation;

	// Null when the caller pumps this peer itself, which is how a test can run hundreds of peers without a thread each.
	private readonly Thread? _pumpThread;

	private readonly Lock _mutex = new();

	private readonly List<byte[]> _receivedMessages;

	private SynapseConnection? _connection;

	private volatile bool _isConnected;

	private volatile bool _isClosed;

	private int _dataMessageCount;

	public bool IsClosed
	{
		get => _isClosed;
	}

	// Set on a peer that is only there to make up the numbers: it tallies Data messages instead of retaining them, so a
	// long broadcast run cannot grow its list without bound.
	public bool CountDataOnly { get; set; }

	public int DataMessageCount
	{
		get => Volatile.Read(ref _dataMessageCount);
	}

	public RelayTestPeer(bool ownsPumpThread = true)
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

		_synapseManager = new SynapseManager(synapseConfig);

		_synapseManager.ConnectionEstablished += _ => _isConnected = true;

		_synapseManager.ConnectionClosed += _ => _isClosed = true;

		_synapseManager.PacketReceived += HandlePacketReceived;

		_synapseManager.Start();

		if (!ownsPumpThread) return;

		_pumpThread = new Thread(Pump)
		{
			IsBackground = true,

			Name = "RelayTestPeer-Pump",
		};

		_pumpThread.Start();
	}

	// For a peer the caller pumps itself, in place of the pump thread.
	public void Poll()
	{
		lock (_mutex)
		{
			_synapseManager.Poll();
		}
	}

	public void ClearReceived()
	{
		lock (_mutex)
		{
			_receivedMessages.Clear();
		}
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

		_pumpThread?.Join(TimeSpan.FromSeconds(5));

		_synapseManager.Dispose();

		_cancellation.Dispose();
	}

	private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
	{
		long startedTimestamp = Stopwatch.GetTimestamp();

		while (Stopwatch.GetElapsedTime(startedTimestamp) < timeout)
		{
			if (condition()) return true;

			Thread.Sleep(WaitPollInterval);
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

		if (CountDataOnly && packetReceivedEventArgs.Payload[0] == (byte)MessageType.Data)
		{
			Interlocked.Increment(ref _dataMessageCount);

			return;
		}

		lock (_mutex)
		{
			_receivedMessages.Add(packetReceivedEventArgs.Payload.ToArray());
		}
	}

	private void Pump()
	{
		while (!_cancellation.IsCancellationRequested)
		{
			lock (_mutex)
			{
				_synapseManager.Poll();
			}

			_cancellation.Token.WaitHandle.WaitOne(PumpInterval);
		}
	}
}
