using System;
using System.Linq;

namespace Selene
{
    internal static class MediaDownloads
    {
        internal static readonly string[] Keys = { "artdownload", "screenshotsdownload", "trailerdownload", "metadatacomplete" };
        public static bool Unified { get { return Features.On("downloadprogress"); } }
        public static bool Enabled { get { return Features.Remembered("library-media-consent") == "all-library-v1" && Keys.All(DownloadConsent.Allowed) && Features.Value("trailerdownload") == "all"; } }
        public static bool ShowSetting(string key) { return !Unified || !Keys.Contains(key); }

        public static string Details()
        {
            return "Get missing art, trailers, screenshots and details for your library.\n" +
                "From Steam and your metadata add-ons · Trailers ~20 MB/game";
        }

        // Kept separate from starting workers so permission transitions can be checked without network access.
        internal static void Grant()
        {
            foreach (var key in Keys)
            {
                if (key == "trailerdownload") Features.SetValue(key, "all"); else Features.Set(key, true);
                DownloadConsent.Grant(key);
            }
            Features.Remember("library-media-consent", "all-library-v1");
            Features.Remember("gamesask-done", "yes");
        }

        public static void Accept() { Grant(); Start(); }

        // one note when the downloads are through (no cards while they run): checked every few seconds while work is going
        private static System.Windows.Threading.DispatcherTimer doneWatch;
        private static bool sawWork;
        private static void WatchForDone()
        {
            if (doneWatch != null) return;
            var since = DateTime.Now;
            doneWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            doneWatch.Tick += (s, e) =>
            {
                // (nothing ever ran, nothing to say: it stops looking after a few minutes)
                if (!Enabled || (!sawWork && (DateTime.Now - since).TotalMinutes > 5)) { doneWatch.Stop(); doneWatch = null; sawWork = false; return; }
                double p = Progress();
                if (p >= 0 && p < 1) { sawWork = true; return; }
                if (p >= 1 && sawWork)
                {
                    doneWatch.Stop(); doneWatch = null; sawWork = false;
                    HomeNav.Log("media downloads: done");
                    if (Features.On("taskcards")) ProgressToast.ShowNote("Game media", "Downloaded", 0xE896);
                }
            };
            doneWatch.Start();
        }
        public static void Start()
        {
            if (!Unified || !Enabled || HomeNav.Api == null) return;
            TrailerFetch.DownloadSettingsChanged();
            GameMedia.DownloadSettingsChanged();
            TrailerFetch.QueueLibraryMedia();
            GameInfo.CompleteLibrary();
            WatchForDone();
        }
        internal static void Revoke()
        {
            Features.Remember("library-media-consent", "");
            foreach (var key in Keys)
            {
                if (key == "trailerdownload") Features.SetValue(key, "off"); else Features.Set(key, false);
                DownloadConsent.Revoke(key);
            }
        }
        public static void Stop()
        {
            Revoke();
            TrailerFetch.DownloadSettingsChanged(); GameMedia.DownloadSettingsChanged();
        }
        public static string Status()
        {
            if (!Enabled) return "Downloads off";
            return TrailerFetch.LibraryStatus + "\nCovers / details: " + GameInfo.CompletionStatus;
        }
        public static string SettingStatus()
        {
            if (Unified) return Status();
            return GamesSetup.Allowed ? Background.ArtNote() ?? "Waiting for a game selection" : null;
        }
        // Both stages, each half of the bar (the least-complete alone held it at 0% with details done). 100% means both are idle.
        internal static double OverallProgress(double art, double details, bool busy)
        {
            if (art < 0) return -1;
            if (double.IsNaN(art) || double.IsNaN(details)) return 0;
            Func<double, double> clamp = v => Math.Max(0, Math.Min(1, v));
            double part = (clamp(art) + clamp(details)) / 2;
            return busy ? Math.Min(.99, part) : part;
        }
        public static double Progress()
        {
            if (!Enabled) return -1;
            return OverallProgress(TrailerFetch.LibraryProgress, GameInfo.Progress,
                TrailerFetch.Busy || GameInfo.Busy);
        }
    }
}
