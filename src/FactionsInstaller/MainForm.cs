using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using FactionsInstaller.Logic;

namespace FactionsInstaller
{
    public sealed class MainForm : Form
    {
        private const string GuideUrl = "https://github.com/Arkil81/Factions/blob/main/docs/install-client.md";

        private readonly WindowsHost _host;
        private readonly TextBox _log;
        private readonly Button _install, _remove, _close;
        private Plan _plan;

        public MainForm(WindowsHost host)
        {
            _host = host;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Factions UI Setup";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(680, 500);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;

            var title = new Label
            {
                Text = "Factions UI",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 12),
            };
            var intro = new Label
            {
                Text = "Factions UI is the Space Engineers client plugin for servers running Factions. This sets it up "
                     + "in the Pulsar plugin loader: it installs Pulsar if you do not have it, makes Steam start the "
                     + "game with Pulsar, and adds and enables Factions UI. You do not need to change anything in the game.",
                Location = new Point(16, 46),
                Size = new Size(648, 52),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            var trust = new Label
            {
                Text = "Plugins run with the same access to your computer as the game itself. Pulsar builds Factions UI "
                     + "from its public source at github.com/Arkil81/Factions, and keeps it updated from there.",
                Location = new Point(16, 102),
                Size = new Size(648, 36),
                ForeColor = Color.FromArgb(150, 80, 0),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 146),
                Size = new Size(648, 290),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = SystemColors.Window,
            };
            var guide = new LinkLabel
            {
                Text = "Install guide",
                AutoSize = true,
                Location = new Point(16, 456),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            guide.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo(GuideUrl) { UseShellExecute = true });

            _install = new Button { Text = "Install", Size = new Size(110, 30), Location = new Point(318, 452), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _remove = new Button { Text = "Remove Factions UI", Size = new Size(140, 30), Location = new Point(434, 452), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _close = new Button { Text = "Close", Size = new Size(84, 30), Location = new Point(580, 452), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            _install.Click += (s, e) => Run(false);
            _remove.Click += (s, e) => Run(true);
            _close.Click += (s, e) => Close();
            AcceptButton = _install;
            CancelButton = _close;

            Controls.AddRange(new Control[] { title, intro, trust, _log, guide, _install, _remove, _close });
            Shown += (s, e) => ShowPlan();
        }

        private void ShowPlan()
        {
            _log.Clear();
            _plan = Planner.PlanInstall(_host);
            foreach (string line in PlanText.Describe(_plan, false))
                Log(line);
            _install.Enabled = _plan.Error == null && !_plan.NothingToDo;
            _remove.Enabled = _plan.Error == null && Planner.PlanRemove(_host).Error == null;
        }

        private void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            _log.AppendText(line + Environment.NewLine);
        }

        private async void Run(bool remove)
        {
            if (!_host.IsSandbox && WindowsHost.GameRunning())
            {
                MessageBox.Show(this, "Close Space Engineers first, then click " + (remove ? "Remove" : "Install") + " again.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Plan plan = remove ? Planner.PlanRemove(_host) : Planner.PlanInstall(_host);
            if (plan.Error != null) { ShowPlan(); return; }

            bool restartSteam = false;
            if (plan.LaunchOptions != null && !_host.IsSandbox && WindowsHost.SteamRunning())
            {
                DialogResult r = MessageBox.Show(this,
                    "Steam has to be closed to change Space Engineers' launch options; it overwrites them while it runs.\n\nClose Steam now? It is started again when the installer is done.",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;
                SetBusy(true);
                bool closed = await Task.Run(() => Applier.CloseSteam(Log));
                if (!closed)
                {
                    Log("Steam did not close within a minute. Close it yourself and click Install again.");
                    SetBusy(false);
                    return;
                }
                restartSteam = true;
                // Steam writes its config on exit, so the plan is made again from what it left.
                plan = Planner.PlanInstall(_host);
                if (plan.Error != null) { SetBusy(false); ShowPlan(); return; }
            }

            SetBusy(true);
            Log("");
            try
            {
                await Task.Run(() => Applier.Apply(plan, Log));
                Plan check = remove ? Planner.PlanRemove(_host) : Planner.PlanInstall(_host);
                if (check.Error != null || !check.NothingToDo)
                {
                    Log("");
                    Log("The files were written but a second check still finds changes to make:");
                    foreach (string line in PlanText.Describe(check, remove)) Log(line);
                }
                else
                {
                    Log("");
                    Log(remove
                        ? "Done. Factions UI is removed from Pulsar; restart the game if it is open."
                        : "Done. Start Space Engineers from Steam as usual. The first start takes longer while Pulsar builds Factions UI.");
                }
            }
            catch (Exception ex)
            {
                Log("");
                Log("Stopped: " + ex.Message);
            }
            finally
            {
                if (restartSteam && _host.SteamExe != null)
                {
                    try { Process.Start(new ProcessStartInfo(_host.SteamExe) { UseShellExecute = true }); Log("Started Steam again."); }
                    catch (Exception ex) { Log("Could not start Steam again (" + ex.Message + "); start it yourself."); }
                }
                SetBusy(false);
                _install.Enabled = false;
            }
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), busy); return; }
            UseWaitCursor = busy;
            _install.Enabled = !busy;
            _remove.Enabled = !busy;
            _close.Enabled = !busy;
        }
    }
}
