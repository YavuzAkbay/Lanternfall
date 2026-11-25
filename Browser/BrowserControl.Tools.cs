// BrowserControl.Tools.cs - the browser's page tools, kept out of SeleneBrowser.cs: the Start menu with its Page tools submenu, find in page,
// reader mode, link options, History, Downloads, the tab switcher (with a picture of each page), and the per-site memories (zoom, phone
// version). The screens they use are ListPanel and TabSwitcher; they live in the chrome overlay like the menu and the settings.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SeleneBrowser
{
    public partial class BrowserControl
    {
        private enum PanelKind { None, History, Downloads, DownloadAsk, Tabs }

        private ListPanel listPanel;
        private TabSwitcher switcher;
        private PanelKind panelKind;
        private List<ListPanel.Row> panelRows = new List<ListPanel.Row>();
        private readonly DispatcherTimer panelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly List<DownloadItem> downloads = new List<DownloadItem>();
        private DownloadRequest pendingDownload;
        private string findQuery = "";
        private bool findActive;
        private string menuLink, menuLinkText;   // the link under the highlight when the menu opened
        private bool submenuOpen;

        // called once from the constructor, after InitExtras
        private void InitTools()
        {
            listPanel = new ListPanel(limiter);
            switcher = new TabSwitcher(limiter);
            chrome.Children.Add(listPanel);
            chrome.Children.Add(switcher);
            listPanel.Chosen = OnRowChosen;
            listPanel.Secondary = OnRowSecondary;
            listPanel.Tertiary = OnRowTertiary;
            switcher.Chosen = delegate(int i) { ClosePanel(false); if (i >= 0 && i < tabs.Count) ActivateTab(tabs[i]); EnterFocus(false); };
            switcher.CloseRequested = CloseTabFromSwitcher;
            switcher.NewTab = delegate { ClosePanel(false); BrowserTab t = NewTab(null, true); ActivateTab(t); GoHome(t); EnterFocus(false); };
            panelTimer.Tick += delegate { if (panelKind == PanelKind.Downloads) RefreshDownloadRows(); };
            Unloaded += delegate { panelTimer.Stop(); if (panelKind != PanelKind.None) ClosePanel(false); };
        }

        // the engine's events for one tab (HookTab calls this for Edge views)
        private void HookEdgeTools(BrowserTab tab, EdgeView edge)
        {
            edge.MobileFor = delegate(string url) { return BrowserSettings.IsMobileSite(BrowserSettings.SiteKey(url)); };
            edge.DownloadRequested += delegate(DownloadRequest r) { OnDownloadRequested(tab, r); };
            edge.DownloadFinished += delegate(DownloadItem item) { OnDownloadFinished(item); };
            edge.ScrollMoved += delegate(double dx, double dy) { if (tab == active && shown && dy != 0) BarsScrolled(dy > 0 ? 1 : -1, (int)Math.Max(1, Math.Abs(dy))); };   // the bars follow what really scrolled
        }

        // ----- the pad while a panel is open -----

        private bool HandlePanel(string b)
        {
            bool close = panelKind == PanelKind.Tabs ? switcher.HandlePad(b) : listPanel.HandlePad(b);
            if (close) ClosePanel(true);
            return true;
        }

        private string PanelHints()
        {
            if (panelKind == PanelKind.Tabs) return TabSwitcher.Hints;
            return listPanel.Hints;
        }

        // the common start of every panel: the page steps back, the pad belongs to the panel
        private void EnterPanel(PanelKind kind)
        {
            BarsShow();
            panelKind = kind;
            mode = Mode.Menu;
            cursorLayer.Visibility = Visibility.Collapsed;
            menuShade.Visibility = Visibility.Collapsed;
            Script("hide()");
            UpdateHints();
        }

        private void ClosePanel(bool refocus)
        {
            if (panelKind == PanelKind.DownloadAsk && pendingDownload != null) AnswerDownload(false);
            panelKind = PanelKind.None;
            panelTimer.Stop();
            if (listPanel != null) listPanel.Close();
            if (switcher != null) switcher.Close();
            if (refocus) EnterFocus(false);
        }

        private void OnRowChosen(int i)
        {
            if (i < 0 || i >= panelRows.Count) return;
            switch (panelKind)
            {
                case PanelKind.History:
                    var h = panelRows[i].Tag as BrowserEntry;
                    if (h == null) return;
                    ClosePanel(false);
                    Navigate(h.Url);
                    EnterFocus(false);
                    break;
                case PanelKind.DownloadAsk:
                    AnswerDownload(i == 0);
                    ClosePanel(true);
                    break;
            }
        }

        private void OnRowSecondary(int i)   // X
        {
            if (i < 0 || i >= panelRows.Count) return;
            if (panelKind == PanelKind.History)
            {
                var h = panelRows[i].Tag as BrowserEntry;
                if (h == null) return;
                history.Remove(h);
                SaveList(HistoryFile, history);
                panelRows = HistoryRows();
                listPanel.SetRows(panelRows, i);
            }
            else if (panelKind == PanelKind.Downloads)
            {
                var d = panelRows[i].Tag as DownloadItem;
                if (d == null || d.State == "downloading") return;
                downloads.Remove(d);
                panelRows = DownloadRows();
                listPanel.SetRows(panelRows, i);
            }
        }

        private void OnRowTertiary(int i)   // Y
        {
            if (panelKind != PanelKind.Downloads || i < 0 || i >= panelRows.Count) return;
            var d = panelRows[i].Tag as DownloadItem;
            if (d != null && d.State == "downloading" && d.Cancel != null) { d.Cancel(); Toast("Download cancelled"); }
        }

        // ----- the Start menu -----

        private void OpenMenu()
        {
            BarsShow();
            menuLink = null;
            menuLinkText = null;
            bool opened = false;
            Action open = delegate { if (opened) return; opened = true; ShowMainMenu(); };
            // a link under the highlight (or the cursor) gets its own entries at the top: ask the page, but never wait long for it
            if (view != null && view.IsReady && !IsHome)
            {
                string at = "";
                if (mode == Mode.Cursor)
                {
                    double k = CssPerDesign() / Scale;
                    at = (cursor.X * k).ToString("0.#", CultureInfo.InvariantCulture) + "," + (cursor.Y * k).ToString("0.#", CultureInfo.InvariantCulture);
                }
                view.EvaluateAsync("window.__nomadNav&&window.__nomadNav.link?window.__nomadNav.link(" + at + "):null", delegate(string json)
                {
                    if (opened) return;
                    var d = ParseJson(json);
                    if (d != null && d.ContainsKey("href"))
                    {
                        menuLink = d["href"] as string;
                        menuLinkText = d.ContainsKey("text") ? d["text"] as string : null;
                    }
                    open();
                });
                After(450, open);
            }
            else open();
        }

        private void OpenMainMenu() { ShowMainMenu(); }

        private void ShowMainMenu()
        {
            var bm = FindBookmark(view.Address);
            var items = new List<MenuEntry>();
            if (!string.IsNullOrEmpty(menuLink))
            {
                string link = menuLink;
                items.Add(new MenuEntry { Label = "Open link in new tab", Run = delegate { OpenInNewTab(link); return true; } });
                items.Add(new MenuEntry { Label = "Copy link", Run = delegate { CopyText(link, "Link copied"); return true; } });
            }
            items.Add(new MenuEntry { Label = "New tab", Run = delegate { ActivateTab(NewTab(null, true)); return true; } });
            items.Add(new MenuEntry { Label = "All tabs", Run = delegate { OpenTabSwitcher(); return true; } });
            items.Add(new MenuEntry { Label = "Close tab", Run = delegate { CloseTab(active); return true; } });
            if (!IsHome && view.Address.Length > 0)
            {
                items.Add(new MenuEntry { Label = bm == null ? "Add bookmark" : "Remove bookmark", Run = delegate { ToggleBookmark(); return true; } });
                items.Add(new MenuEntry { Label = "Find in page", Run = delegate { StartFind(); return true; } });
                items.Add(new MenuEntry { Label = "Page tools", Keep = true, Run = delegate { ShowPageTools(); return true; } });
            }
            else items.Add(new MenuEntry { Label = "Zoom in", Run = delegate { ZoomBy(ZoomStep); return true; } });
            items.Add(new MenuEntry { Label = "History", Run = delegate { OpenHistory(); return true; } });
            if (PageViews.IsEdge(view)) items.Add(new MenuEntry { Label = "Downloads", Run = delegate { OpenDownloads(); return true; } });
            items.Add(new MenuEntry { Label = "Phone link", Run = delegate { OpenLinkCard(); return true; } });
            items.Add(new MenuEntry { Label = "Settings", Run = delegate { OpenSettings(); return true; } });
            items.Add(new MenuEntry
            {
                Label = "Clear history and cookies",
                Run = delegate
                {
                    // it also signs you out of every site (and YouTube's web route): ask twice
                    if (clearArmedAt != 0 && unchecked(Environment.TickCount - clearArmedAt) < 8000) { clearArmedAt = 0; ClearData(); }
                    else { clearArmedAt = Environment.TickCount | 1; Toast("Open this entry again within 8 s to clear history and cookies"); }
                    return true;
                }
            });
            items.Add(new MenuEntry { Label = "Close browser", Run = delegate { return false; } });
            ShowMenu("Browser", items, false);
        }

        private void ShowPageTools()
        {
            var items = new List<MenuEntry>();
            items.Add(new MenuEntry { Label = active != null && active.Reader ? "Leave reader mode" : "Reader mode", Run = delegate { ToggleReader(); return true; } });
            string site = BrowserSettings.SiteKey(view.Address);
            if (site != null && PageViews.IsEdge(view))
            {
                bool phone = BrowserSettings.IsMobileSite(site);
                items.Add(new MenuEntry { Label = phone ? "Desktop version of this site" : "Phone version of this site", Run = delegate { ToggleMobileSite(site, !phone); return true; } });
            }
            items.Add(new MenuEntry { Label = "Zoom in", Run = delegate { ZoomBy(ZoomStep); return true; } });
            items.Add(new MenuEntry { Label = "Zoom out", Run = delegate { ZoomBy(-ZoomStep); return true; } });
            items.Add(new MenuEntry { Label = "Reset zoom", Run = delegate { ZoomBy(-zoom); return true; } });
            items.Add(new MenuEntry { Label = "Back", Keep = true, Run = delegate { ShowMainMenu(); return true; } });
            ShowMenu("Page tools", items, true);
        }

        // Material icons for the entries this file adds (0 = none here; SeleneBrowser.MenuIcon knows the rest)
        private static int ToolsIcon(string label)
        {
            if (label == "All tabs") return 0xE8D8;                         // tab
            if (label == "Find in page") return 0xE8B6;                     // search
            if (label == "Page tools") return 0xE869;                       // build
            if (label == "History") return 0xE889;                          // history
            if (label == "Downloads") return 0xE2C4;                        // file_download
            if (label == "Phone link") return 0xE32C;                     // smartphone
            if (label.StartsWith("Open link")) return 0xE89E;               // open_in_new
            if (label == "Copy link") return 0xE14D;                        // content_copy
            if (label.EndsWith("reader mode") || label == "Reader mode") return 0xE86F;   // chrome_reader_mode
            if (label.StartsWith("Phone version")) return 0xE32C;           // smartphone
            if (label.StartsWith("Desktop version")) return 0xE30C;         // desktop_windows
            if (label == "Back") return 0xE5C4;                             // arrow_back
            return 0;
        }

        private void OpenInNewTab(string url)
        {
            BrowserTab opener = active;
            BrowserTab t = NewTab(url, true);
            t.Opener = opener;   // B on its first page closes it and returns here
            ActivateTab(t);
        }

        private void CopyText(string text, string done)
        {
            try { Clipboard.SetText(text); Toast(done); }
            catch (Exception ex) { logger.Warn("SeleneBrowser: clipboard: " + ex.Message); Toast("Could not copy (the clipboard is busy)"); }
        }

        // ----- find in page -----

        private void StartFind()
        {
            BarsShow();
            keyboard.Open("Find in page", findQuery, delegate(string text)
            {
                findQuery = (text ?? "").Trim();
                if (findQuery.Length == 0) { EndFind(); EnterFocus(false); return; }
                findActive = true;
                EnterFocus(false);
                RunFind(false);
            }, null, null);
            UpdateHints();
        }

        private void RunFind(bool backwards)
        {
            if (!findActive || findQuery.Length == 0) return;
            string js = ReaderScript.FindScript(findQuery, backwards);
            view.EvaluateAsync(js, delegate(string json)
            {
                var d = ParseJson(json);
                int n = 0;
                if (d != null && d.ContainsKey("n")) { try { n = Convert.ToInt32(d["n"], CultureInfo.InvariantCulture); } catch { } }
                string shownQuery = findQuery.Length > 24 ? findQuery.Substring(0, 23) + "…" : findQuery;
                if (n == 0) Toast("Nothing found for \"" + shownQuery + "\"");
                else Toast("\"" + shownQuery + "\": " + n + (n == 1 ? " match" : " matches") + (n > 1 ? "   A next   Y previous" : ""));
            });
        }

        private void EndFind()
        {
            if (!findActive) return;
            findActive = false;
            view.ExecuteScript("try{window.getSelection().removeAllRanges()}catch(e){}");
            UpdateHints();
        }

        // while a search is open A / Y step through the matches, X searches again, B ends it; everything else works as usual
        private bool HandleFindKey(string b)
        {
            if (!findActive || mode != Mode.Focus) return false;
            switch (b)
            {
                case "A": if (limiter.IsFirst(b)) RunFind(false); return true;
                case "Y": if (limiter.IsFirst(b)) RunFind(true); return true;
                case "X": if (limiter.IsFirst(b)) StartFind(); return true;
                case "B": if (limiter.IsFirst(b)) { EndFind(); Toast("Search closed"); } return true;
            }
            return false;
        }

        // ----- reader mode -----

        private void ToggleReader()
        {
            if (active == null || !view.IsReady) return;
            if (active.Reader)
            {
                view.ExecuteScript("window.__lfReader&&window.__lfReader.off&&window.__lfReader.off()");
                active.Reader = false;
                Toast("Reader mode off");
                return;
            }
            Color bg = Theme.Background is SolidColorBrush ? ((SolidColorBrush)Theme.Background).Color : Theme.ScrimColor;
            Color ink = Theme.ColorOf("SeleneInkColor", "#FFFFFFFF");
            BrowserTab tab = active;
            view.EvaluateAsync(ReaderScript.Build(Theme.Hex(bg), Theme.Hex(ink), Theme.AccentHex), delegate(string r)
            {
                if (tab != active) return;
                if (r != null && r.StartsWith("ok", StringComparison.Ordinal))
                {
                    tab.Reader = true;
                    Toast("Reader mode: LT / RT scroll, B leaves it");
                }
                else Toast("No article found to read on this page");
            });
        }

        // B in reader mode leaves it, before anything else takes the press
        private bool LeaveReaderOnBack()
        {
            if (active == null || !active.Reader) return false;
            view.ExecuteScript("window.__lfReader&&window.__lfReader.off&&window.__lfReader.off()");
            active.Reader = false;
            Toast("Reader mode off");
            return true;
        }

        // ----- sites: zoom and phone version -----

        // a new site brings its remembered zoom (or none) back; the same site keeps whatever the tab has
        private void ApplySiteZoom(BrowserTab tab, string url)
        {
            string site = BrowserSettings.SiteKey(url);
            if (site == tab.ZoomSite) return;
            tab.ZoomSite = site;
            double z = site == null ? 0 : BrowserSettings.GetSiteZoom(site);
            tab.Zoom = z;
            if (tab == active) zoom = z;
            if (!tab.ViewDisposed) tab.View.SetZoomLevel(z);
        }

        private void SaveSiteZoom()
        {
            if (active == null) return;
            string site = BrowserSettings.SiteKey(view.Address);
            if (site == null) return;
            active.ZoomSite = site;
            BrowserSettings.SetSiteZoom(site, zoom);
        }

        private void ToggleMobileSite(string site, bool phone)
        {
            var edge = view as EdgeView;
            if (edge == null) { Toast("Needs the Edge engine"); return; }
            BrowserSettings.SetMobileSite(site, phone);
            edge.SetMobile(phone, delegate { edge.Reload(); });
            Toast(phone ? "Showing " + site + " as on a phone" : "Showing " + site + " as on a PC");
        }

        // ----- History -----

        private List<ListPanel.Row> HistoryRows()
        {
            var rows = new List<ListPanel.Row>();
            foreach (BrowserEntry h in history)
            {
                string title = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title.Trim();
                if (title.Length > 70) title = title.Substring(0, 69) + "…";
                rows.Add(new ListPanel.Row { Title = title, Sub = BrowserSettings.SiteKey(h.Url) ?? "", Icon = 0xE889, Tag = h });
            }
            return rows;
        }

        private void OpenHistory()
        {
            panelRows = HistoryRows();
            EnterPanel(PanelKind.History);
            listPanel.Open("History", "", panelRows, "Nothing visited yet.", "D-pad Select    A Open    X Remove    B Back");
            UpdateHints();
        }

        // ----- Downloads -----

        private List<ListPanel.Row> DownloadRows()
        {
            var rows = new List<ListPanel.Row>();
            foreach (DownloadItem d in downloads)
                rows.Add(new ListPanel.Row { Title = DownloadNames.Shown(d.Name), Sub = DownloadNames.Status(d), Icon = 0xE2C4, Tag = d });
            return rows;
        }

        private void RefreshDownloadRows()
        {
            for (int i = 0; i < panelRows.Count; i++)
            {
                var d = panelRows[i].Tag as DownloadItem;
                if (d != null) panelRows[i].Sub = DownloadNames.Status(d);
            }
            listPanel.Refresh();
        }

        private void OpenDownloads()
        {
            panelRows = DownloadRows();
            EnterPanel(PanelKind.Downloads);
            listPanel.Open("Downloads", "Files are saved to your Downloads folder.", panelRows, "No downloads yet.", "D-pad Select    X Remove    Y Cancel download    B Back");
            panelTimer.Start();
            UpdateHints();
        }

        // a page offered a file: ask on the screen first (a page can start a download on its own, so nothing is saved unasked)
        private void OnDownloadRequested(BrowserTab tab, DownloadRequest r)
        {
            if (!BrowserSettings.DownloadsAllowed)
            {
                r.Decide(false, null);
                if (shown && tab == active) Toast("Downloads are turned off in the browser's settings");
                return;
            }
            if (!shown || tab != active || panelKind != PanelKind.None || keyboard.IsOpen || mode == Mode.Menu || view.IsFullScreen)
            {
                r.Decide(false, null);
                if (shown) Toast("A download was refused: finish what you are doing first");
                return;
            }
            pendingDownload = r;
            string name = DownloadNames.Shown(r.Name);
            string note = name + (r.Size >= 0 ? "  (" + DownloadNames.Size(r.Size) + ")" : "")
                + (DownloadNames.IsProgram(r.Name) ? "\nThis is a program. Only save it if you trust the site: Windows checks it again before it runs." : "");
            panelRows = new List<ListPanel.Row>
            {
                new ListPanel.Row { Title = "Save to Downloads", Icon = 0xE2C4 },
                new ListPanel.Row { Title = "Don't save", Icon = 0xE5C9 }
            };
            EnterPanel(PanelKind.DownloadAsk);
            listPanel.Open("Save this file?", note, panelRows, "", "D-pad Select    A Choose    B Don't save");
            UpdateHints();
        }

        private void AnswerDownload(bool ok)
        {
            DownloadRequest r = pendingDownload;
            pendingDownload = null;
            if (r == null) return;
            if (!ok) { r.Decide(false, null); return; }
            var item = new DownloadItem { Name = r.Name, Path = r.Path, Size = r.Size };
            downloads.Insert(0, item);
            if (downloads.Count > 30) downloads.RemoveRange(30, downloads.Count - 30);
            r.Decide(true, item);
            Toast("Downloading " + DownloadNames.Shown(r.Name));
        }

        private void OnDownloadFinished(DownloadItem item)
        {
            if (!shown) return;
            string name = DownloadNames.Shown(item.Name);
            if (item.State == "done")
                Toast("Saved " + name + " to Downloads" + (DownloadNames.IsProgram(item.Name) ? " (Windows checks programs before they run)" : ""));
            else if (item.State == "failed") Toast("Download failed: " + name);
        }

        // ----- the tab switcher -----

        private List<TabSwitcher.TabCard> TabCards()
        {
            var cards = new List<TabSwitcher.TabCard>();
            foreach (BrowserTab t in tabs)
            {
                bool home = IsHomeUrl(t.Url);
                string title = t.Key == "news" ? "News" : (home ? "Home" : (string.IsNullOrWhiteSpace(t.Title) ? "Tab" : t.Title.Trim()));
                string sub = home ? "Start page" : (BrowserSettings.SiteKey(t.Url) ?? t.Url);
                cards.Add(new TabSwitcher.TabCard { Title = title, Sub = sub, Thumb = t.Thumb, Current = t == active });
            }
            return cards;
        }

        private void OpenTabSwitcher()
        {
            EnterPanel(PanelKind.Tabs);
            switcher.Open(TabCards(), Math.Max(0, tabs.IndexOf(active)), tabs.Count < MaxTabs);
            UpdateHints();
            // the page in front is photographed now; the others were photographed when they last loaded
            CaptureThumb(active, delegate { if (panelKind == PanelKind.Tabs) switcher.Open(TabCards(), switcher.Index, tabs.Count < MaxTabs); });
        }

        private void CloseTabFromSwitcher(int i)
        {
            if (i < 0 || i >= tabs.Count) return;
            CloseTab(tabs[i]);
            // closing the last tab leaves a fresh one: the switcher closes with it
            switcher.Open(TabCards(), Math.Min(i, tabs.Count - 1), tabs.Count < MaxTabs);
            mode = Mode.Menu;   // (CloseTab may have switched tabs, which puts the page back in front)
            cursorLayer.Visibility = Visibility.Collapsed;
            Script("hide()");
        }

        // a small picture of the page, for the switcher. Edge only; never throws.
        private void CaptureThumb(BrowserTab tab, Action done)
        {
            var edge = tab == null || tab.ViewDisposed ? null : tab.View as EdgeView;
            if (edge == null || !edge.IsReady) { if (done != null) done(); return; }
            edge.CapturePreview(delegate(byte[] png)
            {
                if (png != null && png.Length > 0)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.DecodePixelWidth = 720;
                        bi.StreamSource = new MemoryStream(png);
                        bi.EndInit();
                        bi.Freeze();
                        tab.Thumb = bi;
                    }
                    catch (Exception ex) { logger.Warn("SeleneBrowser: tab picture: " + ex.Message); }
                }
                if (done != null) done();
            });
        }
    }
}
