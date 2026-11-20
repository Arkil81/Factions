using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// Off-screen NPC fleets as intruders. A fleet whose position on its leg lies in the space of a
    /// faction that holds its owner hostile, and that the faction's sensors detect, is marked at the
    /// faction's nearest node exactly as a pilot is, and climbs the same ladder
    /// (<see cref="TrespassLadder"/>), clocked by how long it stays. A fleet still in the space at
    /// Intercept is caught by the faction's Guard standing at that node
    /// (<see cref="PendingFleetResolver"/>'s trespass catch). A fleet that crosses quickly passes free.
    ///
    /// <para><b>Sensed, never rolled.</b> The fleet has a real position on a real leg and a signature
    /// from its own record (<see cref="FleetSensing.SignatureOf"/>), so the same sensor test that sees
    /// a player sees it.</para>
    ///
    /// <para><b>Who is an intruder.</b> The space owner's standing toward the fleet's faction is below
    /// <see cref="FactionTuning.HostileRelationThreshold"/>, the war line, and the two hold no pact. A
    /// raid, retaliation or conquest aimed at the owner is not an intruder: the inbound-threat machinery
    /// already answers it.</para>
    ///
    /// <para>Provenance: FAC-483 slice 5. Design: docs/fleet-events.md, "Trespass: a hostile ship in
    /// our space".</para>
    /// </summary>
    public static class FleetTrespass
    {
        /// <summary>How a caught fleet is settled.</summary>
        public enum CatchKind
        {
            /// <summary>Marked, never caught.</summary>
            None = 0,
            /// <summary>The convoy-encounter verdict, with the Guard in the raider's place.</summary>
            Convoy = 1,
            /// <summary>A duel against the Guard and the node's guns, the fleet in the attacker's place.</summary>
            Duel = 2
        }

        /// <summary>One enemy fleet seen in a faction's space this pass.</summary>
        public struct Sighting
        {
            public int Si;
            public int Fi;
            public string OwnerTag;
            public string NodeId;
            public TrespassRung Rung;
        }

        /// <summary>How a caught fleet of this kind is settled. Carriers take the convoy verdict. Armed
        /// fleets in transit take a duel; a raid on its way home or at its own target does not, because
        /// its mission terminal owns its hulls.</summary>
        public static CatchKind CatchOf(PendingFleetAction f)
        {
            switch (f.Kind)
            {
                case ActionKind.Trade:
                case ActionKind.Ferry:
                case ActionKind.Haul:
                case ActionKind.Mine:
                    return CatchKind.Convoy;
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer:
                    return f.CurrentStopKind == FleetStopKind.Return || f.CurrentStopKind == FleetStopKind.Engage
                        ? CatchKind.None : CatchKind.Duel;
                case ActionKind.Patrol:
                case ActionKind.Guard:
                case ActionKind.Reinforce:
                    return CatchKind.Duel;
                default:
                    return CatchKind.None;
            }
        }

        /// <summary>Whether <paramref name="owner"/> treats a fleet of <paramref name="intruder"/> in its
        /// space as an intruder: its standing is below the war line and there is no pact.</summary>
        public static bool IsHostile(FactionState owner, FactionState intruder, FactionTuning t)
        {
            if (owner == null || intruder == null || t == null || owner.Tag == intruder.Tag) return false;
            if (owner.RelationTo(intruder.Tag) >= t.HostileRelationThreshold) return false;
            return !Diplomacy.InPactWith(owner, intruder.Tag);
        }

        /// <summary>A faction relation read on SE's reputation scale for
        /// <see cref="TrespassLadder.ShadowSeconds"/>: the war line is the edge of the Enemies band
        /// (−500), and −1 is −1500. So a fleet of a faction just at war is shadowed for the base time,
        /// and one of a sworn enemy for half of it.</summary>
        public static int RepFor(double relation, double hostileThreshold)
        {
            double span = hostileThreshold + 1.0;
            if (span <= 0) return -1500;
            double depth = (hostileThreshold - relation) / span;
            if (depth < 0) depth = 0;
            if (depth > 1) depth = 1;
            return (int)Math.Round(-500.0 - 1000.0 * depth);
        }

        /// <summary>The dwell book's key for a fleet. A fleet id is unique only within its faction's
        /// roster, so the faction is part of it.</summary>
        public static string Subject(PendingFleetAction f)
        {
            return "fleet:" + f.ActorTag + ":" + (f.FleetId ?? f.LogId);
        }

        /// <summary>Finds every enemy fleet a faction detects in its space, marks the faction's nearest
        /// node for each (<see cref="WorldModel.MarkIntruder(string, TrespassRung, long)"/>, naming the
        /// fleet's grid when it has one, so a materialised Guard shadows and pursues it), and fills
        /// <paramref name="into"/>. One <c>TRESPASS_INTRUDER</c> line per fresh mark and one
        /// <c>TRESPASS_RUNG</c> when a fleet's stay reaches Intercept.
        ///
        /// <para>Per fleet: one dominance read, one nearest-node walk when it is in an enemy's space,
        /// and the owner's sensors, collected once per owner per pass.</para></summary>
        public static void Scan(IList<FactionState> states, WorldModel world, FactionTuning tuning,
                                IItemVolumeCatalogue volumes, IList<string> log, List<Sighting> into)
        {
            if (into == null) return;
            into.Clear();
            if (states == null || world == null || tuning == null) return;
            var field = world.Influence as InfluenceField;
            if (field == null) return;
            DateTime now = world.UtcNow;

            var byTag = new Dictionary<string, FactionState>(StringComparer.Ordinal);
            for (int i = 0; i < states.Count; i++)
                if (states[i] != null && states[i].Tag != null) byTag[states[i].Tag] = states[i];
            Dictionary<string, List<SensorSource>> sensorsOf = null;

            for (int si = 0; si < states.Count; si++)
            {
                FactionState s = states[si];
                if (s == null || s.IsPlayerFaction || s.PendingFleets.Count == 0) continue;
                for (int fi = 0; fi < s.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = s.PendingFleets[fi];
                    if (f.Intercepted || f.Resolved || f.IsBench) continue;
                    // Standing its own post: a garrison is not crossing anyone's space.
                    if (f.StandsAsDefender(now, tuning)) continue;

                    Vec3 pos = f.CurrentPosKm(now, tuning);
                    string space = TrespassPolicy.SpaceOwnerAt(field, pos, tuning.TradeReachThreshold);
                    if (space == null || space == s.Tag) continue;
                    FactionState owner;
                    if (!byTag.TryGetValue(space, out owner) || owner.IsPlayerFaction) continue;
                    if (!IsHostile(owner, s, tuning)) continue;
                    if (IsAttackOn(f, owner.Tag)) continue;

                    if (sensorsOf == null) sensorsOf = new Dictionary<string, List<SensorSource>>(StringComparer.Ordinal);
                    List<SensorSource> sensors;
                    if (!sensorsOf.TryGetValue(owner.Tag, out sensors))
                    {
                        sensors = new List<SensorSource>();
                        PlayerSensing.CollectFactionSensors(world.Graph, owner, tuning, now, sensors);
                        sensorsOf[owner.Tag] = sensors;
                    }
                    double signature = FleetSensing.SignatureOf(f, world.PrefabCatalogue, volumes, tuning, now);
                    if (!FleetSensing.IsDetectedBy(sensors, pos, signature)) continue;

                    SpatialNode node = TrespassPolicy.NearestOwnedNode(world.Graph, owner.Tag, pos);
                    if (node == null) continue;

                    string subject = Subject(f);
                    double dwell = world.FleetTrespassDwell.Observe(subject, owner.Tag, now);
                    Personality personality = Personalities.Get(owner.PersonalityId);
                    double control = field.Control(pos, owner.Tag);
                    double relation = owner.RelationTo(s.Tag);
                    double shadowS = TrespassLadder.ShadowSeconds(personality.Traits.Aggression, control,
                        RepFor(relation, tuning.HostileRelationThreshold), TrespassLadder.StartsHostile(personality, null));
                    TrespassRung rung = TrespassLadder.RungFor(dwell, shadowS);
                    long grid = f.MaterialisedEntityId > 0 ? f.MaterialisedEntityId : 0;

                    if (world.MarkIntruder(node.Id, rung, grid) && log != null)
                        log.Add("TRESPASS_INTRUDER " + owner.Tag
                                + " node=" + node.Id
                                + " fleet=" + f.LogId
                                + " actor=" + s.Tag
                                + " kind=" + f.Kind
                                + " relation=" + relation.ToString("0.00", CultureInfo.InvariantCulture)
                                + " distKm=" + node.Position.DistanceTo(pos).ToString("0.#", CultureInfo.InvariantCulture)
                                + " shadowS=" + shadowS.ToString("0", CultureInfo.InvariantCulture)
                                + " thr->" + node.Threat.ToString("0.00", CultureInfo.InvariantCulture));

                    if (world.FleetTrespassDwell.StepTo(subject, owner.Tag, rung) && rung == TrespassRung.Intercept
                        && log != null)
                        log.Add("TRESPASS_RUNG " + owner.Tag
                                + " node=" + node.Id
                                + " fleet=" + f.LogId
                                + " actor=" + s.Tag
                                + " rung=intercept"
                                + " dwellS=" + dwell.ToString("0", CultureInfo.InvariantCulture)
                                + " shadowS=" + shadowS.ToString("0", CultureInfo.InvariantCulture)
                                + " control=" + control.ToString("0.00", CultureInfo.InvariantCulture)
                                + " relation=" + relation.ToString("0.00", CultureInfo.InvariantCulture));

                    into.Add(new Sighting { Si = si, Fi = fi, OwnerTag = owner.Tag, NodeId = node.Id, Rung = rung });
                }
            }
            world.FleetTrespassDwell.Prune(now);
        }

        /// <summary>A raid, retaliation or conquest against <paramref name="ownerTag"/>: answered as an
        /// inbound threat, never as a trespass.</summary>
        private static bool IsAttackOn(PendingFleetAction f, string ownerTag)
        {
            if (f.Kind != ActionKind.Raid && f.Kind != ActionKind.Retaliate && f.Kind != ActionKind.Conquer) return false;
            return string.Equals(f.TargetTag, ownerTag, StringComparison.Ordinal);
        }
    }
}
