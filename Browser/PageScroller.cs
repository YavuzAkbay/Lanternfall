// PageScroller.cs - turns the left stick, the triggers and the cursor pushed against an edge into one smooth page scroll.
// The pad is read here (every tick); the scrolling itself happens inside the page (the shim's scroll(): one small step per painted frame,
// never past an end), so it is as smooth as the page can paint, nothing is sent to the browser when there is no room left, and it stops
// by itself if this side goes quiet. What goes over to the page is only a speed, a few times a second. No WPF here, so
// tools\tests\BrowserTools.Check.cs can compile it alone. C# 5 only.
using System;
using System.Globalization;

namespace SeleneBrowser
{
    internal sealed class PageScroller
    {
        /// <summary>Viewport heights per second with the stick pushed all the way.</summary>
        public const double MaxSpeed = 1.35;
        // seconds the speed takes to follow the stick (about three ticks of ease in, and a short glide out)
        private const double Smooth = 0.07;
        private const long ChangeMs = 30, BeatMs = 150;

        private readonly Action<string> run;
        private double vx, vy, sentX, sentY;
        private long sentAt;
        private bool active;
        private string sentAnchor = "";

        /// <param name="run">Runs a script in the page in front.</param>
        public PageScroller(Action<string> run) { this.run = run; }

        /// <summary>A scroll is going on (or gliding to a stop).</summary>
        public bool Active { get { return active; } }
        public double SpeedY { get { return vy; } }

        /// <summary>-1..1 to -1..1: gentle near the middle, so slow scrolling is easy, full speed at the edge.</summary>
        public static double Curve(double v)
        {
            double a = Math.Pow(Math.Min(1, Math.Abs(v)), 1.7);
            return v < 0 ? -a : a;
        }

        /// <summary>0..1 trigger to 0..1 (a small dead zone, then a gentle curve).</summary>
        public static double Trigger(double t)
        {
            if (t < 0.08) return 0;
            return Math.Pow(Math.Min(1, (t - 0.08) / 0.92), 1.3);
        }

        /// <summary>
        /// The speed the pad asks for, in viewport heights per second (+ = down / right). lx, ly: the left stick after its dead zone
        /// (ly positive when pushed up); up, down: the trigger depths 0..1. An axis that is clearly not the push direction is dropped,
        /// so a stick pushed mostly down does not drift sideways.
        /// </summary>
        public static void Target(double lx, double ly, double up, double down, out double tx, out double ty)
        {
            double ax = Math.Abs(lx), ay = Math.Abs(ly);
            if (ax < ay * 0.4) lx = 0; else if (ay < ax * 0.4) ly = 0;
            tx = Curve(lx) * MaxSpeed;
            ty = Curve(-ly) * MaxSpeed + (Trigger(down) - Trigger(up)) * MaxSpeed * 0.9;
            ty = Math.Max(-MaxSpeed, Math.Min(MaxSpeed, ty));
        }

        /// <summary>
        /// One tick. tx / ty: the wanted speed; anchor: where to scroll as "x,y" in the page's CSS pixels, or "" for the highlighted
        /// element (else the middle of the page); now: a clock in ms. Returns true while the page is being scrolled.
        /// </summary>
        public bool Update(double dt, double tx, double ty, string anchor, long now)
        {
            double k = 1 - Math.Exp(-Math.Max(0.001, dt) / Smooth);
            vx += (tx - vx) * k;
            vy += (ty - vy) * k;
            if (tx == 0 && Math.Abs(vx) < 0.01) vx = 0;
            if (ty == 0 && Math.Abs(vy) < 0.01) vy = 0;
            anchor = anchor ?? "";
            if (vx == 0 && vy == 0)
            {
                if (active) { Send(0, 0, "", now); active = false; }
                return false;
            }
            bool changed = Math.Abs(vx - sentX) > 0.015 || Math.Abs(vy - sentY) > 0.015 || anchor != sentAnchor;
            if (!active || (changed && now - sentAt >= ChangeMs) || now - sentAt >= BeatMs) Send(vx, vy, anchor, now);
            active = true;
            return true;
        }

        /// <summary>Stops at once (a menu opened, the page changed, the browser was hidden).</summary>
        public void Stop()
        {
            vx = vy = 0;
            if (active) { Send(0, 0, "", Environment.TickCount); active = false; }
        }

        private void Send(double x, double y, string anchor, long now)
        {
            sentX = x; sentY = y; sentAt = now; sentAnchor = anchor;
            string call = "window.__nomadNav&&window.__nomadNav.scroll&&window.__nomadNav.scroll(" + F(x) + "," + F(y) + (anchor.Length > 0 ? "," + anchor : "") + ")";
            try { run(call); } catch (Exception) { }
        }

        private static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    }
}
