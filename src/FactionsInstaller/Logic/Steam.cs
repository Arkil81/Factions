using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FactionsInstaller.Logic
{
    /// <summary>Where Steam keeps what the installer reads: users, libraries and launch options.</summary>
    public static class Steam
    {
        public const string SpaceEngineersAppId = "244850";
        private const ulong SteamId64Base = 76561197960265728UL;

        public static readonly string[] LaunchOptionsPath =
            { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", SpaceEngineersAppId, "LaunchOptions" };

        /// <summary>
        /// The localconfig.vdf of the account that signed in to Steam most recently, or of the only
        /// account that has one. Null when no account has signed in on this computer.
        /// </summary>
        public static string FindLocalConfig(IHost host, string steamPath, out string accountName)
        {
            accountName = null;
            string userdata = Paths.Join(steamPath, "userdata");
            string loginUsers = Paths.Join(steamPath, "config", "loginusers.vdf");
            if (host.FileExists(loginUsers))
            {
                VdfNode users = Vdf.Parse(host.ReadAllText(loginUsers)).Child("users");
                if (users != null && users.Children != null)
                {
                    IEnumerable<VdfNode> ordered = users.Children
                        .Where(u => u.IsBlock)
                        .OrderByDescending(u => (u.Child("MostRecent") ?? new VdfNode()).Value == "1")
                        .ThenByDescending(u => ParseLong((u.Child("Timestamp") ?? new VdfNode()).Value));
                    foreach (VdfNode u in ordered)
                    {
                        ulong id64;
                        if (!ulong.TryParse(u.Key, out id64) || id64 < SteamId64Base) continue;
                        string path = Paths.Join(userdata, (id64 - SteamId64Base).ToString(), "config", "localconfig.vdf");
                        if (host.FileExists(path))
                        {
                            VdfNode name = u.Child("PersonaName") ?? u.Child("AccountName");
                            accountName = name == null ? null : name.Value;
                            return path;
                        }
                    }
                }
            }
            if (!host.DirectoryExists(userdata)) return null;
            List<string> configs = host.EnumerateDirectories(userdata)
                .Select(d => Paths.Join(d, "config", "localconfig.vdf"))
                .Where(host.FileExists)
                .ToList();
            return configs.Count == 1 ? configs[0] : null;
        }

        /// <summary>True when a Steam library holds an installed Space Engineers.</summary>
        public static bool IsSpaceEngineersInstalled(IHost host, string steamPath)
        {
            var libraries = new List<string> { steamPath };
            string lf = Paths.Join(steamPath, "steamapps", "libraryfolders.vdf");
            if (host.FileExists(lf))
            {
                VdfNode folders = Vdf.Parse(host.ReadAllText(lf)).Child("libraryfolders");
                if (folders != null && folders.Children != null)
                {
                    foreach (VdfNode f in folders.Children)
                    {
                        if (f.IsBlock)
                        {
                            VdfNode p = f.Child("path");
                            if (p != null && p.Value != null) libraries.Add(p.Value);
                        }
                        else if (f.Value != null && f.Value.Contains(":"))
                        {
                            libraries.Add(f.Value);
                        }
                    }
                }
            }
            return libraries.Any(l => host.FileExists(Paths.Join(l, "steamapps", "appmanifest_" + SpaceEngineersAppId + ".acf")));
        }

        public static string ReadLaunchOptions(string localConfig)
        {
            VdfNode n = Vdf.Parse(localConfig).Path(LaunchOptionsPath);
            return n == null || n.IsBlock ? "" : n.Value;
        }

        private static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, out v) ? v : 0;
        }
    }
}
