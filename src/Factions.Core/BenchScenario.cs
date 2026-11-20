using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>Effects a bench fleet is explicitly allowed. Everything not named here is
    /// suppressed for a bench fleet.</summary>
    [Flags]
    public enum BenchFlags
    {
        None = 0,
        /// <summary>The fight is filed to <see cref="FightLedger"/> like a real one, so a learner
        /// reading the ledger can be tested on a staged fight.</summary>
        Ledger = 1,
        /// <summary>The fleet is visible to the threat indexes: a bench raid on a real node draws
        /// the reactive defence, patrol diversions and bounty offers a real raid would.</summary>
        React = 2,
        /// <summary>The fleet may shoot any hostile grid, the players nearby included, rather than
        /// only its scenario's opponent. See <see cref="TargetSelection.ScopeFor"/>.</summary>
        HostileAny = 4,
    }

    /// <summary>
    /// Bench scenarios (<c>factions bench fight|raid</c>): real <see cref="PendingFleetAction"/>s,
    /// staged on command near a player, that run the ordinary pipeline -- materialisation, the flight
    /// controller, the meeting duel at the end of the <see cref="FleetStopKind.Engage"/> dwell -- so
    /// every fleet-tactics slice can be watched in minutes instead of waiting for a raid to happen
    /// near someone.
    ///
    /// <para><b>A bench fleet changes nothing outside the bench.</b> It is born on its dwell (or its
    /// Guard duty) with no transit leg, drew no hull from stock and paid nothing, and every place a
    /// resolution lands an effect reads <see cref="PendingFleetAction.IsBench"/>: no territory
    /// credit, hold damage, loot, incident, relation lift, campaign outcome, event, hull credit or
    /// hull loss line, and it robs no convoy. <see cref="FilesFight"/> and
    /// <see cref="SeenAsThreat"/> are the two exceptions a scenario can ask for. Real defenders a
    /// bench raid meets at a real node fight it for real: their munitions and hulls are spent as
    /// against any raid, physically or in the duel.</para>
    ///
    /// <para>Never persisted (<see cref="Persistence"/> skips them), so a restart ends every
    /// scenario; grids left in the world are closed by the reaper like any grid nothing holds.</para>
    /// </summary>
    public static class BenchScenario
    {
        /// <summary>Node id a <c>bench fight</c> meets at. Not a graph node: the meeting code matches
        /// fleets on <see cref="PendingFleetAction.TargetNodeId"/> alone, and every graph lookup of
        /// an unknown id finds nothing, so no real station or Guard can join the fight.</summary>
        public const string NodePrefix = "bench:";
        public const int MaxLabelLength = 24;

        public static string FightNodeId(string label) { return NodePrefix + label; }

        /// <summary>The fleet id of one side of a scenario. Carries the label, so every ordinary
        /// lifecycle line (<c>SPAWNED</c>, <c>FLEET_HOLDING_STATION</c>, <c>GUARD_INTERCEPT</c>...)
        /// names the scenario it belongs to.</summary>
        public static string FleetIdFor(string label, string side) { return "b." + label + "." + side; }

        /// <summary>1..<see cref="MaxLabelLength"/> of letters, digits, '-' and '_'.</summary>
        public static bool IsValidLabel(string label)
        {
            if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength) return false;
            for (int i = 0; i < label.Length; i++)
            {
                char c = label[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Whether this fleet's fights are filed to the <see cref="FightLedger"/>: every real
        /// fleet, and a bench fleet only when it asked for <see cref="BenchFlags.Ledger"/>.</summary>
        public static bool FilesFight(PendingFleetAction fleet)
        {
            return !fleet.IsBench || (fleet.BenchFlags & BenchFlags.Ledger) != 0;
        }

        /// <summary>Whether this fleet is read as a threat by the defence and contract indexes: every
        /// real fleet, and a bench fleet only when it asked for <see cref="BenchFlags.React"/>.</summary>
        public static bool SeenAsThreat(PendingFleetAction fleet)
        {
            return !fleet.IsBench || (fleet.BenchFlags & BenchFlags.React) != 0;
        }

        private static bool IsAttackKind(ActionKind kind)
        {
            return kind == ActionKind.Raid || kind == ActionKind.Retaliate || kind == ActionKind.Conquer;
        }

        /// <summary>The attacking side: a Raid already on its <see cref="FleetStopKind.Engage"/>
        /// dwell at <paramref name="targetNodeId"/>, holding at <paramref name="posKm"/> until
        /// <paramref name="now"/> + <paramref name="dwellSeconds"/>, when the meeting duel runs.
        /// One grid per entry of <paramref name="prefabs"/>.</summary>
        public static PendingFleetAction Attacker(string label, string side, string actorTag, string targetTag,
            string targetNodeId, Vec3 posKm, IList<string> prefabs, IPrefabCatalogue catalogue,
            DateTime now, double dwellSeconds, BenchFlags flags)
        {
            PendingFleetAction f = Common(label, side, ActionKind.Raid, actorTag, targetNodeId, posKm, prefabs, catalogue, flags);
            f.TargetTag = targetTag;
            f.DepartedUtc = now;
            f.EstimatedArrivalUtc = now.AddSeconds(dwellSeconds);
            f.CurrentStopKind = FleetStopKind.Engage;
            return f;
        }

        /// <summary>How long a defender's duty window outlasts the attacker's dwell. The meeting duel
        /// runs on the first arrivals pass after the dwell ends, not at its exact end, and a Guard
        /// whose window has closed by then no longer defends. <see cref="Conclude"/> ends the window
        /// with the scenario, so the tail is never stood out.</summary>
        public const double DefenderTailSeconds = 120;

        /// <summary>The defending side: a Guard standing its duty window at
        /// <paramref name="nodeId"/>, holding at <paramref name="posKm"/>. Its arrival effect is
        /// already spent (<see cref="PendingFleetAction.Resolved"/>), exactly as a real Guard's is
        /// once it stands. The window is placed to end <see cref="DefenderTailSeconds"/> after an
        /// attacker's <paramref name="dwellSeconds"/> dwell staged at the same moment, when
        /// <see cref="FactionTuning.GuardDuration"/> is that long; otherwise it runs the full
        /// duration from now.</summary>
        public static PendingFleetAction Defender(string label, string side, string actorTag, string nodeId,
            Vec3 posKm, IList<string> prefabs, IPrefabCatalogue catalogue, DateTime now, double dwellSeconds,
            FactionTuning tuning, BenchFlags flags)
        {
            PendingFleetAction f = Common(label, side, ActionKind.Guard, actorTag, nodeId, posKm, prefabs, catalogue, flags);
            DateTime arrived = now.AddSeconds(dwellSeconds + DefenderTailSeconds) - tuning.GuardDuration;
            if (arrived > now) arrived = now;
            f.DepartedUtc = arrived;
            f.EstimatedArrivalUtc = arrived;
            f.CurrentStopKind = FleetStopKind.Service;
            f.Resolved = true;
            return f;
        }

        private static PendingFleetAction Common(string label, string side, ActionKind kind, string actorTag,
            string nodeId, Vec3 posKm, IList<string> prefabs, IPrefabCatalogue catalogue, BenchFlags flags)
        {
            var hulls = new List<string>(prefabs);
            return new PendingFleetAction
            {
                Kind = kind,
                ActorTag = actorTag,
                TargetNodeId = nodeId,
                OriginPosKm = posKm,
                TargetPosKm = posKm,
                ShipPrefabId = hulls.Count > 0 ? hulls[0] : "",
                ConvoySize = Math.Max(1, hulls.Count),
                Squadron = hulls.Count > 1 ? hulls : null,
                WeaponBands = SquadronAssembler.WeaponBandsOf(catalogue, hulls),
                FleetId = FleetIdFor(label, side),
                LastResolvedSegment = -1,
                BenchLabel = label,
                BenchFlags = flags,
            };
        }

        /// <summary>True when any fleet in <paramref name="states"/> belongs to
        /// <paramref name="label"/>.</summary>
        public static bool IsActive(IList<FactionState> states, string label)
        {
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                    if (string.Equals(fleets[fi].BenchLabel, label, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Removes every fleet of <paramref name="label"/> (every bench fleet when null) and
        /// returns them, so the host can close their grids.</summary>
        public static List<PendingFleetAction> Remove(IList<FactionState> states, string label)
        {
            var removed = new List<PendingFleetAction>();
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = fleets.Count - 1; fi >= 0; fi--)
                {
                    PendingFleetAction f = fleets[fi];
                    if (!f.IsBench) continue;
                    if (label != null && !string.Equals(f.BenchLabel, label, StringComparison.OrdinalIgnoreCase)) continue;
                    removed.Add(f);
                    fleets.RemoveAt(fi);
                }
            }
            return removed;
        }

        /// <summary>How long a defender stands once the attack it met has ended, before its duty
        /// window closes and its grid leaves with it. A Guard whose window already ends sooner keeps
        /// the sooner end.</summary>
        public const double DefenderHoldSeconds = 30;

        /// <summary>The longest a scenario outlives its resolution: an attacker still in the world
        /// this long after its dwell ended is retired with its grids wherever they are.</summary>
        public const double AftermathCapSeconds = 150;

        /// <summary>How far from the fight an attacker's leg away from it ends: past
        /// <see cref="CombatLeash.DisengageClearM"/>, so a repelled attacker has broken clear before
        /// it turns to cruise, and far enough to be seen leaving.</summary>
        public const double AftermathLeaveKm = 6.0;

        /// <summary>True for a bench attacker leaving its fight (<see cref="BeginAftermath"/>): it
        /// flies its leg away on its own grids and never materialises afresh.</summary>
        public static bool InAftermath(PendingFleetAction fleet)
        {
            return fleet.IsBench && fleet.BenchAftermathSinceUtc.HasValue;
        }

        /// <summary>
        /// Called by <see cref="PendingFleetResolver"/> at the end of a bench attacker's dwell, in
        /// place of resolving it: the attacker is put on a leg away from the fight -- the leg a real
        /// raid flies home -- from where it held to <see cref="AftermathLeaveKm"/> further out, on the
        /// line from its opponent (the scenario's defender, else the node) through itself, due
        /// <see cref="AftermathCapSeconds"/> from now. A repelled attacker keeps its
        /// <see cref="PendingFleetAction.RetreatReason"/>, so the host breaks it away first as it does
        /// a real repelled raid. <see cref="Conclude"/> retires the scenario once its grids have
        /// left. False, and the caller resolves the fleet as before, for a fleet that is not a bench
        /// attacker on its dwell, was intercepted, or lost its grid.
        /// </summary>
        public static bool BeginAftermath(ref PendingFleetAction fleet, IList<FactionState> states,
                                          SpatialGraph graph, DateTime now)
        {
            if (!fleet.IsBench || !IsAttackKind(fleet.Kind) || fleet.Intercepted || fleet.IsPhysicalLoss) return false;
            if (fleet.CurrentStopKind != FleetStopKind.Engage || fleet.BenchAftermathSinceUtc.HasValue) return false;

            Vec3 from = fleet.TargetPosKm;
            fleet.OriginPosKm = from;
            fleet.TargetPosKm = LeavePoint(from, OpponentPosition(states, graph, fleet));
            fleet.CurrentStopKind = FleetStopKind.Return;
            fleet.DepartedUtc = now;
            fleet.EstimatedArrivalUtc = now.AddSeconds(AftermathCapSeconds);
            fleet.LastResolvedSegment = -1;
            fleet.Resolved = false;
            fleet.Fight = null;
            fleet.FightDamageDealt = 0;
            fleet.FightDamageTaken = 0;
            fleet.FightZoneHits = 0;
            fleet.BenchAftermathSinceUtc = now;
            return true;
        }

        /// <summary>Where the attacker's opponent stands: the scenario's defender, else the node it
        /// attacked, else its own position.</summary>
        private static Vec3 OpponentPosition(IList<FactionState> states, SpatialGraph graph, PendingFleetAction attacker)
        {
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (!string.Equals(f.BenchLabel, attacker.BenchLabel, StringComparison.Ordinal) || IsAttackKind(f.Kind)) continue;
                    return f.TargetPosKm;
                }
            }
            SpatialNode node;
            if (graph != null && attacker.TargetNodeId != null && graph.Nodes.TryGetValue(attacker.TargetNodeId, out node))
                return node.Position;
            return attacker.TargetPosKm;
        }

        /// <summary><see cref="AftermathLeaveKm"/> beyond <paramref name="from"/>, directly away from
        /// <paramref name="opponent"/>; along +X when the two coincide.</summary>
        public static Vec3 LeavePoint(Vec3 from, Vec3 opponent)
        {
            double dx = from.X - opponent.X, dy = from.Y - opponent.Y, dz = from.Z - opponent.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6) { dx = 1; dy = 0; dz = 0; len = 1; }
            double k = AftermathLeaveKm / len;
            return new Vec3(from.X + dx * k, from.Y + dy * k, from.Z + dz * k);
        }

        /// <summary>
        /// Writes up every scenario whose attack ended this pass -- the attacking fleet was
        /// intercepted in the meeting duel, started its leg away from the fight
        /// (<see cref="BeginAftermath"/>), or resolved -- as one <c>BENCH_RESOLVED</c> line, and
        /// shortens each defender's stand to <see cref="DefenderHoldSeconds"/>. A scenario is then
        /// retired whole -- every fleet resolved, so the cleanup that follows removes it and closes
        /// its grids -- when none of its fleets holds a grid, when its attacker's leg away has run
        /// out, or <see cref="AftermathCapSeconds"/> after the attack ended, with
        /// <c>BENCH_CONCLUDED &lt;label&gt; reason=grids_left|leg_end|cap after=&lt;s&gt;s</c>.
        /// <para>Called by <see cref="PendingFleetResolver.ResolveArrivals"/> just before its
        /// cleanup; costs one field read per fleet when no bench fleet exists.</para>
        /// </summary>
        public static void Conclude(IList<FactionState> states, DateTime now, FactionTuning tuning, IList<string> log)
        {
            List<string> ended = null;
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (!f.IsBench || !IsAttackKind(f.Kind)) continue;
                    if (!(f.Intercepted || f.Resolved || f.BenchAftermathSinceUtc.HasValue)) continue;
                    if (ended == null) ended = new List<string>();
                    if (!ended.Contains(f.BenchLabel)) ended.Add(f.BenchLabel);
                }
            }
            if (ended == null) return;

            for (int li = 0; li < ended.Count; li++)
            {
                string label = ended[li];
                WriteUp(states, label, now, tuning, log);
                string reason;
                DateTime since;
                if (!ShouldRetire(states, label, now, out reason, out since)) continue;
                Retire(states, label, now, tuning);
                log.Add("BENCH_CONCLUDED " + label + " reason=" + reason
                        + " after=" + Math.Max(0, (now - since).TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + "s");
            }
        }

        /// <summary>The <c>BENCH_RESOLVED</c> line, once per attacker, and the defenders' short stand.</summary>
        private static void WriteUp(IList<FactionState> states, string label, DateTime now, FactionTuning tuning, IList<string> log)
        {
            bool pending = false;
            for (int si = 0; si < states.Count && !pending; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (string.Equals(f.BenchLabel, label, StringComparison.Ordinal) && IsAttackKind(f.Kind) && !f.BenchWrittenUp
                        && (f.Intercepted || f.Resolved || f.BenchAftermathSinceUtc.HasValue))
                    { pending = true; break; }
                }
            }
            if (!pending) return;

            var line = new StringBuilder("BENCH_RESOLVED ").Append(label);
            var others = new StringBuilder();
            string where = null;
            DateTime holdEnd = now.AddSeconds(DefenderHoldSeconds);
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (!string.Equals(f.BenchLabel, label, StringComparison.Ordinal)) continue;
                    if (IsAttackKind(f.Kind) && (f.Intercepted || f.Resolved || f.BenchAftermathSinceUtc.HasValue))
                    {
                        // A broken-off attack is intercepted too, but left the fight at its own ratio.
                        bool brokeOff = f.Intercepted && f.StoppedHow == "broke_off";
                        double ratio = brokeOff ? f.StoppedRatio
                                     : f.Intercepted ? 0.0 : (f.ForcedRatio.HasValue ? f.ForcedRatio.Value : 1.0);
                        line.Append(' ').Append(f.LogId).Append(' ').Append(f.ActorTag)
                            .Append(" outcome=").Append(brokeOff ? "broke_off"
                                                      : f.Intercepted || f.RetreatReason == "repelled" ? "repelled"
                                                      : f.IsPhysicalLoss ? "lost" : f.RetreatReason != null ? "retreated" : "landed")
                            .Append(" ratio=").Append(ratio.ToString("0.00", CultureInfo.InvariantCulture))
                            .Append(" cause=").Append(f.HullsMeasured ? "physical"
                                                    : brokeOff ? f.StoppedSource ?? "duel"
                                                    : f.Intercepted ? "intercepted" : f.ForcedRatioCause ?? "unopposed")
                            .Append(" opposed_dps=").Append(f.OpposedDps.ToString(CultureInfo.InvariantCulture));
                        if (where == null) where = f.TargetNodeId;
                        if (f.TargetTag != null && others.Length == 0 && !HasBenchDefender(states, label))
                            others.Append(' ').Append(f.TargetTag);
                        f.BenchWrittenUp = true;
                        fleets[fi] = f;
                        continue;
                    }
                    others.Append(' ').Append(f.LogId).Append(' ').Append(f.ActorTag)
                          .Append(" outcome=").Append(f.Intercepted ? "destroyed" : f.IsPhysicalLoss ? "lost" : "stood");
                    if (!f.Intercepted && f.Kind == ActionKind.Guard && f.Resolved)
                    {
                        if (f.EstimatedArrivalUtc + tuning.GuardDuration > holdEnd)
                            f.EstimatedArrivalUtc = holdEnd - tuning.GuardDuration;
                        fleets[fi] = f;
                    }
                }
            }
            if (others.Length > 0) line.Append(" vs").Append(others);
            if (where != null) line.Append(" @").Append(where);
            log.Add(line.ToString());
        }

        /// <summary>Whether the scenario is done: its attacker's leg away ran out, it has passed
        /// <see cref="AftermathCapSeconds"/>, or no fleet of it holds a grid.
        /// <paramref name="since"/> is when its attack ended.</summary>
        private static bool ShouldRetire(IList<FactionState> states, string label, DateTime now,
                                         out string reason, out DateTime since)
        {
            reason = null;
            since = now;
            bool grids = false, legEnded = false, attackerLeft = false;
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (!string.Equals(f.BenchLabel, label, StringComparison.Ordinal)) continue;
                    if (HoldsGrid(f)) grids = true;
                    if (!IsAttackKind(f.Kind)) continue;
                    if (f.BenchAftermathSinceUtc.HasValue)
                    {
                        since = f.BenchAftermathSinceUtc.Value;
                        attackerLeft = true;
                        if (f.Resolved) legEnded = true;
                    }
                }
            }
            // An attacker intercepted in the duel is removed by this pass's cleanup; its defenders
            // stand out their short hold and go with their windows.
            if (!attackerLeft) return false;
            if (legEnded) reason = "leg_end";
            else if (!grids) reason = "grids_left";
            else if ((now - since).TotalSeconds >= AftermathCapSeconds) reason = "cap";
            return reason != null;
        }

        private static bool HoldsGrid(PendingFleetAction f)
        {
            if (f.MaterialisedEntityId > 0 || f.MaterialisedEntityId == PendingFleetAction.SpawningId) return true;
            if (f.EscortEntityIds != null)
                for (int i = 0; i < f.EscortEntityIds.Count; i++)
                    if (f.EscortEntityIds[i] > 0) return true;
            return false;
        }

        /// <summary>Resolves every fleet of the scenario, so the cleanup removes it and closes its grids.</summary>
        private static void Retire(IList<FactionState> states, string label, DateTime now, FactionTuning tuning)
        {
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                {
                    PendingFleetAction f = fleets[fi];
                    if (!string.Equals(f.BenchLabel, label, StringComparison.Ordinal) || f.Intercepted) continue;
                    if (f.Kind == ActionKind.Guard) f.EstimatedArrivalUtc = now - tuning.GuardDuration;
                    f.Resolved = true;
                    fleets[fi] = f;
                }
            }
        }

        private static bool HasBenchDefender(IList<FactionState> states, string label)
        {
            for (int si = 0; si < states.Count; si++)
            {
                if (states[si] == null) continue;
                List<PendingFleetAction> fleets = states[si].PendingFleets;
                for (int fi = 0; fi < fleets.Count; fi++)
                    if (string.Equals(fleets[fi].BenchLabel, label, StringComparison.Ordinal) && !IsAttackKind(fleets[fi].Kind))
                        return true;
            }
            return false;
        }

        // ---- the abstract duel, on demand ---------------------------------------------------------

        /// <summary><see cref="Duel(List{WeaponBand}, List{WeaponBand}, long, FactionTuning, int, int, bool, DuelStance, DuelStance)"/>
        /// for two squadrons named by prefab, each side's bands built the way a dispatch builds them
        /// (<see cref="SquadronAssembler.WeaponBandsOf"/>).</summary>
        public static BenchDuelReport Duel(IPrefabCatalogue catalogue, IList<string> attackerPrefabs,
            IList<string> defenderPrefabs, long defenderFlatPower, FactionTuning tuning, int runs, int seed)
        {
            return Duel(SquadronAssembler.WeaponBandsOf(catalogue, new List<string>(attackerPrefabs)),
                        SquadronAssembler.WeaponBandsOf(catalogue, new List<string>(defenderPrefabs)),
                        defenderFlatPower, tuning, runs, seed);
        }

        /// <summary>A side's stance for a contested bench duel: <paramref name="arm"/> and
        /// <paramref name="breakOff"/> as given, or the default personality's when null, at the
        /// combat acceleration of <paramref name="prefabs"/> in open space
        /// (<see cref="CombatMobility.OfHulls"/>) -- what a fight away from any planet would read.</summary>
        public static DuelStance Stance(IPrefabCatalogue catalogue, IList<string> prefabs,
            TacticArm? arm, BreakOffLevel? breakOff)
        {
            DuelStance stance = TacticChoice.DefaultFor(null);
            if (arm.HasValue) stance.Arm = arm.Value;
            if (breakOff.HasValue) stance.BreakOff = breakOff.Value;
            stance.Mobility = CombatMobility.OfHulls(catalogue, prefabs, 0, false);
            return stance;
        }

        /// <summary><see cref="Duel(List{WeaponBand}, List{WeaponBand}, long, FactionTuning, int, int, bool, DuelStance, DuelStance)"/>
        /// for two squadrons named by prefab, contested with the given stances
        /// (<see cref="Stance"/>).</summary>
        public static BenchDuelReport Duel(IPrefabCatalogue catalogue, IList<string> attackerPrefabs,
            IList<string> defenderPrefabs, long defenderFlatPower, FactionTuning tuning, int runs, int seed,
            DuelStance attacker, DuelStance defender)
        {
            return Duel(SquadronAssembler.WeaponBandsOf(catalogue, new List<string>(attackerPrefabs)),
                        SquadronAssembler.WeaponBandsOf(catalogue, new List<string>(defenderPrefabs)),
                        defenderFlatPower, tuning, runs, seed, true, attacker, defender);
        }

        /// <summary>
        /// The meeting duel run <paramref name="runs"/> times on one <see cref="SeededRandom"/>
        /// seeded with <paramref name="seed"/>, so the same inputs always give the same
        /// distribution. Each run is one call of the duel every meeting fights
        /// (<see cref="ShipDuel.Fight"/>), unarmed on both sides as an unarmed fleet fights; nothing
        /// here restates its arithmetic. <paramref name="contested"/> and the two stances are
        /// passed straight through, uncontested by default. Needs no world and no player.
        /// </summary>
        public static BenchDuelReport Duel(List<WeaponBand> attackerBands, List<WeaponBand> defenderBands,
            long defenderFlatPower, FactionTuning tuning, int runs, int seed,
            bool contested = false, DuelStance attacker = default(DuelStance), DuelStance defender = default(DuelStance))
        {
            var report = new BenchDuelReport
            {
                Runs = Math.Max(1, runs),
                Seed = seed,
                AttackerDps = SumDps(attackerBands),
                DefenderDps = SumDps(defenderBands),
                DefenderFlat = defenderFlatPower,
                Contested = contested,
                AttackerStance = attacker,
                DefenderStance = defender,
            };
            var rng = new SeededRandom(seed);
            for (int i = 0; i < report.Runs; i++)
            {
                ShipDuel.Result r = ShipDuel.Fight(attackerBands, defenderBands, defenderFlatPower, tuning, rng,
                    null, null, contested, attacker, defender);
                report.AttackerRatios.Add(r.AttackerRatio);
                report.DefenderRatios.Add(r.DefenderRatio);
                if (r.DefenderDestroyed && !r.AttackerDestroyed) report.AttackerWins++;
                else if (r.AttackerDestroyed && !r.DefenderDestroyed) report.DefenderWins++;
                else if (r.AttackerDestroyed) report.Mutual++;
                else report.Standoff++;
                if (r.AttackerBrokeOff) report.AttackerBrokeOff++;
                if (r.DefenderBrokeOff) report.DefenderBrokeOff++;
                if (ShipDuel.StandingDefenceRepels(r, report.DefenderDps + defenderFlatPower)) report.Repelled++;
                if (i == 0) report.FirstTurnSummary = r.TurnSummary;
            }
            report.AttackerRatios.Sort();
            report.DefenderRatios.Sort();
            return report;
        }

        private static double SumDps(List<WeaponBand> bands)
        {
            if (bands == null) return 0;
            double sum = 0;
            for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }
    }

    /// <summary>What <see cref="BenchScenario.Duel(List{WeaponBand}, List{WeaponBand}, long, FactionTuning, int, int, bool, DuelStance, DuelStance)"/>
    /// measured: each side's surviving-capacity ratio over every run (sorted ascending), how the runs
    /// ended, and the turn summary of the first run.</summary>
    public sealed class BenchDuelReport
    {
        public int Runs;
        public int Seed;
        public double AttackerDps;
        public double DefenderDps;
        public long DefenderFlat;
        /// <summary>Whether range was contested, and with which stances.</summary>
        public bool Contested;
        public DuelStance AttackerStance;
        public DuelStance DefenderStance;
        public readonly List<double> AttackerRatios = new List<double>();
        public readonly List<double> DefenderRatios = new List<double>();
        /// <summary>Defender destroyed, attacker not.</summary>
        public int AttackerWins;
        /// <summary>Attacker destroyed, defender not.</summary>
        public int DefenderWins;
        /// <summary>Both destroyed.</summary>
        public int Mutual;
        /// <summary>Neither destroyed.</summary>
        public int Standoff;
        /// <summary>Runs in which that side broke off (contested range only).</summary>
        public int AttackerBrokeOff;
        public int DefenderBrokeOff;
        /// <summary>Runs a dwell meeting would read as repelled by a standing defence
        /// (<see cref="ShipDuel.StandingDefenceRepels"/>).</summary>
        public int Repelled;
        public string FirstTurnSummary;

        /// <summary>Nearest-rank percentile of an ascending list; 0 when it is empty.</summary>
        public static double Percentile(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            return sorted[idx];
        }

        public string Format()
        {
            var sb = new StringBuilder("bench duel: runs=").Append(Runs).Append(" seed=").Append(Seed)
                .Append(" atk_dps=").Append(AttackerDps.ToString("0", CultureInfo.InvariantCulture))
                .Append(" def_dps=").Append(DefenderDps.ToString("0", CultureInfo.InvariantCulture));
            if (DefenderFlat > 0) sb.Append(" def_flat=").Append(DefenderFlat);
            if (Contested)
                sb.Append(" | contested atk=").Append(StanceText(AttackerStance))
                  .Append(" def=").Append(StanceText(DefenderStance));
            AppendRatios(sb, " | atk ratio", AttackerRatios);
            AppendRatios(sb, " | def ratio", DefenderRatios);
            sb.Append(" | atk_wins=").Append(AttackerWins).Append(" def_wins=").Append(DefenderWins)
              .Append(" mutual=").Append(Mutual).Append(" standoff=").Append(Standoff);
            if (AttackerBrokeOff + DefenderBrokeOff > 0)
                sb.Append(" atk_broke_off=").Append(AttackerBrokeOff).Append(" def_broke_off=").Append(DefenderBrokeOff);
            if (Repelled > 0) sb.Append(" repelled=").Append(Repelled);
            sb.Append(" | run 1: ").Append(FirstTurnSummary ?? "");
            return sb.ToString();
        }

        private static string StanceText(DuelStance stance)
        {
            return stance.Arm + "/" + stance.BreakOff + " accel="
                   + (stance.Mobility < 0 ? "unknown" : stance.Mobility.ToString("0.0", CultureInfo.InvariantCulture) + "m/s2");
        }

        private static void AppendRatios(StringBuilder sb, string head, List<double> sorted)
        {
            sb.Append(head)
              .Append(" min=").Append(Percentile(sorted, 0).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" p50=").Append(Percentile(sorted, 0.5).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" max=").Append(Percentile(sorted, 1).ToString("0.00", CultureInfo.InvariantCulture));
        }
    }
}
