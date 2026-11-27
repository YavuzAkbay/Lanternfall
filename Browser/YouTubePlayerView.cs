// YouTubePlayerView.cs - player half of YouTubeControl: full-bleed CefView + WPF controls overlay.
//   Player pad: A play/pause (or open the focused button), Left/Right seek 10 s (held: 30 s), LT/RT 60 s,
//   Down shows the bar then enters the button row (Left/Right move, A opens a picker, Up/B leave), X captions,
//   Y "Up next" panel, LB/RB previous/next in the list, B hide bar then leave, Start player menu.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SeleneBrowser
{
    public partial class YouTubeControl
    {
        private enum Stage { Loading, Playing, Error }

        private sealed class PState
        {
            public double T, D, Buf, Rate = 1;
            public bool Paused, Buffering, Ended, Ready, Live;
            public double Behind, WStart;   // live: seconds behind the live edge, start of the seekable (DVR) window
            public int Height;
            public string Dbg = "";
            public string Quality = "auto", Caption = "", Audio = "", Error = "";
            public List<int> Qualities = new List<int>();
            public List<KeyValuePair<string, string>> Captions = new List<KeyValuePair<string, string>>();
            public List<KeyValuePair<string, string>> Audios = new List<KeyValuePair<string, string>>();
        }

        // State read from the page: one JSON object per poll (adds the buffered end next to window.nomad.state()).
        private const string PollJs = "(function(){try{var s=JSON.parse(window.nomad.state()),v=document.querySelector('video'),b=0;"
            + "if(v&&v.buffered.length){var t=v.currentTime;for(var i=0;i<v.buffered.length;i++){if(v.buffered.start(i)<=t+0.5&&v.buffered.end(i)>=t)b=v.buffered.end(i);}}"
            + "s.b=b;return JSON.stringify(s);}catch(e){return null;}})()";
        private long lastDbgLogAt = -100000;
        private int loggedHeight;
        private static readonly double[] Rates = { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 };
        private static readonly string[] ButtonNames = { "Quality", "Speed", "Captions", "Audio", "Up next", "Subscribe" };
        private const double SeekW = 1800;

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Grid playerLayer = new Grid { Background = Brushes.Black, Visibility = Visibility.Collapsed };
        private readonly Grid pvHost = new Grid();
        private readonly PickerPanel picker = new PickerPanel();
        private readonly Toaster playerToast = new Toaster();
        private readonly DispatcherTimer pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer countTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private static readonly int[] ButtonIcons = { 0xE8B8, 0xE068, 0xE01C, 0xE3A1, 0xE05F, 0xE145 };   // settings, slow_motion_video, closed_caption, audiotrack, playlist_play, add
        private readonly TextBlock pTitle = Theme.Title("", 40);
        private readonly TextBlock pChannel = Theme.Text("", 24, Theme.Ink60);
        private readonly TextBlock pTime = Theme.Text("0:00 / 0:00", 24, Theme.Ink);
        private readonly Border liveBadge = new Border();
        private readonly TextBlock liveBadgeText = Theme.Text("LIVE", 20, Brushes.White);
        private readonly HintBar pHint = new HintBar(19, HorizontalAlignment.Right);
        private readonly TextBlock stageText = Theme.Title("", 32);
        private readonly HintBar stageSub = new HintBar(20, HorizontalAlignment.Center);
        private readonly TextBlock countText = Theme.Text("", 24, Theme.Ink);
        private readonly TextBlock relStatus = Theme.Text("", 22, Theme.Ink60);
        private readonly Border pausedBadge = new Border();
        private readonly Border countBox = new Border();
        private readonly Border topBar = new Border(), bottomBar = new Border(), relatedPanel = new Border();
        private readonly Border seekBuf = new Border(), seekFill = new Border();
        private readonly Ellipse seekKnob = new Ellipse { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Spinner bufSpinner = new Spinner(90);
        private readonly Spinner stageSpinner = new Spinner(90);
        private readonly StackPanel stageBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel relList = new StackPanel();
        private readonly ScrollViewer relScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        private readonly List<Border> buttons = new List<Border>();
        private readonly List<Border> relRows = new List<Border>();
        private readonly StackPanel buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 0, 0, 0) };
        private readonly StackPanel playerTimeRow = new StackPanel { Orientation = Orientation.Horizontal };
        private long quietUntil;
        private bool quiet;

        private IPageView pv;   // Edge WebView2 when available (H.264, HLS), CEF otherwise
        private PlayerOverlayWindow overlayWin;
        private string pvUrl = "";
        private bool liveMode;  // the current video is a live stream: no resume position, no watched progress
        private bool playing, barVisible, rowFocused, relatedOpen, counting, polling, endedHandled, prefsApplied;
        private Stage stage = Stage.Loading;
        private string stageError = "";
        private VideoItem nowPlaying;
        private List<VideoItem> queue = new List<VideoItem>();
        private List<VideoItem> related = new List<VideoItem>();
        private int qi, playToken, clickedToken, rowIndex, relIndex, countLeft, seekRun;
        private int pageReqToken, loadedToken;   // which video's player page was requested / has finished loading
        private PState ps;
        private double dispT;
        private long seekHoldUntil, lastInput, lastSeekPress, loadStartedAt, startCheckAt;
        private int startCheckToken = -1, startCheckRound;
        private long lastAdvanceAt;            // when the playback time last moved (an error is final only if it has not for 8 s)
        private double lastPollT = -1, resumeFrom;
        private string resumeId;               // a retry of this video starts at resumeFrom, not at the saved position
        private int autoRetries;               // fresh-manifest restarts done by the poll for this video (one allowed)

        // ------------------------------------------------------------------ layout

        private UIElement BuildPlayerLayer()
        {
            playerLayer.Children.Add(pvHost);
            Grid d = new Grid { Width = 1920, Height = 1080 };

            StackPanel top = new StackPanel { Margin = new Thickness(60, 36, 60, 0) };
            pTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            top.Children.Add(pTitle);
            top.Children.Add(pChannel);
            topBar.Height = 190;
            topBar.VerticalAlignment = VerticalAlignment.Top;
            topBar.Background = Fade(true);
            topBar.Child = top;
            d.Children.Add(topBar);

            // the seek bar: an ink track, what's buffered a little brighter, played in the theme's accent with a glowing knob
            Grid seek = new Grid { Width = SeekW, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 16) };
            seek.Children.Add(new Border
            {
                Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Background = Theme.InkA(0.22)
            });
            seekBuf.Height = 8; seekBuf.CornerRadius = new CornerRadius(4); seekBuf.HorizontalAlignment = HorizontalAlignment.Left;
            seekBuf.VerticalAlignment = VerticalAlignment.Center; seekBuf.Background = Theme.InkA(0.42);
            seekFill.Height = 8; seekFill.CornerRadius = new CornerRadius(4); seekFill.HorizontalAlignment = HorizontalAlignment.Left;
            seekFill.VerticalAlignment = VerticalAlignment.Center; seekFill.Background = Theme.Accent;
            seekKnob.Fill = Theme.Accent;
            seekKnob.Stroke = Theme.OnAccent;
            seekKnob.StrokeThickness = 2;
            seekKnob.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 16, Opacity = 0.6, Color = Theme.FocusColor };
            seek.Children.Add(seekBuf);
            seek.Children.Add(seekFill);
            seek.Children.Add(seekKnob);

            for (int i = 0; i < ButtonNames.Length; i++)
            {
                Border t = Theme.Tile(ButtonNames[i], 22, ButtonIcons[i], Look.Pill);
                t.Padding = new Thickness(22, 10, 24, 11);
                buttons.Add(t);
                buttonRow.Children.Add(t);
            }
            StackPanel bottom = new StackPanel { Margin = new Thickness(64, 110, 64, 30) };
            liveBadgeText.FontWeight = FontWeights.Bold;
            liveBadge.Child = liveBadgeText;
            liveBadge.CornerRadius = new CornerRadius(7);
            liveBadge.Padding = new Thickness(12, 2, 12, 3);
            liveBadge.Margin = new Thickness(0, 0, 14, 0);
            liveBadge.VerticalAlignment = VerticalAlignment.Center;
            liveBadge.Visibility = Visibility.Collapsed;
            playerTimeRow.Children.Add(liveBadge);
            playerTimeRow.Children.Add(pTime);
            bottom.Children.Add(playerTimeRow);
            bottom.Children.Add(seek);
            bottom.Children.Add(buttonRow);
            pHint.Margin = new Thickness(0, 14, 0, 0);
            bottom.Children.Add(pHint);
            bottomBar.VerticalAlignment = VerticalAlignment.Bottom;
            bottomBar.Background = Fade(false);
            bottomBar.Child = bottom;
            d.Children.Add(bottomBar);

            // paused: a glass disc with the pause mark (the word when the icon font is missing)
            pausedBadge.Background = Theme.GlassA(0.82);
            Theme.Edge(pausedBadge);
            pausedBadge.HorizontalAlignment = HorizontalAlignment.Center;
            pausedBadge.VerticalAlignment = VerticalAlignment.Center;
            FrameworkElement pauseMark = Theme.Icon(0xE034, 64, Theme.Ink);   // pause
            if (pauseMark != null)
            {
                pausedBadge.Width = pausedBadge.Height = 132;
                pausedBadge.CornerRadius = new CornerRadius(66);
                pausedBadge.Child = pauseMark;
            }
            else
            {
                pausedBadge.CornerRadius = new CornerRadius(36);
                pausedBadge.Padding = new Thickness(40, 14, 40, 16);
                pausedBadge.Child = Theme.Title("Paused", 40);
            }
            pausedBadge.Visibility = Visibility.Collapsed;
            pausedBadge.IsHitTestVisible = false;
            d.Children.Add(pausedBadge);

            bufSpinner.HorizontalAlignment = HorizontalAlignment.Center;
            bufSpinner.VerticalAlignment = VerticalAlignment.Center;
            bufSpinner.Visibility = Visibility.Collapsed;
            d.Children.Add(bufSpinner);

            stageBox.Children.Add(stageSpinner);
            stageText.TextWrapping = TextWrapping.Wrap;
            stageText.TextAlignment = TextAlignment.Center;
            stageText.MaxWidth = 1100;
            stageText.Margin = new Thickness(0, 20, 0, 8);
            stageSub.HorizontalAlignment = HorizontalAlignment.Center;
            stageBox.Children.Add(stageText);
            stageBox.Children.Add(stageSub);
            d.Children.Add(stageBox);

            Theme.Glass(countBox, 26);
            countBox.Padding = new Thickness(30, 12, 30, 14);
            countBox.HorizontalAlignment = HorizontalAlignment.Center;
            countBox.VerticalAlignment = VerticalAlignment.Bottom;
            countBox.Margin = new Thickness(0, 0, 0, 340);
            countBox.Child = countText;
            countBox.Visibility = Visibility.Collapsed;
            d.Children.Add(countBox);

            StackPanel relStack = new StackPanel();
            TextBlock relHead = Theme.Title("Up next", 30);
            relHead.Margin = new Thickness(18, 8, 0, 10);
            relStack.Children.Add(relHead);
            relStatus.Margin = new Thickness(18, 8, 0, 0);
            relStack.Children.Add(relStatus);
            relScroll.Content = relList;
            relScroll.MaxHeight = 820;
            relStack.Children.Add(relScroll);
            relatedPanel.Width = 680;
            relatedPanel.Margin = new Thickness(0, 50, 50, 100);   // (the hints stay readable under it)
            relatedPanel.Padding = new Thickness(16, 20, 16, 16);
            Theme.Glass(relatedPanel, 28);
            relatedPanel.HorizontalAlignment = HorizontalAlignment.Right;
            relatedPanel.VerticalAlignment = VerticalAlignment.Stretch;
            relatedPanel.Child = relStack;
            relatedPanel.Visibility = Visibility.Collapsed;
            d.Children.Add(relatedPanel);

            // passive pad-rest chip: the player WebView2 is windowed, so this must live in the chrome grid that
            // PlayerOverlayWindow draws in its own click-through window (just below the 190 px top bar).
            playerRestChip.View.Margin = new Thickness(0, 200, 0, 0);
            d.Children.Add(playerRestChip.View);

            d.Children.Add(picker);
            d.Children.Add(playerToast);
            // the controls sit in their own window over the player: the page is a windowed WebView2 (see PlayerOverlayWindow)
            overlayWin = new PlayerOverlayWindow(playerLayer, new Viewbox { Stretch = Stretch.Uniform, Child = d, IsHitTestVisible = false });

            pollTimer.Tick += delegate { Guard("poll", PollPlayer); };
            saveTimer.Tick += delegate { Guard("save", SaveProgress); };
            hideTimer.Tick += delegate { Guard("hide", OnHideTick); };
            countTimer.Tick += delegate { Guard("countdown", OnCountTick); };
            return playerLayer;
        }

        private static Brush Fade(bool fromTop)
        {
            LinearGradientBrush g = new LinearGradientBrush();
            g.StartPoint = new Point(0, 0);
            g.EndPoint = new Point(0, 1);
            Color scrim = Theme.ScrimColor;
            Color solid = Color.FromArgb(0xE0, scrim.R, scrim.G, scrim.B), clear = Color.FromArgb(0, scrim.R, scrim.G, scrim.B);
            g.GradientStops.Add(new GradientStop(fromTop ? solid : clear, 0));
            g.GradientStops.Add(new GradientStop(fromTop ? clear : solid, 1));
            return g;
        }

        private void Guard(string what, Action a)
        {
            try { a(); } catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube player " + what); }
        }

        // ------------------------------------------------------------------ start / stop

        // dev keys (SeleneCore "nomad:ytplay:<id>"): play one video by id, for automated tests
        public void DevPlay(string videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return;
            VideoItem v = null;
            try { foreach (var x in Subscriptions.LoadCached()) if (x != null && x.Id == videoId) { v = x; break; } } catch { }   // the feed knows its title and channel
            if (v == null) v = new VideoItem { Id = videoId, Title = "", Thumbnail = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg" };
            Play(new List<VideoItem> { v }, 0);
        }

        private void Play(List<VideoItem> list, int index)
        {
            if (list == null || index < 0 || index >= list.Count) return;
            if (playing) FinishCurrent();
            double resume = resumeId != null && resumeId == list[index].Id && resumeFrom > 1 ? resumeFrom : 0;
            resumeId = null;
            autoRetries = 0;
            lastAdvanceAt = clock.ElapsedMilliseconds;
            lastPollT = -1;
            queue = list;
            qi = index;
            nowPlaying = list[index];
            playing = true;
            stage = Stage.Loading;
            stageError = "";
            QuietChrome(false);
            ps = null;
            dispT = 0;
            endedHandled = false;
            prefsApplied = false;
            liveMode = false;
            rowFocused = false;
            rowIndex = 0;   // a new video starts on the first control, not wherever the last one left the row
            CancelCountdown();
            CloseRelated();
            picker.Close();
            int token = ++playToken;
            if (pv != null) pv.ExecuteScript("window.nomad&&window.nomad.pause();");   // the previous video stops now, not when the next one loads
            browseBox.Visibility = Visibility.Collapsed;
            playerLayer.Visibility = Visibility.Visible;
            pTitle.Text = nowPlaying.Title;
            pChannel.Text = nowPlaying.Author;
            UpdatePlayerSubscribe();
            loadStartedAt = clock.ElapsedMilliseconds;
            ShowBar();
            UpdatePlayerUi();
            string id = nowPlaying.Id;
            LocalServer.Prepare(id).ContinueWith(delegate(Task<Result<StreamInfo>> t)
            {
                Result<StreamInfo> r;
                double pos = 0;
                FreeTubeSettings fts = new FreeTubeSettings();
                try
                {
                    r = t.IsFaulted || t.IsCanceled ? Result<StreamInfo>.Fail("Could not load this video. Please try again.") : t.Result;
                    if (r.Ok) { pos = resume > 0 ? resume : NomadStore.GetPosition(id); fts = FreeTubeData.LoadSettings(); }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "SeleneBrowser: YouTube prepare");
                    r = Result<StreamInfo>.Fail("Could not load this video. Please try again.");
                }
                Dispatcher.BeginInvoke(new Action(delegate { Guard("prepared", delegate { OnPrepared(token, r, pos, fts); }); }));
            });
        }

        private void OnPrepared(int token, Result<StreamInfo> r, double pos, FreeTubeSettings fts)
        {
            if (!playing || token != playToken) return;
            if (!r.Ok) { SetError(r.Error); return; }
            StreamInfo si = r.Value;
            if (nowPlaying.Title.Length == 0) nowPlaying.Title = si.Title;
            if (nowPlaying.Author.Length == 0) nowPlaying.Author = si.Author;
            if (nowPlaying.AuthorId.Length == 0) nowPlaying.AuthorId = si.AuthorId;
            if (nowPlaying.Duration <= 0) nowPlaying.Duration = si.Duration;
            pTitle.Text = nowPlaying.Title;
            pChannel.Text = nowPlaying.Author;
            UpdatePlayerSubscribe();

            int cap = fts.DefaultQuality, prefQ;
            string pq = NomadStore.GetPref("yt.quality", "auto");
            if (pq != "auto" && int.TryParse(pq, out prefQ) && prefQ > 0) cap = prefQ;
            int screen = ScreenHeight();
            // a chosen quality (here or FreeTube's default) is taken as is, above the screen too, as YouTube does: a 2160p
            // stream on a 1440p screen is sharper than the 1440p one. Only Auto stops at the screen's height.
            int max = cap > 0 ? cap : screen;

            // YouTube's isLive only: the ANDROID_VR client hands out an HLS manifest for ordinary videos too, and those
            // must keep their resume position and watched progress (the page still reports live if it goes live)
            liveMode = si.IsLive;
            logger.Info("SeleneBrowser: YouTube " + nowPlaying.Id + " live=" + liveMode + " (api isLive=" + si.IsLive + ", hls=" + !string.IsNullOrEmpty(si.HlsUrl) + ")");
            if (liveMode) nowPlaying.IsLive = true;
            if (pv == null)
            {
                pv = PageViews.Create(true, true);   // windowed player: full frame rate and native sharpness for video
                pvHost.Children.Add(pv.Element);
                pv.LoadEnd += OnPlayerLoadEnd;
                PageViews.OnEdgeInitFailed(pv, OnEdgeFailed);
            }
            if (liveMode && !PageViews.IsEdge(pv))
            {
                SetError("Live needs Microsoft Edge WebView2");
                if (shown) StartPlayerTimers();
                return;
            }
            loadStartedAt = clock.ElapsedMilliseconds;
            SetMemoryLevel(false);
            pvUrl = LocalServer.PlayerUrl(nowPlaying.Id, pos, max, fts.UseSponsorBlock);
            pageReqToken = playToken;
            pv.Navigate(pvUrl);
            if (shown) StartPlayerTimers();
        }

        // WebView2 was present but would not start: carry on with CEF (no H.264, so no live streams)
        private void OnEdgeFailed()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                Guard("edge fallback", delegate
                {
                    if (pv == null || PageViews.IsEdge(pv) == false) return;
                    logger.Warn("SeleneBrowser: YouTube player - WebView2 failed to start, switching to CEF");
                    IPageView failed = pv;
                    failed.LoadEnd -= OnPlayerLoadEnd;
                    pvHost.Children.Remove(failed.Element);
                    DisposeObj(failed);   // release the broken WebView2 and its slot in the shared environment
                    pv = new CefView();
                    pvHost.Children.Add(pv.Element);
                    pv.LoadEnd += OnPlayerLoadEnd;
                    if (!playing) return;
                    if (liveMode) { SetError("Live needs Microsoft Edge WebView2"); return; }
                    if (pvUrl.Length > 0 && stage == Stage.Loading) { loadStartedAt = clock.ElapsedMilliseconds; pv.Navigate(pvUrl); }
                });
            }));
        }

        private int ScreenHeight()
        {
            double scale = 1;
            PresentationSource src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) scale = src.CompositionTarget.TransformToDevice.M22;
            return (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale);
        }

        // One trusted click per page load so Chromium lets the video start with sound.
        private void OnPlayerLoadEnd(string url)
        {
            // the page for the current video is in: from now on its state is the one the poll reads
            if (playing && url != null && nowPlaying != null && pageReqToken == playToken
                && url.IndexOf("/player", StringComparison.Ordinal) >= 0 && url.IndexOf("v=" + Uri.EscapeDataString(nowPlaying.Id), StringComparison.Ordinal) >= 0)
                loadedToken = playToken;
            if (!playing || url == null || url.IndexOf("/player", StringComparison.Ordinal) < 0 || clickedToken == playToken) return;
            int token = playToken;
            clickedToken = token;
            After(500, delegate
            {
                if (!playing || token != playToken || pv == null) return;
                FrameworkElement fe = pv.Element as FrameworkElement;
                if (fe != null) pv.Click(fe.ActualWidth / 2, fe.ActualHeight / 2);
            });
        }

        private void SetError(string msg)
        {
            if (nowPlaying != null && !liveMode && dispT > 1) { resumeId = nowPlaying.Id; resumeFrom = dispT; }   // A retries from here
            stage = Stage.Error;
            stageError = msg;
            QuietChrome(false);
            ShowBar();
            UpdatePlayerUi();
        }

        // Leaves the player and returns to the grid with focus on the video that was playing.
        private void ExitPlayer()
        {
            if (!playing) return;
            FinishCurrent();
            playToken++;
            playing = false;
            StopPlayerTimers();
            CancelCountdown();
            CloseRelated();
            picker.Close();
            logger.Info("SeleneBrowser: YouTube back to the list (player stopped)");
            if (pv != null) { pv.ExecuteScript("window.nomad&&(window.nomad.stop||window.nomad.pause)();"); pv.Navigate("about:blank"); SetMemoryLevel(true); }
            QuietChrome(false);
            playerLayer.Visibility = Visibility.Collapsed;
            browseBox.Visibility = Visibility.Visible;
            if (current.Kind == CardKind.Video && nowPlaying != null)
            {
                int ix = current.Videos.FindIndex(delegate(VideoItem x) { return x.Id == nowPlaying.Id; });
                if (ix >= 0)
                {
                    VideoItem v = current.Videos[ix];
                    v.WatchProgress = nowPlaying.WatchProgress;
                    if (v.Duration <= 0) v.Duration = nowPlaying.Duration;
                    current.Focus = ix;
                    EnsureBuilt(ix + 16);
                    ReplaceCard(ix);
                    SetFocus(ix);
                }
            }
            if (current != null && current.Focus >= current.Count && current.Count > 0) current.Focus = 0;
            ViewState feed = tabStates[1];
            if (feed != null && feed.Loaded) { BuildContinueRail(feed); UpdateChip(); }
            UpdateFocusVisual();
            UpdateHints();
        }

        // The player's WebView2 is a big process tree: asks Edge to trim it while idle (Low) and back to Normal when playing.
        // Reflection: the core lives in EdgeView's private field and the property needs a recent runtime.
        private void SetMemoryLevel(bool low)
        {
            try
            {
                if (pv == null) return;
                FieldInfo f = pv.GetType().GetField("core", BindingFlags.Instance | BindingFlags.NonPublic);
                object core = f != null ? f.GetValue(pv) : null;
                if (core == null) return;
                PropertyInfo p = core.GetType().GetProperty("MemoryUsageTargetLevel");
                if (p != null) p.SetValue(core, Enum.Parse(p.PropertyType, low ? "Low" : "Normal"), null);
            }
            catch { }
        }

        // Leaving YouTube entirely: stop the video, dispose the player's WebView2 (re-created on the next play), close the
        // overlay window and drop the stream / thumbnail caches. Timers are already stopped by the caller.
        private void ReleasePlayer()
        {
            try
            {
                if (playing) ExitPlayer();
                polling = false;   // an evaluation on the disposed view never calls back
                if (overlayWin != null) overlayWin.Close();
                if (pv != null)
                {
                    IPageView old = pv;
                    pv = null;
                    loadedToken = -1;
                    try { old.LoadEnd -= OnPlayerLoadEnd; } catch { }
                    try { pvHost.Children.Remove(old.Element); } catch { }
                    DisposeObj(old);   // EdgeView.Dispose already frees its element and WebView2 (and is safe to call twice)
                    if (!(old is EdgeView))
                    {
                        DisposeObj(old.Element);
                        try
                        {
                            FieldInfo wf = old.GetType().GetField("web", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (wf != null) DisposeObj(wf.GetValue(old));
                        }
                        catch { }
                    }
                    logger.Info("SeleneBrowser: YouTube player view disposed");
                }
                LocalServer.TrimCaches();
                ThumbCache.Trim();
            }
            catch (Exception ex) { logger.Error(ex, "SeleneBrowser: YouTube release"); }
        }

        private static void DisposeObj(object o)
        {
            try
            {
                if (o == null) return;
                IDisposable d = o as IDisposable;
                if (d != null) { d.Dispose(); return; }
                MethodInfo m = o.GetType().GetMethod("Dispose", Type.EmptyTypes);   // IPageView.Dispose, if it has one
                if (m != null) m.Invoke(o, null);
            }
            catch { }
        }

        // Called when the overlay is hidden: pause, save, keep the page (no auto resume).
        private void PauseForHide()
        {
            if (!playing) return;
            logger.Info("SeleneBrowser: YouTube screen closed (player paused)");
            if (pv != null) pv.ExecuteScript("window.nomad&&window.nomad.pause();");
            CancelCountdown();
            FinishCurrent();
        }

        private void FinishCurrent()
        {
            SaveProgress();
            if (nowPlaying == null || ps == null || stage != Stage.Playing || liveMode) return;
            VideoItem v = nowPlaying;
            double t = dispT;
            WatchedIds.Add(v.Id);
            Task.Run(delegate { NomadStore.AddHistory(v, t); });
        }

        private void SaveProgress()
        {
            if (nowPlaying == null || ps == null || stage != Stage.Playing || dispT < 1 || liveMode) return;
            string id = nowPlaying.Id;
            double t = dispT, d = ps.D;
            nowPlaying.WatchProgress = t;
            Task.Run(delegate { NomadStore.SavePosition(id, t, d); });
        }

        private void StartPlayerTimers()
        {
            pollTimer.Start();
            saveTimer.Start();
            hideTimer.Start();
        }

        private void StopPlayerTimers()
        {
            pollTimer.Stop();
            saveTimer.Stop();
            hideTimer.Stop();
            countTimer.Stop();
            playerToast.Stop();
        }

        // ------------------------------------------------------------------ polling

        private void PollPlayer()
        {
            if (!playing || !shown || pv == null || polling) return;
            if (stage == Stage.Loading && clock.ElapsedMilliseconds - loadStartedAt > 45000) { SetError("The video took too long to start. Check your connection and retry."); return; }
            polling = true;
            int token = playToken;
            pv.EvaluateAsync(PollJs, delegate(string json)
            {
                polling = false;
                // (until the new video's page has loaded, the view still shows the previous one: its state is not ours)
                if (!playing || token != playToken || loadedToken != playToken) return;
                Guard("state", delegate { ApplyState(json); });
            });
        }

        private void ApplyState(string json)
        {
            if (string.IsNullOrEmpty(json) || stage == Stage.Error) return;
            PState s = ParseState(json);
            if (s == null) return;
            ps = s;
            if (s.Live && s.Dbg.Length > 0 && !s.Paused && clock.ElapsedMilliseconds - lastDbgLogAt >= 10000)
            {
                lastDbgLogAt = clock.ElapsedMilliseconds;
                logger.Info("SeleneBrowser: live dbg " + s.Dbg);
            }
            if (s.Height > 0 && s.Height != loggedHeight)
            {
                loggedHeight = s.Height;
                logger.Info("SeleneBrowser: YouTube rendition " + s.Height + "p (" + (pv != null && PageViews.IsEdge(pv) ? "Edge" : "CEF") + ")");
            }
            if (s.Live && !liveMode) { liveMode = true; if (nowPlaying != null) nowPlaying.IsLive = true; }   // the page knows best
            if (!s.Paused && Math.Abs(s.T - lastPollT) > 0.05) lastAdvanceAt = clock.ElapsedMilliseconds;
            lastPollT = s.T;
            if (clock.ElapsedMilliseconds > seekHoldUntil) dispT = s.T;
            bool movingNow = clock.ElapsedMilliseconds - lastAdvanceAt < 8000;
            if (s.Error.Length > 0 && stage == Stage.Loading && !s.Ready) { SetError(s.Error); return; }
            // the page reports a final error only after its own recovery failed: still act only if the picture really stopped
            if (s.Error.Length > 0 && !movingNow)
            {
                if (autoRetries < 1 && nowPlaying != null && !liveMode)
                {
                    int n = autoRetries + 1;
                    resumeId = nowPlaying.Id; resumeFrom = dispT;
                    logger.Info("SeleneBrowser: YouTube playback stopped (" + s.Dbg + "), restarting with fresh streams");
                    Play(queue, qi);
                    autoRetries = n;
                    return;
                }
                SetError("Couldn’t keep playing this video. Press A to try again.");
                return;
            }
            if (s.Ready && stage == Stage.Loading)
            {
                stage = Stage.Playing;
                OnFirstReady();
                startCheckToken = playToken;
                startCheckRound = 0;
                startCheckAt = clock.ElapsedMilliseconds + 5000;
            }
            // one line per video, 5 s after it is ready: whether it is really playing (a video stuck paused at start was a bug)
            if (startCheckToken == playToken && startCheckAt > 0 && clock.ElapsedMilliseconds >= startCheckAt)
            {
                startCheckRound++;
                startCheckAt = startCheckRound < 4 ? clock.ElapsedMilliseconds + (startCheckRound == 1 ? 10000 : 15000) : 0;
                logger.Info("SeleneBrowser: YouTube " + (nowPlaying != null ? nowPlaying.Id : "?") + " " + (startCheckRound == 1 ? 5 : startCheckRound == 2 ? 15 : startCheckRound == 3 ? 30 : 45) + " s after start: " + (s.Paused ? "PAUSED" : "playing") + " t=" + s.T.ToString("0.0", CultureInfo.InvariantCulture) + (s.Buffering ? " buffering" : "") + " " + s.Dbg);
            }
            if (!s.Ended) endedHandled = false;
            else if (stage == Stage.Playing) HandleEnded();
            UpdatePlayerUi();
        }

        private static PState ParseState(string json)
        {
            try
            {
                Dictionary<string, object> d = Jx.D(Jx.Parse(json));
                if (d == null) return null;
                PState s = new PState();
                s.T = Jx.Dbl(d, "t"); s.D = Jx.Dbl(d, "d"); s.Buf = Jx.Dbl(d, "b");
                s.Paused = Jx.Bool(d, "paused"); s.Buffering = Jx.Bool(d, "buffering"); s.Ended = Jx.Bool(d, "ended"); s.Ready = Jx.Bool(d, "ready");
                s.Quality = Jx.Str(d, "quality"); s.Height = (int)Jx.Long(d, "height");
                s.Live = Jx.Bool(d, "live"); s.Behind = Jx.Dbl(d, "behind"); s.WStart = Jx.Dbl(d, "ws");
                s.Dbg = Jx.Str(d, "dbg") ?? "";
                s.Rate = Jx.Dbl(d, "rate"); if (s.Rate <= 0) s.Rate = 1;
                s.Caption = Jx.Str(d, "caption"); s.Audio = Jx.Str(d, "audio"); s.Error = Jx.Str(d, "error");
                foreach (object o in Jx.L(Jx.Get(d, "qualities"))) s.Qualities.Add((int)Convert.ToDouble(o, CultureInfo.InvariantCulture));
                foreach (object o in Jx.L(Jx.Get(d, "captions"))) s.Captions.Add(new KeyValuePair<string, string>(Jx.Str(o, "lang"), Jx.Str(o, "label")));
                foreach (object o in Jx.L(Jx.Get(d, "audios"))) s.Audios.Add(new KeyValuePair<string, string>(Jx.Str(o, "lang"), Jx.Str(o, "label")));
                return s;
            }
            catch { return null; }
        }

        // Remembered quality / speed / captions, applied once the page reports ready; also logs the watch.
        private void OnFirstReady()
        {
            if (prefsApplied) return;
            prefsApplied = true;
            string rate = NomadStore.GetPref("yt.rate", "1");
            double r;
            if (rate != "1" && double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out r) && r > 0) { Js("setRate(" + rate + ")"); ps.Rate = r; }
            int q;
            if (int.TryParse(NomadStore.GetPref("yt.quality", "auto"), out q) && ps.Qualities.Contains(q)) Js("setQuality(" + q + ")");
            string cap = NomadStore.GetPref("yt.caption", "");
            foreach (KeyValuePair<string, string> c in ps.Captions)
                if (cap.Length > 0 && c.Key == cap) { Js("setCaption(" + Jx.ToJson(cap) + ")"); ps.Caption = cap; }
            if (liveMode) return;   // live streams are never logged as watched
            VideoItem v = nowPlaying;
            double t = dispT;
            WatchedIds.Add(v.Id);
            Task.Run(delegate { NomadStore.AddHistory(v, t); });
        }

        private void Js(string call)
        {
            if (pv != null) pv.ExecuteScript("window.nomad&&window.nomad." + call + ";");
        }

        // ------------------------------------------------------------------ overlay painting

        private void UpdatePlayerUi()
        {
            bool loading = stage == Stage.Loading, err = stage == Stage.Error;
            stageBox.Visibility = loading || err ? Visibility.Visible : Visibility.Collapsed;
            stageSpinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            stageText.Text = loading ? "Loading video\u2026" : stageError;
            stageSub.Text = err ? "A Retry    B Back" : "B Cancel";
            bool playingNow = stage == Stage.Playing && ps != null;
            bufSpinner.Visibility = playingNow && ps.Buffering ? Visibility.Visible : Visibility.Collapsed;
            pausedBadge.Visibility = playingNow && ps.Paused && !ps.Buffering ? Visibility.Visible : Visibility.Collapsed;
            ApplyBar();
            if (playingNow) { UpdateSeekUi(); UpdateButtons(); }
            UpdateHints();
        }

        private void ApplyBar()
        {
            topBar.Visibility = barVisible || stage != Stage.Playing ? Visibility.Visible : Visibility.Collapsed;
            bottomBar.Visibility = barVisible && stage == Stage.Playing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowBar()
        {
            barVisible = true;
            lastInput = clock.ElapsedMilliseconds;
            ApplyBar();
        }

        private void HideBar()
        {
            QuietChrome(false);
            barVisible = false;
            rowFocused = false;
            UpdateRowFocus();
            ApplyBar();
        }

        // While seeking, only the seek bar matters: fade the rest of the chrome back in ~1 s after the last seek.
        private void QuietChrome(bool on)
        {
            quiet = on;
            double o = on ? 0.16 : 1;
            topBar.Opacity = o;
            buttonRow.Opacity = o;
            playerTimeRow.Opacity = o;
            pHint.Opacity = o;
        }

        private void OnHideTick()
        {
            if (quiet && clock.ElapsedMilliseconds >= quietUntil) QuietChrome(false);
            if (!playing || !barVisible || stage != Stage.Playing) return;
            if (rowFocused || picker.IsOpen || relatedOpen || counting) return;
            if (clock.ElapsedMilliseconds - lastInput > 3000) { HideBar(); UpdateHints(); }
        }

        private static string Clock(double sec)
        {
            int s = (int)Math.Max(0, sec);
            string r = Fmt.Duration(s);
            return r.Length == 0 ? "0:00" : r;
        }

        private static readonly Brush LiveRed = new SolidColorBrush(Color.FromRgb(0xE6, 0x1E, 0x1E));

        // seconds behind the live edge (follows a seek immediately, before the page reports it)
        private double LiveBehind()
        {
            if (ps == null || !ps.Live) return 0;
            return Math.Max(0, ps.Behind + (ps.T - dispT));
        }

        private void UpdateSeekUi()
        {
            if (ps.Live)
            {
                double span = Math.Max(1, ps.D - ps.WStart);
                double lf = Math.Max(0, Math.Min(1, (dispT - ps.WStart) / span));
                double lb = Math.Max(0, Math.Min(1, (ps.Buf - ps.WStart) / span));
                double behind = LiveBehind();
                bool atEdge = behind <= 5;
                liveBadge.Visibility = Visibility.Visible;
                liveBadge.Background = atEdge ? LiveRed : Theme.Key;
                string tx = (atEdge ? "" : "-" + Clock(behind) + " behind") + (ps.Paused ? (atEdge ? "" : "    ") + "Paused" : "");
                pTime.Text = tx;
                pTime.Visibility = tx.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                seekFill.Width = SeekW * lf;
                seekBuf.Width = SeekW * lb;
                seekKnob.Margin = new Thickness(SeekW * lf - 14, 0, 0, 0);
                return;
            }
            liveBadge.Visibility = Visibility.Collapsed;
            pTime.Visibility = Visibility.Visible;
            double d = ps.D;
            double frac = d > 0 ? Math.Max(0, Math.Min(1, dispT / d)) : 0;
            double buf = d > 0 ? Math.Max(0, Math.Min(1, ps.Buf / d)) : 0;
            seekFill.Width = SeekW * frac;
            seekBuf.Width = SeekW * buf;
            seekKnob.Margin = new Thickness(SeekW * frac - 14, 0, 0, 0);
            pTime.Text = Clock(dispT) + " / " + Clock(d) + (ps.Paused ? "    Paused" : "");
        }

        private static string LabelFor(List<KeyValuePair<string, string>> list, string key, string none)
        {
            foreach (KeyValuePair<string, string> p in list) if (p.Key == key) return p.Value.Length > 0 ? p.Value : p.Key;
            return none;
        }

        private void UpdateButtons()
        {
            string q = ps.Quality == "auto" ? "Auto" + (ps.Height > 0 ? " (" + ps.Height + "p)" : "") : ps.Quality + "p";
            SetButton(0, "Quality: " + q);
            SetButton(1, "Speed: " + FormatRate(ps.Rate));
            SetButton(2, "Captions: " + (ps.Caption.Length == 0 ? "Off" : LabelFor(ps.Captions, ps.Caption, ps.Caption)));
            SetButton(3, ps.Audios.Count > 1 ? "Audio: " + LabelFor(ps.Audios, ps.Audio, "Default") : "Audio");
            UpdatePlayerSubscribe();
            UpdateRowFocus();
        }

        private void SetButton(int i, string text) { Theme.SetLabel(buttons[i], text); }

        // The last control in the player's button row follows the same local subscription store as the
        // channel page / search rail, so its label is correct no matter where the subscribe happened.
        private void UpdatePlayerSubscribe()
        {
            if (buttons.Count < 6) return;
            string id = nowPlaying != null ? nowPlaying.AuthorId : "";
            bool has = !string.IsNullOrEmpty(id);
            bool on = has && (ownIds.Contains(id) || subIds.Contains(id));
            Theme.SetLabel(buttons[5], !has ? "Channel unknown" : (on ? "Subscribed" : "Subscribe"));
            Theme.SetToggled(buttons[5], on);
            Theme.SetIconShown(buttons[5], has);
        }

        private void TogglePlayerSubscribe()
        {
            if (nowPlaying == null) return;
            string id = nowPlaying.AuthorId;
            if (string.IsNullOrEmpty(id)) { Toast("Channel details are not available for this video."); return; }
            bool on = !(ownIds.Contains(id) || subIds.Contains(id));
            string name = string.IsNullOrEmpty(nowPlaying.Author) ? "this channel" : nowPlaying.Author;
            SetSubscribed(id, name, "", on);
        }

        // The Subscribe control is only reachable when this video actually has a channel id.
        private int LastButton
        {
            get
            {
                int last = buttons.Count - 1;
                if (nowPlaying == null || string.IsNullOrEmpty(nowPlaying.AuthorId)) last--;
                return Math.Max(0, last);
            }
        }

        private void UpdateRowFocus()
        {
            if (rowIndex > LastButton) rowIndex = LastButton;
            for (int i = 0; i < buttons.Count; i++) Theme.SetFocused(buttons[i], rowFocused && i == rowIndex);
        }

        private static string FormatRate(double r) { return r.ToString("0.##", CultureInfo.InvariantCulture) + "x"; }

        private string PlayerHints()
        {
            if (stage == Stage.Loading) return "B Cancel";
            if (stage == Stage.Error) return "A Retry    B Back";
            if (picker.IsOpen) return "D-pad Select    A Choose    B Close";
            if (relatedOpen) return "D-pad Select    A Play    B / Y Close";
            if (counting) return "A Play now    B Cancel";
            if (rowFocused)
            {
                if (rowIndex == 5)
                {
                    if (nowPlaying == null || string.IsNullOrEmpty(nowPlaying.AuthorId))
                        return "A Channel unavailable    Left/Right Move    Up/B Back to seek bar";
                    bool on = ownIds.Contains(nowPlaying.AuthorId) || subIds.Contains(nowPlaying.AuthorId);
                    return "A " + (on ? "Unsubscribe" : "Subscribe") + "    Left/Right Move    Up/B Back to seek bar";
                }
                return "Left/Right Move    A Open    Up/B Back to seek bar";
            }
            if (ps != null && ps.Live)
                return "A Play/Pause    Left/Right Rewind/Forward    LT/RT 60s    Y " + (LiveBehind() > 5 ? "Go live" : "Up next") + "    Down Controls    LB/RB Prev/Next    B Back    Start Menu";
            return "A Play/Pause    Left/Right Seek    LT/RT 60s    Down Controls    X Captions    Y Up next    LB/RB Prev/Next    B Back    Start Menu";
        }

        // ------------------------------------------------------------------ player pad

        private bool HandlePlayerPad(string b)
        {
            bool keep = HandlePlayerCore(b);
            UpdateHints();
            return keep;
        }

        private bool HandlePlayerCore(string b)
        {
            if (b == "B") logger.Info("SeleneBrowser: YouTube player B (stage=" + stage + ", bar=" + barVisible + ", picker=" + picker.IsOpen + ", related=" + relatedOpen + ", row=" + rowFocused + ", countdown=" + counting + ")");
            if (picker.IsOpen) { HandlePicker(b); return true; }
            if (relatedOpen) { HandleRelated(b); return true; }
            if (stage != Stage.Playing)
            {
                if (!limiter.IsFirst(b)) return true;
                if (b == "B") ExitPlayer();
                else if (stage == Stage.Error && b == "A") Play(queue, qi);
                return true;
            }
            bool wasVisible = barVisible;
            if (b != "B") ShowBar(); else lastInput = clock.ElapsedMilliseconds;
            if (counting && (b == "A" || b == "B"))
            {
                if (!limiter.IsFirst(b)) return true;
                if (b == "B") CancelCountdown(); else PlayAt(qi + 1);
                return true;
            }
            if (rowFocused && HandleRow(b)) return true;
            switch (b)
            {
                case "DPadLeft": case "DPadRight": SeekPress(b == "DPadLeft" ? -1 : 1); return true;
                case "A":
                    if (!limiter.IsFirst(b)) return true;
                    Js("toggle()");
                    if (ps != null) { ps.Paused = !ps.Paused; UpdatePlayerUi(); }
                    return true;
                case "TriggerLeft": if (limiter.Allow(b)) SeekBy(-60); return true;
                case "TriggerRight": if (limiter.Allow(b)) SeekBy(60); return true;
                case "DPadDown": if (limiter.IsFirst(b) && wasVisible) { rowFocused = true; rowIndex = Math.Min(rowIndex, LastButton); UpdateRowFocus(); } return true;
                case "X": if (limiter.IsFirst(b)) ToggleCaptions(); return true;
                case "Y":
                    if (!limiter.IsFirst(b)) return true;
                    if (ps != null && ps.Live && LiveBehind() > 5) GoLive(); else OpenRelated();
                    return true;
                case "LeftShoulder": if (limiter.IsFirst(b)) PlayAt(qi - 1); return true;
                case "RightShoulder": if (limiter.IsFirst(b)) PlayAt(qi + 1); return true;
                case "Start": if (limiter.IsFirst(b)) OpenPlayerMenu(); return true;
                case "B":
                    if (!limiter.IsFirst(b)) return true;
                    if (wasVisible) HideBar(); else ExitPlayer();
                    return true;
            }
            return true;
        }

        private bool HandleRow(string b)
        {
            switch (b)
            {
                case "DPadLeft": if (limiter.Allow(b)) { rowIndex = Math.Max(0, rowIndex - 1); UpdateRowFocus(); } return true;
                case "DPadRight": if (limiter.Allow(b)) { rowIndex = Math.Min(LastButton, rowIndex + 1); UpdateRowFocus(); } return true;
                case "A": if (limiter.IsFirst(b)) OpenPicker(rowIndex); return true;
                case "DPadUp": case "B": if (limiter.IsFirst(b)) { rowFocused = false; UpdateRowFocus(); } return true;
                case "DPadDown": return true;
            }
            return false;
        }

        // Held Left/Right: 10 s steps, 30 s once the hold has repeated a few times.
        private void SeekPress(int dir)
        {
            string key = dir < 0 ? "DPadLeft" : "DPadRight";
            long now = clock.ElapsedMilliseconds;
            if (now - lastSeekPress > 250) seekRun = 0;
            lastSeekPress = now;
            if (!limiter.Allow(key)) return;
            seekRun++;
            SeekBy(dir * (seekRun > 5 ? 30 : 10));
        }

        private void SeekBy(double seconds)
        {
            Js("seekBy(" + seconds.ToString(CultureInfo.InvariantCulture) + ")");
            if (ps == null) return;
            if (ps.Live) dispT = Math.Max(ps.WStart, Math.Min(ps.D, dispT + seconds));   // only inside the DVR window
            else dispT = Math.Max(0, ps.D > 0 ? Math.Min(ps.D, dispT + seconds) : dispT + seconds);
            seekHoldUntil = clock.ElapsedMilliseconds + 900;
            quietUntil = clock.ElapsedMilliseconds + 1100;
            if (!quiet) QuietChrome(true);
            UpdateSeekUi();
        }

        private void GoLive()
        {
            if (ps == null || !ps.Live) return;
            Js("goLive()");
            dispT = ps.T + ps.Behind;
            ps.T = dispT;
            ps.Behind = 0;
            ps.Paused = false;
            seekHoldUntil = clock.ElapsedMilliseconds + 900;
            UpdateSeekUi();
        }

        // ------------------------------------------------------------------ queue, countdown

        private void PlayAt(int i)
        {
            if (i < 0) { Toast("This is the first video."); return; }
            if (i >= queue.Count) { Toast("End of the list."); return; }
            Play(queue, i);
        }

        private void HandleEnded()
        {
            if (endedHandled) return;
            endedHandled = true;
            ShowBar();
            if (qi + 1 < queue.Count) StartCountdown();
        }

        private void StartCountdown()
        {
            counting = true;
            countLeft = 5;
            ShowCountdown();
            countTimer.Start();
        }

        private void ShowCountdown()
        {
            countText.Text = "Up next in " + countLeft + ":  " + queue[qi + 1].Title + "     B Cancel";
            countBox.Visibility = Visibility.Visible;
        }

        private void OnCountTick()
        {
            if (!counting) { countTimer.Stop(); return; }
            countLeft--;
            if (countLeft <= 0) { CancelCountdown(); PlayAt(qi + 1); return; }
            ShowCountdown();
        }

        private void CancelCountdown()
        {
            counting = false;
            countTimer.Stop();
            countBox.Visibility = Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ pickers and settings

        private void HandlePicker(string b)
        {
            switch (b)
            {
                case "DPadUp": if (limiter.Allow(b)) picker.Move(-1); break;
                case "DPadDown": if (limiter.Allow(b)) picker.Move(1); break;
                case "A":
                    if (!limiter.IsFirst(b)) break;
                    PickerPanel.Entry e = picker.Current;
                    picker.Close();
                    if (e != null && e.Run != null) e.Run();
                    break;
                case "B": case "Start": if (limiter.IsFirst(b)) picker.Close(); break;
            }
            lastInput = clock.ElapsedMilliseconds;
        }

        private void OpenPlayerMenu()
        {
            List<PickerPanel.Entry> l = new List<PickerPanel.Entry>();
            if (ps != null && ps.Live) l.Add(Item("Go live", delegate { GoLive(); return true; }));
            if (nowPlaying != null && !string.IsNullOrEmpty(nowPlaying.AuthorId))
            {
                string id = nowPlaying.AuthorId, name = nowPlaying.Author;
                l.Add(Item("Go to channel", delegate { ExitPlayer(); OpenChannel(id, name, ""); return true; }));
            }
            for (int i = 0; i < ButtonNames.Length; i++)
            {
                if (i == 5 && (nowPlaying == null || string.IsNullOrEmpty(nowPlaying.AuthorId))) continue;
                int idx = i;
                l.Add(Item(ButtonNames[i], delegate { OpenPicker(idx); return true; }));
            }
            l.Add(Item("Back to list", delegate { ExitPlayer(); return true; }));
            picker.Open("Player", l, 0);
        }

        private void OpenPicker(int which)
        {
            if (which == 4) { OpenRelated(); return; }
            if (which == 5) { TogglePlayerSubscribe(); return; }
            if (ps == null) return;
            List<PickerPanel.Entry> l = new List<PickerPanel.Entry>();
            int start = 0;
            string title = ButtonNames[which];
            if (which == 0)
            {
                l.Add(Pick("Auto", ps.Quality == "auto", delegate { SetQuality("auto"); }));
                foreach (int h in ps.Qualities)
                {
                    string hs = h.ToString(CultureInfo.InvariantCulture);
                    l.Add(Pick(hs + "p", ps.Quality == hs, delegate { SetQuality(hs); }));
                }
            }
            else if (which == 1)
            {
                foreach (double r in Rates)
                {
                    double rate = r;
                    l.Add(Pick(FormatRate(r), Math.Abs(ps.Rate - r) < 0.01, delegate { SetRate(rate); }));
                }
            }
            else if (which == 2)
            {
                l.Add(Pick("Off", ps.Caption.Length == 0, delegate { SetCaption(""); }));
                foreach (KeyValuePair<string, string> c in ps.Captions)
                {
                    string lang = c.Key;
                    l.Add(Pick(c.Value.Length > 0 ? c.Value : lang, ps.Caption == lang, delegate { SetCaption(lang); }));
                }
            }
            else
            {
                foreach (KeyValuePair<string, string> a in ps.Audios)
                {
                    string lang = a.Key;
                    l.Add(Pick(a.Value.Length > 0 ? a.Value : lang, ps.Audio == lang, delegate { SetAudio(lang); }));
                }
                if (l.Count == 0) { Toast("This video has a single audio track."); return; }
            }
            for (int i = 0; i < l.Count; i++) if (l[i].Checked) start = i;
            picker.Open(title, l, start);
        }

        private static PickerPanel.Entry Pick(string label, bool on, Action apply)
        {
            PickerPanel.Entry e = Item(label, delegate { apply(); return true; });
            e.Checked = on;
            return e;
        }

        private void SetQuality(string q)
        {
            Js("setQuality(" + Jx.ToJson(q) + ")");
            NomadStore.SetPref("yt.quality", q);
            ps.Quality = q;
            UpdateButtons();
        }

        private void SetRate(double r)
        {
            Js("setRate(" + r.ToString(CultureInfo.InvariantCulture) + ")");
            NomadStore.SetPref("yt.rate", r.ToString(CultureInfo.InvariantCulture));
            ps.Rate = r;
            UpdateButtons();
        }

        private void SetCaption(string lang)
        {
            Js("setCaption(" + Jx.ToJson(lang) + ")");
            NomadStore.SetPref("yt.caption", lang);
            ps.Caption = lang;
            UpdateButtons();
        }

        private void SetAudio(string lang)
        {
            Js("setAudio(" + Jx.ToJson(lang) + ")");
            ps.Audio = lang;
            UpdateButtons();
        }

        private void ToggleCaptions()
        {
            if (ps == null) return;
            if (ps.Caption.Length > 0) { SetCaption(""); Toast("Captions off"); return; }
            if (ps.Captions.Count == 0) { Toast("No captions for this video."); return; }
            string want = NomadStore.GetPref("yt.caption", ""), lang = ps.Captions[0].Key;
            foreach (KeyValuePair<string, string> c in ps.Captions)
            {
                if (want.Length > 0 && c.Key == want) { lang = c.Key; break; }
                if (want.Length == 0 && c.Key.StartsWith("en", StringComparison.OrdinalIgnoreCase)) { lang = c.Key; break; }
            }
            SetCaption(lang);
            Toast("Captions: " + LabelFor(ps.Captions, lang, lang));
        }

        // ------------------------------------------------------------------ related ("Up next") panel

        private void OpenRelated()
        {
            if (nowPlaying == null) return;
            relatedOpen = true;
            relatedPanel.Visibility = Visibility.Visible;
            relList.Children.Clear();
            relRows.Clear();
            related = new List<VideoItem>();
            relStatus.Text = "Loading\u2026";
            relStatus.Visibility = Visibility.Visible;
            int token = playToken;
            Await(YouTubeApi.Related(nowPlaying.Id), delegate(PageResult<VideoItem> r)
            {
                if (!playing || token != playToken || !relatedOpen) return;
                if (!r.Ok) { relStatus.Text = r.Error; return; }
                related = r.Items.Count > 25 ? r.Items.GetRange(0, 25) : r.Items;
                relStatus.Text = related.Count == 0 ? "No suggestions found." : "";
                relStatus.Visibility = related.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (VideoItem v in related)
                {
                    Border row = RelatedRow(v);
                    relRows.Add(row);
                    relList.Children.Add(row);
                }
                relIndex = 0;
                PaintRelated();
            }, delegate(string err) { relStatus.Text = err; });
        }

        private Border RelatedRow(VideoItem v)
        {
            // a small card with the same focus ring as the grid's
            Grid g;
            Border row = Theme.Card(double.NaN, 125, 12, out g);
            row.Margin = new Thickness(2, 2, 2, 2);
            Theme.SetFocusScale(row, 1.015);   // (the list clips its sides)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid pic = new Grid { Width = 200, Height = 113, Background = Theme.InkA(0.06) };
            Image img = new Image { Stretch = Stretch.UniformToFill };
            pic.Children.Add(img);
            ThumbCache.Load(string.IsNullOrEmpty(v.Thumbnail) ? Fmt.Thumb(v.Id) : v.Thumbnail, 200, Dispatcher,
                delegate(System.Windows.Media.Imaging.BitmapImage bi) { img.Source = bi; });
            string dur = v.IsLive ? "LIVE" : (v.DurationText.Length > 0 ? v.DurationText : Fmt.Duration(v.Duration));
            if (dur.Length > 0)
            {
                Border chip = Theme.Chip(dur, v.IsLive ? Cards.LiveRed : Theme.ScrimA(0.82), v.IsLive ? Brushes.White : Theme.Ink, 14);
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.HorizontalAlignment = HorizontalAlignment.Right;
                chip.VerticalAlignment = VerticalAlignment.Bottom;
                pic.Children.Add(chip);
            }
            g.Children.Add(pic);
            StackPanel tx = new StackPanel { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = Theme.Text(v.Title, 20, Theme.Ink);
            title.FontFamily = Theme.Display;
            title.FontWeight = FontWeights.SemiBold;
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxHeight = 54;
            tx.Children.Add(title);
            TextBlock by = Theme.Text(v.Author, 17, Theme.Ink60);
            by.Margin = new Thickness(0, 4, 0, 0);
            tx.Children.Add(by);
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            return row;
        }

        private void PaintRelated()
        {
            for (int i = 0; i < relRows.Count; i++) Theme.SetFocused(relRows[i], i == relIndex);
            if (relIndex < relRows.Count)
            {
                Border b = relRows[relIndex];
                Dispatcher.BeginInvoke(new Action(delegate { b.BringIntoView(); }), DispatcherPriority.Loaded);
            }
        }

        private void CloseRelated()
        {
            relatedOpen = false;
            relatedPanel.Visibility = Visibility.Collapsed;
        }

        private void HandleRelated(string b)
        {
            switch (b)
            {
                case "DPadUp": if (limiter.Allow(b) && relIndex > 0) { relIndex--; PaintRelated(); } break;
                case "DPadDown": if (limiter.Allow(b) && relIndex + 1 < relRows.Count) { relIndex++; PaintRelated(); } break;
                case "A":
                    if (limiter.IsFirst(b) && relIndex < related.Count) Play(related, relIndex);
                    break;
                case "B": case "Y": if (limiter.IsFirst(b)) CloseRelated(); break;
            }
            lastInput = clock.ElapsedMilliseconds;
        }
    }
}
