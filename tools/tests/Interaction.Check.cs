using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Selene;
// the Browser add-on's own channel type (Browser\YouTubeModels.cs), to see that what Core writes is what it reads
public class BrowserChannel { public string Id { get; set; } public string Name { get; set; } public string Thumbnail { get; set; } public string Banner { get; set; } public string Subscribers { get; set; } public string Description { get; set; } public string VideoCount { get; set; } public string Handle { get; set; } }
class InteractionCheck
{
    static int checks;
    static void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    [STAThread] static int Main(string[] args)
    {
        string scratch = Path.Combine(args[0], "runtime-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("TEMP",scratch); Environment.SetEnvironmentVariable("TMP",scratch);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources["SeleneInkColor"] = Colors.White; app.Resources["SeleneOnFocusColor"] = Colors.Black;
        app.Resources["SeleneGlassColor"] = Color.FromRgb(25,30,40); app.Resources["GlyphColor"] = Color.FromRgb(150,190,250);
        app.Resources["SeleneFocusColor"] = Color.FromRgb(150,190,250); app.Resources["SeleneScrimColor"] = Color.FromRgb(8,12,18);
        app.Resources["SeleneStatStoresSet"] = true;
        Features.Init(new SeleneSettings(), () => {}); Features.Set("sounds",false);
        InputHints.Changed += Ui.PublishPrompts; Ui.RefreshPrompts();
        var window = new Window { Width=1920,Height=1080,Left=-32000,Top=-32000,Opacity=0,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None };
        try
        {
            Assert(!LanguageSetup.Complete, "First-time language selection must be pending");
            Assert(LoginScene.Start() is LanguageScreen,"New users must start on the language screen");
            var language = new LanguageScreen(); window.Content=language.Root; window.Show(); Pump();
            Render(language.Root,Path.Combine(args[0],"first-language.png"));
            var prompt=Ui.Prompt("B","Back"); var holder=new Grid(); holder.Children.Add(prompt); window.Content=holder; Pump();
            var badge=(Grid)prompt.Children[0];
            InputHints.Set(InputKind.Controller); Assert(badge.Width==32,"Controller Back uses a round badge");
            InputHints.Set(InputKind.Keyboard); Assert(badge.Width>32,"Existing Back badge expands for Escape without reopening screen");
            Assert((string)app.Resources["SelenePromptB"]=="Esc","Theme resources update to keyboard hints");
            InputHints.Set(InputKind.Mouse); Assert((string)app.Resources["SelenePromptA"]=="Click","Theme resources update to mouse hints");
            InputHints.Set(InputKind.Controller); Assert((string)app.Resources["SelenePromptB"]=="B","Controller input restores B");
            var settings=new SettingsScreen(false,"Look","language"); window.Content=settings.Root; Pump(); settings.Shown(); Pump();
            settings.Handle("A"); var picker=(ChoiceList)Field(settings,"picker"); picker.Handle("DPadDown"); picker.Handle("A"); Pump();
            Assert(Features.Value("language")=="en","Language choice saves immediately");
            Assert(Field(settings,"ask") is Dialog,"Changed language opens restart modal");
            Wait(250); Render(settings.Root,Path.Combine(args[0],"language-restart.png"));
            settings.Handle("B"); Assert(Field(settings,"ask")==null,"Later dismisses restart modal");
            Assert(Features.Value("language")=="en","Later keeps the chosen language saved");
            Features.Set("downloadprogress", false); // Explicitly exercise the retained individual/legacy flow below.
            var gamesAsk = new GamesAskScreen(); window.Content = gamesAsk.Root; Pump(); gamesAsk.Shown(); Wait(300); Render(gamesAsk.Root, Path.Combine(args[0], "games-question.png"));
            Assert(((FrameworkElement)Field((Dialog)Field(gamesAsk, "ask"), "panel")).ActualHeight < 560, "The games question is a short card, not a scrolling list");
            GamesSetup.Decline(); var gamesRow = new SettingsScreen(false, "Games", "gamesget"); window.Content = gamesRow.Root; Pump(); gamesRow.Shown(); Wait(350); Render(gamesRow.Root, Path.Combine(args[0], "games-row-dot.png"));
            Features.Remember("gamesask-done", "");
            var downloads = new SettingsScreen(false, "Spotlight", "screenshotsdownload");
            window.Content = downloads.Root; Pump(); downloads.Shown(); Pump();
            downloads.Handle("A");
            Assert(Field(downloads, "ask") is Dialog && !Features.On("screenshotsdownload") && !DownloadConsent.Allowed("screenshotsdownload"), "Download toggle asks before enabling or authorizing network work");
            Wait(280); Render(downloads.Root, Path.Combine(args[0], "download-consent.png"));
            var consent = (Dialog)Field(downloads, "ask");
            Assert(((FrameworkElement)Field(consent, "panel")).ActualHeight <= 1000, "Consent destinations stay inside the screen");
            downloads.Handle("A");
            Assert(!Features.On("screenshotsdownload") && Field(downloads, "ask") == null, "Permission question defaults to Not now");
            downloads.Handle("A"); downloads.Handle("DPadRight"); downloads.Handle("A");
            Assert(DownloadConsent.Allowed("screenshotsdownload"), "Explicit Allow grants screenshot permission");
            downloads.Handle("A");
            Assert(!DownloadConsent.Allowed("screenshotsdownload"), "Switching off revokes screenshot permission");
            downloads.Hidden(); window.Content = settings.Root; Pump();
            settings.Handle("A"); picker=(ChoiceList)Field(settings,"picker"); picker.Handle("A"); Assert(Field(settings,"ask")==null,"Selecting the same language does not ask for another restart"); settings.Hidden();
            language=new LanguageScreen(); window.Content=language.Root; Pump(); language.Handle("DPadDown"); language.Handle("A");
            Assert(LanguageSetup.Complete,"Completing language step records progress before restart");
            Assert(Field(language,"ask") is Dialog,"First-time language change also offers restart");
            var glide=typeof(HomeNav).GetMethod("Glide",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(TranslateTransform),typeof(double),typeof(bool),typeof(double)},null);
            var firstRow=new TranslateTransform();var secondRow=new TranslateTransform();
            glide.Invoke(null,new object[]{firstRow,256.0,false,20.0});glide.Invoke(null,new object[]{secondRow,256.0,false,0.0});
            Assert(firstRow.X==20,"Switching between Collection and Spotlight settles the previous row");
            // Repeated hide/restore must preserve the visual tree and publish ambient state exactly once per transition.
            Features.Set("presence", true);
            ScreenPresence.Watch(window);
            int presenceChanges = 0;
            Action<bool> presenceObserver = value => presenceChanges++;
            ScreenPresence.Changed += presenceObserver;
            var retainedContent = window.Content;
            for (int cycle = 0; cycle < 4; cycle++)
            {
                window.Hide(); Pump(); ScreenPresence.Evaluate();
                Assert(!ScreenPresence.InView && Equals(app.Resources["SeleneAmbientMotionEnabled"], false), "Hidden window stops ambient work");
                int beforeRepeat = presenceChanges; ScreenPresence.Evaluate();
                Assert(presenceChanges == beforeRepeat, "Unchanged visibility does not notify subscribers again");
                window.Show(); Pump(); ScreenPresence.Evaluate();
                Assert(ScreenPresence.InView && ReferenceEquals(window.Content, retainedContent), "Restore preserves the same content tree");
            }
            window.Hide(); ScreenPresence.Evaluate(); Features.Set("presence", false); ScreenPresence.Evaluate();
            Assert(ScreenPresence.InView, "Turning presence off resumes media even while hidden");
            Features.Set("presence", true); ScreenPresence.Evaluate();
            Assert(!ScreenPresence.InView, "Turning presence back on applies current visibility");
            ScreenPresence.Changed -= presenceObserver; window.Show(); ScreenPresence.Evaluate();
            Ui.Poke(5000); Assert(Ui.RecentlyActive, "Input wakes the active frame window");
            // Byte budget must evict high-DPI portraits as well as many small covers, without changing decode quality.
            var remember = typeof(TileCovers).GetMethod("Remember", BindingFlags.Static | BindingFlags.NonPublic);
            var covers = (System.Collections.IDictionary)typeof(TileCovers).GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var pixels = new byte[1024 * 1024 * 4];
            var bitmap = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgra32, null, pixels, 4096); bitmap.Freeze();
            for (int i = 0; i < 30; i++) remember.Invoke(null, new object[] { "cover-" + i, bitmap });
            Assert(covers.Count == 20 && !covers.Contains("cover-0") && covers.Contains("cover-29"), "80 MiB pixel budget evicts least recent covers");
            remember.Invoke(null, new object[] { "cover-29", bitmap });
            Assert(covers.Count == 20, "Replacing a cached cover does not double-count its pixel storage");
            var small = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); small.Freeze();
            for (int i = 0; i < 100; i++) remember.Invoke(null, new object[] { "small-" + i, small });
            Assert(covers.Count == 80, "Entry count also bounds very small covers");
            // Load the production tile template: ambient clocks must stop without undoing selected scale/focus.
            var xml = new System.Xml.XmlDocument(); xml.Load(args[1]);
            var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
            ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
            ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var templateXml = (System.Xml.XmlElement)xml.SelectSingleNode("//p:Style[@x:Key='SeleneTileStyle']/p:Setter[@Property='Template']/p:Setter.Value/p:ControlTemplate", ns);
            templateXml.SetAttribute("xmlns:x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(templateXml.OuterXml);
            app.Resources["SeleneAmbientMotionEnabled"] = true;
            var selected = new ListBoxItem { Template = template, IsSelected = true };
            // Keep the template resource local so the presence timer cannot overwrite this fixture.
            selected.Resources["SeleneAmbientMotionEnabled"] = true;
            window.Content = selected; Wait(250);
            var ring = (RotateTransform)template.FindName("RingSpin", selected);
            var selectedScale = (ScaleTransform)template.FindName("TileScale", selected);
            Assert(ring.HasAnimatedProperties, "Visible selected tile runs its ambient ring");
            WaitUntil(() => selectedScale.ScaleX > 1.29);
            selected.Resources["SeleneAmbientMotionEnabled"] = false; WaitUntil(() => !ring.HasAnimatedProperties);

            Assert(!ring.HasAnimatedProperties && selected.IsSelected && selectedScale.ScaleX > 1.29, "Hidden ambient state removes clocks and preserves selected scale");
            selected.Resources["SeleneAmbientMotionEnabled"] = true; WaitUntil(() => ring.HasAnimatedProperties);
            Assert(ring.HasAnimatedProperties, "Restoring ambient state restarts the selected ring");
            selected.Visibility = Visibility.Collapsed; WaitUntil(() => !ring.HasAnimatedProperties);
            Assert(!ring.HasAnimatedProperties, "An invisible selected tile has no ambient clocks");
            // first-run "make it yours": topics -> news -> YouTube channels, then what is written for them
            {
                Features.Set("cardnews", false);   // (saving the news feeds would otherwise start a real fetch)
                int pictureWait; if (!int.TryParse(Environment.GetEnvironmentVariable("LF_PICTURE_WAIT"), out pictureWait)) pictureWait = 300;   // (longer to see the pictures in the screenshots)
                Func<Screen, System.Collections.Generic.List<PickOption>> optionsOf = s => (System.Collections.Generic.List<PickOption>)Field(s, "options");
                Func<Screen, string> goText = s => ((Pill)Field(s, "go")).Text;
                var topics = Personalize.Dev("");
                Assert(topics is PickScreen && optionsOf(topics).Count == Personalize.Topics.Length, "The first step lists the topics");
                window.Content = topics.Root; Pump(); topics.Shown(); Wait(500);
                Assert(goText(topics) == "Skip", "Nothing picked: the button says Skip");
                topics.Handle("A");
                Assert(optionsOf(topics)[0].On && goText(topics).StartsWith("Continue"), "A picks a topic and the button says Continue");
                topics.Handle("DPadRight"); topics.Handle("DPadRight"); topics.Handle("A");
                topics.Handle("Y"); Assert(optionsOf(topics).Count(o => o.On) == 2, "Y does nothing on the topics step");
                Wait(300); Render(topics.Root, Path.Combine(args[0], "personalize-topics.png"));
                topics.Hidden();

                var news = Personalize.Dev("news");
                var newsOptions = optionsOf(news);
                Assert(newsOptions.Count == NewsCard.Starters.Length, "Every suggested news source is offered");
                Assert(newsOptions.All(o => !string.IsNullOrEmpty(o.About) && !string.IsNullOrEmpty(o.Name)), "Every source has a name and a line about it");
                Func<string, PickOption> source = key => newsOptions.First(o => o.Key == key);
                Assert(source(NewsFeeds.SteamLibrary).On && source("https://www.pcgamer.com/rss/").On && source("https://www.nintendolife.com/feeds/latest").On, "PC and Nintendo fill in their sources");
                Assert(!source("https://kotaku.com/rss").On && !source("https://www.pushsquare.com/feeds/latest").On, "Other topics' sources stay off");
                window.Content = news.Root; Pump(); news.Shown(); Wait(pictureWait);
                news.Handle("DPadDown"); news.Handle("DPadRight"); Wait(200); Render(news.Root, Path.Combine(args[0], "personalize-news.png"));
                news.Handle("Y"); Assert(newsOptions.All(o => o.On), "Y picks every source");
                news.Handle("Y"); Assert(newsOptions.All(o => !o.On) && goText(news) == "Skip", "Y again picks none");
                news.Hidden();

                var tube = Personalize.Dev("youtube");
                var tubeOptions = optionsOf(tube);
                Assert(tubeOptions.Count == Personalize.Channels.Length + 1 && tubeOptions.Count >= 16, "The channels are offered, after a card to add one");
                Assert(tubeOptions[0].Open != null && !tubeOptions[0].On && Field(tube, "skipAll") != null, "The first card adds a channel, and X can skip the setup");
                Assert(tubeOptions.First(o => o.Name == "Gamers Nexus").On && tubeOptions.First(o => o.Name == "Nintendo of America").On && !tubeOptions.First(o => o.Name == "IGN").On, "The topics pick their channels");
                window.Content = tube.Root; Pump(); tube.Shown(); Wait(pictureWait);
                Wait(pictureWait); Render(tube.Root, Path.Combine(args[0], "personalize-youtube.png"));
                tube.Handle("Y"); Assert(tubeOptions.Skip(1).All(o => o.On) && !tubeOptions[0].On, "Y picks every channel but never the add card");
                tube.Handle("Y"); Assert(tubeOptions.All(o => !o.On), "Y again picks none");
                for (int i = 0; i < 6; i++) tube.Handle("DPadDown");
                Wait(pictureWait); Render(tube.Root, Path.Combine(args[0], "personalize-youtube-scrolled.png"));
                tube.Hidden();

                // adding a channel by hand: what is accepted, and what a channel's page gives
                Assert(YouTubeLookup.PageFor("@IGN") == "https://www.youtube.com/@IGN", "An @handle finds the channel's page");
                Assert(YouTubeLookup.PageFor("Linus Tech Tips") == "https://www.youtube.com/@LinusTechTips", "A name without its spaces is a handle");
                Assert(YouTubeLookup.PageFor("https://www.youtube.com/@GMTK/videos?si=abc") == "https://www.youtube.com/@GMTK", "A link to the channel's page, with the extras dropped");
                Assert(YouTubeLookup.PageFor("youtube.com/channel/UCKy1dAqELo0zrOtPkf0eTMw") == "https://www.youtube.com/channel/UCKy1dAqELo0zrOtPkf0eTMw", "A channel link without https works");
                Assert(YouTubeLookup.PageFor("UCKy1dAqELo0zrOtPkf0eTMw") == "https://www.youtube.com/channel/UCKy1dAqELo0zrOtPkf0eTMw", "A bare channel id works");
                Assert(YouTubeLookup.PageFor("https://www.youtube.com/user/PewDiePie") == "https://www.youtube.com/user/PewDiePie", "An old /user/ link works");
                Assert(YouTubeLookup.PageFor("https://evil.example/@IGN") == null && YouTubeLookup.PageFor("https://youtube.com.evil.example/@IGN") == null, "Only youtube.com is ever read");
                Assert(YouTubeLookup.PageFor("https://www.youtube.com/watch?v=dQw4w9WgXcQ") == null && YouTubeLookup.PageFor("") == null && YouTubeLookup.PageFor("a/../b") == null, "A video link or nothing is not a channel");
                Assert(YouTubeLookup.PageFor("ign/../../x") == null && YouTubeLookup.PageFor("@ign?x=1&y") == null, "Odd characters never reach the address");
                var html = "<html><head><link rel=\"canonical\" href=\"https://www.youtube.com/@SomeOne\"><meta property=\"og:title\" content=\"Some &amp; One\"><meta property=\"og:image\" content=\"https://yt3.googleusercontent.com/abc=s900-c-k-c0x00ffffff-no-rj\"><link rel=\"alternate\" type=\"application/rss+xml\" href=\"https://www.youtube.com/feeds/videos.xml?channel_id=UCKy1dAqELo0zrOtPkf0eTMw\"></head></html>";
                var parsed = YouTubeLookup.Parse(html);
                Assert(parsed != null && parsed.Id == "UCKy1dAqELo0zrOtPkf0eTMw" && parsed.Name == "Some & One" && parsed.Handle == "@SomeOne" && parsed.Avatar == "https://yt3.googleusercontent.com/abc=s176-c-k-c0x00ffffff-no-rj", "A channel's page gives its id, name, handle and a small avatar");
                Assert(YouTubeLookup.Parse(html.Replace("yt3.googleusercontent.com", "evil.example")).Avatar == "", "A picture from another host is dropped");
                Assert(YouTubeLookup.Parse("<html><title>Not a channel</title></html>") == null && YouTubeLookup.Parse("") == null, "A page that isn't a channel's gives nothing");
                Personalize.Channel got = null;
                var adder = new AddChannelScreen(() => null, c => got = c);
                window.Content = adder.Root; Pump(); adder.Shown(); adder.Type("@ign"); Wait(300);
                Render(adder.Root, Path.Combine(args[0], "personalize-add-channel.png"));
                adder.Hidden();
                if (Environment.GetEnvironmentVariable("LF_LIVE") == "1")
                {
                    var live = YouTubeLookup.Find("@IGN");
                    Assert(live != null && live.Id == "UCKy1dAqELo0zrOtPkf0eTMw" && live.Name == "IGN" && live.Avatar.StartsWith("https://yt3.googleusercontent.com/"), "Live: @IGN is found on YouTube");
                    Assert(YouTubeLookup.Find("this-channel-does-not-exist-zzqx") == null, "Live: a channel that doesn't exist gives nothing");
                }
                var mine = new Personalize.Channel { Id = "UCKy1dAqELo0zrOtPkf0eTMw", Name = "Mine", Handle = "@mine", Avatar = "https://yt3.googleusercontent.com/zzz=s176-c-k", Topics = new string[0] };
                Assert(Personalize.AvatarUrl(mine) == mine.Avatar, "A channel added by hand keeps its own avatar address");

                // the channel list is valid: real-looking ids, none twice, an avatar each, topics that exist
                var rx = new System.Text.RegularExpressions.Regex("^UC[0-9A-Za-z_-]{22}$");
                Assert(Personalize.Channels.All(c => rx.IsMatch(c.Id) && !string.IsNullOrEmpty(c.Avatar) && c.Handle.StartsWith("@")), "Channel ids look like YouTube's");
                Assert(Personalize.Channels.Select(c => c.Id).Distinct().Count() == Personalize.Channels.Length, "No channel is listed twice");
                Assert(Personalize.Channels.All(c => c.Topics.All(t => Personalize.Topics.Any(x => x.Id == t))), "Channels only use known topics");
                Assert(Personalize.Topics.All(t => Personalize.Channels.Any(c => c.Topics.Contains(t.Id))), "Every topic has channels");

                // what is written: this profile's subscriptions, in the shape the Browser reads, without losing what was there
                var profile = Guid.NewGuid();
                var folder = Personalize.BrowserProfileFolder(profile);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "yt-subs-removed.json"), "[\"" + Personalize.Channels[1].Id + "\",\"keep-me\"]");
                Assert(Personalize.SubscribeTo(profile, Personalize.Channels.Take(3)) == 3, "Three new channels are subscribed");
                Assert(Personalize.SubscribeTo(profile, Personalize.Channels.Take(4)) == 1, "Asking again only adds the new one");
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var subs = ser.Deserialize<System.Collections.Generic.List<BrowserChannel>>(File.ReadAllText(Path.Combine(folder, "yt-subs.json")));
                Assert(subs.Count == 4 && subs[0].Id == Personalize.Channels[0].Id && subs[0].Name == "IGN" && subs[0].Handle == "@IGN" && subs[0].Thumbnail.StartsWith("https://yt3.googleusercontent.com/"), "The Browser's own channel type reads what was written");
                var removed = ser.Deserialize<System.Collections.Generic.List<string>>(File.ReadAllText(Path.Combine(folder, "yt-subs-removed.json")));
                Assert(removed.Count == 1 && removed[0] == "keep-me", "A channel picked again is taken off the unsubscribed list, the rest stays");
                Assert(!File.Exists(Path.Combine(folder, "yt-subs.json.tmp")), "No temp file is left behind");

                // news: the picked sources are on, the other suggestions go, a feed added by hand stays
                NewsFeeds.Save(new System.Collections.Generic.List<NewsFeed> { new NewsFeed { Url = "https://kotaku.com/rss", Enabled = true }, new NewsFeed { Url = "https://example.org/my-feed.xml", Enabled = true } });
                Personalize.FollowNews(new[] { NewsFeeds.SteamLibrary, "https://www.nintendolife.com/feeds/latest" });
                var feeds = NewsFeeds.Load();
                Assert(feeds.Count == 3 && feeds.All(f => f.Enabled), "Picked sources and the hand-made feed are on");
                Assert(feeds.Any(f => f.Url == "https://example.org/my-feed.xml") && !feeds.Any(f => f.Url == "https://kotaku.com/rss"), "A source not picked goes, a hand-made feed stays");
            }
            Console.WriteLine("Passed "+checks+" UI interaction checks; rendered first-language and restart screens."); return 0;
        }
        finally { window.Close(); if(Directory.Exists(scratch))Directory.Delete(scratch,true); }
    }
    static void WaitUntil(Func<bool> ready) { var deadline = DateTime.UtcNow.AddSeconds(4); while (!ready() && DateTime.UtcNow < deadline) Wait(25); Assert(ready(), "UI state settles within four seconds"); }
    static void Pump() { Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(()=>{})); }
    static void Wait(int ms) { var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(ms) }; timer.Tick+=(s,e)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame); }
    static void Render(FrameworkElement root,string file)
    {
        root.Measure(new Size(1920,1080));root.Arrange(new Rect(0,0,1920,1080));root.UpdateLayout();
        var bmp=new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32);bmp.Render(root);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using(var stream=File.Create(file))png.Save(stream);
    }
}
