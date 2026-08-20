using System.Diagnostics;
using Newfarm.Server;

namespace BlitzRelay.Tests;

// A real newfarm directory on a real loopback port, with the timings tightened so a test does not spend half a minute waiting
// for a host to be missed.
internal sealed class NewfarmDirectoryFixture : IDisposable
{
	public int Port
	{
		get => _server.BoundPort;
	}

	private readonly NewfarmServer _server;

	private readonly CancellationTokenSource _cancellation;

	private readonly Thread _serverThread;

	public NewfarmDirectoryFixture(Action<NewfarmServerConfig>? configure = null)
	{
		NewfarmServerConfig config = new()
		{
			Port = 0,

			HostTimeoutMilliseconds = 1500,

			WaiterTimeoutMilliseconds = 3000,

			ElectionDeadlineMilliseconds = 30000,

			SweepIntervalMilliseconds = 50,
		};

		configure?.Invoke(config);

		_server = new NewfarmServer(config);

		_cancellation = new CancellationTokenSource();

		_serverThread = new Thread(() => _server.Run(_cancellation.Token))
		{
			IsBackground = true,

			Name = "Newfarm-Migration-Test",
		};

		_serverThread.Start();

		long startedTimestamp = Stopwatch.GetTimestamp();

		while (_server.BoundPort == 0 && Stopwatch.GetElapsedTime(startedTimestamp) < TimeSpan.FromSeconds(10))
		{
			Thread.Sleep(5);
		}

		if (_server.BoundPort == 0) throw new TimeoutException("Timed out waiting for newfarm to bind.");
	}

	public void Dispose()
	{
		_cancellation.Cancel();

		_serverThread.Join(TimeSpan.FromSeconds(5));

		_server.Dispose();

		_cancellation.Dispose();
	}
}
