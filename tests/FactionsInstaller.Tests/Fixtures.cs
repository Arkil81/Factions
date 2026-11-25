using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using FactionsInstaller.Logic;

namespace FactionsInstaller.Tests
{
    /// <summary>An in-memory machine. Paths are compared case-insensitively, as on Windows.</summary>
    public sealed class FakeHost : IHost
    {
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Version> Versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);

        public string SteamPath { get; set; }
        public string AppData { get; set; } = @"C:\Users\player\AppData\Roaming";

        public void Write(string path, string text)
        {
            Files[path] = text;
            for (string d = Paths.Directory(path); d.Length > 0; d = Paths.Directory(d))
                Dirs.Add(d);
        }

        public bool FileExists(string path) => Files.ContainsKey(path);
        public bool DirectoryExists(string path) => Dirs.Contains(path);
        public string ReadAllText(string path) => Files[path];

        public IEnumerable<string> EnumerateDirectories(string path) =>
            Dirs.Where(d => string.Equals(Paths.Directory(d), path, StringComparison.OrdinalIgnoreCase)).ToList();

        public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
            EnumerateDirectories(path).Concat(Files.Keys.Where(f => string.Equals(Paths.Directory(f), path, StringComparison.OrdinalIgnoreCase))).ToList();

        public Version GetFileVersion(string path)
        {
            Version v;
            return Versions.TryGetValue(path, out v) ? v : null;
        }

        /// <summary>What applying a plan does to the files, without the download.</summary>
        public void Apply(Plan plan)
        {
            if (plan.InstallPulsar)
            {
                Write(plan.PulsarExe, "MZ");
                Write(Paths.Join(plan.PulsarDir, "Interim.exe"), "MZ");
                Versions[plan.PulsarExe] = new Version(2, 4, 3);
            }
            foreach (FileChange f in plan.Files)
                Write(f.Path, f.After);
        }
    }

    public static class Sample
    {
        public const string Steam = @"C:\Program Files (x86)\Steam";
        public const string AccountId = "12345678";
        public const string SteamId64 = "76561197972611406"; // 76561197960265728 + 12345678
        public static readonly string LocalConfig = Paths.Join(Steam, "userdata", AccountId, "config", "localconfig.vdf");

        /// <summary>A localconfig.vdf in Steam's layout: tabs, CRLF-free, escaped quotes and backslashes.</summary>
        public static string LocalConfigText(string launchOptions)
        {
            string lo = launchOptions == null ? "" :
                "\t\t\t\t\t\t\"LaunchOptions\"\t\t\"" + Vdf.Escape(launchOptions) + "\"\n";
            return
                "\"UserLocalConfigStore\"\n{\n" +
                "\t\"friends\"\n\t{\n\t\t\"PersonaName\"\t\t\"player\"\n\t}\n" +
                "\t\"Software\"\n\t{\n" +
                "\t\t\"Valve\"\n\t\t{\n" +
                "\t\t\t\"Steam\"\n\t\t\t{\n" +
                "\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
                "\t\t\t\t\t\"440\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-novid\"\n\t\t\t\t\t}\n" +
                "\t\t\t\t\t\"244850\"\n\t\t\t\t\t{\n" +
                "\t\t\t\t\t\t\"LastPlayed\"\t\t\"1763900000\"\n" +
                lo +
                "\t\t\t\t\t\t\"cloud\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"last_sync_state\"\t\t\"synchronized\"\n\t\t\t\t\t\t}\n" +
                "\t\t\t\t\t}\n" +
                "\t\t\t\t}\n" +
                "\t\t\t}\n" +
                "\t\t}\n" +
                "\t}\n" +
                "}\n";
        }

        /// <summary>Steam, one signed-in account, Space Engineers installed.</summary>
        public static FakeHost Machine(string launchOptions)
        {
            var h = new FakeHost { SteamPath = Steam };
            h.Dirs.Add(Steam);
            h.Write(Paths.Join(Steam, "config", "loginusers.vdf"),
                "\"users\"\n{\n\t\"" + SteamId64 + "\"\n\t{\n\t\t\"AccountName\"\t\t\"player\"\n\t\t\"PersonaName\"\t\t\"Player\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}\n");
            h.Write(Paths.Join(Steam, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"244850\"\t\t\"26000000000\"\n\t\t}\n\t}\n}\n");
            h.Write(@"D:\SteamLibrary\steamapps\appmanifest_244850.acf", "\"AppState\"\n{\n}\n");
            h.Write(LocalConfig, LocalConfigText(launchOptions));
            return h;
        }

        public static void InstallPulsar(FakeHost h, string dir, Version version = null)
        {
            h.Write(Paths.Join(dir, "Legacy.exe"), "MZ");
            h.Write(Paths.Join(dir, "Interim.exe"), "MZ");
            h.Write(Paths.Join(dir, "Modern.exe"), "MZ");
            h.Versions[Paths.Join(dir, "Legacy.exe")] = version ?? new Version(2, 4, 3);
        }
    }

    /// <summary>Pulsar's own read and write: XmlSerializer over a FileStream.</summary>
    public static class PulsarXml
    {
        public static string Write<T>(T value)
        {
            var serializer = new XmlSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                serializer.Serialize(ms, value);
                // File.ReadAllText drops the BOM XmlSerializer writes; so does the installer's host.
                return Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF');
            }
        }

        public static T Read<T>(string xml)
        {
            var serializer = new XmlSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
                return (T)serializer.Deserialize(ms);
        }
    }
}
