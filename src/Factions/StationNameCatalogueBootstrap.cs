using System;
using System.IO;
using Factions.Core;
using VRage.FileSystem;

namespace Factions
{
    /// <summary>
    /// Host-only startup bootstrap for station names. Reads the running server's
    /// Content/Data/FactionNames.sbc, merges it with the deployed repository-tracked
    /// station_name_additions.json, and writes the generated master catalogue into
    /// Instance/Factions (the plugin StorageDir).
    /// </summary>
    internal static class StationNameCatalogueBootstrap
    {
        internal const string AdditionsFileName = "station_name_additions.json";
        internal const string CatalogueFileName = "station_name_catalogue.json";

        private static void WriteAtomically(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }

        /// <param name="storageDir">Data directory — where the generated catalogue is written.</param>
        /// <param name="instanceDir">This instance's own storage. The deploy writes the additions
        /// file to the data directory, which every instance on the world shares; an instance copy
        /// is read only as a fallback, so a server whose data directory lacks one still gets its
        /// names rather than vanilla-only.</param>
        public static NameSelector GenerateAndLoad(string storageDir, string instanceDir = null)
        {
            if (string.IsNullOrEmpty(storageDir)) return null;

            try
            {
                string factionNamesPath = Path.Combine(MyFileSystem.ContentPath, "Data", "FactionNames.sbc");
                if (!File.Exists(factionNamesPath))
                {
                    FactionsPlugin.Log.Warn("Station naming disabled: FactionNames.sbc not found at " + factionNamesPath);
                    return null;
                }

                string vanillaXml = File.ReadAllText(factionNamesPath);
                bool additionsFromInstance;
                string additionsPath = Factions.Core.DataDirectory.ResolveInput(
                    AdditionsFileName, storageDir, instanceDir, File.Exists, out additionsFromInstance);
                // A stale data-directory copy silently shadows the one a deploy writes to the
                // instance (FAC-241); say so, since the catalogue below is built from whichever won.
                try
                {
                    string divergence = Factions.Core.DataDirectory.DescribeDivergence(
                        AdditionsFileName, storageDir, instanceDir);
                    if (divergence != null) FactionsPlugin.Log.Warn(divergence);
                    string read = Factions.Core.DataDirectory.DescribeRead(AdditionsFileName, additionsPath);
                    if (read != null) FactionsPlugin.Log.Info(read);
                }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(AdditionsFileName + ": could not compare the data-directory "
                        + "and instance copies (" + ex.Message + ").");
                }
                string additionsJson = File.Exists(additionsPath) ? File.ReadAllText(additionsPath) : null;
                if (additionsJson == null)
                    FactionsPlugin.Log.Warn("Station name additions not found at " + additionsPath
                        + "; generating from vanilla names only.");
                else if (additionsFromInstance)
                    FactionsPlugin.Log.Info(AdditionsFileName + " read from this instance ("
                        + additionsPath + "), not the data directory.");
                string catalogueJson = StationNameCatalogue.GenerateCatalogue(vanillaXml, additionsJson);
                string cataloguePath = Path.Combine(storageDir, CatalogueFileName);
                WriteAtomically(cataloguePath, catalogueJson);

                var selector = new NameSelector(cataloguePath, m => FactionsPlugin.Log.Warn(m));
                if (!selector.Initialize()) return null;

                FactionsPlugin.Log.Info("Station name catalogue generated from " + factionNamesPath
                    + " into " + cataloguePath + ".");
                return selector;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn("Station name catalogue startup failed: " + ex.Message);
                return null;
            }
        }
    }
}
