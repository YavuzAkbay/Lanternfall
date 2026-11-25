// Offline checks for the browser's page tools that need no engine: download names and sizes (Browser\Downloads.cs) and the scripts
// for reader mode and find in page (Browser\ReaderScript.cs). The scripts themselves ran in a real Edge view during development.
// No WPF, WebView2 or Playnite needed (see browser-tools.ps1). C# 5 only.
using System;
using SeleneBrowser;

internal static class BrowserToolsCheck
{
    private static int failures;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static int Main()
    {
        // ---- programs
        Check(DownloadNames.IsProgram("setup.exe") && DownloadNames.IsProgram("SETUP.EXE") && DownloadNames.IsProgram("a.b.msi") && DownloadNames.IsProgram("run.ps1") && DownloadNames.IsProgram("x.lnk"), "programs are recognised");
        Check(!DownloadNames.IsProgram("photo.png") && !DownloadNames.IsProgram("save.zip") && !DownloadNames.IsProgram("notes") && !DownloadNames.IsProgram("") && !DownloadNames.IsProgram(null), "other files are not programs");
        Check(!DownloadNames.IsProgram("exe") && !DownloadNames.IsProgram("a.exe.txt"), "only the last extension counts");

        // ---- sizes
        Check(DownloadNames.Size(-1) == "" && DownloadNames.Size(0) == "0 B" && DownloadNames.Size(512) == "512 B", "small sizes");
        Check(DownloadNames.Size(1536) == "1.5 KB" && DownloadNames.Size(20480) == "20 KB", "kilobytes");
        Check(DownloadNames.Size(5L * 1024 * 1024) == "5 MB" && DownloadNames.Size(48234496) == "46 MB", "megabytes");
        Check(DownloadNames.Size(3L * 1024 * 1024 * 1024) == "3 GB", "gigabytes");

        // ---- status words
        var d = new DownloadItem { Name = "a.zip", Size = 1000, Received = 250 };
        Check(DownloadNames.Status(d).StartsWith("25%"), "a running download shows its percentage: " + DownloadNames.Status(d));
        d.Received = 1000;
        Check(DownloadNames.Status(d).StartsWith("99%"), "a download never shows 100% before it is done");
        d.State = "done";
        Check(DownloadNames.Status(d).StartsWith("Saved to Downloads"), "done says saved");
        d.State = "failed";
        Check(DownloadNames.Status(d) == "Did not finish", "failed");
        d.State = "cancelled";
        Check(DownloadNames.Status(d) == "Cancelled", "cancelled");
        var unknown = new DownloadItem { Name = "b.bin" };
        Check(DownloadNames.Status(unknown) == "Starting...", "before the first bytes");
        unknown.Received = 4096;
        Check(DownloadNames.Status(unknown) == "4 KB so far", "unknown total: bytes so far");

        // ---- names shown on screen
        Check(DownloadNames.Shown("C:\\Users\\x\\Downloads\\game.zip") == "game.zip", "a path shows only its file name");
        Check(DownloadNames.Shown("a\u0007b.txt") == "ab.txt", "control characters are dropped");
        Check(DownloadNames.Shown("") == "file" && DownloadNames.Shown(null) == "file", "an empty name");
        string longName = new string('x', 80) + ".iso";
        string shortened = DownloadNames.Shown(longName);
        Check(shortened.Length < 48 && shortened.EndsWith(".iso") && shortened.Contains("\u2026"), "a long name is cut in the middle");

        // ---- reader mode script
        string js = ReaderScript.Build("#101820", "#f0f0f0", "#e0a458");
        Check(!js.Contains("%BG%") && !js.Contains("%INK%") && !js.Contains("%ACCENT%"), "the theme colors replace every placeholder");
        Check(js.Contains("#101820") && js.Contains("#f0f0f0") && js.Contains("#e0a458"), "the colors are in the script");
        string hostile = ReaderScript.Build("red;}</style><script>alert(1)</script>", "#zzzzzz", null);
        Check(!hostile.Contains("alert(1)") && hostile.Contains("#0b0e14") && hostile.Contains("#f2f4f8") && hostile.Contains("#7aa2ff"), "a colour that is not #rrggbb falls back and cannot inject");
        Check(js.IndexOf('"') < 0, "the script has no double quotes (it sits in a C# string)");

        // ---- find script
        string f = ReaderScript.FindScript("hello", false);
        Check(f.Contains("window.find(q,false,back") && f.EndsWith("(\"hello\",false)"), "find: query and direction");
        Check(ReaderScript.FindScript("x", true).EndsWith("(\"x\",true)"), "find backwards");
        string tricky = ReaderScript.FindScript("it's \"q\" \\ </script>", false);
        Check(tricky.Contains("\\\"q\\\"") && tricky.Contains("\\\\"), "quotes and backslashes in the query are escaped");


        // ---- the page scroller (left stick, triggers, edge push)
        Check(PageScroller.Curve(0) == 0 && PageScroller.Curve(1) == 1 && PageScroller.Curve(-1) == -1 && PageScroller.Curve(0.5) < 0.5 && PageScroller.Curve(-0.5) == -PageScroller.Curve(0.5), "stick curve: gentle in the middle, full at the end, symmetric");
        Check(PageScroller.Trigger(0.05) == 0 && PageScroller.Trigger(1) == 1 && PageScroller.Trigger(0.5) > 0 && PageScroller.Trigger(0.5) < 0.5, "trigger curve: small dead zone, then gentle");
        double tx, ty;
        PageScroller.Target(0, -1, 0, 0, out tx, out ty);
        Check(tx == 0 && Math.Abs(ty - PageScroller.MaxSpeed) < 1e-9, "stick pushed down scrolls down at full speed");
        PageScroller.Target(0, 1, 0, 0, out tx, out ty);
        Check(ty < 0, "stick pushed up scrolls up");
        PageScroller.Target(1, 0, 0, 0, out tx, out ty);
        Check(tx > 0 && ty == 0, "stick pushed right scrolls right");
        PageScroller.Target(0.2, -0.9, 0, 0, out tx, out ty);
        Check(tx == 0 && ty > 0, "a stick pushed mostly down does not drift sideways");
        PageScroller.Target(0.7, -0.7, 0, 0, out tx, out ty);
        Check(tx > 0 && ty > 0, "a real diagonal scrolls both ways");
        PageScroller.Target(0, 0, 0, 1, out tx, out ty);
        Check(ty > 0 && tx == 0, "the right trigger scrolls down");
        PageScroller.Target(0, 0, 1, 0, out tx, out ty);
        Check(ty < 0, "the left trigger scrolls up");
        PageScroller.Target(0, -1, 0, 1, out tx, out ty);
        Check(ty <= PageScroller.MaxSpeed + 1e-9, "stick and trigger together never exceed the top speed");

        var sent = new System.Collections.Generic.List<string>();
        var ps = new PageScroller(delegate(string call) { sent.Add(call); });
        long clock = 1000;
        for (int i = 0; i < 20; i++) { ps.Update(0.016, 0, 0, "", clock); clock += 16; }
        Check(sent.Count == 0 && !ps.Active, "an idle pad sends nothing");
        for (int i = 0; i < 60; i++) { ps.Update(0.016, 0, 1.0, "", clock); clock += 16; }
        Check(ps.Active && ps.SpeedY > 0.9 && ps.SpeedY <= 1.0 + 1e-9, "the speed follows the stick up to the asked speed (" + ps.SpeedY.ToString("0.00") + ")");
        Check(sent.Count > 3 && sent.Count < 40, "while scrolling a few messages go out, not one per tick (" + sent.Count + " in about a second)");
        Check(sent[0].Contains("__nomadNav.scroll(0,") && sent[sent.Count - 1].Contains("scroll(0,1)"), "the message carries the speed: " + sent[sent.Count - 1]);
        int steady = sent.Count;
        for (int i = 0; i < 40; i++) { ps.Update(0.016, 0, 1.0, "", clock); clock += 16; }
        int beats = sent.Count - steady;
        Check(beats >= 3 && beats <= 6, "a steady speed is only repeated now and then as a heartbeat (" + beats + " in 0.64 s)");
        int before = sent.Count;
        for (int i = 0; i < 80; i++) { ps.Update(0.016, 0, 0, "", clock); clock += 16; }
        Check(!ps.Active && ps.SpeedY == 0 && sent[sent.Count - 1].Contains("scroll(0,0)"), "letting go glides out and then sends one stop");
        int stops = 0; for (int i = before; i < sent.Count; i++) if (sent[i].Contains("scroll(0,0)")) stops++;
        Check(stops == 1, "exactly one stop (" + stops + ")");
        int after = sent.Count;
        for (int i = 0; i < 30; i++) { ps.Update(0.016, 0, 0, "", clock); clock += 16; }
        Check(sent.Count == after, "nothing more is sent once stopped");
        sent.Clear();
        for (int i = 0; i < 20; i++) { ps.Update(0.016, 0.5, 0, "412.5,300", clock); clock += 16; }
        Check(sent.Count > 0 && sent[sent.Count - 1].EndsWith(",412.5,300)"), "the point to scroll at is sent along: " + (sent.Count > 0 ? sent[sent.Count - 1] : ""));
        ps.Stop();
        Check(!ps.Active && sent[sent.Count - 1].Contains("scroll(0,0)"), "Stop sends the stop at once");
        int stopCount = sent.Count;
        ps.Stop();
        Check(sent.Count == stopCount, "stopping twice sends once");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
