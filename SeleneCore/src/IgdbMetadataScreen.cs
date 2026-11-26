using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace Selene
{
    // State shared by the IGDB screens: the game being filled, where to go back, the pictures we fetched, and the two
    // choices made along the way. The pictures live in temp until the flow ends (a screen may still be showing them
    // while the next one opens). Nothing is written to the database until the cover step's final Apply.
    internal class IgdbSession
    {
        public readonly Game Game;
        public readonly Func<Screen> Back;
        public IgdbMetadataClient.IgdbImage Background;   // null = keep the current background
        public IgdbMetadataClient.IgdbImage Cover;        // null = keep the current cover
        public string Query;                              // the search as the user left it (B from a later step returns to it)
        public int HitIndex = -1;                         // the result that was chosen
        private readonly List<string> temp = new List<string>();
        private static bool swept;

        public IgdbSession(Game game, Func<Screen> back)
        {
            Game = game;
            Back = back;
        }

        // index 0 is always "keep current"; a remembered pick stays highlighted when a screen is re-opened with B
        public static int ChoiceIndex(List<IgdbMetadataClient.IgdbImage> candidates, IgdbMetadataClient.IgdbImage picked)
        {
            if (picked == null || candidates == null) return 0;
            for (int i = 0; i < candidates.Count; i++)
                if (string.Equals(candidates[i].ImageId, picked.ImageId, StringComparison.OrdinalIgnoreCase)) return i + 1;
            return 0;
        }

        public string Temp(string prefix, string extension)
        {
            var dir = Path.Combine(Path.GetTempPath(), "lanternfall-igdb");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (!swept)
            {
                swept = true;   // leftovers of searches that were backed out of while pictures were still arriving
                try { foreach (var old in Directory.GetFiles(dir)) if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { }
            }
            var path = Path.Combine(dir, prefix + "-" + Guid.NewGuid().ToString("N") + extension);
            lock (temp) temp.Add(path);
            return path;
        }

        public void Clean()
        {
            lock (temp)
            {
                foreach (var p in temp) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
                temp.Clear();
            }
        }
    }

    // IGDB search, as a controller screen: the game's name as the first query, a cover on each result, and the on-screen
    // keyboard to change the search. Uses Playnite's own IGDB backend (see IgdbMetadataClient); no desktop dialogs.
    public class IgdbSearchScreen : Screen
    {
        private readonly IgdbSession session;
        private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly List<Border> rows = new List<Border>();
        private readonly StackPanel list = new StackPanel();
        private readonly ScrollViewer scroller;
        private readonly Grid results;
        private readonly StackPanel keypad = new StackPanel();
        private readonly TextBlock queryText, status;
        private readonly KeyPad pad;
        private readonly PhoneType phone;   // View: type the search on a phone
        private List<IgdbMetadataClient.IgdbHit> hits = new List<IgdbMetadataClient.IgdbHit>();
        private string query;
        private int selected, generation;
        private bool busy, typing;
        private DateTime lastA = DateTime.MinValue;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.92); } }
        // Only while the keyboard is up. Always-on would make Playnite turn controller A's keyboard copy into
        // Enter -> "Start" (LoginScene.OnKey), which re-ran the search instead of picking the highlighted game.
        public override bool WantsText { get { return typing; } }
        public override bool MenuOnStart { get { return false; } }

        internal IgdbSearchScreen(IgdbSession session)
        {
            this.session = session;
            query = session.Query ?? (session.Game != null ? session.Game.Name ?? "" : "");

            var layout = new Grid { Margin = new Thickness(0, 70, 0, 110), Width = 1200, HorizontalAlignment = HorizontalAlignment.Center };
            var right = new StackPanel();
            right.Children.Add(Ui.Title(Lang.T("IGDB search")));
            right.Children.Add(Ui.Sub(Lang.T("Find the right game, choose its background, and fill in the details.")));

            var field = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 12, 22, 14), Margin = new Thickness(0, 24, 0, 0),
                Background = Ink.Brush("SeleneGlassColor", 0.8), BorderBrush = Ink.Brush("SeleneFocusColor"), BorderThickness = new Thickness(2)
            };
            queryText = new TextBlock
            {
                Text = query, FontSize = 23, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            field.Child = queryText;
            right.Children.Add(field);

            status = new TextBlock { FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(4, 10, 0, 6) };
            right.Children.Add(status);

            scroller = new ScrollViewer
            {
                Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
                CanContentScroll = false, MaxHeight = 560
            };
            results = PeekScroller.Wrap(scroller, list);
            right.Children.Add(results);

            pad = new KeyPad("Search", 58, 8);
            pad.Typed = Typed;
            pad.Erased = Erased;
            pad.Done = Done;
            pad.Root.Margin = new Thickness(0, 16, 0, 0);
            keypad.Visibility = Visibility.Collapsed;
            keypad.Children.Add(pad.Root);
            right.Children.Add(keypad);

            layout.Children.Add(right);
            Root.Children.Add(layout);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back"), Ui.Prompt("X", "Edit search"), Ui.Prompt("View", "Phone")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Choose a game"), Ui.Prompt("Y", "Search again")));
            phone = new PhoneType(this, "IGDB search", () => query, SetQuery, Done);
        }

        public override void Shown()
        {
            Ui.Play("OpenPanel.wav", 0.8);
            Search();
        }

        public override void Hidden() { generation++; phone.Close(); }

        // ---- search ----
        private void Search()
        {
            phone.Close();
            // a new search is a new game: never carry an earlier game's picture choices into its apply step
            session.Background = null;
            session.Cover = null;
            session.Clean();   // a new search supersedes the pictures of the previous one
            generation++;
            int gen = generation;
            busy = true;
            typing = false;
            query = query ?? "";
            session.Query = query;
            queryText.Text = query;
            status.Text = Lang.T("Searching IGDB…");
            results.Visibility = Visibility.Visible;
            keypad.Visibility = Visibility.Collapsed;
            pad.Focus(false);
            selected = 0;
            list.Children.Clear();
            rows.Clear();
            tiles.Clear();

            Task.Factory.StartNew(() =>
            {
                List<IgdbMetadataClient.IgdbHit> found = null;
                bool unreachable = false;
                try { found = IgdbMetadataClient.ParseSearch(IgdbMetadataClient.Search(query)); }
                catch (Exception ex) { unreachable = true; HomeNav.Log("igdb: search failed: " + ex.Message); }
                var answers = found == null ? new List<IgdbMetadataClient.IgdbHit>() : found.Take(20).ToList();
                ui.BeginInvoke(new Action(() =>
                {
                    if (gen != generation) return;
                    hits = answers;
                    busy = false;
                    if (session.HitIndex >= 0) { selected = session.HitIndex; session.HitIndex = -1; }   // B from a later step: back on the same result
                    Render();
                    status.Text = answers.Count == 0 ? Lang.T(unreachable ? "Couldn't reach IGDB." : "No games found") : "";
                }));
                if (answers.Count == 0) return;
                // Cover thumbnails are fetched while the list is already on screen (a search can take a moment).
                System.Threading.Tasks.Parallel.For(0, answers.Count, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                {
                    if (gen != generation) return;
                    var hit = answers[i];
                    if (string.IsNullOrEmpty(hit.CoverImageId)) return;
                    var file = session.Temp("hit", ".jpg");
                    try { IgdbMetadataClient.Download(IgdbMetadataClient.ImageUrl(hit.CoverImageId, "thumb"), file); }
                    catch { return; }
                    int at = i;
                    ui.BeginInvoke(new Action(() =>
                    {
                        if (gen != generation) return;
                        Ui.LoadInOrder(new List<string> { file }, 200, delegate(int j, ImageSource pic)
                        {
                            if (gen != generation || at >= tiles.Count || pic == null) return;
                            tiles[at].Face.Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
                        });
                    }));
                });
            });
        }

        private void Render()
        {
            list.Children.Clear();
            rows.Clear();
            tiles.Clear();
            for (int i = 0; i < hits.Count; i++)
            {
                var row = Build(i, hits[i]);
                int at = i;
                row.MouseLeftButtonUp += (sender, e) => { e.Handled = true; if (busy) return; selected = at; ShowFocus(); Choose(); };
                list.Children.Add(row);
                rows.Add(row);
                if (i < 12) Ui.Rise(row, 30 + i * 30, 12, 240);
            }
            if (selected >= rows.Count) selected = Math.Max(0, rows.Count - 1);
            ShowFocus();
        }

        private Border Build(int i, IgdbMetadataClient.IgdbHit hit)
        {
            var tile = new Tile(84, 112, 8, 4) { FocusScale = 1.05 };
            tile.Click = () => { if (busy) return; selected = i; ShowFocus(); Choose(); };
            tiles.Add(tile);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
            words.Children.Add(new TextBlock
            {
                Text = hit.Name, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body,
                Foreground = Ink.Brush("SeleneInkColor"), TextTrimming = TextTrimming.CharacterEllipsis
            });
            var sub = new List<string>();
            if (hit.Year.HasValue) sub.Add(Convert.ToString(hit.Year.Value));
            if (hit.Developers.Count > 0) sub.Add(string.Join(", ", hit.Developers.Take(2).ToArray()));
            if (hit.Platforms.Count > 0) sub.Add(string.Join(", ", hit.Platforms.Take(3).ToArray()));
            words.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", sub.ToArray()), FontSize = 15, FontFamily = Ui.Body,
                Foreground = Ink.Brush("SeleneInkColor", 0.6), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0)
            });

            var line = new DockPanel();
            var cover = new Border { Child = tile.Root, Width = 84, Margin = new Thickness(0, 0, 4, 0) };
            DockPanel.SetDock(cover, Dock.Left);
            line.Children.Add(cover);
            line.Children.Add(words);

            return new Border
            {
                Child = line, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 16, 10),
                Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                Background = Ink.Brush("SeleneGlassColor", 0.72)
            };
        }

        private void ShowFocus()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                bool on = i == selected;
                rows[i].BorderBrush = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
                rows[i].Background = Ink.Brush("SeleneGlassColor", on ? 0.95 : 0.72);
                if (i < tiles.Count) tiles[i].Focus(on);
            }
            if (selected >= rows.Count) return;
            try
            {
                scroller.UpdateLayout();
                var row = rows[selected];
                var top = row.TranslatePoint(new Point(0, 0), list).Y;
                double want = scroller.VerticalOffset, pad = PeekScroller.Band;
                if (top - pad * 0.6 < scroller.VerticalOffset) want = Math.Max(0, top - pad * 0.6);
                else if (top + row.ActualHeight > scroller.VerticalOffset + scroller.ViewportHeight - pad)
                    want = top + row.ActualHeight - (scroller.ViewportHeight - pad);
                if (Math.Abs(want - scroller.VerticalOffset) > 0.5) scroller.ScrollToVerticalOffset(want);
            }
            catch { }
        }

        // ---- typing ----
        private void OpenPad()
        {
            typing = true;
            pad.After(query);
            results.Visibility = Visibility.Collapsed;
            keypad.Visibility = Visibility.Visible;
            status.Text = Lang.T("Edit search");
            ui.BeginInvoke(new Action(() => pad.Focus(true)));
        }

        private void Typed(string s)
        {
            Ui.Key();
            if (query.Length >= 120) return;
            query += s;
            queryText.Text = query;
            pad.After(query);
            phone.Changed();
        }

        // the phone typed: the whole search now
        private void SetQuery(string text)
        {
            text = text ?? "";
            query = text.Length > 120 ? text.Substring(0, 120) : text;
            queryText.Text = query;
            pad.After(query);
            if (query != text) phone.Changed();
        }

        private void Erased()
        {
            Ui.Key();
            if (query.Length == 0) return;
            query = query.Substring(0, query.Length - 1);
            queryText.Text = query;
            pad.After(query);
            phone.Changed();
        }

        private void Done()
        {
            Ui.Confirm();
            Search();
        }

        public override void Type(string text)
        {
            if (!typing) return;
            foreach (var c in text ?? "")
            {
                if (query.Length >= 120) break;
                query += c;
            }
            queryText.Text = query;
            pad.After(query);
            phone.Changed();
        }

        public override void Erase()
        {
            if (!typing) return;
            Erased();
        }

        // ---- picking ----
        private void Choose()
        {
            if (busy || hits == null || selected < 0 || selected >= hits.Count) return;
            var hit = hits[selected];
            session.HitIndex = selected;
            int gen = ++generation;
            busy = true;
            status.Text = Lang.T("Loading details…");
            Task.Factory.StartNew(() =>
            {
                IgdbMetadataClient.IgdbGame data = null;
                try { data = IgdbMetadataClient.ParseGame(IgdbMetadataClient.Game(hit.Id)); }
                catch (Exception ex) { HomeNav.Log("igdb: details failed: " + ex.Message); }
                var found = data;
                ui.BeginInvoke(new Action(() =>
                {
                    if (gen != generation) return;
                    busy = false;
                    if (found == null) { status.Text = Lang.T("Couldn't reach IGDB."); return; }
                    Ui.Confirm();
                    LoginScene.Show(new IgdbImageScreen(session, found), 1);
                }));
            });
        }

        public override void Handle(string b)
        {
            if (b == "A") lastA = DateTime.Now;
            if (b == "Back" && !busy)
            {
                if (!typing) { Ui.Key(); OpenPad(); }   // the phone types into the search field: open it first
                phone.Toggle();
                return;
            }
            if (typing)
            {
                // Playnite's late keyboard copy of a pad A arrives as Enter -> Start; it must not finish the typing early
                if (b == "Start" && (DateTime.Now - lastA).TotalMilliseconds < 500) return;
                if (b == "B")
                {
                    Ui.Back();
                    typing = false;
                    keypad.Visibility = Visibility.Collapsed;
                    results.Visibility = Visibility.Visible;
                    status.Text = "";
                    ShowFocus();
                    return;
                }
                if (b.StartsWith("DPad")) { pad.Move(b); return; }
                pad.Press(b);
                return;
            }
            // B cancels even while a lookup is running (a slow backend must not trap the user)
            if (b == "B") { Ui.Back(); session.Clean(); LoginScene.Show(session.Back(), -1); return; }
            if (busy) return;
            if (b == "X") { Ui.Key(); OpenPad(); return; }
            if (b == "Y") { Ui.Key(); Search(); return; }
            if (b == "DPadUp" || b == "DPadDown")
            {
                int next = selected + (b == "DPadDown" ? 1 : -1);
                if (next < 0 || next >= rows.Count) { if (selected < rows.Count) Ui.Shake(rows[selected]); return; }
                selected = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "A") Choose();
        }
    }

    // Step 1 of the image choice: the wide artwork IGDB has for the game (screenshots as a fallback), each with its
    // resolution, plus a "keep current" choice. A only remembers the pick and opens the cover picker; nothing is
    // written to the database until the cover step's Apply.
    public class IgdbImageScreen : Screen
    {
        private readonly IgdbSession session;
        private readonly IgdbMetadataClient.IgdbGame data;
        private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        private readonly List<IgdbMetadataClient.IgdbImage> candidates;
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly List<TextBlock> captions = new List<TextBlock>();
        private readonly WrapPanel grid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock status;
        private int selected, generation;
        private bool moving;   // a second A (or a click + pad copy) must not open two cover screens
        private DateTime readyAt = DateTime.MinValue;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.92); } }
        public override bool MenuOnStart { get { return false; } }

        internal IgdbImageScreen(IgdbSession session, IgdbMetadataClient.IgdbGame data)
        {
            this.session = session;
            this.data = data;
            candidates = IgdbMetadataClient.BackgroundCandidates(data);
            selected = IgdbSession.ChoiceIndex(candidates, session.Background);

            var layout = new Grid { Margin = new Thickness(0, 70, 0, 110), Width = 1240, HorizontalAlignment = HorizontalAlignment.Center };
            var body = new StackPanel();
            body.Children.Add(Ui.Title(Lang.T("Select background image for") + " " + (data.Name ?? "")));
            body.Children.Add(Ui.Sub(Lang.T("Choose a background")));
            status = new TextBlock { FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(2, 12, 0, 8) };
            body.Children.Add(status);
            // layout stays in device-independent units; only image decoding converts them (PixelSize.Width)
            grid.ItemWidth = 310;
            grid.ItemHeight = 176;
            body.Children.Add(grid);
            layout.Children.Add(body);
            Root.Children.Add(layout);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Next: cover image")));
            Render();
        }

        public override void Shown()
        {
            Ui.Play("OpenPanel.wav", 0.8);
            readyAt = DateTime.Now.AddMilliseconds(300);   // swallow the double A that opened this screen
        }
        public override void Hidden() { generation++; }

        private void Render()
        {
            grid.Children.Clear();
            tiles.Clear();
            captions.Clear();
            for (int i = 0; i <= candidates.Count; i++)
            {
                bool keep = i == 0;
                var image = keep ? null : candidates[i - 1];
                var tile = new Tile(236, 132, 10, 4) { FocusScale = 1.06 };
                int at = i;
                tile.Click = () => { selected = at; ShowFocus(); Next(); };
                if (keep)
                {
                    var mark = new TextBlock { Text = Ui.Icon(0xe5ca), FontFamily = Ui.Icons, FontSize = 34, Foreground = Ink.Brush("SeleneInkColor", 0.8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    tile.Face.Child = mark;
                }
                var caption = new TextBlock
                {
                    Text = keep ? Lang.T("Keep current background") : image.Width + "×" + image.Height,
                    FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7),
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
                };
                tiles.Add(tile);
                captions.Add(caption);
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 6, 4, 6) };
                cell.Children.Add(tile.Root);
                cell.Children.Add(caption);
                grid.Children.Add(cell);
            }
            // Thumbnails are network calls: fetch them off the UI thread, then let Ui.LoadInOrder decode and paint them.
            int gen = ++generation;
            Task.Factory.StartNew(() =>
            {
                var files = new List<string>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (gen != generation) return;   // the user went back: no more downloads for a page that is gone
                    var image = candidates[i];
                    var file = session.Temp("bg-thumb", ".jpg");
                    try { IgdbMetadataClient.Download(IgdbMetadataClient.ImageUrl(image.ImageId, "screenshot_med"), file); }
                    catch { file = null; }
                    files.Add(file);
                }
                ui.BeginInvoke(new Action(() =>
                {
                    if (gen != generation) return;
                    Ui.LoadInOrder(files, 480, delegate(int i, ImageSource pic)
                    {
                        if (gen != generation || i < 0 || i + 1 >= tiles.Count || pic == null) return;
                        tiles[i + 1].Face.Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
                    });
                }));
            });
            ShowFocus();
        }

        private void ShowFocus()
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].Focus(i == selected);
                captions[i].Foreground = i == selected ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.7);
            }
        }

        // how many thumbnails the WrapPanel really put on a row (depends on the available width)
        private int Columns()
        {
            if (grid.ActualWidth <= 0 || grid.ItemWidth <= 0) return 4;
            return Math.Max(1, (int)Math.Floor(grid.ActualWidth / grid.ItemWidth));
        }

        // A remembers the background and moves on to the cover step; the database is untouched until that step's Apply.
        private void Next()
        {
            if (moving) return;
            moving = true;
            Ui.Confirm();
            session.Background = selected == 0 ? null : candidates[selected - 1];
            LoginScene.Show(new IgdbCoverScreen(session, data), 1);
        }

        // Every field Playnite's IGDB add-on fills, written the way its metadata downloader writes it. Platforms, tags,
        // categories, your score and the screenshots library are deliberately left alone: the add-on doesn't set them
        // either (artwork/screenshots here are only candidates for the background or cover you pick).
        internal static bool ApplyOnUi(IgdbSession session, IgdbMetadataClient.IgdbGame data, string background, string cover)
        {
            var api = HomeNav.Api;
            if (api == null || session.Game == null) return false;
            var g = api.Database.Games.Get(session.Game.Id);
            if (g == null) return false;
            var db = api.Database;
            if (!string.IsNullOrWhiteSpace(data.Name) && g.Name != data.Name.Trim())
            {
                g.Name = data.Name.Trim();
                GameInfo.Forget(g.Id);   // a new name can match where the old one didn't
                try { var none = Path.Combine(SelenePaths.Media("Trailers"), g.Id + ".nosquare"); if (File.Exists(none)) File.Delete(none); } catch { }   // square art may be found under the new name
            }
            if (!string.IsNullOrWhiteSpace(data.Description)) g.Description = data.Description;
            if (data.ReleaseDate.HasValue) g.ReleaseDate = new ReleaseDate(data.ReleaseDate.Value);
            if (data.CriticScore.HasValue) g.CriticScore = data.CriticScore;
            if (data.CommunityScore.HasValue) g.CommunityScore = data.CommunityScore;
            if (data.Genres.Count > 0) g.GenreIds = db.Genres.Add(data.Genres).Select(x => x.Id).ToList();
            if (data.Developers.Count > 0) g.DeveloperIds = db.Companies.Add(data.Developers).Select(x => x.Id).ToList();
            if (data.Publishers.Count > 0) g.PublisherIds = db.Companies.Add(data.Publishers).Select(x => x.Id).ToList();
            if (data.Features.Count > 0) g.FeatureIds = db.Features.Add(data.Features).Select(x => x.Id).ToList();
            if (data.AgeRatings.Count > 0) g.AgeRatingIds = db.AgeRatings.Add(data.AgeRatings).Select(x => x.Id).ToList();
            if (data.Series.Count > 0) g.SeriesIds = db.Series.Add(data.Series).Select(x => x.Id).ToList();
            if (data.Links.Count > 0)
            {
                var links = new ObservableCollection<Link>();
                foreach (var l in data.Links)
                    if (!string.IsNullOrWhiteSpace(l.Url))
                        links.Add(new Link(string.IsNullOrWhiteSpace(l.Name) ? "IGDB" : l.Name, l.Url));
                if (links.Count > 0) g.Links = links;
            }
            if (!string.IsNullOrEmpty(background))
            {
                g.BackgroundImage = db.AddFile(background, g.Id);
                try { File.Copy(background, Path.Combine(SelenePaths.Media("Hero"), g.Id + ".jpg"), true); }   // GameFacts.HeroPath wins over the background
                catch (Exception ex) { HomeNav.Log("igdb: hero copy: " + ex.Message); }
            }
            if (!string.IsNullOrEmpty(cover)) g.CoverImage = db.AddFile(cover, g.Id);
            g.Modified = DateTime.Now;
            db.Games.Update(g);
            HomeNav.Log("igdb: filled " + g.Name + " (name, description, release, scores, companies, genres, images)");
            return true;
        }

        public override void Handle(string b)
        {
            if (DateTime.Now < readyAt) return;
            if (b == "B") { Ui.Back(); LoginScene.Show(new IgdbSearchScreen(session), -1); return; }
            if (b == "DPadLeft" || b == "DPadRight")
            {
                int next = selected + (b == "DPadRight" ? 1 : -1);
                if (next < 0 || next >= tiles.Count) { Ui.Shake(tiles[selected].Root); return; }
                selected = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "DPadUp" || b == "DPadDown")
            {
                int step = Columns();
                int next = selected + (b == "DPadDown" ? step : -step);
                if (b == "DPadDown" && next >= tiles.Count && selected / step < (tiles.Count - 1) / step) next = tiles.Count - 1;   // a short last row
                if (next < 0 || next >= tiles.Count) { Ui.Shake(tiles[selected].Root); return; }
                selected = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "A") Next();
        }
    }

    // Step 2 of the image choice: the official IGDB cover plus any portrait artwork/screenshots the backend has, with
    // "keep current cover" first. A applies every field in one write; B goes back to the background step. The cover is
    // the user's pick (or untouched), never silently taken from data.CoverImageId.
    public class IgdbCoverScreen : Screen
    {
        private readonly IgdbSession session;
        private readonly IgdbMetadataClient.IgdbGame data;
        private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        private readonly List<IgdbMetadataClient.IgdbImage> candidates;
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly List<TextBlock> captions = new List<TextBlock>();
        private readonly WrapPanel grid = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock status;
        private int selected, generation;
        private bool busy;
        private DateTime readyAt = DateTime.MinValue;

        public override bool Overlay { get { return true; } }
        public override Brush OverlayShade { get { return Ink.Brush("SeleneScrimColor", 0.92); } }
        public override bool MenuOnStart { get { return false; } }

        internal IgdbCoverScreen(IgdbSession session, IgdbMetadataClient.IgdbGame data)
        {
            this.session = session;
            this.data = data;
            candidates = IgdbMetadataClient.CoverCandidates(data);
            selected = IgdbSession.ChoiceIndex(candidates, session.Cover);

            var layout = new Grid { Margin = new Thickness(0, 70, 0, 110), Width = 1240, HorizontalAlignment = HorizontalAlignment.Center };
            var body = new StackPanel();
            body.Children.Add(Ui.Title(Lang.T("Select cover image for") + " " + (data.Name ?? "")));
            body.Children.Add(Ui.Sub(Lang.T("Choose a cover")));
            status = new TextBlock { FontSize = 16, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.6), Margin = new Thickness(2, 12, 0, 8) };
            body.Children.Add(status);
            grid.ItemWidth = 160;
            grid.ItemHeight = 214;
            body.Children.Add(grid);
            layout.Children.Add(body);
            Root.Children.Add(layout);
            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back to background")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Apply")));
            Render();
        }

        public override void Shown()
        {
            Ui.Play("OpenPanel.wav", 0.8);
            readyAt = DateTime.Now.AddMilliseconds(300);   // the pad A that opened this step must not apply it
        }
        public override void Hidden() { generation++; }

        private void Render()
        {
            grid.Children.Clear();
            tiles.Clear();
            captions.Clear();
            for (int i = 0; i <= candidates.Count; i++)
            {
                bool keep = i == 0;
                var image = keep ? null : candidates[i - 1];
                var tile = new Tile(130, 170, 8, 4) { FocusScale = 1.06 };
                int at = i;
                tile.Click = () => { if (busy) return; selected = at; ShowFocus(); Apply(); };
                if (keep)
                {
                    var mark = new TextBlock { Text = Ui.Icon(0xe5ca), FontFamily = Ui.Icons, FontSize = 34, Foreground = Ink.Brush("SeleneInkColor", 0.8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    tile.Face.Child = mark;
                }
                var caption = new TextBlock
                {
                    Text = keep ? Lang.T("Keep current cover") : (image.Kind == "cover" ? Lang.T("IGDB cover") : image.Width + "×" + image.Height),
                    FontSize = 14, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.7),
                    HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 150, Margin = new Thickness(0, 4, 0, 0)
                };
                tiles.Add(tile);
                captions.Add(caption);
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 6, 4, 6) };
                cell.Children.Add(tile.Root);
                cell.Children.Add(caption);
                grid.Children.Add(cell);
            }
            // Like the background step: thumbnails are network calls, fetched off the UI thread and painted in order.
            int gen = ++generation;
            Task.Factory.StartNew(() =>
            {
                var files = new List<string>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (gen != generation) return;
                    var file = session.Temp("cover-thumb", ".jpg");
                    try { IgdbMetadataClient.Download(IgdbMetadataClient.ImageUrl(candidates[i].ImageId, "cover_big"), file); }
                    catch { file = null; }
                    files.Add(file);
                }
                ui.BeginInvoke(new Action(() =>
                {
                    if (gen != generation) return;
                    Ui.LoadInOrder(files, 320, delegate(int i, ImageSource pic)
                    {
                        if (gen != generation || i < 0 || i + 1 >= tiles.Count || pic == null) return;
                        tiles[i + 1].Face.Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill };
                    });
                }));
            });
            ShowFocus();
        }

        private void ShowFocus()
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].Focus(i == selected);
                captions[i].Foreground = i == selected ? Ink.Brush("SeleneFocusColor") : Ink.Brush("SeleneInkColor", 0.7);
            }
        }

        private int Columns()
        {
            if (grid.ActualWidth <= 0 || grid.ItemWidth <= 0) return 6;
            return Math.Max(1, (int)Math.Floor(grid.ActualWidth / grid.ItemWidth));
        }

        private void Apply()
        {
            if (busy) return;
            int gen = ++generation;
            busy = true;
            status.Text = Lang.T("Applying metadata…");
            var background = session.Background;
            var picked = selected == 0 ? null : candidates[selected - 1];
            Task.Factory.StartNew(() =>
            {
                string bgFile = null, coverFile = null;
                if (background != null)
                {
                    // Commit the path only after the file is really on disk: db.AddFile on a missing file would throw
                    // before the game is updated, losing every other field too.
                    var path = session.Temp("bg", ".jpg");
                    try
                    {
                        IgdbMetadataClient.Download(IgdbMetadataClient.ImageUrl(background.ImageId, background.Width > 1920 ? "original" : "1080p"), path);
                        bgFile = path;
                    }
                    catch (Exception ex) { HomeNav.Log("igdb: background download failed: " + ex.Message); }
                }
                if (picked != null)
                {
                    var path = session.Temp("cover", ".jpg");
                    try
                    {
                        // the grid shows a small cover_big thumb, but the saved cover should be full resolution
                        var size = picked.Kind == "cover" ? "original" : (picked.Width > 1920 ? "original" : "1080p");
                        IgdbMetadataClient.Download(IgdbMetadataClient.ImageUrl(picked.ImageId, size), path);
                        coverFile = path;
                    }
                    catch (Exception ex) { HomeNav.Log("igdb: cover download failed: " + ex.Message); }
                }
                var bg = bgFile;
                var art = coverFile;
                // a picture that was asked for and did not arrive: the text still goes in, but the notice must say so
                bool missing = (background != null && bg == null) || (picked != null && art == null);
                ui.BeginInvoke(new Action(() =>
                {
                    if (gen != generation) return;   // B cancelled the wait: nothing is written
                    bool applied = false;
                    try { applied = IgdbImageScreen.ApplyOnUi(session, data, bg, art); }
                    catch (Exception ex) { HomeNav.Log("igdb: saving metadata failed: " + ex.Message); }
                    string done = Lang.T("Metadata from IGDB applied to") + " " + (session.Game != null ? session.Game.Name : "");
                    Ui.Notice(!applied ? Lang.T("Couldn't save the metadata.")
                        : missing ? done + ". " + Lang.T("Some pictures could not be downloaded.") : done, 5000);
                    session.Clean();
                    LoginScene.Show(session.Back(), -1);
                }));
            });
        }

        public override void Handle(string b)
        {
            if (busy && b == "B")
            {
                // the downloads can take up to a minute: B gives up the wait (the late result is dropped, nothing is written)
                generation++;
                busy = false;
                Ui.Back();
                session.Cover = selected == 0 ? null : candidates[selected - 1];
                LoginScene.Show(new IgdbImageScreen(session, data), -1);
                return;
            }
            if (busy || DateTime.Now < readyAt) return;
            if (b == "B")
            {
                Ui.Back();
                session.Cover = selected == 0 ? null : candidates[selected - 1];
                LoginScene.Show(new IgdbImageScreen(session, data), -1);
                return;
            }
            if (b == "DPadLeft" || b == "DPadRight")
            {
                int next = selected + (b == "DPadRight" ? 1 : -1);
                if (next < 0 || next >= tiles.Count) { Ui.Shake(tiles[selected].Root); return; }
                selected = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "DPadUp" || b == "DPadDown")
            {
                int step = Columns();
                int next = selected + (b == "DPadDown" ? step : -step);
                if (b == "DPadDown" && next >= tiles.Count && selected / step < (tiles.Count - 1) / step) next = tiles.Count - 1;   // a short last row
                if (next < 0 || next >= tiles.Count) { Ui.Shake(tiles[selected].Root); return; }
                selected = next;
                Ui.Tick();
                ShowFocus();
                return;
            }
            if (b == "A") Apply();
        }
    }
}
