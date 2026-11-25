// PhoneBridge.cs - the phone keyboard for Lanternfall Core's own pad keyboards (Find a game, IGDB search, Add feed).
// Core cannot reference this add-on, so it finds this class by reflection (the same pattern as OpenInThemeBrowser) and talks to it
// with plain strings and System delegates only. The keyboard rides on the one phone link (PhoneRemote.cs): the phone that is
// already paired just gets a typing box on its page. The settings (phone keyboard on / passwords) and the firewall check stay
// here. Callbacks arrive on worker threads: the caller must hop to its UI thread. C# 5 only.
using System;

namespace SeleneBrowser
{
    /// <summary>One phone typing session for a caller in another assembly.</summary>
    public sealed class PhoneHandle
    {
        internal RemoteTyping Typing;
        private bool connected;

        /// <summary>The address the QR code holds; null when the phone keyboard is unavailable (then Message says why).</summary>
        public string Url { get; internal set; }
        public string Message { get; internal set; }

        /// <summary>A reason the phone may not get through (Windows Firewall), or null. Fills in a moment after Start.</summary>
        public string Hint
        {
            get
            {
                if (connected || Typing == null) return null;
                FirewallState s = PhoneFirewall.Last;
                if (s == FirewallState.Missing || s == FirewallState.Blocked)
                    return "Windows Firewall may stop your phone from connecting. Set it up once in the Browser's Settings (needs a mouse or keyboard).";
                return null;
            }
        }

        internal void MarkConnected() { connected = true; }

        /// <summary>The pad changed the text: the phone's box follows.</summary>
        public void SetText(string text)
        {
            RemoteTyping t = Typing;
            if (t != null) t.SetText(text);
        }

        public void Close()
        {
            RemoteTyping t = Typing;
            Typing = null;
            if (t != null) t.Close();
        }
    }

    public static class PhoneBridge
    {
        /// <summary>Starts a session. Never null; check Url. text(t) = the whole field after the phone typed; done / cancelled = the
        /// phone's buttons; connected = the phone is on the page. accent = "#rrggbb" or null.</summary>
        public static PhoneHandle Start(string title, string initial, bool multiline, bool secret, string accent,
            Action<string> text, Action done, Action cancelled, Action connected)
        {
            var handle = new PhoneHandle();
            if (!BrowserSettings.PhoneKeyboard)
            {
                handle.Message = "The phone keyboard is off. Turn it on in the Browser's Settings (Start menu > Settings).";
                return handle;
            }
            if (secret && !BrowserSettings.PhonePasswords)
            {
                handle.Message = "Type passwords here on the TV. You can allow the phone for passwords in the Browser's Settings.";
                return handle;
            }
            string error = RemoteService.Enable();   // asking for the phone keyboard is the user's yes: the phone link switches on
            PhoneRemote r = RemoteService.Remote;
            if (error != null || r.Url == null) { handle.Message = error ?? "The phone keyboard could not start."; return handle; }
            RemoteTyping t = r.BeginTyping(title, initial, multiline, secret, accent);
            handle.Typing = t;
            handle.Url = r.Url;
            if (text != null) t.TextFromPhone += delegate(string s) { text(s); };
            if (done != null) t.Done += delegate { done(); };
            if (cancelled != null) t.Cancelled += delegate { cancelled(); };
            Action arrived = delegate { handle.MarkConnected(); if (connected != null) connected(); };
            r.PhoneConnected += arrived;
            t.Closed += delegate { r.PhoneConnected -= arrived; };
            PhoneFirewall.CheckAsync(null);
            if (r.PhoneHere) arrived();
            return handle;
        }
    }
}
