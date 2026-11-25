// BrowserTab.cs - one tab of the console browser: its own page engine plus the state that belongs to it.
using System.Windows.Media;

namespace SeleneBrowser
{
    internal sealed class BrowserTab
    {
        public IPageView View;
        public string Key;              // "news" = the tab Lanternfall's news links reuse
        public string Title = "New tab";
        public string Url = "";
        public double Zoom;
        public string ResumeUrl;
        public bool ViewDisposed;
        public long LastUsed;
        public BrowserTab Opener;       // the tab whose pop-up this is: B on its first page closes it and returns there
        public ImageSource Thumb;       // a small picture of the page, for the tab switcher
        public string ZoomSite;         // the site this tab's zoom belongs to (per-site zoom memory)
        public bool Reader;             // reader mode is on in this page
        public bool FromTheme;          // opened by a link from Selene (News, Store...): B on its first page returns to that screen
    }
}
