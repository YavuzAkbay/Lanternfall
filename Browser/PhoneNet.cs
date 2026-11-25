// PhoneNet.cs - the plumbing the phone link (PhoneRemote.cs) stands on: which addresses and clients are allowed, picking this PC's
// home-network address, reading one HTTP request, tokens, and writing a reply. No WPF or Playnite references on purpose, so
// tools	ests can compile it alone. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SeleneBrowser
{
    internal sealed class PhoneReply
    {
        public int Status = 200;
        public string Type = "application/json; charset=utf-8";
        public string Body = "";
        public bool Page;   // the phone page itself (gets the content security policy)
        public string Csp;  // overrides the default policy for the page (the remote's page shows YouTube thumbnails)
    }

    internal static class PhoneNet
    {
        /// <summary>10/8, 172.16/12, 192.168/16 and link-local 169.254/16 (an IPv4-mapped IPv6 address counts as its IPv4).</summary>
        public static bool IsPrivate(IPAddress a)
        {
            if (a == null) return false;
            if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6Teredo == false)
            {
                byte[] six = a.GetAddressBytes();
                bool mapped = true;
                for (int i = 0; i < 10; i++) if (six[i] != 0) mapped = false;
                if (!mapped || six[10] != 0xFF || six[11] != 0xFF) return false;
                a = new IPAddress(new[] { six[12], six[13], six[14], six[15] });
            }
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        /// <summary>A client the session answers: the home network (or this PC itself, for tests).</summary>
        public static bool IsAllowedClient(IPAddress a)
        {
            if (a == null) return false;
            if (IPAddress.IsLoopback(a)) return true;
            return IsPrivate(a);
        }

        /// <summary>Adapters that are not the home network: VPNs, virtual switches, Bluetooth, containers.</summary>
        public static bool IsVirtualAdapter(string nameAndDescription)
        {
            string s = (nameAndDescription ?? "").ToLowerInvariant();
            string[] bad = { "vethernet", "virtual", "vmware", "hyper-v", "vpn", "tap-", "tap windows", "tailscale", "wireguard", "zerotier", "docker", "wsl", "bluetooth", "pseudo", "npcap", "loopback", "hamachi", "openvpn" };
            foreach (string w in bad) if (s.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>Set by test harnesses (through reflection) to listen somewhere else, such as loopback, which Windows Firewall never asks about.</summary>
#pragma warning disable 649   // assigned only through reflection, by test harnesses
        internal static IPAddress TestAddress;
#pragma warning restore 649

        /// <summary>The PC's address on the home network (an active Wi-Fi / Ethernet adapter with a gateway), or null.</summary>
        public static IPAddress PickLanAddress()
        {
            if (TestAddress != null) return TestAddress;
            IPAddress withGateway = null, any = null;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (IsVirtualAdapter(nic.Name + " " + nic.Description)) continue;
                    IPInterfaceProperties props = nic.GetIPProperties();
                    bool gateway = false;
                    foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) gateway = true;
                    foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(u.Address)) continue;
                        if (gateway && withGateway == null) withGateway = u.Address;
                        if (any == null) any = u.Address;
                    }
                }
            }
            catch { }
            return withGateway ?? any;
        }

        /// <summary>Compares two strings in the same time whatever they hold (tokens).</summary>
        public static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Reads one HTTP request (header up to 8 KB, body up to maxBody). False when it is malformed or too big.</summary>
        public static bool TryReadRequest(Stream s, int maxBody, out string method, out string target, out string body, out int errorStatus)
        {
            method = target = body = "";
            errorStatus = 400;
            var head = new List<byte>();
            int state = 0;   // how much of \r\n\r\n has been seen
            while (state < 4)
            {
                int b = s.ReadByte();
                if (b < 0) return false;
                head.Add((byte)b);
                if (head.Count > 8192) { errorStatus = 431; return false; }
                if ((b == '\r' && (state == 0 || state == 2)) || (b == '\n' && (state == 1 || state == 3))) state++;
                else state = b == '\r' ? 1 : 0;
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/", StringComparison.Ordinal)) return false;
            method = first[0];
            target = first[1];
            int length = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim();
                if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(lines[i].Substring(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0) return false;
            }
            if (length > maxBody) { errorStatus = 413; return false; }
            if (length > 0)
            {
                byte[] buf = new byte[length];
                int got = 0;
                while (got < length)
                {
                    int n = s.Read(buf, got, length - got);
                    if (n <= 0) return false;
                    got += n;
                }
                body = Encoding.UTF8.GetString(buf);
            }
            return true;
        }

        public static string NewToken()
        {
            byte[] b = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    internal static class PhoneHttp
    {
        public static void Write(Stream s, PhoneReply r)
        {
            byte[] body = Encoding.UTF8.GetBytes(r.Body ?? "");
            var h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(r.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(r.Status)).Append("\r\n");
            h.Append("Content-Type: ").Append(r.Type).Append("\r\n");
            h.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            h.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n");
            if (r.Page) h.Append("Content-Security-Policy: ").Append(r.Csp ?? "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'").Append("\r\n");
            h.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(h.ToString());
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        public static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 410: return "Gone";
                case 413: return "Payload Too Large";
                case 429: return "Too Many Requests";
                case 502: return "Bad Gateway";
                case 431: return "Request Header Fields Too Large";
            }
            return "Error";
        }

        public static PhoneReply Plain(int status, string message)
        {
            return new PhoneReply { Status = status, Type = "text/plain; charset=utf-8", Body = message };
        }
    }
}
