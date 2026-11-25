// Spotlight's bottom-left slot shows news about the selected game (Steam patch notes / dev updates) instead of the
// screenshot strip when there is news to show. Hooks live in HomeNav/HomeHints (they call OnSelected/Clear/HandleA);
// the Steam API reader itself is the self-contained SteamGameNews.cs. With no AppID or no news the strip is left alone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Playnite.SDK.Models;

namespace Selene
{
    // the on-disk copy of one app's last good fetch (12 h old entries are ignored and replaced)
    internal class GameNewsCacheEntry
    {
        public long SavedTicks;
        public List<SteamNewsArticle> Articles;
    }

    public static class GameNews
    {
        private const int Tiles = 3;
        // ask for more than the tiles need: the language filter drops foreign posts after the fetch, and 3 asked = 1-2 shown
        private const int FetchCount = 12;
        private const string SteamPluginId = "cb91dfc9-b977-43bf-8e70-55f46e410fab";
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

        private static Dispatcher ui;
        private static Dispatcher UiDispatcher { get { return ui != null ? ui : (Application.Current != null ? Application.Current.Dispatcher : null); } }
        private static void CapturedOnUi() { if (ui == null) ui = Dispatcher.CurrentDispatcher; }

        private static DispatcherTimer debounce;
        private static Game waiting;
        private static int generation;
        private static volatile bool visible;
        private static volatile List<SteamNewsArticle> shown = new List<SteamNewsArticle>();

        private static bool resourcesReady;
        private static bool shotsShown;   // the resources already say "screenshots": stepping through games writes nothing

        public static bool Visible { get { return visible; } }

        // all the resource keys exist with safe defaults from the first call, before anything is fetched
        private static void EnsureResources()
        {
            if (resourcesReady || Application.Current == null) return;
            resourcesReady = true;
            var res = Application.Current.Resources;
            res["SeleneGameNewsVis"] = Visibility.Collapsed;
            res["SeleneShotsSlotVis"] = Visibility.Visible;   // screenshots stay on until news actually arrives
            ClearTiles();
        }

        private static void ClearTiles()
        {
            if (Application.Current == null) return;
            var res = Application.Current.Resources;
            for (int i = 0; i < Tiles; i++)
            {
                res["SeleneGameNews" + i] = null;
                res["SeleneGameNewsTitle" + i] = "";
                res["SeleneGameNewsSub" + i] = "";
                res["SeleneGameNewsTileVis" + i] = Visibility.Collapsed;
            }
        }

        // shots are the default: news only takes the slot over once real articles have arrived
        private static void ShowShots()
        {
            EnsureResources();
            bool wasNews = visible;
            visible = false;
            shown = new List<SteamNewsArticle>();
            if (Application.Current == null) return;
            if (shotsShown) return;   // every selection step lands here: rewriting a dozen DynamicResources each time is a hitch at 4K
            shotsShown = true;
            var res = Application.Current.Resources;
            res["SeleneGameNewsVis"] = Visibility.Collapsed;
            res["SeleneShotsSlotVis"] = Visibility.Visible;
            ClearTiles();
            if (wasNews) HomeNav.RehomeFocusIfHidden();
        }

        public static void Clear()
        {
            CapturedOnUi();
            generation++;   // any in-flight fetch is stale now
            waiting = null;
            if (debounce != null) debounce.Stop();
            ShowShots();
        }

        // Spotlight's row holds Playnite's GamesCollectionViewEntry wrappers, not Game objects (as in GameMedia.AsGame)
        private static Game AsGame(object o)
        {
            var g = o as Game;
            if (g != null || o == null) return g;
            var prop = o.GetType().GetProperty("Game");
            return prop != null ? prop.GetValue(o, null) as Game : null;
        }

        // called when Spotlight's selection changes; the wait lets stepping through cards skip the work entirely
        public static void OnSelected(object selected)
        {
            CapturedOnUi();
            EnsureResources();
            if (!Features.On("gamenews")) { Clear(); return; }
            var game = AsGame(selected);
            if (game == null) { Clear(); return; }
            generation++;
            waiting = game;
            ShowShots();   // no stale news from another game: the strip is the safe default until this one's news arrives
            if (debounce == null)
            {
                debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                debounce.Tick += (s, e) => { debounce.Stop(); var g = waiting; if (g != null) Settle(g); };
            }
            debounce.Stop();
            debounce.Start();
        }

        private static void Settle(Game game)
        {
            if (!Features.On("gamenews")) { Clear(); return; }
            if (!StillSelected(game)) return;   // the row moved on while the timer was running
            int mine = generation;
            string key = Key(game);
            string app;
            var dispatcher = UiDispatcher;
            if (!TryRemembered(key, out app))
            {
                // the name lookup asks Steam over the network (GameMedia.SteamAppIdByName), so it belongs on the worker
                Task.Run(() =>
                {
                    bool failed;
                    string found = Resolve(game, out failed);
                    if (!failed) Remember(key, found);   // "no AppID" is remembered (not retried on every selection); a network error is not
                    if (found == null)
                    {
                        HomeNav.Log("game news: no Steam AppID for " + game.Name);
                        NoNews(dispatcher, mine);
                    }
                    else Load(game, found, mine, dispatcher);
                });
                return;
            }
            if (app == null)
            {
                HomeNav.Log("game news: no Steam AppID for " + game.Name);
                NoNews(dispatcher, mine);
                return;
            }
            Load(game, app, mine, dispatcher);
        }

        private static void NoNews(Dispatcher dispatcher, int mine)
        {
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(() => { if (mine == generation) ShowShots(); }));
        }

        private static bool StillSelected(Game game)
        {
            try
            {
                if (HomeNav.Api == null || HomeNav.Api.MainView == null) return true;
                var selected = HomeNav.Api.MainView.SelectedGames;
                if (selected == null) return true;
                var first = selected.FirstOrDefault();
                return first == null || first.Id == game.Id;
            }
            catch (Exception ex) { HomeNav.Log("game news: selection check: " + ex.Message); return true; }
        }

        private static string Key(Game game) { return game.Id.ToString() + "|" + (game.Name ?? ""); }

        // failed: the lookup itself broke (offline, Steam down), so the answer must not be kept as "this game has no AppID"
        private static string Resolve(Game game, out bool failed)
        {
            failed = false;
            try
            {
                string app = MediaSource.Get(game.Id).SteamId;   // an explicit choice wins
                if (IsAppId(app)) return app;
                if (string.Equals(game.PluginId.ToString(), SteamPluginId, StringComparison.OrdinalIgnoreCase) && IsAppId(game.GameId)) return game.GameId;
                app = GameMedia.SteamAppIdByName(game.Name);
                return IsAppId(app) ? app : null;
            }
            catch (Exception ex) { failed = true; HomeNav.Log("game news: AppID for " + game.Name + ": " + ex.Message); return null; }
        }

        private static bool IsAppId(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < value.Length; i++) if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }

        private static readonly Dictionary<string, string> appIds = new Dictionary<string, string>();
        private static readonly object appIdGate = new object();

        private static bool TryRemembered(string key, out string app)
        {
            lock (appIdGate) return appIds.TryGetValue(key, out app);
        }

        private static void Remember(string key, string app)
        {
            lock (appIdGate) appIds[key] = app;
        }

        private static void Load(Game game, string appId, int mine, Dispatcher dispatcher)
        {
            string name = game.Name;
            // article pictures are often missing; this game's own art keeps the tiles from looking empty
            string fallback = GameFacts.BackgroundPath(game) ?? GameFacts.CoverPath(game);
            Task.Run(() =>
            {
                List<SteamNewsArticle> articles = null;
                var pictures = new Dictionary<string, string>();
                try
                {
                    articles = ReadCache(appId);
                    if (articles == null)
                    {
                        articles = SteamNewsClient.Parse(SteamNewsClient.Fetch(appId, FetchCount)).Take(Tiles).ToList();
                        WriteCache(appId, articles);
                    }
                    foreach (var a in articles)
                    {
                        if (a == null || string.IsNullOrEmpty(a.Thumbnail)) continue;
                        string file = NewsCard.Picture(a.Thumbnail);   // the shared local picture cache, off the UI thread
                        if (file != null) pictures[a.Thumbnail] = file;
                    }
                }
                catch (Exception ex) { HomeNav.Log("game news: " + name + ": " + (ex.InnerException ?? ex).Message); articles = null; }
                if (dispatcher == null) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (mine != generation) return;   // a newer selection (or Clear) owns the slot now
                    Publish(articles, pictures, fallback);
                }));
            });
        }

        private static void Publish(List<SteamNewsArticle> articles, Dictionary<string, string> pictures, string fallback)
        {
            EnsureResources();
            if (articles == null || articles.Count == 0) { ShowShots(); return; }
            var top = articles.Take(Tiles).ToList();
            shown = top;
            visible = true;
            shotsShown = false;
            var res = Application.Current.Resources;
            res["SeleneGameNewsVis"] = Visibility.Visible;
            res["SeleneShotsSlotVis"] = Visibility.Collapsed;
            for (int i = 0; i < Tiles; i++)
            {
                var a = i < top.Count ? top[i] : null;
                string file = null;
                if (a != null && !string.IsNullOrEmpty(a.Thumbnail) && pictures.ContainsKey(a.Thumbnail)) file = pictures[a.Thumbnail];
                // a slot with no article stays hidden: an art-only tile that cannot be opened is worse than no tile
                res["SeleneGameNews" + i] = a != null ? Decode(file != null ? file : fallback) : null;
                res["SeleneGameNewsTileVis" + i] = a != null ? Visibility.Visible : Visibility.Collapsed;
                res["SeleneGameNewsTitle" + i] = a != null ? a.Title : "";
                res["SeleneGameNewsSub" + i] = a != null ? a.Source + " \u00b7 " + Relative(a.Published) : "";
            }
            HomeNav.Log("game news: showing " + top.Count + " article(s)");
            HomeNav.RehomeFocusIfHidden();   // a screenshot tile that had the focus just collapsed
        }

        private static BitmapImage Decode(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = PixelSize.Width(448);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) { HomeNav.Log("game news: picture: " + ex.Message); return null; }
        }

        private static string Relative(DateTime published)
        {
            if (published == DateTime.MinValue) return "";
            var span = DateTime.UtcNow - published;
            if (span.TotalMinutes < 60) return Lang.T("Just now");
            if (span.TotalHours < 24) return (int)span.TotalHours + " " + Lang.T("h ago");
            if (span.TotalDays < 7) { int days = (int)span.TotalDays; return days + " " + (days == 1 ? Lang.T("day ago") : Lang.T("days ago")); }
            return published.Year == DateTime.UtcNow.Year ? published.ToString("d MMM") : published.ToString("d MMM yyyy");   // a year-old patch note must not look current
        }

        // D-pad Down from the game row focuses the first news tile when news is showing (Playnite's pad path skips FocusNews)
        public static bool FocusFirst(DependencyObject root)
        {
            if (!visible || root == null) return false;
            var tile = FindNamed(root, "SeleneGameNewsTile0") as UIElement;
            if (tile == null || !tile.IsVisible) return false;
            if (!tile.Focus() || !ReferenceEquals(Keyboard.FocusedElement, tile)) return false;
            return true;
        }

        private static DependencyObject FindNamed(DependencyObject root, string name)
        {
            if (root == null) return null;
            var fe = root as FrameworkElement;
            if (fe != null && !fe.IsVisible) return null;   // stale/hidden hub copies after a row rebuild
            if (fe != null && fe.Name == name) return root;
            int children = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < children; i++)
            {
                var hit = FindNamed(VisualTreeHelper.GetChild(root, i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        // A on a news tile opens the article in the in-theme browser (the same reuse key as the other news cards)
        // HomeNav passes Keyboard.FocusedElement (an IInputElement) straight through
        public static bool HandleA(System.Windows.IInputElement focused) { return Handle(focused as DependencyObject); }

        // used by both the pad/keyboard hooks and the ButtonEx Click handler (Playnite swallows A on a button with no Click)
        public static bool Open(int index)
        {
            if (index < 0 || index >= shown.Count) { HomeNav.Log("game news: no article at " + index); return false; }
            var article = shown[index];
            if (article == null || !SteamNewsClient.ValidUrl(article.Url)) { HomeNav.Log("game news: no safe URL at " + index); return false; }
            try { HomeNav.OpenInThemeBrowser(article.Url, "news"); return true; }
            catch (Exception ex)
            {
                HomeNav.Log("game news: open article: " + ex.Message);
                try { Ui.Notice(Lang.T("Could not open the article:")); } catch { }
                return false;
            }
        }

        private static bool Handle(DependencyObject focused)
        {
            if (!Features.On("gamenews") || focused == null) return false;
            FrameworkElement tile = null;
            for (DependencyObject d = focused; d != null; d = Parent(d))
            {
                var fe = d as FrameworkElement;
                if (fe != null && fe.Name.StartsWith("SeleneGameNewsTile", StringComparison.Ordinal)) { tile = fe; break; }
            }
            if (tile == null) return false;
            int index;
            if (!int.TryParse(Convert.ToString(tile.Tag), out index)) return false;
            return Open(index);
        }

        private static DependencyObject Parent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            var content = d as FrameworkContentElement;
            return content != null ? content.Parent : null;
        }

        // ---- disk cache, keyed by Steam AppID (writes are atomic, as in NewsFeeds) ----

        // v2: the old cache holds records without body images (Steam's thumbnail field is usually empty)
        private static string CacheFile { get { return Path.Combine(SelenePaths.DataFolder, "game-news-cache-2.json"); } }
        private static readonly object diskGate = new object();

        private static List<SteamNewsArticle> ReadCache(string appId)
        {
            try
            {
                lock (diskGate)
                {
                    if (!File.Exists(CacheFile)) return null;
                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    var map = serializer.Deserialize<Dictionary<string, GameNewsCacheEntry>>(File.ReadAllText(CacheFile));
                    GameNewsCacheEntry entry;
                    if (map == null || !map.TryGetValue(appId, out entry) || entry == null || entry.Articles == null) return null;
                    long age = DateTime.UtcNow.Ticks - entry.SavedTicks;
                    if (age < 0 || age > Ttl.Ticks) return null;
                    return entry.Articles.Take(Tiles).ToList();
                }
            }
            catch (Exception ex) { HomeNav.Log("game news: cache read: " + ex.Message); return null; }
        }

        private static void WriteCache(string appId, List<SteamNewsArticle> articles)
        {
            try
            {
                lock (diskGate)
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    Dictionary<string, GameNewsCacheEntry> map = null;
                    if (File.Exists(CacheFile)) map = serializer.Deserialize<Dictionary<string, GameNewsCacheEntry>>(File.ReadAllText(CacheFile));
                    if (map == null) map = new Dictionary<string, GameNewsCacheEntry>();
                    long now = DateTime.UtcNow.Ticks;
                    foreach (var key in map.Where(p => p.Value == null || now - p.Value.SavedTicks > Ttl.Ticks || now < p.Value.SavedTicks).Select(p => p.Key).ToList()) map.Remove(key);
                    map[appId] = new GameNewsCacheEntry { SavedTicks = now, Articles = articles };
                    Directory.CreateDirectory(SelenePaths.DataFolder);
                    string tmp = CacheFile + ".tmp";
                    File.WriteAllText(tmp, serializer.Serialize(map), Encoding.UTF8);
                    if (File.Exists(CacheFile)) File.Replace(tmp, CacheFile, null); else File.Move(tmp, CacheFile);
                }
            }
            catch (Exception ex) { HomeNav.Log("game news: cache write: " + ex.Message); }
        }
    }
}
