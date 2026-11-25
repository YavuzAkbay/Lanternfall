// Downloads.cs - the browser's file downloads: what a page offers (DownloadRequest, asked on screen first), the session's list of
// them (DownloadItem) and the small pure helpers (is it a program, how big, a safe name). No WPF or WebView2 here, so
// tools\tests\BrowserTools.Check.cs can compile it alone. C# 5 only.
using System;
using System.Globalization;
using System.IO;

namespace SeleneBrowser
{
    /// <summary>A page started a download and WebView2 is waiting for the answer (see EdgeView.DownloadRequested).</summary>
    internal sealed class DownloadRequest
    {
        public string Name;          // the file name the page suggested
        public string Path;          // where it will be saved
        public long Size;            // bytes, -1 when the page did not say
        /// <summary>true = save it, false = refuse it. Call once, on the UI thread.</summary>
        public Action<bool, DownloadItem> Decide;
    }

    /// <summary>One download of this session, as the Downloads list shows it.</summary>
    internal sealed class DownloadItem
    {
        public string Name;
        public string Path;
        public long Received;
        public long Size = -1;
        public string State = "downloading";   // downloading | done | failed | cancelled
        public Action Cancel;                  // stops it while it runs
    }

    internal static class DownloadNames
    {
        private static readonly string[] Programs =
        {
            ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".jar", ".lnk",
            ".reg", ".msix", ".appx", ".msp", ".hta", ".cpl", ".dll"
        };

        /// <summary>A file that runs code when opened: saved like any other, but the notice says Windows checks it first.</summary>
        public static bool IsProgram(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string ext;
            try { ext = System.IO.Path.GetExtension(name); } catch (ArgumentException) { return false; }
            if (string.IsNullOrEmpty(ext)) return false;
            foreach (string p in Programs) if (string.Equals(ext, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>"3.4 MB" style size; "" when unknown.</summary>
        public static string Size(long bytes)
        {
            if (bytes < 0) return "";
            var inv = CultureInfo.InvariantCulture;
            if (bytes < 1024) return bytes.ToString(inv) + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString(kb < 10 ? "0.#" : "0", inv) + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString(mb < 10 ? "0.#" : "0", inv) + " MB";
            return (mb / 1024.0).ToString("0.#", inv) + " GB";
        }

        /// <summary>"43%  (12 MB of 28 MB)" while it runs, the size when done, a word otherwise.</summary>
        public static string Status(DownloadItem d)
        {
            if (d == null) return "";
            switch (d.State)
            {
                case "done": return "Saved to Downloads" + (d.Size >= 0 ? "  ·  " + Size(d.Size) : "");
                case "failed": return "Did not finish";
                case "cancelled": return "Cancelled";
            }
            if (d.Size > 0)
            {
                int pct = (int)Math.Max(0, Math.Min(99, d.Received * 100 / d.Size));
                return pct + "%  ·  " + Size(d.Received) + " of " + Size(d.Size);
            }
            return d.Received > 0 ? Size(d.Received) + " so far" : "Starting...";
        }

        /// <summary>A name that is safe to show in a message (no path, no control characters, cut when long).</summary>
        public static string Shown(string pathOrName)
        {
            string n = pathOrName ?? "";
            try { n = System.IO.Path.GetFileName(n); } catch (ArgumentException) { }
            var sb = new System.Text.StringBuilder();
            foreach (char c in n) if (!char.IsControl(c)) sb.Append(c);
            n = sb.ToString();
            if (n.Length == 0) return "file";
            return n.Length > 48 ? n.Substring(0, 30) + "…" + n.Substring(n.Length - 14) : n;
        }
    }
}
