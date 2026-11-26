// PadInput.cs - XInput polling (right stick, triggers, raw buttons) and a repeat limiter for held pad presses.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SeleneBrowser
{
    /// <summary>Polls controller 0..3 through xinput1_4.dll (fallback xinput9_1_0.dll). Call Update() once per tick.</summary>
    internal sealed class PadInput
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XState
        {
            public uint PacketNumber;
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short LX, LY, RX, RY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int GetState14(int index, out XState state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern int GetState910(int index, out XState state);

        private const double Dead = 0.24;
        private bool use910;
        private bool broken;

        /// <summary>Right stick, -1..1 with dead zone removed; Y is positive when pushed up.</summary>
        public double RightX, RightY;
        /// <summary>Left stick, the same way (it scrolls the page; Playnite also reports it as D-pad presses).</summary>
        public double LeftX, LeftY;
        public double LeftTrigger, RightTrigger;
        public ushort Buttons;
        public bool Connected;

        public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
        public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;

        /// <summary>Reads the first connected pad; returns Connected.</summary>
        public bool Update()
        {
            Connected = false; RightX = RightY = LeftX = LeftY = LeftTrigger = RightTrigger = 0; Buttons = 0;
            if (broken) return false;
            try
            {
                XState s;
                if (ReadFirst(out s))
                {
                    Connected = true;
                    Buttons = s.Buttons;
                    LeftTrigger = s.LeftTrigger / 255.0;
                    RightTrigger = s.RightTrigger / 255.0;
                    Stick(s.RX / 32767.0, s.RY / 32767.0, out RightX, out RightY);
                    Stick(s.LX / 32767.0, s.LY / 32767.0, out LeftX, out LeftY);
                    return true;
                }
            }
            catch (DllNotFoundException)
            {
                if (use910) broken = true; else use910 = true;
            }
            catch (EntryPointNotFoundException) { broken = true; }
            return false;
        }

        private int slotHint;       // the slot that answered last
        private int lastFullScan = Environment.TickCount - 2000;

        // The first connected pad. Slot 0 and the slot that answered last are asked every time; the other slots are slow to answer
        // when empty (some drivers), so a keyboard-only PC would pay for three failing calls on every tick: they are looked at once a second.
        private bool ReadFirst(out XState s)
        {
            s = default(XState);
            int now = Environment.TickCount;
            bool full = unchecked(now - lastFullScan) >= 1000;
            if (full) lastFullScan = now;
            for (int i = 0; i < 4; i++)
            {
                if (i != 0 && i != slotHint && !full) continue;
                int r = use910 ? GetState910(i, out s) : GetState14(i, out s);
                if (r != 0) continue;
                slotHint = i;
                return true;
            }
            slotHint = 0;
            return false;
        }

        /// <summary>
        /// A fresh look at the pad right now (Update's numbers are up to one tick old). Used to tell a real D-pad press from the left
        /// stick: Playnite reports both as "DPad..." presses. Connected is false when no XInput pad answers (a keyboard, another pad type).
        /// </summary>
        public void Sample(out bool connected, out ushort buttons, out bool leftStickPushed)
        {
            connected = false; buttons = 0; leftStickPushed = false;
            if (broken) return;
            try
            {
                XState s;
                if (ReadFirst(out s))
                {
                    connected = true;
                    buttons = s.Buttons;
                    double x, y;
                    Stick(s.LX / 32767.0, s.LY / 32767.0, out x, out y);
                    leftStickPushed = x != 0 || y != 0;
                }
            }
            catch (DllNotFoundException) { if (use910) broken = true; else use910 = true; }
            catch (EntryPointNotFoundException) { broken = true; }
        }

        // Radial dead zone, rescaled so the output starts at 0.
        private static void Stick(double x, double y, out double ox, out double oy)
        {
            double m = Math.Sqrt(x * x + y * y);
            if (m < Dead) { ox = oy = 0; return; }
            double k = Math.Min(1.0, (m - Dead) / (1.0 - Dead)) / m;
            ox = x * k; oy = y * k;
        }
    }

    /// <summary>
    /// Turns the pad contract's "repeated press every ~80 ms while held" into a usable stream.
    /// Allow(): fires at once, then after FirstDelay, then every Interval. IsFirst(): true only for a fresh press.
    /// </summary>
    internal sealed class RepeatLimiter
    {
        private sealed class State { public int Start, LastFire, LastSeen; }
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();
        public int FirstDelay = 350, Interval = 90, Gap = 180;
        // Optional second, faster stage for held directions. 0 = disabled (the default for other screens).
        public int FastAfter = 0, FastInterval = 55;

        private State Touch(string key, out bool fresh)
        {
            int now = Environment.TickCount;
            State s;
            fresh = false;
            if (!states.TryGetValue(key, out s)) { s = new State(); states[key] = s; fresh = true; }
            else if (now - s.LastSeen > Gap) fresh = true;
            if (fresh) { s.Start = now; s.LastFire = now; }
            s.LastSeen = now;
            return s;
        }

        public bool Allow(string key)
        {
            bool fresh;
            var s = Touch(key, out fresh);
            if (fresh) return true;
            int now = Environment.TickCount;
            int interval = FastAfter > 0 && now - s.Start >= FastAfter ? FastInterval : Interval;
            if (now - s.Start < FirstDelay || now - s.LastFire < interval) return false;
            s.LastFire = now;
            return true;
        }

        public bool IsFirst(string key)
        {
            bool fresh;
            Touch(key, out fresh);
            return fresh;
        }
    }
}
