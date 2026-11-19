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

        /// <summary>Hide the address bar and the button hints while scrolling a page. Default on.</summary>
        public static bool AutoHideBars
        {
            get { EnsureLoaded(); return autoHide; }
            set { EnsureLoaded(); if (autoHide == value) return; autoHide = value; Saved(); }
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
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.WriteAllText(file, new JavaScriptSerializer().Serialize(d), Encoding.UTF8);
                    }
                }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: cannot write " + file); }
            });
        }
    }
}
