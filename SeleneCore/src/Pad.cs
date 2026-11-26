using System;
using System.Runtime.InteropServices;

namespace Selene
{
    // The first connected controller's sticks and triggers, read directly (XInput) for things that follow them
    // continuously (framing a picture), where Playnite's repeated button presses would move in steps.
    public static class Pad
    {
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern int GetState14(int user, byte[] state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] private static extern int GetState910(int user, byte[] state);

        private static Func<int, byte[], int> getState;
        private const double StickDeadzone = 0.24, TriggerDeadzone = 0.12;

        public struct State
        {
            public bool Connected;
            public double LeftX, LeftY;      // -1..1 (deadzone removed), up = positive
            public double RightX, RightY;    // the same for the right stick
            public double LeftTrigger, RightTrigger;   // 0..1
        }

        // read every rendered frame by the things that follow a stick; all on the UI thread, so one buffer is enough.
        // With no pad in any slot the four calls are not repeated every frame (an empty slot is slow): they are tried
        // again once a second, and a pad that was found is asked first.
        private static readonly byte[] raw = new byte[16];
        private static int lastUser;
        private static int nextScan;
        private static bool waiting;

        public static State Read()
        {
            if (HomeNav.Isolated) return new State();
            if (getState == null) getState = Find();
            int now = Environment.TickCount;
            if (waiting && unchecked(now - nextScan) < 0) return new State();   // (signed difference: safe when the tick count wraps)
            for (int i = 0; i < 4; i++)
            {
                int user = (lastUser + i) % 4;
                if (getState(user, raw) != 0) continue;
                lastUser = user;
                waiting = false;
                return new State
                {
                    Connected = true,
                    LeftTrigger = Trigger(raw[6]), RightTrigger = Trigger(raw[7]),
                    LeftX = Stick(BitConverter.ToInt16(raw, 8)), LeftY = Stick(BitConverter.ToInt16(raw, 10)),
                    RightX = Stick(BitConverter.ToInt16(raw, 12)), RightY = Stick(BitConverter.ToInt16(raw, 14))
                };
            }
            nextScan = unchecked(now + 1000);
            waiting = true;
            return new State();
        }

        private static double Stick(short v)
        {
            double n = Math.Max(-1, v / 32767.0);
            double m = Math.Abs(n);
            return m < StickDeadzone ? 0 : Math.Sign(n) * (m - StickDeadzone) / (1 - StickDeadzone);
        }

        private static double Trigger(byte v)
        {
            double n = v / 255.0;
            return n < TriggerDeadzone ? 0 : (n - TriggerDeadzone) / (1 - TriggerDeadzone);
        }

        private static Func<int, byte[], int> Find()
        {
            var candidates = new Func<int, byte[], int>[] { GetState14, GetState910 };
            foreach (var candidate in candidates)
            {
                try { candidate(0, new byte[16]); return candidate; }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
            return (user, state) => 1167;  // ERROR_DEVICE_NOT_CONNECTED
        }
    }
}
