// YouTubeControl.cs - controller-only YouTube screen (top tab bar + thumbnail grid + search + channels), laid out like the home screen.
// Pad contract: HandlePad / OnShown / OnHidden / Hints (docs\NOMAD-SPEC.md). The player lives in YouTubePlayerView.cs.
//   Tab bar : Left/Right move, A open, Down -> the page's content, LB/RB tabs, X refresh, B close.   Grid : D-pad move, A select,
//   Y menu (search keyboard on Search), B tab bar / back, LT/RT page, X refresh, Start menu. The chrome is in YouTubeChrome.cs.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;

namespace SeleneBrowser
{
    public partial class YouTubeControl : PluginUserControl, INotifyPropertyChanged
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const int Cols = 5;
        private const int TabSearch = 2;
        private static readonly string[] RailNames = { "Home", "Subscriptions", "Search", "History", "Playlists", "Channels" };

        private sealed class Boot
        {
            public List<ChannelItem> Channels = new List<ChannelItem>();
            public List<ChannelItem> Own = new List<ChannelItem>();
            public List<string> Searches = new List<string>();
            public bool ShortsOn = true;
        }

        private readonly Grid root = new Grid();
        private readonly Atmosphere atmosphere = new Atmosphere();
        private readonly Viewbox browseBox = new Viewbox { Stretch = Stretch.Uniform };
        private readonly OnScreenKeyboard keyboard = new OnScreenKeyboard();
        private readonly RepeatLimiter limiter = new RepeatLimiter();
        private readonly PickerPanel menu = new PickerPanel();
        private readonly Toaster toast = new Toaster();
        private readonly RestChip browseRestChip = new RestChip();   // pad-rest reminder while browsing (WPF tree)
        private readonly RestChip playerRestChip = new RestChip();   // pad-rest reminder in the player overlay window
        private YtTopBar tabBar;
        private Grid contentHost;   // everything under the top bar: faded + raised in when the screen's content changes
        private readonly int cols = Cols;
        private readonly List<Border> cards = new List<Border>();
        private readonly Border gridSpacer = new Border { Width = Cols * Cards.CellW, Height = 0, IsHitTestVisible = false, Background = null, Visibility = Visibility.Collapsed };   // windowing top spacer (gridPanel child 0 when used)
        private readonly List<ViewState> stack = new List<ViewState>();
        private readonly ViewState[] tabStates = new ViewState[6];
        private readonly Dictionary<string, List<string>> suggestCache = new Dictionary<string, List<string>>();
        private static readonly int[] RailIcons = { 0xE88A, 0xE064, 0xE8B6, 0xE889, 0xE05F, 0xE7EF };   // home, subscriptions, search, history, playlist_play, group
        private readonly TextBlock titleText = Theme.Title("", 44);
        private readonly TextBlock subText = Theme.Text("", 22, Theme.Ink60);
        private readonly TextBlock statusText = Theme.Text("", 21, Theme.Ink60);
        private readonly HintBar hintText = new HintBar(22, HorizontalAlignment.Right);   // 32 px badges, 22 pt words
        private readonly YtMessage msg = new YtMessage();
        private readonly Ellipse headAvatar = new Ellipse { Width = 88, Height = 88, Margin = new Thickness(0, 0, 22, 0), Visibility = Visibility.Collapsed };
        private readonly Spinner statusSpinner = new Spinner(26);   // compact "new videos are loading" cue in the header
        private readonly SkeletonGrid skeleton = new SkeletonGrid(Cols);
        // faces start at x = 64: the cell's 9 px card margin + the 6 px ring gap sit left of it (see Cards)
        private readonly WrapPanel gridPanel = new WrapPanel { Width = Cols * Cards.CellW, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(49, 12, 0, 110) };
        private readonly ScrollViewer gridScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        private readonly SmoothScroller scroller;

        private readonly Border shortsChip = Theme.Tile("Shorts: On", 20, 0xE04A, Look.Pill);   // video_library
        private readonly StackPanel channelRail = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly ScrollViewer channelRailScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        private readonly List<Border> channelCards = new List<Border>();
        private List<ChannelItem> railChannels;
        private int channelIndex;
        private readonly StackPanel filterChipsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<Border> chipTiles = new List<Border>();
        private readonly List<Border> headerTiles = new List<Border>();
        private int chipIndex;
        private readonly StackPanel continueRail = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly ScrollViewer continueRailScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        private readonly List<Border> continueCards = new List<Border>();
        private List<VideoItem> railContinue;
        private int continueIndex;
        private readonly TextBlock continueLabel = Chrome.SectionLabel("Continue watching");
        private readonly TextBlock channelLabel = Chrome.SectionLabel("Channels");
        private readonly Border subscribeBtn = Theme.Tile("Subscribe", 22, 0xE145, Look.Pill);   // add (+)
        private readonly Border watchLiveBtn = Theme.Tile("Watch Live", 22, 0xE639, Look.Pill);  // live_tv
        // the ambient backdrop: the focused card's picture melts into the screen (debounced, so a fast scroll doesn't thrash it)
        private readonly DispatcherTimer artTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private const bool AmbientArt = false;   // focused thumbnail melted into the backdrop: off
        private Border artCard;       // the card whose picture is wanted (null = none)
        private CardArt artWaiting;   // art whose picture hasn't arrived yet
        private readonly StackPanel formatChipsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<Border> formatTiles = new List<Border>();
        private int formatIndex;
        private readonly TextBlock headDesc = Theme.Text("", 20, Theme.Ink60);
        private readonly TextBlock profileText = Theme.Text("", 18, Theme.Ink60);
        private ViewState current;
        private Border focusedBorder;
        private bool railFocus, chipFocus, channelFocus, continueFocus, headerFocus, formatFocus, shortsOn = true;
        private int headerIndex;
        private int activeTab, railIndex;
        private bool shown, initialized;
        private string lastQuery = "";
        private string suggestWanted = "";
        private string hints = "";
        private string knownProfileId;
        private HashSet<string> subIds = new HashSet<string>(), ownIds = new HashSet<string>();
        private List<string> searchHist = new List<string>();

        public event PropertyChangedEventHandler PropertyChanged;
        public string Hints { get { return hints; } }

        public YouTubeControl()
        {
            Focusable = false;
            InitRemote();
            ProfileScope.Refresh();
            knownProfileId = ProfileScope.CurrentId;
            scroller = new SmoothScroller(gridScroll);
            scroller.BottomInset = 84;   // the button hints lie over the viewport's foot
            Content = BuildLayout();
            current = TabState(0);
            ShowState(current);
            Unloaded += delegate
            {
                Presence.Leave("youtube");
                browseRestChip.Stop(); playerRestChip.Stop();
                StopAllTimers(); ReleasePlayer();
            };
            artTimer.Tick += delegate { artTimer.Stop(); ApplyArt(); };
            Theme.Changed += delegate { CardInfo.Refresh(); };   // the home screen's site tiles are drawn in the theme's colours
            UpdateHints();
        }

        // ------------------------------------------------------------------ pad contract

        public bool HandlePad(string button)
        {
            try
            {
                PadActivity.Mark();
                if (linkCard.IsOpen) return HandleLinkCard(button);
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
                Presence.Enter("youtube");   // no idle screen / display sleep while YouTube is up
                // Refresh() is a shared signal (CardInfo's workers call it too), so compare OUR last id.
                ProfileScope.Refresh();
                if (knownProfileId != ProfileScope.CurrentId) SwitchProfile();
                if (!initialized) InitialLoad();
                else
                {
                    Task.Run(delegate { WatchedIds.Rebuild(false); });
                    ViewState feed = tabStates[1];
                    if (feed != null && feed.Loaded) BuildContinueRail(feed);
                }
                if (playing) StartPlayerTimers();
                browseRestChip.Start(); playerRestChip.Start();
                Theme.Sync();
                UpdateHints();
                if (current != null) { RefreshChrome(); UpdateFocusVisual(); }   // restore the skeleton / refresh cue and the ambient art after OnHidden
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube OnShown"); }
        }

        public void OnHidden()
        {
            try
            {
                shown = false;
                Presence.Leave("youtube");
                browseRestChip.Stop(); playerRestChip.Stop();
                CardInfo.MarkYouTubeSeen();
                CardInfo.Refresh();   // the home screen's YouTube card shows what was watched
                PauseForHide();
                StopAllTimers();
                ReleasePlayer();
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube OnHidden"); }
        }

        private void StopAllTimers()
        {
            scroller.Stop();
            toast.Stop();
            artTimer.Stop();
            if (artWaiting != null) { artWaiting.Arrived = null; artWaiting = null; }
            artCard = null;   // the next OnShown paints it again
            atmosphere.SetArt(null);
            skeleton.Hide();
            statusSpinner.Visibility = Visibility.Collapsed;
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
                case "TriggerLeft":
                    if (limiter.Allow(b) && GridLevel) SetFocus(Math.Max(0, current.Focus - 3 * cols));
                    return true;
                case "TriggerRight":
                    if (limiter.Allow(b) && GridLevel) SetFocus(Math.Min(current.Count - 1, current.Focus + 3 * cols));
                    return true;
            }
            return true;
        }

        /// <summary>The focus is on the video grid itself (not the tab bar, a rail, a chip row or the header's buttons).</summary>
        private bool GridLevel { get { return !railFocus && !chipFocus && !channelFocus && !continueFocus && !headerFocus && !formatFocus; } }

        private bool ChipVisible { get { return current != null && current.Tab == 1 && stack.Count == 0; } }
        private bool ChannelRailVisible { get { return current != null && stack.Count == 0 && channelCards.Count > 0 && (current.Tab == TabSearch || current.Tab == 1); } }
        private bool SubscribeVisible { get { return current != null && stack.Count > 0 && current.Header != null && current.Header.Id.Length > 0; } }
        private bool SubscriptionsVisible { get { return current != null && current.Tab == 1 && stack.Count == 0; } }
        private bool ContinueVisible { get { return SubscriptionsVisible && continueCards.Count > 0; } }

        private void ClearTop()
        {
            channelFocus = false; chipFocus = false; continueFocus = false; formatFocus = false;
        }

        private void FocusChannel()
        {
            if (channelIndex < 0 || channelIndex >= channelCards.Count) return;
            UpdateFocusVisual();
            try { channelCards[channelIndex].BringIntoView(); } catch { }
        }

        private void FocusContinue()
        {
            if (continueIndex < 0 || continueIndex >= continueCards.Count) return;
            UpdateFocusVisual();
            try { continueCards[continueIndex].BringIntoView(); } catch { }
        }

        // Down from the tab bar: the topmost control of the page (the channel's buttons, a rail, the chips), else the first video.
        private void EnterContent()
        {
            if (SubscribeVisible) { railFocus = false; headerFocus = true; headerIndex = Math.Max(0, Math.Min(headerIndex, headerTiles.Count - 1)); UpdateFocusVisual(); return; }
            if (ChannelRailVisible) { railFocus = false; channelFocus = true; channelIndex = Math.Max(0, Math.Min(channelIndex, channelCards.Count - 1)); FocusChannel(); return; }
            if (SubscriptionsVisible && filterChipsPanel.Visibility == Visibility.Visible) { railFocus = false; chipFocus = true; chipIndex = Math.Max(0, Math.Min(chipIndex, chipTiles.Count - 1)); UpdateFocusVisual(); return; }
            if (ContinueVisible) { railFocus = false; continueFocus = true; continueIndex = Math.Max(0, Math.Min(continueIndex, continueCards.Count - 1)); FocusContinue(); return; }
            if (current.Count > 0) { railFocus = false; SetFocus(current.Focus); return; }
            if (msg.Action != null && msg.Visibility == Visibility.Visible) { railFocus = false; UpdateFocusVisual(); }   // the Retry / Search pill
        }

        private void FocusTabBar()
        {
            ClearTop();
            headerFocus = false;
            railFocus = true;
            railIndex = activeTab;
            UpdateFocusVisual();
        }

        // Up from the first row of the grid (or from an empty page): the nearest control above it, else the tab bar.
        private void GoUpFromGrid()
        {
            if (formatChipsPanel.Visibility == Visibility.Visible && formatTiles.Count > 0) { formatFocus = true; formatIndex = Math.Min(formatIndex, formatTiles.Count - 1); UpdateFocusVisual(); return; }
            if (SubscribeVisible) { headerFocus = true; UpdateFocusVisual(); return; }
            if (ContinueVisible) { continueFocus = true; continueIndex = Math.Min(continueIndex, continueCards.Count - 1); FocusContinue(); return; }
            if (SubscriptionsVisible && filterChipsPanel.Visibility == Visibility.Visible) { chipFocus = true; chipIndex = Math.Min(chipIndex, chipTiles.Count - 1); UpdateFocusVisual(); return; }
            if (ChannelRailVisible) { channelFocus = true; channelIndex = Math.Min(channelIndex, channelCards.Count - 1); FocusChannel(); return; }
            FocusTabBar();
        }

        private void Navigate(string b)
        {
            if (channelFocus)
            {
                if (b == "DPadLeft") { channelIndex = Math.Max(0, channelIndex - 1); FocusChannel(); }
                else if (b == "DPadRight") { channelIndex = Math.Min(channelCards.Count - 1, channelIndex + 1); FocusChannel(); }
                else if (b == "DPadDown")
                {
                    if (current.Count == 0 && !(SubscriptionsVisible && filterChipsPanel.Visibility == Visibility.Visible)) return;
                    channelFocus = false;
                    if (SubscriptionsVisible && filterChipsPanel.Visibility == Visibility.Visible)
                    { chipFocus = true; chipIndex = Math.Min(chipIndex, chipTiles.Count - 1); UpdateFocusVisual(); }
                    else SetFocus(0);
                }
                else if (b == "DPadUp") FocusTabBar();
                return;
            }
            if (chipFocus)
            {
                if (b == "DPadLeft") { chipIndex = Math.Max(0, chipIndex - 1); UpdateFocusVisual(); }
                else if (b == "DPadRight") { chipIndex = Math.Min(chipTiles.Count - 1, chipIndex + 1); UpdateFocusVisual(); }
                else if (b == "DPadUp")
                {
                    chipFocus = false;
                    if (ChannelRailVisible) { channelFocus = true; channelIndex = Math.Min(channelIndex, channelCards.Count - 1); FocusChannel(); }
                    else FocusTabBar();
                }
                else if (b == "DPadDown")
                {
                    if (!ContinueVisible && current.Count == 0) return;   // never drop focus into an empty grid
                    chipFocus = false;
                    if (ContinueVisible) { continueFocus = true; continueIndex = Math.Min(continueIndex, continueCards.Count - 1); FocusContinue(); }
                    else SetFocus(0);
                }
                return;
            }
            if (continueFocus)
            {
                if (b == "DPadLeft") { continueIndex = Math.Max(0, continueIndex - 1); FocusContinue(); }
                else if (b == "DPadRight") { continueIndex = Math.Min(continueCards.Count - 1, continueIndex + 1); FocusContinue(); }
                else if (b == "DPadUp") { continueFocus = false; chipFocus = true; UpdateFocusVisual(); }
                else if (b == "DPadDown") { if (current.Count == 0) return; continueFocus = false; SetFocus(0); }
                return;
            }
            if (headerFocus)
            {
                if (b == "DPadDown")
                {
                    headerFocus = false;
                    if (formatChipsPanel.Visibility == Visibility.Visible)
                    { formatFocus = true; formatIndex = Math.Min(formatIndex, formatTiles.Count - 1); UpdateFocusVisual(); }
                    else if (current.Count > 0) SetFocus(0);
                    else UpdateFocusVisual();
                }
                else if (b == "DPadLeft" || b == "DPadRight")
                {
                    int max = Math.Max(0, headerTiles.Count - 1);
                    headerIndex = b == "DPadLeft" ? Math.Max(0, headerIndex - 1) : Math.Min(max, headerIndex + 1);
                    UpdateFocusVisual();
                }
                else if (b == "DPadUp") FocusTabBar();
                return;
            }
            if (formatFocus)
            {
                if (b == "DPadLeft") { formatIndex = Math.Max(0, formatIndex - 1); UpdateFocusVisual(); }
                else if (b == "DPadRight") { formatIndex = Math.Min(formatTiles.Count - 1, formatIndex + 1); UpdateFocusVisual(); }
                else if (b == "DPadUp") { formatFocus = false; headerFocus = true; UpdateFocusVisual(); }
                else if (b == "DPadDown") { if (current.Count == 0) return; formatFocus = false; SetFocus(0); }
                return;
            }
            if (railFocus)
            {
                // the tab bar: Left/Right pick a tab (A opens it), Down goes into the page
                if (b == "DPadLeft") railIndex = Math.Max(0, railIndex - 1);
                else if (b == "DPadRight") railIndex = Math.Min(RailNames.Length - 1, railIndex + 1);
                else if (b == "DPadDown") { EnterContent(); return; }
                UpdateFocusVisual();
                return;
            }
            int n = current.Count, i = current.Focus, col = i % cols;
            if (b == "DPadUp" && (n == 0 || i < cols)) { GoUpFromGrid(); return; }
            if (n == 0) return;
            if (b == "DPadLeft") { if (col == 0) return; i--; }
            else if (b == "DPadRight") { if (col < cols - 1 && i + 1 < n) i++; }
            else if (b == "DPadDown") { if (i + cols < n) i += cols; else if (i / cols < (n - 1) / cols) i = n - 1; }
            else if (b == "DPadUp") { if (i - cols >= 0) i -= cols; }
            SetFocus(i);
        }

        // false = close the overlay (only from the tab bar of a top level view)
        private bool Back()
        {
            // B on the channel's Subscribe control returns focus to the videos, not the whole page (as hinted).
            if (headerFocus) { headerFocus = false; if (current.Count > 0) SetFocus(0); else UpdateFocusVisual(); return true; }
            if (stack.Count > 0) { PopView(); return true; }
            if (!railFocus) { FocusTabBar(); return true; }
            return false;
        }

        private void Activate()
        {
            if (channelFocus)
            {
                if (channelIndex >= 0 && channelIndex < current.Channels.Count)
                {
                    ChannelItem c = current.Channels[channelIndex];
                    OpenChannel(c.Id, c.Name, c.Thumbnail);
                }
                return;
            }
            if (headerFocus)
            {
                if (headerIndex == 1 && watchLiveBtn.Visibility == Visibility.Visible) PlayLiveFromChannel();
                else ToggleSubscribedHeader();
                return;
            }
            if (formatFocus) { ApplyFormat(); return; }
            if (continueFocus)
            {
                if (continueIndex >= 0 && continueIndex < current.Continue.Count) Play(current.Continue, continueIndex);
                return;
            }
            if (chipFocus) { ApplyChip(); return; }
            if (railFocus)
            {
                if (railIndex == TabSearch) { OpenSearch(""); return; }
                ActivateTab(railIndex, false);
                railFocus = current.Count == 0;   // a tab with videos hands the focus to them, an empty / loading one keeps it on the bar
                UpdateFocusVisual();
                return;
            }
            if (current.Count == 0)
            {
                if (current.Error.Length > 0) ReloadCurrent();
                else if (msg.Action == msg.SearchBtn && msg.Visibility == Visibility.Visible) OpenSearch(lastQuery);
                return;
            }
            int ix = current.Focus;
            if (current.Kind == CardKind.Channel) { ChannelItem c = current.Channels[ix]; OpenChannel(c.Id, c.Name, c.Thumbnail); }
            else if (current.Kind == CardKind.Playlist) OpenPlaylist(current.Playlists[ix]);
            else Play(current.Videos, ix);
        }

        // Y: channel actions on the Search rail, the search keyboard on Search, otherwise the focused video's menu.
        private void SecondaryAction()
        {
            if (formatFocus) return;   // the format chips have their own A action only
            if (channelFocus)
            {
                if (channelIndex < 0 || channelIndex >= current.Channels.Count) return;
                ChannelItem c = current.Channels[channelIndex];
                List<PickerPanel.Entry> l = new List<PickerPanel.Entry>();
                l.Add(Item("Open channel", 0xE7FD, delegate { OpenChannel(c.Id, c.Name, c.Thumbnail); return true; }));
                l.Add(SubscribeEntry(c.Id, c.Name, c.Thumbnail));
                OpenMenu(c.Name, l);
                return;
            }
            if (headerFocus && current.Header != null)
            {
                ChannelItem h = current.Header;
                List<PickerPanel.Entry> l = new List<PickerPanel.Entry>();
                l.Add(SubscribeEntry(h.Id, h.Name, h.Thumbnail));
                l.Add(Item("Refresh channel", 0xE5D5, delegate { ReloadCurrent(); return true; }));
                OpenMenu(h.Name, l);
                return;
            }
            if (continueFocus)
            {
                if (continueIndex >= 0 && continueIndex < current.Continue.Count)
                {
                    VideoItem v = current.Continue[continueIndex];
                    OpenMenu(v.Title, VideoEntries(v));
                }
                return;
            }
            if (railFocus || (activeTab == TabSearch && stack.Count == 0)) { OpenSearch(lastQuery); return; }
            if (railFocus || chipFocus || current.Kind != CardKind.Video || current.Count == 0) return;
            List<PickerPanel.Entry> entries = VideoEntries(current.Videos[current.Focus]);
            OpenMenu(current.Videos[current.Focus].Title, entries);
        }

        // ------------------------------------------------------------------ menus

        private static PickerPanel.Entry Item(string label, Func<bool> run)
        {
            return Item(label, 0, run);
        }

        private static PickerPanel.Entry Item(string label, int icon, Func<bool> run)
        {
            PickerPanel.Entry e = new PickerPanel.Entry();
            e.Label = label;
            e.Icon = icon;
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
            if (ChipVisible) l.Add(Item(shortsOn ? "Hide Shorts" : "Show Shorts", shortsOn ? 0xE8F5 : 0xE8F4, delegate { ToggleShorts(); return true; }));
            if (current.Header != null && current.Header.Id.Length > 0) l.Add(SubscribeEntry(current.Header.Id, current.Header.Name, current.Header.Thumbnail));
            l.Add(Item("Refresh", 0xE5D5, delegate { ReloadCurrent(); return true; }));
            l.Add(Item("Phone link", 0xE32C, delegate { OpenLinkCard(); return true; }));
            l.Add(Item("Clear Nomad watch history", 0xE872, delegate { ClearNomadHistory(); return true; }));
            l.Add(Item("Close YouTube", 0xE5CD, delegate { return false; }));
            OpenMenu("YouTube", l);
        }

        private List<PickerPanel.Entry> VideoEntries(VideoItem v)
        {
            List<PickerPanel.Entry> l = new List<PickerPanel.Entry>();
            if (v.AuthorId.Length > 0)
            {
                string id = v.AuthorId, name = v.Author;
                l.Add(Item("Go to channel", 0xE7FD, delegate { OpenChannel(id, name, ""); return true; }));
                l.Add(SubscribeEntry(id, name, ""));
            }
            l.Add(Item("Mark as watched", 0xE876, delegate { MarkWatched(v); return true; }));
            return l;
        }

        private PickerPanel.Entry SubscribeEntry(string id, string name, string avatar)
        {
            if (ownIds.Contains(id) || subIds.Contains(id))
                return Item("Unsubscribe from " + name, 0xE15B, delegate { SetSubscribed(id, name, avatar, false); return true; });
            return Item("Subscribe to " + name, 0xE145, delegate { SetSubscribed(id, name, avatar, true); return true; });
        }

        private static readonly string[] Formats = { "Videos", "Shorts", "Live", "Playlists" };

        private static List<VideoItem> FormatVideos(ViewState vs)
        {
            List<VideoItem> src = vs.AllVideos;
            if (vs.Format == "Shorts") return src.FindAll(delegate(VideoItem v) { return v.IsShort; });
            if (vs.Format == "Live") return src.FindAll(delegate(VideoItem v) { return v.IsLive; });
            return src;
        }

        private void ApplyFormat()
        {
            if (current == null || formatIndex < 0 || formatIndex >= Formats.Length) return;
            string f = Formats[formatIndex];
            if (f == "Playlists")
            {
                current.Videos = new List<VideoItem>();
                current.Empty = "Channel playlists aren't available here. Use the Playlists tab for imported ones.";
                current.Loaded = true;
                ShowState(current);
                return;
            }
            current.Format = f;
            current.Videos = FormatVideos(current);
            current.Focus = 0;
            current.Loaded = true;
            ShowState(current);
        }

        private void PlayLiveFromChannel()
        {
            ViewState vs = current;
            if (vs == null) return;
            for (int i = 0; i < vs.AllVideos.Count; i++)
                if (vs.AllVideos[i].IsLive) { Play(vs.AllVideos, i); return; }
            Toast("No live video is on right now.");
        }

        private void ToggleSubscribedHeader()
        {
            if (current == null || current.Header == null || current.Header.Id.Length == 0) return;
            ChannelItem h = current.Header;
            bool on = !(ownIds.Contains(h.Id) || subIds.Contains(h.Id));
            SetSubscribed(h.Id, h.Name, h.Thumbnail, on);
        }

        private void SetSubscribed(string id, string name, string avatar, bool on)
        {
            ChannelItem c = new ChannelItem();
            c.Id = id; c.Name = name; c.Thumbnail = avatar;
            if (on) { ownIds.Add(id); subIds.Add(id); }
            else { ownIds.Remove(id); subIds.Remove(id); }
            Toast(on ? "Subscribed to " + name : "Unsubscribed from " + name);
            UpdateSubscribeUI();
            UpdatePlayerSubscribe();
            string profileAtStart = ProfileScope.CurrentId;
            Task.Run(delegate
            {
                try
                {
                    if (ProfileScope.CurrentId != profileAtStart) return;   // never write into another profile
                    NomadStore.SetSubscribed(c, on);
                    List<ChannelItem> all = Subscriptions.Channels();
                    List<ChannelItem> own = NomadStore.OwnSubscriptions();
                    HashSet<string> ids = new HashSet<string>();
                    HashSet<string> mine = new HashSet<string>();
                    foreach (ChannelItem x in all) ids.Add(x.Id);
                    foreach (ChannelItem x in own) mine.Add(x.Id);
                    if (ProfileScope.CurrentId != profileAtStart) return;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        subIds = ids;
                        ownIds = mine;
                        if (tabStates[1] != null) tabStates[1].Loaded = false;
                        if (tabStates[5] != null) tabStates[5].Loaded = false;
                        UpdateSubscribeUI();
                        UpdatePlayerSubscribe();
                        RefreshChannelRail();
                    }));
                    if (on)
                    {
                        // RefreshChannel waits for any in-flight bulk refresh before saving, so the
                        // newly subscribed channel is picked up even while the feed shows "Updating…".
                        Subscriptions.RefreshChannel(id).ContinueWith(delegate(Task<int> t)
                        {
                            if (t.IsFaulted && t.Exception != null)
                                logger.Error(t.Exception, "SeleneBrowser: refresh new subscription");
                        });
                    }
                }
                catch (Exception ex) { logger.Error(ex, "SeleneBrowser: set YouTube subscription"); }
            });
        }

        private void UpdateSubscribeUI()
        {
            headerTiles.Clear();
            if (!SubscribeVisible)
            {
                subscribeBtn.Visibility = Visibility.Collapsed;
                watchLiveBtn.Visibility = Visibility.Collapsed;
                headerFocus = false;
                UpdateFocusVisual();
                return;
            }
            bool on = ownIds.Contains(current.Header.Id) || subIds.Contains(current.Header.Id);
            Theme.SetLabel(subscribeBtn, on ? "Subscribed" : "Subscribe");
            Theme.SetToggled(subscribeBtn, on);
            subscribeBtn.Visibility = Visibility.Visible;
            headerTiles.Add(subscribeBtn);
            bool live = false;
            foreach (VideoItem v in current.AllVideos) if (v.IsLive) { live = true; break; }
            watchLiveBtn.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            if (live) headerTiles.Add(watchLiveBtn);
            if (headerIndex >= headerTiles.Count) headerIndex = 0;
            UpdateFocusVisual();
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
            if (current != null && current.Focus < current.Videos.Count && current.Videos[current.Focus].Id == v.Id)
                ReplaceCard(current.Focus);
            ViewState feed = tabStates[1];
            if (feed != null && feed.Loaded)
            {
                BuildContinueRail(feed);   // a completed video leaves the Continue Watching rail
                if (feed.Filter == "Unwatched")
                {
                    feed.Videos = FilterFeed(feed, feed.AllVideos);
                    feed.Focus = Math.Min(feed.Focus, Math.Max(0, feed.Videos.Count - 1));
                }
                if (feed == current) ShowState(feed);
            }
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
            vs.Channels = new List<ChannelItem>();
            vs.Focus = 0;
            vs.Continuation = "";
            vs.Loaded = false;
            channelFocus = false; channelIndex = 0;
            vs.Loader = delegate(string c) { return YouTubeApi.Search(query, c); };
            ActivateTab(TabSearch, true);
            railFocus = false;
            FetchSearchChannels(vs, query);
            UpdateFocusVisual();
        }

        // Channels are a separate InnerTube filter: fetch them next to the video results and show a rail above the grid.
        private void FetchSearchChannels(ViewState vs, string query)
        {
            int tok = vs.Token;
            Await(YouTubeApi.SearchChannels(query, ""), delegate(PageResult<ChannelItem> r)
            {
                if (tok != vs.Token || r == null) return;
                if (!r.Ok) return;   // keep the video results; an empty rail is not an error screen
                vs.Channels = r.Items ?? new List<ChannelItem>();
                channelIndex = 0;
                if (vs == current) { BuildChannelRail(vs); UpdateChip(); UpdateFocusVisual(); }
            }, null);
        }

        private void BuildChannelRail(ViewState vs)
        {
            if (railChannels == vs.Channels) return;
            // twelve fit: channels with a new upload come first (stable, so the rest keep their order); channelIndex counts into this list
            if (vs.Tab == 1 && vs.Channels != null)
            {
                List<ChannelItem> ordered = new List<ChannelItem>();
                foreach (ChannelItem c in vs.Channels) if (ChannelHasNew(vs, c.Id)) ordered.Add(c);
                foreach (ChannelItem c in vs.Channels) if (!ordered.Contains(c)) ordered.Add(c);
                vs.Channels = ordered;
            }
            railChannels = vs.Channels;
            channelRail.Children.Clear();
            channelCards.Clear();
            if (vs.Channels == null) return;
            for (int i = 0; i < vs.Channels.Count && i < 12; i++)
            {
                ChannelItem c = vs.Channels[i];
                bool mine = ownIds.Contains(c.Id) || subIds.Contains(c.Id);
                Border b = Cards.ChannelRail(c, Dispatcher, mine, ChannelHasNew(vs, c.Id), vs.Tab == 1);
                channelCards.Add(b);
                channelRail.Children.Add(b);
            }
            if (channelIndex >= channelCards.Count) channelIndex = Math.Max(0, channelCards.Count - 1);
        }

        /// <summary>A channel is "new" when its newest cached upload is under 24 hours old.</summary>
        private bool ChannelHasNew(ViewState vs, string id)
        {
            // Search has no feed of its own: use the (per-profile) cached subscription feed instead.
            List<VideoItem> source = vs.AllVideos;
            if (vs.Tab == TabSearch && tabStates[1] != null) source = tabStates[1].AllVideos;
            long newest = 0;
            foreach (VideoItem v in source)
                if (v.AuthorId == id && v.Published > newest) newest = v.Published;
            if (newest <= 0) return false;
            long age = Fmt.NowUnix() - newest;
            return age >= -300 && age < 86400;
        }

        // Rebuild the Search/Subscriptions rail so its "Following" and "NEW" badges stay current.
        private void RefreshChannelRail()
        {
            if (current == null || stack.Count != 0 || (current.Tab != TabSearch && current.Tab != 1)) return;
            railChannels = null;
            BuildChannelRail(current);
            UpdateChip();
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
            YtFx.RiseIn(contentHost);
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
                if (vs.Tab == -1) vs.AllVideos = r.Items;   // channel page: the source for the format chips
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
                List<VideoItem> target = vs.Tab == -1 ? vs.AllVideos : vs.Videos;
                foreach (VideoItem v in target) have.Add(v.Id);
                int added = 0;
                Annotate(r.Items);
                foreach (VideoItem v in r.Items) if (have.Add(v.Id)) { target.Add(v); added++; }
                if (vs.Tab == -1) vs.Videos = FormatVideos(vs);
                vs.Continuation = added > 0 ? (r.Continuation ?? "") : "";
                if (vs == current) { EnsureBuiltAhead(); RefreshChrome(); MaybeLoadMore(); TrimWindow(); }
            }, delegate(string err) { if (tok == vs.Token) { vs.Loading = false; Fail(vs, err); } });
        }

        private void MaybeLoadMore()
        {
            if (current.Loader != null && current.HasMore && !current.Loading && current.Focus >= current.Count - 2 * cols - 1) LoadMore(current);
        }

        private sealed class SubsBoot
        {
            public List<ChannelItem> Channels = new List<ChannelItem>();
            public List<VideoItem> Videos = new List<VideoItem>();
        }

        private void LoadSubscriptions(ViewState vs)
        {
            int tok = ++vs.Token;
            vs.Loading = vs.Videos.Count == 0;
            if (vs == current) RefreshChrome();
            Work<SubsBoot>(delegate
            {
                SubsBoot b = new SubsBoot();
                b.Channels = Subscriptions.Channels();
                b.Videos = Subscriptions.LoadCached();
                Annotate(b.Videos);
                return b;
            }, delegate(SubsBoot b)
            {
                if (tok != vs.Token) return;
                vs.Loading = false;
                vs.Channels = b.Channels;
                if (b.Videos.Count > 0) ReplaceVideos(vs, b.Videos);
                else { BuildChannelRail(vs); BuildContinueRail(vs); UpdateChip(); }
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
                    if (r != null && r.Videos.Count > 0)
                    {
                        // One jump per completed refresh, and only if a video the grid actually shows is new.
                        HashSet<string> have = new HashSet<string>();
                        foreach (VideoItem v in vs.Videos) have.Add(v.Id);
                        bool fresh = false;
                        foreach (VideoItem v in r.Videos)
                        {
                            if (!shortsOn && v.IsShort) continue;
                            if (!have.Contains(v.Id)) { fresh = true; break; }
                        }
                        ReplaceVideos(vs, r.Videos, fresh);
                    }
                    if (r != null && r.Message.Length > 0)
                    {
                        Toast(r.Message);
                        if (vs.Videos.Count == 0) { vs.Empty = r.Message; if (vs == current) ShowState(vs); }   // the empty page says why, not "subscribe"
                    }
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
            vs.Format = "Videos";
            headerIndex = 0; formatIndex = 0;
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
            YtFx.RiseIn(contentHost);
        }

        private void PopView()
        {
            ViewState prev = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            current.Token++;
            ShowState(prev);
            YtFx.RiseIn(contentHost);
        }

        // ------------------------------------------------------------------ data helpers

        private void ReplaceVideos(ViewState vs, List<VideoItem> items)
        {
            ReplaceVideos(vs, items, false);
        }

        /// <summary>jumpToTop: a refresh brought new videos, so drop the old focus and show the new ones first.</summary>
        private void ReplaceVideos(ViewState vs, List<VideoItem> items, bool jumpToTop)
        {
            string keep = vs.Focus < vs.Videos.Count ? vs.Videos[vs.Focus].Id : null;
            if (vs.Tab == 1)
            {
                // Subscriptions: keep the full feed, then apply the Shorts switch and the chosen filter chip
                vs.AllVideos = items;
                items = FilterFeed(vs, items);
                if (vs == current) { RefreshChannelRail(); BuildContinueRail(vs); }
            }
            vs.Videos = items;
            bool jump = jumpToTop && vs.Focus == 0;   // the user is at the top already: show the new ones; elsewhere stay on the same video
            int ix = jump || keep == null ? -1 : items.FindIndex(delegate(VideoItem x) { return x.Id == keep; });
            vs.Focus = ix >= 0 ? ix : 0;
            vs.Loaded = true;
            if (vs == current) ShowState(vs);   // ShowState snaps the scroll to the focused row (0 = top)
        }

        private static readonly string[] FeedFilters = { "All", "Today", "Unwatched", "Continue" };
        private static readonly string[] FeedFilterLabels = { "All", "Today", "Unwatched", "In progress" };

        private List<VideoItem> FilterFeed(ViewState vs, List<VideoItem> items)
        {
            List<VideoItem> res = items;
            if (!shortsOn) res = res.FindAll(delegate(VideoItem x) { return !x.IsShort; });
            if (vs.Filter == "Today")
            {
                long cut = Fmt.NowUnix() - 86400;
                res = res.FindAll(delegate(VideoItem x) { return x.Published >= cut; });
            }
            else if (vs.Filter == "Unwatched")
                res = res.FindAll(delegate(VideoItem x) { return !WatchedIds.Contains(x.Id); });
            else if (vs.Filter == "Continue")
                res = res.FindAll(delegate(VideoItem x) { return x.WatchProgress > 0 && (x.Duration <= 0 || x.WatchProgress < x.Duration * 0.95); });
            return res;
        }

        private void ApplyChip()
        {
            if (chipIndex >= 0 && chipIndex < FeedFilters.Length)
            {
                ViewState vs = current;
                vs.Filter = FeedFilters[chipIndex];
                if (vs.Tab == 1 && vs.Loaded)
                {
                    vs.Videos = FilterFeed(vs, vs.AllVideos);
                    vs.Focus = Math.Min(vs.Focus, Math.Max(0, vs.Videos.Count - 1));
                    ShowState(vs);
                }
            }
            else ToggleShorts();
            UpdateFocusVisual();
        }

        /// <summary>In-progress videos, newest activity first; this is the Continue Watching rail.</summary>
        private void BuildContinueRail(ViewState vs)
        {
            List<VideoItem> list = new List<VideoItem>();
            try
            {
                foreach (VideoItem h in NomadStore.MergedHistory(120))
                {
                    if (h.IsLive || h.WatchProgress <= 0) continue;
                    if (h.Duration > 0 && h.WatchProgress >= h.Duration * 0.95) continue;
                    list.Add(h);
                    if (list.Count >= 8) break;
                }
            }
            catch { }
            vs.Continue = list;
            railContinue = list;
            continueRail.Children.Clear();
            continueCards.Clear();
            foreach (VideoItem v in list)
            {
                Border b = Cards.ContinueVideo(v, Dispatcher);
                continueCards.Add(b);
                continueRail.Children.Add(b);
            }
            if (continueIndex >= continueCards.Count) continueIndex = Math.Max(0, continueCards.Count - 1);
            if (continueCards.Count == 0 && continueFocus) { continueFocus = false; chipFocus = true; }
            UpdateChip();
            UpdateFocusVisual();
        }

        private void ToggleShorts()
        {
            shortsOn = !shortsOn;
            string val = shortsOn ? "1" : "0";
            Task.Run(delegate { NomadStore.SetPref("yt.shorts", val); CardInfo.Refresh(); });   // home tiles follow the switch
            ViewState vs = tabStates[1];
            if (vs != null && vs.Loaded) ReplaceVideos(vs, vs.AllVideos);
            UpdateChip();
            UpdateFocusVisual();
            Toast(shortsOn ? "Shorts shown" : "Shorts hidden");
        }

        private void UpdateChip()
        {
            Theme.SetLabel(shortsChip, shortsOn ? "Shorts: On" : "Shorts: Off");
            bool subs = SubscriptionsVisible;
            bool channelPage = SubscribeVisible;
            filterChipsPanel.Visibility = subs ? Visibility.Visible : Visibility.Collapsed;
            if (!subs) chipFocus = false;
            formatChipsPanel.Visibility = channelPage ? Visibility.Visible : Visibility.Collapsed;
            if (!channelPage) formatFocus = false;
            for (int i = 0; i < formatTiles.Count; i++)
                Theme.SetSelected(formatTiles[i], i < Formats.Length && current != null && current.Format == Formats[i]);
            for (int i = 0; i < chipTiles.Count; i++)
                Theme.SetSelected(chipTiles[i], i < FeedFilters.Length && current != null && current.Filter == FeedFilters[i]);
            bool rail = ChannelRailVisible;
            channelRailScroll.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
            channelLabel.Visibility = channelRailScroll.Visibility;
            if (!rail) channelFocus = false;
            bool cont = ContinueVisible;
            continueRailScroll.Visibility = cont ? Visibility.Visible : Visibility.Collapsed;
            continueLabel.Visibility = continueRailScroll.Visibility;
            if (!cont) continueFocus = false;
            bool sub = SubscribeVisible;
            subscribeBtn.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
            if (!sub) headerFocus = false;
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

        private void UpdateProfileLabel()
        {
            profileText.Text = ProfileScope.Available
                ? (string.IsNullOrEmpty(ProfileScope.CurrentName) ? "Your channels" : ProfileScope.CurrentName + "'s channels")
                : "Shared profile";
        }

        /// <summary>A different "Who's playing?" profile signed in: drop every cached view and start over on its data.</summary>
        private void SwitchProfile()
        {
            Subscriptions.Invalidate();
            knownProfileId = ProfileScope.CurrentId;
            Task.Run(delegate { try { WatchedIds.Rebuild(true); } catch { } });
            initialized = false;
            stack.Clear();
            for (int i = 0; i < tabStates.Length; i++) tabStates[i] = null;
            suggestCache.Clear();
            searchHist = new List<string>();
            subIds = new HashSet<string>();
            ownIds = new HashSet<string>();
            lastQuery = "";
            ClearTop();
            railFocus = true; railIndex = 0;
            current = TabState(0);
            ShowState(current);
            UpdateProfileLabel();
            CardInfo.Refresh();
        }

        private void InitialLoad()
        {
            initialized = true;
            UpdateProfileLabel();
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
                // The first video can become ready before this load finishes: repaint every subscribe
                // control now that the real subscription sets are in place, not just the browse grid.
                UpdateSubscribeUI();
                UpdatePlayerSubscribe();
                // (the watched set is complete only now: anything drawn before - the cached feed - is drawn again)
                ActivateTab(b.Channels.Count > 0 ? 1 : 0, true);
                if (current != null) ShowState(current);
            }, delegate(string err) { ActivateTab(0, true); });
        }

        // ------------------------------------------------------------------ grid rendering

        private void ShowState(ViewState vs)
        {
            current = vs;
            BuildChannelRail(vs);   // rail visibility in UpdateChip depends on the rebuilt cards
            UpdateChip();
            ResetSpacer();   // drop any stale spacer before the children are cleared
            gridPanel.Children.Clear();
            cards.Clear();
            focusedBorder = null;
            if (vs.Focus >= vs.Count) vs.Focus = Math.Max(0, vs.Count - 1);
            EnsureBuiltAhead();
            titleText.Text = vs.Title;
            ShowHeader(vs);
            RefreshChrome();
            gridScroll.UpdateLayout();
            scroller.Snap(0);
            scroller.Snap(scroller.Reveal((vs.Focus / cols) * Cards.CellH, Cards.CellH));
            UpdateFocusVisual();
            TrimWindow();
        }

        private static string HeaderSub(ChannelItem c)
        {
            if (c == null) return "";
            string s = c.Subscribers;
            if (!string.IsNullOrEmpty(c.VideoCount)) s = s.Length > 0 ? s + "  \u00B7  " + c.VideoCount : c.VideoCount;
            return s;
        }

        private void ShowHeader(ViewState vs)
        {
            subText.Text = HeaderSub(vs.Header);
            subText.Visibility = subText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            headDesc.Text = vs.Header != null ? vs.Header.Description : "";
            headDesc.Visibility = headDesc.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            headAvatar.Visibility = vs.Header != null ? Visibility.Visible : Visibility.Collapsed;
            headAvatar.Fill = Theme.Key;
            if (vs.Header != null)
                ThumbCache.Load(vs.Header.Thumbnail, 96, Dispatcher, delegate(System.Windows.Media.Imaging.BitmapImage bi)
                {
                    if (current == vs) headAvatar.Fill = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                });
            UpdateSubscribeUI();
        }

        // Status line, spinner / error / empty message.
        private void RefreshChrome()
        {
            ViewState vs = current;
            statusText.Text = vs.Status.Length > 0 ? vs.Status : (vs.Loading && vs.Count > 0 ? "Loading more\u2026" : "");
            if (vs.Header != null && subText.Text != HeaderSub(vs.Header))
            {
                subText.Text = HeaderSub(vs.Header);
                subText.Visibility = subText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (vs.Title != titleText.Text) titleText.Text = vs.Title;
            // Subscriptions: pulsing placeholders until the first feed arrives; once cached videos are on screen,
            // keep them visible and use the small header spinner instead of covering the grid.
            bool subs = vs.Tab == 1 && stack.Count == 0;
            bool refreshing = subs && Subscriptions.IsRefreshing;
            statusSpinner.Visibility = refreshing && vs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool skeletonOn = subs && vs.Count == 0 && vs.Error.Length == 0 && (vs.Loading || !vs.Loaded || refreshing);
            if (skeletonOn) skeleton.Show(); else skeleton.Hide();
            if (vs.Count > 0 || skeletonOn)
            {
                msg.Visibility = Visibility.Collapsed;
                PaintMsgAction();
                UpdateHints();
                return;
            }
            msg.Visibility = Visibility.Visible;
            if (vs.Error.Length > 0) msg.ShowMessage(ErrorIcon, vs.Error, "", msg.Retry);
            else if (vs.Loading || !vs.Loaded) msg.ShowLoading("Loading…");
            else
            {
                // a Search page with no query yet: the pill opens the keyboard
                bool needsQuery = vs.Tab == TabSearch && stack.Count == 0 && vs.Loader == null;
                msg.ShowMessage(vs.Tab >= 0 && vs.Tab < RailIcons.Length ? RailIcons[vs.Tab] : 0xE04A, vs.Empty, "", needsQuery ? msg.SearchBtn : null);
            }
            PaintMsgAction();
            UpdateHints();
        }

        private static int ErrorIcon { get { return Theme.HasIcon(0xE2C1) ? 0xE2C1 : 0xE000; } }   // cloud_off, else error

        /// <summary>The Retry / Search pill takes the focus when the page has nothing else to focus and the bar isn't holding it.</summary>
        private void PaintMsgAction()
        {
            bool on = msg.Visibility == Visibility.Visible && msg.Action != null && current != null && current.Count == 0 && GridLevel;
            Theme.SetFocused(msg.Retry, on && msg.Action == msg.Retry);
            Theme.SetFocused(msg.SearchBtn, on && msg.Action == msg.SearchBtn);
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

        // Shorter lookahead: 2 rows (at least 8 cards) instead of a flat 16.
        private void EnsureBuiltAhead()
        {
            if (current == null) return;
            EnsureBuilt(current.Focus + Math.Max(8, 2 * cols));
        }

        private void ResetSpacer()
        {
            gridPanel.Children.Remove(gridSpacer);
            gridSpacer.Height = 0;
            gridSpacer.Width = cols * Cards.CellW;
            gridSpacer.Visibility = Visibility.Collapsed;
        }

        // Only the cards above the window are Collapsed; the top spacer takes up their rows so the
        // WrapPanel keeps its full height (and ScrollableHeight / scroll targets stay correct). Cards
        // from `first` onward stay Visible - lookahead is short, so that is only a few rows. Never
        // collapse trailing cards: without a bottom spacer that would shorten the panel.
        private void TrimWindow()
        {
            if (current == null || cards.Count == 0) return;
            int c = Math.Max(1, cols);   // guard against 0
            int first = Math.Max(0, current.Focus - 2 * c);
            first -= first % c;          // align to a row boundary
            int keepFrom = first - 4 * c;   // pictures stay for four more rows above the window (scrolling back is instant), then go
            for (int i = 0; i < cards.Count; i++)
            {
                Visibility want = i >= first ? Visibility.Visible : Visibility.Collapsed;
                if (cards[i].Visibility != want) cards[i].Visibility = want;
                ThumbHandle th = cards[i].Tag as ThumbHandle;
                if (th != null) { if (i < keepFrom) th.Release(); else th.Restore(); }
            }
            if (first > 0)
            {
                gridSpacer.Height = (first / c) * Cards.CellH;
                gridSpacer.Width = c * Cards.CellW;
                if (gridPanel.Children.Count == 0 || gridPanel.Children[0] != gridSpacer)
                {
                    gridPanel.Children.Remove(gridSpacer);
                    gridPanel.Children.Insert(0, gridSpacer);
                }
                gridSpacer.Visibility = Visibility.Visible;
            }
            else
            {
                gridSpacer.Height = 0;
                gridSpacer.Width = c * Cards.CellW;
                gridSpacer.Visibility = Visibility.Collapsed;
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
            int at = gridPanel.Children.IndexOf(cards[i]);   // the spacer may occupy child 0
            if (at < 0) return;
            gridPanel.Children.RemoveAt(at);   // (the indexer setter throws "index already in use")
            gridPanel.Children.Insert(at, fresh);
            cards[i] = fresh;
            if (focusedBorder != null) focusedBorder = null;
            UpdateFocusVisual();
            TrimWindow();   // preserve the new card's place in the visibility window
        }

        private void SetFocus(int i)
        {
            if (current.Count == 0) return;
            i = Math.Max(0, Math.Min(i, current.Count - 1));
            current.Focus = i;
            EnsureBuiltAhead();
            UpdateFocusVisual();
            scroller.To(scroller.Reveal((i / cols) * Cards.CellH, Cards.CellH));
            MaybeLoadMore();
            TrimWindow();
        }

        private void UpdateFocusVisual()
        {
            if (focusedBorder != null) Theme.SetFocused(focusedBorder, false);
            focusedBorder = null;
            for (int i = 0; i < channelCards.Count; i++) Theme.SetFocused(channelCards[i], channelFocus && i == channelIndex);
            for (int i = 0; i < continueCards.Count; i++) Theme.SetFocused(continueCards[i], continueFocus && i == continueIndex);
            for (int i = 0; i < chipTiles.Count; i++) Theme.SetFocused(chipTiles[i], chipFocus && i == chipIndex);
            for (int i = 0; i < formatTiles.Count; i++) Theme.SetFocused(formatTiles[i], formatFocus && i == formatIndex);
            for (int i = 0; i < headerTiles.Count; i++) Theme.SetFocused(headerTiles[i], headerFocus && !railFocus && i == headerIndex);
            if (GridLevel && current != null && current.Focus < cards.Count)
            {
                focusedBorder = cards[current.Focus];
                Theme.SetFocused(focusedBorder, true);
            }
            tabBar.Paint(activeTab, railFocus, railIndex);
            PaintMsgAction();
            UpdateArt();
            UpdateHints();
        }

        // ------------------------------------------------------------------ ambient art

        // The card the pad is on, whose picture belongs in the backdrop (null on the tab bar, the chips, the header buttons...).
        private Border ArtCard()
        {
            if (railFocus || chipFocus || headerFocus || formatFocus || keyboard.IsOpen) return null;
            if (channelFocus) return channelIndex >= 0 && channelIndex < channelCards.Count ? channelCards[channelIndex] : null;
            if (continueFocus) return continueIndex >= 0 && continueIndex < continueCards.Count ? continueCards[continueIndex] : null;
            return focusedBorder;
        }

        private void UpdateArt()
        {
            // The focused video's thumbnail no longer tints the backdrop (the user preferred the theme's own look).
            // The Atmosphere.SetArt plumbing stays; flip this switch to bring it back.
            if (!AmbientArt) return;
            if (!shown) return;
            Border want = ArtCard();
            if (ReferenceEquals(want, artCard)) return;
            artCard = want;
            artTimer.Stop();
            artTimer.Start();   // ~150 ms of quiet before the backdrop follows (a fast scroll only paints where it stops)
        }

        private void ApplyArt()
        {
            if (artWaiting != null) { artWaiting.Arrived = null; artWaiting = null; }
            Border card = artCard;
            if (card == null) { atmosphere.SetArt(null); return; }
            CardArt art = CardArt.Of(card);
            if (art == null) { atmosphere.SetArt(null); return; }
            ImageSource src = art.Source;
            if (src != null) { atmosphere.SetArt(src); return; }
            // not downloaded yet: the previous art stays until it arrives
            artWaiting = art;
            art.Arrived = delegate(ImageSource arrived)
            {
                if (ReferenceEquals(artCard, card)) atmosphere.SetArt(arrived);
                art.Arrived = null;
                if (ReferenceEquals(artWaiting, art)) artWaiting = null;
            };
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
            hintText.Show(h);
            pHint.Show(h);
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Hints"));
        }

        private void UpdateHints()
        {
            const string Tabs = "LB/RB Switch Tab";
            if (linkCard.IsOpen) SetHints("B Close    X New address    Y Turn off");
            else if (keyboard.IsOpen) SetHints(OnScreenKeyboard.Hints);
            else if (playing) SetHints(PlayerHints());
            else if (menu.IsOpen) SetHints("D-pad Select    A Choose    B Close");
            else if (channelFocus) SetHints("A Open Channel    Y Options    Left/Right Browse    Down Next    Up Tabs    B Tabs    " + Tabs);
            else if (continueFocus) SetHints("A Play    Y Options    Left/Right Browse    Up Filters    Down Videos    B Tabs    " + Tabs);
            else if (chipFocus) SetHints("A Filter    Left/Right Choose    Up " + (ChannelRailVisible ? "Channels" : "Tabs") + "    Down Next    B Tabs    " + Tabs);
            else if (headerFocus) SetHints("A Choose    Left/Right Buttons    Y Menu    Down Formats    Up Tabs    B Videos    " + Tabs);
            else if (formatFocus) SetHints("A Choose Format    Left/Right Formats    Up Buttons    Down Videos    B " + (stack.Count > 0 ? "Back" : "Tabs") + "    " + Tabs);
            else if (railFocus) SetHints("Left/Right Tabs    A Open    Down Content    B " + (stack.Count > 0 ? "Back" : "Close YouTube") + "    X Refresh    Y Search    Start Menu");
            else if (current != null && current.Count == 0) SetHints((msg.Action != null ? "A " + (msg.Action == msg.Retry ? "Retry" : "Search") + "    " : "") + "Up Tabs    B " + (stack.Count > 0 ? "Back" : "Tabs") + "    " + Tabs + "    X Refresh    Start Menu");
            else if (activeTab == TabSearch && stack.Count == 0) SetHints("D-pad Move    A Play    B Tabs    Y Search    " + Tabs + "    LT/RT Page    X Refresh    Start Menu");
            else SetHints("D-pad Move    A Select    B " + (stack.Count > 0 ? "Back" : "Tabs") + "    Y Menu    " + Tabs + "    LT/RT Page    X Refresh    Start Menu");
        }

        // ------------------------------------------------------------------ layout

        private UIElement BuildLayout()
        {
            // a 1920 x 1080 design canvas, scaled by the Viewbox; all the numbers here are the home screen's
            Grid design = new Grid { Width = 1920, Height = 1080 };
            design.RowDefinitions.Add(new RowDefinition { Height = new GridLength(110) });                    // 0 the top bar
            design.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 1 the page

            // top bar: name + profile (left), LB / tabs / RB (centre), clock (right)
            tabBar = new YtTopBar(RailNames, RailIcons, profileText);
            design.Children.Add(tabBar);

            // content: header, channel rail, chips, continue rail, grid
            Grid content = new Grid();
            contentHost = content;
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 0 header
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 1 channel rail
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 2 filter / format chips
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 3 continue watching
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 4 video grid
            Grid.SetRow(content, 1);

            // header: the page's title and its line of facts (the home hero's text), the channel's buttons on the right
            Grid head = new Grid { Margin = new Thickness(64, 2, 64, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(headAvatar);
            StackPanel titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(titleText);
            titles.Children.Add(subText);
            headDesc.TextWrapping = TextWrapping.Wrap;
            headDesc.MaxWidth = 1000;
            headDesc.MaxHeight = 54;
            headDesc.Margin = new Thickness(0, 4, 0, 0);
            headDesc.Visibility = Visibility.Collapsed;
            titles.Children.Add(headDesc);
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            StackPanel status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            statusSpinner.Margin = new Thickness(0, 0, 12, 0);
            statusSpinner.Visibility = Visibility.Collapsed;
            status.Children.Add(statusSpinner);
            status.Children.Add(statusText);
            foreach (Border pill in new Border[] { subscribeBtn, watchLiveBtn })
            {
                pill.Padding = new Thickness(24, 10, 26, 11);   // 52 px high, 22 pt
                pill.Visibility = Visibility.Collapsed;
            }
            subscribeBtn.Margin = new Thickness(24, 0, 0, 0);
            watchLiveBtn.Margin = new Thickness(12, 0, 0, 0);
            status.Children.Add(subscribeBtn);
            status.Children.Add(watchLiveBtn);
            Grid.SetColumn(status, 2);
            head.Children.Add(status);
            content.Children.Add(head);

            // chips (52 px pills): the channel page's formats, the Subscriptions filters + the Shorts switch
            for (int i = 0; i < Formats.Length; i++)
            {
                Border chip = Theme.Tile(Formats[i], 21, 0, Look.Pill);
                chip.Padding = new Thickness(22, 10, 24, 11);
                chip.Margin = new Thickness(0, 2, 12, 6);
                formatTiles.Add(chip);
                formatChipsPanel.Children.Add(chip);
            }
            formatChipsPanel.Margin = new Thickness(64, 0, 0, 0);
            formatChipsPanel.Visibility = Visibility.Collapsed;
            Grid.SetRow(formatChipsPanel, 2);
            content.Children.Add(formatChipsPanel);

            for (int i = 0; i < FeedFilters.Length; i++)
            {
                Border chip = Theme.Tile(FeedFilterLabels[i], 21, 0, Look.Pill);
                chip.Padding = new Thickness(22, 10, 24, 11);
                chip.Margin = new Thickness(0, 2, 12, 6);
                chipTiles.Add(chip);
                filterChipsPanel.Children.Add(chip);
            }
            shortsChip.Padding = new Thickness(22, 10, 26, 11);
            shortsChip.Margin = new Thickness(12, 2, 0, 6);
            shortsChip.HorizontalAlignment = HorizontalAlignment.Left;
            chipTiles.Add(shortsChip);
            filterChipsPanel.Children.Add(shortsChip);
            filterChipsPanel.Margin = new Thickness(64, 0, 0, 0);
            filterChipsPanel.Visibility = Visibility.Collapsed;
            Grid.SetRow(filterChipsPanel, 2);
            content.Children.Add(filterChipsPanel);

            // the rails: a heading over a row of cards that scrolls off the page's right edge (a card's face starts at x = 64: 12 + 6 in from the rail's 46)
            channelLabel.Margin = new Thickness(64, 4, 0, 0);
            channelLabel.Visibility = Visibility.Collapsed;
            channelRailScroll.Content = channelRail;
            channelRailScroll.HorizontalAlignment = HorizontalAlignment.Stretch;
            channelRailScroll.Margin = new Thickness(46, 0, 0, 2);
            channelRailScroll.Visibility = Visibility.Collapsed;
            StackPanel channelBlock = new StackPanel();
            channelBlock.Children.Add(channelLabel);
            channelBlock.Children.Add(channelRailScroll);
            Grid.SetRow(channelBlock, 1);
            content.Children.Add(channelBlock);

            continueLabel.Margin = new Thickness(64, 4, 0, 0);
            continueLabel.Visibility = Visibility.Collapsed;
            continueRailScroll.Content = continueRail;
            continueRailScroll.HorizontalAlignment = HorizontalAlignment.Stretch;
            continueRailScroll.Margin = new Thickness(46, 0, 0, 2);
            continueRailScroll.Visibility = Visibility.Collapsed;
            StackPanel continueBlock = new StackPanel();
            continueBlock.Children.Add(continueLabel);
            continueBlock.Children.Add(continueRailScroll);
            Grid.SetRow(continueBlock, 3);
            content.Children.Add(continueBlock);

            gridScroll.Content = gridPanel;
            Grid.SetRow(gridScroll, 4);
            content.Children.Add(gridScroll);
            Grid.SetRow(skeleton, 4);
            content.Children.Add(skeleton);
            Grid.SetRow(msg, 4);
            content.Children.Add(msg);
            design.Children.Add(content);

            // passive pad-rest chip while browsing: top centre under the bar, over the content but under the hints / menu / toast.
            browseRestChip.View.Margin = new Thickness(0, 114, 0, 0);
            Grid.SetRowSpan(browseRestChip.View, 2);
            design.Children.Add(browseRestChip.View);

            // the button hints: 64 px side margins, 40 px off the bottom, over the backdrop's bottom shade
            // a dock under the hints (the home screen's bottom shade): cards scrolling behind them stay out of the words' way
            var hintDock = new Border
            {
                Height = 190, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
                Background = Theme.Get("SeleneScrimBrush", "#FF000000")
            };
            var dockFade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            dockFade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            dockFade.GradientStops.Add(new GradientStop(Color.FromArgb(235, 0, 0, 0), 0.7));
            dockFade.GradientStops.Add(new GradientStop(Color.FromArgb(250, 0, 0, 0), 1));
            dockFade.Freeze();
            hintDock.OpacityMask = dockFade;
            Grid.SetRowSpan(hintDock, 2);
            Grid.SetColumnSpan(hintDock, 2);
            design.Children.Add(hintDock);
            // the grid's top edge fades out instead of cutting a half-scrolled row off under the rails
            var gridFade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gridFade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            gridFade.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.05));
            gridFade.Freeze();
            gridScroll.OpacityMask = gridFade;
            hintText.Margin = new Thickness(64, 0, 64, 40);
            hintText.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRowSpan(hintText, 2);
            design.Children.Add(hintText);

            Grid.SetRowSpan(menu, 2);
            design.Children.Add(menu);
            Grid.SetRowSpan(toast, 2);
            design.Children.Add(toast);
            Grid.SetRowSpan(linkCard, 2);
            design.Children.Add(linkCard);

            root.Children.Add(atmosphere);   // the theme's backdrop (+ the focused card's art) under everything
            browseBox.Child = design;
            root.Children.Add(browseBox);
            root.Children.Add(BuildPlayerLayer());
            root.Children.Add(keyboard);
            return root;
        }
    }
}
