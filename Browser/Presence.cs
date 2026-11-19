// While the Browser or the YouTube player is open, Lanternfall's idle screen (SeleneCore) must stay away and Windows must not
// turn the display off. The two add-ons share only the Playnite process, so the signal is an AppDomain slot that SeleneCore
// reads ("Lanternfall.MediaActive", a bool), plus SetThreadExecutionState for the display.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SeleneBrowser
{
    internal static class Presence
    {
        public const string Slot = "Lanternfall.MediaActive";
        private static readonly HashSet<string> holders = new HashSet<string>();
        private static readonly object gate = new object();

        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

        // call on the UI thread (the execution state belongs to the calling thread)
        public static void Enter(string who) { lock (gate) { if (holders.Add(who) && holders.Count == 1) Apply(true); } }
        public static void Leave(string who) { lock (gate) { if (holders.Remove(who) && holders.Count == 0) Apply(false); } }

        private static void Apply(bool on)
        {
            try { AppDomain.CurrentDomain.SetData(Slot, on); } catch { }
            try { SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS); } catch { }
        }
    }
}
