using System;
using System.Collections.Generic;
using System.Linq;

namespace Selene
{
    // Offline checks for the self-contained IGDB client: canned JSON only, no network.
    class IgdbMetaCheck
    {
        const string SearchJson = @"{
          ""data"": [
            {
              ""id"": 12345,
              ""name"": ""Honkai: Star Rail"",
              ""first_release_date"": 1704067200,
              ""platforms_expanded"": [ { ""name"": ""PC (Microsoft Windows)"" }, { ""name"": ""PlayStation 5"" } ],
              ""involved_companies_expanded"": [
                { ""developer"": true, ""publisher"": false, ""company_expanded"": { ""name"": ""HoYoverse"" } },
                { ""developer"": false, ""publisher"": true, ""company_expanded"": { ""name"": ""HoYoverse Publishing"" } }
              ],
              ""cover_expanded"": { ""image_id"": ""co1abc"" },
              ""textScore"": 42.5
            },
            { ""id"": 999, ""name"": ""   "", ""first_release_date"": null }
          ]
        }";

        const string GameJson = @"{
          ""data"": {
            ""id"": 12345,
            ""name"": ""Honkai: Star Rail"",
            ""summary"": ""A turn-based RPG."",
            ""first_release_date"": 1704067200,
            ""aggregated_rating"": 88.7,
            ""total_rating"": 91.2,
            ""rating"": 80.0,
            ""genres_expanded"": [ { ""name"": ""Role-playing (RPG)"" }, { ""name"": ""Adventure"" } ],
            ""involved_companies_expanded"": [
              { ""developer"": true, ""publisher"": false, ""company_expanded"": { ""name"": ""HoYoverse"" } },
              { ""developer"": false, ""publisher"": true, ""company_expanded"": { ""name"": ""HoYoverse Publishing"" } }
            ],
            ""game_modes_expanded"": [ { ""name"": ""Single player"" } ],
            ""age_ratings_expanded"": [
              { ""rating_category_expanded"": { ""rating"": ""3"" }, ""organization_expanded"": { ""name"": ""PEGI"" } },
              { ""rating_category_expanded"": { ""rating"": ""E"" }, ""organization_expanded"": { ""name"": ""ESRB"" } }
            ],
            ""collections_expanded"": [ { ""name"": ""Honkai"" } ],
            ""websites_expanded"": [
              { ""url"": ""https://hsr.hoyoverse.com"", ""type_expanded"": { ""type"": ""official"" } }
            ],
            ""cover_expanded"": { ""image_id"": ""co1abc"", ""width"": 600, ""height"": 800 },
            ""artworks_expanded"": [
              { ""image_id"": ""art1"", ""width"": 3840, ""height"": 2160, ""artwork_type"": 2, ""alpha_channel"": false },
              { ""image_id"": ""logo"", ""width"": 1920, ""height"": 1080, ""artwork_type"": 7, ""alpha_channel"": true },
              { ""image_id"": ""art2"", ""width"": 2560, ""height"": 1080, ""artwork_type"": 1, ""alpha_channel"": false },
              { ""image_id"": ""narrow"", ""width"": 1920, ""height"": 1200, ""artwork_type"": 2, ""alpha_channel"": false }
            ],
            ""screenshots_expanded"": [
              { ""image_id"": ""shot1"", ""width"": 1920, ""height"": 1080, ""alpha_channel"": false },
              { ""image_id"": ""shot2"", ""width"": 1280, ""height"": 720, ""alpha_channel"": false }
            ]
          }
        }";

        const string PortraitGameJson = @"{
          ""data"": {
            ""name"": ""Portrait test"",
            ""cover_expanded"": { ""image_id"": ""co1abc"", ""width"": 600, ""height"": 800 },
            ""artworks_expanded"": [
              { ""image_id"": ""artP1"", ""width"": 600, ""height"": 800, ""alpha_channel"": false },
              { ""image_id"": ""wide"", ""width"": 1920, ""height"": 1080, ""alpha_channel"": false },
              { ""image_id"": ""artAlpha"", ""width"": 600, ""height"": 800, ""alpha_channel"": true },
              { ""image_id"": ""co1abc"", ""width"": 600, ""height"": 800, ""alpha_channel"": false }
            ],
            ""screenshots_expanded"": [
              { ""image_id"": ""shotP"", ""width"": 1080, ""height"": 1920, ""alpha_channel"": false }
            ]
          }
        }";

        const string NoCoverGameJson = @"{""data"": { ""name"": ""No cover"", ""screenshots_expanded"": [ { ""image_id"": ""landscape"", ""width"": 1920, ""height"": 1080 } ] }}";

        static int failures;

        static void Eq(object expected, object actual, string what)
        {
            if (!object.Equals(expected, actual))
                throw new Exception(what + ": expected [" + expected + "], got [" + actual + "]");
        }

        static void Group(string name, Action check)
        {
            int before = failures;
            try { check(); }
            catch (Exception ex) { Console.Error.WriteLine("  " + ex.Message); failures++; }
            Console.WriteLine((failures == before ? "PASS " : "FAIL ") + name);
        }

        static int Main()
        {
            Group("ParseSearch", delegate
            {
                var hits = IgdbMetadataClient.ParseSearch(SearchJson);
                Eq(1, hits.Count, "blank-name entries are skipped");
                var hit = hits[0];
                Eq("12345", hit.Id, "search id");
                Eq("Honkai: Star Rail", hit.Name, "search name");
                Eq(2024, hit.Year, "search year from Unix seconds (UTC)");
                Eq(2, hit.Platforms.Count, "search platforms count");
                Eq(true, hit.Platforms.Contains("PlayStation 5"), "search platform name");
                Eq(true, hit.Developers.Contains("HoYoverse"), "search developer");
                Eq(false, hit.Developers.Contains("HoYoverse Publishing"), "publisher is not a developer");
                Eq(true, hit.Publishers.Contains("HoYoverse Publishing"), "search publisher");
                Eq("co1abc", hit.CoverImageId, "search cover id");
                Eq(42.5, hit.Score, "search text score");
            });

            Group("ParseGame", delegate
            {
                var game = IgdbMetadataClient.ParseGame(GameJson);
                if (game == null) throw new Exception("game did not parse");
                Eq("12345", game.Id, "game id");
                Eq("Honkai: Star Rail", game.Name, "game name");
                Eq("A turn-based RPG.", game.Description, "game summary");
                Eq(2024, game.ReleaseDate.Value.Year, "game release year");
                Eq(89, game.CriticScore, "game critic score (aggregated_rating)");
                Eq(91, game.CommunityScore, "game community score prefers total_rating");
                Eq(2, game.Genres.Count, "game genres count");
                Eq(true, game.Genres.Contains("Adventure"), "game genre name");
                Eq(true, game.Developers.Contains("HoYoverse"), "game developer");
                Eq(true, game.Publishers.Contains("HoYoverse Publishing"), "game publisher");
                Eq(1, game.Features.Count, "game modes become features");
                Eq("Single player", game.Features[0], "feature name");
                Eq(2, game.AgeRatings.Count, "age ratings count");
                Eq(true, game.AgeRatings.Contains("PEGI 3"), "PEGI rating");
                Eq(true, game.AgeRatings.Contains("ESRB E"), "ESRB rating");
                Eq(true, game.Series.Contains("Honkai"), "game series from collections");
                Eq(1, game.Links.Count, "game links count");
                Eq("official", game.Links[0].Name, "link type");
                Eq("https://hsr.hoyoverse.com", game.Links[0].Url, "link url");
                Eq("co1abc", game.CoverImageId, "game cover id");
                Eq(4, game.Artworks.Count, "artwork candidates");
                Eq(2, game.Screenshots.Count, "screenshot candidates");
                Eq("screenshot", game.Screenshots[0].Kind, "screenshot kind");
            });

            Group("ImageUrl", delegate
            {
                Eq("https://images.igdb.com/igdb/image/upload/t_1080p/co1abc.jpg",
                   IgdbMetadataClient.ImageUrl("https://images.igdb.com/igdb/image/upload/t_thumb/co1abc.jpg", "1080p"),
                   "https URL size replacement");
                Eq("https://images.igdb.com/igdb/image/upload/t_original/co1abc.png",
                   IgdbMetadataClient.ImageUrl("http://images.igdb.com/igdb/image/upload/t_thumb/co1abc.png", "original"),
                   "http URL normalized to https, extension preserved");
                Eq("https://images.igdb.com/igdb/image/upload/t_720p/co1abc.jpg",
                   IgdbMetadataClient.ImageUrl("//images.igdb.com/igdb/image/upload/t_cover_big/co1abc.jpg", "720p"),
                   "protocol-relative URL normalized");
                Eq("https://images.igdb.com/igdb/image/upload/t_1080p/co1abc.jpg",
                   IgdbMetadataClient.ImageUrl("co1abc", "1080p"),
                   "bare image id becomes an IGDB URL");
            });

            Group("BackgroundCandidates", delegate
            {
                var game = IgdbMetadataClient.ParseGame(GameJson);
                var candidates = IgdbMetadataClient.BackgroundCandidates(game);
                Eq(5, candidates.Count, "alpha, narrow and unknown artwork types dropped");
                var ids = candidates.Select(c => c.ImageId).ToArray();
                Eq("art1,narrow,art2,shot1,shot2", string.Join(",", ids), "rank then width ordering");
                Eq("screenshot", candidates[3].Kind, "screenshots come after artwork");

                var many = new IgdbMetadataClient.IgdbGame { Screenshots = new List<IgdbMetadataClient.IgdbImage>() };
                for (var i = 0; i < 15; i++)
                    many.Screenshots.Add(new IgdbMetadataClient.IgdbImage { ImageId = "s" + i, Width = 1920, Height = 1080, Kind = "screenshot" });
                Eq(12, IgdbMetadataClient.BackgroundCandidates(many).Count, "capped at 12");
                Eq(0, IgdbMetadataClient.BackgroundCandidates(null).Count, "null game yields no candidates");
            });

            Group("CoverCandidates", delegate
            {
                // the backend has a single cover: no "covers_expanded", and these artworks are all landscape
                var plain = IgdbMetadataClient.ParseGame(GameJson);
                var plainCovers = IgdbMetadataClient.CoverCandidates(plain);
                Eq(1, plainCovers.Count, "official cover only when no portrait art exists");
                Eq("co1abc", plainCovers[0].ImageId, "official cover is first");
                Eq("cover", plainCovers[0].Kind, "official cover kind");
                Eq(600, plain.CoverImage.Width, "cover width from cover_expanded");
                Eq(800, plain.CoverImage.Height, "cover height from cover_expanded");

                // portrait art/screenshots become alternative covers; landscape, alpha and the duplicate cover are skipped
                var portrait = IgdbMetadataClient.ParseGame(PortraitGameJson);
                var covers = IgdbMetadataClient.CoverCandidates(portrait);
                Eq(3, covers.Count, "official cover plus two portrait alternatives");
                Eq("co1abc,artP1,shotP", string.Join(",", covers.Select(c => c.ImageId).ToArray()), "cover, artwork, screenshot order");
                Eq("screenshot", covers[2].Kind, "portrait screenshot keeps its kind");

                Eq(0, IgdbMetadataClient.CoverCandidates(null).Count, "null game yields no candidates");
                Eq(0, IgdbMetadataClient.CoverCandidates(IgdbMetadataClient.ParseGame(NoCoverGameJson)).Count,
                   "no cover and no portrait art yields no candidates beyond keep-current");

                var many = new IgdbMetadataClient.IgdbGame { Screenshots = new List<IgdbMetadataClient.IgdbImage>() };
                for (var i = 0; i < 15; i++)
                    many.Screenshots.Add(new IgdbMetadataClient.IgdbImage { ImageId = "p" + i, Width = 600, Height = 800, Kind = "screenshot" });
                Eq(12, IgdbMetadataClient.CoverCandidates(many).Count, "cover candidates capped at 12");
            });

            Console.WriteLine(failures == 0 ? "All IGDB metadata checks passed." : (failures + " IGDB metadata check(s) failed."));
            return failures == 0 ? 0 : 1;
        }
    }
}
