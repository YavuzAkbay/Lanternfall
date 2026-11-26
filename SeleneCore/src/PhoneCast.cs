using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Selene
{
    // The phone remote's way onto the TV. The Lanternfall Browser add-on serves the phone page (SeleneBrowser.PhoneRemote) and finds
    // this class by reflection; Core owns the overlays, the pad and the library, so Core decides whether the TV may do something now.
    // Every method is called on the UI thread and uses System types only (the add-on cannot see Core's). The string methods return
    // null when it worked, otherwise one sentence the phone shows. New file: fork-only.
    public static class PhoneCast
    {
        // the buttons a phone may press (Playnite's ControllerInput names, as HomeNav and the overlays read them)
        private static readonly string[] Buttons = { "A", "B", "X", "Y", "Start", "Back", "LeftShoulder", "RightShoulder", "TriggerLeft", "TriggerRight",
                                                     "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "LeftStick", "RightStick" };

        // kind "video": value is a YouTube video id (starts in the YouTube screen); kind "url": an http(s) address (opens in the browser)
        public static string Receive(string kind, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value)) return "Nothing to open.";
                string busy = Busy();
                if (busy != null) return busy;
                if (kind == "video")
                {
                    HomeNav.Log("phone remote: video " + value);
                    HomeNav.OpenYouTubeVideo(value);
                    return null;
                }
                if (kind == "url")
                {
                    if (!NewsFeeds.WebUrl(value)) return "That link cannot be opened.";
                    HomeNav.Log("phone remote: link");
                    HomeNav.OpenInThemeBrowser(value);
                    return null;
                }
                return "The TV does not know that request.";
            }
            catch (Exception ex)
            {
                try { HomeNav.Log("phone remote: " + ex.Message); } catch { }
                return "The TV could not do that.";
            }
        }

        private static string Busy()
        {
            if (HomeNav.Quitting) return "The TV is shutting down.";
            if (HomeNav.GameRunning() || HomeNav.Launching) return "A game is running on the TV. Close it first, then send this again.";
            if (LoginScene.InSetup) return "The TV is waiting at the profile screen. Pick who is playing first.";
            return null;
        }

        // One controller button from the phone: pressed and let go through Playnite's own input path, so every screen of the theme
        // (home, Collection, menus, the overlays) reacts exactly as to the real pad. Not while a game runs: the game has the pad then.
        public static string Pad(string button)
        {
            try
            {
                if (Array.IndexOf(Buttons, button) < 0) return "Unknown button.";
                if (HomeNav.Quitting) return "The TV is shutting down.";
                if (HomeNav.GameRunning() || HomeNav.Launching) return "A game is running. Use your controller in the game.";
                string result = null;
                bool now = true;
                Action send = () =>
                {
                    if (!Press(button, true)) { result = "The TV did not take the press."; return; }
                    // let go a moment later, as a finger would
                    var up = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                    up.Tick += (s, e) => { up.Stop(); Press(button, false); };
                    up.Start();
                };
                // Playnite behind another window: it comes forward first, then the press lands (a moment later, so it is not checked)
                HomeNav.PhoneFront(() => { if (now) send(); else Application.Current.Dispatcher.BeginInvoke(send); });
                now = false;
                return result;
            }
            catch (Exception ex)
            {
                try { HomeNav.Log("phone remote pad: " + ex.Message); } catch { }
                return "The TV did not take the press.";
            }
        }

        // Playnite's own controller path (PlaynitePad); Selene's stand-in only when this Playnite has none
        private static bool Press(string button, bool down)
        {
            return PlaynitePad.Available ? PlaynitePad.Press(button, down) : Ui.SendPad(button, down);
        }

        // What the TV shows, for the phone's header: screen = home | library | menu | profile | game, plus the game in focus / running,
        // and the theme's focus colour (its accent) so the phone page wears the same palette.
        public static Dictionary<string, object> State()
        {
            var d = new Dictionary<string, object>();
            try
            {
                var running = HomeNav.RunningGame();
                if (running != null || HomeNav.Launching)
                {
                    d["screen"] = "game";
                    if (running != null) { d["game"] = running.Name ?? ""; d["gameId"] = running.Id.ToString(); }
                }
                else if (LoginScene.InSetup) d["screen"] = "profile";
                else if (LoginScene.IsActive) d["screen"] = "menu";
                else if (SeleneShell.CollectionActive) d["screen"] = "library";
                else d["screen"] = "home";
                if (!d.ContainsKey("game"))
                {
                    var w = Application.Current != null ? Application.Current.MainWindow : null;
                    var vm = w != null ? w.DataContext : null;
                    var p = vm != null ? vm.GetType().GetProperty("SelectedGame") : null;
                    var sel = p != null ? p.GetValue(vm, null) : null;
                    var name = sel != null ? sel.GetType().GetProperty("Name") : null;
                    if (name != null) d["sel"] = name.GetValue(sel, null) as string ?? "";
                    // one of the home's own cards in focus (Browser, YouTube, News...): its name instead of a game's
                    var card = (string)d["screen"] == "home" ? HomeNav.FocusedCard() : null;
                    if (card != null && card.StartsWith("nav:") && card.Length > 4)
                    {
                        var key = card.Substring(4);
                        d["sel"] = key == "youtube" ? "YouTube" : key == "surprise" ? "Surprise me" : char.ToUpperInvariant(key[0]) + key.Substring(1);
                    }
                }
                var accent = Application.Current != null ? Application.Current.TryFindResource("SeleneFocusBrush") as SolidColorBrush : null;
                if (accent != null) d["accent"] = string.Format("#{0:x2}{1:x2}{2:x2}", accent.Color.R, accent.Color.G, accent.Color.B);
            }
            catch (Exception ex) { try { HomeNav.Log("phone remote state: " + ex.Message); } catch { } }
            if (!d.ContainsKey("screen")) d["screen"] = "home";
            return d;
        }

        // The library for the phone's Games tab: the games the TV shows (hidden ones stay hidden), most recently played first.
        public static List<Dictionary<string, object>> Games()
        {
            var list = new List<Dictionary<string, object>>();
            var api = HomeNav.Api;
            if (api == null) return list;
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var g in api.Database.Games.Where(x => !x.Hidden).OrderByDescending(x => x.LastActivity ?? DateTime.MinValue).ThenBy(x => x.Name))
            {
                var e = new Dictionary<string, object>
                {
                    { "id", g.Id.ToString() }, { "name", g.Name ?? "" }, { "inst", g.IsInstalled }, { "fav", g.Favorite }, { "run", g.IsRunning },
                    { "play", (long)Math.Min(g.Playtime, (ulong)long.MaxValue) },
                    { "cover", !string.IsNullOrEmpty(g.CoverImage) }
                };
                if (g.LastActivity.HasValue) e["last"] = (long)(g.LastActivity.Value.ToUniversalTime() - epoch).TotalMilliseconds;
                var src = g.Source != null ? g.Source.Name : null;
                if (!string.IsNullOrEmpty(src)) e["src"] = src;
                list.Add(e);
                if (list.Count >= 5000) break;
            }
            return list;
        }

        // The full path of a game's cover on this PC, or null (none, or a web address Playnite has not downloaded).
        public static string CoverPath(string id)
        {
            Guid gid;
            var api = HomeNav.Api;
            if (api == null || !Guid.TryParse(id ?? "", out gid)) return null;
            var g = api.Database.Games.Get(gid);
            if (g == null || g.Hidden || string.IsNullOrEmpty(g.CoverImage)) return null;
            if (g.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            var path = api.Database.GetFullFilePath(g.CoverImage);
            return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? path : null;
        }

        // Play from the phone: the same start as Play on the TV (loading screen and all). Browser / YouTube and any open screen close first.
        public static string Launch(string id)
        {
            try
            {
                Guid gid;
                var api = HomeNav.Api;
                if (api == null || !Guid.TryParse(id ?? "", out gid)) return "That game was not found.";
                var g = api.Database.Games.Get(gid);
                if (g == null || g.Hidden) return "That game was not found.";
                if (HomeNav.Quitting) return "The TV is shutting down.";
                if (LoginScene.InSetup) return "The TV is waiting at the profile screen. Pick who is playing first.";
                if (HomeNav.Launching) return "A game is already starting.";
                var running = HomeNav.RunningGame();
                if (running != null && running.Id != gid) return running.Name + " is running. Close it first.";
                if (running == null && !g.IsInstalled) return g.Name + " is not installed.";
                HomeNav.Log("phone remote: play " + g.Name);
                HomeNav.CloseNomadOverlays();
                if (LoginScene.IsActive) LoginScene.FinishThen(() => HomeNav.StartGameNow(gid));
                else HomeNav.StartGameNow(gid);
                return null;
            }
            catch (Exception ex)
            {
                try { HomeNav.Log("phone remote play: " + ex.Message); } catch { }
                return "The TV could not start that game.";
            }
        }
    }
}
