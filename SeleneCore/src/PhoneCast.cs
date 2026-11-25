using System;

namespace Selene
{
    // The phone remote's way onto the TV. The Lanternfall Browser add-on serves the phone page (SeleneBrowser.PhoneRemote) and finds
    // this class by reflection; Core owns the overlays, so Core decides whether the TV may show something now and opens it.
    // Called on the UI thread. Returns null when it worked, otherwise one sentence the phone shows. New file: fork-only.
    public static class PhoneCast
    {
        // kind "video": value is a YouTube video id (starts in the YouTube screen); kind "url": an http(s) address (opens in the browser)
        public static string Receive(string kind, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value)) return "Nothing to open.";
                if (HomeNav.Quitting) return "The TV is shutting down.";
                if (HomeNav.GameRunning() || HomeNav.Launching) return "A game is running on the TV. Close it first, then send this again.";
                if (LoginScene.InSetup) return "The TV is waiting at the profile screen. Pick who is playing first.";
                if (kind == "video")
                {
                    HomeNav.Log("phone remote: video " + value);
                    HomeNav.OpenYouTubeVideo(value);
                    return null;
                }
                if (kind == "url")
                {
                    if (!NewsFeeds.WebUrl(value)) return "That link cannot be opened.";
                    HomeNav.Log("phone remote: link");
                    HomeNav.OpenInThemeBrowser(value);
                    return null;
                }
                return "The TV does not know that request.";
            }
            catch (Exception ex)
            {
                try { HomeNav.Log("phone remote: " + ex.Message); } catch { }
                return "The TV could not do that.";
            }
        }
    }
}
