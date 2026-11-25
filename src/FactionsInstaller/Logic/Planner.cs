using System;
using System.Collections.Generic;
using System.Linq;

namespace FactionsInstaller.Logic
{
    /// <summary>Everything the planner reads from the machine, so a plan can be computed without one.</summary>
    public interface IHost
    {
        /// <summary>Steam's install folder, or null when Steam is not installed.</summary>
        string SteamPath { get; }
        /// <summary>%AppData% (Roaming).</summary>
        string AppData { get; }
        bool FileExists(string path);
        bool DirectoryExists(string path);
        string ReadAllText(string path);
        IEnumerable<string> EnumerateDirectories(string path);
        IEnumerable<string> EnumerateFileSystemEntries(string path);
        /// <summary>The file version of an executable, or null when it has none.</summary>
        Version GetFileVersion(string path);
    }

    /// <summary>Windows paths built the same way whatever the OS the logic runs on.</summary>
    public static class Paths
    {
        public static string Join(string first, params string[] rest)
        {
            string p = first;
            foreach (string r in rest)
                p = p.TrimEnd('\\', '/') + "\\" + r.TrimStart('\\', '/');
            return p;
        }

        public static string Directory(string path)
        {
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i < 0 ? "" : path.Substring(0, i);
        }
    }

    /// <summary>One file the installer will write: its current text (null if new) and its new text.</summary>
    public sealed class FileChange
    {
        public string Path;
        public string Before;
        public string After;
        public readonly List<string> Changes = new List<string>();
    }

    public sealed class Plan
    {
        /// <summary>Set when the installer must stop; nothing is written.</summary>
        public string Error;
        public string SteamAccount;
        /// <summary>Pulsar is downloaded into <see cref="PulsarDir"/> before anything else.</summary>
        public bool InstallPulsar;
        public string PulsarDir;
        public string PulsarExe;
        public Version PulsarVersion;
        public FileChange LaunchOptions;
        public FileChange Sources;
        public FileChange Profile;
        public readonly List<string> Notes = new List<string>();

        public IEnumerable<FileChange> Files
        {
            get { return new[] { Sources, Profile, LaunchOptions }.Where(f => f != null); }
        }

        public bool NothingToDo
        {
            get { return Error == null && !InstallPulsar && !Files.Any(); }
        }

        public static Plan Fail(string error)
        {
            return new Plan { Error = error };
        }
    }

    /// <summary>
    /// Works out what installing (or removing) Factions UI needs on this machine. Reads only; the
    /// plan is applied by the caller. Running it again after a successful apply yields a plan with
    /// nothing to do, which is how an install is verified.
    /// </summary>
    public static class Planner
    {
        public const string PulsarFolderName = "Pulsar";

        public static Plan PlanInstall(IHost host)
        {
            Plan plan;
            string localConfig, localConfigText;
            LaunchOptionsInfo options;
            if (!LocateSteam(host, out plan, out localConfig, out localConfigText, out options))
                return plan;

            // Where Pulsar is, or where it is going to be.
            string defaultDir = Paths.Join(host.AppData, PulsarFolderName);
            string exe = null;
            if (options.Launcher != PulsarLauncher.None)
            {
                string named = Paths.Join(Paths.Directory(options.PulsarExe), "Legacy.exe");
                if (host.FileExists(named))
                {
                    exe = named;
                    if (options.Launcher != PulsarLauncher.Legacy)
                        plan.Notes.Add("Space Engineers was set to start with Pulsar's " + options.Launcher + ".exe, which Factions UI does not run under; it will start with Legacy.exe");
                }
                else
                {
                    plan.Notes.Add("The launch options name " + options.PulsarExe + ", which is not there");
                }
            }
            if (exe == null)
            {
                string atDefault = Paths.Join(defaultDir, "Legacy.exe");
                if (host.FileExists(atDefault))
                {
                    exe = atDefault;
                }
                else
                {
                    if (host.DirectoryExists(defaultDir) && host.EnumerateFileSystemEntries(defaultDir).Any())
                        return Plan.Fail(defaultDir + " already has files in it but no Pulsar. Pulsar empties its folder when it updates, so it is not installed there. Move those files elsewhere and run this again.");
                    plan.InstallPulsar = true;
                    exe = atDefault;
                    plan.Notes.Add("Pulsar is not installed; the latest release will be downloaded into " + defaultDir);
                }
            }
            plan.PulsarExe = exe;
            plan.PulsarDir = Paths.Directory(exe);

            if (!plan.InstallPulsar)
            {
                plan.PulsarVersion = host.GetFileVersion(exe);
                if (plan.PulsarVersion != null && plan.PulsarVersion.Major != 2)
                    return Plan.Fail("This installer knows Pulsar 2. The Pulsar at " + plan.PulsarDir + " is version " + plan.PulsarVersion + ". Install Factions UI by hand; see the install guide.");
            }

            // Steam launch options.
            string newOptions;
            try
            {
                newOptions = LaunchOptions.WithLauncher(options.Raw, exe);
            }
            catch (InvalidOperationException)
            {
                return Plan.Fail("Space Engineers' launch options already start the game through another program (" + options.Raw + "). Change them by hand to start with \"" + exe + "\" %command%.");
            }
            if (newOptions != options.Raw)
            {
                var lo = new FileChange { Path = localConfig, Before = localConfigText };
                try
                {
                    lo.After = Vdf.SetString(localConfigText, Steam.LaunchOptionsPath, newOptions);
                }
                catch (FormatException e)
                {
                    return Plan.Fail("Steam's localconfig.vdf could not be read (" + e.Message + ").");
                }
                lo.Changes.Add("set Space Engineers' Steam launch options: " + (options.Raw.Length == 0 ? "(empty)" : options.Raw) + "  ->  " + newOptions);
                plan.LaunchOptions = lo;
            }

            // Pulsar's own config.
            string dataDir = DataDir(host, options, plan.PulsarDir);
            try
            {
                plan.Sources = Change(host, Paths.Join(dataDir, "Sources", "sources.xml"), PulsarConfig.AddSource);
                plan.Profile = Change(host, Paths.Join(dataDir, "Profiles", "Current.xml"), PulsarConfig.EnablePlugin);
            }
            catch (ConfigException e)
            {
                return Plan.Fail("Pulsar's " + e.Message + ". Nothing was changed.");
            }
            return plan;
        }

        public static Plan PlanRemove(IHost host)
        {
            Plan plan;
            string localConfig, localConfigText;
            LaunchOptionsInfo options;
            if (!LocateSteam(host, out plan, out localConfig, out localConfigText, out options))
                return plan;

            var dirs = new List<string>();
            if (options.Launcher != PulsarLauncher.None) dirs.Add(Paths.Directory(options.PulsarExe));
            dirs.Add(Paths.Join(host.AppData, PulsarFolderName));
            string dir = dirs.FirstOrDefault(d => host.FileExists(Paths.Join(d, "Legacy.exe")));
            if (dir == null)
                return Plan.Fail("Pulsar was not found, so there is nothing to remove.");
            plan.PulsarDir = dir;
            plan.PulsarExe = Paths.Join(dir, "Legacy.exe");

            string dataDir = DataDir(host, options, dir);
            try
            {
                plan.Sources = Change(host, Paths.Join(dataDir, "Sources", "sources.xml"), PulsarConfig.RemoveSource);
                plan.Profile = Change(host, Paths.Join(dataDir, "Profiles", "Current.xml"), PulsarConfig.DisablePlugin);
            }
            catch (ConfigException e)
            {
                return Plan.Fail("Pulsar's " + e.Message + ". Nothing was changed.");
            }
            return plan;
        }

        private static bool LocateSteam(IHost host, out Plan plan, out string localConfig, out string text, out LaunchOptionsInfo options)
        {
            plan = new Plan();
            localConfig = text = null;
            options = null;
            string steam = host.SteamPath;
            if (steam == null || !host.DirectoryExists(steam))
            {
                plan = Plan.Fail("Steam was not found on this computer.");
                return false;
            }
            if (!Steam.IsSpaceEngineersInstalled(host, steam))
            {
                plan = Plan.Fail("Space Engineers is not installed in Steam. Install it, start it once, and run this again.");
                return false;
            }
            string account;
            localConfig = Steam.FindLocalConfig(host, steam, out account);
            if (localConfig == null)
            {
                plan = Plan.Fail("No Steam account's settings were found. Sign in to Steam, start Space Engineers once, and run this again.");
                return false;
            }
            plan.SteamAccount = account;
            text = host.ReadAllText(localConfig);
            string raw;
            try
            {
                raw = Steam.ReadLaunchOptions(text);
            }
            catch (FormatException e)
            {
                plan = Plan.Fail("Steam's localconfig.vdf could not be read (" + e.Message + ").");
                return false;
            }
            options = LaunchOptions.Parse(raw);
            if (options.Profile != null)
            {
                plan = Plan.Fail("Space Engineers' launch options choose a Pulsar profile (-profile " + options.Profile + "). Enable Factions UI in that profile by hand, or remove -profile and run this again.");
                return false;
            }
            return true;
        }

        /// <summary>Pulsar's data folder: "Legacy" beside the exe, or under %AppData%\Pulsar with -useHome.</summary>
        private static string DataDir(IHost host, LaunchOptionsInfo options, string pulsarDir)
        {
            string baseDir = options.UseHome ? Paths.Join(host.AppData, PulsarFolderName) : pulsarDir;
            return Paths.Join(baseDir, "Legacy");
        }

        private static FileChange Change(IHost host, string path, Func<string, ConfigEdit> edit)
        {
            string before = host.FileExists(path) ? host.ReadAllText(path) : null;
            ConfigEdit result = edit(before);
            if (!result.Changed) return null;
            var c = new FileChange { Path = path, Before = before, After = result.Text };
            c.Changes.AddRange(result.Changes);
            return c;
        }
    }
}
