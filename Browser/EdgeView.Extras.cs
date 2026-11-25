// EdgeView.Extras.cs - what the console browser adds on top of a plain page: downloads (asked on screen first), a phone user agent
// for single sites, and a small picture of the page for the tab switcher. Kept apart from EdgeView.cs so that file stays the
// engine's basics. All WebView2 type references stay in the EdgeView files. C# 5 only.
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SeleneBrowser
{
    public partial class EdgeView
    {
        /// <summary>A page offered a file. The host shows the question and answers through the request. Without a handler downloads are refused.</summary>
        internal event Action<DownloadRequest> DownloadRequested;
        /// <summary>A download that was accepted finished (or failed / was cancelled): its item's State says which.</summary>
        internal event Action<DownloadItem> DownloadFinished;

        /// <summary>The page scrolled by itself (the left stick): how far, in CSS pixels (dx, dy; + = right / down). Not raised when there was no room.</summary>
        internal event Action<double, double> ScrollMoved;

        private void RaiseScrollMoved(string message)
        {
            var h = ScrollMoved;
            if (h == null) return;
            string[] parts = message.Split(':');
            double dx, dy;
            if (parts.Length != 3 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dx)
                || !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dy)) return;
            h(dx, dy);
        }

        /// <summary>Set by the host: should this address be shown with the phone user agent?</summary>
        internal Func<string, bool> MobileFor;

        private string defaultUserAgent = "";
        private bool mobile;

        /// <summary>The page is being shown as on a phone.</summary>
        internal bool IsMobile { get { return mobile; } }

        private void ExtrasInit()
        {
            try { defaultUserAgent = core.Settings.UserAgent ?? ""; }
            catch (Exception ex) { Fail("user agent", ex); }
        }

        // ----- downloads -----

        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs a)
        {
            var ask = DownloadRequested;
            if (isPlayer || ask == null) { a.Cancel = true; Raise(PageNotice, "Downloads are not supported in this browser"); return; }
            string path = a.ResultFilePath;
            CoreWebView2DownloadOperation op = a.DownloadOperation;
            a.Handled = true;   // no Edge download bubble: the question is on the screen, in the theme
            CoreWebView2Deferral deferral = a.GetDeferral();
            long size = -1;
            try { if (op.TotalBytesToReceive.HasValue) size = (long)op.TotalBytesToReceive.Value; } catch (Exception) { }
            bool decided = false;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            Action<bool, DownloadItem> decide = null;
            decide = delegate(bool ok, DownloadItem item)
            {
                if (decided) return;
                decided = true;
                timeout.Stop();
                try
                {
                    a.Cancel = !ok || item == null;
                    if (!a.Cancel) Track(op, item);
                    deferral.Complete();
                }
                catch (Exception ex) { Fail("download answer", ex); }
            };
            // nobody answered in a minute (a menu is up, the browser was hidden): refuse, so the page is not left waiting
            timeout.Tick += delegate { decide(false, null); };
            timeout.Start();
            try { ask(new DownloadRequest { Name = Path.GetFileName(path), Path = path, Size = size, Decide = decide }); }
            catch (Exception ex) { Fail("download ask", ex); decide(false, null); }
        }

        private void Track(CoreWebView2DownloadOperation op, DownloadItem item)
        {
            item.Cancel = delegate { try { op.Cancel(); } catch (Exception) { } };
            op.BytesReceivedChanged += delegate
            {
                try
                {
                    item.Received = op.BytesReceived;
                    if (op.TotalBytesToReceive.HasValue) item.Size = (long)op.TotalBytesToReceive.Value;
                }
                catch (Exception) { }
            };
            op.StateChanged += delegate
            {
                try
                {
                    if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        item.State = "done";
                        item.Received = op.BytesReceived;
                        if (item.Size < 0) item.Size = op.BytesReceived;
                    }
                    else if (op.State == CoreWebView2DownloadState.Interrupted)
                        item.State = op.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled ? "cancelled" : "failed";
                    else return;
                    var h = DownloadFinished;
                    if (h != null) h(item);
                }
                catch (Exception ex) { Fail("download state", ex); }
            };
        }

        // ----- phone user agent -----

        private static readonly Regex ChromeVersion = new Regex(@"Chrome/(\d+)\.([\d.]+)", RegexOptions.Compiled);

        /// <summary>The user agent of an Android phone with the same Chromium as this runtime.</summary>
        internal static string MobileUserAgent(string desktopUserAgent)
        {
            Match m = ChromeVersion.Match(desktopUserAgent ?? "");
            string v = m.Success ? m.Value.Substring("Chrome/".Length) : "120.0.0.0";
            return "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + v + " Mobile Safari/537.36";
        }

        /// <summary>Shows pages as on a phone (or as on a PC again). The caller reloads the page; done runs once the engine took it.</summary>
        internal void SetMobile(bool on, Action done)
        {
            if (core == null || disposed) { if (done != null) done(); return; }
            mobile = on;
            string json;
            if (on)
            {
                string ua = MobileUserAgent(defaultUserAgent);
                Match m = ChromeVersion.Match(ua);
                string major = m.Success ? m.Groups[1].Value : "120", full = m.Success ? m.Value.Substring("Chrome/".Length) : "120.0.0.0";
                var ser = new JavaScriptSerializer();
                json = "{\"userAgent\":" + ser.Serialize(ua) + ",\"platform\":\"Linux armv81\",\"userAgentMetadata\":{\"brands\":[{\"brand\":\"Chromium\",\"version\":\""
                    + major + "\"},{\"brand\":\"Not.A/Brand\",\"version\":\"99\"}],\"fullVersion\":\"" + full + "\",\"platform\":\"Android\",\"platformVersion\":\"13.0.0\","
                    + "\"architecture\":\"\",\"model\":\"Pixel 7\",\"mobile\":true}}";
            }
            else json = "{\"userAgent\":\"\"}";
            try
            {
                core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", json).ContinueWith(delegate(Task<string> t)
                {
                    if (t.IsFaulted && t.Exception != null) Fail("user agent override", t.Exception.GetBaseException());
                    Dispatcher.BeginInvoke(new Action(delegate { if (done != null) done(); }));
                });
            }
            catch (Exception ex) { Fail("user agent override", ex); if (done != null) done(); }
        }

        // a site remembered as a phone site (or no longer one) changes the user agent before the page is asked for: the request is
        // cancelled, the agent switched, and the same address opened again (redirects keep whatever the first request had)
        private bool MobileSwitchNeeded(CoreWebView2NavigationStartingEventArgs a)
        {
            var decide = MobileFor;
            if (decide == null || isPlayer || a.IsRedirected) return false;
            string uri = a.Uri;
            if (string.IsNullOrEmpty(uri) || !(uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return false;
            bool want;
            try { want = decide(uri); } catch (Exception) { return false; }
            if (want == mobile) return false;
            a.Cancel = true;
            SetMobile(want, delegate { if (core != null && !disposed) { try { core.Navigate(uri); } catch (Exception ex) { Fail("Navigate", ex); } } });
            return true;
        }

        // ----- a picture of the page (the tab switcher) -----

        /// <summary>A PNG of what the page shows now (null when it cannot be had). done runs on the UI thread.</summary>
        internal void CapturePreview(Action<byte[]> done)
        {
            if (core == null || disposed) { done(null); return; }
            var ms = new MemoryStream();
            try
            {
                core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms).ContinueWith(delegate(Task t)
                {
                    byte[] bytes = null;
                    if (!t.IsFaulted && !t.IsCanceled) { try { bytes = ms.ToArray(); } catch (Exception) { } }
                    Dispatcher.BeginInvoke(new Action(delegate { done(bytes); }));
                });
            }
            catch (Exception ex) { Fail("capture", ex); done(null); }
        }
    }
}
