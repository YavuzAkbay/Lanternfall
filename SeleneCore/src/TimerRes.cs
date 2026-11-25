using System;
using System.Runtime.InteropServices;

namespace Selene
{
    // Lanternfall: a counted hold on Windows' 1 ms timer resolution. A DispatcherTimer at 16.7 ms (the glide and the row-follow
    // timers on a slower screen) is rounded up to ~31 ms ticks at the default 15.6 ms resolution, which is a 32 fps row.
    // PadTapGuard also holds 1 ms while it is active, but the smooth timers must not depend on it.
    internal static class TimerRes
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private static readonly object gate = new object();
        private static int held;

        public static void Acquire()
        {
            lock (gate)
            {
                if (held++ == 0) { try { timeBeginPeriod(1); } catch { } }
            }
        }

        public static void Release()
        {
            lock (gate)
            {
                if (held == 0) return;
                if (--held == 0) { try { timeEndPeriod(1); } catch { } }
            }
        }
    }
}
