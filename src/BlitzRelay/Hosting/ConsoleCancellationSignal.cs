namespace BlitzRelay.Hosting;

internal sealed class ConsoleCancellationSignal : ICancellationSignal
{
	public IDisposable Register(CancellationTokenSource cancellationTokenSource)
	{
		Console.CancelKeyPress += ConsoleCancelKeyPressEventHandler;

		return new DisposableAction(() => Console.CancelKeyPress -= ConsoleCancelKeyPressEventHandler);

		void ConsoleCancelKeyPressEventHandler(object? sender, ConsoleCancelEventArgs args)
		{
			args.Cancel = true;

			cancellationTokenSource.Cancel();
		}
	}

	private sealed class DisposableAction(Action action) : IDisposable
	{
		public void Dispose()
		{
			action();
		}
	}
}
