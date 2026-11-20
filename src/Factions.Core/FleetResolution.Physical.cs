using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// How an engagement dwell has been fought when the grids took part: the duel turns already
    /// accounted for, each side's capacity when the fight began, and whether the grids hold it now.
    /// Carried on the attacking fleet (<see cref="PendingFleetAction.Fight"/>). See
    /// docs/fleet-events.md, "Physical authority and the handoff".
    ///
    /// <para>Capacity is the duel's own unit: a side's summed DPS, its hulls' bands scaled by their
    /// integrity, the defence's with the station's bands and flat floor. The duel's ratio is what
    /// is left of the capacity a side brought, so a ratio measured off the grids is the same number
    /// read the other way.</para>
    /// </summary>
    public sealed class FightProgress
    {
        /// <summary>Duel turns already fought, by the duel or by the grids. The dwell is divided
        /// evenly between the turns: turn <c>t</c> covers the fraction <c>[t/N, (t+1)/N)</c>.</summary>
        public int TurnsFought;
        /// <summary>The attacker's capacity when the fight began: the pool ratios read against.</summary>
        public double AttackerBase;
        /// <summary>The defence's capacity when the fight began, ships and station together.</summary>
        public double DefenderBase;
        /// <summary>The share of the station's contribution still standing. The grids do not measure a
        /// station, so only the duel lowers it.</summary>
        public double StationShare = 1.0;
        /// <summary>Hulls each side brought into the fight.</summary>
        public int AttackerHulls;
        public int DefenderHulls;
        /// <summary>Every fleet of the meeting is in the world and the grids decide.</summary>
        public bool Physical;
        public bool FoughtPhysically;
        public bool FoughtAbstractly;
        /// <summary>A duel segment that already decided the fight (a side destroyed or broken off);
        /// the meeting resolves from it when the dwell ends. Not persisted.</summary>
        public ShipDuel.Result? Decided;
        /// <summary><see cref="Decided"/> was the grids' own verdict — a fight flown in waves that one side
        /// ran out of hulls for (<see cref="PendingFleetResolver.DecideWaved"/>) — not a duel segment's.</summary>
        public bool DecidedByGrids;
    }

    /// <summary>One side's measured result of a fight the grids took part in. Capacity figures are
    /// the duel's unit, so they compare with a duel's prediction; damage figures are SE block
    /// integrity from the damage hook, summed over the side's fleets.</summary>
    public struct PhysicalFightOutcome
    {
        public string Source;
        public string AttackerTag;
        public string DefenderTag;
        public double AttackerRatio;
        public double DefenderRatio;
        /// <summary>Capacity each side took off the other.</summary>
        public double AttackerCapacityDealt;
        public double DefenderCapacityDealt;
        public double AttackerDamageDealt;
        public double AttackerDamageTaken;
        public double DefenderDamageDealt;
        public double DefenderDamageTaken;
        /// <summary>What the target's safe zone absorbed of the attacker's fire
        /// (<see cref="PendingFleetAction.FightZoneHits"/>).</summary>
        public double AttackerZoneHits;
        public int AttackerHullsBrought;
        public int AttackerHullsLost;
        public int DefenderHullsBrought;
        public int DefenderHullsLost;
    }

    /// <summary>Where a fleet joining a fight already under way should appear: at the separation of
    /// the bracket being fought, from its opponent.</summary>
    public struct MeetingSpawn
    {
        /// <summary>The closest a joining fleet appears to its opponent, whatever the bracket: the
        /// point-blank bracket is 0 m, and grids must not appear inside each other.</summary>
        public const double MinSeparationM = 400;

        public bool InMeeting;
        /// <summary>The bracket of the turn being fought, at least <see cref="MinSeparationM"/>.</summary>
        public double SeparationM;
        /// <summary>The opposing fleet, or -1/-1 when the opponent is the node's station.</summary>
        public int OpponentSi, OpponentFi;
        /// <summary>The opponent's abstract position: its fleet's, or the node's.</summary>
        public Vec3 OpponentPosKm;
        /// <summary>The way out from the opponent when the fleet would appear on top of it: away from
        /// the nearest planet, else +Y.</summary>
        public Vec3 FallbackDirection;

        /// <summary><paramref name="anchorKm"/> plus <see cref="SeparationM"/> along the line from it
        /// towards <paramref name="ownKm"/>, or along <see cref="FallbackDirection"/> when the two
        /// coincide.</summary>
        public Vec3 PositionKm(Vec3 ownKm, Vec3 anchorKm)
        {
            double dx = ownKm.X - anchorKm.X, dy = ownKm.Y - anchorKm.Y, dz = ownKm.Z - anchorKm.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-3)
            {
                dx = FallbackDirection.X; dy = FallbackDirection.Y; dz = FallbackDirection.Z;
                len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1e-9) { dx = 0; dy = 1; dz = 0; len = 1; }
            }
            double km = SeparationM / 1000.0 / len;
            return new Vec3(anchorKm.X + dx * km, anchorKm.Y + dy * km, anchorKm.Z + dz * km);
        }
    }

    public static partial class PendingFleetResolver
    {
        public const string SourceDuel = "duel";
        public const string SourcePhysical = "physical";
        public const string SourceHandoff = "handoff";

        /// <summary>The raid's own dwell at its target, still to be fought: the only fight the grids
        /// can decide. A mid-transit meeting is between nodes and never materialised as one.</summary>
        private static bool OnFightableDwell(PendingFleetAction raid)
        {
            return EngagesOnArrival(raid.Kind) && raid.CurrentStopKind == FleetStopKind.Engage
                && !raid.Resolved && !raid.Intercepted && raid.RetreatReason == null
                && raid.TargetNodeId != null && !IsOutcomeAlreadyDecided(raid);
        }

        /// <summary>Every fleet standing as the defence of the raid's target, from any other faction —
        /// the same set the dwell's meeting resolution fights.</summary>
        private static List<(int si, int fi)> DwellDefenders(IList<FactionState> states, (int si, int fi) raidRef,
            DateTime now, FactionTuning tuning)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            string raidTag = states[raidRef.si].Tag;
            var force = new List<(int si, int fi)>();
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null || states[si].Tag == raidTag) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction p = fleets[fi];
                    if (p.TargetNodeId == raid.TargetNodeId && p.StandsAsDefender(now, tuning)) force.Add((si, fi));
                }
            }
            return force;
        }

        private static bool MeetingMaterialised(IList<FactionState> states, WorldModel world, (int si, int fi) raidRef,
            List<(int si, int fi)> force)
        {
            if (IsWaved(world, states[raidRef.si].PendingFleets[raidRef.fi])) return true;
            if (states[raidRef.si].PendingFleets[raidRef.fi].MaterialisedEntityId <= 0) return false;
            for (int i = 0; i < force.Count; i++)
                if (states[force[i].si].PendingFleets[force[i].fi].MaterialisedEntityId <= 0) return false;
            return true;
        }

        private static bool IsWaved(WorldModel world, PendingFleetAction raid)
        {
            return world != null && raid.FleetId != null && world.WavedMeetings.Contains(raid.FleetId);
        }

        /// <summary>
        /// A fight flown in waves is over: one side has no hull left on the field or in the queue
        /// (docs/fight-control.md). Its verdict is the grids', and it is final — read here rather than
        /// recomputed from capacity at the dwell's end, where a side whose hulls all retreated still has
        /// capacity and would read as standing. <paramref name="attackerStands"/>: the raid's side is the
        /// one left. The station is not a side of the site, so a raid that beat every defending hull is
        /// still repelled while the station's own weapons stand, as it is in <see cref="PhysicalResult"/>.
        /// Ends the dwell now. False when <paramref name="si"/>/<paramref name="fi"/> is not a dwell
        /// fight.
        /// </summary>
        public static bool DecideWaved(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            int si, int fi, bool attackerStands, IList<string> log)
        {
            if (states == null || world == null || tuning == null) return false;
            PendingFleetAction raid = states[si].PendingFleets[fi];
            if (!OnFightableDwell(raid)) return false;
            var raidRef = (si, fi);
            List<(int si, int fi)> force = DwellDefenders(states, raidRef, world.UtcNow, tuning);
            SpatialNode node = NodeOf(world, raid.TargetNodeId);
            FightProgress p = EnsureProgress(states, world, tuning, raidRef, force, node);
            if (p.Decided.HasValue) return true;

            IPrefabCatalogue cat = world.PrefabCatalogue;
            double atkCap = FleetCapacity(states, raidRef, cat);
            double defCap = ForceCapacity(states, force, cat) + p.StationShare * StationCapacity(node, tuning);
            double atkRatio = p.AttackerBase > 0 ? Clamp01(atkCap / p.AttackerBase) : 1.0;
            double defRatio = p.DefenderBase > 0 ? Clamp01(defCap / p.DefenderBase) : 1.0;
            double? stationDps = node != null ? world.StationWeaponDps(node.Id) : null;
            bool stationStanding = stationDps.HasValue && stationDps.Value > 0;
            bool repelled = !attackerStands || stationStanding;
            p.Decided = new ShipDuel.Result
            {
                AttackerRatio = atkRatio,
                DefenderRatio = defRatio,
                AttackerBrokeOff = repelled,
                AttackerRepelled = repelled,
                DefenderBrokeOff = attackerStands,
                TurnSummary = "waves " + (attackerStands ? "defence_spent" : "attack_spent")
                              + " atk=" + F2(atkRatio) + " def=" + F2(defRatio)
                              + (attackerStands && stationStanding ? " station_standing" : ""),
                AttackerAttrition = 1.0,
                DefenderAttrition = 1.0,
                AttackerCapacity = Math.Max(1, p.AttackerBase),
                DefenderCapacity = Math.Max(1, p.DefenderBase),
            };
            p.DecidedByGrids = true;
            p.FoughtPhysically = true;
            raid = states[si].PendingFleets[fi];
            if (raid.EstimatedArrivalUtc > world.UtcNow) raid.EstimatedArrivalUtc = world.UtcNow;
            states[si].PendingFleets[fi] = raid;
            return true;
        }

        /// <summary>The fleets of the dwell fight <paramref name="si"/>/<paramref name="fi"/> leads: the
        /// raid first, then every fleet standing as the defence of its target. Empty when that fleet is
        /// not a raid on a dwell the grids may decide. What the host flies in waves.</summary>
        public static List<(int si, int fi)> MeetingOf(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            int si, int fi)
        {
            var meeting = new List<(int si, int fi)>();
            if (states == null || world == null || tuning == null) return meeting;
            PendingFleetAction raid = states[si].PendingFleets[fi];
            if (!OnFightableDwell(raid) || TargetChangedHands(raid, world) != null) return meeting;
            meeting.Add((si, fi));
            meeting.AddRange(DwellDefenders(states, (si, fi), world.UtcNow, tuning));
            return meeting;
        }

        private static int DuelTurns(FactionTuning tuning) { return RangeSchedule.FixedOrder(tuning).Length; }

        /// <summary>The turn being fought at <paramref name="now"/>: the dwell divided evenly.</summary>
        private static int TurnAt(PendingFleetAction raid, DateTime now, int turns)
        {
            int t = (int)Math.Floor(raid.CompletionRatio(now) * turns);
            return t < 0 ? 0 : (t > turns - 1 ? turns - 1 : t);
        }

        /// <summary>A fleet's capacity now, its bands first rebuilt from the hulls it still has at their
        /// integrity, so a hull lost or damaged in the world counts. Stored back.</summary>
        private static double FleetCapacity(IList<FactionState> states, (int si, int fi) r, IPrefabCatalogue cat)
        {
            PendingFleetAction f = states[r.si].PendingFleets[r.fi];
            HullDamage.RescaleBands(ref f, cat);
            states[r.si].PendingFleets[r.fi] = f;
            return TotalDps(f.WeaponBands);
        }

        /// <summary>A side's pool going into the duel's next turns: its capacity now, or — for a side
        /// that began with none, an unarmed one — the duel's one-point pool, whole. The duel gives an
        /// unarmed side that pool too; reading its zero capacity as destroyed would write off hulls
        /// nothing shot.</summary>
        private static double PoolNow(double baseCapacity, double capacityNow)
        {
            return baseCapacity > 0 ? capacityNow : 1.0;
        }

        private static double StationCapacity(SpatialNode node, FactionTuning tuning)
        {
            return TotalDps(node != null ? node.StationWeapons : null) + CombatStrength.StationFlat(node, tuning);
        }

        private static double ForceCapacity(IList<FactionState> states, List<(int si, int fi)> force, IPrefabCatalogue cat)
        {
            double sum = 0;
            for (int i = 0; i < force.Count; i++) sum += FleetCapacity(states, force[i], cat);
            return sum;
        }

        private static int ForceHulls(IList<FactionState> states, List<(int si, int fi)> force)
        {
            int n = 0;
            for (int i = 0; i < force.Count; i++) n += LiveHulls(states[force[i].si].PendingFleets[force[i].fi]);
            return n;
        }

        private static int LiveHulls(PendingFleetAction f)
        {
            return f.Squadron != null && f.Squadron.Count > 0 ? f.HullsToReturn.Count : f.HullCount;
        }

        /// <summary>The raid's fight record, begun now with each side's capacity when it does not exist.</summary>
        private static FightProgress EnsureProgress(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            (int si, int fi) raidRef, List<(int si, int fi)> force, SpatialNode node)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            if (raid.Fight != null) return raid.Fight;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            var p = new FightProgress
            {
                AttackerBase = FleetCapacity(states, raidRef, cat),
                DefenderBase = ForceCapacity(states, force, cat) + StationCapacity(node, tuning),
                AttackerHulls = LiveHulls(raid),
                DefenderHulls = ForceHulls(states, force),
            };
            raid = states[raidRef.si].PendingFleets[raidRef.fi];
            raid.Fight = p;
            states[raidRef.si].PendingFleets[raidRef.fi] = raid;
            return p;
        }

        private static SpatialNode NodeOf(WorldModel world, string nodeId)
        {
            SpatialNode node;
            world.Graph.Nodes.TryGetValue(nodeId ?? "", out node);
            return node;
        }

        private static string DefenderTagOf(IList<FactionState> states, List<(int si, int fi)> force, SpatialNode node)
        {
            if (force.Count > 0) return states[force[0].si].Tag;
            return node != null ? node.OwnerFactionTag : null;
        }

        /// <summary>The defence's bands as the duel merges them: every fleet of the force, then the
        /// station's own weapons.</summary>
        private static List<WeaponBand> DefenceBands(IList<FactionState> states, List<(int si, int fi)> force, SpatialNode node)
        {
            return MergeBands(force.Count > 0 ? ForceBands(states, force[0], force) : null,
                              node != null ? node.StationWeapons : null);
        }

        /// <summary>Both sides of the meeting as the tactic layer reads them — each side's stance, the
        /// learned pick behind it and whether the outcome is learned from.</summary>
        private static DuelSides Sides(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            (int si, int fi) raidRef, List<(int si, int fi)> force, string defenderTag, SpatialNode node)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            return DuelStances(world, states, raidRef, force, defenderTag, node, raid.WeaponBands,
                               DefenceBands(states, force, node), CombatStrength.StationFlat(node, tuning), tuning);
        }

        /// <summary>Fights the turns from <see cref="FightProgress.TurnsFought"/> up to
        /// <paramref name="toTurn"/> with the duel, from both sides' capacity now. A segment that ends
        /// the fight is held as <see cref="FightProgress.Decided"/> and ends the dwell now; any other
        /// writes what it did into the hulls, so the grids that follow carry it. Answers whether any
        /// turn was fought.</summary>
        private static bool FightAbstractTurns(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log, (int si, int fi) raidRef, List<(int si, int fi)> force, SpatialNode node,
            FightProgress p, int toTurn)
        {
            if (p.Decided.HasValue || toTurn <= p.TurnsFought) return false;
            IPrefabCatalogue cat = world.PrefabCatalogue;
            double atkCap = FleetCapacity(states, raidRef, cat);
            double stationCap = StationCapacity(node, tuning);
            double defCap = ForceCapacity(states, force, cat) + p.StationShare * stationCap;
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            DuelSides sides = Sides(states, world, tuning, raidRef, force, DefenderTagOf(states, force, node), node);
            List<WeaponBand> defenderBands = DefenceBands(states, force, node);
            DuelMunitions raidMunitions = RaidMunitions(raidRef, states, world, tuning);
            DuelMunitions forceMunitions = force.Count > 0
                ? ForceMunitions(states, force[0], force, node != null ? node.StationWeapons : null, world, tuning) : null;
            ShipDuel.Result r = ShipDuel.Fight(raid.WeaponBands, defenderBands, CombatStrength.StationFlat(node, tuning),
                tuning, rng, raidMunitions, forceMunitions, true, sides.Attacker, sides.Defender,
                p.TurnsFought, toTurn, p.AttackerBase, PoolNow(p.AttackerBase, atkCap), p.DefenderBase, PoolNow(p.DefenderBase, defCap));
            CommitMunitions(raidMunitions, forceMunitions, states, world, log);
            p.TurnsFought = toTurn;
            p.FoughtAbstractly = true;

            if (r.AttackerDestroyed || r.DefenderDestroyed || r.AttackerBrokeOff || r.DefenderBrokeOff)
            {
                p.Decided = r;
                raid = states[raidRef.si].PendingFleets[raidRef.fi];
                if (raid.EstimatedArrivalUtc > world.UtcNow) raid.EstimatedArrivalUtc = world.UtcNow;
                states[raidRef.si].PendingFleets[raidRef.fi] = raid;
                return true;
            }
            raid = states[raidRef.si].PendingFleets[raidRef.fi];
            RecordAttrition(ref raid, r.AttackerAttrition, "duel_prefix", world, log);
            states[raidRef.si].PendingFleets[raidRef.fi] = raid;
            if (force.Count > 0) RecordForceAttrition(states, force[0], force, r.DefenderAttrition, "duel_prefix", world, log);
            p.StationShare *= r.DefenderAttrition;
            return true;
        }

        /// <summary>Each side's capacity now as a share of what it brought, for a handoff line.</summary>
        private static void Shares(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            (int si, int fi) raidRef, List<(int si, int fi)> force, SpatialNode node, FightProgress p,
            out double atk, out double def)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            double atkCap = FleetCapacity(states, raidRef, cat);
            double defCap = ForceCapacity(states, force, cat) + p.StationShare * StationCapacity(node, tuning);
            atk = p.AttackerBase > 0 ? Clamp01(atkCap / p.AttackerBase) : 1.0;
            def = p.DefenderBase > 0 ? Clamp01(defCap / p.DefenderBase) : 1.0;
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        private static string HandoffLine(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            (int si, int fi) raidRef, List<(int si, int fi)> force, SpatialNode node, FightProgress p,
            string dir, int turns, DateTime now)
        {
            double atk, def;
            Shares(states, world, tuning, raidRef, force, node, p, out atk, out def);
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            var sb = new StringBuilder("FIGHT_HANDOFF ");
            sb.Append(raid.LogId).Append(' ').Append(raid.ActorTag).Append(" dir=").Append(dir)
              .Append(" @").Append(raid.TargetNodeId)
              .Append(" dwell=").Append(F2(raid.CompletionRatio(now)))
              .Append(" turns_fought=").Append(p.TurnsFought).Append('/').Append(turns);
            if (dir == SourcePhysical)
                sb.Append(" bracket=").Append(BracketAt(states, world, tuning, raidRef, force, node, p.TurnsFought, turns)
                                              .ToString("0", CultureInfo.InvariantCulture)).Append('m');
            sb.Append(" atk=").Append(F2(atk)).Append(" hulls=").Append(HullDamage.Describe(raid));
            sb.Append(" def=").Append(F2(def));
            if (p.StationShare < 1.0) sb.Append(" station=").Append(F2(p.StationShare));
            for (int i = 0; i < force.Count; i++)
            {
                PendingFleetAction d = states[force[i].si].PendingFleets[force[i].fi];
                sb.Append(' ').Append(d.LogId).Append(' ').Append(d.ActorTag).Append(" hulls=").Append(HullDamage.Describe(d));
            }
            return sb.ToString();
        }

        /// <summary>The bracket turn <paramref name="turn"/> is fought at, as the duel would schedule it
        /// for the two sides as they stand.</summary>
        private static double BracketAt(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            (int si, int fi) raidRef, List<(int si, int fi)> force, SpatialNode node, int turn, int turns)
        {
            int t = turn < 0 ? 0 : (turn > turns - 1 ? turns - 1 : turn);
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            DuelSides sides = Sides(states, world, tuning, raidRef, force, DefenderTagOf(states, force, node), node);
            DuelTurn[] plan = RangeSchedule.Plan(raid.WeaponBands, DefenceBands(states, force, node),
                                                 CombatStrength.StationFlat(node, tuning), sides.Attacker, sides.Defender, tuning);
            return plan[t].RangeM;
        }

        /// <summary>
        /// Follows every raid dwelling at its target whose fight the grids may decide, and hands the
        /// fight between the grids and the duel as its fleets enter and leave the world. Called at
        /// the start of every arrivals pass; the host also calls it when it takes grids out itself.
        /// <list type="bullet">
        ///   <item><b>To the grids</b> when every fleet of the meeting is in the world: the turns the
        ///   dwell has passed since the last accounted one are fought by the duel first, and what they
        ///   did is written into the hulls (a segment that ends the fight ends the dwell instead).
        ///   <c>FIGHT_HANDOFF … dir=physical</c> when any of the fight had been abstract.</item>
        ///   <item><b>To the duel</b> when any of them leaves: the turn in progress counts as fought by
        ///   the grids, and the duel fights the rest at the end of the dwell from the hulls as the
        ///   grids left them. <c>FIGHT_HANDOFF … dir=abstract</c>.</item>
        /// </list>
        /// A raid with no record and no grids is left alone: its fight is the ordinary duel.
        /// </summary>
        public static void ObserveFights(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            IRandom rng, IList<string> log)
        {
            if (states == null || world == null || tuning == null) return;
            DateTime now = world.UtcNow;
            int turns = DuelTurns(tuning);
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction raid = fleets[fi];
                    if (!OnFightableDwell(raid) || TargetChangedHands(raid, world) != null) continue;
                    FightProgress p = raid.Fight;
                    if (p != null && p.Decided.HasValue) continue;
                    if (p == null && raid.MaterialisedEntityId <= 0 && !IsWaved(world, raid)) continue;

                    var raidRef = (si, fi);
                    List<(int si, int fi)> force = DwellDefenders(states, raidRef, now, tuning);
                    SpatialNode node = NodeOf(world, raid.TargetNodeId);
                    bool physical = MeetingMaterialised(states, world, raidRef, force);

                    if (physical && (p == null || !p.Physical))
                    {
                        p = EnsureProgress(states, world, tuning, raidRef, force, node);
                        FightAbstractTurns(states, world, tuning, rng, log, raidRef, force, node, p, TurnAt(raid, now, turns));
                        if (p.Decided.HasValue) continue;
                        p.Physical = true;
                        p.FoughtPhysically = true;
                        if (p.FoughtAbstractly || p.TurnsFought > 0)
                            log.Add(HandoffLine(states, world, tuning, raidRef, force, node, p, SourcePhysical, turns, now));
                    }
                    else if (!physical && p != null && p.Physical)
                    {
                        p.Physical = false;
                        p.TurnsFought = Math.Max(p.TurnsFought, Math.Min(turns, TurnAt(raid, now, turns) + 1));
                        log.Add(HandoffLine(states, world, tuning, raidRef, force, node, p, "abstract", turns, now));
                    }
                }
            }
        }

        /// <summary>
        /// Called by the host as it begins to materialise fleet <paramref name="si"/>/<paramref name="fi"/>.
        /// When that fleet is one side of a dwell the grids may decide — the raid, or a defender
        /// standing at its target — the fight is brought up to now with the duel first, so the hulls
        /// appear at the integrity it left them, and the answer says how far from its opponent to put
        /// the grid: the separation of the bracket now being fought.
        /// </summary>
        public static MeetingSpawn BeforeMaterialise(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            int si, int fi, IRandom rng, IList<string> log)
        {
            var none = new MeetingSpawn { InMeeting = false, OpponentSi = -1, OpponentFi = -1 };
            if (states == null || world == null || tuning == null) return none;
            DateTime now = world.UtcNow;
            PendingFleetAction fleet = states[si].PendingFleets[fi];
            (int si, int fi) raidRef = (-1, -1);
            if (OnFightableDwell(fleet)) { if (TargetChangedHands(fleet, world) == null) raidRef = (si, fi); }
            else if (fleet.StandsAsDefender(now, tuning)) raidRef = RaidAt(states, fleet.TargetNodeId, states[si].Tag, now);
            if (raidRef.si < 0) return none;

            List<(int si, int fi)> force = DwellDefenders(states, raidRef, now, tuning);
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            SpatialNode node = NodeOf(world, raid.TargetNodeId);
            int turns = DuelTurns(tuning);
            FightProgress p = EnsureProgress(states, world, tuning, raidRef, force, node);
            if (!p.Physical)
                FightAbstractTurns(states, world, tuning, rng, log, raidRef, force, node, p,
                                   TurnAt(states[raidRef.si].PendingFleets[raidRef.fi], now, turns));

            var spawn = new MeetingSpawn
            {
                InMeeting = true,
                SeparationM = Math.Max(MeetingSpawn.MinSeparationM,
                                       BracketAt(states, world, tuning, raidRef, force, node, p.TurnsFought, turns)),
                OpponentSi = -1,
                OpponentFi = -1,
            };
            raid = states[raidRef.si].PendingFleets[raidRef.fi];
            if (raidRef.si != si || raidRef.fi != fi)
            {
                spawn.OpponentSi = raidRef.si;
                spawn.OpponentFi = raidRef.fi;
                spawn.OpponentPosKm = raid.CurrentPosKm(now, tuning);
            }
            else if (force.Count > 0)
            {
                spawn.OpponentSi = force[0].si;
                spawn.OpponentFi = force[0].fi;
                spawn.OpponentPosKm = states[force[0].si].PendingFleets[force[0].fi].CurrentPosKm(now, tuning);
            }
            else spawn.OpponentPosKm = node != null ? node.Position : raid.TargetPosKm;
            IPlanetCatalogue planets = world.Planets ?? NullPlanetCatalogue.Instance;
            spawn.FallbackDirection = planets.SurfaceNormal(spawn.OpponentPosKm);
            return spawn;
        }

        /// <summary>The first raid of another faction dwelling at <paramref name="nodeId"/> on a fight
        /// still to be decided, or (-1, -1).</summary>
        private static (int si, int fi) RaidAt(IList<FactionState> states, string nodeId, string defenderTag, DateTime now)
        {
            if (nodeId == null) return (-1, -1);
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null || states[si].Tag == defenderTag) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                    if (OnFightableDwell(fleets[fi]) && fleets[fi].TargetNodeId == nodeId) return (si, fi);
            }
            return (-1, -1);
        }

        /// <summary>
        /// The meeting at the end of a raid's dwell. A raid whose fight never involved the grids goes
        /// to the duel (<c>src=duel</c>). Otherwise:
        /// <list type="bullet">
        ///   <item>still in the world: the grids decide (<c>src=physical</c>, or <c>handoff</c> when
        ///   part of it was abstract). Each side's ratio is its capacity now over what it brought; a
        ///   side at zero is destroyed; with range contested a side below its break-off fraction left.
        ///   The hulls already carry what happened, so the attrition factors are 1.</item>
        ///   <item>out of the world: the duel fights the turns left from the hulls as they stand,
        ///   against the pools the fight began with, so what the grids already took is neither
        ///   refunded nor taken twice (<c>src=handoff</c>, or <c>duel</c> when the grids never
        ///   fought it).</item>
        /// </list>
        /// Either way both sides learn from what was measured (<see cref="LearnFromMeasured"/>).
        /// <para>Undecided repels the attacker on every path. The grids decide it themselves
        /// (<see cref="PhysicalResult"/>); a result the duel had a hand in (<c>src=duel</c> or
        /// <c>handoff</c>) that leaves the defence standing and ahead on lost share, or neither side touched,
        /// (<see cref="ShipDuel.StandingDefenceRepels"/>) is marked repelled the same way. A meeting on the
        /// way to the target (<paramref name="meetNode"/>) is not a dwell, and is left as it fought.</para>
        /// </summary>
        private static ShipDuel.Result ResolveMeeting(
            List<WeaponBand> attackerBands, List<WeaponBand> defenderBands, long defenderFlatPower,
            FactionTuning tuning, IRandom rng, WorldModel world, IList<FactionState> states,
            (int si, int fi) attackerRef, List<(int si, int fi)> defenderFleets, string defenderTag, SpatialNode node,
            string meetNode, IList<string> log, DuelMunitions attackerMunitions = null, DuelMunitions defenderMunitions = null)
        {
            PendingFleetAction raid = states[attackerRef.si].PendingFleets[attackerRef.fi];
            FightProgress p = raid.Fight;
            if (meetNode != null || p == null || raid.CurrentStopKind != FleetStopKind.Engage)
            {
                ShipDuel.Result plain = ResolveShipDuel(attackerBands, defenderBands, defenderFlatPower, tuning, rng, world,
                    states, attackerRef, defenderFleets, defenderTag, node, log, attackerMunitions, defenderMunitions);
                plain.Source = SourceDuel;
                if (meetNode == null && ShipDuel.StandingDefenceRepels(plain, TotalDps(defenderBands) + defenderFlatPower))
                    ShipDuel.MarkRepelled(ref plain);
                raid = states[attackerRef.si].PendingFleets[attackerRef.fi];
                if (raid.HullsMeasured)
                {
                    raid.HullsMeasured = false;
                    states[attackerRef.si].PendingFleets[attackerRef.fi] = raid;
                }
                return plain;
            }

            List<(int si, int fi)> force = defenderFleets ?? new List<(int si, int fi)>();
            int turns = DuelTurns(tuning);
            IPrefabCatalogue cat = world.PrefabCatalogue;
            double atkCap = FleetCapacity(states, attackerRef, cat);
            double guardCap = ForceCapacity(states, force, cat);
            double defCap = guardCap + p.StationShare * StationCapacity(node, tuning);
            DuelSides sides = Sides(states, world, tuning, attackerRef, force, defenderTag, node);
            if (sides.Learning) LogPicks(sides, log);

            ShipDuel.Result r;
            if (p.Decided.HasValue)
            {
                r = p.Decided.Value;
                r.Source = p.DecidedByGrids ? SourcePhysical : p.FoughtPhysically ? SourceHandoff : SourceDuel;
            }
            else if (p.Physical)
            {
                double? stationDps = node != null ? world.StationWeaponDps(node.Id) : null;
                bool stationStanding = stationDps.HasValue && stationDps.Value > 0;
                raid = states[attackerRef.si].PendingFleets[attackerRef.fi];
                r = PhysicalResult(p, sides, atkCap, defCap, turns,
                                   !p.FoughtAbstractly && (GuardsStanding(force, guardCap) || stationStanding),
                                   stationStanding ? stationDps.Value : 0,
                                   p.FoughtAbstractly || AttackerHitAnything(raid));
                r.Source = p.FoughtAbstractly ? SourceHandoff : SourcePhysical;
            }
            else
            {
                raid = states[attackerRef.si].PendingFleets[attackerRef.fi];
                r = ShipDuel.Fight(raid.WeaponBands, DefenceBands(states, force, node), defenderFlatPower, tuning, rng,
                    attackerMunitions, defenderMunitions, true, sides.Attacker, sides.Defender,
                    Math.Min(p.TurnsFought, turns), turns, p.AttackerBase, PoolNow(p.AttackerBase, atkCap), p.DefenderBase, PoolNow(p.DefenderBase, defCap));
                if (r.TurnSummary.Length == 0) r.TurnSummary = "no turns left";
                r.Source = p.FoughtPhysically ? SourceHandoff : SourceDuel;
            }
            // The grids' own rule decided src=physical; wherever the duel had a hand, a defence it
            // left standing and ahead on lost share repels the attacker the same way.
            if (r.Source != SourcePhysical && !r.AttackerRepelled
                && ShipDuel.StandingDefenceRepels(r, TotalDps(defenderBands) + defenderFlatPower))
                ShipDuel.MarkRepelled(ref r);

            r.Tactic = new DuelTactic
            {
                AttackerArm = sides.Attacker.Arm, AttackerBreakOff = sides.Attacker.BreakOff,
                DefenderArm = sides.Defender.Arm, DefenderBreakOff = sides.Defender.BreakOff,
                BracketsM = r.BracketsFought,
            };
            raid = states[attackerRef.si].PendingFleets[attackerRef.fi];
            raid.DuelTactic = r.Tactic;
            raid.HullsMeasured = r.Source == SourcePhysical;
            states[attackerRef.si].PendingFleets[attackerRef.fi] = raid;
            PhysicalFightOutcome o = Outcome(states, attackerRef, force, defenderTag, p, r);
            r.TurnSummary = r.TurnSummary + " | " + DescribeOutcome(o);
            if (sides.Records) LearnFromMeasured(o, sides, node, tuning, world, log);
            ClearFightDamage(states, attackerRef, force);
            return r;
        }

        /// <summary>The meeting read off the grids, in the duel's terms.</summary>
        /// <summary>Whether the defending fleets still stand at the dwell's end: at least one Guard is
        /// still in the fight (a Guard whose grid was destroyed has left the force) and between them
        /// they can still fire. A station counts separately, by the weapons on its grid
        /// (<see cref="WorldModel.StationWeaponDps"/>).</summary>
        internal static bool GuardsStanding(List<(int si, int fi)> force, double guardCapacity)
        {
            return force.Count > 0 && guardCapacity > 0;
        }

        /// <summary>Whether the attacker's grids hit anything in the fight: damage the hook credited
        /// to them, or rounds the target's safe zone absorbed. Both are the host's measurements; a raid
        /// that fired at a protected station did no damage, but it did attack.</summary>
        internal static bool AttackerHitAnything(PendingFleetAction raid)
        {
            return raid.FightDamageDealt > 0 || raid.FightZoneHits > 0;
        }

        /// <summary>The meeting read off the grids, in the duel's terms. With
        /// <paramref name="repelWhileStanding"/> — the grids fought all of it and the defence still
        /// stands: a Guard, or a station whose grid still has working weapons
        /// (<paramref name="stationDps"/>, 0 when it is not what stands) — the fight is undecided, and undecided repels the attacker
        /// (<see cref="ShipDuel.Result.AttackerRepelled"/>): it lands only past a defence that was
        /// destroyed or broke off. Without <paramref name="attackerHit"/> — the grids fought all of it
        /// and the attacker's hit nothing, not the defence and not the zone in front of it — it is
        /// repelled whatever stands (<c>no_hits</c>): an attack that never fired did not get through.</summary>
        private static ShipDuel.Result PhysicalResult(FightProgress p, DuelSides sides, double atkCap, double defCap, int turns,
                                                      bool repelWhileStanding, double stationDps, bool attackerHit)
        {
            double atkRatio = p.AttackerBase > 0 ? Clamp01(atkCap / p.AttackerBase) : 1.0;
            double defRatio = p.DefenderBase > 0 ? Clamp01(defCap / p.DefenderBase) : 1.0;
            bool atkDestroyed = p.AttackerBase > 0 && atkCap <= 0, defDestroyed = p.DefenderBase > 0 && defCap <= 0;
            bool atkLeft = false, defLeft = false;
            if (!atkDestroyed && !defDestroyed)
            {
                atkLeft = sides.Attacker.CanBreakOff && atkRatio < TacticChoice.BreakOffFraction(sides.Attacker.BreakOff);
                defLeft = sides.Defender.CanBreakOff && defRatio < TacticChoice.BreakOffFraction(sides.Defender.BreakOff);
            }
            bool standing = repelWhileStanding && !atkDestroyed && !defLeft;
            bool unanswered = !attackerHit && !atkDestroyed && !defDestroyed && !defLeft;
            bool repelled = standing || unanswered;
            string summary = "grids T" + (Math.Min(p.TurnsFought, turns - 1) + 1) + "-T" + turns
                             + " atk=" + F2(atkRatio) + " def=" + F2(defRatio)
                             + (atkLeft || defLeft ? " broke_off=" + (atkLeft && defLeft ? "both" : atkLeft ? "atk" : "def") : "")
                             + (standing ? " defence_standing" : "")
                             + (standing && stationDps > 0 ? " station_dps=" + stationDps.ToString("0", CultureInfo.InvariantCulture) : "")
                             + (unanswered ? " no_hits" : "");
            return new ShipDuel.Result
            {
                AttackerRatio = atkRatio,
                DefenderRatio = defRatio,
                AttackerDestroyed = atkDestroyed,
                DefenderDestroyed = defDestroyed,
                AttackerBrokeOff = atkLeft || repelled,
                AttackerRepelled = repelled,
                DefenderBrokeOff = defLeft,
                TurnSummary = summary,
                AttackerAttrition = 1.0,
                DefenderAttrition = 1.0,
                AttackerCapacity = Math.Max(1, p.AttackerBase),
                DefenderCapacity = Math.Max(1, p.DefenderBase),
            };
        }

        /// <summary>What the fight did to each side, measured: for the resolution line and the learner.
        /// Damage is what the host's damage hook attributed to each side's grids when the grids fought
        /// all of it and saw any; otherwise each side's capacity lost, the duel's own unit, which also
        /// covers the turns the duel fought.</summary>
        private static PhysicalFightOutcome Outcome(IList<FactionState> states, (int si, int fi) raidRef,
            List<(int si, int fi)> force, string defenderTag, FightProgress p, ShipDuel.Result r)
        {
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            var o = new PhysicalFightOutcome
            {
                Source = r.Source,
                AttackerTag = raid.ActorTag,
                DefenderTag = defenderTag,
                AttackerRatio = r.AttackerRatio,
                DefenderRatio = r.DefenderRatio,
                AttackerCapacityDealt = p.DefenderBase * (1.0 - r.DefenderRatio),
                DefenderCapacityDealt = p.AttackerBase * (1.0 - r.AttackerRatio),
                AttackerDamageDealt = raid.FightDamageDealt,
                AttackerDamageTaken = raid.FightDamageTaken,
                AttackerZoneHits = raid.FightZoneHits,
                AttackerHullsBrought = p.AttackerHulls,
                AttackerHullsLost = r.AttackerDestroyed ? p.AttackerHulls : Math.Max(0, p.AttackerHulls - LiveHulls(raid)),
                DefenderHullsBrought = p.DefenderHulls,
                DefenderHullsLost = r.DefenderDestroyed ? p.DefenderHulls : Math.Max(0, p.DefenderHulls - ForceHulls(states, force)),
            };
            for (int i = 0; i < force.Count; i++)
            {
                PendingFleetAction d = states[force[i].si].PendingFleets[force[i].fi];
                o.DefenderDamageDealt += d.FightDamageDealt;
                o.DefenderDamageTaken += d.FightDamageTaken;
            }
            return o;
        }

        /// <summary>Damage dealt and taken for the learner, one side's view: the hook's when it saw the
        /// whole fight, the capacity figures otherwise.</summary>
        public static void MeasuredDamage(PhysicalFightOutcome o, bool attacker, out double dealt, out double taken)
        {
            bool hook = o.Source == SourcePhysical
                        && o.AttackerDamageDealt + o.AttackerDamageTaken + o.DefenderDamageDealt + o.DefenderDamageTaken > 0;
            if (hook)
            {
                // The attacker's hits on the defence and the defence's on the attacker are the same
                // exchange read from either end; each side's own record is the one it saw.
                dealt = attacker ? o.AttackerDamageDealt : o.DefenderDamageDealt;
                taken = attacker ? o.AttackerDamageTaken : o.DefenderDamageTaken;
                return;
            }
            dealt = attacker ? o.AttackerCapacityDealt : o.DefenderCapacityDealt;
            taken = attacker ? o.DefenderCapacityDealt : o.AttackerCapacityDealt;
        }

        /// <summary>Both sides learn from a fight the grids took part in, through the same
        /// <see cref="TacticMemory.Record"/> the abstract duel feeds: measured damage and hulls lost,
        /// against what the commit arithmetic predicted over the whole schedule.</summary>
        private static void LearnFromMeasured(PhysicalFightOutcome o, DuelSides sides, SpatialNode node,
            FactionTuning tuning, WorldModel world, IList<string> log)
        {
            List<WeaponBand> atkBands = sides.AttackerBands, defBands = sides.DefenderBands;
            long flat = CombatStrength.StationFlat(node, tuning);
            DuelTurn[] plan = RangeSchedule.Plan(atkBands, defBands, flat, sides.Attacker, sides.Defender, tuning);
            double atkPredicted = CombatStrength.ScheduleDamage(atkBands, 0, plan, tuning);
            double defPredicted = CombatStrength.ScheduleDamage(defBands, flat, plan, tuning);
            double dealt, taken;
            if (sides.AttackerState != null)
            {
                MeasuredDamage(o, true, out dealt, out taken);
                TacticReview r = sides.AttackerState.TacticMemory.Record(new TacticOutcome
                {
                    OpponentTag = sides.DefenderTag, Context = sides.AttackerContext.Key,
                    Arm = sides.Attacker.Arm, BreakOff = sides.Attacker.BreakOff,
                    DamageDealt = dealt, DamageTaken = taken,
                    PredictedDealt = atkPredicted, PredictedTaken = defPredicted,
                    HullsBrought = o.AttackerHullsBrought, HullsLost = o.AttackerHullsLost,
                    Commanded = sides.AttackerCommanded,
                }, sides.AttackerState.PersonalityId, world.UtcNow);
                if (r.Recorded && DebugTraces.Tactics && log != null)
                    log.Add(TacticMemory.FormatReview(sides.AttackerState.Tag, r));
            }
            if (sides.DefenderState != null)
            {
                MeasuredDamage(o, false, out dealt, out taken);
                TacticReview r = sides.DefenderState.TacticMemory.Record(new TacticOutcome
                {
                    OpponentTag = sides.AttackerTag, Context = sides.DefenderContext.Key,
                    Arm = sides.Defender.Arm, BreakOff = sides.Defender.BreakOff,
                    DamageDealt = dealt, DamageTaken = taken,
                    PredictedDealt = defPredicted, PredictedTaken = atkPredicted,
                    HullsBrought = o.DefenderHullsBrought, HullsLost = o.DefenderHullsLost,
                    Commanded = sides.DefenderCommanded,
                }, sides.DefenderState.PersonalityId, world.UtcNow);
                if (r.Recorded && DebugTraces.Tactics && log != null)
                    log.Add(TacticMemory.FormatReview(sides.DefenderState.Tag, r));
            }
        }

        private static string DescribeOutcome(PhysicalFightOutcome o)
        {
            return "hulls_lost atk=" + o.AttackerHullsLost + "/" + o.AttackerHullsBrought
                   + " def=" + o.DefenderHullsLost + "/" + o.DefenderHullsBrought
                   + " hit atk=" + o.AttackerDamageDealt.ToString("0", CultureInfo.InvariantCulture)
                   + " def=" + o.DefenderDamageDealt.ToString("0", CultureInfo.InvariantCulture)
                   + (o.AttackerZoneHits > 0 ? " zone atk=" + o.AttackerZoneHits.ToString("0", CultureInfo.InvariantCulture) : "");
        }

        private static void ClearFightDamage(IList<FactionState> states, (int si, int fi) raidRef, List<(int si, int fi)> force)
        {
            ClearFightDamage(states, raidRef);
            for (int i = 0; i < force.Count; i++) ClearFightDamage(states, force[i]);
        }

        private static void ClearFightDamage(IList<FactionState> states, (int si, int fi) r)
        {
            PendingFleetAction f = states[r.si].PendingFleets[r.fi];
            f.FightDamageDealt = 0;
            f.FightDamageTaken = 0;
            f.FightZoneHits = 0;
            states[r.si].PendingFleets[r.fi] = f;
        }

        /// <summary>
        /// The stance a materialised fleet flies in a dwell fight when its faction learns tactics (a
        /// bench scenario always does): the pick <see cref="TacticChoice.For(FactionState,double,string,TacticContext,bool,DateTime,out TacticPick)"/>
        /// makes for this meeting, the same one the duel and the commit gates use. False when the fleet
        /// is in no such fight or nothing is learned, and the flight controller keeps its default.
        /// </summary>
        public static bool LearnedStanceFor(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            int si, int fi, out TacticArm arm, out BreakOffLevel breakOff)
        {
            arm = TacticResolver.DefaultArm;
            breakOff = TacticResolver.DefaultBreakOff;
            if (states == null || world == null || tuning == null) return false;
            DateTime now = world.UtcNow;
            PendingFleetAction fleet = states[si].PendingFleets[fi];
            (int si, int fi) raidRef = (-1, -1);
            if (EngagesOnArrival(fleet.Kind) && fleet.CurrentStopKind == FleetStopKind.Engage && fleet.TargetNodeId != null)
                raidRef = (si, fi);
            else if (fleet.StandsAsDefender(now, tuning)) raidRef = RaidAt(states, fleet.TargetNodeId, states[si].Tag, now);
            if (raidRef.si < 0) return false;
            PendingFleetAction raid = states[raidRef.si].PendingFleets[raidRef.fi];
            if (!(world.TacticLearning || raid.IsBench)) return false;
            List<(int si, int fi)> force = DwellDefenders(states, raidRef, now, tuning);
            SpatialNode node = NodeOf(world, raid.TargetNodeId);
            DuelSides sides = Sides(states, world, tuning, raidRef, force, DefenderTagOf(states, force, node), node);
            if (!sides.Learning) return false;
            DuelStance stance = raidRef.si == si && raidRef.fi == fi ? sides.Attacker : sides.Defender;
            arm = stance.Arm;
            breakOff = stance.BreakOff;
            return true;
        }
    }
}
