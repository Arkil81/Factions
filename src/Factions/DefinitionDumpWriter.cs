using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Sandbox.Definitions;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// Writes the <em>resolved</em> definition set — what <see cref="MyDefinitionManager"/> holds after
    /// every mod has had its say — to <c>definition_dump.json</c> in the storage directory.
    ///
    /// <para>Reading a mod's <c>.sbc</c> files answers what a mod <em>declares</em>. Only this answers
    /// which declaration <em>won</em>, and it is the only instrument that sees a definition a mod
    /// rewrote from C# at <c>LoadData</c> with no <c>.sbc</c> at all.</para>
    ///
    /// <para>Provenance is the mod, never the file: <see cref="MyModContext"/> is one object per mod and
    /// its <c>CurrentFile</c> advances as files parse, so at dump time it names whichever file that mod
    /// parsed last — not the one the definition came from.</para>
    /// </summary>
    internal static class DefinitionDumpWriter
    {
        /// <summary>Builds the dump and writes it. Returns a one-line summary for the bridge. Must run
        /// on the game thread; never throws.</summary>
        public static string Write(string storageDir)
        {
            if (string.IsNullOrEmpty(storageDir)) return "dumpdefs: no storage directory.";
            if (MyDefinitionManager.Static == null) return "dumpdefs: definition manager unavailable (no session loaded).";

            try
            {
                var dump = new DefinitionDump { At = DateTime.UtcNow };
                ReadWorld(dump);
                ReadDefinitions(dump);
                ReadFactionTypes(dump);
                ReadContractTypes(dump);
                ReadPlanetGenerators(dump);

                string path = Path.Combine(storageDir, "definition_dump.json");
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(dump, Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);

                return "dumpdefs: " + dump.TotalDefinitions + " definition(s) (" + dump.IdIndexDefinitions
                     + " id-index + " + dump.TypedSetDefinitions + " typed) from " + dump.ByMod.Count
                     + " source(s); " + dump.FactionTypes.Count + " faction type(s), "
                     + dump.ContractTypes.Count + " contract type(s), " + dump.Planets.Count + " planet(s), "
                     + dump.ShipDrills.Count + " drill(s), " + dump.Components.Count + " component(s)"
                     + (dump.ComponentsWithoutPrice > 0 ? ", " + dump.ComponentsWithoutPrice + " unpriced" : "")
                     + " -> " + path;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Definition dump failed.");
                return "dumpdefs: failed -- " + ex.Message;
            }
        }

        private static void ReadWorld(DefinitionDump dump)
        {
            if (MySession.Static == null) return;
            dump.World = MySession.Static.Name;
            List<MyObjectBuilder_Checkpoint.ModItem> mods = MySession.Static.Mods;
            if (mods == null) return;
            foreach (MyObjectBuilder_Checkpoint.ModItem mod in mods)
                dump.WorldMods.Add(new WorldModEntry
                {
                    PublishedFileId = mod.PublishedFileId,
                    Name = mod.Name,
                    Service = mod.PublishedServiceName,
                });
        }

        /// <summary>Walks the resolved definition set. It takes two accessors, because they hold
        /// **disjoint** stores and neither is the whole thing: the non-generic
        /// <c>GetAllDefinitions()</c> reads <c>m_definitionsById</c> (cube blocks, components, physical
        /// items) while <c>GetAllDefinitions&lt;MyDefinitionBase&gt;()</c> reads the typed set (faction
        /// types, contract types). Measured on the AWL Cauldron world: 6,265 and 2,938 respectively,
        /// with the drills and components in the first and the faction and contract types in the
        /// second. Both counts are reported so the union is auditable rather than asserted.
        ///
        /// <para>Type-tests with <c>as</c> so a subclass — every concrete contract type is one — lands
        /// in its base bucket.</para></summary>
        private static void ReadDefinitions(DefinitionDump dump)
        {
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            var byMod = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions())
            {
                if (def == null) continue;
                dump.IdIndexDefinitions++;
                Consider(dump, byType, byMod, seen, def);
            }

            foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions<MyDefinitionBase>())
            {
                if (def == null) continue;
                dump.TypedSetDefinitions++;
                Consider(dump, byType, byMod, seen, def);
            }

            dump.ByType = ToSortedCounts(byType);
            dump.ByMod = ToSortedCounts(byMod);
        }

        /// <summary>Counts one definition and files it into its detail section, once, however many
        /// accessors reached it.</summary>
        private static void Consider(DefinitionDump dump, Dictionary<string, int> byType, Dictionary<string, int> byMod,
                                     HashSet<string> seen, MyDefinitionBase def)
        {
            if (!seen.Add(KeyOf(def))) return;
            dump.TotalDefinitions++;
            Bump(byType, def.GetType().Name);
            Bump(byMod, SourceOf(def));

            var factionType = def as MyFactionTypeDefinition;
            if (factionType != null) { AddFactionType(dump, factionType); return; }

            var contractType = def as MyContractTypeDefinition;
            if (contractType != null) { AddContractType(dump, contractType); return; }

            var drill = def as MyShipDrillDefinition;
            if (drill != null && dump.SeenDrills.Add(KeyOf(def))) { dump.ShipDrills.Add(ReadDrill(drill)); return; }

            var component = def as MyComponentDefinition;
            if (component != null && dump.SeenComponents.Add(KeyOf(def)))
            {
                ComponentEntry entry = ReadComponent(component);
                if (entry.MinimalPricePerUnit <= 0) dump.ComponentsWithoutPrice++;
                dump.Components.Add(entry);
            }
        }

        /// <summary>Faction types have their own typed accessor, the one
        /// <see cref="SeFactionTypeCatalogue"/> already uses. Runs after the main pass and dedupes, so
        /// it fills a gap rather than double-counting when both see the same definition.</summary>
        private static void ReadFactionTypes(DefinitionDump dump)
        {
            IEnumerable<MyFactionTypeDefinition> defs = MyDefinitionManager.Static.GetAllDefinitions<MyFactionTypeDefinition>();
            if (defs == null) return;
            foreach (MyFactionTypeDefinition def in defs)
                if (def != null) AddFactionType(dump, def);
        }

        /// <summary>Contract types live in their own dictionary, not in the id index.</summary>
        private static void ReadContractTypes(DefinitionDump dump)
        {
            var defs = MyDefinitionManager.Static.GetContractTypeDefinitions();
            foreach (var kv in defs)
                if (kv.Value != null) AddContractType(dump, kv.Value);
        }

        private static void AddFactionType(DefinitionDump dump, MyFactionTypeDefinition def)
        {
            if (!dump.SeenFactionTypes.Add(KeyOf(def))) return;
            dump.FactionTypes.Add(ReadFactionType(def));
        }

        private static void AddContractType(DefinitionDump dump, MyContractTypeDefinition def)
        {
            if (!dump.SeenContractTypes.Add(KeyOf(def))) return;
            dump.ContractTypes.Add(ReadContractType(def));
        }

        private static string KeyOf(MyDefinitionBase def)
        {
            return def.Id.ToString();
        }

        /// <summary>Planet generators live in their own definition set, so the pass above does not
        /// see them.</summary>
        private static void ReadPlanetGenerators(DefinitionDump dump)
        {
            IEnumerable<MyPlanetGeneratorDefinition> planets = MyDefinitionManager.Static.GetPlanetsGeneratorsDefinitions();
            if (planets == null) return;

            foreach (MyPlanetGeneratorDefinition planet in planets)
            {
                if (planet == null || !dump.SeenPlanets.Add(KeyOf(planet))) continue;
                var entry = new PlanetEntry
                {
                    Subtype = planet.Id.SubtypeName,
                    Source = SourceOf(planet),
                    HasAtmosphere = planet.HasAtmosphere,
                    SurfaceGravity = planet.SurfaceGravity,
                    AtmosphereHeight = planet.AtmosphereHeight,
                    // Measured false for every planet on a dedicated server, base-game ones included,
                    // though 8 vanilla generators declare <SurfaceDetail>. So it reports the resolved
                    // state honestly but cannot show that a mod stripped detail: it is already false
                    // without one.
                    HasSurfaceDetail = planet.Detail != null,
                };

                MyPlanetOreMapping[] ores = planet.OreMappings;
                if (ores != null)
                    foreach (MyPlanetOreMapping ore in ores)
                    {
                        if (ore == null) continue;
                        entry.OreMappings.Add(new OreMappingEntry
                        {
                            Type = ore.Type,
                            Value = ore.Value,
                            Start = ore.Start,
                            Depth = ore.Depth,
                        });
                    }

                dump.Planets.Add(entry);
            }
        }

        private static FactionTypeEntry ReadFactionType(MyFactionTypeDefinition def)
        {
            var entry = new FactionTypeEntry
            {
                Subtype = def.Id.SubtypeName,
                Source = SourceOf(def),
                OfferPriceStartingMultiplier = def.OfferPriceStartingMultiplier,
                OrderPriceStartingMultiplier = def.OrderPriceStartingMultiplier,
            };
            AddIds(entry.Offers, def.OffersList);
            AddIds(entry.Orders, def.OrdersList);
            if (def.GridsForSale != null)
                foreach (string grid in def.GridsForSale)
                    if (!string.IsNullOrEmpty(grid)) entry.GridsForSale.Add(grid);
            return entry;
        }

        private static ContractTypeEntry ReadContractType(MyContractTypeDefinition def)
        {
            var entry = new ContractTypeEntry
            {
                Subtype = def.Id.SubtypeName,
                ClrType = def.GetType().Name,
                Source = SourceOf(def),
                MinimumReputation = def.MinimumReputation,
                FailReputationPrice = def.FailReputationPrice,
                MinimumMoney = def.MinimumMoney,
                MinStartingDeposit = def.MinStartingDeposit,
                MaxStartingDeposit = def.MaxStartingDeposit,
                DurationMultiplier = def.DurationMultiplier,
                StrategyType = def.StrategyType.ToString(),
            };

            Dictionary<SerializableDefinitionId, float> chances = def.ChancesPerFactionType;
            if (chances != null)
                foreach (KeyValuePair<SerializableDefinitionId, float> kv in chances)
                    entry.ChancesPerFactionType[kv.Key.SubtypeId] = kv.Value;

            return entry;
        }

        private static DrillEntry ReadDrill(MyShipDrillDefinition def)
        {
            return new DrillEntry
            {
                Subtype = def.Id.SubtypeName,
                Source = SourceOf(def),
                CutOutRadius = def.CutOutRadius,
                CutOutOffset = def.CutOutOffset,
                Speed = def.Speed,
                DiscardingMultiplier = def.DiscardingMultiplier,
            };
        }

        private static ComponentEntry ReadComponent(MyComponentDefinition def)
        {
            return new ComponentEntry
            {
                Subtype = def.Id.SubtypeName,
                Source = SourceOf(def),
                MinimalPricePerUnit = def.MinimalPricePerUnit,
                MinimumOfferAmount = def.MinimumOfferAmount,
                MaximumOfferAmount = def.MaximumOfferAmount,
                MinimumOrderAmount = def.MinimumOrderAmount,
                MaximumOrderAmount = def.MaximumOrderAmount,
                Mass = def.Mass,
                Volume = def.Volume,
            };
        }

        /// <summary>Mod that supplied the winning definition, or <c>(base game)</c>. Internal rather
        /// than private because <c>PrefabCatalogueScanner</c> names the same provenance when it
        /// reports a weapon block it could not score.</summary>
        internal static string SourceOf(MyDefinitionBase def)
        {
            MyModContext context = def.Context;
            if (context == null) return "(unknown)";
            if (context.IsBaseGame) return "(base game)";
            string name = context.ModName;
            string id = context.ModId;
            if (string.IsNullOrEmpty(name)) return string.IsNullOrEmpty(id) ? "(unknown mod)" : id;
            return string.IsNullOrEmpty(id) ? name : name + " [" + id + "]";
        }

        private static void AddIds(List<string> target, SerializableDefinitionId[] ids)
        {
            if (ids == null) return;
            foreach (SerializableDefinitionId id in ids)
                target.Add(id.TypeIdString + "/" + id.SubtypeId);
        }

        private static void Bump(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key)) key = "(unknown)";
            int current;
            counts.TryGetValue(key, out current);
            counts[key] = current + 1;
        }

        private static List<CountEntry> ToSortedCounts(Dictionary<string, int> counts)
        {
            var list = new List<CountEntry>(counts.Count);
            foreach (KeyValuePair<string, int> kv in counts)
                list.Add(new CountEntry { Name = kv.Key, Count = kv.Value });
            list.Sort(delegate (CountEntry a, CountEntry b)
            {
                int byCount = b.Count.CompareTo(a.Count);
                return byCount != 0 ? byCount : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
            return list;
        }

        private sealed class DefinitionDump
        {
            public DateTime At { get; set; }
            public string World { get; set; }
            public int TotalDefinitions { get; set; }
            public int IdIndexDefinitions { get; set; }
            public int TypedSetDefinitions { get; set; }
            public int ComponentsWithoutPrice { get; set; }
            public List<WorldModEntry> WorldMods { get; set; } = new List<WorldModEntry>();
            public List<CountEntry> ByType { get; set; } = new List<CountEntry>();
            public List<CountEntry> ByMod { get; set; } = new List<CountEntry>();
            public List<FactionTypeEntry> FactionTypes { get; set; } = new List<FactionTypeEntry>();
            public List<ContractTypeEntry> ContractTypes { get; set; } = new List<ContractTypeEntry>();
            public List<PlanetEntry> Planets { get; set; } = new List<PlanetEntry>();
            public List<DrillEntry> ShipDrills { get; set; } = new List<DrillEntry>();
            public List<ComponentEntry> Components { get; set; } = new List<ComponentEntry>();

            // Each detail section is filled by the main pass and again by its own typed accessor,
            // because no single accessor reaches every one of them.
            [JsonIgnore] public HashSet<string> SeenFactionTypes { get; } = new HashSet<string>(StringComparer.Ordinal);
            [JsonIgnore] public HashSet<string> SeenContractTypes { get; } = new HashSet<string>(StringComparer.Ordinal);
            [JsonIgnore] public HashSet<string> SeenDrills { get; } = new HashSet<string>(StringComparer.Ordinal);
            [JsonIgnore] public HashSet<string> SeenComponents { get; } = new HashSet<string>(StringComparer.Ordinal);
            [JsonIgnore] public HashSet<string> SeenPlanets { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class WorldModEntry
        {
            public ulong PublishedFileId { get; set; }
            public string Name { get; set; }
            public string Service { get; set; }
        }

        private sealed class CountEntry
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        private sealed class FactionTypeEntry
        {
            public string Subtype { get; set; }
            public string Source { get; set; }
            public float OfferPriceStartingMultiplier { get; set; }
            public float OrderPriceStartingMultiplier { get; set; }
            public List<string> Offers { get; set; } = new List<string>();
            public List<string> Orders { get; set; } = new List<string>();
            public List<string> GridsForSale { get; set; } = new List<string>();
        }

        private sealed class ContractTypeEntry
        {
            public string Subtype { get; set; }
            public string ClrType { get; set; }
            public string Source { get; set; }
            public int MinimumReputation { get; set; }
            public int FailReputationPrice { get; set; }
            public long MinimumMoney { get; set; }
            public long MinStartingDeposit { get; set; }
            public long MaxStartingDeposit { get; set; }
            public double DurationMultiplier { get; set; }
            public string StrategyType { get; set; }
            public Dictionary<string, float> ChancesPerFactionType { get; set; } = new Dictionary<string, float>();
        }

        private sealed class PlanetEntry
        {
            public string Subtype { get; set; }
            public string Source { get; set; }
            public bool HasAtmosphere { get; set; }
            public float SurfaceGravity { get; set; }
            public float AtmosphereHeight { get; set; }
            public bool HasSurfaceDetail { get; set; }
            public List<OreMappingEntry> OreMappings { get; set; } = new List<OreMappingEntry>();
        }

        private sealed class OreMappingEntry
        {
            public string Type { get; set; }
            public byte Value { get; set; }
            public float Start { get; set; }
            public float Depth { get; set; }
        }

        private sealed class DrillEntry
        {
            public string Subtype { get; set; }
            public string Source { get; set; }
            public float CutOutRadius { get; set; }
            public float CutOutOffset { get; set; }
            public float Speed { get; set; }
            public float DiscardingMultiplier { get; set; }
        }

        private sealed class ComponentEntry
        {
            public string Subtype { get; set; }
            public string Source { get; set; }
            public int MinimalPricePerUnit { get; set; }
            public int MinimumOfferAmount { get; set; }
            public int MaximumOfferAmount { get; set; }
            public int MinimumOrderAmount { get; set; }
            public int MaximumOrderAmount { get; set; }
            public float Mass { get; set; }
            public float Volume { get; set; }
        }
    }
}
