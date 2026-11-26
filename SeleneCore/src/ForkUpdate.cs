using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Selene
{
    // Lanternfall: the Browser add-on (Browser and YouTube screens) has a version of its own, and its fixes (YouTube changes
    // its site now and then) usually ship in a suffix release such as 1.5.8-1, whose theme and Core keep their version.
    // Selene's update check compares only the theme's version, so such a release would never be offered. This adds: when
    // the newest release carries a newer Lanternfall Browser than the one installed, it counts as an update, and only
    // LanternfallBrowser.pext is downloaded and installed. UpdateCheck and UpdateScreen call in here through one-line hooks.
    //
    // The release's Browser version comes from its BUILDINFO.txt ("Lanternfall Browser: 0.4.1", written by
    // tools\package.ps1), or else from its notes ("Lanternfall Browser 0.4.1"), for releases made before that line existed.
    public static class ForkUpdate
    {
        private static readonly Regex buildInfoLine = new Regex(@"(?m)^Lanternfall Browser:\s*v?(\d+(?:\.\d+){1,3})\s*$");
        private static readonly Regex notesMention = new Regex(@"Lanternfall Browser\s+v?(\d+(?:\.\d+){1,3})\b");

        // the newest release's Browser version ("" when it says none) and the installed one (null when it isn't installed)
        public static string ReleaseBrowser { get; private set; }
        private static string installed;
        private static bool installedRead;

        static ForkUpdate() { ReleaseBrowser = ""; }

        // on the update check's worker thread, right after the newest release was read
        public static void Read(string tag, string notes)
        {
            string found = null;
            try
            {
                if (!string.IsNullOrEmpty(tag))
                {
                    try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; } catch { }
                    using (var web = new TimedWebClient { Encoding = System.Text.Encoding.UTF8 })
                    {
                        web.Headers[System.Net.HttpRequestHeader.UserAgent] = "Selene-Playnite";
                        var m = buildInfoLine.Match(web.DownloadString(ForkBrand.RepoUrl + "/releases/download/v" + tag + "/BUILDINFO.txt"));
                        if (m.Success) found = m.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex) { HomeNav.Log("update check: BUILDINFO.txt: " + ex.Message); }
            if (found == null && !string.IsNullOrEmpty(notes))
            {
                // the highest version the notes name (they may mention an older one too)
                found = notesMention.Matches(notes).Cast<Match>().Select(m => m.Groups[1].Value)
                    .OrderByDescending(v => Parse(v)).FirstOrDefault();
            }
            ReleaseBrowser = found ?? "";
            HomeNav.Log("update check: release " + tag + " carries Lanternfall Browser " + (found ?? "(not named)") + ", installed " + (Installed() ?? "(none)")
                + (BrowserNewer ? ": the Browser can be updated" : ""));
        }

        // the installed Lanternfall Browser's version, from the extension.yaml beside its DLL (null: not installed / not loaded)
        public static string Installed()
        {
            if (installedRead) return installed;
            installedRead = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SeleneBrowser");
                string yaml = null;
                if (asm != null && !string.IsNullOrEmpty(asm.Location)) yaml = Path.Combine(Path.GetDirectoryName(asm.Location), "extension.yaml");
                if ((yaml == null || !File.Exists(yaml)) && HomeNav.Api != null)
                {
                    var ext = Path.Combine(HomeNav.Api.Paths.ConfigurationPath, "Extensions");
                    if (Directory.Exists(ext)) yaml = Directory.GetDirectories(ext, "LanternfallBrowser_*").Select(d => Path.Combine(d, "extension.yaml")).FirstOrDefault(File.Exists);
                }
                if (yaml != null && File.Exists(yaml))
                {
                    var m = Regex.Match(File.ReadAllText(yaml), @"(?m)^Version:\s*['""]?([0-9][0-9.]*)");
                    if (m.Success) installed = m.Groups[1].Value;
                }
            }
            catch (Exception ex) { HomeNav.Log("update check: installed Browser version: " + ex.Message); }
            return installed;
        }

        // the newest release has a newer Browser than the installed one
        public static bool BrowserNewer
        {
            get
            {
                var have = Installed();
                return have != null && ReleaseBrowser.Length > 0 && Parse(ReleaseBrowser) > Parse(have);
            }
        }

        // the update is the Browser alone: its version moved, the theme's didn't
        public static bool BrowserOnly
        {
            get
            {
                Version a, b;
                var tag = UpdateCheck.Available ?? "";
                bool themeNewer = tag.Length > 0 && Version.TryParse(tag.Split('-')[0], out a) && Version.TryParse((Brand.Version ?? "0").Split('-')[0], out b) && a > b;
                return !themeNewer && BrowserNewer;
            }
        }

        // the packages to download and install, and each one's share of the progress bar
        public static string[] Packages(string[] all) { return BrowserOnly ? new[] { ForkBrand.BrowserPackage } : all; }
        public static double[] Weights(double[] all) { return BrowserOnly ? new[] { 1.0 } : all; }

        // "Selene 1.5.9" for a whole update, "Lanternfall Browser 0.4.1" for a Browser-only one
        public static string Name(string whole) { return BrowserOnly ? "Lanternfall Browser " + ReleaseBrowser : ForkBrand.Text(whole); }

        // "You have 1.5.8." / "You have Browser 0.4.0."
        public static string Have(string whole) { return BrowserOnly ? "Browser " + Installed() : ForkBrand.Text(whole); }

        private static Version Parse(string v)
        {
            Version r;
            if (!Version.TryParse(v.Contains('.') ? v : v + ".0", out r)) return new Version(0, 0);
            return r;
        }
    }
}
