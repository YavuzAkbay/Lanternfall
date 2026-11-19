// YouTubeControl.cs - controller-only YouTube screen (rail + thumbnail grid + search + channels).
// Pad contract: HandlePad / OnShown / OnHidden / Hints (docs\NOMAD-SPEC.md). The player lives in YouTubePlayerView.cs.
//   Rail : Up/Down move, A open, Right -> grid, LB/RB tabs, X refresh, B close.   Grid : D-pad move, A select,
//   Y menu (search keyboard on Search), B rail / back, LT/RT page, X refresh, Start menu.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;

namespace SeleneBrowser
{
    public partial class YouTubeControl : PluginUserControl, INotifyPropertyChanged
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const int Cols = 4;
        private const int TabSearch = 2;
        private static readonly string[] RailNames = { "Home", "Subscriptions", "Search", "History", "Playlists", "Channels" };

        private sealed class Boot
        {
            public List<ChannelItem> Channels = new List<ChannelItem>();
            public List<ChannelItem> Own = new List<ChannelItem>();
            public List<string> Searches = new List<string>();
            public bool ShortsOn = true;
        }

        private readonly Grid root = new Grid { Background = Theme.Background };
        private readonly Viewbox browseBox = new Viewbox { Stretch = Stretch.Uniform };
        private readonly OnScreenKeyboard keyboard = new OnScreenKeyboard();
        private readonly RepeatLimiter limiter = new RepeatLimiter();
        private readonly PickerPanel menu = new PickerPanel();
        private readonly Toaster toast = new Toaster();
        private readonly List<Border> railTiles = new List<Border>();
        private readonly List<Border> cards = new List<Border>();
        private readonly List<ViewState> stack = new List<ViewState>();
        private readonly ViewState[] tabStates = new ViewState[6];
        private readonly Dictionary<string, List<string>> suggestCache = new Dictionary<string, List<string>>();
        private readonly TextBlock titleText = Theme.Text("", 40, Theme.Ink);
        private readonly TextBlock subText = Theme.Text("", 22, Theme.Ink60);
        private readonly TextBlock statusText = Theme.Text("", 22, Theme.Ink60);
        private readonly TextBlock hintText = Theme.Text("", 22, Theme.Ink60);
        private readonly TextBlock msgText = Theme.Text("", 30, Theme.Ink);
        private readonly TextBlock msgSub = Theme.Text("", 24, Theme.Ink60);
        private readonly Ellipse headAvatar = new Ellipse { Width = 80, Height = 80, Margin = new Thickness(0, 0, 20, 0), Visibility = Visibility.Collapsed };
        private readonly Spinner msgSpinner = new Spinner(64);
        private readonly StackPanel msgPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly WrapPanel gridPanel = new WrapPanel { Width = Cols * Cards.CellW, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(20, 12, 0, 40) };
        private readonly ScrollViewer gridScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        private readonly SmoothScroller scroller;

        private readonly Border shortsChip = Theme.Tile("Shorts: On", 24);
        private ViewState current;
        private Border focusedBorder;
        private bool railFocus, chipFocus, shortsOn = true;
        private int activeTab, railIndex;
        private bool shown, initialized;
        private string lastQuery = "";
        private string suggestWanted = "";
        private string hints = "";
        private HashSet<string> subIds = new HashSet<string>(), ownIds = new HashSet<string>();
        private List<string> searchHist = new List<string>();

        public event PropertyChangedEventHandler PropertyChanged;
        public string Hints { get { return hints; } }

        public YouTubeControl()
        {
            Focusable = false;
            scroller = new SmoothScroller(gridScroll);
            Content = BuildLayout();
            current = TabState(0);
            ShowState(current);
            Unloaded += delegate { StopAllTimers(); };
            UpdateHints();
        }

        // ------------------------------------------------------------------ pad contract

        public bool HandlePad(string button)
        {
            try
            {
                if (keyboard.IsOpen)
                {
                    keyboard.HandlePad(button);
                    if (!keyboard.IsOpen) UpdateHints();
                    return true;
                }
                if (playing) return HandlePlayerPad(button);
                if (menu.IsOpen) return HandleMenu(button);
                return HandleBrowse(button);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "SeleneBrowser: YouTube HandlePad " + button);
                Toast("Something went wrong. Please try again.");
                return true;
            }
        }

        public void OnShown()
        {
            try
            {
                shown = true;
                if (!initialized) InitialLoad();
                else Task.Run(delegate { WatchedIds.Rebuild(false); });
                if (playing) StartPlayerTimers();
                UpdateHints();
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube OnShown"); }
        }

        public void OnHidden()
        {
            try
            {
                shown = false;
                PauseForHide();
                StopAllTimers();
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube OnHidden"); }
        }

        private void StopAllTimers()
        {
            scroller.Stop();
            toast.Stop();
            StopPlayerTimers();
        }

        // ------------------------------------------------------------------ browse pad

        private bool HandleBrowse(string b)
        {
            switch (b)
            {
                case "DPadUp": case "DPadDown": case "DPadLeft": case "DPadRight":
                    if (limiter.Allow(b)) Navigate(b);
                    return true;
                case "A": if (limiter.IsFirst(b)) Activate(); return true;
                case "B": return !limiter.IsFirst(b) || Back();
                case "X": if (limiter.IsFirst(b)) ReloadCurrent(); return true;
                case "Y": if (limiter.IsFirst(b)) SecondaryAction(); return true;
                case "Start": if (limiter.IsFirst(b)) OpenStartMenu(); return true;
                case "LeftShoulder": if (limiter.IsFirst(b)) ActivateTab((activeTab + RailNames.Length - 1) % RailNames.Length, false); return true;
                case "RightShoulder": if (limiter.IsFirst(b)) ActivateTab((activeTab + 1) % RailNames.Length, false); return true;
                case "TriggerLeft": if (limiter.Allow(b) && !railFocus && !chipFocus) SetFocus(Math.Max(0, current.Focus - 3 * Cols)); return true;
                case "TriggerRight": if (limiter.Allow(b) && !railFocus && !chipFocus) SetFocus(Math.Min(current.Count - 1, current.Focus + 3 * Cols)); return true;
            }
            return true;
        }

        private bool ChipVisible { get { return current != null && current.Tab == 1 && stack.Count == 0; } }

        private void Navigate(string b)
        {
            if (chipFocus)
            {
                if (b == "DPadDown") { if (current.Count > 0) { chipFocus = false; UpdateFocusVisual(); } }
                else if (b == "DPadLeft") { chipFocus = false; railFocus = true; railIndex = activeTab; UpdateFocusVisual(); }
                return;
            }
            if (railFocus)
            {
                if (b == "DPadUp") railIndex = Math.Max(0, railIndex - 1);
                else if (b == "DPadDown") railIndex = Math.Min(RailNames.Length - 1, railIndex + 1);
                else if (b == "DPadRight") railFocus = false;
                UpdateFocusVisual();
                return;
            }
            int n = current.Count, i = current.Focus, col = i % Cols;
            if (b == "DPadUp" && ChipVisible && (n == 0 || i < Cols)) { chipFocus = true; UpdateFocusVisual(); return; }
            if (b == "DPadLeft")
            {
                if (n == 0 || col == 0) { railFocus = true; railIndex = activeTab; UpdateFocusVisual(); return; }
                i--;
            }
            else if (n == 0) return;
            else if (b == "DPadRight") { if (col < Cols - 1 && i + 1 < n) i++; }
            else if (b == "DPadDown") { if (i + Cols < n) i += Cols; else if (i / Cols < (n - 1) / Cols) i = n - 1; }
            else if (b == "DPadUp") { if (i - Cols >= 0) i -= Cols; }
            SetFocus(i);
        }

        // false = close the overlay (only from the rail of a top level view)
        private bool Back()
        {
            if (stack.Count > 0) { PopView(); return true; }
            if (chipFocus) { chipFocus = false; railFocus = true; railIndex = activeTab; UpdateFocusVisual(); return true; }
            if (!railFocus) { railFocus = true; railIndex = activeTab; UpdateFocusVisual(); return true; }
            return false;
        }

        private void Activate()
        {
            if (chipFocus) { ToggleShorts(); return; }
            if (railFocus)
            {
                if (railIndex == TabSearch) { OpenSearch(""); return; }
                ActivateTab(railIndex, false);
                railFocus = current.Count == 0;
                UpdateFocusVisual();
                return;
            }
            if (current.Count == 0)
            {
                if (current.Error.Length > 0) ReloadCurrent();
                return;
            }
            int i = current.Focus;
            if (current.Kind == CardKind.Channel) { ChannelItem c = current.Channels[i]; OpenChannel(c.Id, c.Name, c.Thumbnail); }
            else if (current.Kind == CardKind.Playlist) OpenPlaylist(current.Playlists[i]);
            else Play(current.Videos, i);
        }

        // Y: search keyboard on the Search tab, otherwise the context menu of the focused video.
        private void SecondaryAction()
        {
            if ((railFocus ? railIndex : activeTab) == TabSearch) { OpenSearch(lastQuery); return; }
            if (railFocus || chipFocus || current.Kind != CardKind.Video || current.Count == 0) return;
            List<PickerPanel.Entry> entries = VideoEntries(current.Videos[current.Focus]);
            OpenMenu(current.Videos[current.Focus].Title, entries);
        }

        // ------------------------------------------------------------------ menus

        private static PickerPanel.Entry Item(string label, Func<bool> run)
        {
            PickerPanel.Entry e = new PickerPanel.Entry();
            e.Label = label;
            e.Run = run;
            return e;
        }

        private void OpenMenu(string heading, List<PickerPanel.Entry> entries)
        {
            menu.Open(heading, entries, 0);
            UpdateHints();
        }

        private bool HandleMenu(string b)
        {
            switch (b)
            {
                case "DPadUp": if (limiter.Allow(b)) menu.Move(-1); break;
                case "DPadDown": if (limiter.Allow(b)) menu.Move(1); break;
                case "A":
                    if (!limiter.IsFirst(b)) break;
                    PickerPanel.Entry e = menu.Current;
                    menu.Close();
                    UpdateHints();
                    return e == null || e.Run == null || e.Run();
                case "B": case "Start": case "Y": if (limiter.IsFirst(b)) { menu.Close(); UpdateHints(); } break;
            }
            return true;
        }

        private void OpenStartMenu()
        {
            List<PickerPanel.Entry> l = new List<PickerPanel.Entry>();
            if (!railFocus && current.Tab == TabSearch && current.Kind == CardKind.Video && current.Count > 0)
                l.AddRange(VideoEntries(current.Videos[current.Focus]));
            if (ChipVisible) l.Add(Item(shortsOn ? "Hide Shorts" : "Show Shorts", delegate { ToggleShorts(); return true; }));
            if (current.Header != null && current.Header.Id.Length > 0) l.Add(SubscribeEntry(current.Header.Id, current.Header.Name, current.Header.Thumbnail));
            l.Add(Item("Refresh", delegate { ReloadCurrent(); return true; }));
            l.Add(Item("Clear Nomad watch history", delegate { ClearNomadHistory(); return true; }));
            l.Add(Item("Close YouTube", delegate { return false; }));
            OpenMenu("YouTube", l);
        }

        private List<PickerPanel.Entry> VideoEntries(VideoItem v)
        {
            List<PickerPanel.Entry> l = new List<PickerPanel.Entry>();
            if (v.AuthorId.Length > 0)
            {
                string id = v.AuthorId, name = v.Author;
                l.Add(Item("Go to channel", delegate { OpenChannel(id, name, ""); return true; }));
                l.Add(SubscribeEntry(id, name, ""));
            }
            l.Add(Item("Mark as watched", delegate { MarkWatched(v); return true; }));
            return l;
        }

        private PickerPanel.Entry SubscribeEntry(string id, string name, string avatar)
        {
            if (ownIds.Contains(id))
                return Item("Unsubscribe from " + name, delegate { SetSubscribed(id, name, avatar, false); return true; });
            if (subIds.Contains(id))
                return Item("Subscribed (managed in FreeTube)", delegate { Toast("Manage this subscription in FreeTube."); return true; });
            return Item("Subscribe to " + name, delegate { SetSubscribed(id, name, avatar, true); return true; });
        }

        private void SetSubscribed(string id, string name, string avatar, bool on)
        {
            ChannelItem c = new ChannelItem();
            c.Id = id; c.Name = name; c.Thumbnail = avatar;
            Task.Run(delegate { NomadStore.SetSubscribed(c, on); });
            if (on) { ownIds.Add(id); subIds.Add(id); }
            else { ownIds.Remove(id); subIds.Remove(id); }
            if (tabStates[1] != null) tabStates[1].Loaded = false;
            if (tabStates[5] != null) tabStates[5].Loaded = false;
            Toast(on ? "Subscribed to " + name : "Unsubscribed from " + name);
        }

        private void MarkWatched(VideoItem v)
        {
            Task.Run(delegate
            {
                if (v.Duration > 0) NomadStore.SavePosition(v.Id, v.Duration, v.Duration);
                NomadStore.AddHistory(v, v.Duration);
            });
            v.WatchProgress = v.Duration;
            WatchedIds.Add(v.Id);
            ReplaceCard(current.Focus);
            Toast("Marked as watched");
        }

        private void ClearNomadHistory()
        {
            Task.Run(delegate
            {
                try { NomadStore.ClearHistory(); WatchedIds.Rebuild(true); }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: clear YouTube history"); }
            }).ContinueWith(delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Toast("Nomad watch history cleared");
                    if (tabStates[3] != null) tabStates[3].Loaded = false;
                    if (current.Tab == 3) LoadState(current);
                }));
            });
        }

        // ------------------------------------------------------------------ search

        private void OpenSearch(string initial)
        {
            keyboard.Open("Search YouTube", initial, delegate(string text) { RunSearch(text); UpdateHints(); }, delegate { UpdateHints(); }, Suggest);
            UpdateHints();
        }

        private void RunSearch(string text)
        {
            string query = (text ?? "").Trim();
            if (query.Length == 0) return;
            lastQuery = query;
            searchHist.RemoveAll(delegate(string s) { return string.Equals(s, query, StringComparison.OrdinalIgnoreCase); });
            searchHist.Insert(0, query);
            Task.Run(delegate { NomadStore.AddSearch(query); });
            ViewState vs = TabState(TabSearch);
            vs.Title = "Search: " + query;
            vs.Videos = new List<VideoItem>();
            vs.Focus = 0;
            vs.Continuation = "";
            vs.Loaded = false;
            vs.Loader = delegate(string c) { return YouTubeApi.Search(query, c); };
            ActivateTab(TabSearch, true);
            railFocus = false;
            UpdateFocusVisual();
        }

        // History + cached network suggestions (fetched in the background, shown from the next keystroke on).
        private IEnumerable<string> Suggest(string t)
        {
            List<string> res = new List<string>();
            if (string.IsNullOrWhiteSpace(t))
            {
                for (int i = 0; i < searchHist.Count && res.Count < 8; i++) res.Add(searchHist[i]);
                return res;
            }
            foreach (string h in searchHist)
                if (res.Count < 3 && h.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) res.Add(h);
            for (int len = t.Length; len >= 1; len--)
            {
                List<string> hit;
                if (!suggestCache.TryGetValue(t.Substring(0, len), out hit)) continue;
                foreach (string s in hit)
                    if (res.Count < 10 && s.StartsWith(t, StringComparison.OrdinalIgnoreCase) && !res.Contains(s)) res.Add(s);
                break;
            }
            if (!suggestCache.ContainsKey(t)) FetchSuggestions(t);
            return res;
        }

        private void FetchSuggestions(string t)
        {
            suggestWanted = t;
            After(250, delegate
            {
                if (suggestWanted != t || suggestCache.ContainsKey(t)) return;
                Await(YouTubeApi.SearchSuggestions(t), delegate(List<string> r) { suggestCache[t] = r ?? new List<string>(); }, null);
            });
        }

        // ------------------------------------------------------------------ tabs and pages

        private ViewState TabState(int tab)
        {
            if (tabStates[tab] != null) return tabStates[tab];
            ViewState vs = new ViewState();
            vs.Tab = tab;
            vs.Title = RailNames[tab];
            vs.Kind = tab == 4 ? CardKind.Playlist : (tab == 5 ? CardKind.Channel : CardKind.Video);
            if (tab == TabSearch) vs.Empty = "Press A on Search, or Y, to search YouTube.";
            if (tab == 1) vs.Empty = "No videos yet. Subscribe from a video's Y menu, or import your FreeTube data.";
            if (tab == 3) vs.Empty = "Nothing watched yet.";
            if (tab == 4) vs.Empty = "No playlists found (FreeTube playlists are imported automatically).";
            if (tab == 5) vs.Empty = "No subscribed channels yet.";
            tabStates[tab] = vs;
            return vs;
        }

        private void ActivateTab(int tab, bool reload)
        {
            activeTab = tab;
            railIndex = tab;
            stack.Clear();
            ViewState vs = TabState(tab);
            ShowState(vs);
            if (!vs.Loaded || reload) LoadState(vs);
        }

        private void ReloadCurrent()
        {
            if (current.Tab == 1 && Subscriptions.IsRefreshing) { Toast("Already updating subscriptions."); return; }
            LoadState(current);
        }

        private void LoadState(ViewState vs)
        {
            vs.Error = "";
            switch (vs.Tab)
            {
                case 0:
                    if (vs.Loader == null) vs.Loader = delegate(string c) { return YouTubeApi.Trending(c); };
                    FetchFirst(vs);
                    break;
                case 1: LoadSubscriptions(vs); break;
                case TabSearch:
                    if (vs.Loader != null) FetchFirst(vs);
                    else { vs.Loaded = true; if (vs == current) RefreshChrome(); }
                    break;
                case 3: LoadList<VideoItem>(vs, delegate { List<VideoItem> l = NomadStore.MergedHistory(300); return l; }, ReplaceVideos); break;
                case 4: LoadList<PlaylistItem>(vs, delegate { return FreeTubeData.LoadPlaylists(); }, ReplacePlaylists); break;
                case 5: LoadList<ChannelItem>(vs, delegate { return Subscriptions.Channels(); }, ReplaceChannels); break;
                default: FetchFirst(vs); break; // channel pages
            }
        }

        private void LoadList<T>(ViewState vs, Func<List<T>> job, Action<ViewState, List<T>> apply)
        {
            int tok = ++vs.Token;
            vs.Loading = true;
            if (vs == current) RefreshChrome();
            Work(job, delegate(List<T> items)
            {
                if (tok != vs.Token) return;
                vs.Loading = false;
                apply(vs, items);
            }, delegate(string err) { if (tok == vs.Token) { vs.Loading = false; Fail(vs, err); } });
        }

        // First page of an API-backed view (trending, search, channel).
        private void FetchFirst(ViewState vs)
        {
            if (vs.Loader == null) return;
            int tok = ++vs.Token;
            vs.Loading = true;
            if (vs == current) RefreshChrome();
            Await(vs.Loader(""), delegate(PageResult<VideoItem> r)
            {
                if (tok != vs.Token) return;
                vs.Loading = false;
                if (!r.Ok) { Fail(vs, r.Error); return; }
                if (r.Header != null)
                {
                    vs.Header = r.Header;
                    if (r.Header.Name.Length > 0) vs.Title = r.Header.Name;
                }
                Annotate(r.Items);
                vs.Continuation = r.Continuation ?? "";
                ReplaceVideos(vs, r.Items);
            }, delegate(string err) { if (tok == vs.Token) { vs.Loading = false; Fail(vs, err); } });
        }

        private void LoadMore(ViewState vs)
        {
            if (vs.Loader == null || vs.Loading || !vs.HasMore) return;
            int tok = vs.Token;
            vs.Loading = true;
            if (vs == current) RefreshChrome();
            Await(vs.Loader(vs.Continuation), delegate(PageResult<VideoItem> r)
            {
                if (tok != vs.Token) return;
                vs.Loading = false;
                if (!r.Ok) { Fail(vs, r.Error); return; }
                HashSet<string> have = new HashSet<string>();
                foreach (VideoItem v in vs.Videos) have.Add(v.Id);
                int added = 0;
                Annotate(r.Items);
                foreach (VideoItem v in r.Items) if (have.Add(v.Id)) { vs.Videos.Add(v); added++; }
                vs.Continuation = added > 0 ? (r.Continuation ?? "") : "";
                if (vs == current) { EnsureBuilt(vs.Focus + 16); RefreshChrome(); MaybeLoadMore(); }
            }, delegate(string err) { if (tok == vs.Token) { vs.Loading = false; Fail(vs, err); } });
        }

        private void MaybeLoadMore()
        {
            if (current.Loader != null && current.HasMore && !current.Loading && current.Focus >= current.Count - 2 * Cols - 1) LoadMore(current);
        }

        private void LoadSubscriptions(ViewState vs)
        {
            int tok = ++vs.Token;
            vs.Loading = vs.Videos.Count == 0;
            if (vs == current) RefreshChrome();
            Work<List<VideoItem>>(delegate { List<VideoItem> l = Subscriptions.LoadCached(); Annotate(l); return l; }, delegate(List<VideoItem> cached)
            {
                if (tok != vs.Token) return;
                vs.Loading = false;
                if (cached.Count > 0) ReplaceVideos(vs, cached);
                vs.Loaded = true;
                RefreshSubscriptions(vs, tok);
            }, delegate(string err) { if (tok == vs.Token) { vs.Loading = false; Fail(vs, err); } });
        }

        private void RefreshSubscriptions(ViewState vs, int tok)
        {
            if (Subscriptions.IsRefreshing) return;
            vs.Status = "Updating\u2026";
            if (vs == current) RefreshChrome();
            Subscriptions.Refresh(delegate(List<VideoItem> snap, int done, int total)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (tok != vs.Token) return;
                    vs.Status = "Updating\u2026 " + done + "/" + total;
                    long now = Environment.TickCount;
                    if (vs.Videos.Count == 0 && now - vs.LastApply > 1500) { vs.LastApply = now; ReplaceVideos(vs, snap); }
                    if (vs == current) RefreshChrome();
                }));
            }).ContinueWith(delegate(Task<FeedRefreshResult> t)
            {
                FeedRefreshResult r = null;
                try { r = t.Result; if (r != null) Annotate(r.Videos); }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: subscriptions refresh"); }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (tok != vs.Token) return;
                    vs.Status = "";
                    if (r != null && r.Videos.Count > 0) ReplaceVideos(vs, r.Videos);
                    if (r != null && r.Message.Length > 0) Toast(r.Message);
                    if (vs == current) RefreshChrome();
                }));
            });
        }

        private void OpenChannel(string id, string name, string avatar)
        {
            ViewState vs = new ViewState();
            vs.Title = name;
            vs.Header = new ChannelItem();
            vs.Header.Id = id; vs.Header.Name = name; vs.Header.Thumbnail = avatar;
            vs.Empty = "This channel has no videos.";
            vs.Loader = delegate(string c) { return YouTubeApi.Channel(id, c); };
            PushView(vs);
            FetchFirst(vs);
        }

        private void OpenPlaylist(PlaylistItem p)
        {
            ViewState vs = new ViewState();
            vs.Title = p.Title;
            vs.Videos = new List<VideoItem>(p.Videos);
            vs.Loaded = true;
            vs.Empty = "This playlist is empty.";
            PushView(vs);
            Work<bool>(delegate { Annotate(vs.Videos); return true; }, delegate(bool ok) { if (vs == current) ShowState(vs); }, null);
        }

        private void PushView(ViewState vs)
        {
            stack.Add(current);
            railFocus = false;
            ShowState(vs);
        }

        private void PopView()
        {
            ViewState prev = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            current.Token++;
            ShowState(prev);
        }

        // ------------------------------------------------------------------ data helpers

        private void ReplaceVideos(ViewState vs, List<VideoItem> items)
        {
            string keep = vs.Focus < vs.Videos.Count ? vs.Videos[vs.Focus].Id : null;
            if (vs.Tab == 1)
            {
                // Subscriptions: keep the full feed, show it without Shorts when they are switched off
                vs.AllVideos = items;
                if (!shortsOn) items = items.FindAll(delegate(VideoItem x) { return !x.IsShort; });
            }
            vs.Videos = items;
            int ix = keep == null ? -1 : items.FindIndex(delegate(VideoItem x) { return x.Id == keep; });
            vs.Focus = ix >= 0 ? ix : 0;
            vs.Loaded = true;
            if (vs == current) ShowState(vs);
        }

        private void ToggleShorts()
        {
            shortsOn = !shortsOn;
            string val = shortsOn ? "1" : "0";
            Task.Run(delegate { NomadStore.SetPref("yt.shorts", val); });
            ViewState vs = tabStates[1];
            if (vs != null && vs.Loaded) ReplaceVideos(vs, vs.AllVideos);
            UpdateChip();
            UpdateFocusVisual();
            Toast(shortsOn ? "Shorts shown" : "Shorts hidden");
        }

        private void UpdateChip()
        {
            ((TextBlock)shortsChip.Child).Text = shortsOn ? "Shorts: On" : "Shorts: Off";
            shortsChip.Visibility = ChipVisible ? Visibility.Visible : Visibility.Collapsed;
            if (!ChipVisible) chipFocus = false;
        }

        private void ReplacePlaylists(ViewState vs, List<PlaylistItem> items)
        {
            vs.Playlists = items;
            vs.Focus = Math.Min(vs.Focus, Math.Max(0, items.Count - 1));
            vs.Loaded = true;
            if (vs == current) ShowState(vs);
        }

        private void ReplaceChannels(ViewState vs, List<ChannelItem> items)
        {
            vs.Channels = items;
            vs.Focus = Math.Min(vs.Focus, Math.Max(0, items.Count - 1));
            vs.Loaded = true;
            if (vs == current) ShowState(vs);
        }

        private void Fail(ViewState vs, string msg)
        {
            if (vs.Count == 0) vs.Error = msg;
            else Toast(msg);
            if (vs == current) RefreshChrome();
        }

        // Watch progress for the progress bars (positions of Nomad + FreeTube).
        private static void Annotate(List<VideoItem> list)
        {
            foreach (VideoItem v in list)
            {
                double p = NomadStore.GetPosition(v.Id);
                if (p > 0) v.WatchProgress = p;
            }
        }

        private void InitialLoad()
        {
            initialized = true;
            Work<Boot>(delegate
            {
                Boot b = new Boot();
                NomadStore.GetPosition("warm-up");
                b.Channels = Subscriptions.Channels();
                b.Own = NomadStore.OwnSubscriptions();
                b.Searches = NomadStore.Searches(60);
                b.ShortsOn = NomadStore.GetPref("yt.shorts", "1") != "0";
                WatchedIds.Rebuild(false);
                return b;
            }, delegate(Boot b)
            {
                subIds = new HashSet<string>();
                ownIds = new HashSet<string>();
                foreach (ChannelItem c in b.Channels) subIds.Add(c.Id);
                foreach (ChannelItem c in b.Own) ownIds.Add(c.Id);
                searchHist = b.Searches;
                shortsOn = b.ShortsOn;
                // (the watched set is complete only now: anything drawn before - the cached feed - is drawn again)
                ActivateTab(b.Channels.Count > 0 ? 1 : 0, true);
                if (current != null) ShowState(current);
            }, delegate(string err) { ActivateTab(0, true); });
        }

        // ------------------------------------------------------------------ grid rendering

        private void ShowState(ViewState vs)
        {
            current = vs;
            UpdateChip();
            gridPanel.Children.Clear();
            cards.Clear();
            focusedBorder = null;
            if (vs.Focus >= vs.Count) vs.Focus = Math.Max(0, vs.Count - 1);
            EnsureBuilt(vs.Focus + 16);
            titleText.Text = vs.Title;
            ShowHeader(vs);
            RefreshChrome();
            gridScroll.UpdateLayout();
            scroller.Snap(0);
            scroller.Snap(scroller.Reveal((vs.Focus / Cols) * Cards.CellH, Cards.CellH));
            UpdateFocusVisual();
        }

        private void ShowHeader(ViewState vs)
        {
            subText.Text = vs.Header != null ? vs.Header.Subscribers : "";
            subText.Visibility = subText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            headAvatar.Visibility = vs.Header != null ? Visibility.Visible : Visibility.Collapsed;
            headAvatar.Fill = Theme.Key;
            if (vs.Header != null)
                ThumbCache.Load(vs.Header.Thumbnail, 80, Dispatcher, delegate(System.Windows.Media.Imaging.BitmapImage bi)
                {
                    if (current == vs) headAvatar.Fill = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                });
        }

        // Status line, spinner / error / empty message.
        private void RefreshChrome()
        {
            ViewState vs = current;
            statusText.Text = vs.Status.Length > 0 ? vs.Status : (vs.Loading && vs.Count > 0 ? "Loading more\u2026" : "");
            if (vs.Header != null && subText.Text != vs.Header.Subscribers)
            {
                subText.Text = vs.Header.Subscribers;
                subText.Visibility = subText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (vs.Title != titleText.Text) titleText.Text = vs.Title;
            if (vs.Count > 0) { msgPanel.Visibility = Visibility.Collapsed; UpdateHints(); return; }
            msgPanel.Visibility = Visibility.Visible;
            if (vs.Error.Length > 0)
            {
                msgSpinner.Visibility = Visibility.Collapsed;
                msgText.Text = vs.Error;
                msgSub.Text = "A Retry    B Back";
            }
            else if (vs.Loading || !vs.Loaded)
            {
                msgSpinner.Visibility = Visibility.Visible;
                msgText.Text = "Loading\u2026";
                msgSub.Text = "";
            }
            else
            {
                msgSpinner.Visibility = Visibility.Collapsed;
                msgText.Text = vs.Empty;
                msgSub.Text = "";
            }
            UpdateHints();
        }

        private void EnsureBuilt(int upTo)
        {
            int limit = Math.Min(upTo, current.Count);
            while (cards.Count < limit)
            {
                Border c = BuildCard(cards.Count);
                cards.Add(c);
                gridPanel.Children.Add(c);
            }
        }

        private Border BuildCard(int i)
        {
            if (current.Kind == CardKind.Channel) return Cards.Channel(current.Channels[i], Dispatcher);
            if (current.Kind == CardKind.Playlist) return Cards.Playlist(current.Playlists[i], Dispatcher);
            return Cards.Video(current.Videos[i], Dispatcher, current.Tab != 3);   // (History itself is not dimmed)
        }

        private void ReplaceCard(int i)
        {
            if (i < 0 || i >= cards.Count || current.Kind != CardKind.Video) return;
            Border fresh = BuildCard(i);
            if (i >= gridPanel.Children.Count) return;
            gridPanel.Children.RemoveAt(i);   // (the indexer setter throws "index already in use")
            gridPanel.Children.Insert(i, fresh);
            cards[i] = fresh;
            if (focusedBorder != null) focusedBorder = null;
            UpdateFocusVisual();
        }

        private void SetFocus(int i)
        {
            if (current.Count == 0) return;
            i = Math.Max(0, Math.Min(i, current.Count - 1));
            current.Focus = i;
            EnsureBuilt(i + 16);
            UpdateFocusVisual();
            scroller.To(scroller.Reveal((i / Cols) * Cards.CellH, Cards.CellH));
            MaybeLoadMore();
        }

        private void UpdateFocusVisual()
        {
            if (focusedBorder != null) Theme.SetFocused(focusedBorder, false);
            focusedBorder = null;
            if (!railFocus && !chipFocus && current.Focus < cards.Count)
            {
                focusedBorder = cards[current.Focus];
                Theme.SetFocused(focusedBorder, true);
            }
            Theme.SetFocused(shortsChip, chipFocus && !railFocus);
            for (int i = 0; i < railTiles.Count; i++)
            {
                railTiles[i].Background = i == activeTab ? Theme.Key : Brushes.Transparent;
                Theme.SetFocused(railTiles[i], railFocus && i == railIndex);
            }
            UpdateHints();
        }

        // ------------------------------------------------------------------ small helpers

        private void Work<T>(Func<T> job, Action<T> ok, Action<string> fail)
        {
            Await(Task.Run(job), ok, fail);
        }

        private void Await<T>(Task<T> task, Action<T> ok, Action<string> fail)
        {
            task.ContinueWith(delegate(Task<T> t)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        if (t.IsFaulted || t.IsCanceled)
                        {
                            if (t.Exception != null) logger.Error(t.Exception, "SeleneBrowser: YouTube task failed");
                            if (fail != null) fail("Something went wrong. Please try again.");
                        }
                        else ok(t.Result);
                    }
                    catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube continuation"); }
                }));
            });
        }

        private void After(int ms, Action action)
        {
            DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate
            {
                t.Stop();
                try { action(); } catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube delayed action"); }
            };
            t.Start();
        }

        private void Toast(string message)
        {
            if (playing) playerToast.Show(message); else toast.Show(message);
        }

        private void SetHints(string h)
        {
            if (h == hints) return;
            hints = h;
            hintText.Text = h;
            pHint.Text = h;
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Hints"));
        }

        private void UpdateHints()
        {
            if (keyboard.IsOpen) SetHints(OnScreenKeyboard.Hints);
            else if (playing) SetHints(PlayerHints());
            else if (menu.IsOpen) SetHints("D-pad Select    A Choose    B Close");
            else if (chipFocus) SetHints("A Show/Hide Shorts    Down Videos    Left Rail    B Rail    LB/RB Tabs    X Refresh    Start Menu");
            else if (railFocus) SetHints("D-pad Move    A Open    B Close YouTube    LB/RB Tabs    X Refresh    Y Search    Start Menu");
            else if (activeTab == TabSearch && stack.Count == 0) SetHints("D-pad Move    A Play    B " + "Rail    Y Search    LB/RB Tabs    LT/RT Page    X Refresh    Start Menu");
            else SetHints("D-pad Move    A Select    B " + (stack.Count > 0 ? "Back" : "Rail") + "    Y Menu    LB/RB Tabs    LT/RT Page    X Refresh    Start Menu");
        }

        // ------------------------------------------------------------------ layout

        private UIElement BuildLayout()
        {
            Grid design = new Grid { Width = 1920, Height = 1080, Background = Theme.Background };
            design.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            design.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            design.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            design.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });

            // rail
            StackPanel rail = new StackPanel();
            TextBlock logo = Theme.Text("YouTube", 40, Theme.Accent);
            logo.FontWeight = FontWeights.Bold;
            logo.Margin = new Thickness(40, 36, 0, 30);
            rail.Children.Add(logo);
            for (int i = 0; i < RailNames.Length; i++)
            {
                Border t = Theme.Tile(RailNames[i], 30);
                t.Padding = new Thickness(24, 12, 24, 12);
                t.Margin = new Thickness(24, 5, 24, 5);
                t.Background = Brushes.Transparent;
                ((TextBlock)t.Child).HorizontalAlignment = HorizontalAlignment.Left;
                railTiles.Add(t);
                rail.Children.Add(t);
            }
            design.Children.Add(new Border { Background = Theme.Panel, Child = rail });

            // content: header + grid
            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid head = new Grid { Margin = new Thickness(40, 26, 50, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(headAvatar);
            StackPanel titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(titleText);
            titles.Children.Add(subText);
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            Grid.SetColumn(statusText, 2);
            head.Children.Add(statusText);
            content.Children.Add(head);

            shortsChip.Padding = new Thickness(22, 6, 22, 6);
            shortsChip.Margin = new Thickness(40, 0, 0, 4);
            shortsChip.HorizontalAlignment = HorizontalAlignment.Left;
            shortsChip.Visibility = Visibility.Collapsed;
            Grid.SetRow(shortsChip, 1);
            content.Children.Add(shortsChip);

            gridScroll.Content = gridPanel;
            Grid.SetRow(gridScroll, 2);
            content.Children.Add(gridScroll);
            msgPanel.Children.Add(msgSpinner);
            msgText.TextWrapping = TextWrapping.Wrap;
            msgText.TextAlignment = TextAlignment.Center;
            msgText.MaxWidth = 1000;
            msgText.Margin = new Thickness(0, 18, 0, 6);
            msgSub.HorizontalAlignment = HorizontalAlignment.Center;
            msgPanel.Children.Add(msgText);
            msgPanel.Children.Add(msgSub);
            Grid.SetRow(msgPanel, 2);
            content.Children.Add(msgPanel);
            Grid.SetColumn(content, 1);
            design.Children.Add(content);

            hintText.Margin = new Thickness(40, 0, 40, 0);
            Grid.SetRow(hintText, 1);
            Grid.SetColumnSpan(hintText, 2);
            design.Children.Add(hintText);

            Grid.SetRowSpan(menu, 2);
            Grid.SetColumnSpan(menu, 2);
            design.Children.Add(menu);
            Grid.SetRowSpan(toast, 2);
            Grid.SetColumnSpan(toast, 2);
            design.Children.Add(toast);

            browseBox.Child = design;
            root.Children.Add(browseBox);
            root.Children.Add(BuildPlayerLayer());
            root.Children.Add(keyboard);
            return root;
        }
    }
}
