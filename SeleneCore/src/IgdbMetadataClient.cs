using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.IO;
using System.Web;
using System.Web.Script.Serialization;

namespace Selene
{
    // Self-contained IGDB metadata client that talks to Playnite's own backend (no API key needed).
    // Kept free of Playnite SDK / WPF references so it can be unit-tested on its own.
    internal static class IgdbMetadataClient
    {
        private const string Backend = "https://api2.playnite.link/api/igdb/";
        private const int TimeoutMs = 30000;
        private const string Agent = "Mozilla/5.0 (compatible; SeleneCore IGDB client)";

        internal class IgdbHit
        {
            public string Id;
            public string Name;
            public int? Year;
            public List<string> Platforms;
            public List<string> Developers;
            public List<string> Publishers;
            public string CoverImageId;
            public double Score;
        }

        internal class IgdbGame
        {
            public string Id;
            public string Name;
            public string Description;
            public DateTime? ReleaseDate;
            public int? CriticScore;
            public int? CommunityScore;
            public List<string> Genres;
            public List<string> Developers;
            public List<string> Publishers;
            public List<string> Features;
            public List<string> AgeRatings;
            public List<string> Series;
            public List<IgdbWebLink> Links;
            public string CoverImageId;
            public IgdbImage CoverImage;   // the one cover the backend has (cover_expanded), with its size
            public List<IgdbImage> Artworks;
            public List<IgdbImage> Screenshots;
        }

        internal class IgdbImage
        {
            public string ImageId;
            public int Width;
            public int Height;
            public int Type;
            public bool Alpha;
            public string Kind;
        }

        internal class IgdbWebLink
        {
            public string Name;
            public string Url;
        }

        // ---------- parsing ----------

        public static List<IgdbHit> ParseSearch(string json)
        {
            var hits = new List<IgdbHit>();
            var data = At(Deserialize(json), "data") as IEnumerable<object>;
            if (data == null) return hits;
            foreach (var o in data)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var name = Convert.ToString(At(d, "name"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                var hit = new IgdbHit();
                hit.Id = Convert.ToString(At(d, "id"));
                hit.Name = name;
                var first = At(d, "first_release_date");
                if (first != null) hit.Year = FromUnix(Convert.ToDouble(first)).Year;
                hit.Platforms = Names(At(d, "platforms_expanded"), "name");
                hit.Developers = new List<string>();
                hit.Publishers = new List<string>();
                Companies(d, hit.Developers, hit.Publishers);
                hit.CoverImageId = Convert.ToString(At(d, "cover_expanded", "image_id"));
                var score = At(d, "textScore");
                hit.Score = score == null ? 0 : Convert.ToDouble(score);
                hits.Add(hit);
            }
            return hits;
        }

        public static IgdbGame ParseGame(string json)
        {
            var d = At(Deserialize(json), "data") as Dictionary<string, object>;
            if (d == null) return null;
            var game = new IgdbGame();
            game.Id = Convert.ToString(At(d, "id"));
            game.Name = Convert.ToString(At(d, "name"));
            game.Description = Convert.ToString(At(d, "summary"));
            var released = At(d, "first_release_date");
            if (released != null) game.ReleaseDate = FromUnix(Convert.ToDouble(released));
            var critic = At(d, "aggregated_rating");
            if (critic != null) game.CriticScore = Convert.ToInt32(Convert.ToDouble(critic));
            var community = At(d, "total_rating");
            if (community == null) community = At(d, "rating");
            if (community != null) game.CommunityScore = Convert.ToInt32(Convert.ToDouble(community));
            game.Genres = Names(At(d, "genres_expanded"), "name");
            game.Developers = new List<string>();
            game.Publishers = new List<string>();
            Companies(d, game.Developers, game.Publishers);
            game.Features = Names(At(d, "game_modes_expanded"), "name");
            game.AgeRatings = AgeRatings(At(d, "age_ratings_expanded"));
            game.Series = Names(At(d, "collections_expanded"), "name");
            game.Links = Links(At(d, "websites_expanded"));
            game.CoverImageId = Convert.ToString(At(d, "cover_expanded", "image_id"));
            if (!string.IsNullOrWhiteSpace(game.CoverImageId))
                game.CoverImage = new IgdbImage
                {
                    ImageId = game.CoverImageId,
                    Width = Convert.ToInt32(At(d, "cover_expanded", "width")),
                    Height = Convert.ToInt32(At(d, "cover_expanded", "height")),
                    Kind = "cover"
                };
            game.Artworks = Images(At(d, "artworks_expanded"), "artwork");
            game.Screenshots = Images(At(d, "screenshots_expanded"), "screenshot");
            return game;
        }

        private static void Companies(Dictionary<string, object> d, List<string> devs, List<string> pubs)
        {
            var companies = At(d, "involved_companies_expanded") as IEnumerable<object>;
            if (companies == null) return;
            foreach (var c in companies)
            {
                var name = Convert.ToString(At(c, "company_expanded", "name"));
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim();
                if (Equals(At(c, "developer"), true) && !devs.Contains(name)) devs.Add(name);
                if (Equals(At(c, "publisher"), true) && !pubs.Contains(name)) pubs.Add(name);
            }
        }

        private static List<string> AgeRatings(object list)
        {
            var result = new List<string>();
            var entries = list as IEnumerable<object>;
            if (entries == null) return result;
            foreach (var e in entries)
            {
                var rating = Convert.ToString(At(e, "rating_category_expanded", "rating"));
                if (string.IsNullOrWhiteSpace(rating)) continue;
                var org = Convert.ToString(At(e, "organization_expanded", "name"));
                if (string.IsNullOrWhiteSpace(org)) org = Convert.ToString(At(e, "rating_category_expanded", "organization_expanded", "name"));
                result.Add((string.IsNullOrWhiteSpace(org) ? "" : org.Trim() + " ") + rating.Trim());
            }
            return result;
        }

        private static List<IgdbWebLink> Links(object list)
        {
            var result = new List<IgdbWebLink>();
            var sites = list as IEnumerable<object>;
            if (sites == null) return result;
            foreach (var s in sites)
            {
                var url = Convert.ToString(At(s, "url"));
                if (string.IsNullOrWhiteSpace(url)) continue;
                result.Add(new IgdbWebLink { Name = Convert.ToString(At(s, "type_expanded", "type")), Url = url });
            }
            return result;
        }

        private static List<IgdbImage> Images(object list, string kind)
        {
            var result = new List<IgdbImage>();
            var entries = list as IEnumerable<object>;
            if (entries == null) return result;
            foreach (var o in entries)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var id = Convert.ToString(At(d, "image_id"));
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new IgdbImage
                {
                    ImageId = id,
                    Width = Convert.ToInt32(At(d, "width")),
                    Height = Convert.ToInt32(At(d, "height")),
                    Type = Convert.ToInt32(At(d, "artwork_type")),
                    Alpha = Equals(At(d, "alpha_channel"), true),
                    Kind = kind
                });
            }
            return result;
        }

        // ---------- images ----------

        // Turn an IGDB image URL into the requested size, or an image id into an IGDB URL.
        public static string ImageUrl(string urlOrId, string size)
        {
            if (string.IsNullOrWhiteSpace(urlOrId)) return null;
            var s = urlOrId.Trim();
            var isUrl = s.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("//", StringComparison.Ordinal);
            if (!isUrl) return "https://images.igdb.com/igdb/image/upload/t_" + size + "/" + s + ".jpg";
            if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
            else if (s.StartsWith("http:", StringComparison.OrdinalIgnoreCase)) s = "https:" + s.Substring(5);
            var start = s.IndexOf("/t_", StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                var end = s.IndexOf('/', start + 3);
                if (end > start) s = s.Substring(0, start) + "/t_" + size + s.Substring(end);
            }
            return s;
        }

        // Wide, opaque backgrounds only: key art without a logo first, then other artwork, then screenshots.
        public static List<IgdbImage> BackgroundCandidates(IgdbGame game)
        {
            var result = new List<IgdbImage>();
            if (game == null) return result;
            var ranked = new List<KeyValuePair<int, IgdbImage>>();
            if (game.Artworks != null)
            {
                foreach (var image in game.Artworks)
                {
                    int rank;
                    if (image.Type == 2) rank = 0;
                    else if (image.Type == 1) rank = 1;
                    else if (image.Type == 3) rank = 2;
                    else continue;
                    AddCandidate(ranked, rank, image);
                }
            }
            if (game.Screenshots != null)
            {
                foreach (var image in game.Screenshots) AddCandidate(ranked, 10, image);
            }
            foreach (var pair in ranked.OrderBy(p => p.Key).ThenByDescending(p => p.Value.Width).Take(12))
                result.Add(pair.Value);
            return result;
        }

        private static void AddCandidate(List<KeyValuePair<int, IgdbImage>> ranked, int rank, IgdbImage image)
        {
            if (image == null || image.Alpha || image.Width < 1280 || image.Height <= 0) return;
            if ((double)image.Width / image.Height < 1.6) return;
            foreach (var have in ranked)
                if (string.Equals(have.Value.ImageId, image.ImageId, StringComparison.OrdinalIgnoreCase)) return;
            ranked.Add(new KeyValuePair<int, IgdbImage>(rank, image));
        }

        // Cover sources the backend really has: the single official cover (cover_expanded, kept first when present),
        // then portrait artwork/screenshots that can serve as an alternative cover. No key, no invented image ids.
        // The UI adds its own "keep current cover" choice at index 0.
        public static List<IgdbImage> CoverCandidates(IgdbGame game)
        {
            var result = new List<IgdbImage>();
            if (game == null) return result;
            if (game.CoverImage != null && !string.IsNullOrWhiteSpace(game.CoverImage.ImageId)) AddCover(result, game.CoverImage, false);
            else if (!string.IsNullOrWhiteSpace(game.CoverImageId))
                AddCover(result, new IgdbImage { ImageId = game.CoverImageId, Kind = "cover" }, false);
            if (game.Artworks != null)
                foreach (var image in game.Artworks) AddCover(result, image, true);
            if (game.Screenshots != null)
                foreach (var image in game.Screenshots) AddCover(result, image, true);
            if (result.Count > 12) result.RemoveRange(12, result.Count - 12);
            return result;
        }

        private static void AddCover(List<IgdbImage> result, IgdbImage image, bool portraitOnly)
        {
            if (image == null || string.IsNullOrWhiteSpace(image.ImageId)) return;
            // the same picture can appear as both a cover and an artwork; offer it once
            foreach (var have in result)
                if (string.Equals(have.ImageId, image.ImageId, StringComparison.OrdinalIgnoreCase)) return;
            if (portraitOnly && (image.Type == 7 || image.Alpha || image.Width <= 0 || image.Height <= image.Width)) return;   // 7 = logo
            result.Add(image);
        }

        // ---------- network ----------

        public static string Search(string query)
        {
            var body = "{\"SearchTerm\":" + new JavaScriptSerializer().Serialize(query ?? "") + "}";
            return Request(Backend + "search", body);
        }

        public static string Game(string id)
        {
            return Request(Backend + "game/" + Uri.EscapeDataString(id ?? ""), null);
        }

        private const int MaxDownload = 20 * 1024 * 1024;

        public static void Download(string url, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.UserAgent = Agent;
            using (var response = (HttpWebResponse)req.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                if (response.ContentLength > MaxDownload) throw new InvalidDataException("The picture is too large");
                var buffer = new byte[16384]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + count > MaxDownload) throw new InvalidDataException("The picture is too large");
                    memory.Write(buffer, 0, count);
                }
                // written beside it and moved in whole: a cut-off picture must not stay under the final name
                var part = path + ".part";
                File.WriteAllBytes(part, memory.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(part, path);
            }
        }

        private static string Request(string url, string body)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = TimeoutMs;
            req.ReadWriteTimeout = TimeoutMs;
            req.UserAgent = Agent;
            req.Method = body == null ? "GET" : "POST";
            if (body != null)
            {
                req.ContentType = "application/json";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            using (var response = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), System.Text.Encoding.UTF8))
                return reader.ReadToEnd();
        }

        // ---------- helpers ----------

        private static object Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
        }

        internal static object At(object node, params string[] path)
        {
            object current = node;
            foreach (var key in path)
            {
                var d = current as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out current)) return null;
            }
            return current;
        }

        internal static List<string> Names(object list, string key)
        {
            var names = new List<string>();
            var entries = list as IEnumerable<object>;
            if (entries == null) return names;
            foreach (var o in entries)
            {
                var name = key == null ? Convert.ToString(o) : Convert.ToString(At(o, key));
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            }
            return names;
        }

        private static DateTime FromUnix(double seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }
    }
}
