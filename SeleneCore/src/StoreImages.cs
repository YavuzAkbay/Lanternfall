using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Selene
{
    // Pictures for the Store, fetched by Selene itself: a few downloads at a time (WPF's own web pictures, a hundred at
    // once, ran the process out of memory and took Playnite down), kept on disk for next time and offline, decoded small
    // and frozen, handed back on the UI thread. Anything that goes wrong just means no picture.
    public static class StoreImages
    {
        private const string Agent = ForkBrand.Agent;
        private const int MemoryMax = 120;
        private const long MemoryBytesMax = 120L * 1024 * 1024;   // decoded size (a 32-bit process: 120 big pictures at 4K would be ~800 MB)
        private const int DecodeMax = 1280;                       // real pixels wide, however big the layout asks (the art is darkened and blurred anyway)
        private static long memoryBytes;
        private static readonly Dictionary<string, ImageSource> memory = new Dictionary<string, ImageSource>();
        private static readonly LinkedList<string> order = new LinkedList<string>();
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(4);
        private static readonly object sync = new object();

        private static string Folder { get { return Path.Combine(SelenePaths.DataFolder, "store-pictures"); } }

        private static string FileFor(string url)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(url));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return Path.Combine(Folder, sb + ".img");
            }
        }

        // `done(picture)` on the UI thread (at once when it's already in memory); null when it can't be had
        public static void Get(string url, int width, Action<ImageSource> done)
        {
            if (string.IsNullOrEmpty(url)) { done(null); return; }
            var key = url + "|" + width;
            ImageSource hit;
            lock (sync)
            {
                if (memory.TryGetValue(key, out hit)) { order.Remove(key); order.AddFirst(key); }
            }
            if (hit != null) { done(hit); return; }
            var ui = System.Windows.Application.Current.Dispatcher;
            // waits for a free slot without parking a pool thread: a Store page asks for hundreds at once
            gate.WaitAsync().ContinueWith(t =>
            {
                ImageSource img = null;
                try { img = Decode(Bytes(url), width); }
                catch (Exception ex) { HomeNav.Log("store picture: " + ex.Message + " (" + url + ")"); }
                finally { gate.Release(); }
                if (img != null)
                    lock (sync)
                    {
                        ImageSource old;
                        if (memory.TryGetValue(key, out old)) { memoryBytes -= SizeOf(old); order.Remove(key); }
                        memory[key] = img;
                        memoryBytes += SizeOf(img);
                        order.AddFirst(key);
                        while (order.Count > 1 && (order.Count > MemoryMax || memoryBytes > MemoryBytesMax))
                        {
                            ImageSource drop;
                            if (memory.TryGetValue(order.Last.Value, out drop)) memoryBytes -= SizeOf(drop);
                            memory.Remove(order.Last.Value); order.RemoveLast();
                        }
                    }
                ui.BeginInvoke(new Action(() => done(img)));
            }, TaskScheduler.Default);
        }

        private static long SizeOf(ImageSource s)
        {
            var b = s as BitmapSource;
            return b == null ? 0 : (long)b.PixelWidth * b.PixelHeight * 4;
        }

        // JPEG, PNG, GIF, BMP or WebP by their first bytes: a captive portal's or an error page's HTML must not be kept as the picture for good
        private static bool IsImage(byte[] d)
        {
            if (d == null || d.Length < 12) return false;
            if (d[0] == 0xFF && d[1] == 0xD8) return true;                                              // JPEG
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;              // PNG
            if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return true;                              // GIF
            if (d[0] == 0x42 && d[1] == 0x4D) return true;                                              // BMP
            if (d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return true;   // WebP
            return false;
        }

        private static byte[] Bytes(string url)
        {
            var file = FileFor(url);
            try
            {
                if (File.Exists(file))
                {
                    var have = File.ReadAllBytes(file);
                    if (IsImage(have)) return have;
                    File.Delete(file);   // saved by an older version without the check: fetched again
                }
            }
            catch { }
            byte[] data;
            using (var web = new TimedWebClient())
            {
                web.Headers[HttpRequestHeader.UserAgent] = Agent;
                data = web.DownloadData(url);
            }
            if (!IsImage(data)) throw new InvalidDataException("not a picture");
            try
            {
                Directory.CreateDirectory(Folder);
                var part = file + ".part";
                File.WriteAllBytes(part, data);
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);
            }
            catch { }
            return data;
        }

        private static ImageSource Decode(byte[] data, int width)
        {
            if (data == null || data.Length == 0) return null;
            using (var ms = new MemoryStream(data))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = Math.Min(DecodeMax, PixelSize.Width(width));   // (layout units -> real pixels)
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }
    }
}
