using BlitzRelay.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlitzRelay.Hosting;

internal sealed class RelayHost
(
	Func<RelayHostOptions, ILoggerFactory, Server> serverFactory,
	Func<RelayHostOptions, Server, WebApplication> httpApiFactory,
	Func<ILoggerFactory> createLoggerFactory,
	ICancellationSignal cancellationSignal
)
	: IRelayHost
{
	public async Task<int> RunAsync(RelayHostOptions relayHostOptions, CancellationToken cancellationToken)
	{
		using ILoggerFactory loggerFactory = createLoggerFactory();

		using Server server = serverFactory(relayHostOptions, loggerFactory);

		using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		using IDisposable registration = cancellationSignal.Register(cancellationTokenSource);

		await using WebApplication httpApi = httpApiFactory(relayHostOptions, server);

		try
		{
			Task<int> relayServerTask = server.RunAsync(cancellationTokenSource.Token);

			await httpApi.StartAsync(CancellationToken.None);

			Task httpApiTask = httpApi.WaitForShutdownAsync(CancellationToken.None);

			Task completedTask = await Task.WhenAny(relayServerTask, httpApiTask);

			if (completedTask == relayServerTask) await httpApi.StopAsync(CancellationToken.None);

			await cancellationTokenSource.CancelAsync();

			return await relayServerTask;
		}
		catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
		{
			return 0;
		}
	}
}
