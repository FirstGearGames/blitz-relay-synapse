using BlitzRelay.Networking;
using System.Net;
using System.Net.Sockets;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

// A real relay on a real loopback port, running its poll loop for as long as the fixture is alive.
internal sealed class RelayHostFixture : IDisposable
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

	private static int ReserveUdpPort()
	{
		using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

		probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

		return ((IPEndPoint)probe.LocalEndPoint!).Port;
	}
}
