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
        public double LeftTrigger, RightTrigger;
        public ushort Buttons;
        public bool Connected;

        public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

        /// <summary>Reads the first connected pad; returns Connected.</summary>
        public bool Update()
        {
            Connected = false; RightX = RightY = LeftTrigger = RightTrigger = 0; Buttons = 0;
            if (broken) return false;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    XState s;
                    int r = use910 ? GetState910(i, out s) : GetState14(i, out s);
                    if (r != 0) continue;
                    Connected = true;
                    Buttons = s.Buttons;
                    LeftTrigger = s.LeftTrigger / 255.0;
                    RightTrigger = s.RightTrigger / 255.0;
                    Stick(s.RX / 32767.0, s.RY / 32767.0, out RightX, out RightY);
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
            if (now - s.Start < FirstDelay || now - s.LastFire < Interval) return false;
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
