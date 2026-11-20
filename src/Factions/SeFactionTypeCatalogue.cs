using System;
using System.Collections.Generic;
using System.Text;
using Factions.Core;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.World;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// SE-backed <see cref="IFactionTypeCatalogue"/>. Reads <c>FactionTypes.sbc</c> via
    /// <see cref="MyDefinitionManager"/> and maps faction tags to type names via
    /// <see cref="MySession.Static.Factions"/>.
    ///
    /// <para>Constructed once at session load. The tag→type map is a point-in-time snapshot;
    /// factions that join after session load will fall through to an empty offer list.</para>
    /// </summary>
    public sealed class SeFactionTypeCatalogue : IFactionTypeCatalogue
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);
        private static readonly IReadOnlyList<string> Empty = new List<string>().AsReadOnly();

        // factionType ("Miner") → invKey list derived from OffersList
        private readonly Dictionary<string, List<string>> _offersByType;
        // factionType ("Miner") → invKey list derived from OrdersList
        private readonly Dictionary<string, List<string>> _ordersByType;
        // faction tag → faction type name
        private readonly Dictionary<string, string> _typeByTag;

        public int TypeCount { get { return _offersByType.Count; } }
        public int TagCount  { get { return _typeByTag.Count; } }

        private SeFactionTypeCatalogue(
            Dictionary<string, List<string>> offersByType,
            Dictionary<string, List<string>> ordersByType,
            Dictionary<string, string> typeByTag)
        {
            _offersByType = offersByType;
            _ordersByType = ordersByType;
            _typeByTag    = typeByTag;
        }

        public bool TryGetFactionType(string factionTag, out string factionType)
        {
            return _typeByTag.TryGetValue(factionTag, out factionType);
        }

        public IReadOnlyList<string> OfferInvKeys(string factionType)
        {
            if (string.IsNullOrEmpty(factionType)) return Empty;
            List<string> list;
            return _offersByType.TryGetValue(factionType, out list) ? list : Empty;
        }

        public IReadOnlyList<string> OrderInvKeys(string factionType)
        {
            if (string.IsNullOrEmpty(factionType)) return Empty;
            List<string> list;
            return _ordersByType.TryGetValue(factionType, out list) ? list : Empty;
        }

        /// <summary>Build the catalogue from SE definition and faction data. Call at session load.</summary>
        public static SeFactionTypeCatalogue Build()
        {
            var offersByType = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var ordersByType = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var typeByTag    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Pass 1: OffersList and OrdersList per faction type from typed sandbox definitions.
            if (MyDefinitionManager.Static != null)
            {
                try
                {
                    foreach (MyFactionTypeDefinition def in MyDefinitionManager.Static.GetAllDefinitions<MyFactionTypeDefinition>())
                    {
                        string typeName;
                        List<string> offerKeys;
                        List<string> orderKeys;
                        if (!TryReadFactionTypeLists(def, out typeName, out offerKeys, out orderKeys)) continue;

                        if (offerKeys != null && offerKeys.Count > 0)
                            offersByType[typeName] = offerKeys;

                        if (orderKeys != null && orderKeys.Count > 0)
                            ordersByType[typeName] = orderKeys;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "SeFactionTypeCatalogue: failed to read faction type definitions.");
                }
            }

            // Pass 2: tag → type name from live faction list.
            // Prefer MyFaction.FactionTypeString to match Keen's MyStationGenerator lookup,
            // then fall back to the enum name after skipping PlayerMade.
            //
            // The two paths must agree about "None", and they agree by RECORDING it, not by
            // dropping it. "None" is a real answer -- it is what SE says about a faction with no
            // economic role -- and FactionStorePolicy needs to hear it: that policy deliberately
            // treats an unresolved tag as "keeps its store", because a world with no catalogue at
            // all has every tag unresolved and is supposed to use the inventory fallback. Skipping
            // None here would make the two indistinguishable and hand FCTM its store back.
            //
            // PlayerMade stays skipped: a player faction is not a non-trading faction, and leaving
            // it unresolved is what keeps a Magnate-tier player store restocking.
            if (MySession.Static?.Factions != null)
            {
                try
                {
                    foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
                    {
                        MyFaction faction = kv.Value;
                        if (faction == null || string.IsNullOrEmpty(faction.Tag)) continue;

                        string factionTypeName = faction.FactionTypeString;
                        if (!string.IsNullOrEmpty(factionTypeName))
                        {
                            typeByTag[faction.Tag] = factionTypeName;
                            continue;
                        }

                        string fallbackTypeName = faction.FactionType.ToString();
                        if (string.Equals(fallbackTypeName, "PlayerMade", StringComparison.OrdinalIgnoreCase))
                            continue;
                        typeByTag[faction.Tag] = fallbackTypeName;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "SeFactionTypeCatalogue: failed to read faction type tags.");
                }
            }

            Log.Info(string.Format(
                "SeFactionTypeCatalogue: {0} faction type(s) with offer lists; {1} faction tag(s) mapped.",
                offersByType.Count, typeByTag.Count));
            LogResolvedLists(ordersByType, typeByTag);

            return new SeFactionTypeCatalogue(offersByType, ordersByType, typeByTag);
        }

        private static bool TryReadFactionTypeLists(MyFactionTypeDefinition def, out string typeName, out List<string> offerKeys, out List<string> orderKeys)
        {
            typeName = null;
            offerKeys = null;
            orderKeys = null;

            if (def == null) return false;

            typeName = def.Id.SubtypeName;
            if (string.IsNullOrEmpty(typeName)) return false;

            offerKeys = BuildInvKeyList(def.OffersList);
            orderKeys = BuildInvKeyList(def.OrdersList);
            return true;
        }


        /// <summary>Prints the two facts the counts above cannot answer: which ore each faction type
        /// will buy natively, and which type each live tag resolves to.
        ///
        /// <para>A station's buy-order candidates are its faction type's <c>OrdersList</c>, so
        /// "why will this station not buy ore" is answerable only from the pairing of these two —
        /// and neither is readable from disk afterwards, because SE keeps factions and banking in a
        /// binary section of the save. Before this, answering it meant inferring the type from a
        /// candidate count in <c>STORE_LIST</c>, which needs a player standing at a spawned station
        /// (FAC-157).</para>
        ///
        /// <para>Two lines, once per session, at load — deliberately not behind the store debug
        /// toggle, which would put them out of reach on exactly the empty server where they are the
        /// only way to answer the question. The faction's own demand widens the candidate set past
        /// what is printed here; the <c>ore=</c> column is the stock world data, not the effective
        /// set.</para></summary>
        private static void LogResolvedLists(
            Dictionary<string, List<string>> ordersByType, Dictionary<string, string> typeByTag)
        {
            var byType = new StringBuilder();
            foreach (KeyValuePair<string, List<string>> kv in ordersByType)
            {
                int ore = 0;
                if (kv.Value != null)
                    foreach (string key in kv.Value)
                        if (key != null && key.StartsWith("Ore:", StringComparison.OrdinalIgnoreCase)) ore++;

                if (byType.Length > 0) byType.Append(", ");
                byType.Append(kv.Key).Append(" orders=").Append(kv.Value == null ? 0 : kv.Value.Count)
                      .Append(" ore=").Append(ore);
            }
            Log.Info("SeFactionTypeCatalogue: order lists by type -- "
                + (byType.Length > 0 ? byType.ToString() : "(none)"));

            var tags = new List<string>(typeByTag.Keys);
            tags.Sort(StringComparer.OrdinalIgnoreCase);
            var byTag = new StringBuilder();
            foreach (string tag in tags)
            {
                if (byTag.Length > 0) byTag.Append(", ");
                byTag.Append(tag).Append('=').Append(typeByTag[tag]);
            }
            Log.Info("SeFactionTypeCatalogue: tag -> type -- "
                + (byTag.Length > 0 ? byTag.ToString() : "(none)"));
        }

        private static List<string> BuildInvKeyList(SerializableDefinitionId[] ids)
        {
            if (ids == null || ids.Length == 0) return null;

            var invKeys = new List<string>(ids.Length);
            foreach (SerializableDefinitionId sid in ids)
            {
                string key = InventoryKeyMapper.FromTypeIdAndSubtype(sid.TypeIdString, sid.SubtypeId);
                if (!string.IsNullOrEmpty(key))
                    invKeys.Add(key);
            }

            return invKeys.Count > 0 ? invKeys : null;
        }

        private static List<string> BuildInvKeyListFromObject(object listValue)
        {
            var ids = listValue as System.Collections.IEnumerable;
            if (ids == null) return null;

            var invKeys = new List<string>();
            foreach (object entry in ids)
            {
                if (entry == null) continue;

                if (entry is SerializableDefinitionId)
                {
                    var sid = (SerializableDefinitionId)entry;
                    string sidKey = InventoryKeyMapper.FromTypeIdAndSubtype(sid.TypeIdString, sid.SubtypeId);
                    if (!string.IsNullOrEmpty(sidKey))
                        invKeys.Add(sidKey);
                    continue;
                }

                Type entryType = entry.GetType();
                object typeIdValue = entryType.GetProperty("TypeIdString") != null ? entryType.GetProperty("TypeIdString").GetValue(entry, null) : null;
                object subtypeValue = entryType.GetProperty("SubtypeId") != null ? entryType.GetProperty("SubtypeId").GetValue(entry, null) : null;
                string key = InventoryKeyMapper.FromTypeIdAndSubtype(typeIdValue as string, subtypeValue as string);
                if (!string.IsNullOrEmpty(key))
                    invKeys.Add(key);
            }

            return invKeys.Count > 0 ? invKeys : null;
        }
    }
}
