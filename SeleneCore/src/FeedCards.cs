// The Browser / YouTube Spotlight cards show their own three tiles in the bottom slot that games use for screenshots or news.
// HomeNav calls OnSelected when the row moves; the data behind the tiles is published as plain resources by SeleneBrowser.dll
// (Browser/CardFeed*), so this file only owns selection, focus and the A action. Kept in a new file to stay easy to merge.
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Selene
{
    public static class FeedCards
    {
        private const int Tiles = 3;
        private static volatile bool visible;
        private static string active = "youtube";

        public static bool Visible { get { return visible; } }
        private static bool tookSlot;

        private static void Put(System.Windows.ResourceDictionary res, string key, object value)
        {
            if (!object.Equals(res[key], value)) res[key] = value;
        }

        // which of the sibling bottom grids the hub should focus / slide with
        public static string BottomName(bool newsVisible)
        {
            if (visible) return active == "youtube" ? "SeleneYouTubeFeedCards" : "SeleneBrowserFeedCards";
            return newsVisible ? "SeleneGameNewsCards" : "HubBottomCards";
        }

        public static void OnSelected(object selected)
        {
            if (Application.Current == null) return;
            var s = selected as string;
            string name = s != null && s.StartsWith("nav:", StringComparison.Ordinal) ? s.Substring(4) : null;
            bool youtube = name == "youtube", browser = name == "browser";
            bool show = Features.On("cardpreviews") && (youtube || browser);
            visible = show;
            if (show) active = youtube ? "youtube" : "browser";

            var res = Application.Current.Resources;
            // only real changes are written: this runs on every step through the row
            Put(res, "SeleneYouTubeFeedEmpty", Lang.T("Subscribe to channels to see their newest videos here."));
            Put(res, "SeleneBrowserFeedEmpty", Lang.T("Open the browser to save places you visit often."));
            Put(res, "SeleneYouTubeFeedVis", show && youtube ? Visibility.Visible : Visibility.Collapsed);
            Put(res, "SeleneBrowserFeedVis", show && browser ? Visibility.Visible : Visibility.Collapsed);
            // GameNews/GameMedia run just before this and leave the screenshot slot visible: take it over only for the web cards.
            // Leaving them, give it back unless a game's Steam news owns it (GameNews rewrites it when its news arrives).
            if (show) { Put(res, "SeleneShotsSlotVis", Visibility.Collapsed); tookSlot = true; }
            else if (tookSlot) { tookSlot = false; if (!GameNews.Visible) Put(res, "SeleneShotsSlotVis", Visibility.Visible); HomeNav.RehomeFocusIfHidden(); }
        }

        private static string Name(string kind, int index)
        {
            string tile = kind == "youtube" ? "SeleneYouTubeFeedTile" : "SeleneBrowserFeedTile";
            return tile + index;
        }

        private static string Prefix(string kind) { return kind == "youtube" ? "SeleneYouTubeFeed" : "SeleneBrowserFeed"; }

        // D-pad Down from the row: focus the first tile of the visible strip
        public static bool FocusFirst(DependencyObject root)
        {
            if (!visible || root == null) return false;
            var tile = FindNamed(root, Name(active, 0)) as UIElement;
            if (tile == null || !tile.IsVisible) return false;
            if (!tile.Focus() || !ReferenceEquals(Keyboard.FocusedElement, tile)) return false;
            return true;
        }

        private static DependencyObject FindNamed(DependencyObject root, string name)
        {
            if (root == null) return null;
            var fe = root as FrameworkElement;
            if (fe != null && !fe.IsVisible) return null;
            if (fe != null && fe.Name == name) return root;
            int children = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < children; i++)
            {
                var hit = FindNamed(VisualTreeHelper.GetChild(root, i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        public static bool HandleA(IInputElement focused) { return Handle(focused as DependencyObject); }

        // also called by HomeNav's ButtonEx Click handler (Playnite swallows A on a button without one)
        public static bool Open(string kind, int index)
        {
            if (!visible || index < 0 || index >= Tiles || Application.Current == null) return false;
            var value = Application.Current.Resources[Prefix(kind) + "Key" + index];
            string key = value == null ? "" : value.ToString();
            if (key.Length == 0) return false;
            try
            {
                if (kind == "browser")
                {
                    if (!NewsFeeds.WebUrl(key)) return false;
                    HomeNav.OpenInThemeBrowser(key, "cardfeed");
                }
                else
                {
                    HomeNav.OpenYouTubeVideo(key);
                }
                return true;
            }
            catch (Exception ex) { HomeNav.Log("card feed: open " + kind + ": " + ex.Message); return false; }
        }

        private static bool Handle(DependencyObject focused)
        {
            if (!visible || focused == null) return false;
            string kind = null;
            FrameworkElement tile = null;
            for (DependencyObject d = focused; d != null; d = Parent(d))
            {
                var fe = d as FrameworkElement;
                if (fe == null || fe.Name == null) continue;
                if (fe.Name.StartsWith("SeleneYouTubeFeedTile", StringComparison.Ordinal)) { kind = "youtube"; tile = fe; break; }
                if (fe.Name.StartsWith("SeleneBrowserFeedTile", StringComparison.Ordinal)) { kind = "browser"; tile = fe; break; }
            }
            if (tile == null) return false;
            int index;
            return int.TryParse(Convert.ToString(tile.Tag), out index) && Open(kind, index);
        }

        private static DependencyObject Parent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D) return VisualTreeHelper.GetParent(d);
            var content = d as FrameworkContentElement;
            return content != null ? content.Parent : null;
        }
    }
}
