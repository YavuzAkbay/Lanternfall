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
            if (BrowserSettings.PhoneRemote) System.Threading.Tasks.Task.Run(delegate { StartNow(); });   // the firewall step can take a moment
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
            if (!Remote.Running) PhoneFirewall.EnsureQuietly();   // before the first listen, or Windows shows its Allow box
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
                IPAddress now = PhoneNet.PickLanAddress();   // adapter enumeration, kept outside the lock
                lock (gate)
                {
                    // Disable() may have run while the address was looked up: then the link must stay off
                    if (!BrowserSettings.PhoneRemote) return;
                    PhoneRemote r = Remote;
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
        private static Type castType;
        private static bool looked;
        private static readonly Dictionary<string, MethodInfo> castMethods = new Dictionary<string, MethodInfo>();
        private const string Outdated = "The theme needs updating before the phone remote works (Lanternfall Core).";

        private static T OnUi<T>(Func<T> call)
        {
            Application app = Application.Current;
            if (app == null) throw new InvalidOperationException("no UI");
            // Dispatcher.Invoke gives null when the 8 seconds run out, which a string result would read as "done, no problem": check
            // that the call really finished and refuse otherwise (the phone is told it did not work)
            bool finished = false;
            object result = app.Dispatcher.Invoke(DispatcherPriority.Normal, TimeSpan.FromSeconds(8), new Func<object>(delegate
            {
                T value = call();
                finished = true;
                return value;
            }));
            if (!finished) throw new TimeoutException("The TV did not answer in time");
            return (T)result;
        }

        // Selene.PhoneCast.Receive(kind, value): null = done, otherwise a sentence for the phone
        private static string Core(string kind, string value)
        {
            Look();
            if (cast == null) return Outdated;
            try { return cast.Invoke(null, new object[] { kind, value }) as string; }
            catch (TargetInvocationException ex) { return "The TV could not do that: " + (ex.InnerException ?? ex).Message; }
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType("Selene.PhoneCast", false);
                    MethodInfo m = t == null ? null : t.GetMethod("Receive", new[] { typeof(string), typeof(string) });
                    if (m != null) { cast = m; castType = t; break; }
                }
                catch { }
            }
        }

        // a newer PhoneCast method (Pad, State, Games, CoverPath, Launch); null when the installed Core is older than this add-on
        private static MethodInfo CoreMethod(string name, Type[] args)
        {
            Look();
            if (castType == null) return null;
            MethodInfo m;
            lock (castMethods)
            {
                if (!castMethods.TryGetValue(name, out m)) { m = castType.GetMethod(name, args); castMethods[name] = m; }
            }
            return m;
        }

        private static object CoreCall(string name, Type[] args, object[] values)
        {
            MethodInfo m = CoreMethod(name, args);
            if (m == null) return null;
            try { return m.Invoke(null, values); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static bool CoreHas(string name, Type[] args) { return CoreMethod(name, args) != null; }

        private static readonly Type[] OneString = { typeof(string) };

        public string Pad(string button)
        {
            if (!CoreHas("Pad", OneString)) return Outdated;
            return OnUi<string>(delegate { return CoreCall("Pad", OneString, new object[] { button }) as string; });
        }

        public string Launch(string id)
        {
            if (!CoreHas("Launch", OneString)) return Outdated;
            string r = OnUi<string>(delegate { return CoreCall("Launch", OneString, new object[] { id }) as string; });
            if (r == null) RemoteService.RaiseSent();
            return r;
        }

        public List<Dictionary<string, object>> Games(out string error)
        {
            error = null;
            if (!CoreHas("Games", Type.EmptyTypes)) { error = Outdated; return null; }
            var list = OnUi<List<Dictionary<string, object>>>(delegate { return CoreCall("Games", Type.EmptyTypes, new object[0]) as List<Dictionary<string, object>>; });
            if (list == null) error = "The TV could not list the games.";
            return list;
        }

        // covers are shrunk to phone size once and kept in memory (a big library over Wi-Fi stays quick)
        private static readonly Dictionary<string, byte[]> covers = new Dictionary<string, byte[]>();
        private static readonly Queue<string> coverOrder = new Queue<string>();
        private static readonly SemaphoreSlim shrinking = new SemaphoreSlim(3);

        public byte[] Cover(string id)
        {
            if (!CoreHas("CoverPath", OneString)) return null;
            string path = OnUi<string>(delegate { return CoreCall("CoverPath", OneString, new object[] { id }) as string; });
            if (string.IsNullOrEmpty(path)) return null;
            string key;
            try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture); }
            catch { return null; }
            lock (covers) { byte[] hit; if (covers.TryGetValue(key, out hit)) return hit; }
            byte[] jpg;
            if (!shrinking.Wait(10000)) return null;
            try { jpg = Shrink(path, 300); }
            finally { shrinking.Release(); }
            if (jpg == null) return null;
            lock (covers)
            {
                if (!covers.ContainsKey(key))
                {
                    covers[key] = jpg;
                    coverOrder.Enqueue(key);
                    while (coverOrder.Count > 600) covers.Remove(coverOrder.Dequeue());
                }
            }
            return jpg;
        }

        private static byte[] Shrink(string path, int width)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > 40L * 1024 * 1024) return null;
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                img.UriSource = new Uri(path);
                img.DecodePixelWidth = width;
                img.EndInit();
                img.Freeze();
                var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 82 };
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    return ms.ToArray();
                }
            }
            catch { return null; }
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
                // the theme's own screen (home, Collection, a menu, a game running) and its accent colour
                try
                {
                    var core = CoreCall("State", Type.EmptyTypes, new object[0]) as Dictionary<string, object>;
                    if (core != null) foreach (var kv in core) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
                }
                catch { }
                d["pad"] = CoreHas("Pad", OneString);
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
