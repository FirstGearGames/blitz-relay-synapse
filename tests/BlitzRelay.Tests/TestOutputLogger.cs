using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace BlitzRelay.Tests;

internal sealed class TestOutputLogger<T0>(ITestOutputHelper output, LogLevel minimumLevel = LogLevel.Debug) : ILogger<T0>
{
	public IDisposable? BeginScope<TState>(TState state) where TState : notnull
	{
		return null;
	}

	public bool IsEnabled(LogLevel logLevel)
	{
		return logLevel >= minimumLevel;
	}

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel)) return;

		try
		{
			output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? string.Empty : $" {exception}")}");
		}
		catch (InvalidOperationException)
		{
			// The test has already finished, so there is nowhere left to write.
		}
	}
}
