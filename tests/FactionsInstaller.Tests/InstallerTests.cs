using System;
using System.Linq;
using FactionsInstaller.Logic;
using FactionsInstaller.Tests.Pulsar;
using Xunit;

namespace FactionsInstaller.Tests
{
    public class VdfTests
    {
        [Fact]
        public void Reads_an_escaped_launch_option()
        {
            string text = Sample.LocalConfigText("\"C:\\Pulsar\\Legacy.exe\" %command% -sources");
            Assert.Equal("\"C:\\Pulsar\\Legacy.exe\" %command% -sources", Steam.ReadLaunchOptions(text));
        }

        [Fact]
        public void Replacing_a_value_changes_no_other_byte()
        {
            string before = Sample.LocalConfigText("-skipintro");
            string after = Vdf.SetString(before, Steam.LaunchOptionsPath, "\"C:\\P\\Legacy.exe\" %command% -skipintro");
            string expected = before.Replace("\"-skipintro\"", "\"\\\"C:\\\\P\\\\Legacy.exe\\\" %command% -skipintro\"");
            Assert.Equal(expected, after);
        }

        [Fact]
        public void Inserts_a_missing_key_into_the_app_block()
        {
            string before = Sample.LocalConfigText(null);
            string after = Vdf.SetString(before, Steam.LaunchOptionsPath, "\"C:\\P\\Legacy.exe\" %command%");
            Assert.Equal("\"C:\\P\\Legacy.exe\" %command%", Steam.ReadLaunchOptions(after));
            VdfNode app = Vdf.Parse(after).Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "244850");
            Assert.Equal("1763900000", app.Child("LastPlayed").Value);
            Assert.Equal("synchronized", app.Path("cloud", "last_sync_state").Value);
            Assert.Equal("-novid", Vdf.Parse(after).Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "440", "LaunchOptions").Value);
            Assert.Contains("\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"", after);
        }

        [Fact]
        public void Creates_the_app_block_when_the_game_has_none()
        {
            string before = Sample.LocalConfigText(null).Replace("\"244850\"", "\"999\"");
            string after = Vdf.SetString(before, Steam.LaunchOptionsPath, "x %command%");
            Assert.Equal("x %command%", Steam.ReadLaunchOptions(after));
            Assert.NotNull(Vdf.Parse(after).Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "999", "LastPlayed"));
        }

        [Fact]
        public void Refuses_a_file_without_the_root_block()
        {
            Assert.Throws<FormatException>(() => Vdf.SetString("\"other\"\n{\n}\n", Steam.LaunchOptionsPath, "x"));
        }
    }

    public class LaunchOptionsTests
    {
        private const string Exe = @"C:\Users\player\AppData\Roaming\Pulsar\Legacy.exe";

        [Theory]
        [InlineData("", "\"" + Exe + "\" %command%")]
        [InlineData("   ", "\"" + Exe + "\" %command%")]
        [InlineData("-skipintro", "\"" + Exe + "\" %command% -skipintro")]
        [InlineData("%command% -skipintro", "\"" + Exe + "\" %command% -skipintro")]
        [InlineData("\"C:\\Users\\player\\AppData\\Roaming\\Pulsar\\Interim.exe\" %command% -sources",
                    "\"" + Exe + "\" %command% -sources")]
        [InlineData("C:\\Users\\player\\AppData\\Roaming\\Pulsar\\Modern.exe %command%",
                    "\"" + Exe + "\" %command%")]
        public void Puts_Legacy_in_front_and_keeps_every_argument(string before, string after)
        {
            Assert.Equal(after, LaunchOptions.WithLauncher(before, Exe));
        }

        [Fact]
        public void Leaves_options_that_already_name_this_Legacy_alone()
        {
            string raw = "\"" + Exe + "\" %command% -sources -noSplash";
            Assert.Same(raw, LaunchOptions.WithLauncher(raw, Exe));
        }

        [Fact]
        public void Refuses_to_put_Pulsar_behind_another_wrapper()
        {
            Assert.Throws<InvalidOperationException>(() => LaunchOptions.WithLauncher("C:\\tools\\overlay.exe %command%", Exe));
        }

        [Fact]
        public void Reads_the_flags_that_move_Pulsars_data()
        {
            LaunchOptionsInfo i = LaunchOptions.Parse("\"D:\\Pulsar\\Interim.exe\" %command% -USEHOME -profile \"My Mods\"");
            Assert.Equal(PulsarLauncher.Interim, i.Launcher);
            Assert.Equal("D:\\Pulsar\\Interim.exe", i.PulsarExe);
            Assert.True(i.UseHome);
            Assert.Equal("My Mods", i.Profile);
        }
    }

    public class PulsarConfigTests
    {
        private static SourcesConfig ExistingSources(string file = "FactionsUI.xml", bool withHub = false)
        {
            var s = new SourcesConfig
            {
                RemoteHubSources = new[]
                {
                    new RemoteHubConfig { Name = "PluginHub", Repo = "StarCpt/PluginHub", Branch = "main", Enabled = true, Trusted = true, Hash = "abc", LastCheck = new DateTime(2026, 11, 20) },
                },
                ModSources = new[] { new ModConfig { Name = "Some mod", ID = 123456789, Enabled = true } },
                LocalPluginSources = new[] { new LocalPluginConfig { Name = "Mine", Folder = "C:\\dev", File = "Mine.xml", Enabled = true } },
            };
            if (file != null)
            {
                s.RemotePluginSources = new[]
                {
                    new RemotePluginConfig { Name = "Other", Repo = "someone/Other", Branch = "main", File = "Other.xml", Enabled = true },
                    new RemotePluginConfig { Name = "Factions", Repo = "Arkil81/Factions", Branch = "main", File = file, Enabled = true, LastCheck = new DateTime(2026, 11, 24, 8, 0, 0, DateTimeKind.Utc) },
                };
            }
            if (withHub)
                s.RemoteHubSources = s.RemoteHubSources.Concat(new[] { new RemoteHubConfig { Name = "Factions", Repo = "Arkil81/Factions", Branch = "main", Enabled = true } }).ToArray();
            return s;
        }

        private static RemotePluginConfig Ours(SourcesConfig s) =>
            Assert.Single(s.RemotePluginSources, p => p.Repo == "Arkil81/Factions");

        [Fact]
        public void A_new_sources_file_keeps_the_official_hub()
        {
            ConfigEdit e = PulsarConfig.AddSource(null);
            SourcesConfig s = PulsarXml.Read<SourcesConfig>(e.Text);
            RemoteHubConfig hub = Assert.Single(s.RemoteHubSources);
            Assert.Equal("StarCpt/PluginHub", hub.Repo);
            Assert.True(hub.Enabled);
            Assert.True(hub.Trusted);
            RemotePluginConfig p = Ours(s);
            Assert.Equal("main", p.Branch);
            Assert.Equal("FactionsUI.xml", p.File);
            Assert.True(p.Enabled);
            Assert.False(p.Trusted);
            Assert.Null(p.LastCheck);
            Assert.True(s.ShowWarning);
            Assert.Equal(2, s.MaxSourceAge);
        }

        [Fact]
        public void Adding_to_a_Pulsar_written_file_keeps_everything_else()
        {
            var before = ExistingSources(file: null);
            ConfigEdit e = PulsarConfig.AddSource(PulsarXml.Write(before));
            Assert.True(e.Changed);
            SourcesConfig s = PulsarXml.Read<SourcesConfig>(e.Text);
            Assert.Equal("abc", Assert.Single(s.RemoteHubSources).Hash);
            Assert.Equal(123456789, Assert.Single(s.ModSources).ID);
            Assert.Equal("C:\\dev", Assert.Single(s.LocalPluginSources).Folder);
            Assert.Equal("FactionsUI.xml", Ours(s).File);
        }

        [Fact]
        public void The_auto_filled_PluginHub_xml_is_repaired_and_refetched()
        {
            ConfigEdit e = PulsarConfig.AddSource(PulsarXml.Write(ExistingSources(file: "PluginHub.xml")));
            Assert.True(e.Changed);
            SourcesConfig s = PulsarXml.Read<SourcesConfig>(e.Text);
            RemotePluginConfig p = Ours(s);
            Assert.Equal("FactionsUI.xml", p.File);
            Assert.Null(p.LastCheck);
            Assert.Equal("Other.xml", s.RemotePluginSources.Single(x => x.Repo == "someone/Other").File);
        }

        [Fact]
        public void A_correct_source_is_not_rewritten()
        {
            string before = PulsarXml.Write(ExistingSources());
            ConfigEdit e = PulsarConfig.AddSource(before);
            Assert.False(e.Changed);
            Assert.Same(before, e.Text);
        }

        [Fact]
        public void A_hub_entry_for_this_repository_is_removed()
        {
            ConfigEdit e = PulsarConfig.AddSource(PulsarXml.Write(ExistingSources(withHub: true)));
            SourcesConfig s = PulsarXml.Read<SourcesConfig>(e.Text);
            Assert.Equal("StarCpt/PluginHub", Assert.Single(s.RemoteHubSources).Repo);
        }

        [Fact]
        public void Adding_twice_changes_nothing_the_second_time()
        {
            ConfigEdit first = PulsarConfig.AddSource(PulsarXml.Write(ExistingSources(file: null)));
            Assert.False(PulsarConfig.AddSource(first.Text).Changed);
        }

        [Fact]
        public void A_new_profile_is_one_Pulsar_accepts()
        {
            ConfigEdit e = PulsarConfig.EnablePlugin(null);
            Profile p = PulsarXml.Read<Profile>(e.Text);
            Assert.True(p.Validate());
            Assert.Equal("Current", p.Name);
            Assert.Equal(FactionsUi.PluginId, Assert.Single(p.GitHub).Id);
        }

        [Fact]
        public void Enabling_keeps_every_other_plugin_and_mod()
        {
            var before = new Profile("Current");
            before.GitHub.Add(new GitHubPluginConfig { Id = "other-plugin", SelectedVersion = "beta" });
            before.DevFolder.Add(new LocalFolderConfig { Id = "dev-plugin", DebugBuild = false });
            before.Local.Add("C:\\plugins\\x.dll");
            before.Mods.Add(987654321UL);

            ConfigEdit e = PulsarConfig.EnablePlugin(PulsarXml.Write(before));
            Profile p = PulsarXml.Read<Profile>(e.Text);
            Assert.True(p.Validate());
            Assert.Equal(new[] { "other-plugin", FactionsUi.PluginId }.OrderBy(x => x), p.GitHub.Select(g => g.Id).OrderBy(x => x));
            Assert.Equal("beta", p.GitHub.Single(g => g.Id == "other-plugin").SelectedVersion);
            Assert.False(Assert.Single(p.DevFolder).DebugBuild);
            Assert.Equal("C:\\plugins\\x.dll", Assert.Single(p.Local));
            Assert.Equal(987654321UL, Assert.Single(p.Mods));
            Assert.False(PulsarConfig.EnablePlugin(e.Text).Changed);
        }

        [Fact]
        public void An_unreadable_profile_is_refused_not_rewritten()
        {
            Assert.Throws<ConfigException>(() => PulsarConfig.EnablePlugin("<Profile><Name>Current</Name>"));
            Assert.Throws<ConfigException>(() => PulsarConfig.EnablePlugin("<SourcesConfig />"));
            Assert.Throws<ConfigException>(() => PulsarConfig.EnablePlugin("<Profile><GitHub /></Profile>"));
        }

        [Fact]
        public void Every_profile_list_is_written_even_when_absent()
        {
            // The net9 serializer creates a missing collection itself, so Validate cannot see this;
            // the element is asserted directly.
            foreach (string input in new[] { null, "<Profile><Name>Current</Name><GitHub /></Profile>" })
            {
                string text = PulsarConfig.EnablePlugin(input).Text;
                foreach (string list in new[] { "GitHub", "DevFolder", "Local", "Mods" })
                    Assert.Contains("<" + list, text);
                Assert.True(PulsarXml.Read<Profile>(text).Validate());
            }
        }

        [Fact]
        public void Removing_undoes_adding()
        {
            var before = new Profile("Current");
            before.GitHub.Add(new GitHubPluginConfig { Id = "other-plugin" });
            string enabled = PulsarConfig.EnablePlugin(PulsarXml.Write(before)).Text;
            Profile p = PulsarXml.Read<Profile>(PulsarConfig.DisablePlugin(enabled).Text);
            Assert.True(p.Validate());
            Assert.Equal("other-plugin", Assert.Single(p.GitHub).Id);

            string sources = PulsarConfig.AddSource(PulsarXml.Write(ExistingSources(withHub: true))).Text;
            SourcesConfig s = PulsarXml.Read<SourcesConfig>(PulsarConfig.RemoveSource(sources).Text);
            Assert.DoesNotContain(s.RemotePluginSources, x => x.Repo == "Arkil81/Factions");
            Assert.Single(s.RemotePluginSources);
        }
    }

    public class PlannerTests
    {
        private static readonly string DefaultPulsar = @"C:\Users\player\AppData\Roaming\Pulsar";
        private static readonly string DefaultExe = DefaultPulsar + @"\Legacy.exe";

        [Fact]
        public void A_machine_without_Pulsar_gets_everything()
        {
            FakeHost h = Sample.Machine(null);
            Plan plan = Planner.PlanInstall(h);
            Assert.Null(plan.Error);
            Assert.True(plan.InstallPulsar);
            Assert.Equal(DefaultExe, plan.PulsarExe);
            Assert.Equal("\"" + DefaultExe + "\" %command%", Steam.ReadLaunchOptions(plan.LaunchOptions.After));
            Assert.Equal(DefaultPulsar + @"\Legacy\Sources\sources.xml", plan.Sources.Path);
            Assert.Equal(DefaultPulsar + @"\Legacy\Profiles\Current.xml", plan.Profile.Path);
            Assert.Null(plan.Sources.Before);
            Assert.Equal("Player", plan.SteamAccount);
        }

        [Fact]
        public void Applying_a_plan_leaves_nothing_to_do()
        {
            FakeHost h = Sample.Machine("-skipintro");
            h.Apply(Planner.PlanInstall(h));
            Plan again = Planner.PlanInstall(h);
            Assert.Null(again.Error);
            Assert.True(again.NothingToDo, string.Join("; ", again.Files.SelectMany(f => f.Changes)));
            Assert.Equal("\"" + DefaultExe + "\" %command% -skipintro", Steam.ReadLaunchOptions(h.Files[Sample.LocalConfig]));
        }

        [Fact]
        public void Interim_is_switched_to_Legacy_in_the_same_Pulsar()
        {
            FakeHost h = Sample.Machine("\"D:\\Games\\Pulsar\\Interim.exe\" %command% -sources");
            Sample.InstallPulsar(h, @"D:\Games\Pulsar");
            Plan plan = Planner.PlanInstall(h);
            Assert.Null(plan.Error);
            Assert.False(plan.InstallPulsar);
            Assert.Equal("\"D:\\Games\\Pulsar\\Legacy.exe\" %command% -sources", Steam.ReadLaunchOptions(plan.LaunchOptions.After));
            Assert.Equal(@"D:\Games\Pulsar\Legacy\Sources\sources.xml", plan.Sources.Path);
        }

        [Fact]
        public void A_correct_launch_line_is_not_touched()
        {
            FakeHost h = Sample.Machine("\"" + DefaultExe + "\" %command%");
            Sample.InstallPulsar(h, DefaultPulsar);
            Plan plan = Planner.PlanInstall(h);
            Assert.Null(plan.LaunchOptions);
            Assert.NotNull(plan.Sources);
        }

        [Fact]
        public void UseHome_moves_the_data_folder_to_AppData()
        {
            FakeHost h = Sample.Machine("\"D:\\Pulsar\\Legacy.exe\" %command% -useHome");
            Sample.InstallPulsar(h, @"D:\Pulsar");
            Plan plan = Planner.PlanInstall(h);
            Assert.Equal(DefaultPulsar + @"\Legacy\Profiles\Current.xml", plan.Profile.Path);
        }

        [Fact]
        public void Pulsar_missing_from_the_named_folder_is_installed_at_the_default()
        {
            FakeHost h = Sample.Machine("\"E:\\gone\\Legacy.exe\" %command%");
            Plan plan = Planner.PlanInstall(h);
            Assert.True(plan.InstallPulsar);
            Assert.Equal("\"" + DefaultExe + "\" %command%", Steam.ReadLaunchOptions(plan.LaunchOptions.After));
        }

        [Fact]
        public void A_default_folder_holding_other_files_is_not_used()
        {
            FakeHost h = Sample.Machine(null);
            h.Write(DefaultPulsar + @"\notes.txt", "mine");
            Plan plan = Planner.PlanInstall(h);
            Assert.NotNull(plan.Error);
            Assert.Empty(plan.Files);
        }

        [Fact]
        public void An_unknown_Pulsar_major_version_stops_the_install()
        {
            FakeHost h = Sample.Machine("\"" + DefaultExe + "\" %command%");
            Sample.InstallPulsar(h, DefaultPulsar, new Version(3, 0, 0));
            Assert.Contains("version 3.0.0", Planner.PlanInstall(h).Error);
        }

        [Fact]
        public void A_profile_override_stops_the_install()
        {
            FakeHost h = Sample.Machine("\"" + DefaultExe + "\" %command% -profile Modded");
            Sample.InstallPulsar(h, DefaultPulsar);
            Assert.Contains("-profile Modded", Planner.PlanInstall(h).Error);
        }

        [Fact]
        public void No_Steam_or_no_game_is_an_error()
        {
            Assert.NotNull(Planner.PlanInstall(new FakeHost()).Error);
            FakeHost h = Sample.Machine(null);
            h.Files.Remove(@"D:\SteamLibrary\steamapps\appmanifest_244850.acf");
            Assert.Contains("not installed in Steam", Planner.PlanInstall(h).Error);
        }

        [Fact]
        public void Remove_takes_out_only_Factions_UI()
        {
            FakeHost h = Sample.Machine(null);
            h.Apply(Planner.PlanInstall(h));
            string launchLine = h.Files[Sample.LocalConfig];
            Plan remove = Planner.PlanRemove(h);
            Assert.Null(remove.Error);
            Assert.Null(remove.LaunchOptions);
            h.Apply(remove);
            Assert.False(PulsarConfig.IsEnabled(h.Files[remove.Profile.Path]));
            Assert.Equal(launchLine, h.Files[Sample.LocalConfig]);
            Assert.True(Planner.PlanRemove(h).NothingToDo);
        }
    }
}
