// BrowserSettings.cs - the browser's own persisted settings (DataDir\settings.json).
// Tolerant by design: a missing, empty or corrupt file just means the defaults.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Playnite.SDK;

namespace SeleneBrowser
{
    internal static class BrowserSettings
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object gate = new object();
        private static bool loaded;
        private static bool adBlock = true;
        private static bool autoHide = true;
        private static bool startedWithAdBlock = true;
        private static bool phoneKeyboard = true;
        private static bool phoneRemote;   // the phone remote (a persistent link for sending videos and links); off until the user opens it on the TV
        private static bool phonePasswords;   // off: a password is typed on the TV (the phone page is plain HTTP on the home network)
        private static double pageScale;   // 0 = automatic (follows the screen), otherwise a fixed page scale
        private static double pageBoost = 1;   // Auto only: a multiplier on the automatic size
        private static bool downloadsAllowed = true;   // a download still asks first; off: they are refused
        private static readonly Dictionary<string, double> siteZoom = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);   // site -> zoom level
        private static readonly HashSet<string> mobileSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // sites shown as on a phone

        /// <summary>Raised on the calling thread after a setting changed (and was saved).</summary>
        public static event Action Changed;

        private static string FilePath
        {
            get
            {
                string dir = SeleneBrowserPlugin.DataDir;
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "settings.json");
            }
        }

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (gate)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    string file = FilePath;
                    if (file != null && File.Exists(file))
                    {
                        var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                        if (d != null)
                        {
                            adBlock = ReadBool(d, "AdBlockEnabled", true);
                            autoHide = ReadBool(d, "AutoHideBars", true);
                            phoneKeyboard = ReadBool(d, "PhoneKeyboard", true);
                            phonePasswords = ReadBool(d, "PhonePasswords", false);
                            phoneRemote = ReadBool(d, "PhoneRemote", false);
                            object ps;
                            if (d.TryGetValue("PageScale", out ps) && (ps is int || ps is double || ps is decimal))
                                pageScale = Math.Max(0, Math.Min(4, Convert.ToDouble(ps, System.Globalization.CultureInfo.InvariantCulture)));
                            object pb;
                            if (d.TryGetValue("PageBoost", out pb) && (pb is int || pb is double || pb is decimal))
                                pageBoost = Math.Max(0.5, Math.Min(3, Convert.ToDouble(pb, System.Globalization.CultureInfo.InvariantCulture)));
                            downloadsAllowed = ReadBool(d, "DownloadsAllowed", true);
                            var zooms = d.ContainsKey("SiteZoom") ? d["SiteZoom"] as Dictionary<string, object> : null;
                            if (zooms != null)
                                foreach (var kv in zooms)
                                    if (kv.Value is int || kv.Value is double || kv.Value is decimal)
                                        siteZoom[kv.Key] = Math.Max(-3, Math.Min(5, Convert.ToDouble(kv.Value, System.Globalization.CultureInfo.InvariantCulture)));
                            var mobiles = d.ContainsKey("MobileSites") ? d["MobileSites"] as System.Collections.IEnumerable : null;
                            if (mobiles != null && !(d["MobileSites"] is string))
                                foreach (object o in mobiles) { var site = o as string; if (!string.IsNullOrEmpty(site)) mobileSites.Add(site); }
                        }
                    }
                }
                catch (Exception ex) { logger.Warn("SeleneBrowser: settings.json unreadable, using defaults: " + ex.Message); }
                startedWithAdBlock = adBlock;
            }
        }

        private static bool ReadBool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v) || !(v is bool)) return fallback;
            return (bool)v;
        }

        /// <summary>Block known ad/tracker hosts (and, from the next launch, load uBlock Origin Lite). Default on.</summary>
        public static bool AdBlockEnabled
        {
            get { EnsureLoaded(); return adBlock; }
            set { EnsureLoaded(); if (adBlock == value) return; adBlock = value; Saved(); }
        }

        /// <summary>The value AdBlockEnabled had when this process started: the uBlock extension follows that one.</summary>
        public static bool AdBlockAtStartup
        {
            get { EnsureLoaded(); return startedWithAdBlock; }
        }

        /// <summary>View on the on-screen keyboard shows a QR code to type on a phone. Default on (nothing listens until it is pressed).</summary>
        public static bool PhoneKeyboard
        {
            get { EnsureLoaded(); return phoneKeyboard; }
            set { EnsureLoaded(); if (phoneKeyboard == value) return; phoneKeyboard = value; Saved(); }
        }

        /// <summary>The phone remote is on: a phone that scanned its QR can send YouTube links, web links and searches to the TV. Default off; Start menu > Phone remote switches it on.</summary>
        public static bool PhoneRemote
        {
            get { EnsureLoaded(); return phoneRemote; }
            set { EnsureLoaded(); if (phoneRemote == value) return; phoneRemote = value; Saved(); }
        }

        /// <summary>Allow the phone keyboard for password fields. Default off: the phone page is plain HTTP on the home network.</summary>
        public static bool PhonePasswords
        {
            get { EnsureLoaded(); return phonePasswords; }
            set { EnsureLoaded(); if (phonePasswords == value) return; phonePasswords = value; Saved(); }
        }

        /// <summary>Files a page offers are saved (after a question on screen). Off: they are refused. Default on.</summary>
        public static bool DownloadsAllowed
        {
            get { EnsureLoaded(); return downloadsAllowed; }
            set { EnsureLoaded(); if (downloadsAllowed == value) return; downloadsAllowed = value; Saved(); }
        }

        /// <summary>The site an address belongs to for the per-site memories: its host without "www.", lower case; null for anything but http(s).</summary>
        public static string SiteKey(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            string h = u.Host.ToLowerInvariant();
            if (h.StartsWith("www.", StringComparison.Ordinal)) h = h.Substring(4);
            return h.Length == 0 ? null : h;
        }

        /// <summary>The zoom level remembered for a site (0 when none).</summary>
        public static double GetSiteZoom(string site)
        {
            EnsureLoaded();
            double z;
            lock (gate) return site != null && siteZoom.TryGetValue(site, out z) ? z : 0;
        }

        /// <summary>Remembers a site's zoom level (0 forgets it). Saved quietly: not a settings change that the rest of the browser reacts to.</summary>
        public static void SetSiteZoom(string site, double level)
        {
            EnsureLoaded();
            if (site == null) return;
            lock (gate)
            {
                if (Math.Abs(level) < 0.01) { if (!siteZoom.Remove(site)) return; }
                else
                {
                    level = Math.Max(-3, Math.Min(5, level));
                    double was;
                    if (siteZoom.TryGetValue(site, out was) && Math.Abs(was - level) < 0.001) return;
                    siteZoom[site] = level;
                    if (siteZoom.Count > 300) { string first = null; foreach (var k in siteZoom.Keys) { if (k != site) { first = k; break; } } if (first != null) siteZoom.Remove(first); }
                }
            }
            Save();
        }

        public static bool IsMobileSite(string site)
        {
            EnsureLoaded();
            lock (gate) return site != null && mobileSites.Contains(site);
        }

        public static void SetMobileSite(string site, bool on)
        {
            EnsureLoaded();
            if (site == null) return;
            lock (gate) { if (on ? !mobileSites.Add(site) : !mobileSites.Remove(site)) return; }
            Save();
        }

        /// <summary>Hide the address bar and the button hints while scrolling a page. Default on.</summary>
        public static bool AutoHideBars
        {
            get { EnsureLoaded(); return autoHide; }
            set { EnsureLoaded(); if (autoHide == value) return; autoHide = value; Saved(); }
        }

        /// <summary>
        /// How big web pages are drawn before the user's own zoom: 0 = Auto (the page scales with the screen, so a 4K TV at 100%
        /// shows what a 1080p screen would), or a fixed factor such as 1.5. Default Auto.
        /// </summary>
        public static double PageScale
        {
            get { EnsureLoaded(); return pageScale; }
        }

        /// <summary>With PageScale = 0 (Auto): how much bigger (or smaller) than the automatic size pages are drawn. 1 = as Auto picks it.</summary>
        public static double PageBoost
        {
            get { EnsureLoaded(); return pageBoost; }
        }

        /// <summary>Sets the page size: fixedScale 0 = Auto (then boost adjusts it), otherwise a fixed scale. Saved once.</summary>
        public static void SetPageSize(double fixedScale, double boost)
        {
            EnsureLoaded();
            fixedScale = Math.Max(0, Math.Min(4, fixedScale));
            boost = Math.Max(0.5, Math.Min(3, boost));
            if (Math.Abs(pageScale - fixedScale) < 0.001 && Math.Abs(pageBoost - boost) < 0.001) return;
            pageScale = fixedScale;
            pageBoost = boost;
            Saved();
        }

        private static void Saved()
        {
            Save();
            var h = Changed;
            if (h != null) h();
        }

        // Writes on a worker so the UI never waits for the disk; the newest values are serialized inside the lock, so
        // quick successive toggles can never leave an older file behind.
        private static void Save()
        {
            string file = FilePath;
            if (file == null) return;
            Task.Run(delegate
            {
                try
                {
                    lock (gate)
                    {
                        var d = new Dictionary<string, object>();
                        d["AdBlockEnabled"] = adBlock;
                        d["AutoHideBars"] = autoHide;
                        d["PhoneKeyboard"] = phoneKeyboard;
                        d["PhonePasswords"] = phonePasswords;
                        d["PhoneRemote"] = phoneRemote;
                        d["PageScale"] = pageScale;
                        d["PageBoost"] = pageBoost;
                        d["DownloadsAllowed"] = downloadsAllowed;
                        d["SiteZoom"] = new Dictionary<string, double>(siteZoom);
                        d["MobileSites"] = new List<string>(mobileSites);
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllText(file, new JavaScriptSerializer().Serialize(d), Encoding.UTF8);
                    }
                }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: cannot write " + file); }
            });
        }
    }
}
