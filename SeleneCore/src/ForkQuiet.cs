using System;

namespace Selene
{
    // Lanternfall: no background music behind the fullscreen theme. The "Selene OST" track is no longer shipped; what
    // Selene had set up for it in UniPlaySong is taken back out, and UniPlaySong's ambient (default) music is switched
    // off once (Sound & music > Ambient music brings it back, and stays as the user leaves it).
    // Kept in its own file so merging upstream Selene stays clean; hooked from SeleneCore.OnApplicationStarted.
    public static class ForkQuiet
    {
        private const string Marker = "lanternfall-quiet-ambient";

        public static void Apply()
        {
            try
            {
                if (!MusicLevel.Installed()) return;   // nothing to quiet yet: try again once the music add-on is there
                // the default-music file Selene pointed UniPlaySong at is gone: the setting is cleared, and the source goes back to its own
                SongSetup.UpdateUps(ClearSeleneTrack, "Lanternfall: no Selene OST");
                if (Features.Remembered(Marker) == "1") return;
                if (MusicLevel.UpsSwitch("EnableDefaultMusic")) MusicLevel.SetUpsSwitch("EnableDefaultMusic", false);
                Features.Remember(Marker, "1");
            }
            catch (Exception ex) { HomeNav.Log("quiet ambient: " + (ex.InnerException ?? ex).Message); }
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
    }
}
