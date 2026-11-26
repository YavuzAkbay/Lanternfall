using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using Playnite.SDK.Models;

namespace Selene
{
    // Lanternfall: get a native square tile for every game, so Spotlight doesn't have to crop a tall cover.
    // SquareFetch already does the work for one game; this only walks the library gently on a worker thread.
    // It never bypasses the SteamGridDB key or the art-download consent, pauses while a game is running,
    // and a second press stops it after the game in flight.
    public static class SquareArtBulk
    {
        private const string SteamPlugin = "cb91dfc9-b977-43bf-8e70-55f46e410fab";
        private static readonly object gate = new object();
        private static bool running, stop;

        public static bool Running { get { lock (gate) return running; } }

        // null = ready; otherwise the reason the action can't start (shown on the settings row)
        public static string Problem()
        {
            if (!Features.On("squareart")) return "Turn on Square art from SteamGridDB first.";
            if (SquareFetch.Key == null) return "Add a SteamGridDB key first.";
            if (!DownloadConsent.Allowed("artdownload")) return "Turn on art downloads first.";
            return null;
        }

        public static void Start()
        {
            lock (gate)
            {
                if (running) { stop = true; Say("Square art: stopping after this game…"); return; }
                running = true;
                stop = false;
            }
            Say("Square art: getting tiles for your library…");
            var worker = new Thread(Run) { IsBackground = true, Name = "Square art library" };
            worker.Start();
        }

        private static void Run()
        {
            int total = 0, made = 0, had = 0, none = 0, failed = 0;
            try
            {
                // Spotlight's game list: real games only. Service cards (Browser, YouTube, Store, …) are
                // UI strings in the home row, not library entries, and are never sent to SteamGridDB.
                var games = GameFacts.Games();
                total = games.Count;
                var root = SelenePaths.Media("Square");
                var trailerRoot = SelenePaths.Media("Trailers");
                for (int i = 0; i < games.Count; i++)
                {
                    lock (gate) if (stop) break;
                    if (i % 100 == 99) Say("Square art: " + (i + 1) + " of " + total + "\u2026", true);   // before the skips below, so a mostly done library still reports
                    var g = games[i] as Game;
                    if (g == null || string.IsNullOrEmpty(g.Name)) continue;
                    var file = Path.Combine(root, g.Id + ".jpg");
                    if (File.Exists(file)) { had++; continue; }

                    while (HomeNav.GameRunning())
                    {
                        lock (gate) if (stop) break;
                        Thread.Sleep(1000);
                    }
                    lock (gate) if (stop) break;

                    bool asked = false;
                    for (int attempt = 0; attempt < 2 && !asked; attempt++)
                    {
                        try
                        {
                            // Steam games have a store id; everything else falls back to SteamGridDB's exact-name search
                            SquareFetch.Fetch(g, () => g.PluginId.ToString() == SteamPlugin ? g.GameId : null);
                            asked = true;
                        }
                        catch (WebException ex)
                        {
                            var response = ex.Response as HttpWebResponse;
                            if (response != null && response.StatusCode == (HttpStatusCode)429 && attempt == 0)
                            {
                                // SteamGridDB's rate limit: rest a minute (a stop is honoured within a second), then try this game once more
                                for (int rest = 0; rest < 60; rest++) { lock (gate) if (stop) break; Thread.Sleep(1000); }
                                lock (gate) if (stop) break;
                                continue;
                            }
                            // any other web error (timeout, 5xx, bad image link, a second rate limit) only skips this game
                            HomeNav.Log("square art: " + g.Name + ": " + ex.Message);
                            asked = true;
                        }
                        catch (Exception ex) { HomeNav.Log("square art: " + g.Name + ": " + ex.Message); }
                    }
                    if (File.Exists(file)) made++;
                    else if (File.Exists(Path.Combine(trailerRoot, g.Id + ".nosquare"))) none++;
                    else failed++;

                    Thread.Sleep(400);   // be gentle with a free API
                }
            }
            catch (Exception ex) { HomeNav.Log("square art library: " + ex.Message); }
            finally
            {
                lock (gate) { running = false; stop = false; }
                var app = Application.Current;
                if (app != null) app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if ("".Equals(app.Resources["SeleneSquareRoot"])) SeleneShell.ApplyArtRoots();
                    TileCovers.SquareArrived();
                }));
                Say(total == 0 ? "Square art: no games found." : "Square art: " + made + " new, " + had + " already had one, " + none + " unavailable, " + failed + " failed.");
            }
        }

        private static void Say(string text, bool quiet = false)
        {
            var app = Application.Current;
            if (app == null) { HomeNav.Log(text); return; }
            app.Dispatcher.BeginInvoke(new Action(() => Ui.Notice(text, 4000, quiet)));
        }
    }
}
