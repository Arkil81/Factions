using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>A node's defence as a scan reads it: 0–3 pips, or not read
    /// (docs/fog-of-war.md → Economic stage, the military band). Stored on a discovered entry, so
    /// <see cref="Unknown"/> must stay 0.</summary>
    public enum DefenceBand : byte
    {
        Unknown = 0,
        /// <summary>Nothing beyond the station's flat floor: no turrets, no fieldable hulls.</summary>
        Undefended = 1,
        Light = 2,
        Contested = 3,
        Fortress = 4,
    }

    /// <summary>One defence reading: its band, and the gate's DPS behind it (server-side only).</summary>
    public struct DefenceReading
    {
        public DefenceBand Band;
        public float Dps;
    }

    public static class NodeDefence
    {
        /// <summary>Pips drawn for a band; 0 for <see cref="DefenceBand.Unknown"/> as well as
        /// <see cref="DefenceBand.Undefended"/>, which only the panel tells apart.</summary>
        public static int Pips(DefenceBand band)
        {
            return band == DefenceBand.Unknown ? 0 : (int)band - 1;
        }

        /// <summary>The band of <paramref name="totalDps"/>, the offensive gate's own figure for
        /// <paramref name="node"/>. At or below the node's <see cref="CombatStrength.StationFlat"/>
        /// it is undefended; the other two edges are tuning, read off the live census (FAC-531).</summary>
        public static DefenceBand Band(double totalDps, SpatialNode node, FactionTuning tuning)
        {
            if (tuning == null) return DefenceBand.Unknown;
            if (totalDps <= CombatStrength.StationFlat(node, tuning)) return DefenceBand.Undefended;
            if (totalDps < tuning.IntelDefenceContestedDps) return DefenceBand.Light;
            if (totalDps < tuning.IntelDefenceFortressDps) return DefenceBand.Contested;
            return DefenceBand.Fortress;
        }

        /// <summary>A reader for the band of any node now: the gate's
        /// <see cref="CombatStrength.OfNodeDefence(SpatialNode,IPrefabCatalogue,FactionTuning,IReadOnlyList{WeaponBand})"/>
        /// with the Guards committed in <paramref name="states"/> counted, so a scan reads the same
        /// number a raid is sized against. The committed-Guard index is taken once, here.</summary>
        public static Func<SpatialNode, DefenceBand> Reader(IPrefabCatalogue catalogue, FactionTuning tuning,
                                                            IList<FactionState> states)
        {
            Func<SpatialNode, DefenceReading> read = Readings(catalogue, tuning, states);
            return node => read(node).Band;
        }

        /// <summary>As <see cref="Reader"/>, with the DPS beside the band.</summary>
        public static Func<SpatialNode, DefenceReading> Readings(IPrefabCatalogue catalogue, FactionTuning tuning,
                                                                 IList<FactionState> states)
        {
            Dictionary<string, List<WeaponBand>> guarded = states != null
                ? FleetManager.BuildGuardedStrengthIndex(states)
                : new Dictionary<string, List<WeaponBand>>();
            return node =>
            {
                if (node == null) return default(DefenceReading);
                List<WeaponBand> standing;
                guarded.TryGetValue(node.Id, out standing);
                double dps = CombatStrength.OfNodeDefence(node, catalogue, tuning, standing);
                return new DefenceReading { Band = Band(dps, node, tuning), Dps = (float)dps };
            };
        }
    }
}
