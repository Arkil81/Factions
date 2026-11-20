using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Factions.Core
{
    /// <summary>
    /// Writes gas-giant entries into Real Gas Giants' own world-storage config.
    ///
    /// <para>RGG does not create planets — it decorates one whose <c>StorageName</c> matches a
    /// <c>GasGiantConfigInfo.PlanetName</c> in this file. Without a matching entry a body on its
    /// generator still becomes a gas giant, but with bare defaults: no ring, no skin.</para>
    ///
    /// <para><b>This writes a staging file, not the live config, and that is the whole point.</b>
    /// RGG rewrites <c>Config.xml</c> from its in-memory state on every save — including the save
    /// that runs as the server shuts down — so a file written while the server is up is destroyed on
    /// the way down and is never the copy the next load reads. Writing the live path from in here
    /// would produce a file that looks correct and does nothing.</para>
    ///
    /// <para>The staging file goes in Factions' own instance directory, which RGG never touches. The
    /// operator copies it over <c>Config.xml</c> <b>while the server is stopped</b>, then starts:
    /// <c>SaveData()</c> only fires on a save, so a file written between shutdown and startup is the
    /// one <c>LoadData()</c> reads. It logs
    /// <c>RGG ReadFromSaveFile SKIP (OverrideFromConfig)</c>, and the first save afterwards writes
    /// the live component's settings back into both the file and the save variable — one-shot, then
    /// permanent. Verified live 2026-08-25; see docs/issues/FAC-087.</para>
    ///
    /// <para>A world builder writing a mod's setup file is per-mod by definition. That is deliberate
    /// and scoped to world creation — the "no per-mod integration layers" rule governs runtime
    /// behaviour (operator, 2026-08-24).</para>
    ///
    /// <para><b>RGG's own file cannot be read by <c>XDocument.Load</c>.</b> It declares
    /// <c>encoding="utf-16"</c> over single-byte UTF-8 with no byte-order mark, and the reader honours
    /// the declaration and refuses. It is read as text instead, and an existing file that still cannot be
    /// parsed fails the staging rather than becoming a fresh document: the operator is told to copy the
    /// result over their own config, so a fresh one would silently reset every global setting in it.
    /// See docs/issues/FAC-146.</para>
    /// </summary>
    public static class RggConfigWriter
    {
        public const string DefaultStorageFolder = "3232085677.sbm_RealGasGiants";
        public const string ConfigFileName = "Config.xml";

        /// <summary>Where the merged config is staged. Named so it is obvious what it is for and
        /// obvious that it is not RGG's own file.</summary>
        public const string StagingFileName = "rgg_gasgiants.staged.xml";

        public sealed class Result
        {
            /// <summary>Where the merged config was written — Factions' own directory.</summary>
            public string StagingPath;

            /// <summary>Where it has to end up, by hand, with the server stopped.</summary>
            public string LivePath;

            public int Written;
            public int Replaced;
            public string Failure;
            public bool Ok { get { return string.IsNullOrEmpty(Failure); } }
        }

        /// <summary>
        /// Merges an entry per gas giant into a copy of RGG's config, preserving every other setting
        /// in the live file, and writes the result to the staging path for the operator to place
        /// while the server is stopped.
        /// </summary>
        public static Result Write(WorldPlan plan, string savePath, string storageFolder, string stagingDir)
        {
            var result = new Result();
            var giants = new List<PlannedPlanet>();
            if (plan != null)
                foreach (PlannedPlanet pp in plan.Planets)
                    if (pp.GasGiant != null) giants.Add(pp);
            if (giants.Count == 0) { result.Failure = null; return result; }

            if (string.IsNullOrEmpty(savePath)) { result.Failure = "no save path."; return result; }
            if (string.IsNullOrEmpty(stagingDir)) { result.Failure = "no staging directory."; return result; }
            string dir = Path.Combine(Path.Combine(savePath, "Storage"),
                string.IsNullOrEmpty(storageFolder) ? DefaultStorageFolder : storageFolder);
            result.LivePath = Path.Combine(dir, ConfigFileName);
            result.StagingPath = Path.Combine(stagingDir, StagingFileName);

            try
            {
                // Merge onto whatever RGG currently holds, so the operator's own settings survive.
                XDocument doc;
                string readFailure;
                if (!TryLoad(result.LivePath, out doc, out readFailure))
                {
                    result.Failure = readFailure;
                    return result;
                }
                XElement root = doc.Root;

                // Without this the loader reads the file and then overwrites it from the save's own
                // copy. RGG clears the flag itself after one use, so it does not stay latched.
                Set(root, "OverrideFromConfig", "true");

                XElement list = root.Element("GasGiantInfoList");
                if (list == null) { list = new XElement("GasGiantInfoList"); root.Add(list); }

                foreach (PlannedPlanet pp in giants)
                {
                    List<XElement> existing = list.Elements("GasGiantConfigInfo")
                        .Where(e => (string)e.Attribute("PlanetName") == pp.StorageName).ToList();
                    foreach (XElement stale in existing) { stale.Remove(); result.Replaced++; }
                    list.Add(Entry(pp));
                    result.Written++;
                }

                Directory.CreateDirectory(stagingDir);
                string temp = result.StagingPath + ".tmp";
                doc.Save(temp);
                if (File.Exists(result.StagingPath)) File.Replace(temp, result.StagingPath, null);
                else File.Move(temp, result.StagingPath);
            }
            catch (Exception ex)
            {
                result.Failure = ex.GetType().Name + ": " + ex.Message;
            }
            return result;
        }

        /// <summary>The live config to merge onto, or a fresh document when there is none yet. False,
        /// with nothing to merge onto, when a file exists and cannot be parsed.</summary>
        private static bool TryLoad(string path, out XDocument doc, out string failure)
        {
            doc = null;
            failure = null;
            if (File.Exists(path))
            {
                try
                {
                    // Parsed from text so the reader never acts on RGG's declaration: File.ReadAllText
                    // sniffs a byte-order mark and otherwise decodes UTF-8, which is what RGG's bytes are.
                    doc = XDocument.Parse(File.ReadAllText(path));
                }
                catch (Exception ex)
                {
                    failure = "cannot read " + path + " (" + ex.GetType().Name + ": " + ex.Message
                            + "); nothing staged, because a fresh config copied over it would reset every"
                            + " other setting it holds.";
                    return false;
                }
                if (doc.Root == null)
                {
                    failure = "cannot read " + path + " (no root element); nothing staged.";
                    return false;
                }
            }
            else
            {
                // Field order follows RGG's own serializer so a human diff against a mod-written file
                // stays readable.
                doc = new XDocument(new XElement("RealGasGiantsSettingsConfig",
                    new XElement("OverrideFromConfig", "false"),
                    new XElement("GasGiantInfoList"),
                    new XElement("PlanetRingInfoList")));
            }

            // XDocument.Save takes its encoding from the declaration, so a parsed RGG file would be
            // written back as UTF-16. The staging file stays UTF-8, the format FAC-087's stop/copy/start
            // was verified with.
            doc.Declaration = new XDeclaration("1.0", "utf-8", null);
            return true;
        }

        private static void Set(XElement root, string name, string value)
        {
            XElement e = root.Element(name);
            if (e == null) root.Add(new XElement(name, value));
            else e.Value = value;
        }

        private static XElement Entry(PlannedPlanet pp)
        {
            GasGiantSpec g = pp.GasGiant;
            var entry = new XElement("GasGiantConfigInfo",
                new XAttribute("PlanetName", pp.StorageName),
                new XElement("PlanetCustomName", pp.Name),
                new XElement("Radius", Num(g.RadiusKm)),
                Colour("PlanetColor", g.Color),
                new XElement("PlanetSkin", g.Skin ?? "Default1"),
                new XElement("OxygenDensity", Num(g.OxygenDensity)));

            var rings = new XElement("RingsInfo");
            if (g.Rings != null)
                foreach (GasGiantRingSpec r in g.Rings)
                {
                    if (r == null) continue;
                    rings.Add(new XElement("RingConfigInfo",
                        new XElement("HasRing", "true"),
                        new XElement("RingSkin", r.Skin ?? "Default1"),
                        Colour("RingNormal", r.Normal),
                        Colour("RingColor", r.Color),
                        new XElement("RingLightMult", Num(r.LightMult)),
                        new XElement("RingShadowMult", Num(r.ShadowMult)),
                        new XElement("RingInnerScale", Num(r.RggInnerScale(g.RadiusKm))),
                        new XElement("RingOuterScale", Num(r.RggOuterScale(g.RadiusKm))),
                        new XElement("RingLayerSpacingScale", Num(r.LayerSpacingScale)),
                        new XElement("RingRotationPeriodSeconds", Num(r.RotationPeriodSeconds)),
                        new XElement("ConstrainNearbyAsteroidsToRing", Bool(r.ConstrainNearbyAsteroidsToRing)),
                        new XElement("ShadowOnRingEnabled", Bool(r.ShadowOnRing)),
                        new XElement("CollectRingResources", Bool(r.CollectResources)),
                        new XElement("CollectResourceRingSubtypeId", r.CollectResourceSubTypeId ?? "Ice"),
                        new XElement("CollectResourceRingAmount", r.CollectResourceAmount.ToString(CultureInfo.InvariantCulture)),
                        new XElement("EnabledDraw", Bool(r.EnabledDraw)),
                        new XElement("EnabledParticle", Bool(r.EnabledParticle))));
                }
            entry.Add(rings);

            entry.Add(new XElement("InteriorInfo",
                new XElement("AsteroidRemoval", Bool(g.AsteroidRemoval)),
                new XElement("PressureDamagePlayers", Bool(g.PressureDamagePlayers)),
                new XElement("PressureDamageGrids", Bool(g.PressureDamageGrids))));

            entry.Add(new XElement("ResourceInfo",
                new XElement("CollectPlanetResources", Bool(g.CollectPlanetResources)),
                new XElement("CollectResourceUpperSubtypeId", g.CollectResourceUpperSubTypeId ?? "Ice"),
                new XElement("CollectResourceUpperAmount", g.CollectResourceUpperAmount.ToString(CultureInfo.InvariantCulture)),
                new XElement("CollectResourceLowerSubtypeId", g.CollectResourceLowerSubTypeId ?? "Ice"),
                new XElement("CollectResourceLowerAmount", g.CollectResourceLowerAmount.ToString(CultureInfo.InvariantCulture))));

            entry.Add(new XElement("GravityStrength", Num(g.GravityStrength)));
            entry.Add(new XElement("GravityFalloff", Num(g.GravityFalloff)));
            entry.Add(new XElement("DayLengthSeconds", Num(g.DayLengthSeconds)));
            entry.Add(new XElement("SpinCounterClockwise", Bool(g.SpinCounterClockwise)));
            return entry;
        }

        private static XElement Colour(string name, Vec3 v)
        {
            return new XElement(name,
                new XAttribute("X", Num(v.X)), new XAttribute("Y", Num(v.Y)), new XAttribute("Z", Num(v.Z)));
        }

        // Invariant culture throughout: this file is read by a mod's XML deserializer, not by a human
        // in a locale.
        private static string Num(double d) { return d.ToString("0.######", CultureInfo.InvariantCulture); }
        private static string Bool(bool b) { return b ? "true" : "false"; }
    }
}
