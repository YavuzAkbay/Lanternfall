using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Selene
{
    // First-run "make it yours", after the profile is made: what are you into -> which news to follow -> which YouTube channels.
    // Each step is a grid of cards (A picks or drops one, Y picks all, Continue goes on); the topics fill in the next two steps,
    // so a few presses give a News card and a YouTube feed that are already yours. Nothing is required: Skip on every step.
    // The news goes to the same file the News feeds screen edits; the channels go to this profile's YouTube subscriptions.
    public class PickOption
    {
        public string Key, Name, About;
        public string Glyph;        // a Segoe glyph in the badge ...
        public string Badge;        // ... or a few letters
        public string PictureUrl;   // the badge's picture (a channel's avatar), kept on disk once fetched
        public string FeedUrl;      // a news feed: its newest article's picture is the card's background
        public string[] Topics = new string[0];
        public bool On;
        public Action<List<string>> Open;   // a card that opens something (a keyboard) instead of switching on; gets what is picked so far
    }

    public static class Personalize
    {
        // the Browser add-on's id (a plugin of its own: Core can't reference it, only write to its data folder)
        public const string BrowserId = "7362a822-4c12-4880-a054-0b2932af6ae7";

        public class Topic { public string Id, Name, About, Glyph, Badge; }
        public class Channel { public string Id, Name, About, Handle, Avatar; public string[] Topics; }

        public static readonly Topic[] Topics =
        {
            new Topic { Id = "headlines", Name = "Big gaming news", About = "Releases, reviews, trailers", Glyph = "" },
            new Topic { Id = "pc", Name = "PC gaming", About = "Games, hardware, benchmarks", Badge = "PC" },
            new Topic { Id = "nintendo", Name = "Nintendo", About = "Switch and the Directs", Badge = "N" },
            new Topic { Id = "playstation", Name = "PlayStation", About = "PS5 news and showcases", Badge = "PS" },
            new Topic { Id = "xbox", Name = "Xbox & Game Pass", About = "Xbox news and showcases", Glyph = "" },
            new Topic { Id = "retro", Name = "Retro & indie", About = "Classics, speedruns, small studios", Glyph = "" },
            new Topic { Id = "deepdives", Name = "Deep dives", About = "Documentaries and design talks", Glyph = "" },
        };

        // what each news source is and which topics it covers (the sources themselves are NewsCard.Starters)
        private class NewsMeta { public string About; public string[] Topics; }
        private static readonly Dictionary<string, NewsMeta> NewsInfo = new Dictionary<string, NewsMeta>(StringComparer.OrdinalIgnoreCase)
        {
            { NewsFeeds.SteamLibrary, new NewsMeta { About = "Patch notes for your own games", Topics = new[] { "pc" } } },
            { "https://www.pcgamer.com/rss/", new NewsMeta { About = "PC games, hardware, reviews", Topics = new[] { "pc", "headlines" } } },
            { "https://www.rockpapershotgun.com/feed", new NewsMeta { About = "PC games, indie and long reads", Topics = new[] { "pc", "retro" } } },
            { "https://www.pcgamesn.com/mainrss.xml", new NewsMeta { About = "PC news and guides", Topics = new[] { "pc" } } },
            { "https://kotaku.com/rss", new NewsMeta { About = "Gaming news and culture", Topics = new[] { "headlines" } } },
            { "https://www.polygon.com/rss/index.xml", new NewsMeta { About = "Games, reviews, culture", Topics = new[] { "headlines" } } },
            { "https://www.eurogamer.net/feed", new NewsMeta { About = "Reviews and features", Topics = new[] { "headlines" } } },
            { "https://www.gamespot.com/feeds/mashup/", new NewsMeta { About = "News and reviews", Topics = new[] { "headlines" } } },
            { "https://www.gameinformer.com/rss.xml", new NewsMeta { About = "News, previews, reviews", Topics = new[] { "headlines" } } },
            { "https://feeds.arstechnica.com/arstechnica/gaming/", new NewsMeta { About = "Games with a tech eye", Topics = new[] { "pc", "headlines" } } },
            { "https://www.theverge.com/rss/gaming/index.xml", new NewsMeta { About = "Games and the gear", Topics = new[] { "headlines" } } },
            { "https://www.vg247.com/feed", new NewsMeta { About = "Daily news and guides", Topics = new[] { "headlines" } } },
            { "https://www.thegamer.com/feed/", new NewsMeta { About = "News, lists and guides", Topics = new[] { "headlines" } } },
            { "https://www.destructoid.com/feed/", new NewsMeta { About = "News, reviews, indie picks", Topics = new[] { "headlines", "retro" } } },
            { "https://www.nintendolife.com/feeds/latest", new NewsMeta { About = "Everything Nintendo", Topics = new[] { "nintendo", "retro" } } },
            { "https://www.pushsquare.com/feeds/latest", new NewsMeta { About = "PlayStation news", Topics = new[] { "playstation" } } },
            { "https://news.xbox.com/en-us/feed/", new NewsMeta { About = "Official Xbox news", Topics = new[] { "xbox" } } },
            { "https://blog.playstation.com/feed/", new NewsMeta { About = "Official PlayStation news", Topics = new[] { "playstation" } } },
            { "https://gematsu.com/feed", new NewsMeta { About = "Japanese and Asian games", Topics = new[] { "headlines" } } },
            { "https://www.videogameschronicle.com/feed/", new NewsMeta { About = "Industry news and scoops", Topics = new[] { "headlines" } } },
        };

        // YouTube channels (ids and avatars read from YouTube's own pages; Avatar is the part after yt3.googleusercontent.com/)
        public static readonly Channel[] Channels =
        {
            new Channel { Id = "UCKy1dAqELo0zrOtPkf0eTMw", Name = "IGN", Handle = "@IGN", About = "Trailers, reviews, news", Topics = new[] { "headlines" },
                Avatar = "4jRpju9vRtgoIA6SxuIomVcCmjub6ydA1TzGRHts853ZzxRITi41gxP50jTuGBdUlvAgehZJK8Y" },
            new Channel { Id = "UCbu2SsF-Or3Rsn3NxqODImw", Name = "GameSpot", Handle = "@GameSpot", About = "Reviews and trailers", Topics = new[] { "headlines" },
                Avatar = "2q8-YlqtAs3pWHamRcooAOvt_M88afnTwo7DYwqvHV9OSGlf2pBa5h3QJGeHrBkBidUjaL0mpA" },
            new Channel { Id = "UCmeds0MLhjfkjD_5acPnFlQ", Name = "Giant Bomb", Handle = "@giantbomb", About = "Games, talked about with humor", Topics = new[] { "headlines" },
                Avatar = "kch-zNAuWMuIKsXBz1s8F8Wr4dIjAy6l46ucf5wVrhRi2q0FL45R9JxSMnfX1DBweNrkVHyy" },
            new Channel { Id = "UCZ7AeeVbyslLM_8-nVy2B8Q", Name = "Skill Up", Handle = "@SkillUp", About = "Honest reviews, industry takes", Topics = new[] { "headlines", "deepdives" },
                Avatar = "CcyQzLCpV1rVfUWeaEgRut_FZGTkOfM7VFc0dG80dm6UraKtid4Tbetka8elQPn-4AABg3k-8A" },
            new Channel { Id = "UCT6QFE3peNry9PdO5uGj96g", Name = "Kinda Funny Games", Handle = "@KindaFunnyGames", About = "Daily news show and podcasts", Topics = new[] { "headlines" },
                Avatar = "axzTphozq_1Nj3kqHVfWWU-Jby0VhwzRkne0JjnF8eV0lmWv5dyvej0_z6qhFR5Fy3Vqd8cq3g" },
            new Channel { Id = "UCsvn_Po0SmunchJYOWpOxMg", Name = "videogamedunkey", Handle = "@videogamedunkey", About = "Comedy and blunt opinions", Topics = new[] { "headlines" },
                Avatar = "41hm8RIIr1HbZIg7A_M9CvtHvJdRXtEEwz4JdaxWYjuwDntO0Tyag4Qe104nIxaSnnybsUdnOQ" },
            new Channel { Id = "UC9PBzalIcEQCsiIkq36PyUA", Name = "Digital Foundry", Handle = "@DigitalFoundry", About = "Game and console tech analysis", Topics = new[] { "pc", "playstation", "xbox", "deepdives" },
                Avatar = "PBh4giP5yBGdq6OxwdAW32ZvLI-uy_kzMAlV7sQ-jTaWK3q4f3euUJL8lFX3onIkHlc6OBCTpQ" },
            new Channel { Id = "UChIs72whgZI9w6d6FhwGGHA", Name = "Gamers Nexus", Handle = "@GamersNexus", About = "In-depth PC hardware testing", Topics = new[] { "pc" },
                Avatar = "ytc/AIdro_n50Rj8oqwUnusv4I6_DllKflZrSdSxmXhYvpHs0hOEj5o" },
            new Channel { Id = "UCXuqSBlHAE6Xw-yeJA0Tunw", Name = "Linus Tech Tips", Handle = "@LinusTechTips", About = "Tech, PCs and builds", Topics = new[] { "pc" },
                Avatar = "gnvYLhXy8FAlPXZ2RTrkrgj-5kyt0vdE2FUGVOiKGdEZIa-wN5A-7nwZBlWJLzUMmoh1NWAU" },
            new Channel { Id = "UCI8iQa1hv7oV_Z8D35vVuSg", Name = "Hardware Unboxed", Handle = "@HardwareUnboxed", About = "GPU and CPU benchmarks", Topics = new[] { "pc" },
                Avatar = "ytc/AIdro_kXQj4ztRaGwJt_NY5NnbKJ2xppFk6LXaBk_w2xs7xCcw" },
            new Channel { Id = "UC-2Y8dQb0S6DtpxNgAKoJKA", Name = "PlayStation", Handle = "@PlayStation", About = "Official trailers and showcases", Topics = new[] { "playstation" },
                Avatar = "uiQh9JC7wAmCl63jhNilJvAcqNbwnMmUf5FUCQBecipe6yGmUWpPvAMVixALEt6xpPOVbsouzw" },
            new Channel { Id = "UCjBp_7RuDBUYbd1LegWEJ8g", Name = "Xbox", Handle = "@xbox", About = "Official trailers and showcases", Topics = new[] { "xbox" },
                Avatar = "t1D8mEDqHIguuhdzqX2XmpkaChbw8z2szDzkXjhyD9XOmQXGsZRGzQJh2Hdto6k-7aoTnsDN7Q" },
            new Channel { Id = "UCGIY_O-8vW4rfX98KlMkvRg", Name = "Nintendo of America", Handle = "@NintendoAmerica", About = "Official trailers and Directs", Topics = new[] { "nintendo" },
                Avatar = "HNg_MA3lYwk6JCHxftRxRUAcMFK0334jI3-G-ERWMnTe5vIT1fORQ2er531XI_eCnMl4v_Wtk38" },
            new Channel { Id = "UCl7ZXbZUCWI2Hz--OrO4bsA", Name = "Nintendo Life", Handle = "@NintendoLife", About = "Nintendo news and reviews", Topics = new[] { "nintendo", "retro" },
                Avatar = "ytc/AIdro_lMat6n_N6vZXGn0NEr2dY79GOWQfx4QxR6Y1YSRlySMMQ" },
            new Channel { Id = "UCtUbO6rBht0daVIOGML3c8w", Name = "Summoning Salt", Handle = "@SummoningSalt", About = "The history of speedrunning", Topics = new[] { "retro", "deepdives" },
                Avatar = "ytc/AIdro_n8NSEiNfCuno3JKW2ggFYNvxVUKPg2jzf2O_7W-pmkAv4" },
            new Channel { Id = "UCqJ-Xo29CKyLTjn6z2XwYAw", Name = "Game Maker's Toolkit", Handle = "@GMTK", About = "How game design works", Topics = new[] { "deepdives", "retro" },
                Avatar = "fZzS7tuwCW14i6tAAtGG6q8E1Y3-_k2G36mbn7VNKHOwtaJ4vKF8Es8Ky6745TiNGOAVSAyR6A" },
            new Channel { Id = "UCwRqWnW5ZkVaP_lZF7caZ-g", Name = "Retro Game Mechanics", Handle = "@RGMechEx", About = "How old games really work", Topics = new[] { "retro", "deepdives" },
                Avatar = "ytc/AIdro_kwpaFOA2SKmnhLnSyRT2GHFN_pAMtyoL471qw7pT3_-og" },
            new Channel { Id = "UCjFaPUcJU1vwk193mnW_w1w", Name = "Modern Vintage Gamer", Handle = "@ModernVintageGamer", About = "Retro hardware and its history", Topics = new[] { "retro" },
                Avatar = "3-AaRq47w71sEzGkvzCjGu9-jbHYyZa9HemnfgGF_16wsClU3Od0Y1_KgxnWWWoCxi-aV0o4TPc" },
            new Channel { Id = "UCI3DTtB-a3fJPjKtQ5kYHfA", Name = "Games Done Quick", Handle = "@GamesDoneQuick", About = "Speedrun marathons, live", Topics = new[] { "retro" },
                Avatar = "ytc/AIdro_mHR8eFrqsRiKXyAUEpFVibNzg1R54wstoXhm-J8G8I8Rs" },
            new Channel { Id = "UC0fDG3byEcMtbOqPMymDNbw", Name = "Noclip", Handle = "@NoclipDocs", About = "Game development documentaries", Topics = new[] { "deepdives" },
                Avatar = "093arh9Jg1MqfdvcqcrDcUQm9QMdXnzWEJ-dpT_YUn5QU_8AQYbBIL37TvUHQDUaG1XErynq3w" },
        };

        // (a channel the user added carries its whole avatar address)
        public static string AvatarUrl(Channel c, int size = 176)
        {
            if (string.IsNullOrEmpty(c.Avatar)) return "";
            if (c.Avatar.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return c.Avatar;
            return "https://yt3.googleusercontent.com/" + c.Avatar + "=s" + size + "-c-k-c0x00ffffff-no-rj";
        }

        // the Browser add-on is installed (the YouTube step is only worth a screen then)
        public static bool BrowserInstalled
        {
            get
            {
                try
                {
                    var id = new Guid(BrowserId);
                    return HomeNav.Api != null && HomeNav.Api.Addons.Plugins.Any(p => p.Id == id);
                }
                catch { return false; }
            }
        }

        // where Lanternfall Browser keeps this profile's personal files (beside Core's own data folder)
        public static string BrowserProfileFolder(Guid profileId)
        {
            var own = SelenePaths.DataFolder.TrimEnd('\\', '/');
            return Path.Combine(Path.GetDirectoryName(own), BrowserId, "profiles", profileId.ToString("N"));
        }

        // The channels are added to this profile's own subscriptions (the file the Browser reads: yt-subs.json) and taken off its
        // "unsubscribed" list. What was subscribed already stays. Returns how many channels were added.
        public static int SubscribeTo(Guid profileId, IEnumerable<Channel> channels)
        {
            var folder = BrowserProfileFolder(profileId);
            Directory.CreateDirectory(folder);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var file = Path.Combine(folder, "yt-subs.json");
            var subs = Read<List<Dictionary<string, object>>>(ser, file) ?? new List<Dictionary<string, object>>();
            var removedFile = Path.Combine(folder, "yt-subs-removed.json");
            var removed = Read<List<string>>(ser, removedFile) ?? new List<string>();
            int added = 0;
            foreach (var c in channels)
            {
                removed.RemoveAll(r => r == c.Id);
                if (subs.Any(s => s != null && s.ContainsKey("Id") && string.Equals(Convert.ToString(s["Id"]), c.Id, StringComparison.Ordinal))) continue;
                subs.Add(new Dictionary<string, object>
                {
                    { "Id", c.Id }, { "Name", c.Name }, { "Thumbnail", AvatarUrl(c) }, { "Banner", "" }, { "Subscribers", "" },
                    { "Description", "" }, { "VideoCount", "" }, { "Handle", c.Handle }
                });
                added++;
            }
            Write(file, ser.Serialize(subs));
            Write(removedFile, ser.Serialize(removed));
            return added;
        }

        private static T Read<T>(JavaScriptSerializer ser, string file) where T : class
        {
            try { return File.Exists(file) ? ser.Deserialize<T>(File.ReadAllText(file, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        // a temp file, then one replace: a crash never leaves a half-written file for the Browser to read
        private static void Write(string file, string text)
        {
            var temp = file + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
        }

        // The sources the user picked become the news feeds: the starters not picked go, picked ones are on, feeds added by hand stay.
        public static void FollowNews(IEnumerable<string> urls)
        {
            var picked = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
            var starters = new HashSet<string>(NewsCard.Starters.Select(s => s[1]), StringComparer.OrdinalIgnoreCase);
            var feeds = NewsFeeds.Load().Where(f => !starters.Contains(f.Url) || picked.Contains(f.Url)).ToList();
            foreach (var url in picked)
            {
                var have = feeds.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
                if (have == null) feeds.Add(new NewsFeed { Url = url, Enabled = true }); else have.Enabled = true;
            }
            NewsFeeds.Save(feeds);   // (the News card refreshes)
        }

        // ---------------- the steps ----------------
        // where the first run goes after the profile is made: the first step; `finish` is called when the last one is done (or skipped)
        public static Screen Start(Profile profile, Action finish)
        {
            return new Flow(profile, finish).Topics();
        }

        // developer switch (screen:personalize, :news, :youtube in %TEMP%\selene-dev-keys): a step on its own, as if the topics were PC + Nintendo
        public static Screen Dev(string step)
        {
            var profile = Profiles.Active ?? Profiles.All.FirstOrDefault() ?? new Profile { Id = Guid.NewGuid(), Name = "Test" };
            var flow = new Flow(profile, () => LoginScene.Finish());
            return flow.At(step);
        }

        private class Flow
        {
            public Screen At(string step)
            {
                if (step != "" && step != null) { topics.Add("pc"); topics.Add("nintendo"); Presets(); }
                if (step == "add") return new AddChannelScreen(() => YouTube(), AddCustom);
                return step == "news" ? News() : step == "youtube" ? YouTube() : Topics();
            }

            private readonly Profile profile;
            private readonly Action finish;
            private readonly bool youtube = BrowserInstalled;
            private readonly HashSet<string> topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private HashSet<string> news, channels;   // null = not visited yet: the topics fill them in
            private string presetFor;                 // the topics news and channels were filled from

            public Flow(Profile profile, Action finish) { this.profile = profile; this.finish = finish; }

            private int Steps { get { return youtube ? 3 : 2; } }
            private string TopicKey { get { return string.Join(",", topics.OrderBy(t => t).ToArray()); } }

            // the topics changed since the picks were made from them: they're made again
            private void Presets()
            {
                if (presetFor == TopicKey && news != null) return;
                presetFor = TopicKey;
                bool any = topics.Count > 0;
                news = new HashSet<string>(NewsCard.Starters.Select(s => s[1]).Where(url =>
                {
                    if (NewsFeeds.IsSteamLibrary(url)) return true;
                    NewsMeta m;
                    if (!any) return string.Equals(url, "https://www.pcgamer.com/rss/", StringComparison.OrdinalIgnoreCase);
                    return NewsInfo.TryGetValue(url, out m) && m.Topics.Any(topics.Contains);
                }), StringComparer.OrdinalIgnoreCase);
                channels = new HashSet<string>(Channels.Where(c => c.Topics.Any(topics.Contains)).Select(c => c.Id), StringComparer.Ordinal);
            }

            public Screen Topics()
            {
                var options = Personalize.Topics.Select(t => new PickOption { Key = t.Id, Name = t.Name, About = t.About, Glyph = t.Glyph, Badge = t.Badge, On = topics.Contains(t.Id) }).ToList();
                return new PickScreen("STEP 1 OF " + Steps, "What are you into?", "Pick a few. They fill in the next steps, so there's almost nothing left to choose.",
                    options, 4, false, null, picked => { topics.Clear(); foreach (var k in picked) topics.Add(k); Presets(); LoginScene.Show(News(), 1); }, Skip);
            }

            // X on any step: on to Who's playing with nothing written (the news keep Lanternfall's own two to start with)
            private void Skip()
            {
                HomeNav.Log("personalize: skipped");
                finish();
            }

            private Screen News()
            {
                var options = new List<PickOption>();
                foreach (var s in NewsCard.Starters)
                {
                    NewsMeta m;
                    NewsInfo.TryGetValue(s[1], out m);
                    options.Add(new PickOption
                    {
                        Key = s[1], Name = s[0], About = m != null ? m.About : "", Badge = Initials(s[0]), FeedUrl = NewsFeeds.IsSteamLibrary(s[1]) ? null : s[1],
                        Glyph = NewsFeeds.IsSteamLibrary(s[1]) ? "" : null, On = news.Contains(s[1])
                    });
                }
                return new PickScreen("STEP 2 OF " + Steps, "Which news do you want?", "Your News card on the home shows the newest from these. You can change them anytime in News.",
                    options, 5, true, () => Topics(), picked =>
                    {
                        news = new HashSet<string>(picked, StringComparer.OrdinalIgnoreCase);
                        if (youtube) LoginScene.Show(YouTube(), 1); else Done();
                    }, Skip);
            }

            // channels the user typed in (after the first card, ahead of the suggestions: they're seen at once)
            private readonly List<Channel> custom = new List<Channel>();

            private Screen YouTube()
            {
                var options = new List<PickOption>();
                options.Add(new PickOption
                {
                    Key = "+add", Name = "Add a channel", About = "A name, an @handle or a link", Glyph = "",
                    Open = picked => { channels = new HashSet<string>(picked, StringComparer.Ordinal); LoginScene.Show(new AddChannelScreen(() => YouTube(), AddCustom), 1); }
                });
                foreach (var c in custom.Concat(Channels))
                    options.Add(new PickOption { Key = c.Id, Name = c.Name, About = c.About, Badge = Initials(c.Name), PictureUrl = AvatarUrl(c), Topics = c.Topics, On = channels.Contains(c.Id) });
                return new PickScreen("STEP 3 OF 3", "Which YouTube channels?", "They go into your YouTube subscriptions, ready the first time you open YouTube. Add any channel you follow.",
                    options, 5, true, () => News(), picked => { channels = new HashSet<string>(picked, StringComparer.Ordinal); Done(); }, Skip);
            }

            // a channel looked up on the keyboard step: picked, and back on the list (one already listed is just picked)
            private void AddCustom(Channel c)
            {
                channels.Add(c.Id);
                if (!Channels.Any(x => x.Id == c.Id) && !custom.Any(x => x.Id == c.Id)) custom.Insert(0, c);
                Ui.Chime();
                LoginScene.Show(YouTube(), -1);
            }

            private void Done()
            {
                int sources = 0, subscribed = 0;
                try { FollowNews(news); sources = news.Count; }
                catch (Exception ex) { HomeNav.Log("personalize: news: " + ex.Message); }
                if (youtube && channels.Count > 0)
                {
                    try { SubscribeTo(profile.Id, custom.Concat(Channels).Where(c => channels.Contains(c.Id))); subscribed = channels.Count; }
                    catch (Exception ex) { HomeNav.Log("personalize: youtube: " + ex.Message); }
                }
                HomeNav.Log("personalize: " + topics.Count + " topics, " + sources + " news sources, " + subscribed + " channels");
                Ui.Chime();
                var parts = new List<string>();
                if (sources > 0) parts.Add(sources + (sources == 1 ? " news source" : " news sources"));
                if (subscribed > 0) parts.Add(subscribed + (subscribed == 1 ? " YouTube channel" : " YouTube channels"));
                finish();
                if (parts.Count > 0) Ui.Notice("Set up: " + string.Join(" and ", parts.ToArray()) + ". Change them anytime from News and YouTube.", 7000);
            }
        }

        // "PC Gamer" -> "PG", "Kotaku" -> "K"
        public static string Initials(string name)
        {
            var words = (name ?? "").Split(new[] { ' ', ':', '.', '-', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length == 1) return words[0].Substring(0, 1).ToUpperInvariant();
            return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
        }
    }

    // One step: a grid of cards to switch on or off, and a button that goes on. (A: pick, Y: all or none, Start / the button:
    // continue, B: the step before.)
    public class PickScreen : Screen
    {
        public override bool MenuOnStart { get { return false; } }   // (Start is "continue" here)
        private const double CellW = 300, CellH = 140, TileW = 270, TileH = 112;
        private readonly List<PickOption> options;
        private readonly List<Tile> tiles = new List<Tile>();
        private readonly List<Border> checks = new List<Border>();
        private readonly List<Ellipse> badges = new List<Ellipse>();
        private readonly List<Border> backdrops = new List<Border>();
        private readonly ProfileSteps.ScrollingGrid grid;
        private readonly Pill go;
        private readonly Func<Screen> back;
        private readonly Action<List<string>> next;
        private readonly int columns;
        private readonly bool allowAll;
        private int index, lastCol;
        private bool left, handled;

        private readonly Action skipAll;   // X: leave the setup altogether (null: no such button)

        public PickScreen(string eyebrow, string title, string sub, List<PickOption> options, int columns, bool allowAll, Func<Screen> back, Action<List<string>> next, Action skipAll = null)
        {
            this.options = options; this.columns = columns; this.allowAll = allowAll; this.back = back; this.next = next; this.skipAll = skipAll;
            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 78, 0, 0), MaxWidth = 1300 };
            head.Children.Add(new TextBlock { Text = eyebrow, FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Body, Foreground = Ink.Brush("GlyphColor"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
            head.Children.Add(Ui.Title(title));
            head.Children.Add(Ui.Sub(sub));
            Root.Children.Add(head);

            // a window the grid scrolls in (more cards than fit: the YouTube step has four rows)
            grid = new ProfileSteps.ScrollingGrid(columns, CellH, 540);
            grid.Viewport.Width = columns * CellW;
            grid.Viewport.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Viewport.VerticalAlignment = VerticalAlignment.Top;
            grid.Viewport.Margin = new Thickness(0, 236, 0, 0);
            for (int i = 0; i < options.Count; i++) AddCard(i);
            Root.Children.Add(grid.Viewport);

            go = new Pill("Skip", 280);
            go.Root.HorizontalAlignment = HorizontalAlignment.Center;
            go.Root.VerticalAlignment = VerticalAlignment.Top;
            go.Root.Margin = new Thickness(0, 806, 0, 0);
            go.Root.MouseLeftButtonUp += (s, e) => { index = options.Count; ShowFocus(); Continue(); e.Handled = true; };
            Root.Children.Add(go.Root);

            var hints = new List<StackPanel>();
            if (back != null) hints.Add(Ui.Prompt("B", "Back"));
            if (allowAll) hints.Add(Ui.Prompt("Y", "All or none"));
            if (skipAll != null) hints.Add(Ui.Prompt("X", "Skip setup"));
            if (hints.Count > 0) Root.Children.Add(Ui.Prompts(false, hints.ToArray()));
            Root.Children.Add(Ui.Prompts(true, Ui.Prompt("A", "Pick"), Ui.Prompt("Start", "Continue")));
            for (int i = 0; i < options.Count; i++) Paint(i);
            UpdateGo();
        }

        private void AddCard(int i)
        {
            var o = options[i];
            var tile = new Tile(TileW, TileH, 16, 5) { FocusScale = 1.05 };
            int at = i;
            tile.Click = () => { if (index == at) Toggle(); else { index = at; Ui.Tick(); ShowFocus(); } };
            var face = new Grid();
            // a feed's newest picture, faded behind the words once it has come in
            var backdrop = new Border { CornerRadius = new CornerRadius(15), Opacity = 0, IsHitTestVisible = false };
            backdrops.Add(backdrop);
            face.Children.Add(backdrop);
            face.Children.Add(new Border { CornerRadius = new CornerRadius(15), IsHitTestVisible = false, Background = new LinearGradientBrush(Color.FromArgb(0x10, 0, 0, 0), Color.FromArgb(0x90, 0, 0, 0), 90) });

            var badge = new Grid { Width = 46, Height = 46, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 12, 0, 0) };
            var disc = new Ellipse { Fill = Tint(o.Name) };
            badges.Add(disc);
            badge.Children.Add(disc);
            if (!string.IsNullOrEmpty(o.Glyph)) badge.Children.Add(new TextBlock { Text = o.Glyph, FontFamily = Ui.Glyphs, FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            else badge.Children.Add(new TextBlock { Text = o.Badge ?? "", FontSize = (o.Badge ?? "").Length > 1 ? 17 : 21, FontWeight = FontWeights.Bold, FontFamily = Ui.Body, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            face.Children.Add(badge);

            var tick = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 14, 0),
                BorderThickness = new Thickness(2), IsHitTestVisible = false,
                Child = new TextBlock { Text = "", FontFamily = Ui.Glyphs, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            if (o.Open != null) tick.Visibility = Visibility.Collapsed;   // (a card that opens something is never "on")
            checks.Add(tick);
            face.Children.Add(tick);

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 11), IsHitTestVisible = false };
            words.Children.Add(new TextBlock { Text = o.Name, FontSize = 19, FontWeight = FontWeights.SemiBold, FontFamily = Ui.Display, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(o.About))
                words.Children.Add(new TextBlock { Text = o.About, FontSize = 13, FontFamily = Ui.Body, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            face.Children.Add(words);
            tile.Face.Child = face;
            tiles.Add(tile);
            var cell = new Grid { Width = CellW, Height = CellH };
            tile.Root.HorizontalAlignment = HorizontalAlignment.Center;
            tile.Root.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(tile.Root);
            grid.Panel.Children.Add(cell);
        }

        // each source's own color, from its name: the cards are told apart at a glance before any picture arrives
        private static Brush Tint(string name)
        {
            var colors = new[] { 0x3D6FD8, 0xC2436B, 0x2F9E7A, 0xD98A2B, 0x7A52C7, 0x2B97B8, 0xB5483A, 0x5C8F2E };
            int h = 17;
            foreach (var ch in name ?? "") h = h * 31 + ch;
            int c = colors[Math.Abs(h % colors.Length)];
            var b = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            b.Freeze();
            return b;
        }

        // a card as picked or not: the focus color on its tick and its edge
        private void Paint(int i)
        {
            bool on = options[i].On;
            tiles[i].Face.Background = on ? Ink.Brush("SeleneFocusColor", 0.28) : Liquid.Fill();
            tiles[i].Face.BorderBrush = on ? Liquid.FocusRing : Liquid.Rim;
            checks[i].Background = on ? Ink.Brush("SeleneFocusColor") : Brushes.Transparent;
            checks[i].BorderBrush = on ? Liquid.FocusRing : new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
            ((TextBlock)checks[i].Child).Foreground = on ? Ink.Brush("SeleneOnFocusColor") : Brushes.Transparent;
        }

        private int Count { get { return options.Count(o => o.On); } }

        private void UpdateGo()
        {
            int n = Count;
            go.Text = n == 0 ? "Skip" : "Continue · " + n;
        }

        private void Toggle()
        {
            if (index >= options.Count) return;
            if (options[index].Open != null)
            {
                if (handled) return;
                handled = true;   // (the next screen is up: this one takes no more presses)
                Ui.Confirm();
                options[index].Open(options.Where(o => o.On).Select(o => o.Key).ToList());
                return;
            }
            options[index].On = !options[index].On;
            Paint(index);
            Ui.Pop(checks[index]);
            if (options[index].On) Ui.Confirm(); else Ui.Tick();
            UpdateGo();
        }

        private void ShowFocus()
        {
            for (int i = 0; i < tiles.Count; i++) tiles[i].Focus(i == index);
            go.Focus(index == options.Count);
            if (index < options.Count) grid.Reveal(index);
        }

        public override void Shown()
        {
            for (int i = 0; i < tiles.Count && i < 16; i++) Ui.Rise(tiles[i].Root, 120 + i * 28, 14, 260);
            ShowFocus();
            LoadPictures();
        }

        public override void Hidden() { left = true; }

        private void Continue()
        {
            if (handled) return;
            handled = true;
            Ui.Confirm();
            next(options.Where(o => o.On).Select(o => o.Key).ToList());
        }

        public override void Handle(string b)
        {
            if (handled) return;
            if (b == "B")
            {
                if (back == null) return;
                handled = true;
                Ui.Back();
                LoginScene.Show(back(), -1);
                return;
            }
            if (b == "Start") { Continue(); return; }
            if (b == "A") { if (index == options.Count) Continue(); else Toggle(); return; }
            if (b == "X" && skipAll != null) { handled = true; Ui.Back(); skipAll(); return; }
            if (b == "Y" && allowAll)
            {
                bool all = options.Any(o => o.Open == null && !o.On);
                for (int i = 0; i < options.Count; i++) { if (options[i].Open != null) continue; options[i].On = all; Paint(i); }
                Ui.Confirm();
                UpdateGo();
                return;
            }
            if (!b.StartsWith("DPad")) return;
            int to = index;
            if (index == options.Count)
            {
                if (b == "DPadUp") to = Math.Min(options.Count - 1, (options.Count - 1) / columns * columns + lastCol);
            }
            else
            {
                int move = Ui.GridMove(index, b, options.Count, columns);
                if (move >= 0) to = move;
                else if (b == "DPadDown") to = options.Count;
                if (to < options.Count) lastCol = to % columns;
            }
            if (to == index) return;
            index = to;
            Ui.Tick();
            ShowFocus();
        }

        // pictures come in as they arrive, off the UI thread: an avatar fills the card's badge, a feed's newest article is its backdrop
        private void LoadPictures()
        {
            var dispatcher = Application.Current.Dispatcher;
            var work = new List<int>();
            for (int i = 0; i < options.Count; i++) if (options[i].PictureUrl != null || options[i].FeedUrl != null) work.Add(i);
            if (work.Count == 0) return;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try
                {
                    System.Threading.Tasks.Parallel.ForEach(work, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                    {
                        if (left) return;
                        string file = null;
                        bool avatar = options[i].PictureUrl != null;
                        try
                        {
                            if (avatar) file = NewsCard.Picture(options[i].PictureUrl);
                            else
                            {
                                string errors;
                                var article = NewsFeeds.Fetch(new[] { new NewsFeed { Url = options[i].FeedUrl, Enabled = true } }, out errors, () => left).FirstOrDefault(a => !string.IsNullOrEmpty(a.Image));
                                if (article != null) file = NewsCard.Picture(article.Image);
                            }
                        }
                        catch (Exception ex) { HomeNav.Log("personalize picture: " + ex.Message); }
                        if (file == null || left) return;
                        int at = i; var path = file;
                        dispatcher.BeginInvoke(new Action(() => ShowPicture(at, path, avatar)), System.Windows.Threading.DispatcherPriority.Background);
                    });
                }
                catch (Exception ex) { HomeNav.Log("personalize pictures: " + ex.Message); }
            });
        }

        private void ShowPicture(int at, string file, bool avatar)
        {
            if (left || at >= tiles.Count) return;
            try
            {
                var src = Wallpapers.Load(file, avatar ? 176 : 560);
                if (src == null) return;
                if (avatar)
                {
                    badges[at].Fill = new ImageBrush(src) { Stretch = Stretch.UniformToFill };
                    var badge = (Grid)badges[at].Parent;
                    if (badge.Children.Count > 1) badge.Children[1].Visibility = Visibility.Collapsed;   // (the letters sat on the color)
                }
                else
                {
                    backdrops[at].Background = new ImageBrush(src) { Stretch = Stretch.UniformToFill };
                    Ui.Fade(backdrops[at], 0.7, 400);
                }
            }
            catch (Exception ex) { HomeNav.Log("personalize picture: " + ex.Message); }
        }
    }
}
