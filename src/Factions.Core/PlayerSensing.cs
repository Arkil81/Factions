using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One player-controlled grid, as something a faction's sensors can find.
    ///
    /// <para>A player is a <b>target</b> here, which is the direction the model was missing: every
    /// other sensing path in this codebase treats a player as a sensor and a node or a fleet as the
    /// thing found.</para></summary>
    public struct PlayerContact
    {
        /// <summary>SE identity the grid is attributed to — its owner, who need not be online or
        /// aboard. One identity may hold several contacts.</summary>
        public long IdentityId;

        /// <summary>Entity id of the grid itself. The handle a transition is keyed on: an identity
        /// with two hulls must not read as lost when one of them drops out of coverage.</summary>
        public long GridEntityId;

        public Vec3 PositionKm;

        /// <summary>How loud the grid is. Zero means "not supplied" and reads as the reference 1.0,
        /// not as silence -- the same "unknown, do not gate" convention
        /// <see cref="FleetSensing.SignatureOf"/> uses for an unscanned prefab. A caller that cannot
        /// measure a grid therefore gets reference-range detection rather than an invisible player.</summary>
        public double Signature;

        /// <summary>Radius this grid is announcing itself over, km — a working antenna with
        /// broadcasting on. Zero is "not broadcasting", and also what an unsupplied value reads as.
        /// A faction detects the grid at least this far out, because SE's own HUD already does
        /// (FAC-140).</summary>
        public double BroadcastRadiusKm;
    }

    /// <summary>
    /// Which players a faction can currently see.
    ///
    /// <para>The geometry is <see cref="Sensing.Detects"/>, unchanged -- this adds a second kind of
    /// target rather than a second detection rule, so the map, the HUD scanner and a faction's own
    /// sensors all continue to answer "can that be seen" the same way.</para>
    ///
    /// <para><b>Nothing here is persisted.</b> A remembered position of a moving thing is worse than
    /// no position, which is why <see cref="KnowledgeSweep"/> never writes a fleet into a
    /// <see cref="FactionKnowledge"/>. A player is exactly as mobile, so the result is rebuilt every
    /// sweep and is empty on load.</para>
    /// </summary>
    public static class PlayerSensing
    {
        /// <summary>Everything a faction sees through: its estate (including any installed
        /// <see cref="NodeCapabilityKind.SensorArray"/>) plus every convoy it currently has in
        /// flight. Appends, so a caller can stack these on a list it is already building.
        ///
        /// <para>A fleet is a weaker sensor than a station -- <see cref="FactionTuning.SensorFleetRangeKm"/>
        /// against <see cref="FactionTuning.SensorNodePassiveRangeKm"/> -- but it is the only one that
        /// moves, so it is what gives a faction any reach at all beyond its own territory.</para></summary>
        public static void CollectFactionSensors(
            SpatialGraph graph, FactionState state, FactionTuning tuning, DateTime nowUtc,
            List<SensorSource> into)
        {
            if (graph == null || state == null || tuning == null || into == null) return;

            FleetSensing.CollectNodeSensors(graph, state.Tag, tuning, into);

            double fleetStrength = tuning.SensorFleetRangeKm;
            if (fleetStrength <= 0) return;
            for (int i = 0; i < state.PendingFleets.Count; i++)
            {
                PendingFleetAction fleet = state.PendingFleets[i];
                if (fleet.Intercepted || fleet.Resolved) continue;
                into.Add(new SensorSource
                {
                    PositionKm = fleet.CurrentPosKm(nowUtc, tuning),
                    StrengthKm = fleetStrength
                });
            }
        }

        /// <summary>Replaces <paramref name="into"/> with the contacts these sensors detect, and
        /// returns how many. Cleared first, so a faction that has lost every sensor reports nobody
        /// rather than keeping its last answer.</summary>
        public static int Detect(
            IList<SensorSource> sensors, IList<PlayerContact> contacts, List<PlayerContact> into,
            SensorOcclusion occlusion = null)
        {
            if (into == null) return 0;
            into.Clear();
            if (sensors == null || sensors.Count == 0 || contacts == null) return 0;

            for (int i = 0; i < contacts.Count; i++)
            {
                PlayerContact contact = contacts[i];
                double signature = contact.Signature > 0 ? contact.Signature : 1.0;
                if (!FleetSensing.IsDetectedBy(sensors, contact.PositionKm, signature,
                                               contact.BroadcastRadiusKm, occlusion)) continue;
                into.Add(contact);
            }
            return into.Count;
        }

        /// <summary>Whether this faction currently sees that identity. A linear scan on purpose: the
        /// set is the handful of players online, and a dictionary would cost more to maintain than it
        /// saves to query.</summary>
        public static bool Sees(IList<PlayerContact> sensed, long identityId)
        {
            if (sensed == null) return false;
            for (int i = 0; i < sensed.Count; i++)
                if (sensed[i].IdentityId == identityId) return true;
            return false;
        }

        /// <summary>Whether this faction currently sees that <b>grid</b>. What an acquire/lose
        /// transition is keyed on: an owner with two hulls is still "seen" while either is in
        /// coverage, so an identity-keyed diff would silently swallow one of them
        /// appearing or leaving.</summary>
        public static bool SeesGrid(IList<PlayerContact> sensed, long gridEntityId)
        {
            if (sensed == null || gridEntityId == 0) return false;
            for (int i = 0; i < sensed.Count; i++)
                if (sensed[i].GridEntityId == gridEntityId) return true;
            return false;
        }
    }
}
