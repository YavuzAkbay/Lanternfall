// YouTubePlayerView.cs - player half of YouTubeControl: full-bleed CefView + WPF controls overlay.
//   Player pad: A play/pause (or open the focused button), Left/Right seek 10 s (held: 30 s), LT/RT 60 s,
//   Down shows the bar then enters the button row (Left/Right move, A opens a picker, Up/B leave), X captions,
//   Y "Up next" panel, LB/RB previous/next in the list, B hide bar then leave, Start player menu.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        private static readonly double[] Rates = { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 };
        private static readonly string[] ButtonNames = { "Quality", "Speed", "Captions", "Audio", "Up next" };
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
        private readonly TextBlock pTitle = Theme.Text("", 38, Brushes.White);
        private readonly TextBlock pChannel = Theme.Text("", 26, Theme.Ink60);
        private readonly TextBlock pTime = Theme.Text("0:00 / 0:00", 26, Brushes.White);
        private readonly Border liveBadge = new Border();
        private readonly TextBlock liveBadgeText = Theme.Text("LIVE", 24, Brushes.White);
        private readonly TextBlock pHint = Theme.Text("", 22, Theme.Ink60);
        private readonly TextBlock stageText = Theme.Text("", 32, Brushes.White);
        private readonly TextBlock stageSub = Theme.Text("", 24, Theme.Ink60);
        private readonly TextBlock countText = Theme.Text("", 26, Brushes.White);
        private readonly TextBlock relStatus = Theme.Text("", 24, Theme.Ink60);
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

        private IPageView pv;   // Edge WebView2 when available (H.264, HLS), CEF otherwise
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
        private long seekHoldUntil, lastInput, lastSeekPress, loadStartedAt;

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

            Grid seek = new Grid { Width = SeekW, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 14) };
            seek.Children.Add(new Border
            {
                Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255))
            });
            seekBuf.Height = 10; seekBuf.CornerRadius = new CornerRadius(5); seekBuf.HorizontalAlignment = HorizontalAlignment.Left;
            seekBuf.VerticalAlignment = VerticalAlignment.Center; seekBuf.Background = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
            seekFill.Height = 10; seekFill.CornerRadius = new CornerRadius(5); seekFill.HorizontalAlignment = HorizontalAlignment.Left;
            seekFill.VerticalAlignment = VerticalAlignment.Center; seekFill.Background = Theme.Accent;
            seekKnob.Fill = Theme.Accent;
            seek.Children.Add(seekBuf);
            seek.Children.Add(seekFill);
            seek.Children.Add(seekKnob);

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (string name in ButtonNames)
            {
                Border t = Theme.Tile(name, 26);
                t.Padding = new Thickness(22, 8, 22, 8);
                buttons.Add(t);
                row.Children.Add(t);
            }
            StackPanel bottom = new StackPanel { Margin = new Thickness(60, 90, 60, 24) };
            liveBadgeText.FontWeight = FontWeights.Bold;
            liveBadge.Child = liveBadgeText;
            liveBadge.CornerRadius = new CornerRadius(6);
            liveBadge.Padding = new Thickness(14, 2, 14, 2);
            liveBadge.Margin = new Thickness(0, 0, 16, 0);
            liveBadge.Visibility = Visibility.Collapsed;
            StackPanel timeRow = new StackPanel { Orientation = Orientation.Horizontal };
            timeRow.Children.Add(liveBadge);
            timeRow.Children.Add(pTime);
            bottom.Children.Add(timeRow);
            bottom.Children.Add(seek);
            bottom.Children.Add(row);
            pHint.Margin = new Thickness(4, 10, 0, 0);
            bottom.Children.Add(pHint);
            bottomBar.VerticalAlignment = VerticalAlignment.Bottom;
            bottomBar.Background = Fade(false);
            bottomBar.Child = bottom;
            d.Children.Add(bottomBar);

            pausedBadge.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
            pausedBadge.CornerRadius = new CornerRadius(14);
            pausedBadge.Padding = new Thickness(36, 14, 36, 14);
            pausedBadge.HorizontalAlignment = HorizontalAlignment.Center;
            pausedBadge.VerticalAlignment = VerticalAlignment.Center;
            pausedBadge.Child = Theme.Text("Paused", 48, Brushes.White);
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

            countBox.Background = new SolidColorBrush(Color.FromArgb(0xD0, 0, 0, 0));
            countBox.CornerRadius = new CornerRadius(12);
            countBox.Padding = new Thickness(28, 14, 28, 14);
            countBox.HorizontalAlignment = HorizontalAlignment.Center;
            countBox.VerticalAlignment = VerticalAlignment.Bottom;
            countBox.Margin = new Thickness(0, 0, 0, 340);
            countBox.Child = countText;
            countBox.Visibility = Visibility.Collapsed;
            d.Children.Add(countBox);

            StackPanel relStack = new StackPanel();
            TextBlock relHead = Theme.Text("Up next", 32, Theme.Ink);
            relHead.Margin = new Thickness(14, 6, 0, 8);
            relStack.Children.Add(relHead);
            relStatus.Margin = new Thickness(14, 8, 0, 0);
            relStack.Children.Add(relStatus);
            relScroll.Content = relList;
            relScroll.MaxHeight = 900;
            relStack.Children.Add(relScroll);
            relatedPanel.Width = 660;
            relatedPanel.Margin = new Thickness(0, 50, 50, 50);
            relatedPanel.Padding = new Thickness(14);
            relatedPanel.CornerRadius = new CornerRadius(14);
            relatedPanel.Background = Theme.Panel;
            relatedPanel.HorizontalAlignment = HorizontalAlignment.Right;
            relatedPanel.VerticalAlignment = VerticalAlignment.Stretch;
            relatedPanel.Child = relStack;
            relatedPanel.Visibility = Visibility.Collapsed;
            d.Children.Add(relatedPanel);

            d.Children.Add(picker);
            d.Children.Add(playerToast);
            playerLayer.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = d, IsHitTestVisible = false });

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
            Color solid = Color.FromArgb(0xE0, 0, 0, 0), clear = Color.FromArgb(0, 0, 0, 0);
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
            var v = new VideoItem { Id = videoId, Title = videoId, Thumbnail = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg" };
            Play(new List<VideoItem> { v }, 0);
        }

        private void Play(List<VideoItem> list, int index)
        {
            if (list == null || index < 0 || index >= list.Count) return;
            if (playing) FinishCurrent();
            queue = list;
            qi = index;
            nowPlaying = list[index];
            playing = true;
            stage = Stage.Loading;
            stageError = "";
            ps = null;
            dispT = 0;
            endedHandled = false;
            prefsApplied = false;
            liveMode = false;
            rowFocused = false;
            CancelCountdown();
            CloseRelated();
            picker.Close();
            int token = ++playToken;
            if (pv != null) pv.ExecuteScript("window.nomad&&window.nomad.pause();");   // the previous video stops now, not when the next one loads
            browseBox.Visibility = Visibility.Collapsed;
            playerLayer.Visibility = Visibility.Visible;
            pTitle.Text = nowPlaying.Title;
            pChannel.Text = nowPlaying.Author;
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
                    if (r.Ok) { pos = NomadStore.GetPosition(id); fts = FreeTubeData.LoadSettings(); }
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

            int cap = fts.DefaultQuality, prefQ;
            string pq = NomadStore.GetPref("yt.quality", "auto");
            if (pq != "auto" && int.TryParse(pq, out prefQ) && prefQ > 0) cap = prefQ;
            int screen = ScreenHeight();
            int max = cap > 0 ? Math.Min(cap, screen) : screen;

            // an HLS stream is only ever handed out for live videos (the player page goes live on it too)
            liveMode = si.IsLive || !string.IsNullOrEmpty(si.HlsUrl);
            logger.Info("SeleneBrowser: YouTube " + nowPlaying.Id + " live=" + liveMode + " (api isLive=" + si.IsLive + ", hls=" + !string.IsNullOrEmpty(si.HlsUrl) + ")");
            if (liveMode) nowPlaying.IsLive = true;
            if (pv == null)
            {
                pv = PageViews.Create();
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
                    pv.LoadEnd -= OnPlayerLoadEnd;
                    pvHost.Children.Remove(pv.Element);
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
            stage = Stage.Error;
            stageError = msg;
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
            if (pv != null) { pv.ExecuteScript("window.nomad&&window.nomad.pause();"); pv.Navigate("about:blank"); }
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
            UpdateHints();
        }

        // Called when the overlay is hidden: pause, save, keep the page (no auto resume).
        private void PauseForHide()
        {
            if (!playing) return;
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
            if (s.Live && !liveMode) { liveMode = true; if (nowPlaying != null) nowPlaying.IsLive = true; }   // the page knows best
            if (clock.ElapsedMilliseconds > seekHoldUntil) dispT = s.T;
            if (s.Error.Length > 0) { SetError("Playback problem: " + s.Error); return; }
            if (s.Ready && stage == Stage.Loading)
            {
                stage = Stage.Playing;
                OnFirstReady();
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
            barVisible = false;
            rowFocused = false;
            UpdateRowFocus();
            ApplyBar();
        }

        private void OnHideTick()
        {
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
            UpdateRowFocus();
        }

        private void SetButton(int i, string text) { ((TextBlock)buttons[i].Child).Text = text; }

        private void UpdateRowFocus()
        {
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
            if (rowFocused) return "Left/Right Move    A Open    Up/B Back to seek bar";
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
                case "DPadDown": if (limiter.IsFirst(b) && wasVisible) { rowFocused = true; UpdateRowFocus(); } return true;
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
                case "DPadRight": if (limiter.Allow(b)) { rowIndex = Math.Min(buttons.Count - 1, rowIndex + 1); UpdateRowFocus(); } return true;
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
            for (int i = 0; i < ButtonNames.Length; i++)
            {
                int idx = i;
                l.Add(Item(ButtonNames[i], delegate { OpenPicker(idx); return true; }));
            }
            l.Add(Item("Back to list", delegate { ExitPlayer(); return true; }));
            picker.Open("Player", l, 0);
        }

        private void OpenPicker(int which)
        {
            if (which == 4) { OpenRelated(); return; }
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
            Border row = new Border
            {
                Margin = new Thickness(8, 5, 8, 5), Padding = new Thickness(6), BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(8), Background = Theme.Key
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Image img = new Image { Width = 200, Height = 113, Stretch = Stretch.UniformToFill };
            ThumbCache.Load(string.IsNullOrEmpty(v.Thumbnail) ? Fmt.Thumb(v.Id) : v.Thumbnail, 200, Dispatcher,
                delegate(System.Windows.Media.Imaging.BitmapImage bi) { img.Source = bi; });
            g.Children.Add(img);
            StackPanel tx = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = Theme.Text(v.Title, 22, Theme.Ink);
            title.TextWrapping = TextWrapping.Wrap;
            title.MaxHeight = 90;
            tx.Children.Add(title);
            tx.Children.Add(Theme.Text(v.Author, 20, Theme.Ink60));
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            row.Child = g;
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
