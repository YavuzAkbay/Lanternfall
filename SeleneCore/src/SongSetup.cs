using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Playnite.SDK.Models;

namespace Selene
{
    // UniPlaySong's settings as Lanternfall keeps them: its hover settle, Selene's default music, and its automatic downloads
    // switched off where an older build had set it up with its own tools. Lanternfall no longer downloads yt-dlp/ffmpeg or asks
    // UniPlaySong for songs: getting songs is UniPlaySong's own business, with its own controls.
    public static class SongSetup
    {
        private static readonly Guid UniPlaySongId = new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        // where older builds put yt-dlp (only read, to recognise the setups they made)
        private static string ManagedYtDlp { get { return Path.Combine(SelenePaths.DataFolder, "Tools", "yt-dlp.exe"); } }
        internal static bool DisableAutomaticTriggers(object settings)
        {
            bool changed = false;
            foreach (var name in new[] { "AutoDownloadOnLibraryUpdate", "AutoDownloadOnGameInstall" })
            {
                var prop = settings != null ? settings.GetType().GetProperty(name) : null;
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool) && Equals(prop.GetValue(settings, null), true))
                { prop.SetValue(settings, false, null); changed = true; }
            }
            return changed;
        }
        public static void DisableManagedAutomaticDownloads()
        {
            try
            {
                var current = UpsSettings();
                var path = current != null ? Prop(current, "YtDlpPath") as string : null;
                if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(ManagedYtDlp), StringComparison.OrdinalIgnoreCase)) return;
                if (UpdateUps(DisableAutomaticTriggers, "Selene download consent"))
                    HomeNav.Log("songs: automatic library/install downloads disabled for Selene-managed tools; independent installations left unchanged");
            }
            catch (Exception ex) { HomeNav.Log("songs: managed download preferences: " + (ex.InnerException ?? ex).Message); }
        }

        // Songs wait until you stop: UniPlaySong switched song for every game passed while a direction was held, and each
        // switch (fade, open, decode) ran on Playnite's UI thread: up to 0.7 s frozen mid-scroll. Its own "hover settle"
        // waits until the selection rests. Turned on (1 s, its shortest) when it's off; switch: Sound & music.
        // Selene's own background default music, in UniPlaySong's default-music slot. The track ships in the theme
        // (Media\Selene OST.mp3); it is copied to Selene Core's data folder, so a theme update or a switch of theme can't
        // take it from under UniPlaySong. Only when no default file is chosen yet, and only once: a file you picked stays, and clearing
        // the setting later is respected. Without it UniPlaySong plays its own first bundled track.
        public static void ApplyDefaultMusic()
        {
            if (!Features.On("selenemusic")) return;
            try
            {
                var source = SelenePaths.ThemeFile("Media", "Selene OST.mp3");
                if (!System.IO.File.Exists(source)) return;
                var target = System.IO.Path.Combine(SelenePaths.DataFolder, "Music", "Selene OST.mp3");
                if (UpdateUps(settings => UseSeleneDefault(settings, source, target), "Selene OST")) HomeNav.Log("songs: Selene's background default music is UniPlaySong's default track");
            }
            catch (Exception ex) { HomeNav.Log("songs: background default music: " + (ex.InnerException ?? ex).Message); }
        }

        // the settings change itself (true: changed, to be saved)
        internal static bool UseSeleneDefault(object settings, string source, string target)
        {
            var type = settings != null ? settings.GetType() : null;
            var prop = type != null ? type.GetProperty("DefaultMusicPath") : null;
            if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(string)) return false;
            var current = prop.GetValue(settings, null) as string;
            bool already = string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
            // the name it had before it was called Selene OST: moved over, not left behind
            var legacy = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target), "Selene background default.mp3");
            bool mine = already || string.Equals(current, legacy, StringComparison.OrdinalIgnoreCase);
            if (!mine && !string.IsNullOrWhiteSpace(current)) return false;                      // their own file

            // UniPlaySong's "default music source": 0 a file, 1 a folder, 2 its own bundled tracks. A file path counts for nothing
            // while it is on 2 (a new install's value), so the source has to be switched too. Left alone when the user chose a
            // folder, or a bundled track other than the one it starts on.
            var sourceProp = type.GetProperty("DefaultMusicSourceOption");
            int? sourceNow = null;
            if (sourceProp != null && sourceProp.CanWrite) try { sourceNow = Convert.ToInt32(sourceProp.GetValue(settings, null)); } catch { }
            var presetProp = type.GetProperty("SelectedBundledPreset");
            var preset = presetProp != null ? presetProp.GetValue(settings, null) as string : null;
            const string StartPreset = "tunetank-dark-ambient-soundscape-music.mp3";
            if (sourceNow == 1) return false;                                                    // a folder of their own
            if (sourceNow == 9) return false;                                                    // Spotify as the ambient (its own switch)
            if (sourceNow == 2 && !string.IsNullOrEmpty(preset) && !string.Equals(preset, StartPreset, StringComparison.OrdinalIgnoreCase) && !mine) return false;   // a bundled track they picked
            var marker = Features.Remembered("selene-default-music");
            // marker "1": set by an earlier build that did not switch the source ("2": this one); "2" with the source moved off 0 is their choice
            if (already && marker == "2" && sourceNow != 0) return false;
            if (!mine && (marker == "1" || marker == "2")) return false;                          // they cleared it

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            if (!System.IO.File.Exists(target) || new System.IO.FileInfo(target).Length != new System.IO.FileInfo(source).Length) System.IO.File.Copy(source, target, true);

            Features.Remember("selene-default-music", "2");
            bool changed = false;
            if (!already) { prop.SetValue(settings, target, null); changed = true; if (mine) { try { System.IO.File.Delete(legacy); } catch { } } }
            if (sourceNow.HasValue && sourceNow.Value != 0)
            {
                sourceProp.SetValue(settings, sourceProp.PropertyType.IsEnum ? Enum.ToObject(sourceProp.PropertyType, 0) : Convert.ChangeType(0, sourceProp.PropertyType), null);
                changed = true;
            }
            return changed;
        }
        internal static bool EnableSettle(object settings)
        {
            var on = settings != null ? settings.GetType().GetProperty("HoverSettleEnabled") : null;
            if (on == null || !on.CanWrite || on.PropertyType != typeof(bool) || Equals(on.GetValue(settings, null), true)) return false;
            on.SetValue(settings, true, null);
            var secs = settings.GetType().GetProperty("HoverSettleSeconds");
            if (secs != null && secs.CanWrite && secs.PropertyType == typeof(double)) secs.SetValue(settings, 1.0, null);
            return true;
        }

        public static void ApplySongSettle()
        {
            if (!Features.On("songsettle")) return;
            try { if (UpdateUps(EnableSettle, "Selene smooth scrolling")) HomeNav.Log("songs: UniPlaySong waits until the selection rests (hover settle on)"); }
            catch (Exception ex) { HomeNav.Log("songs: hover settle: " + (ex.InnerException ?? ex).Message); }
        }

        private static object UpsPlugin() { return HomeNav.Api != null ? HomeNav.Api.Addons.Plugins.FirstOrDefault(p => p.Id == UniPlaySongId) : null; }
        private static object UpsSettings()
        {
            var plugin = UpsPlugin();
            var service = plugin != null ? Field(plugin, "_settingsService") : null;
            return service != null ? Prop(service, "Current") : null;
        }

        // a copy of UniPlaySong's settings changed by `change`, handed to its settings service and saved (false: nothing to change)
        internal static bool UpdateUps(Func<object, bool> change, string reason)
        {
            var plugin = UpsPlugin();
            var service = plugin != null ? Field(plugin, "_settingsService") : null;
            var current = service != null ? Prop(service, "Current") : null;
            if (current == null) return false;
            var clone = typeof(Playnite.SDK.Data.Serialization).GetMethods().First(m => m.Name == "GetClone" && m.IsGenericMethod).MakeGenericMethod(current.GetType()).Invoke(null, new[] { current });
            if (!change(clone)) return false;
            var update = service.GetType().GetMethod("UpdateSettings");
            if (update == null) { HomeNav.Log("songs: UniPlaySong settings could not be updated (" + reason + ")"); return false; }
            update.Invoke(service, new[] { clone, reason });
            var save = plugin.GetType().GetMethods().FirstOrDefault(m => m.Name == "SavePluginSettings" && m.IsGenericMethod);
            if (save != null) save.MakeGenericMethod(clone.GetType()).Invoke(plugin, new[] { clone });
            HomeNav.ForgetUps();
            return true;
        }
        private static object Field(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static object Prop(object o, string name)
        {
            var p = o != null ? o.GetType().GetProperty(name) : null;
            return p != null ? p.GetValue(o, null) : null;
        }
    }
}
