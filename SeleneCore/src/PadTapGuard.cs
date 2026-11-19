using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK.Events;

namespace Selene
{
    // Playnite looks at the controller about every 16 ms (a thread that polls SDL, compares with the last look and sends a press
    // or a release only when the state differs). A tap that starts and ends between two looks - quick mashing, or a Bluetooth pad
    // that hands over a press and its release together - changes nothing the next look can see, so Playnite never hears of it.
    // This watches the pad (XInput) every 2 ms, and when a press has not reached Playnite's input a moment later and the button
    // is already up again, it hands that press to Playnite's own button handling, exactly as if Playnite had seen it.
    // Switch: "tapguard".
    public static class PadTapGuard
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern int GetState(int user, out XState state);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private const int PollMs = 2;
        private const int GraceMs = 70;        // a press Playnite has not delivered this long after it began (it looks every ~16 ms)
        private const int ForgetMs = 1500;     // a press this old is not replayed (the moment has passed)

        private sealed class Lane
        {
            public readonly ushort Bit;
            public readonly string Name;
            public readonly Queue<long> Edges = new Queue<long>();     // presses seen here that Playnite has not (yet) delivered
            public readonly Queue<long> Credits = new Queue<long>();   // deliveries that came before this watcher noticed the press
            public long Posted;                                        // the press a replay is already queued for
            public int Own;                                            // replays whose arrival in the input is still to come
            public long OwnAt;
            public Lane(ushort bit, string name) { Bit = bit; Name = name; }
        }

        // Playnite's own names (ControllerInput); sticks and triggers are left to Playnite (a press there has no "tap")
        private static readonly Lane[] Lanes =
        {
            new Lane(0x1000, "A"), new Lane(0x2000, "B"), new Lane(0x4000, "X"), new Lane(0x8000, "Y"),
            new Lane(0x0001, "DPadUp"), new Lane(0x0002, "DPadDown"), new Lane(0x0004, "DPadLeft"), new Lane(0x0008, "DPadRight"),
            new Lane(0x0100, "LeftShoulder"), new Lane(0x0200, "RightShoulder"), new Lane(0x0010, "Start"), new Lane(0x0020, "Back")
        };

        private static readonly object gate = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static Dictionary<string, Lane> byName;
        private static Dispatcher ui;
        private static volatile ushort mask;      // the buttons down at the last look
        private static bool started, broken;
        private static int rescued;

        public static void Start()
        {
            if (started || HomeNav.Isolated || Application.Current == null) return;
            started = true;
            ui = Application.Current.Dispatcher;
            byName = new Dictionary<string, Lane>();
            foreach (var lane in Lanes) byName[lane.Name] = lane;
            var thread = new Thread(Watch) { IsBackground = true, Name = "Selene pad tap guard", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        private static PropertyInfo buttonProperty;

        // Called for every input event, first of all: a pad press Playnite delivers is counted (before anything can cancel it).
        public static void Seen(System.Windows.Input.KeyEventArgs ke)
        {
            if (!started || ke == null || ke.RoutedEvent != System.Windows.Input.Keyboard.KeyDownEvent) return;
            var type = ke.GetType();
            if (type.Name != "GameControllerInputEventArgs") return;
            if (buttonProperty == null || buttonProperty.DeclaringType != type) buttonProperty = type.GetProperty("Button");
            var button = buttonProperty != null ? buttonProperty.GetValue(ke, null) : null;
            if (button != null) Arrived(button.ToString());
        }

        private static void Arrived(string button)
        {
            if (!started || button == null) return;
            Lane lane;
            if (!byName.TryGetValue(button, out lane)) return;
            long now = clock.ElapsedMilliseconds;
            lock (gate)
            {
                if (lane.Own > 0 && now - lane.OwnAt < 1000) { lane.Own--; return; }   // our own replay arriving
                lane.Own = 0;
                if (lane.Edges.Count > 0) lane.Edges.Dequeue();
                else lane.Credits.Enqueue(now);   // delivered before the watcher noticed the press: it cancels that press when it is noticed
            }
        }

        private static void Watch()
        {
            ushort prev = 0;
            int slot = -1;
            long nextFront = 0, nextScan = 0;
            bool active = false, period = false;
            while (true)
            {
                long now = clock.ElapsedMilliseconds;
                if (now >= nextFront)
                {
                    nextFront = now + 250;
                    try { active = !broken && Features.On("tapguard") && HomeNav.PlayniteInFront(); } catch { active = false; }
                    if (active != period)
                    {
                        period = active;
                        try { if (active) timeBeginPeriod(1); else timeEndPeriod(1); } catch { }
                    }
                }
                if (!active)
                {
                    if (prev != 0 || mask != 0) { prev = 0; mask = 0; Forget(); }
                    Thread.Sleep(150);
                    continue;
                }
                ushort now_mask = Read(ref slot, now, ref nextScan);
                if (slot < 0 && now_mask == 0 && prev != 0) Forget();
                ushort down = (ushort)(now_mask & ~prev);
                prev = now_mask;
                mask = now_mask;
                foreach (var lane in Lanes)
                {
                    bool pressedNow = (down & lane.Bit) != 0;
                    bool held = (now_mask & lane.Bit) != 0;
                    long due = 0;
                    lock (gate)
                    {
                        if (pressedNow)
                        {
                            while (lane.Credits.Count > 0 && now - lane.Credits.Peek() > 60) lane.Credits.Dequeue();
                            if (lane.Credits.Count > 0) lane.Credits.Dequeue();   // Playnite already has this one
                            else lane.Edges.Enqueue(now);
                        }
                        while (lane.Edges.Count > 0 && now - lane.Edges.Peek() > ForgetMs) lane.Edges.Dequeue();
                        if (lane.Edges.Count > 0)
                        {
                            long oldest = lane.Edges.Peek();
                            // only once the button is up again: while it is down Playnite's next look still sees it
                            if (now - oldest >= GraceMs && !held && lane.Posted != oldest) { lane.Posted = oldest; due = oldest; }
                        }
                    }
                    if (due != 0)
                    {
                        var l = lane; long stamp = due;
                        try { ui.BeginInvoke(DispatcherPriority.Input, new Action(() => Replay(l, stamp))); } catch { }
                    }
                }
                Thread.Sleep(PollMs);
            }
        }

        private static void Forget()
        {
            lock (gate) foreach (var lane in Lanes) { lane.Edges.Clear(); lane.Credits.Clear(); lane.Posted = 0; }
        }

        private static ushort Read(ref int slot, long now, ref long nextScan)
        {
            try
            {
                XState s;
                if (slot >= 0)
                {
                    if (GetState(slot, out s) == 0) return s.Buttons;
                    slot = -1;
                }
                if (now < nextScan) return 0;   // (asking an empty slot is slow: not more than once a second)
                nextScan = now + 1000;
                for (int i = 0; i < 4; i++) if (GetState(i, out s) == 0) { slot = i; return s.Buttons; }
            }
            catch (DllNotFoundException) { broken = true; }
            catch (EntryPointNotFoundException) { broken = true; }
            return 0;
        }

        // On the UI thread, at a priority below Playnite's own deliveries: anything it did send is already counted by now.
        private static void Replay(Lane lane, long stamp)
        {
            try
            {
                lock (gate)
                {
                    if (lane.Edges.Count == 0 || lane.Edges.Peek() != stamp) { lane.Posted = 0; return; }
                    if ((mask & lane.Bit) != 0) { lane.Posted = 0; return; }   // pressed again since: it is settled when let go
                    lane.Edges.Dequeue();
                    lane.Posted = 0;
                    lane.Own++;
                    lane.OwnAt = clock.ElapsedMilliseconds;
                }
                if (!Press(lane.Name))
                    lock (gate) { if (lane.Own > 0) lane.Own--; }
                else
                {
                    rescued++;
                    HomeNav.Log("pad: caught a quick " + lane.Name + " Playnite missed (" + rescued + " so far)");
                }
            }
            catch (Exception ex) { HomeNav.Log("pad tap guard: " + (ex.InnerException ?? ex).Message); broken = true; }
        }

        // The press and its release through Playnite's own button handling (the same call its look at the pad makes), so the
        // events, the simulated keys and the held-button repeat all behave as for a press it saw itself.
        private static MethodInfo process;
        private static FieldInfo controllers;
        private static PropertyInfo managerProperty;

        private static bool Press(string name)
        {
            var app = Application.Current;
            if (app == null) return false;
            if (managerProperty == null)
                for (var t = app.GetType(); t != null && managerProperty == null; t = t.BaseType)
                    managerProperty = t.GetProperty("GameController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var manager = managerProperty != null ? managerProperty.GetValue(app, null) : null;
            if (manager == null) return false;
            if (process == null) process = manager.GetType().GetMethod("ProcessButtonState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (controllers == null) controllers = manager.GetType().GetField("Controllers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (process == null || controllers == null) { broken = true; HomeNav.Log("pad tap guard: Playnite's controller handling not found, off"); return false; }
            ControllerInput button;
            if (!Enum.TryParse(name, out button)) return false;
            object controller = null;
            var list = controllers.GetValue(manager) as IEnumerable;
            if (list != null)
                foreach (var c in list)
                {
                    var pad = c as IGamepad;
                    if (pad != null && pad.Enabled) { controller = c; break; }
                }
            if (controller == null) return false;
            // both at once: Playnite's own next look then finds the button as it left it (up) and sends nothing more
            process.Invoke(manager, new object[] { (byte)1, button, controller });
            process.Invoke(manager, new object[] { (byte)0, button, controller });
            return true;
        }
    }
}
