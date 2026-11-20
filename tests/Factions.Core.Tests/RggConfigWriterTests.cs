using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-146: the staged Real Gas Giants config is meant to be a merge onto the live one, and the
    /// operator is told to copy it over that file. Against a config RGG itself wrote it was always the
    /// fresh-document fallback, which dropped nine global settings. The fixture is the live
    /// <c>Config.xml</c> from the test server, byte for byte: single-byte UTF-8, no byte-order mark,
    /// under an <c>encoding="utf-16"</c> declaration.
    /// </summary>
    public sealed class RggConfigWriterTests : IDisposable
    {
        private const string Cauldron = "Cauldron-1225884968d19000";

        private static readonly string[] Globals =
        {
            "AllowChatCommands", "GasGiantBlocksSun", "GasGiantAirDensity", "GasGiantWindSpeed",
            "SetGlobalNoAsteroids", "CanSpawnOnGasGiants", "EnableGasGiantWindTurbines",
            "UseLensFlareOccluder", "RingParticleLevel",
        };

        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "factions-fac146-" + Guid.NewGuid().ToString("N"));

        private string SavePath { get { return Path.Combine(_root, "save"); } }
        private string StagingDir { get { return Path.Combine(_root, "staging"); } }
        private string LivePath
        {
            get
            {
                return Path.Combine(SavePath, "Storage", RggConfigWriter.DefaultStorageFolder,
                    RggConfigWriter.ConfigFileName);
            }
        }
        private string StagingPath { get { return Path.Combine(StagingDir, RggConfigWriter.StagingFileName); } }

        private static string FixturePath
        {
            get { return Path.Combine(AppContext.BaseDirectory, "Fixtures", "rgg_config_live.xml"); }
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private void PlaceLive(byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LivePath));
            File.WriteAllBytes(LivePath, bytes);
        }

        private static WorldPlan CauldronPlan()
        {
            var gg = new GasGiantSpec();
            gg.Rings.Add(new GasGiantRingSpec());
            var plan = new WorldPlan();
            plan.Planets.Add(new PlannedPlanet { Name = "Cauldron", StorageName = Cauldron, GasGiant = gg });
            return plan;
        }

        private RggConfigWriter.Result Stage()
        {
            return RggConfigWriter.Write(CauldronPlan(), SavePath, null, StagingDir);
        }

        [Fact]
        public void Fixture_is_the_file_XDocument_Load_refuses()
        {
            // The precondition the rest of this class depends on. If this stops throwing, the fixture
            // is no longer RGG's format and the merge tests below prove nothing about it.
            byte[] head = File.ReadAllBytes(FixturePath).Take(5).ToArray();
            Assert.Equal(new byte[] { 0x3C, 0x3F, 0x78, 0x6D, 0x6C }, head);
            Assert.Throws<XmlException>(() => XDocument.Load(FixturePath));
        }

        [Fact]
        public void Staging_merges_onto_RGGs_own_file_and_keeps_every_global()
        {
            PlaceLive(File.ReadAllBytes(FixturePath));
            XElement live = XDocument.Parse(File.ReadAllText(FixturePath)).Root;

            RggConfigWriter.Result r = Stage();

            Assert.True(r.Ok, r.Failure);
            Assert.Equal(1, r.Written);
            Assert.Equal(1, r.Replaced);

            XElement staged = XDocument.Load(StagingPath).Root;
            foreach (string name in Globals)
            {
                Assert.NotNull(live.Element(name));
                XElement e = staged.Element(name);
                Assert.True(e != null, name + " was dropped from the staged config");
                Assert.Equal(live.Element(name).Value, e.Value);
            }
            Assert.NotNull(staged.Attribute(XNamespace.Xmlns + "xsi"));
            Assert.NotNull(staged.Attribute(XNamespace.Xmlns + "xsd"));
            Assert.Equal("true", staged.Element("OverrideFromConfig").Value);
            Assert.NotNull(staged.Element("PlanetRingInfoList"));
            Assert.Single(staged.Element("GasGiantInfoList").Elements("GasGiantConfigInfo")
                .Where(e => (string)e.Attribute("PlanetName") == Cauldron));
        }

        [Fact]
        public void Staged_file_is_utf8_with_a_matching_declaration()
        {
            // A parsed RGG document carries encoding="utf-16", and XDocument.Save follows the
            // declaration; left alone the staged file comes out UTF-16LE, not the UTF-8 the FAC-087
            // procedure was verified with.
            PlaceLive(File.ReadAllBytes(FixturePath));

            Assert.True(Stage().Ok);

            byte[] head = File.ReadAllBytes(StagingPath).Take(3).ToArray();
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, head);
            Assert.Equal("utf-8", XDocument.Load(StagingPath).Declaration.Encoding);
        }

        [Fact]
        public void An_unreadable_existing_config_fails_and_stages_nothing()
        {
            PlaceLive(new byte[] { 0x3C, 0x52, 0x65, 0x61, 0x6C });   // "<Real", truncated

            RggConfigWriter.Result r = Stage();

            Assert.False(r.Ok);
            Assert.True(r.Failure.IndexOf(LivePath, StringComparison.Ordinal) >= 0, r.Failure);
            Assert.False(File.Exists(StagingPath));
        }

        [Fact]
        public void No_existing_config_stages_a_fresh_one()
        {
            RggConfigWriter.Result r = Stage();

            Assert.True(r.Ok, r.Failure);
            Assert.Equal(1, r.Written);
            Assert.Equal(0, r.Replaced);
            XElement staged = XDocument.Load(StagingPath).Root;
            Assert.Equal("RealGasGiantsSettingsConfig", staged.Name.LocalName);
            Assert.Equal("true", staged.Element("OverrideFromConfig").Value);
        }
    }
}
