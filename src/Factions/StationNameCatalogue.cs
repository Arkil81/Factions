using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Factions
{
    /// <summary>
    /// Host-side, provenance-preserving station naming catalogue. Parses the current installed
    /// FactionNames.sbc and merges repository additions before writing the runtime catalogue.
    /// </summary>
    public sealed class StationNameCatalogue
    {
        /// <summary>
        /// A single station name with its provenance (vanilla or custom).
        /// </summary>
        [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
        public sealed class CatalogueName
        {
            /// <summary>The display name for a station (e.g. "Adams Relay").</summary>
            [JsonProperty(Required = Required.Always)]
            public string Name { get; set; }

            /// <summary>"vanilla" = from FactionNames.sbc, "custom" = from NameAdditions.</summary>
            [JsonProperty(Required = Required.Always)]
            public string Source { get; set; }

            /// <summary>
            /// For custom names: "stem", "suffix", or "possessiveSuffix" indicating the component type.
            /// Null for vanilla names.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string ComponentType { get; set; }

            /// <summary>
            /// For custom suffix/possessiveSuffix: the base stem it should combine with (e.g., "Command").
            /// Null for stems or vanilla names.
            /// </summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string BaseStemHint { get; set; }
        }

        /// <summary>
        /// Names for a single personality-type combination (e.g., Miner + Outposts).
        /// </summary>
        [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
        public sealed class CatalogueEntry
        {
            /// <summary>The personality ID: "miner", "mercantile", "settler", "raider", "military", etc.</summary>
            [JsonProperty(Required = Required.Always)]
            public string PersonalityId { get; set; }

            /// <summary>The station type: "Outposts", "OrbitalStations", or "SpaceStations".</summary>
            [JsonProperty(Required = Required.Always)]
            public string StationType { get; set; }

            /// <summary>All available names for this personality-type, with provenance metadata.</summary>
            [JsonProperty(Required = Required.Always)]
            public List<CatalogueName> Names { get; set; } = new List<CatalogueName>();
        }

        /// <summary>
        /// The complete master catalogue: a list of entries, one per personality-type combination.
        /// </summary>
        [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
        public sealed class MasterCatalogue
        {
            /// <summary>When this catalogue was generated (ISO 8601 UTC).</summary>
            [JsonProperty(Required = Required.Always)]
            public string GeneratedUtc { get; set; }

            /// <summary>Hash or version of the source FactionNames.sbc used to build this.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string SourceHash { get; set; }

            /// <summary>All personality-type combinations.</summary>
            [JsonProperty(Required = Required.Always)]
            public List<CatalogueEntry> Entries { get; set; } = new List<CatalogueEntry>();
        }

        /// <summary>
        /// Schema for repository-tracked additions, scoped by personality and station category.
        /// Each category retains its structured stems, suffixes, and possessiveSuffixes separately.
        /// Possessive suffix values omit the apostrophe-s; generation formats them as <c>&lt;stem&gt;'s &lt;suffix&gt;</c>.
        /// </summary>
        [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
        public sealed class PersonalityNameAdditions
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<string> Stems { get; set; } = new List<string>();

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<string> Suffixes { get; set; } = new List<string>();

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<string> PossessiveSuffixes { get; set; } = new List<string>();
        }

        [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
        public sealed class NameAdditionsRoot
        {
            /// <summary>Personality ID → station category → structured custom vocabulary.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public Dictionary<string, Dictionary<string, PersonalityNameAdditions>> Personalities { get; set; }
                = new Dictionary<string, Dictionary<string, PersonalityNameAdditions>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Maps vanilla personality IDs to their station type name (as used in FactionNames.sbc).
        /// miner→Miner, mercantile→Trader, settler→Builder, raider→Pirate, default→Military.
        /// "derelict" (UNKN's naming category, see NeutralFaction) and "factorum" (FCTM) map to type
        /// names that deliberately match nothing in FactionNames.sbc, so their pools are 100% the
        /// custom vocabulary in station_name_additions.json, not vanilla names — abandonment-themed
        /// for derelict, registry-coded and utilitarian for factorum.
        /// </summary>
        private static readonly Dictionary<string, string> PersonalityToTypeName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "miner",     "Miner" },
                { "mercantile", "Trader" },
                { "settler",   "Builder" },
                { "raider",    "Pirate" },
                { "factorum",  "Factorum" },
                { "default",   "Military" },
                { "derelict",  "Derelict" }
            };

        /// <summary>
        /// All station type names used in FactionNames.sbc.
        /// </summary>
        private static readonly string[] AllStationTypes = new[] { "Outposts", "OrbitalStations", "SpaceStations" };

        /// <summary>
        /// Generates a master catalogue JSON from vanilla FactionNames and optional custom additions.
        ///
        /// Parses the vanilla sbc-format XML string and extracts station names by personality and type.
        /// If nameAdditionsJson is provided, merges custom stems/suffixes/possessiveSuffixes and includes
        /// them in the output with provenance metadata.
        /// </summary>
        /// <param name="vanillaFactionNamesXml">Content of FactionNames.sbc (XML string).</param>
        /// <param name="nameAdditionsJson">Optional JSON string with custom name additions (parsed as NameAdditionsRoot).</param>
        /// <returns>A MasterCatalogue JSON string (indented, newline-separated).</returns>
        public static string GenerateCatalogue(string vanillaFactionNamesXml, string nameAdditionsJson = null)
        {
            var entries = new List<CatalogueEntry>();

            // Parse additions (if provided).
            var additions = new Dictionary<string, Dictionary<string, PersonalityNameAdditions>>(
                StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(nameAdditionsJson))
            {
                try
                {
                    var root = JsonConvert.DeserializeObject<NameAdditionsRoot>(nameAdditionsJson);
                    if (root?.Personalities != null)
                        additions = root.Personalities;
                }
                catch
                {
                    // If parsing fails, continue with empty additions.
                }
            }

            // Parse vanilla FactionNames.sbc and extract personality-type mappings.
            // We use a simple XML parser (System.Xml) to find FactionName entries.
            var vanillaByPersonalityAndType = ExtractVanillaNames(vanillaFactionNamesXml);

            // Build catalogue entries for all personality-type combinations.
            foreach (var personalityId in PersonalityToTypeName.Keys)
            {
                string typeName = PersonalityToTypeName[personalityId];

                foreach (string stationType in AllStationTypes)
                {
                    var entry = new CatalogueEntry
                    {
                        PersonalityId = personalityId,
                        StationType = stationType
                    };

                    // Add vanilla names for this personality-type.
                    string key = typeName + "|" + stationType;
                    if (vanillaByPersonalityAndType.ContainsKey(key))
                    {
                        foreach (string vanillaName in vanillaByPersonalityAndType[key])
                        {
                            entry.Names.Add(new CatalogueName
                            {
                                Name = vanillaName,
                                Source = "vanilla"
                            });
                        }
                    }

                    // Add custom names scoped to this exact personality + station category.
                    Dictionary<string, PersonalityNameAdditions> byCategory;
                    PersonalityNameAdditions custom;
                    if (additions.TryGetValue(personalityId, out byCategory)
                        && byCategory != null
                        && byCategory.TryGetValue(stationType, out custom))
                    {
                        // Stems are standalone names.
                        if (custom.Stems != null)
                        {
                            foreach (string stem in custom.Stems)
                            {
                                if (!string.IsNullOrWhiteSpace(stem))
                                {
                                    entry.Names.Add(new CatalogueName
                                    {
                                        Name = NormalizeWhitespace(stem),
                                        Source = "customExplicit",
                                        ComponentType = "stem"
                                    });
                                }
                            }
                        }

                        // Suffixes should be combined with a stem.
                        if (custom.Suffixes != null && custom.Stems != null && custom.Stems.Count > 0)
                        {
                            foreach (string suffix in custom.Suffixes)
                            {
                                if (!string.IsNullOrWhiteSpace(suffix))
                                {
                                    // Create a combined name for each stem + suffix.
                                    foreach (string stem in custom.Stems)
                                    {
                                        entry.Names.Add(new CatalogueName
                                        {
                                            Name = NormalizeWhitespace(stem + " " + suffix),
                                            Source = "generated",
                                            ComponentType = "suffix",
                                            BaseStemHint = stem
                                        });
                                    }
                                }
                            }
                        }

                        // Possessive suffixes are also combined with stems.
                        if (custom.PossessiveSuffixes != null && custom.Stems != null && custom.Stems.Count > 0)
                        {
                            foreach (string posSuffix in custom.PossessiveSuffixes)
                            {
                                if (!string.IsNullOrWhiteSpace(posSuffix))
                                {
                                    foreach (string stem in custom.Stems)
                                    {
                                        entry.Names.Add(new CatalogueName
                                        {
                                            Name = NormalizeWhitespace(stem + "'s " + posSuffix),
                                            Source = "generated",
                                            ComponentType = "possessiveSuffix",
                                            BaseStemHint = stem
                                        });
                                    }
                                }
                            }
                        }
                    }

                    // Normalize/deduplicate names case-insensitively before publishing the pool.
                    DeduplicateNames(entry.Names);
                    if (entry.Names.Count > 0)
                        entries.Add(entry);
                }
            }

            var catalogue = new MasterCatalogue
            {
                GeneratedUtc = DateTime.UtcNow.ToString("O"),
                SourceHash = ComputeSha256(vanillaFactionNamesXml),
                Entries = entries
            };

            return JsonConvert.SerializeObject(catalogue, Formatting.Indented);
        }

        private static void DeduplicateNames(List<CatalogueName> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<CatalogueName>();
            foreach (CatalogueName name in names)
            {
                if (name == null) continue;
                name.Name = NormalizeWhitespace(name.Name);
                if (!string.IsNullOrEmpty(name.Name) && seen.Add(name.Name)) unique.Add(name);
            }
            names.Clear();
            names.AddRange(unique);
        }

        private static string NormalizeWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ComputeSha256(string content)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? string.Empty));
                var text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>
        /// Extracts vanilla station names from FactionNames.sbc XML.
        /// Returns a dictionary keyed by "TypeName|StationType" (e.g., "Miner|Outposts").
        /// </summary>
        private static Dictionary<string, List<string>> ExtractVanillaNames(string xml)
        {
            var result = new Dictionary<string, List<string>>();

            if (string.IsNullOrWhiteSpace(xml))
                return result;

            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);

                // Find all FactionName elements.
                var factionNames = doc.GetElementsByTagName("FactionName");
                foreach (System.Xml.XmlElement factionNode in factionNames)
                {
                    // Extract the faction Type (e.g., "Miner", "Trader", etc.).
                    var typeNode = factionNode.SelectSingleNode("Type");
                    if (typeNode == null) continue;

                    string factionType = typeNode.InnerText?.Trim();
                    if (string.IsNullOrEmpty(factionType)) continue;

                    // Extract all station names for this faction type (via StationTypeNames).
                    var stationTypeNamesNode = factionNode.SelectSingleNode("StationTypeNames");
                    if (stationTypeNamesNode != null)
                    {
                        var stationTypes = stationTypeNamesNode.SelectNodes("StationType");
                        foreach (System.Xml.XmlElement stationType in stationTypes)
                        {
                            // Get the station type ID (e.g., "Outposts", "OrbitalStations", "SpaceStations").
                            var typeIdNode = stationType.SelectSingleNode("StationTypeId") as System.Xml.XmlElement;
                            if (typeIdNode == null) continue;

                            string stationTypeId = typeIdNode.GetAttribute("Subtype");
                            if (string.IsNullOrEmpty(stationTypeId)) continue;

                            // Get all names for this station type.
                            var namesNode = stationType.SelectSingleNode("Names");
                            if (namesNode != null)
                            {
                                var strings = namesNode.SelectNodes("string");
                                string key = factionType + "|" + stationTypeId;
                                if (!result.ContainsKey(key))
                                    result[key] = new List<string>();

                                foreach (System.Xml.XmlElement str in strings)
                                {
                                    string name = str.InnerText?.Trim();
                                    if (!string.IsNullOrEmpty(name))
                                        result[key].Add(name);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently fail and return what was parsed so far.
            }

            return result;
        }
    }
}
