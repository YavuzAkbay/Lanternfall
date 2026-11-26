// Per-account scope: reads Selene Core's profiles.json directly (the Browser add-on cannot reference SeleneCore types).
// C# 5 only. Nothing here logs personal data (profile names/ids are kept in memory only).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace SeleneBrowser
{
    /// <summary>
    /// Resolves the signed-in Selene profile from Core's profiles.json so the Browser add-on can keep
    /// personal YouTube data in a per-profile folder. All members are defensive and never throw.
    /// </summary>
    public static class ProfileScope
    {
        // Core's add-on id is LanternfallCore_d9f97137-a577-4324-bcdf-dbe64e4c21d2; Playnite's
        // GetPluginUserDataPath() is <APPDATA>\Playnite\ExtensionsData\<plugin guid>.
        const string CoreDataGuid = "d9f97137-a577-4324-bcdf-dbe64e4c21d2";
        const string ProfilesFile = "profiles.json";

        // dev switches mirrored from SeleneCore/src/Profiles.cs: when %TEMP%\selene-dev-profiles exists,
        // profiles live in %TEMP%\selene-dev-profiles-data instead of Core's normal data folder.
        const string DevMarker = "selene-dev-profiles";
        const string DevFolder = "selene-dev-profiles-data";

        static readonly object lk = new object();
        static string id;
        static string name;
        static bool read;
        // what the last parse of profiles.json gave, valid while the file's write time and length are unchanged
        static bool cacheValid;
        static string cachePath, cacheId, cacheName;
        static DateTime cacheWritten;
        static long cacheLength;

        // tiny local DTOs: only the three fields we need; JavaScriptSerializer ignores the rest.
        public class ProfilesFileDto
        {
            public ProfilesFileDto() { Profiles = new List<ProfileEntryDto>(); }
            public List<ProfileEntryDto> Profiles { get; set; }
            public string ActiveId { get; set; }
        }

        public class ProfileEntryDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        static string FilePath()
        {
            try
            {
                string temp = Path.GetTempPath();
                string folder;
                if (File.Exists(Path.Combine(temp, DevMarker)))
                    folder = Path.Combine(temp, DevFolder);
                else
                    folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Playnite", "ExtensionsData", CoreDataGuid);
                return Path.Combine(folder, ProfilesFile);
            }
            catch { return ""; }
        }

        /// <summary>Re-read profiles.json. True when the resolved profile id changed since the last read.</summary>
        public static bool Refresh()
        {
            string previous;
            lock (lk) { previous = id; }
            string nextId = "";
            string nextName = "";
            try
            {
                string path = FilePath();
                bool fromCache = false;
                DateTime written = DateTime.MinValue;
                long length = -1;
                if (path.Length > 0)
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        written = info.LastWriteTimeUtc;
                        length = info.Length;
                        // the watcher calls this every 2 s: an unchanged file is not read and parsed again
                        lock (lk)
                        {
                            if (cacheValid && cachePath == path && cacheWritten == written && cacheLength == length)
                            {
                                nextId = cacheId; nextName = cacheName; fromCache = true;
                            }
                        }
                    }
                }
                if (!fromCache && length >= 0)
                {
                    string text;
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                        text = sr.ReadToEnd();
                    if (!string.IsNullOrEmpty(text))
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        ser.MaxJsonLength = int.MaxValue;
                        ProfilesFileDto dto = ser.Deserialize<ProfilesFileDto>(text);
                        if (dto != null && dto.Profiles != null && !string.IsNullOrEmpty(dto.ActiveId))
                        {
                            Guid active;
                            if (Guid.TryParse(dto.ActiveId, out active))
                            {
                                foreach (ProfileEntryDto p in dto.Profiles)
                                {
                                    if (p == null || string.IsNullOrEmpty(p.Id)) continue;
                                    Guid pid;
                                    if (!Guid.TryParse(p.Id, out pid)) continue;
                                    if (pid == active)
                                    {
                                        // "N" = 32 hex chars, safe as a directory name.
                                        nextId = pid.ToString("N");
                                        nextName = p.Name == null ? "" : p.Name;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    lock (lk)
                    {
                        cacheValid = true; cachePath = path; cacheWritten = written; cacheLength = length;
                        cacheId = nextId; cacheName = nextName;
                    }
                }
            }
            catch { }
            // only the profile Core signed in during this session (the file still holds last run's until the picker is done)
            try
            {
                string signedIn = AppDomain.CurrentDomain.GetData("Lanternfall.SignedInProfile") as string;
                if (!string.Equals(signedIn, nextId, StringComparison.OrdinalIgnoreCase)) { nextId = ""; nextName = ""; }
            }
            catch { nextId = ""; nextName = ""; }
            lock (lk)
            {
                id = nextId;
                name = nextName;
                read = true;
            }
            return !string.Equals(previous, nextId, StringComparison.Ordinal);
        }

        static void EnsureRead()
        {
            if (read) return;
            Refresh();
        }

        /// <summary>Guid "N" format of the signed-in profile, or "" when signed out / unavailable.</summary>
        public static string CurrentId
        {
            get { EnsureRead(); lock (lk) return id == null ? "" : id; }
        }

        /// <summary>Display name of the signed-in profile, or "".</summary>
        public static string CurrentName
        {
            get { EnsureRead(); lock (lk) return name == null ? "" : name; }
        }

        /// <summary>True when a signed-in profile id was resolved.</summary>
        public static bool Available
        {
            get { EnsureRead(); lock (lk) return id != null && id.Length > 0; }
        }
    }
}
