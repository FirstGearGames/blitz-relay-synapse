using BlitzRelay.Networking;
using Microsoft.Extensions.Logging;
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

	/// <summary>
	/// Starts a relay that logs to the test's output.
	/// </summary>
	/// <param name="output">The test's output sink.</param>
	/// <param name="connectionKey">The key the relay admits peers with.</param>
	public RelayHostFixture(ITestOutputHelper output, string connectionKey) : this(new TestOutputLogger<Server>(output), connectionKey)
	{
	}

	/// <summary>
	/// Starts a relay that logs through <paramref name="logger"/>.
	/// </summary>
	/// <param name="logger">The logger the relay writes through.</param>
	/// <param name="connectionKey">The key the relay admits peers with.</param>
	public RelayHostFixture(ILogger<Server> logger, string connectionKey)
	{
		Port = ReserveUdpPort();

		_cancellation = new CancellationTokenSource();

		_server = new Server(Port, connectionKey, logger);

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
