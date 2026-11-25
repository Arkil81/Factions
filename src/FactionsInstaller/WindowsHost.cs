using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FactionsInstaller.Logic;
using Microsoft.Win32;

namespace FactionsInstaller
{
    /// <summary>The real machine. <c>--steam</c> and <c>--appdata</c> point it at a test tree instead.</summary>
    public sealed class WindowsHost : IHost
    {
        public WindowsHost(string steamOverride, string appDataOverride)
        {
            IsSandbox = steamOverride != null || appDataOverride != null;
            SteamPath = Normalize(steamOverride) ?? FindSteam();
            AppData = Normalize(appDataOverride) ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        /// <summary>True when pointed at a test tree: no real Steam watches those files.</summary>
        public bool IsSandbox { get; private set; }
        public string SteamPath { get; private set; }
        public string AppData { get; private set; }

        private static string Normalize(string path)
        {
            return path == null ? null : Path.GetFullPath(path.Replace('/', '\\'));
        }

        private static string FindSteam()
        {
            string p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrWhiteSpace(p))
                p = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            return string.IsNullOrWhiteSpace(p) ? null : p.Replace('/', '\\');
        }

        public bool FileExists(string path) { return File.Exists(path); }
        public bool DirectoryExists(string path) { return Directory.Exists(path); }
        public string ReadAllText(string path) { return File.ReadAllText(path); }
        public IEnumerable<string> EnumerateDirectories(string path) { return Directory.EnumerateDirectories(path).ToList(); }
        public IEnumerable<string> EnumerateFileSystemEntries(string path) { return Directory.EnumerateFileSystemEntries(path).ToList(); }

        public Version GetFileVersion(string path)
        {
            if (!File.Exists(path)) return null;
            FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
            if (v.FileMajorPart == 0 && v.FileMinorPart == 0 && v.FileBuildPart == 0) return null;
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }

        public string SteamExe
        {
            get
            {
                string p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string;
                if (!string.IsNullOrWhiteSpace(p)) return p.Replace('/', '\\');
                return SteamPath == null ? null : Path.Combine(SteamPath, "steam.exe");
            }
        }

        public static bool IsRunning(params string[] names)
        {
            foreach (string n in names)
            {
                Process[] ps = Process.GetProcessesByName(n);
                bool any = ps.Length > 0;
                foreach (Process p in ps) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        public static bool SteamRunning() { return IsRunning("steam"); }
        public static bool GameRunning() { return IsRunning("SpaceEngineers", "Legacy", "Interim"); }
    }
}
