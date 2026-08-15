using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BlitzRelay.Networking;

// Windows rounds a wait up to the system timer tick, 15.6ms by default, so a poll loop that asks for 3ms is handed
// 15.6ms and the relay's forwarding latency is pinned to the tick rather than to its own interval. Measured on this
// machine: every requested interval from 1ms to 15ms came back at 15.6ms. A high-resolution waitable timer is exempt
// from the tick (3.15ms mean for a 3ms request, 3.01 to 3.54 spread) and, unlike timeBeginPeriod, raises no timer
// resolution process-wide. Linux and macOS waits are already fine-grained, so they keep the plain handle wait.
internal sealed class PollWaiter : IDisposable
{
	private const uint CreateWaitableTimerHighResolution = 0x00000002;

	private const uint TimerAllAccess = 0x1F0003;

	private readonly CancellationToken _cancellationToken;

	private readonly SafeWaitHandle? _timerHandle;

	private readonly EventWaitHandle? _timer;

	private readonly WaitHandle[]? _waitHandles;

	public PollWaiter(CancellationToken cancellationToken)
	{
		_cancellationToken = cancellationToken;

		if (!OperatingSystem.IsWindows()) return;

		SafeWaitHandle timerHandle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);

		// Pre-1803 Windows rejects the high-resolution flag. The plain wait still works, just at the tick.
		if (timerHandle.IsInvalid)
		{
			timerHandle.Dispose();

			return;
		}

		_timerHandle = timerHandle;

		_timer = new EventWaitHandle(initialState: false, EventResetMode.AutoReset)
		{
			SafeWaitHandle = timerHandle,
		};

		_waitHandles = [_timer, cancellationToken.WaitHandle];
	}

	public void Wait(TimeSpan duration)
	{
		if (duration <= TimeSpan.Zero) return;

		if (_timerHandle is null || _waitHandles is null)
		{
			_cancellationToken.WaitHandle.WaitOne(duration);

			return;
		}

		WaitHighResolution(duration);
	}

	public void Dispose()
	{
		_timer?.Dispose();
	}

	// A negative due time is relative, in 100ns units.
	[SupportedOSPlatform("windows")]
	private void WaitHighResolution(TimeSpan duration)
	{
		long dueTime = -duration.Ticks;

		if (!SetWaitableTimerEx(_timerHandle!, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
		{
			_cancellationToken.WaitHandle.WaitOne(duration);

			return;
		}

		WaitHandle.WaitAny(_waitHandles!);
	}

	[SupportedOSPlatform("windows")]
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

	[SupportedOSPlatform("windows")]
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, in long dueTime, int period, IntPtr completionRoutine, IntPtr argToCompletionRoutine, IntPtr wakeContext, uint tolerableDelay);
}
