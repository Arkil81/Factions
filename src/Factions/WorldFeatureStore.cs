using System.Collections.Generic;
using System.IO;
using Factions.Core;

namespace Factions
{
    /// <summary>Reads the authored world-feature file from disk. Parsing, validation and every
    /// decision about what a malformed file means live in <see cref="WorldFeatureFile"/> (Core, so
    /// they are unit-testable); this only resolves the path and handles the file being absent.
    /// <para><b>The shipped default is blank.</b> No file means deep space holds no minerals, which
    /// is the intended end state rather than a degraded one. See docs/ore-geography.md, and
    /// <c>instance/world_features.example.json</c> for the schema.</para></summary>
    internal static class WorldFeatureStore
    {
        public const string FileName = WorldFeatureFile.FileName;

        public static List<WorldFeature> Load(string path, out string status)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                status = FileName + ": absent -- deep space holds no minerals. This is the shipped default.";
                return new List<WorldFeature>();
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (System.Exception ex)
            {
                status = FileName + ": could not be read, treating deep space as empty -- " + ex.Message;
                return new List<WorldFeature>();
            }

            return WorldFeatureFile.Parse(json, out status);
        }
    }
}
