using System.Runtime.InteropServices;

namespace M10Modbus
{
    /// <summary>Raises the Windows timer resolution to 1 ms while at least one poll worker runs (reference counted).
    /// No-op on other operating systems.</summary>
    public static class TimerResolution
    {
        private static readonly object Sync = new();
        private static int _count;
        private static bool _throttlingDisabled;

        public static int ActiveCount { get { lock (Sync) return _count; } }

        public static IDisposable Acquire()
        {
            lock (Sync)
            {
                if (_count++ == 0 && OperatingSystem.IsWindows())
                {
                    TryDisableTimerThrottling();
                    TryCall(() => timeBeginPeriod(1));
                }
            }
            return new Handle();
        }

        private static void Release()
        {
            lock (Sync)
            {
                if (_count == 0) return;
                if (--_count == 0 && OperatingSystem.IsWindows()) TryCall(() => timeEndPeriod(1));
            }
        }

        // Windows 11 may ignore the requested resolution for minimized/occluded windows unless the process opts out.
        private static void TryDisableTimerThrottling()
        {
            if (_throttlingDisabled) return;
            _throttlingDisabled = true;
            TryCall(() =>
            {
                var state = new ProcessPowerThrottlingState { Version = 1, ControlMask = 0x4, StateMask = 0 };
                SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
            });
        }

        private static void TryCall(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
        }

        private sealed class Handle : IDisposable
        {
            private int _disposed;
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Release(); }
        }

        private const int ProcessPowerThrottling = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(IntPtr process, int informationClass,
            ref ProcessPowerThrottlingState information, uint size);
    }
}
