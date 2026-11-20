namespace Factions.Core
{
    /// <summary>
    /// How far a node projects Layer-2 influence, in km — the <b>one</b> place that value is decided.
    ///
    /// <para>It used to be decided in three, disagreeing: <c>WorldModel.SourceRadius</c> (24 km) for
    /// nodes the world creates, <c>SourceMapping.BaseRadiusKm</c> (40 km) for scan-backed ones, and a
    /// 40 km field initialiser for anything else. A census of the live estate found all 503 nodes at
    /// exactly 24.0 — the 40 km paths were dormant, so the divergence was latent rather than visible,
    /// and a node arriving through one of them would have projected 4.6x the volume of everything
    /// else on the map (FAC-250).</para>
    ///
    /// <para><b>Radius is now earned, not assigned.</b> A bare claim projects
    /// <see cref="FactionTuning.InfluenceRadiusFloorKm"/>; founding a station is the large step up to
    /// <see cref="FactionTuning.InfluenceRadiusFoundedKm"/>; every capability <i>level</i> installed
    /// on top adds <see cref="FactionTuning.InfluenceRadiusPerCapabilityLevelKm"/>, to a ceiling of
    /// <see cref="FactionTuning.InfluenceRadiusMaxKm"/>. The two constants this replaced survive as
    /// the ends of that scale rather than as a contradiction: 24 km is what a founded station still
    /// projects, 40 km is what a fully developed one reaches.</para>
    ///
    /// <para><b>Derived, never persisted.</b> <see cref="SpatialNode.Radius"/> remains as an explicit
    /// in-memory override for constructed worlds (the offline sim's scenarios set it deliberately),
    /// but the host neither writes nor reads it — a stored radius would pin a node to whatever it was
    /// worth on the day it was saved, and stop tracking the development it is meant to measure. See
    /// the repo convention "persist durable state and rebuild derived state".</para>
    /// </summary>
    public static class InfluenceRadius
    {
        /// <summary>Fallback used when no tuning is available (a bare <c>new FactionTuning()</c> is
        /// not one — its inline defaults are real). Matches the founded value, which is what every
        /// live node was worth before this existed.</summary>
        public const double FallbackKm = 24.0;

        /// <summary>The radius <paramref name="node"/> projects. An explicit
        /// <see cref="SpatialNode.Radius"/> greater than zero wins, so a constructed scenario keeps
        /// saying exactly what it means.</summary>
        public static double For(SpatialNode node, FactionTuning tuning)
        {
            if (node == null) return FallbackKm;
            if (node.Radius > 0) return node.Radius;
            if (tuning == null) return FallbackKm;

            double floor = tuning.InfluenceRadiusFloorKm > 0 ? tuning.InfluenceRadiusFloorKm : FallbackKm;
            double founded = tuning.InfluenceRadiusFoundedKm > 0 ? tuning.InfluenceRadiusFoundedKm : floor;
            double r = node.StableStationId != 0 ? founded : floor;

            double perLevel = tuning.InfluenceRadiusPerCapabilityLevelKm;
            if (perLevel > 0 && node.Capabilities.Count > 0)
            {
                int levels = 0;
                foreach (var kv in node.Capabilities)
                    if (kv.Value > 0) levels += kv.Value;
                r += perLevel * levels;
            }

            double max = tuning.InfluenceRadiusMaxKm;
            return max > 0 && r > max ? max : r;
        }
    }
}
