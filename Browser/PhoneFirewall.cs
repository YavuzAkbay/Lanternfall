// PhoneFirewall.cs - Windows Firewall and the phone keyboard. A PC that is on a "Public" network (Windows' default for home Wi-Fi
// that was never marked Private) drops every inbound connection to a program with no allow rule, and the "Allow access?" box
// Windows shows is on the desktop, out of reach of a pad. So: ask the firewall (COM, no admin needed) whether Playnite's
// fullscreen program can be reached, and offer a one-time setup that adds ONE inbound rule, TCP, local network only, for this
// program, through Windows' own UAC box (answered once with a mouse or keyboard). Nothing is changed unless the user presses it.
// C# 5 only.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SeleneBrowser
{
    internal enum FirewallState { Unknown, Open, Missing, Blocked }

    internal static class PhoneFirewall
    {
        public const string RuleName = "Lanternfall phone keyboard";
        private const int DirIn = 1, ActionAllow = 1, ActionBlock = 0;

        private static volatile FirewallState last = FirewallState.Unknown;
        private static int checking;

        /// <summary>The last answer of Check (Unknown until the first one finishes).</summary>
        public static FirewallState Last { get { return last; } }

        public static string ExePath
        {
            get { try { return Process.GetCurrentProcess().MainModule.FileName; } catch { return null; } }
        }

        /// <summary>Asks Windows Firewall; blocks for a moment, so call it on a worker thread.</summary>
        public static FirewallState Check()
        {
            FirewallState result = FirewallState.Unknown;
            try
            {
                string exe = ExePath;
                Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (exe != null && type != null)
                {
                    dynamic policy = Activator.CreateInstance(type);
                    int profiles = (int)policy.CurrentProfileTypes;
                    bool allow = false, block = false;
                    foreach (dynamic rule in policy.Rules)
                    {
                        try
                        {
                            if (!(bool)rule.Enabled || (int)rule.Direction != DirIn) continue;
                            string app = rule.ApplicationName as string;
                            if (string.IsNullOrEmpty(app)) continue;
                            if (!string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)), exe, StringComparison.OrdinalIgnoreCase)) continue;
                            if (((int)rule.Profiles & profiles) == 0) continue;
                            int action = (int)rule.Action;
                            if (action == ActionBlock) block = true;
                            else if (action == ActionAllow)
                            {
                                string protocol = rule.Protocol == null ? "" : rule.Protocol.ToString();
                                if (protocol == "6" || protocol == "256") allow = true;   // TCP or any
                            }
                        }
                        catch { }
                    }
                    result = block ? FirewallState.Blocked : (allow ? FirewallState.Open : FirewallState.Missing);
                }
            }
            catch { result = FirewallState.Unknown; }
            last = result;
            return result;
        }

        /// <summary>Refreshes Last on a worker; at most one check runs at a time.</summary>
        public static void CheckAsync(Action<FirewallState> then)
        {
            if (Interlocked.CompareExchange(ref checking, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                FirewallState s = FirewallState.Unknown;
                try { s = Check(); } catch { }
                Interlocked.Exchange(ref checking, 0);
                if (then != null) then(s);
            });
        }

        /// <summary>
        /// The one-time setup: deletes this program's old inbound rules (a block rule made by an answered "Cancel" would win over an
        /// allow rule) and adds the allow rule. Shows Windows' UAC box. Blocks until it is answered: call it on a worker thread.
        /// </summary>
        public static bool Setup(out string error)
        {
            error = null;
            string exe = ExePath;
            if (exe == null || exe.IndexOf('"') >= 0) { error = "Could not tell which program to allow."; return false; }
            string del = "netsh advfirewall firewall delete rule name=all dir=in program=\"" + exe + "\"";
            string add = "netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow program=\"" + exe
                + "\" enable=yes profile=any protocol=TCP remoteip=LocalSubnet";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/s /c \"" + del + " & " + add + "\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { error = "Windows did not start the setup."; return false; }
                    p.WaitForExit(60000);
                    if (!p.HasExited) { error = "The setup took too long."; return false; }
                    if (p.ExitCode != 0) { error = "Windows Firewall refused the rule (code " + p.ExitCode + ")."; return false; }
                }
            }
            catch (Win32Exception ex)
            {
                error = ex.NativeErrorCode == 1223 ? "The Windows prompt was declined." : "Could not start the setup: " + ex.Message;
                return false;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            Check();
            return last == FirewallState.Open;
        }
    }
}
