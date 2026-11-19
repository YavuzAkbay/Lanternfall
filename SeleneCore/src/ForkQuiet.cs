using System;
using System.Windows;
using System.Windows.Threading;

namespace Selene
{
    // Lanternfall: no background music behind the fullscreen theme. The "Selene OST" track is no longer shipped; what
    // Selene had set up for it in UniPlaySong is taken back out, and UniPlaySong's ambient (default) music is switched
    // off once (Sound & music > Ambient music brings it back, and stays as the user leaves it).
    // Kept in its own file so merging upstream Selene stays clean; hooked from SeleneCore.OnApplicationStarted.
    public static class ForkQuiet
    {
        private const string Marker = "lanternfall-quiet-ambient";
        private static int attempts;

        public static void Apply()
        {
            try
            {
                RemoveOldTrack();
                if (!MusicLevel.Installed()) return;   // nothing to quiet yet: try again once the music add-on is there
                if (!Quiet()) Retry();
            }
            catch (Exception ex) { HomeNav.Log("quiet ambient: " + (ex.InnerException ?? ex).Message); }
        }

        // UniPlaySong's settings may not be loaded yet when Playnite has just started: look again a few times, so the
        // one-time switch-off is never used up without having happened
        private static void Retry()
        {
            if (++attempts > 8) return;
            var dispatcher = Application.Current != null ? Application.Current.Dispatcher : null;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try { if (!Quiet()) Retry(); }
                    catch (Exception ex) { HomeNav.Log("quiet ambient: " + (ex.InnerException ?? ex).Message); }
                };
                timer.Start();
            }));
        }

        // true: UniPlaySong's settings were there and are in order; false: not there yet
        private static bool Quiet()
        {
            bool seen = false;
            bool first = Features.Remembered(Marker) != "1";
            // the default-music file Selene pointed UniPlaySong at is gone: the setting is cleared, and the source goes back to its own
            SongSetup.UpdateUps(settings =>
            {
                seen = true;
                bool changed = ClearSeleneTrack(settings);
                if (first && SwitchOffAmbient(settings)) changed = true;
                return changed;
            }, "Lanternfall: no Selene OST");
            if (!seen) return false;
            if (first) Features.Remember(Marker, "1");   // only now: the settings were read
            HomeNav.Log("quiet ambient: no Selene OST in UniPlaySong" + (first ? ", ambient music off" : ""));
            return true;
        }

        // true: the settings changed (to be saved)
        internal static bool SwitchOffAmbient(object settings)
        {
            var type = settings != null ? settings.GetType() : null;
            var prop = type != null ? type.GetProperty("EnableDefaultMusic") : null;
            if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(bool) || !Equals(prop.GetValue(settings, null), true)) return false;
            prop.SetValue(settings, false, null);
            return true;
        }

        // true: the settings changed (to be saved)
        internal static bool ClearSeleneTrack(object settings)
        {
            var type = settings != null ? settings.GetType() : null;
            var prop = type != null ? type.GetProperty("DefaultMusicPath") : null;
            if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string)) return false;
            var current = prop.GetValue(settings, null) as string;
            if (string.IsNullOrEmpty(current)) return false;
            var name = System.IO.Path.GetFileName(current);
            if (!string.Equals(name, "Selene OST.mp3", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "Selene background default.mp3", StringComparison.OrdinalIgnoreCase)) return false;
            prop.SetValue(settings, "", null);
            return true;
        }

        // copies of the old track an earlier install left behind (an update unpacks over the old theme folder, it does not empty it)
        private static void RemoveOldTrack()
        {
            foreach (var file in new[]
            {
                SelenePaths.ThemeFile("Media", "Selene OST.mp3"),
                System.IO.Path.Combine(SelenePaths.DataFolder, "Music", "Selene OST.mp3"),
                System.IO.Path.Combine(SelenePaths.DataFolder, "Music", "Selene background default.mp3")
            })
            {
                try { if (System.IO.File.Exists(file)) { System.IO.File.Delete(file); HomeNav.Log("quiet ambient: removed " + file); } }
                catch (Exception ex) { HomeNav.Log("quiet ambient: could not remove " + file + ": " + ex.Message); }
            }
        }
    }
}
