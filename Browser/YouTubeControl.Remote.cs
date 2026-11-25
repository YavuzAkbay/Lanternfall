// YouTubeControl.Remote.cs - what the phone remote (RemoteService.cs) can see and do on the YouTube screen. All of it runs on the UI
// thread. The phone card (Start menu > Phone remote) lives here too. C# 5 only.
using System;
using System.Collections.Generic;
using System.Windows;

namespace SeleneBrowser
{
    public partial class YouTubeControl
    {
        /// <summary>The YouTube screen the theme built (the phone remote talks to it).</summary>
        internal static YouTubeControl RemoteCurrent;

        internal bool RemoteShown { get { return shown; } }

        private readonly PhoneLinkCard linkCard = new PhoneLinkCard();

        private void InitRemote()
        {
            RemoteCurrent = this;
            linkCard.Closed += delegate { UpdateHints(); };
        }

        private void OpenLinkCard()
        {
            linkCard.Open();
            UpdateHints();
        }

        // a pad press while the card is up
        private bool HandleLinkCard(string b)
        {
            if (linkCard.HandlePad(b, limiter)) linkCard.Close();
            return true;
        }

        internal Dictionary<string, object> RemoteStatus()
        {
            var d = new Dictionary<string, object>();
            if (!playing || nowPlaying == null) { d["playing"] = false; return d; }
            d["playing"] = true;
            d["id"] = nowPlaying.Id;
            d["title"] = nowPlaying.Title ?? "";
            d["author"] = nowPlaying.Author ?? "";
            PState s = ps;
            d["paused"] = s != null && s.Paused;
            d["t"] = s == null ? 0 : Math.Round(s.T, 1);
            d["d"] = s == null ? 0 : Math.Round(s.D, 1);
            d["live"] = s != null && s.Live;
            return d;
        }

        /// <summary>toggle, back, forward, next, previous, stop. Null when done, otherwise a sentence for the phone.</summary>
        internal string RemoteControl(string command)
        {
            if (!playing) return "Nothing is playing on the TV.";
            PadActivity.Mark();
            switch (command)
            {
                case "toggle":
                    Js("toggle()");
                    if (ps != null) { ps.Paused = !ps.Paused; UpdatePlayerUi(); }
                    return null;
                case "back": SeekBy(-10); return null;
                case "forward": SeekBy(10); return null;
                case "next": if (queue != null && qi + 1 < queue.Count) PlayAt(qi + 1); else return "That was the last video."; return null;
                case "previous": if (qi > 0) PlayAt(qi - 1); else Js("seekBy(-100000)"); return null;
                case "stop": ExitPlayer(); UpdateHints(); return null;
            }
            return "Unknown command.";
        }
    }
}
