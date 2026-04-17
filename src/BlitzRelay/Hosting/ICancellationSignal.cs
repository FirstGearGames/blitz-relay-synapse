namespace BlitzRelay.Hosting;

public interface ICancellationSignal
{
	IDisposable Register(CancellationTokenSource cancellationTokenSource);
}
