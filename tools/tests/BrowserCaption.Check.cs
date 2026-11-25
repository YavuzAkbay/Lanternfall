// Regression check for YouTube caption rendering in the fullscreen player.
// YouTube auto-captions separate words with whitespace-only text nodes between </c> and <c>; Shaka's VTT parser
// drops them, and our old CSS painted the dark box on wrapper spans instead of the spans holding cue text.
// C# 5 only. Compiled against the real, built SeleneBrowser.dll - the production code is not copied here.
using System;
using System.Reflection;
using SeleneBrowser;

namespace SeleneBrowser.CaptionCheck
{
    public static class Check
    {
        static int assertions;

        static void Equal(string expected, string actual, string why)
        {
            if (expected != actual)
                throw new Exception(why + ": expected <" + expected + ">, got <" + actual + ">");
            assertions++;
        }

        static void True(bool value, string why)
        {
            if (!value) throw new Exception(why);
            assertions++;
        }

        static MethodInfo Normalize()
        {
            MethodInfo m = typeof(YouTubeApi).GetMethod("NormalizeCaptionVtt",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new Exception("YouTubeApi.NormalizeCaptionVtt is missing");
            return m;
        }

        static string Normalize(string vtt)
        {
            return (string)Normalize().Invoke(null, new object[] { vtt });
        }

        static string Html()
        {
            FieldInfo f = typeof(PlayerPage).GetField("Html",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (f == null || !f.IsLiteral) throw new Exception("PlayerPage.Html is missing");
            return (string)f.GetRawConstantValue();
        }

        public static int Main()
        {
            // The reported bug: every separator lives between tags.
            Equal("they're <c>most</c><c> well</c><c> known</c><c> for</c><c> these</c>",
                Normalize("they're <c>most</c> <c>well</c> <c>known</c> <c>for</c> <c>these</c>"),
                "separators between </c> and <c> move inside the next <c>");

            // Styled class tags, karaoke timestamps and voice tags use the same pattern.
            Equal("one</c><c.colorE5E5E5> two</c>",
                Normalize("one</c> <c.colorE5E5E5>two</c>"),
                "class tags keep their class and gain the separator");
            Equal("one</c><00:00:02.400><c> two</c>",
                Normalize("one</c> <00:00:02.400><c>two</c>"),
                "a karaoke timestamp between the tags is preserved");
            Equal("one</v><v Speaker> two</v>",
                Normalize("one</v> <v Speaker>two</v>"),
                "voice tags are normalized too");
            Equal("one</c><c>&nbsp;two</c>",
                Normalize("one</c>&nbsp;<c>two</c>"),
                "an nbsp separator survives as part of the word's text node");

            // Captions that already carry the separator inside <c> don't get a second one; plain VTT is untouched.
            Equal("one<00:00:01.000><c> two</c><c> three</c>",
                Normalize("one<00:00:01.000><c> two</c> <c> three</c>"),
                "a redundant separator between tags is dropped, not doubled");
            string plain = "WEBVTT\nKind: captions\n\n00:00:01.000 --> 00:00:02.000\nhello world\n";
            Equal(plain, Normalize(plain), "plain VTT passes through unchanged");
            Equal("", Normalize(""), "empty input passes through");

            // The player page must not undo Shaka's whitespace preservation or paint wrapper spans.
            string html = Html();
            True(html.Contains("white-space:pre-wrap!important"), "cue spans keep Shaka's pre-wrap whitespace");
            True(html.Contains(".shaka-text-wrapper{background:rgba(0,0,0,.72)"),
                "word-level cues get one box per line, not one per word");
            True(html.Contains(".shaka-text-wrapper span[style*=background-color]{background:transparent"),
                "words inside a wrapper don't paint their own boxes");
            True(html.Contains("float:none!important"), "nested cues can't float out of the line wrapper");
            True(html.Contains("span[style*=background-color]"), "plain-text cues still get a dark box");
            True(!html.Contains("white-space:pre-line"), "pre-line no longer collapses word separators");
            True(!html.Contains("span span{"), "nested spans are no longer blanked out");

            Console.WriteLine("browser caption checks passed (" + assertions + " assertions)");
            return 0;
        }
    }
}
