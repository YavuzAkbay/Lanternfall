using System;
using System.Linq;
using System.Reflection;
using System.Windows.Input;

namespace Selene
{
    // A controller press made the way Playnite makes one for a real pad (Playnite.Input.GameControllerManager.ProcessButtonState):
    // first its controller event, built as Playnite builds it (no key in it; its constructor picks the KeyDown / KeyUp stage, one
    // ProcessInput), then, for a navigation button, the keyboard key Playnite simulates next to it (the theme drops that key as the
    // press's copy). Used by the phone remote (PhoneCast) and by PadTapGuard's replays. UI thread only. Fork-only file.
    //
    // Why not simpler: Ui.SendPad puts the arrow key into the controller event and sends the Preview stage too, so the home row stepped
    // twice per press; calling Playnite's own ProcessButtonState from the UI thread posts the controller event, which then arrived after
    // the simulated key, and the profile screen took both. The manager lives on PlayniteApplication.Current.GameController, which is
    // not WPF's Application.Current (Playnite.FullscreenApp.App has no such property).
    internal static class PlaynitePad
    {
        private static object manager;
        private static MethodInfo simulateKey, mapKey, notNavigation;
        private static Type padArgs, padInput, padState;
        private static bool looked, broken;

        /// <summary>True when Playnite's controller handling was found (looks once).</summary>
        public static bool Available { get { Look(); return !broken && padArgs != null && simulateKey != null; } }

        /// <summary>Presses (down) or lets go of a button. False when it could not (then nothing was sent).</summary>
        public static bool Press(string button, bool down)
        {
            if (!Available) return false;
            try
            {
                Playnite.SDK.Events.ControllerInput input;
                if (!Enum.TryParse(button, out input)) return false;
                var names = Enum.GetNames(padState);
                var state = Enum.Parse(padState, down ? (names.FirstOrDefault(n => n.IndexOf("Press", StringComparison.OrdinalIgnoreCase) >= 0) ?? names[0])
                                                      : (names.FirstOrDefault(n => n.IndexOf("Releas", StringComparison.OrdinalIgnoreCase) >= 0) ?? names[names.Length - 1]));
                var args = (InputEventArgs)Activator.CreateInstance(padArgs, Key.None, state, Enum.Parse(padInput, button));
                InputManager.Current.ProcessInput(args);
                bool navigation = notNavigation == null || !(bool)notNavigation.Invoke(manager, new object[] { input });
                if (navigation && mapKey != null)
                {
                    var key = mapKey.Invoke(manager, new object[] { input });
                    if (key is uint && (uint)key != 0) simulateKey.Invoke(manager, new object[] { key, down });
                }
                return true;
            }
            catch (Exception ex)
            {
                HomeNav.Log("pad: Playnite's controller handling failed, not used again: " + (ex.InnerException ?? ex).Message);
                broken = true;
                return false;
            }
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            try
            {
                object playnite = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.StartsWith("Playnite")) continue;
                    var pt = asm.GetType("Playnite.PlayniteApplication", false);
                    var cur = pt != null ? pt.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                    if (cur != null) { playnite = cur.GetValue(null, null); break; }
                }
                var prop = playnite != null ? playnite.GetType().GetProperty("GameController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                manager = prop != null ? prop.GetValue(playnite, null) : null;
                if (manager != null)
                {
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var methods = manager.GetType().GetMethods(all);
                    simulateKey = methods.FirstOrDefault(m => m.Name == "SimulateKeyInput" && m.GetParameters().Length == 2);
                    mapKey = methods.FirstOrDefault(m => m.Name == "MapPadToKeyboard" && m.GetParameters().Length == 1);
                    notNavigation = methods.FirstOrDefault(m => m.Name == "IsButtonNotNavigation" && m.GetParameters().Length == 1);
                }
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.StartsWith("Playnite")) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
                    if (padArgs == null) padArgs = types.FirstOrDefault(x => x.Name == "GameControllerInputEventArgs");
                    if (padInput == null) padInput = types.FirstOrDefault(x => x.IsEnum && x.Name == "ControllerInput");
                    if (padState == null) padState = types.FirstOrDefault(x => x.IsEnum && x.Name == "ControllerInputState");
                }
                if (padInput == null || padState == null) padArgs = null;
            }
            catch (Exception ex) { HomeNav.Log("pad: looking for Playnite's controller handling: " + ex.Message); broken = true; }
            HomeNav.Log("pad: Playnite's controller handling " + (!broken && padArgs != null && simulateKey != null ? "found" : "not found"));
        }
    }
}
