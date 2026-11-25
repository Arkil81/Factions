using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using FactionsInstaller.Logic;

namespace FactionsInstaller
{
    /// <summary>
    /// FactionsUI-Setup.exe. With no arguments it opens the installer window.
    ///
    /// Headless, for testing against a throwaway tree instead of the real Steam and %AppData%:
    ///   FactionsUI-Setup.exe --steam &lt;dir&gt; --appdata &lt;dir&gt; --out &lt;file&gt; [--apply] [--remove]
    /// writes the plan (and with --apply, the result and a second check) to &lt;file&gt;. Exit code 0 when
    /// the plan had no error and, with --apply, the second check found nothing left to do.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                opts[args[i]] = hasValue ? args[++i] : "";
            }
            string steam, appData, output;
            opts.TryGetValue("--steam", out steam);
            opts.TryGetValue("--appdata", out appData);
            var host = new WindowsHost(steam, appData);

            if (opts.TryGetValue("--out", out output))
                return Headless(host, output, opts.ContainsKey("--apply"), opts.ContainsKey("--remove"));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(host));
            return 0;
        }

        private static int Headless(WindowsHost host, string output, bool apply, bool remove)
        {
            var lines = new List<string>();
            int code = 1;
            try
            {
                Plan plan = remove ? Planner.PlanRemove(host) : Planner.PlanInstall(host);
                lines.AddRange(PlanText.Describe(plan, remove));
                if (plan.Error == null)
                {
                    code = 0;
                    if (apply)
                    {
                        lines.Add("");
                        Applier.Apply(plan, lines.Add);
                        Plan check = remove ? Planner.PlanRemove(host) : Planner.PlanInstall(host);
                        lines.Add("");
                        lines.Add("Second check:");
                        lines.AddRange(PlanText.Describe(check, remove));
                        code = check.Error == null && check.NothingToDo ? 0 : 2;
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("Stopped: " + ex);
                code = 3;
            }
            lines.Add("exit " + code);
            File.WriteAllLines(output, lines);
            return code;
        }
    }
}
