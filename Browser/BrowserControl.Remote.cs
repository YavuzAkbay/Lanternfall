// BrowserControl.Remote.cs - what the phone link (RemoteService.cs) can see and do in the browser. All of it runs on the UI thread.
// C# 5 only.
using System;
using System.Collections.Generic;

namespace SeleneBrowser
{
    public partial class BrowserControl
    {
        /// <summary>The browser the theme built (the phone link talks to it).</summary>
        internal static BrowserControl RemoteCurrent;

        internal bool RemoteShown { get { return shown; } }

        /// <summary>The address or search words, turned into an address (a web search when it does not look like one).</summary>
        internal static string RemoteResolve(string text) { return ResolveInput(text); }

        internal Dictionary<string, object> RemoteInfo()
        {
            var d = new Dictionary<string, object>();
            string url = "";
            bool back = false;
            try
            {
                if (view != null && view.IsReady && !IsHome) { url = view.Address ?? ""; back = view.CanGoBack; }
            }
            catch { }
            d["tab"] = active == null || string.IsNullOrEmpty(active.Title) ? "" : active.Title;
            d["url"] = url;
            d["canBack"] = back;
            return d;
        }

        /// <summary>The phone's trackpad: move (dx, dy in phone pixels, already scaled), scroll (dy), click. The cursor is the one the
        /// right stick drives. Null when done, otherwise a sentence for the phone.</summary>
        internal string RemotePointer(string action, int dx, int dy)
        {
            if (view == null || !view.IsReady) return "The page is not ready yet.";
            if (keyboard.IsOpen || linkCard.IsOpen || settings.IsOpen || panelKind != PanelKind.None || mode == Mode.Menu || mode == Mode.Bar)
                return "Close the menu on the TV first.";
            PadActivity.Mark();
            WakePointer();
            if (mode == Mode.Focus) EnterCursor();
            double k = CssPerDesign();
            switch (action)
            {
                case "move":
                    phoneMovedCursor = true;
                    cursor = new System.Windows.Point(
                        Math.Max(0, Math.Min(page.ActualWidth - 1, cursor.X + dx)),
                        Math.Max(0, Math.Min(page.ActualHeight - 1, cursor.Y + dy)));
                    PlaceCursor();
                    hoverDirty = true;
                    ProbeHover(clock.ElapsedMilliseconds);
                    view.MoveMouse(cursor.X * k, cursor.Y * k);
                    return null;
                case "scroll":
                    if (dy != 0) { BarsScrolled(dy < 0 ? 1 : -1, Math.Abs(dy)); view.Wheel(cursor.X * k, cursor.Y * k, dy); }
                    return null;
                case "click":
                    ClickAt(cursor.X * k, cursor.Y * k);
                    return null;
            }
            return "Unknown action.";
        }

        /// <summary>back, forward, reload, home, up, down, zoomin, zoomout, newtab, closetab. Null when done, otherwise a sentence for the phone.</summary>
        internal string RemoteBrowse(string command)
        {
            PadActivity.Mark();
            if (view == null) return "The page is not ready yet.";
            switch (command)
            {
                case "back": if (!view.CanGoBack) return "There is nothing to go back to."; view.GoBack(); return null;
                case "forward": view.GoForward(); return null;
                case "reload": view.Reload(); return null;
                case "home": GoHome(); return null;
                case "up": ScrollPage(-1); ScrollPage(-1); return null;
                case "down": ScrollPage(1); ScrollPage(1); return null;
                case "zoomin": ZoomBy(ZoomStep); return null;
                case "zoomout": ZoomBy(-ZoomStep); return null;
                case "newtab": ActivateTab(NewTab(null, true)); return null;
                case "closetab": CloseTab(active); return null;
            }
            return "Unknown command.";
        }
    }
}
