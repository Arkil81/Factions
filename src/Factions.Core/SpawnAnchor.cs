using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a respawn-ship spawn was anchored to.</summary>
    public enum SpawnAnchorKind
    {
        /// <summary>Nothing qualified; the host leaves SE's own chosen position untouched.</summary>
        None = 0,
        /// <summary>An online faction mate of the spawning player.</summary>
        FactionMate = 1,
        /// <summary>A station node owned by a faction welcoming enough to spawn a stranger beside.</summary>
        Station = 2,
        /// <summary>The nearest point of a region the world admits space respawns in, when neither a
        /// mate nor a station inside one qualifies. See <see cref="WorldFeature.SpaceRespawn"/>.</summary>
        Region = 3
    }

    /// <summary>
    /// A station node offered to <see cref="SpawnAnchorSelector"/> as a possible spawn anchor or
    /// wayfinding target. <see cref="AtWarWithSpawner"/> is resolved by the host from SE's own
    /// standings table; everything else comes straight off the <see cref="SpatialNode"/>.
    /// </summary>
    public sealed class SpawnAnchorCandidate
    {
        public string NodeId;
        public string DisplayName;
        public Vec3 PositionKm;
        /// <summary>Where this node stands — on terrain, in a gravity well, or in open space. Copied
        /// from <see cref="SpatialNode.Site"/>. The respawn tiers are exactly this three-way split,
        /// so they read it directly rather than through <see cref="NodeZone"/>, whose Surface answer
        /// comes from relative gravity and includes hundreds of km of open air on a deep-atmosphere
        /// body. See docs/issues/FAC-096 stage 2.</summary>
        public StationSite Site;
        /// <summary>Planet this node belongs to, or null when no planet applies.</summary>
        public string PlanetId;
        public string OwnerTag;
        /// <summary><see cref="Traits.Xenophobia"/> of the owner's personality.</summary>
        public double OwnerXenophobia;
        /// <summary>True when the owner is at war with the spawning player's faction. Always false
        /// for a factionless player, who has no standings — which is exactly why xenophobia has to
        /// carry the safety judgement on a brand-new identity's first spawn.</summary>
        public bool AtWarWithSpawner;
        /// <summary>True when the feature this node stands in admits a space respawn
        /// (<see cref="WorldFeaturePlacement.AllowsSpaceRespawnAt"/>). Read only when
        /// <see cref="SpawnAnchorRequest.SpaceRespawnRestricted"/> is set.</summary>
        public bool SpaceRespawnAllowed;
    }

    /// <summary>The spawn SE has already decided on, which anchoring may override.</summary>
    public sealed class SpawnAnchorRequest
    {
        /// <summary>Where SE's own placement put the spawn. The tiebreak origin: with several
        /// anchors eligible, the one nearest SE's answer is the least surprising move.</summary>
        public Vec3 VanillaPositionKm;

        /// <summary>Planet the player picked in the respawn screen, or null for a space spawn.
        /// A planet choice is the player's, so anchoring never moves a spawn off it.</summary>
        public string PlanetId;

        /// <summary>True when the respawn screen selected a planet.</summary>
        public bool OnPlanet;

        /// <summary>True for a space spawn on a world that authors at least one
        /// <see cref="WorldFeature.SpaceRespawn"/> region: the spawn may then only land inside one,
        /// so stations elsewhere are not anchors and the host has already dropped mates elsewhere.</summary>
        public bool SpaceRespawnRestricted;

        /// <summary>Nearest point to <see cref="VanillaPositionKm"/> inside an admitting region
        /// (<see cref="WorldFeaturePlacement.TryNearestSpaceRespawnPoint"/>), the anchor of last
        /// resort for a restricted spawn. Null when there is none.</summary>
        public Vec3? SpaceRespawnFallbackKm;
    }

    /// <summary>Anchor chosen for one respawn-ship spawn.</summary>
    public sealed class SpawnAnchorChoice
    {
        public SpawnAnchorKind Kind;
        public string NodeId;
        public string DisplayName;
        public Vec3 PositionKm;
        /// <summary>How far from <see cref="PositionKm"/> the spawn should be placed, in metres.</summary>
        public double RadiusM;

        public static readonly SpawnAnchorChoice None =
            new SpawnAnchorChoice { Kind = SpawnAnchorKind.None };
    }

    /// <summary>
    /// Decides what a respawn-ship spawn should be placed beside, and which station its GPS and
    /// datapad should name. Both answers are pure functions of the node estate, the online faction
    /// mates and the tuning — no SE types, so the whole selection is testable without a host.
    ///
    /// Placement and wayfinding deliberately differ on planets. A planet spawn is only ever anchored
    /// to a Surface node of the planet the player picked, because a respawn rover placed beside an
    /// orbital station is in vacuum with an atmospheric hull. The pointer has no such constraint, so
    /// it falls back through that planet's Orbit nodes and then anything at all rather than leaving
    /// a new arrival with no destination.
    ///
    /// In space, a world that authors <see cref="WorldFeature.SpaceRespawn"/> regions confines the
    /// spawn to them: mate, then station, then the nearest point of a region. SE's own position is
    /// never kept there, because it can land anywhere — including inside a region that kills a
    /// fresh arrival on sight.
    /// </summary>
    public static class SpawnAnchorSelector
    {
        /// <summary>
        /// Anchor for the spawn itself, or <see cref="SpawnAnchorChoice.None"/> to keep SE's answer.
        /// A faction mate always wins over a station: rejoining your own people is the stronger
        /// intent, and the mate radius is the tighter one.
        ///
        /// <paramref name="mateKm"/> must already be filtered by the host to mates in the same
        /// environment as the spawn (on the chosen planet, or off every planet for a space spawn) —
        /// that test needs SE's planet volumes and so cannot happen here.
        /// </summary>
        public static SpawnAnchorChoice SelectPlacement(
            SpawnAnchorRequest req,
            IList<Vec3> mateKm,
            IList<SpawnAnchorCandidate> stations,
            FactionTuning tuning)
        {
            if (req == null || tuning == null) return SpawnAnchorChoice.None;

            Vec3 origin = req.VanillaPositionKm;

            if (mateKm != null && mateKm.Count > 0)
            {
                int best = -1;
                double bestDist = double.MaxValue;
                for (int i = 0; i < mateKm.Count; i++)
                {
                    double d = origin.DistanceSquaredTo(mateKm[i]);
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                if (best >= 0)
                {
                    return new SpawnAnchorChoice
                    {
                        Kind = SpawnAnchorKind.FactionMate,
                        PositionKm = mateKm[best],
                        RadiusM = tuning.RespawnMateAnchorRadiusM
                    };
                }
            }

            SpawnAnchorCandidate station = NearestWelcoming(
                stations, tuning, origin, PlacementEligibility(req));
            if (station == null) return SelectRegionFallback(req, tuning);

            return new SpawnAnchorChoice
            {
                Kind = SpawnAnchorKind.Station,
                NodeId = station.NodeId,
                DisplayName = station.DisplayName,
                PositionKm = station.PositionKm,
                RadiusM = tuning.RespawnStationAnchorRadiusM
            };
        }

        /// <summary>
        /// The last-resort anchor for a restricted space spawn: the nearest point of an admitting
        /// region, or <see cref="SpawnAnchorChoice.None"/> when the spawn is not restricted or no
        /// region exists. Also what the host falls back to when a mate or station anchor could not be
        /// cleared.
        /// </summary>
        public static SpawnAnchorChoice SelectRegionFallback(SpawnAnchorRequest req, FactionTuning tuning)
        {
            if (req == null || tuning == null) return SpawnAnchorChoice.None;
            if (req.OnPlanet || !req.SpaceRespawnRestricted || !req.SpaceRespawnFallbackKm.HasValue)
                return SpawnAnchorChoice.None;

            return new SpawnAnchorChoice
            {
                Kind = SpawnAnchorKind.Region,
                PositionKm = req.SpaceRespawnFallbackKm.Value,
                RadiusM = tuning.RespawnRegionAnchorRadiusM
            };
        }

        /// <summary>
        /// Station the spawn's GPS marker and datapad should name, or null when nothing qualifies.
        /// Searched in tiers so a planet arrival is never pointed at orbit while a station stands on
        /// the ground beside them, and a space arrival is never pointed at a surface station it
        /// cannot reach in a pod. <paramref name="fromKm"/> is where the player actually ended up,
        /// which is not <see cref="SpawnAnchorRequest.VanillaPositionKm"/> once placement has moved
        /// them.
        /// </summary>
        public static SpawnAnchorCandidate SelectWayfindingTarget(
            SpawnAnchorRequest req,
            Vec3 fromKm,
            IList<SpawnAnchorCandidate> stations,
            FactionTuning tuning)
        {
            if (req == null || tuning == null) return null;

            foreach (Func<SpawnAnchorCandidate, bool> tier in WayfindingTiers(req))
            {
                SpawnAnchorCandidate hit = NearestWelcoming(stations, tuning, fromKm, tier);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// Whether a stranger can be set down beside this station without it being a death sentence.
        /// War is the host's answer; xenophobia is the only signal that still applies to a
        /// factionless player, who is at war with nobody.
        /// </summary>
        public static bool IsWelcoming(SpawnAnchorCandidate c, FactionTuning tuning)
        {
            if (c == null || tuning == null) return false;
            if (c.AtWarWithSpawner) return false;
            return c.OwnerXenophobia < tuning.RespawnAnchorMaxXenophobia;
        }

        private static Func<SpawnAnchorCandidate, bool> PlacementEligibility(SpawnAnchorRequest req)
        {
            if (req.OnPlanet)
            {
                string planetId = req.PlanetId;
                return c => c.Site == StationSite.Surface && SamePlanet(c.PlanetId, planetId);
            }
            if (req.SpaceRespawnRestricted)
                return c => c.Site != StationSite.Surface && c.SpaceRespawnAllowed;
            return c => c.Site != StationSite.Surface;
        }

        private static IEnumerable<Func<SpawnAnchorCandidate, bool>> WayfindingTiers(SpawnAnchorRequest req)
        {
            if (req.OnPlanet)
            {
                string planetId = req.PlanetId;
                yield return c => c.Site == StationSite.Surface && SamePlanet(c.PlanetId, planetId);
                yield return c => c.Site == StationSite.Orbital && SamePlanet(c.PlanetId, planetId);
            }
            else
            {
                // A spawn confined to a region is pointed at a station in one first: the arrival is
                // standing in it, and the nearest station in open space may be inside a nest.
                if (req.SpaceRespawnRestricted)
                    yield return c => c.Site != StationSite.Surface && c.SpaceRespawnAllowed;
                yield return c => c.Site == StationSite.Space;
                yield return c => c.Site == StationSite.Orbital;
            }
            yield return c => true;
        }

        private static bool SamePlanet(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Ties break on NodeId so the same estate always yields the same anchor -- two stations at
        // an identical distance are not unheard of on a symmetric orbit band.
        private static SpawnAnchorCandidate NearestWelcoming(
            IList<SpawnAnchorCandidate> stations,
            FactionTuning tuning,
            Vec3 origin,
            Func<SpawnAnchorCandidate, bool> eligible)
        {
            if (stations == null) return null;

            SpawnAnchorCandidate best = null;
            double bestDist = double.MaxValue;
            for (int i = 0; i < stations.Count; i++)
            {
                SpawnAnchorCandidate c = stations[i];
                if (c == null || !IsWelcoming(c, tuning) || !eligible(c)) continue;

                double d = origin.DistanceSquaredTo(c.PositionKm);
                if (d > bestDist) continue;
                if (d == bestDist && best != null
                    && string.CompareOrdinal(c.NodeId, best.NodeId) >= 0) continue;

                bestDist = d;
                best = c;
            }
            return best;
        }
    }
}
