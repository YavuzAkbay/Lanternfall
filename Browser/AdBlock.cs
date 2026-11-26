// AdBlock.cs - ad blocking for the Edge WebView2 browser view.
//  1) host blocker: one WebResourceRequested filter per known ad/tracker host, so normal requests never reach .NET
//  2) best effort: uBlock Origin Lite fetched from GitHub into DataDir\extensions\ubol and added as a WebView2 browser extension
// Everything is wrapped in try/catch: a failure here must never break browsing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Playnite.SDK;

namespace SeleneBrowser
{
    internal static class AdBlock
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object gate = new object();
        private static int blocked;
        private static int pageBlocked;
        private static Task<string> extTask;
        private static bool installStarted;

        /// <summary>Requests answered with 403 by the host blocker since start.</summary>
        public static int BlockedCount { get { return blocked; } }

        /// <summary>Requests answered with 403 since the current page started loading.</summary>
        public static int PageBlockedCount { get { return pageBlocked; } }

        // never list anything YouTube (googlevideo, ytimg, gstatic, googleapis), jsdelivr, Twitch or Kick need to play
        private const string HostList = @"
doubleclick.net googlesyndication.com googleadservices.com adservice.google.com pagead2.googlesyndication.com googletagservices.com 2mdn.net
google-analytics.com adsense.google.com partner.googleadservices.com
amazon-adsystem.com adnxs.com adsrvr.org taboola.com outbrain.com criteo.com criteo.net pubmatic.com rubiconproject.com openx.net
casalemedia.com indexww.com contextweb.com lijit.com sovrn.com sharethrough.com triplelift.com 33across.com yieldmo.com gumgum.com
smartadserver.com adform.net adform.com appnexus.com advertising.com adtech.de adtechus.com bidswitch.net bidtheatre.com
media.net mediavine.com adthrive.com ezoic.net ezoic.com ezodn.com ezojs.com monetizemore.com
revcontent.com mgid.com content.ad zemanta.com nativo.com nativo.net yahoo-ads.com ads.yahoo.com advertising.yahoo.com gemini.yahoo.com
moatads.com doubleverify.com adsafeprotected.com integralads.com serving-sys.com sizmek.com flashtalking.com
innovid.com spotxchange.com spotx.tv springserve.com tremorhub.com tremorvideo.com teads.tv freewheel.tv fwmrm.net
lkqd.net lkqd.com connatix.com primis.tech vidazoo.com vidoomy.com undertone.com
adcolony.com applovin.com unityads.unity3d.com chartboost.com vungle.com ironsrc.com supersonicads.com inmobi.com mopub.com
adroll.com rlcdn.com bluekai.com krxd.net demdex.net everesttech.net omtrdc.net adobedtm.com
scorecardresearch.com quantserve.com quantcount.com chartbeat.com chartbeat.net hotjar.com mouseflow.com crazyegg.com
segment.io mixpanel.com amplitude.com fullstory.com
bat.bing.com clarity.ms
adsymptotic.com adblade.com adbrite.com admob.com adition.com adspirit.de
rfihub.com rfihub.net turn.com mathtag.com bidr.io tapad.com liveramp.com
exelator.com eyeota.net lotame.com crwdcntrl.net
popads.net popcash.net popunderjs.com propellerads.com propellerclick.com propu.sh
exoclick.com exosrv.com exdynsrv.com exoticads.com juicyads.com juicyads.net trafficjunky.com trafficjunky.net trafficfactory.biz
clickadu.com clickaine.com clickdealer.com hilltopads.net hilltopads.com adcash.com adcashmedia.com
onclickads.net onclkds.com onclasrv.com onclicka.com adsterra.com adsterratools.com highperformanceformat.com
a-ads.com ad-maven.com admaven.com adsco.re adspyglass.com adskeeper.com adskeeper.co.uk
richpush.co push.house pushwhy.com pushnami.com pushengage.com notix.io
mondiad.com monadplug.com ad-center.com adx1.com adxpansion.com
bidgear.com bidvertiser.com buysellads.com carbonads.net carbonads.com bsa.pro
trafficstars.com tsyndicate.com
realsrv.com tsyndicate.com syndication.exoclick.com
cdn4ads.com cdn7.network adnium.com
yllix.com ylx-1.com
usersync.org
zedo.com zeusclicks.com
yandexadexchange.net adfox.ru
adriver.ru
hybrid.ai
lijit.com
openx.com
nend.net i-mobile.co.jp
adingo.jp
fastclick.net valueclick.com
mediaplex.com
tribalfusion.com
conversantmedia.com dotomi.com
yieldlab.net yieldlab.de
ligatus.com
plista.com
imrworldwide.com
ads-twitter.com
ads.linkedin.com
analytics.tiktok.com
ads.pinterest.com
ads.reddit.com events.reddit.com
sb.scorecardresearch.com
track.adform.net
cdn.taboola.com
trc.taboola.com
tr.snapchat.com
pixel.facebook.com
adsmeasurement.com
bttrack.com
cpmstar.com
cpx.to
clkmg.com
clkrev.com
clicksor.com
inuvo.com
infolinks.com
intellitxt.com
kontera.com
vibrantmedia.com
adblockanalytics.com
adscale.de
admixer.net
adotmob.com
adhigh.net
adsrvmedia.net
afcdn.net
dtscout.com
dtscdn.com
gadsme.com
glowlinks.com
medianet.com
nsmartad.com
pagefair.com
pagefair.net
plugrush.com
rtbhouse.com
rtbhouse.net
runative-syndicate.com
smaato.net
smaato.com
sskzlabs.com
tapjoy.com
tradedoubler.com
trafmag.com
trkn.us
uimserv.net
vdo.ai
vidverto.io
viewability.com
voluum.com
voluumtrk.com
zergnet.com
";

        private static string[] hosts;

        private static string[] Hosts()
        {
            if (hosts != null) return hosts;
            var set = new HashSet<string>();
            foreach (string h in HostList.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(h.ToLowerInvariant());
            var arr = new string[set.Count];
            set.CopyTo(arr);
            hosts = arr;
            return arr;
        }

        /// <summary>Registers the host filters on a (non-player) view and answers matching requests with 403.</summary>
        public static void Attach(CoreWebView2 core)
        {
            try
            {
                CoreWebView2Environment env = core.Environment;
                foreach (string h in Hosts())
                {
                    core.AddWebResourceRequestedFilter("*://*." + h + "/*", CoreWebView2WebResourceContext.All);
                    core.AddWebResourceRequestedFilter("*://" + h + "/*", CoreWebView2WebResourceContext.All);
                }
                core.WebResourceRequested += delegate(object o, CoreWebView2WebResourceRequestedEventArgs e)
                {
                    try
                    {
                        if (!BrowserSettings.AdBlockEnabled) return;   // switched off in the browser's settings: let it through
                        e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                        Interlocked.Increment(ref blocked);
                        Interlocked.Increment(ref pageBlocked);
                    }
                    catch (Exception) { }
                };
                core.NavigationStarting += delegate { Interlocked.Exchange(ref pageBlocked, 0); };   // top-level navigations only
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: AdBlock host filters: " + ex.Message); }
        }

        // ----- uBlock Origin Lite -----

        private static string ExtRoot { get { return Path.Combine(SeleneBrowserPlugin.DataDir, "extensions"); } }
        private static string ExtDir { get { return Path.Combine(ExtRoot, "ubol"); } }
        private static string TagFile { get { return Path.Combine(ExtRoot, "ubol.tag"); } }
        private static string InstalledFile { get { return Path.Combine(ExtRoot, "ubol.installed"); } }

        /// <summary>Starts (once) the background download/refresh of the extension folder.</summary>
        public static void StartExtensionFetch()
        {
            if (!BrowserSettings.AdBlockEnabled) return;   // off: nothing is downloaded
            lock (gate)
            {
                if (extTask != null) return;
                // a fetch that found nothing (offline) is tried again from a later view, but not more often than every 5 minutes
                if (fetchTried && unchecked(Environment.TickCount - lastFetchAt) < 5 * 60 * 1000) return;
                fetchTried = true;
                lastFetchAt = Environment.TickCount;
                extTask = Task.Factory.StartNew<string>(Fetch, TaskCreationOptions.LongRunning);
            }
        }

        private static bool fetchTried;
        private static int lastFetchAt;

        private static string Fetch()
        {
            string manifest = Path.Combine(ExtDir, "manifest.json");
            try
            {
                Directory.CreateDirectory(ExtRoot);
                bool fresh = File.Exists(manifest) && File.Exists(TagFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(TagFile)).TotalDays < 14;
                if (!fresh) Download();
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: AdBlock extension fetch: " + ex.Message); }
            return File.Exists(manifest) ? ExtDir : null;
        }

        private static HttpWebRequest Request(string url, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "Lanternfall-Browser";
            r.Timeout = timeoutMs;
            r.ReadWriteTimeout = timeoutMs;
            return r;
        }

        private static void Download()
        {
            string json;
            var rq = Request("https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest", 15000);
            rq.Accept = "application/vnd.github+json";
            using (var rs = (HttpWebResponse)rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream())) json = sr.ReadToEnd();
            var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) return;
            string tag = d.ContainsKey("tag_name") ? d["tag_name"] as string : null;
            string url = null;
            var assets = d.ContainsKey("assets") ? d["assets"] as System.Collections.IEnumerable : null;
            if (assets != null)
                foreach (object a in assets)
                {
                    var ad = a as Dictionary<string, object>;
                    if (ad == null) continue;
                    string name = (ad.ContainsKey("name") ? ad["name"] as string : null) ?? "";
                    string low = name.ToLowerInvariant();
                    if (low.Contains("chromium") && low.EndsWith(".zip")) { url = ad.ContainsKey("browser_download_url") ? ad["browser_download_url"] as string : null; break; }
                }
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return;
            tag = tag ?? "unknown";
            string manifest = Path.Combine(ExtDir, "manifest.json");
            if (File.Exists(manifest) && File.Exists(TagFile) && File.ReadAllText(TagFile).Trim() == tag)
            {
                File.SetLastWriteTimeUtc(TagFile, DateTime.UtcNow);   // already current: just restart the 14 day clock
                return;
            }
            string zip = Path.GetTempFileName();
            string fresh = ExtDir + ".new";
            try
            {
                var dl = Request(url, 120000);
                using (var rs = (HttpWebResponse)dl.GetResponse())
                using (var s = rs.GetResponseStream())
                using (var f = File.Create(zip)) s.CopyTo(f);
                if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, fresh);
                string root = FindManifestDir(fresh, 0);
                if (root == null) { logger.Warn("SeleneBrowser: AdBlock extension zip has no manifest.json"); return; }
                if (Directory.Exists(ExtDir)) Directory.Delete(ExtDir, true);
                Directory.Move(root, ExtDir);
                File.WriteAllText(TagFile, tag);
                logger.Info("SeleneBrowser: uBlock Origin Lite " + tag + " downloaded");
            }
            finally
            {
                try { File.Delete(zip); } catch (Exception) { }
                try { if (Directory.Exists(fresh)) Directory.Delete(fresh, true); } catch (Exception) { }
            }
        }

        private static string FindManifestDir(string dir, int depth)
        {
            if (File.Exists(Path.Combine(dir, "manifest.json"))) return dir;
            if (depth >= 2) return null;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string r = FindManifestDir(sub, depth + 1);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>Adds the extension to the profile once per process (call on the UI thread after the view's core is ready).</summary>
        public static void InstallExtension(CoreWebView2 core)
        {
            try
            {
                lock (gate) { if (installStarted) return; installStarted = true; }
                // ad blocking was off when the browser started: the extension must not run (it lives in the profile, so remove it)
                if (!BrowserSettings.AdBlockAtStartup) { RemoveFromProfile(core); return; }
                StartExtensionFetch();
                Task<string> fetching;
                lock (gate) fetching = extTask;
                if (fetching == null) { lock (gate) installStarted = false; return; }   // backing off after a failed fetch: a later view retries
                var ui = TaskScheduler.FromCurrentSynchronizationContext();
                fetching.ContinueWith(delegate(Task<string> t)
                {
                    bool ok = false;
                    try
                    {
                        if (!t.IsFaulted && !t.IsCanceled && t.Result != null) { ok = true; AddToProfile(core, t.Result, ui); }
                    }
                    catch (Exception ex) { logger.Warn("SeleneBrowser: AdBlock extension install: " + ex.Message); }
                    if (!ok)
                        lock (gate)
                        {
                            installStarted = false;   // retry from a later view ...
                            if (extTask == fetching) extTask = null;   // ... with a new download attempt, not the finished (empty) task
                        }
                }, ui);
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: AdBlock extension: " + ex.Message); }
        }

        private static void RemoveFromProfile(CoreWebView2 core)
        {
            var ui = TaskScheduler.FromCurrentSynchronizationContext();
            core.Profile.GetBrowserExtensionsAsync().ContinueWith(delegate(Task<IReadOnlyList<CoreWebView2BrowserExtension>> t)
            {
                try
                {
                    if (t.IsFaulted || t.IsCanceled) { Log(t); return; }
                    foreach (CoreWebView2BrowserExtension x in t.Result)   // only this add-on installs extensions
                        x.RemoveAsync().ContinueWith(delegate(Task r) { if (r.IsFaulted || r.IsCanceled) Log(r); }, ui);
                    if (t.Result.Count > 0) logger.Info("SeleneBrowser: uBlock Origin Lite removed (ad blocking is off)");
                }
                catch (Exception ex) { logger.Warn("SeleneBrowser: AdBlock extension removal: " + ex.Message); }
            }, ui);
        }

        private static void AddToProfile(CoreWebView2 core, string folder, TaskScheduler ui)
        {
            string tag = File.Exists(TagFile) ? File.ReadAllText(TagFile).Trim() : "";
            string installed = File.Exists(InstalledFile) ? File.ReadAllText(InstalledFile).Trim() : "";
            CoreWebView2Profile profile = core.Profile;
            profile.GetBrowserExtensionsAsync().ContinueWith(delegate(Task<IReadOnlyList<CoreWebView2BrowserExtension>> t)
            {
                try
                {
                    if (t.IsFaulted || t.IsCanceled) { Log(t); return; }
                    // only this add-on installs extensions, so any present one is uBOL (its name may be a localized __MSG_ key)
                    CoreWebView2BrowserExtension old = t.Result.Count > 0 ? t.Result[0] : null;
                    if (old != null && installed == tag) return;   // current version already in the profile
                    Action add = delegate
                    {
                        profile.AddBrowserExtensionAsync(folder).ContinueWith(delegate(Task<CoreWebView2BrowserExtension> a)
                        {
                            if (a.IsFaulted || a.IsCanceled) { Log(a); return; }
                            try { File.WriteAllText(InstalledFile, tag); } catch (Exception) { }
                            logger.Info("SeleneBrowser: uBlock Origin Lite installed " + tag);
                        }, ui);
                    };
                    if (old == null) { add(); return; }
                    old.RemoveAsync().ContinueWith(delegate(Task r)
                    {
                        if (r.IsFaulted || r.IsCanceled) Log(r);
                        add();
                    }, ui);
                }
                catch (Exception ex) { logger.Warn("SeleneBrowser: AdBlock extension: " + ex.Message); }
            }, ui);
        }

        private static void Log(Task t)
        {
            if (t.Exception != null) logger.Warn("SeleneBrowser: AdBlock extension: " + t.Exception.GetBaseException().Message);
        }
    }
}
