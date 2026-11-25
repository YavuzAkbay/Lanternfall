using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Selene
{
    // Lanternfall: let the controller rest while a film plays, without ever touching the video.
    //
    // The pad contract in HomeNav routes every press to the Browser / YouTube overlay while one is up, and Presence keeps
    // the display awake and the idle screen away. Nothing here hides an overlay, pauses media or moves focus. All this does
    // is stop PadTapGuard's 2 ms XInput poll (and its 1 ms system timer) after a while with no input, and - in the opt-in
    // "Power off" mode - hold the Guide button back from Selene so the user can use the pad's own 6-second firmware
    // power-off. That button is also claimed by Windows (Game Bar / Task View) and Steam, so Power off cannot be switched
    // on until a one-time check has proved that the pad switches off while Playnite keeps the foreground.
    //
    // A passive prompt is published to the Browser plugin through AppDomain slot "Lanternfall.PadRestPrompt"; the two
    // plugins share a process but not types. The Browser draws it in its own click-through UI and never takes input.
    internal static class PadRest
    {
        public const string PromptSlot = "Lanternfall.PadRestPrompt";
        private const string VerifiedKey = "padrestverified";
        private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan VerifyLimit = TimeSpan.FromSeconds(18);

        [StructLayout(LayoutKind.Sequential)]
        private struct XState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XCaps
        {
            public byte Type, SubType;
            public short Flags, Version;
            public int InputSize, OutputSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersion
        {
            public int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Csd;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OsVersion info);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int GetState(int slot, out XState state);
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
        private static extern int GetCapabilities(int slot, int flags, out XCaps caps);

        private static DispatcherTimer watch, verifyTimer, clearTimer;
        private static DateTime lastInput = DateTime.Now;
        private static DateTime verifyAt, verifyFocusAt;
        private static string prompt, lastFailure;
        private static volatile bool armed, padOff, verifying, focusLost, broken, verifySawPad;
        private static Action verifyDone;
        private static volatile int pads;

        // Guide is held back only where it could cover a film: while resting, while the pad is off, while waking,
        // and during the one-time verification. On every other screen Guide behaves exactly as before.
        // Low power leaves Guide alone (it must still take you home); only Power off needs it held back, and only until the pad
        // has been woken and a short moment has passed.
        internal static bool BlockingGuide
        {
            get
            {
                if (verifying || padOff) return true;
                if (!armed || armedMode != "poweroff") return false;
                return !woken || DateTime.Now < wokeUntil;
            }
        }
        private static string armedMode = "";
        private static volatile bool woken;
        private static DateTime wokeUntil = DateTime.MinValue;

        private static string Mode
        {
            get
            {
                string v = Features.Value("controllerrest");
                return v == "off" || v == "low" || v == "poweroff" ? v : "low";
            }
        }

        public static void Install()
        {
            if (watch != null) return;
            watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            watch.Tick += (s, e) => Check();
            watch.Start();
            HomeNav.Log("controller rest: installed (" + Mode + ")");
        }

        // Any real input ends the quiet moment. HomeNav filters Guide out before this call while resting: the home button
        // is the pad's power button then, and must not disarm rest mode or fail the one-time verification.
        public static void Touched()
        {
            // the press that opened the verification (its key copy, its release) must not end it at once
            if (verifying && (DateTime.Now - verifyAt).TotalMilliseconds < 1000) return;
            lastInput = DateTime.Now;
            if (armed) Disarm("input");
            if (verifying) FinishVerify(false, "The pad was used during verification.");
        }

        private static void Check()
        {
            try
            {
                if (verifying) { VerifyTick(); return; }
                string mode = Mode;
                if (mode != "poweroff" && padOff) Disarm("mode changed");
                if (mode == "off" || !ForkBrand.MediaActive() || !HomeNav.PlayniteInFront() || HomeNav.Launching || HomeNav.GameRunning()
                    || IdleScreen.Showing)
                {
                    if (armed) Disarm("not watching");
                    return;
                }
                // the Browser reads the pad itself (right stick, page keys): it publishes when, so reading is not "no input"
                try
                {
                    object seen = AppDomain.CurrentDomain.GetData("Lanternfall.BrowserInput");
                    if (seen is long)
                    {
                        DateTime at = new DateTime((long)seen, DateTimeKind.Utc).ToLocalTime();
                        if (at > lastInput) lastInput = at;
                    }
                }
                catch { }
                Poll();
                if (mode == "poweroff" && !Verified())
                {
                    if (armed) Disarm("not verified");
                    return;
                }
                // Once armed, a pad switching off is expected: TrackPad() owns that state and Guide must stay held back
                // until the pad is back and the user actually uses it. Only refuse to *start* resting with the wrong pad count.
                if (!armed && mode == "poweroff" && pads != 1) return;
                if (armed && mode == "poweroff" && pads > 1) { Disarm("more than one pad"); return; }
                TimeSpan idle = Idle;
                try
                {
                    // developer switch: selene-dev-padrest ("10" or any number) rests after that many seconds
                    string dev = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selene-dev-padrest");
                    if (System.IO.File.Exists(dev))
                    {
                        double secs;
                        if (double.TryParse(System.IO.File.ReadAllText(dev).Trim(), out secs) && secs > 0 && secs < 3600)
                            idle = TimeSpan.FromSeconds(secs);
                    }
                }
                catch { }
                if (DateTime.Now - lastInput < idle)
                {
                    if (armed) Disarm("active");
                    return;
                }
                if (!armed) Arm(mode);
                else if (mode == "poweroff") TrackPad();
            }
            catch (Exception ex) { HomeNav.Log("controller rest: " + ex.Message); }
        }

        private static void Arm(string mode)
        {
            armed = true;
            armedMode = mode;
            woken = false;
            padOff = false;
            PadTapGuard.Suspended = true;       // stop the 2 ms poll and the 1 ms system timer
            PadTapGuard.Flush = true;           // no replay of a half-pressed edge from before the rest
            SetPrompt(mode == "low" ? "low" : "armed");
            HomeNav.Log("controller rest: " + (mode == "low" ? "low power" : "power off armed") + " (" + pads + " pad(s))");
        }

        private static void Disarm(string why)
        {
            if (!armed && !padOff) return;
            armed = false;
            padOff = false;
            PadTapGuard.Suspended = false;
            PadTapGuard.ResumeAfter = Environment.TickCount + 300;   // a pad that just came back can report a held button as new
            SetPrompt("");
            HomeNav.Log("controller rest: awake (" + why + ")");
        }

        // The pad powers itself off in firmware; the only sign is XInput going quiet. When it comes back, Guide is still
        // held back so the power-on press cannot open Selene's guide or pull a game in front of the video.
        private static void TrackPad()
        {
            bool present = pads > 0;
            if (!present && !padOff)
            {
                padOff = true;
                SetPrompt("off");
                HomeNav.Log("controller rest: pad switched off");
            }
            else if (present && padOff)
            {
                padOff = false;
                woken = true;   // Guide stays held for a few seconds (the wake press), then it is Guide again
                wokeUntil = DateTime.Now.AddSeconds(4);
                PadTapGuard.ResumeAfter = Environment.TickCount + 300;
                SetPrompt("");
                HomeNav.Log("controller rest: pad is back");
            }
        }

        private static void Poll()
        {
            int found = 0;
            XState s;
            for (int i = 0; i < 4; i++)
            {
                try { if (GetState(i, out s) == 0) found++; }
                catch (Exception ex) { broken = true; HomeNav.Log("controller rest: xinput: " + ex.Message); break; }
            }
            pads = broken ? 1 : found;
        }

        private static void SetPrompt(string value)
        {
            if (value == prompt) return;
            prompt = value;
            try { AppDomain.CurrentDomain.SetData(PromptSlot, value); }
            catch (Exception ex) { HomeNav.Log("controller rest: prompt: " + ex.Message); }
        }

        // ---- one-time verification -------------------------------------------------------------------------------

        // The settings row opens this dialog itself, so its own "ask" field routes pad A/B to it (controller-only app).
        public static Dialog VerifyDialog(Action onStart, Action onCancel)
        {
            return new Dialog("\uE896", "Verify controller power-off",
                "Hold the home button on your controller for 8 seconds and keep Playnite in front.\n\n"
                + "First turn off, in Windows: Settings > Gaming > Game Bar > 'Allow your controller to open Game Bar', and Settings > Gaming > Xbox mode. In Steam: Controller > 'Guide button focuses Steam'.\n\n"
                + "Power off stays unavailable unless the pad switches off without Windows covering the screen.",
                "Start", "Not now", onStart, onCancel, false, true);
        }

        public static void StartVerify(Action done)
        {
            if (verifying) return;
            verifyDone = done;
            if (clearTimer != null) clearTimer.Stop();
            verifying = true;
            verifyAt = DateTime.Now;
            verifyFocusAt = DateTime.MinValue;
            focusLost = false;
            lastFailure = null;
            lastInput = DateTime.Now;
            pads = 1;
            // the check proves the pad switched off, so there must be exactly one to begin with: with none (keyboard only, or a
            // pad XInput cannot see) "no pad" is the starting state, and the first tick would otherwise pass as verified
            verifySawPad = false;
            int atStart = CountPads();
            if (atStart != 1)
            {
                FinishVerify(false, atStart == 0 ? "No Xbox-compatible controller found. Connect one and try again." : "Connect exactly one controller to verify.");
                return;
            }
            verifySawPad = true;
            SetPrompt("verify");
            if (verifyTimer == null)
            {
                verifyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                verifyTimer.Tick += (s, e) => VerifyTick();
            }
            verifyTimer.Start();
            HomeNav.Log("controller rest: verification started");
        }

        private static void VerifyTick()
        {
            if (!verifying) return;
            try
            {
                if (!HomeNav.PlayniteInFront())
                {
                    if (verifyFocusAt == DateTime.MinValue) verifyFocusAt = DateTime.Now;
                    if ((DateTime.Now - verifyFocusAt).TotalMilliseconds > 600) focusLost = true;
                }
                else verifyFocusAt = DateTime.MinValue;
                Poll();
                if (pads == 1) verifySawPad = true;
                if (pads == 0 && !broken && verifySawPad)
                {
                    Features.Remember(VerifiedKey, Build());
                    Features.SetValue("controllerrest", "poweroff");   // only now: a cancelled check leaves Low power alone
                    FinishVerify(true, null);
                    SetPrompt("verified");
                    HomeNav.Log("controller rest: verified on " + Build());
                    return;
                }
                if (focusLost) { FinishVerify(false, "Windows took the front. Turn off Game Bar controller binding and Xbox mode."); return; }
                if ((DateTime.Now - verifyAt) > VerifyLimit) { FinishVerify(false, "The pad did not switch off. Hold the home button 8 seconds."); return; }
            }
            catch (Exception ex) { FinishVerify(false, "Verification failed: " + ex.Message); }
        }

        private static void FinishVerify(bool ok, string why)
        {
            if (!verifying) return;
            verifying = false;
            if (verifyTimer != null) verifyTimer.Stop();
            if (!ok)
            {
                lastFailure = why;
                SetPrompt("verifyfail");
                HomeNav.Log("controller rest: verification failed: " + why);
            }
            // the Browser hides these itself after a few seconds; clear the slot too so a later video does not show them again
            if (clearTimer == null)
            {
                clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
                clearTimer.Tick += (s, e) => { clearTimer.Stop(); if (prompt == "verified" || prompt == "verifyfail") SetPrompt(""); };
            }
            clearTimer.Stop();
            clearTimer.Start();
            if (verifyDone != null)
            {
                Action done = verifyDone;
                verifyDone = null;
                try { done(); } catch (Exception ex) { HomeNav.Log("controller rest: refresh: " + ex.Message); }
            }
        }

        private static string Build()
        {
            // Environment.OSVersion lies (6.2) in a process without a Windows 10 manifest; the kernel call does not
            try
            {
                var v = new OsVersion();
                v.Size = Marshal.SizeOf(typeof(OsVersion));
                if (RtlGetVersion(ref v) == 0 && v.Major > 0) return v.Major + "." + v.Minor + "." + v.Build;
            }
            catch { }
            return Environment.OSVersion.Version.Major + "." + Environment.OSVersion.Version.Minor + "." + Environment.OSVersion.Version.Build;
        }

        private static bool Verified()
        {
            string v = Features.Remembered(VerifiedKey);
            return v != null && v == Build();
        }

        // ---- the two settings rows --------------------------------------------------------------------------------

        public static string ChoiceNote()
        {
            string mode = Mode;
            if (mode == "low") return "Guide is held while resting; B or Start still exits.";
            if (mode != "poweroff") return "";
            if (!Verified()) return "Verify the home button once below before this can turn on.";
            return "Ready (verified on this Windows build)." + Headset() + PadCount();
        }

        public static string Status()
        {
            if (verifying) return "Hold the home button 8 seconds now...";
            if (Verified()) return "Verified. Power off is available." + Headset() + PadCount();
            if (CountPads() != 1) return "Connect exactly one controller to verify.";
            if (lastFailure != null) return lastFailure;
            return "Not verified yet.";
        }

        private static string PadCount()
        {
            int n = CountPads();
            return n == 1 ? "" : " Connect only one controller for power off.";
        }

        private static int CountPads()
        {
            int found = 0;
            XState s;
            try { for (int i = 0; i < 4; i++) if (GetState(i, out s) == 0) found++; }
            catch { return 1; }
            return found;
        }

        private static string Headset()
        {
            try
            {
                XCaps c;
                if (broken) return "";
                for (int i = 0; i < 4; i++)
                    if (GetCapabilities(i, 0, out c) == 0 && ((c.Flags & 0x0004) != 0))
                        return " A headset on the pad goes silent when it powers off.";
            }
            catch { }
            return "";
        }
    }
}
