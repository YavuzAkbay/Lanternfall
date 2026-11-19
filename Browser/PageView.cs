// PageView.cs - the engine-neutral surface the browser uses (CefView = CEF, EdgeView = Edge WebView2).
using System;
using System.Runtime.CompilerServices;
using System.Windows;
using Playnite.SDK;

namespace SeleneBrowser
{
    public interface IPageView
    {
        /// <summary>The WPF element to put in the layout.</summary>
        UIElement Element { get; }

        string Address { get; }
        string Title { get; }
        bool IsReady { get; }
        bool CanGoBack { get; }
        bool IsFullScreen { get; }

        void Navigate(string url);
        void GoBack();
        void GoForward();
        void Reload();
        void ExecuteScript(string js);
        void EvaluateAsync(string js, Action<string> onResult);

        // trusted input; coordinates are DIPs relative to Element
        void Click(double x, double y);
        void MoveMouse(double x, double y);
        /// <summary>Positive deltaY scrolls up.</summary>
        void Wheel(double x, double y, int deltaY);
        /// <summary>Positive deltaY scrolls up, positive deltaX scrolls left.</summary>
        void Wheel(double x, double y, int deltaY, int deltaX);
        void TypeText(string text);
        void PressKey(int virtualKey);

        /// <summary>CEF zoom level; page scale = 1.2 ^ level.</summary>
        void SetZoomLevel(double level);
        void ClearCookies();

        event Action<string> LoadEnd;
        event Action<string> AddressChanged;
        event Action<string> TitleChanged;
        event EventHandler FullScreenChanged;
    }

    /// <summary>Creates the page engine: Edge WebView2 (H.264/AAC capable) when available, CEF otherwise. Shared by the browser and the YouTube player.</summary>
    internal static class PageViews
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static IPageView Create()
        {
            try
            {
                IPageView edge = TryCreateEdge();
                if (edge != null) return edge;
            }
            catch (Exception ex) { logger.Warn("SeleneBrowser: WebView2 probe failed: " + ex.Message); }
            logger.Info("SeleneBrowser: browser engine = CEF (WebView2 not available)");
            return new CefView();
        }

        // WebView2 types are only touched from NoInlining methods so a missing DLL fails here, not in the caller
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IPageView TryCreateEdge()
        {
            if (!EdgeView.IsAvailable()) return null;
            EdgeView v = new EdgeView();
            logger.Info("SeleneBrowser: browser engine = Edge WebView2 " + EdgeView.RuntimeVersion());
            return v;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsEdge(IPageView v) { return v is EdgeView; }

        /// <summary>Calls onFailed (once) when an Edge view could not start. No effect on other engines.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void OnEdgeInitFailed(IPageView v, Action onFailed)
        {
            EdgeView e = v as EdgeView;
            if (e != null) e.InitFailed += onFailed;
        }
    }
}
