// PadActivity.cs - tells Lanternfall Core that the pad is being used inside the Browser / YouTube overlay.
// The Browser reads the right stick (and page input) itself, so Playnite never sees it and Core's controller-rest timer would
// otherwise count a minute of browsing as "no input". Same shared-process slot pattern as the rest chip.
using System;

namespace SeleneBrowser
{
    internal static class PadActivity
    {
        public const string Slot = "Lanternfall.BrowserInput";
        private static int lastMark;

        // cheap: at most one slot write per half second
        public static void Mark()
        {
            int now = Environment.TickCount;
            if (unchecked(now - lastMark) < 500) return;
            lastMark = now;
            try { AppDomain.CurrentDomain.SetData(Slot, DateTime.UtcNow.Ticks); } catch { }
        }
    }
}
