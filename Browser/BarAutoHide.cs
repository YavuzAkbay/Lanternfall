// BarAutoHide.cs - decides when the browser's address bar and hint strip step aside so the page gets the whole screen.
// Pure state machine: the owner supplies how to collapse / restore the bars, whether hiding is allowed right now, and how
// to read the page's scroll offset. Everything is debounced, so the page never jitters between the two layouts.
//   hides   : after the user scrolls down a little (wheel, edge scroll), when a page sits scrolled down, or after the cursor has
//             been roaming for a few seconds
//   restores: scrolling back up, reaching the top again, a new page, or any explicit request (Y, menu, address, settings)
using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace SeleneBrowser
{
    internal sealed class BarAutoHide
    {
        private const long Dwell = 700;              // ms the layout stays put after a change
        private const double DownToHide = 360;       // wheel units scrolled down before the bars go
        private const double UpToShow = 480;         // wheel units scrolled up before they return
        private const double FarDown = 1000;         // wheel units that hide the bars even when the page offset can't be read
        private const double ScrolledPx = 200;       // a page this far down counts as being browsed
        private const double MoveSeconds = 3;        // cursor roaming that counts as browsing

        private readonly Action<bool> apply;                 // true = collapse the bars
        private readonly Func<bool> canHide;                 // browsing a page (no bar / menu / keyboard / fullscreen)
        private readonly Action<Action<double>> readScrollY; // page scroll offset in px, NaN when unknown
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };

        private bool enabled = true;
        private bool hidden;
        private bool suppressed;      // restored on purpose: stays restored until the user scrolls again
        private bool probing;
        private double baseY = double.NaN, peakY;
        private double down, up, moved;
        private long changedAt, lastScrollAt, lastMoveAt;
        private string pageKey;

        public BarAutoHide(Action<bool> apply, Func<bool> canHide, Action<Action<double>> readScrollY)
        {
            this.apply = apply;
            this.canHide = canHide;
            this.readScrollY = readScrollY;
            changedAt = -Dwell;
            timer.Tick += delegate { try { Tick(); } catch (Exception) { probing = false; } };
        }

        public bool Hidden { get { return hidden; } }

        /// <summary>Off: the bars come back at once and stay.</summary>
        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (enabled == value) return;
                enabled = value;
                if (!enabled) Show(false);
            }
        }

        public void Start() { timer.Start(); }
        public void Stop() { timer.Stop(); }

        /// <summary>Restores the bars. suppress: keep them until the user scrolls again (an explicit request, a new page).</summary>
        public void Show(bool suppress)
        {
            down = up = moved = 0;
            peakY = 0;
            baseY = double.NaN;
            suppressed = suppress;
            if (!hidden) return;
            hidden = false;
            changedAt = clock.ElapsedMilliseconds;
            apply(false);
        }

        /// <summary>A navigation: a different page (not just another query or #fragment) starts at the top with the bars shown.</summary>
        public void NewPage(string url)
        {
            string key = KeyOf(url);
            if (key == pageKey) return;
            pageKey = key;
            Show(true);
        }

        private static string KeyOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int cut = url.IndexOfAny(new[] { '?', '#' });
            return cut >= 0 ? url.Substring(0, cut) : url;
        }

        /// <summary>The user scrolled the page by amount wheel units (dir +1 = down).</summary>
        public void Scrolled(int dir, int amount)
        {
            if (!enabled || !canHide()) return;
            long now = clock.ElapsedMilliseconds;
            if (now - lastScrollAt > 1500) { down = 0; up = 0; }
            lastScrollAt = now;
            if (dir > 0)
            {
                up = 0;
                down += amount;
                if (!hidden && down >= DownToHide) HideFromWheel(now);
            }
            else if (dir < 0)
            {
                down = 0;
                if (!hidden) return;
                up += amount;
                if (up >= UpToShow && Settled(now)) Show(true);
            }
        }

        /// <summary>The cursor was moved for dt seconds.</summary>
        public void Moved(double dt)
        {
            if (!enabled || hidden || !canHide()) return;
            long now = clock.ElapsedMilliseconds;
            if (now - lastMoveAt > 1500) moved = 0;
            lastMoveAt = now;
            moved += dt;
            if (moved >= MoveSeconds && Settled(now)) Hide(0);
        }

        private bool Settled(long now) { return now - changedAt >= Dwell; }

        private void Hide(double y)
        {
            if (hidden) return;
            hidden = true;
            suppressed = false;
            peakY = y;
            down = up = moved = 0;
            changedAt = clock.ElapsedMilliseconds;
            apply(true);
        }

        private void HideFromWheel(long now)
        {
            if (probing || !Settled(now)) return;
            probing = true;
            readScrollY(delegate(double y)
            {
                probing = false;
                if (hidden || !enabled || !canHide()) return;
                // a page that is not scrolled (short page, or an inner scroller we can't see) needs a longer push
                if ((!double.IsNaN(y) && y > 40) || down >= FarDown) Hide(double.IsNaN(y) ? 0 : y);
            });
        }

        // twice a second while the browser is shown: sees scrolling the wheel hooks can't (D-pad focus moves, scroll restoration)
        private void Tick()
        {
            if (!enabled || probing || !canHide()) return;
            probing = true;
            readScrollY(delegate(double y)
            {
                probing = false;
                if (!enabled || !canHide() || double.IsNaN(y)) return;
                long now = clock.ElapsedMilliseconds;
                if (hidden)
                {
                    if (y > peakY) peakY = y;
                    // back at the top of a page that was scrolled: the bars return (peakY guards pages whose offset reads 0)
                    if (peakY > ScrolledPx && y <= 2 && Settled(now)) Show(false);
                    return;
                }
                if (suppressed)
                {
                    if (double.IsNaN(baseY)) baseY = y;
                    if (y <= 2) { suppressed = false; baseY = double.NaN; }
                    else if (Math.Abs(y - baseY) > 300) suppressed = false;   // scrolled on its own since they were restored
                    else return;
                }
                if (y > ScrolledPx && Settled(now)) Hide(y);
            });
        }
    }
}
