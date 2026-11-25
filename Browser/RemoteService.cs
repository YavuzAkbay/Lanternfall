// RemoteService.cs - keeps the phone remote (PhoneRemote.cs) running inside Playnite and answers the phone for the TV: plays a video,
// opens a link, drives the player, searches YouTube. Everything the phone asks reaches the screen on the UI thread; the theme's
// Core plugin (Selene.PhoneCast, found by reflection like the other Browser hooks) opens the YouTube / Browser overlay and refuses
// while a game runs. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;

namespace SeleneBrowser
{
    internal static class RemoteService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object gate = new object();
        private static PhoneRemote remote;
        private static Timer watch;
        private static string lastError;

        /// <summary>A phone sent something that worked (raised on a worker thread): open cards close, the screen has moved on.</summary>
        public static event Action Sent;

        public static PhoneRemote Remote
        {
            get
            {
                lock (gate)
                {
                    if (remote == null)
                    {
                        string dir = SeleneBrowserPlugin.DataDir;
                        remote = new PhoneRemote(new RemoteHost(), string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "phone-link.json"));
                    }
                    return remote;
                }
            }
        }

        /// <summary>Why the link could not start (null when it runs or is off).</summary>
        public static string Error { get { lock (gate) return lastError; } }

        /// <summary>At startup: the link comes back when the user had switched it on.</summary>
        public static void AutoStart()
        {
            if (BrowserSettings.PhoneRemote) StartNow();
        }

        /// <summary>The user chose Phone remote on the TV: switch it on (remembered) and start listening.</summary>
        public static string Enable()
        {
            BrowserSettings.PhoneRemote = true;
            return StartNow();
        }

        public static void Disable()
        {
            BrowserSettings.PhoneRemote = false;
            lock (gate)
            {
                lastError = null;
                if (watch != null) { watch.Dispose(); watch = null; }
                if (remote != null) remote.Stop();
            }
        }

        /// <summary>Playnite is closing: the listener goes now (the setting stays as it was, so the link comes back next time).</summary>
        public static void Shutdown()
        {
            lock (gate)
            {
                if (watch != null) { watch.Dispose(); watch = null; }
                if (remote != null) remote.Stop();
            }
        }

        /// <summary>A new address: every phone that had the old one is cut off.</summary>
        public static void Reset()
        {
            Remote.Reset();
            lock (gate) lastError = Remote.Running ? null : lastError;
        }

        private static string StartNow()
        {
            string error;
            lock (gate)
            {
                PhoneRemote r = Remote;
                error = r.Running ? null : r.Start();
                lastError = error;
                // the PC's address changes (Wi-Fi back, a new lease) or the TV booted before the network: look again once a minute
                if (watch == null) watch = new Timer(delegate { Recheck(); }, null, 60000, 60000);
            }
            if (error != null) logger.Warn("SeleneBrowser: phone remote: " + error);
            PhoneFirewall.CheckAsync(null);
            return error;
        }

        private static void Recheck()
        {
            try
            {
                if (!BrowserSettings.PhoneRemote) return;
                lock (gate)
                {
                    PhoneRemote r = Remote;
                    IPAddress now = PhoneNet.PickLanAddress();
                    if (r.Running && now != null && r.BoundAddress != null && !r.BoundAddress.Equals(now)) r.Stop();
                    if (!r.Running) lastError = r.Start();
                    else lastError = null;
                }
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: phone remote recheck: " + ex.Message); }
        }

        internal static void RaiseSent()
        {
            var h = Sent;
            if (h != null) { try { h(); } catch { } }
        }
    }

    /// <summary>What the TV does for the phone.</summary>
    internal sealed class RemoteHost : IRemoteHost
    {
        private static MethodInfo cast;
        private static bool looked;

        private static T OnUi<T>(Func<T> call)
        {
            Application app = Application.Current;
            if (app == null) throw new InvalidOperationException("no UI");
            return (T)app.Dispatcher.Invoke(DispatcherPriority.Normal, TimeSpan.FromSeconds(8), call);
        }

        // Selene.PhoneCast.Receive(kind, value): null = done, otherwise a sentence for the phone
        private static string Core(string kind, string value)
        {
            if (!looked)
            {
                looked = true;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        Type t = a.GetType("Selene.PhoneCast", false);
                        MethodInfo m = t == null ? null : t.GetMethod("Receive", new[] { typeof(string), typeof(string) });
                        if (m != null) { cast = m; break; }
                    }
                    catch { }
                }
            }
            if (cast == null) return "The theme needs updating before the phone remote works (Lanternfall Core).";
            try { return cast.Invoke(null, new object[] { kind, value }) as string; }
            catch (TargetInvocationException ex) { return "The TV could not do that: " + (ex.InnerException ?? ex).Message; }
        }

        public string PlayVideo(string id)
        {
            string r = OnUi<string>(delegate { return Core("video", id); });
            if (r == null) RemoteService.RaiseSent();
            return r;
        }

        public string OpenUrl(string url)
        {
            string r = OnUi<string>(delegate { return Core("url", url); });
            if (r == null) RemoteService.RaiseSent();
            return r;
        }

        public string WebSearch(string words)
        {
            string url = BrowserControl.RemoteResolve(words);
            if (url == null) return "Nothing to search for.";
            return OpenUrl(url);
        }

        public string Pointer(string action, int dx, int dy)
        {
            return OnUi<string>(delegate
            {
                BrowserControl br = BrowserControl.RemoteCurrent;
                return br == null || !br.RemoteShown ? "The browser is not open on the TV." : br.RemotePointer(action, dx, dy);
            });
        }

        public string Control(string command)
        {
            return OnUi<string>(delegate
            {
                YouTubeControl yt = YouTubeControl.RemoteCurrent;
                return yt == null || !yt.RemoteShown ? "YouTube is not open on the TV." : yt.RemoteControl(command);
            });
        }

        public string Browse(string command)
        {
            return OnUi<string>(delegate
            {
                BrowserControl br = BrowserControl.RemoteCurrent;
                return br == null || !br.RemoteShown ? "The browser is not open on the TV." : br.RemoteBrowse(command);
            });
        }

        public Dictionary<string, object> Status()
        {
            return OnUi<Dictionary<string, object>>(delegate
            {
                YouTubeControl yt = YouTubeControl.RemoteCurrent;
                BrowserControl br = BrowserControl.RemoteCurrent;
                Dictionary<string, object> d;
                if (yt != null && yt.RemoteShown) { d = yt.RemoteStatus(); d["ctx"] = "youtube"; }
                else if (br != null && br.RemoteShown) { d = br.RemoteInfo(); d["ctx"] = "browser"; }
                else d = new Dictionary<string, object> { { "ctx", "home" } };
                return d;
            });
        }

        public List<Dictionary<string, object>> Search(string query, out string error)
        {
            error = null;
            PageResult<VideoItem> page;
            try
            {
                var task = YouTubeApi.Search(query, null);
                if (!task.Wait(20000)) { error = "YouTube took too long. Try again."; return null; }
                page = task.Result;
            }
            catch { error = "The search did not work. Try again."; return null; }
            if (page == null || !page.Ok) { error = page != null && !string.IsNullOrEmpty(page.Error) ? page.Error : "The search did not work."; return null; }
            var list = new List<Dictionary<string, object>>();
            foreach (VideoItem v in page.Items)
            {
                if (v == null || string.IsNullOrEmpty(v.Id) || v.Id.Length != 11 || v.IsShort) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "id", v.Id }, { "title", v.Title ?? "" }, { "author", v.Author ?? "" }, { "duration", v.IsLive ? "LIVE" : (v.DurationText ?? "") },
                    { "thumb", "https://i.ytimg.com/vi/" + v.Id + "/mqdefault.jpg" }
                });
                if (list.Count >= 12) break;
            }
            return list;
        }
    }
}
