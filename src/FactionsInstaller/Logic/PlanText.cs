using System.Collections.Generic;
using System.Linq;

namespace FactionsInstaller.Logic
{
    /// <summary>A plan in the words the installer window shows.</summary>
    public static class PlanText
    {
        public static List<string> Describe(Plan plan, bool remove)
        {
            var lines = new List<string>();
            if (plan.Error != null)
            {
                lines.Add(plan.Error);
                return lines;
            }
            if (plan.SteamAccount != null)
                lines.Add("Steam account: " + plan.SteamAccount);
            if (plan.PulsarDir != null)
                lines.Add("Pulsar: " + plan.PulsarDir + (plan.PulsarVersion == null ? "" : " (version " + plan.PulsarVersion.ToString(3) + ")"));
            lines.AddRange(plan.Notes);
            lines.Add("");
            if (plan.NothingToDo)
            {
                lines.Add(remove ? "Factions UI is not installed; there is nothing to remove." : "Factions UI is already installed and set up. There is nothing to change.");
                return lines;
            }
            lines.Add("This will:");
            if (plan.InstallPulsar)
                lines.Add("  - download the latest Pulsar from github.com/SpaceGT/Pulsar into " + plan.PulsarDir);
            foreach (FileChange f in plan.Files)
                foreach (string c in f.Changes)
                    lines.Add("  - " + c);
            if (plan.Files.Any(f => f.Before != null))
                lines.Add("Each file is backed up beside itself (.bak) before it is changed.");
            return lines;
        }
    }
}
