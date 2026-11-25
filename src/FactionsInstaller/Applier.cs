using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FactionsInstaller.Logic;

namespace FactionsInstaller
{
    /// <summary>Carries out a <see cref="Plan"/>: downloads Pulsar if needed, then writes each file.</summary>
    public static class Applier
    {
        private const string PulsarLatest = "https://api.github.com/repos/SpaceGT/Pulsar/releases/latest";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static void Apply(Plan plan, Action<string> log)
        {
            if (plan.InstallPulsar)
                InstallPulsar(plan.PulsarDir, log);
            if (!File.Exists(plan.PulsarExe))
                throw new IOException("Pulsar's " + plan.PulsarExe + " is missing.");

            // Steam's file last: if anything before it fails, the game still starts the way it did.
            foreach (FileChange f in plan.Files.OrderBy(f => f == plan.LaunchOptions ? 1 : 0))
            {
                string now = File.Exists(f.Path) ? File.ReadAllText(f.Path) : null;
                if (now != f.Before)
                    throw new IOException(f.Path + " changed while the installer was open. Nothing more was written; run the installer again.");
                Directory.CreateDirectory(Path.GetDirectoryName(f.Path));
                if (now != null)
                {
                    string bak = BackupPath(f.Path);
                    File.Copy(f.Path, bak);
                    log("Backed up " + f.Path + " to " + Path.GetFileName(bak));
                }
                string tmp = f.Path + ".factionsui-new";
                File.WriteAllText(tmp, f.After, Utf8);
                if (now != null) File.Replace(tmp, f.Path, null);
                else File.Move(tmp, f.Path);
                log("Wrote " + f.Path);
            }
        }

        /// <summary>
        /// "&lt;file&gt;.bak", then ".bak1", ".bak2"… — the names Pulsar gives its own backups, which its
        /// profile loader skips, so a backup in the Profiles folder is never read as a profile.
        /// </summary>
        private static string BackupPath(string path)
        {
            string bak = path + ".bak";
            for (int i = 1; File.Exists(bak); i++)
                bak = path + ".bak" + i;
            return bak;
        }

        private static void InstallPulsar(string dir, Action<string> log)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("FactionsUI-Setup");
                http.Timeout = TimeSpan.FromMinutes(10);
                log("Looking up the latest Pulsar release");
                string json = http.GetStringAsync(PulsarLatest).GetAwaiter().GetResult();
                Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"(?<v>[^\"]+)\"");
                Match url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(?<u>[^\"]+-win-x64\\.zip)\"", RegexOptions.IgnoreCase);
                if (!url.Success)
                    throw new IOException("The latest Pulsar release has no Windows download.");
                log("Downloading Pulsar " + (tag.Success ? tag.Groups["v"].Value : "") + " from " + url.Groups["u"].Value);

                string zip = Path.Combine(Path.GetTempPath(), "FactionsUI-Setup-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    using (Stream s = http.GetStreamAsync(url.Groups["u"].Value).GetAwaiter().GetResult())
                    using (FileStream f = File.Create(zip))
                        s.CopyTo(f);
                    Extract(zip, dir);
                }
                finally
                {
                    try { File.Delete(zip); } catch (IOException) { }
                }
            }
            if (!File.Exists(Path.Combine(dir, "Legacy.exe")))
                throw new IOException("The Pulsar download did not contain Legacy.exe.");
            log("Installed Pulsar into " + dir);
        }

        private static void Extract(string zip, string dir)
        {
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            Directory.CreateDirectory(root);
            using (ZipArchive a = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry e in a.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The Pulsar download contains a path outside its folder: " + e.FullName);
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
            }
        }

        /// <summary>Asks Steam to exit and waits up to a minute for it to go.</summary>
        public static bool CloseSteam(Action<string> log)
        {
            log("Closing Steam");
            Process.Start(new ProcessStartInfo("steam://exit") { UseShellExecute = true });
            for (int i = 0; i < 120; i++)
            {
                if (!WindowsHost.SteamRunning()) return true;
                Thread.Sleep(500);
            }
            return false;
        }
    }
}
