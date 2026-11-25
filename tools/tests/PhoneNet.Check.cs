// Offline checks for the phone link's plumbing (Browser\PhoneNet.cs): address and client rules, request parsing, tokens.
// No WPF, WebView2 or Playnite needed (see phonenet.ps1). C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using SeleneBrowser;

internal static class PhoneNetCheck
{
    private static int failures;
    private static readonly IPAddress Phone = IPAddress.Parse("192.168.178.40");

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) failures++;
    }

    private static Dictionary<string, object> Parse(string json)
    {
        return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
    }

    private static bool Read(string raw, int max, out string method, out string target, out string body, out int status)
    {
        return PhoneNet.TryReadRequest(new MemoryStream(Encoding.UTF8.GetBytes(raw)), max, out method, out target, out body, out status);
    }

    private static int Main()
    {
        // ---- addresses
        Check(PhoneNet.IsPrivate(IPAddress.Parse("192.168.1.5")), "192.168/16 is private");
        Check(PhoneNet.IsPrivate(IPAddress.Parse("10.0.0.2")), "10/8 is private");
        Check(PhoneNet.IsPrivate(IPAddress.Parse("172.16.0.1")) && PhoneNet.IsPrivate(IPAddress.Parse("172.31.255.1")), "172.16-31 is private");
        Check(!PhoneNet.IsPrivate(IPAddress.Parse("172.32.0.1")) && !PhoneNet.IsPrivate(IPAddress.Parse("172.15.0.1")), "172.15 and 172.32 are not");
        Check(!PhoneNet.IsPrivate(IPAddress.Parse("8.8.8.8")) && !PhoneNet.IsPrivate(IPAddress.Parse("100.64.0.1")), "public and CGNAT addresses are not private");
        Check(PhoneNet.IsPrivate(IPAddress.Parse("169.254.10.10")), "link-local is private");
        Check(PhoneNet.IsPrivate(IPAddress.Parse("::ffff:192.168.1.5")), "an IPv4-mapped IPv6 address counts as its IPv4");
        Check(!PhoneNet.IsPrivate(IPAddress.Parse("::ffff:8.8.8.8")) && !PhoneNet.IsPrivate(IPAddress.Parse("2001:db8::1")), "public IPv6 is not private");
        Check(PhoneNet.IsAllowedClient(IPAddress.Loopback) && PhoneNet.IsAllowedClient(Phone), "loopback and home clients are allowed");
        Check(!PhoneNet.IsAllowedClient(IPAddress.Parse("203.0.113.9")) && !PhoneNet.IsAllowedClient(null), "outside clients are not");
        Check(PhoneNet.IsVirtualAdapter("vEthernet (Default Switch) Hyper-V Virtual Ethernet Adapter"), "Hyper-V adapter is skipped");
        Check(PhoneNet.IsVirtualAdapter("Tailscale Tailscale Tunnel") && PhoneNet.IsVirtualAdapter("TAP-Windows Adapter V9"), "VPN adapters are skipped");
        Check(!PhoneNet.IsVirtualAdapter("Ethernet Realtek PCIe GbE Family Controller") && !PhoneNet.IsVirtualAdapter("Wi-Fi Intel(R) Wi-Fi 6 AX201 160MHz"), "real adapters are kept");
        Check(PhoneNet.FixedEquals("abc", "abc") && !PhoneNet.FixedEquals("abc", "abd") && !PhoneNet.FixedEquals("abc", "abcd") && !PhoneNet.FixedEquals(null, "a"), "token comparison");
        string t1 = PhoneNet.NewToken(), t2 = PhoneNet.NewToken();
        Check(t1.Length == 24 && t1 != t2, "tokens are 24 hex characters and differ");

        // ---- request parsing
        string m, tg, bd; int st;
        Check(Read("GET /k/abc HTTP/1.1\r\nHost: x\r\n\r\n", 100, out m, out tg, out bd, out st) && m == "GET" && tg == "/k/abc" && bd == "", "reads a GET");
        Check(Read("POST /k/abc/text HTTP/1.1\r\nContent-Length: 12\r\ncontent-type: text/plain\r\n\r\n{\"text\":\"a\"}", 100, out m, out tg, out bd, out st) && m == "POST" && bd == "{\"text\":\"a\"}", "reads a POST body");
        Check(Read("POST /x HTTP/1.1\r\nContent-Length: 11\r\n\r\n", 100, out m, out tg, out bd, out st) == false, "a body that never arrives fails");
        Check(Read("POST /x HTTP/1.1\r\nContent-Length: 500\r\n\r\n", 100, out m, out tg, out bd, out st) == false && st == 413, "an oversized body is 413");
        Check(Read("POST /x HTTP/1.1\r\nContent-Length: -4\r\n\r\n", 100, out m, out tg, out bd, out st) == false, "a negative length fails");
        Check(Read("nonsense\r\n\r\n", 100, out m, out tg, out bd, out st) == false, "a malformed request line fails");
        Check(Read("GET /x HTTP/1.1\r\nX: " + new string('a', 9000) + "\r\n\r\n", 100, out m, out tg, out bd, out st) == false && st == 431, "a huge header is 431");
        Check(Read("GET /x HTTP/1.1\r\nHost", 100, out m, out tg, out bd, out st) == false, "a cut-off request fails");

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
