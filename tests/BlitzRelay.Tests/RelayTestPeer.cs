using BlitzRelay.Protocol;
using SynapseSocket.Connections;
using SynapseSocket.Core;
using SynapseSocket.Core.Configuration;
using System.Net;

namespace BlitzRelay.Tests;

// A real SynapseSocket peer on a real UDP socket. Nothing here is mocked: the relay under test sees exactly what a
// game client would put on the wire.
internal sealed class RelayTestPeer : IDisposable
{
	private static readonly TimeSpan PumpDelay = TimeSpan.FromMilliseconds(5);

	private readonly SynapseManager _synapseManager;

	private readonly CancellationTokenSource _cancellation;

	private readonly Task _pumpTask;

	private readonly Lock _mutex = new();

	private readonly List<byte[]> _receivedMessages;

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

		_synapseManager = new SynapseManager(synapseConfig);

		_synapseManager.ConnectionEstablished += _ => _isConnected = true;

		_synapseManager.ConnectionClosed += _ => _isClosed = true;

		_synapseManager.PacketReceived += HandlePacketReceived;

		_synapseManager.Start();

		_pumpTask = Task.Run(PumpAsync);
	}

	// A handshake is a single unacknowledged datagram, so a peer that hears nothing back sends another one.
	public async Task ConnectAsync(int relayPort, TimeSpan timeout)
	{
		IPEndPoint relayEndPoint = new(IPAddress.Loopback, relayPort);

		DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

		while (!_isConnected && DateTimeOffset.UtcNow < deadline)
		{
			lock (_mutex)
			{
				_connection = _synapseManager.Connect(relayEndPoint);
			}

			await WaitUntilAsync(() => _isConnected, TimeSpan.FromSeconds(1), "connection to be established", throwOnTimeout: false);
		}

		if (!_isConnected) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for a connection to the relay on port {relayPort}.");
	}

	public async Task AuthenticateAsync(string connectionKey)
	{
		Send(MessageCodec.CreateAuthenticate(connectionKey), isReliable: true);

		// A successful authentication is answered with silence, so give the relay a poll or two to record it.
		await Task.Delay(TimeSpan.FromMilliseconds(100));
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
	public async Task<byte[]> WaitForMessageAsync(MessageType messageType, TimeSpan timeout)
	{
		byte[]? message = null;

		await WaitUntilAsync(() => TryTakeMessage(messageType, out message), timeout, $"a {messageType} message");

		return message!;
	}

	public async Task WaitUntilClosedAsync(TimeSpan timeout)
	{
		await WaitUntilAsync(() => _isClosed, timeout, "the relay to close the connection");
	}

	public async Task<bool> HasReceivedMessageAsync(MessageType messageType, TimeSpan within)
	{
		byte[]? message = null;

		try
		{
			await WaitUntilAsync(() => TryTakeMessage(messageType, out message), within, $"a {messageType} message");

			return true;
		}
		catch (TimeoutException)
		{
			return false;
		}
	}

	public void Dispose()
	{
		_cancellation.Cancel();

		try
		{
			_pumpTask.Wait(TimeSpan.FromSeconds(5));
		}
		catch (AggregateException)
		{
			// The pump was cancelled, which is the expected way for it to end.
		}

		_synapseManager.Dispose();

		_cancellation.Dispose();
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string description, bool throwOnTimeout = true)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

		while (DateTimeOffset.UtcNow < deadline)
		{
			if (condition()) return;

			await Task.Delay(TimeSpan.FromMilliseconds(5));
		}

		if (condition() || !throwOnTimeout) return;

		throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.##}s waiting for {description}.");
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

	private async Task PumpAsync()
	{
		while (!_cancellation.IsCancellationRequested)
		{
			lock (_mutex)
			{
				_synapseManager.Poll();
			}

			try
			{
				await Task.Delay(PumpDelay, _cancellation.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}
}
