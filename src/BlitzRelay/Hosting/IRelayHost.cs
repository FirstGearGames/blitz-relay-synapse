namespace BlitzRelay.Hosting;

public interface IRelayHost
{
	Task<int> RunAsync(RelayHostOptions relayHostOptions, CancellationToken cancellationToken);
}
