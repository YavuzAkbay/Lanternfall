using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Selene
{
    // Finds a YouTube channel from what a person types: its @handle, its name without spaces, a link to it, or its channel id.
    // Only youtube.com pages are ever read, and only the one page for the channel asked for.
    public static class YouTubeLookup
    {
        private static readonly Regex ChannelId = new Regex("^UC[0-9A-Za-z_-]{22}$");
        private static readonly Regex Word = new Regex("^[A-Za-z0-9._-]{1,60}$");

        // the youtube.com page to read for what was typed, or null when it isn't a channel we can look up
        public static string PageFor(string typed)
        {
            typed = (typed ?? "").Trim();
            if (typed.Length == 0 || typed.Length > 300) return null;
            if (typed.IndexOf('/') >= 0 || typed.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Uri uri;
                if (!Uri.TryCreate(typed.IndexOf("://", StringComparison.Ordinal) < 0 ? "https://" + typed : typed, UriKind.Absolute, out uri)) return null;
                var host = uri.Host.ToLowerInvariant();
                if (host != "youtube.com" && host != "www.youtube.com" && host != "m.youtube.com") return null;
                var parts = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
                if (parts.Length == 0) return null;
                if (parts[0].StartsWith("@")) return Handle(parts[0].Substring(1));
                if (parts.Length >= 2 && parts[0] == "channel" && ChannelId.IsMatch(parts[1])) return "https://www.youtube.com/channel/" + parts[1];
                if (parts.Length >= 2 && (parts[0] == "c" || parts[0] == "user") && Word.IsMatch(parts[1])) return "https://www.youtube.com/" + parts[0] + "/" + parts[1];
                return null;   // (a video link, a playlist, the home page)
            }
            if (ChannelId.IsMatch(typed)) return "https://www.youtube.com/channel/" + typed;
            return Handle(typed.TrimStart('@').Replace(" ", ""));
        }

        private static string Handle(string handle) { return Word.IsMatch(handle) ? "https://www.youtube.com/@" + handle : null; }

        // what the channel's page says about it: its id (from its feed link), name and picture; null when the page isn't a channel's
        public static Personalize.Channel Parse(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var id = Regex.Match(html, "feeds/videos\\.xml\\?channel_id=(UC[0-9A-Za-z_-]{22})");
            var name = Meta(html, "og:title");
            if (!id.Success || string.IsNullOrWhiteSpace(name)) return null;
            if (name.Length > 80) name = name.Substring(0, 80);
            var handle = Regex.Match(html, "<link rel=\"canonical\" href=\"https://www\\.youtube\\.com/(@[A-Za-z0-9._-]+)\"");
            var image = Meta(html, "og:image");
            Uri picture;
            // only Google's own picture hosts; the size asked for is the small one the cards and the Browser need
            if (!Uri.TryCreate(image, UriKind.Absolute, out picture) || picture.Scheme != "https" || !(picture.Host.EndsWith(".googleusercontent.com") || picture.Host.EndsWith(".ggpht.com"))) image = "";
            else image = Regex.Replace(image, "=s\\d+", "=s176");
            return new Personalize.Channel
            {
                Id = id.Groups[1].Value, Name = name.Trim(), Handle = handle.Success ? handle.Groups[1].Value : "",
                About = handle.Success ? handle.Groups[1].Value : "Added by you", Avatar = image, Topics = new string[0]
            };
        }

        private static string Meta(string html, string property)
        {
            var m = Regex.Match(html, "<meta property=\"" + Regex.Escape(property) + "\" content=\"([^\"]*)\"");
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        }

        // reads the page (off the UI thread: it waits for the network); null when there's no such channel or YouTube didn't answer
        public static Personalize.Channel Find(string typed)
        {
            var page = PageFor(typed);
            if (page == null) return null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(page);
                request.Timeout = 12000; request.ReadWriteTimeout = 12000;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
                request.Headers["Accept-Language"] = "en-US,en;q=0.8";
                request.Headers["Cookie"] = "CONSENT=YES+1; SOCS=CAI";   // (no cookie question instead of the channel's page)
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var response = request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[16384]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { memory.Write(buffer, 0, count); if (memory.Length > 6 * 1024 * 1024) break; }
                    return Parse(System.Text.Encoding.UTF8.GetString(memory.ToArray()));
                }
            }
            catch (Exception ex)
            {
                var web = ex as WebException;
                if (web != null && web.Response != null) { try { web.Response.Close(); } catch { } }
                HomeNav.Log("personalize: channel lookup: " + ex.Message);
                return null;
            }
        }
    }

    // "Add a channel", laid out like the profile name step: the title, a rounded field with a caret, the same keyboard under it.
    // Typing works from the keyboard on screen, a real keyboard, or the phone (View); LB pastes; Add (Start) looks the channel up
    // before it is kept.
    public class AddChannelScreen : Screen
    {
        private readonly Func<Screen> back;
        private readonly Action<Personalize.Channel> added;
        private readonly KeyPad pad;
        private readonly PhoneType phone;
        private readonly TextBlock value = new TextBlock { FontSize = 28, FontFamily = Ui.Display, Foreground = Ink.Brush("SeleneInkColor"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        private readonly TextBlock hint = new TextBlock { Text = "@handle, name or link", FontSize = 24, FontFamily = Ui.Body, Foreground = Ink.Brush("SeleneInkColor", 0.4), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly TextBlock note;
        private readonly Border field;
        private string typed = "";
        private bool busy, closed;
        private const int MaxLength = 300, Visible = 34;   // (what the field shows of a long link: its last characters)
        public override bool WantsText { get { return true; } }
        public override bool MenuOnStart { get { return false; } }

        public AddChannelScreen(Func<Screen> back, Action<Personalize.Channel> added)
        {
            this.back = back; this.added = added;
            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 110, 0, 0) };
            head.Children.Add(Ui.Title("Which channel?"));
            note = Ui.Sub("Type its @handle or name, or paste a link. We look it up on YouTube first.");
            head.Children.Add(note);
            Root.Children.Add(head);

            var layout = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 300, 0, 0) };
            var caret = new Rectangle { Width = 2, Height = 30, Fill = Ink.Brush("GlyphColor"), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            caret.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1100),
                KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)), new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.55)) }
            });
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(26, 0, 26, 0) };
            line.Children.Add(value);
            line.Children.Add(caret);
            var inside = new Grid();
            inside.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 0), Children = { hint } });
            inside.Children.Add(line);
            field = new Border
            {
                Width = 760, Height = 68, CornerRadius = new CornerRadius(34), Child = inside, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true,
                Background = Ink.Brush("SeleneInkColor", 0.1), BorderBrush = Ink.Brush("SeleneFocusColor"), BorderThickness = new Thickness(2)
            };
            layout.Children.Add(field);
            // the same keys as the name step (a letter row has an underscore where the name step has an apostrophe: handles use one)
            pad = new KeyPad("Add", 70, 10, new[] { "1234567890", "QWERTYUIOP", "ASDFGHJKL_", "ZXCVBNM-." }, false);
            pad.Typed = Type; pad.Erased = Erase; pad.Done = Add; pad.Root.Margin = new Thickness(0, 40, 0, 0);
            layout.Children.Add(pad.Root);
            Root.Children.Add(layout);

            Root.Children.Add(Ui.Prompts(false, Ui.Prompt("B", "Back"), Ui.Prompt("LB", "Paste"), Ui.Prompt("View", "Type on phone")));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Select")));
            phone = new PhoneType(this, "Add a YouTube channel", () => typed, SetTyped, Add);
            Show();
        }

        private void Show()
        {
            value.Text = typed.Length <= Visible ? typed : "…" + typed.Substring(typed.Length - Visible);
            hint.Visibility = typed.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // the phone (or Paste) typed: the whole text now
        private void SetTyped(string text)
        {
            text = text ?? "";
            typed = text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
            Show();
            pad.After(typed);
            if (typed != text) phone.Changed();
        }

        public override void Hidden() { closed = true; phone.Close(); }
        public override void Shown() { pad.Focus(true); }
        public override void Type(string text) { if (busy) return; if (typed.Length + text.Length <= MaxLength) typed += text; Show(); pad.After(typed); phone.Changed(); }
        public override void Erase() { if (busy) return; if (typed.Length > 0) typed = typed.Substring(0, typed.Length - 1); Type(""); }

        private void Paste()
        {
            if (busy) return;
            try
            {
                var pasted = (Clipboard.GetText() ?? "").Trim();
                if (pasted.Length == 0) { note.Text = "Nothing to paste. Copy a channel's name or link first."; return; }
                if (pasted.Length > MaxLength) { note.Text = "That is too long for a channel name or link."; return; }
                SetTyped(pasted);
                Ui.Confirm();
            }
            catch (Exception ex) { note.Text = ex.Message; }
        }

        private void Add()
        {
            if (busy) return;
            if (YouTubeLookup.PageFor(typed) == null)
            {
                note.Text = typed.Trim().Length == 0 ? "Type or paste a channel first." : "That isn't a channel name or link. Try its @handle, or paste its link.";
                return;
            }
            busy = true;
            note.Text = "Looking it up…";
            var wanted = typed;
            var dispatcher = Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                Personalize.Channel found = null;
                try { found = YouTubeLookup.Find(wanted); } catch (Exception ex) { HomeNav.Log("personalize: channel: " + ex.Message); }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    busy = false;
                    if (closed) return;
                    if (found == null) { note.Text = "Couldn't find that channel. Check the spelling, or paste a link to it."; return; }
                    added(found);
                }));
            });
        }

        public override void Handle(string button)
        {
            if (button == "Back") { phone.Toggle(); return; }
            if (button == "LeftShoulder") { Paste(); return; }
            if (button == "B") { if (!busy) { Ui.Back(); LoginScene.Show(back(), -1); } return; }
            if (button.StartsWith("DPad")) { pad.Move(button); return; }
            pad.Press(button);
        }
    }
}
