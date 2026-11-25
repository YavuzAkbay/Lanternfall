// Regression check for browser tab/pad handling.
// BrowserControl.TabStep(string) is a private static helper shared by HandlePage and HandleBar:
// LB (LeftShoulder) maps to "prev", RB (RightShoulder) to "next", every other pad button to null.
// C# 5 only. The type is loaded by name so csc does not need the WPF / Playnite.SDK reference chain.
using System;
using System.IO;
using System.Reflection;

namespace SeleneBrowser.TabCheck
{
    public static class Check
    {
        static int assertions;
        static MethodInfo step;

        static void Equal(string expected, string actual, string why)
        {
            if (expected != actual)
                throw new Exception(why + ": expected <" + (expected ?? "null") + ">, got <" + (actual ?? "null") + ">");
            assertions++;
        }

        static MethodInfo StepMethod()
        {
            string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SeleneBrowser.dll");
            Assembly assembly = Assembly.LoadFrom(dll);
            Type control = assembly.GetType("SeleneBrowser.BrowserControl", false);
            if (control == null) throw new Exception("SeleneBrowser.BrowserControl type is missing");
            MethodInfo m = control.GetMethod("TabStep",
                BindingFlags.Static | BindingFlags.NonPublic,
                null, new Type[] { typeof(string) }, null);
            if (m == null) throw new Exception("BrowserControl.TabStep(string) is missing");
            if (m.ReturnType != typeof(string)) throw new Exception("BrowserControl.TabStep must return string");
            return m;
        }

        static string Step(string button)
        {
            return (string)step.Invoke(null, new object[] { button });
        }

        public static int Main()
        {
            step = StepMethod();

            Equal("prev", Step("LeftShoulder"), "LB selects the previous tab");
            Equal("next", Step("RightShoulder"), "RB selects the next tab");

            string[] others = { "DPadLeft", "DPadRight", "DPadUp", "DPadDown",
                                "TriggerLeft", "TriggerRight", "A", "B", "X", "Y", "Start", "Back" };
            foreach (string button in others)
                Equal(null, Step(button), button + " must not change tabs");

            Console.WriteLine("[PASS] browser tab pad mapping: " + assertions + " assertions");
            return 0;
        }
    }
}
