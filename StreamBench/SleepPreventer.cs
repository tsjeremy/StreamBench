// SleepPreventer.cs
// Prevents Windows from sleeping while a long-running benchmark or download is active.
// Screen-off timeout is intentionally unaffected — only unattended system sleep is blocked.
//
// Usage:
//   using var _sleep = SleepPreventer.Acquire();
//   // ... long work ...
//   // SleepGuard.Dispose() is called automatically, restoring normal sleep behaviour.

using System.Runtime.InteropServices;

namespace StreamBench;

/// <summary>
/// Wraps the Windows <c>SetThreadExecutionState</c> API so that system sleep is
/// prevented during long benchmark or model-download operations.
/// Screen-off timeout is not affected (<c>ES_DISPLAY_REQUIRED</c> is NOT set).
/// No-op on non-Windows platforms.
/// </summary>
public static class SleepPreventer
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────
#pragma warning disable CA1416  // Windows-only; guarded by OperatingSystem.IsWindows()

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS      = 0x80000000u;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001u;

#pragma warning restore CA1416

    /// <summary>
    /// Acquires a sleep-prevention lease. Dispose the returned object to release it.
    /// Safe to call on non-Windows — returns a no-op guard.
    /// </summary>
    public static SleepGuard Acquire()
    {
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
#pragma warning restore CA1416
        }
        return new SleepGuard();
    }

    /// <summary>Disposable lease returned by <see cref="Acquire"/>.</summary>
    public sealed class SleepGuard : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                SetThreadExecutionState(ES_CONTINUOUS); // clear — restore normal sleep behaviour
#pragma warning restore CA1416
            }
        }
    }
}
