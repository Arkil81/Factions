using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The runtime's half of <c>factions bench fight|raid|leave|return|status|clear</c>: puts staged
    /// <see cref="BenchScenario"/> fleets into the live faction states, keeps the two sides of a
    /// scenario hostile in SE's faction table while it runs, and takes a scenario's grids out of the
    /// world and back on command. Positioning and argument parsing live in <see cref="BenchSpawner"/>.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>SE faction pairs held at Enemies for each live scenario, by label.</summary>
        private readonly Dictionary<string, List<KeyValuePair<string, string>>> _benchHostile =
            new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fleet ids a <c>bench leave</c> took out of the world, and the label each belongs
        /// to. Each carries a long <see cref="_rematerialiseAfter"/> hold so the observation loop
        /// does not spawn it straight back while the player stands beside it.</summary>
        private readonly Dictionary<string, string> _benchAway = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly TimeSpan BenchAwayHold = TimeSpan.FromHours(12);

        internal IPrefabCatalogue BenchCatalogue { get { return _prefabCatalogue; } }
        internal FactionTuning BenchTuning { get { return _tuning; } }
        internal WorldModel BenchWorld { get { return _world; } }

        internal FactionState BenchState(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            foreach (FactionState st in _states)
                if (st != null && string.Equals(st.Tag, tag, StringComparison.OrdinalIgnoreCase)) return st;
            return null;
        }

        internal bool BenchLabelInUse(string label)
        {
            return BenchScenario.IsActive(_states, label);
        }

        private bool BenchForcesHostile(string a, string b)
        {
            if (_benchHostile.Count == 0) return false;
            foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> kv in _benchHostile)
                foreach (KeyValuePair<string, string> pair in kv.Value)
                    if ((string.Equals(pair.Key, a, StringComparison.OrdinalIgnoreCase) && string.Equals(pair.Value, b, StringComparison.OrdinalIgnoreCase))
                        || (string.Equals(pair.Key, b, StringComparison.OrdinalIgnoreCase) && string.Equals(pair.Value, a, StringComparison.OrdinalIgnoreCase)))
                        return true;
            return false;
        }

        /// <summary>Adds <paramref name="fleets"/> to their actors' states, holds every pair in
        /// <paramref name="hostilePairs"/> at Enemies for as long as the scenario lives, and writes
        /// <paramref name="stagedLine"/> to the decision log. Returns the SE stance of each pair after
        /// the projection, for the command response.</summary>
        internal string StageBench(string label, List<PendingFleetAction> fleets,
                                   List<KeyValuePair<string, string>> hostilePairs, string stagedLine)
        {
            foreach (PendingFleetAction f in fleets)
            {
                FactionState st = BenchState(f.ActorTag);
                if (st != null) st.PendingFleets.Add(f);
                if (f.FleetId != null) _benchArms.Remove(f.FleetId);
            }
            _benchHostile[label] = hostilePairs;
            if (hostilePairs.Count > 0) ProjectRelationsToSe("bench");
            _log.Write(stagedLine);

            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in hostilePairs)
                sb.Append(sb.Length > 0 ? ", " : "").Append(pair.Key).Append('-').Append(pair.Value)
                  .Append('=').Append(SeStance(pair.Key, pair.Value));
            return sb.ToString();
        }

        /// <summary>SE's current stance between two factions, as the relation table answers it.</summary>
        private static string SeStance(string tagA, string tagB)
        {
            try
            {
                Sandbox.Game.World.MySession session = Sandbox.Game.World.MySession.Static;
                if (session == null || session.Factions == null) return "?";
                Sandbox.Game.World.MyFaction fa = session.Factions.TryGetFactionByTag(tagA);
                Sandbox.Game.World.MyFaction fb = session.Factions.TryGetFactionByTag(tagB);
                if (fa == null || fb == null) return "no-se-faction";
                Tuple<VRage.Game.MyRelationsBetweenFactions, int> rel =
                    session.Factions.GetRelationBetweenFactions(fa.FactionId, fb.FactionId);
                return rel == null ? "?" : rel.Item1 + "(" + rel.Item2.ToString(CultureInfo.InvariantCulture) + ")";
            }
            catch { return "?"; }
        }

        /// <summary>Removes the scenario (every scenario when <paramref name="label"/> is null) and
        /// closes its grids. A fleet whose prefab load is still in flight is dropped with it; the grid
        /// that load produces has nothing holding it and is closed by the reaper.</summary>
        internal string ClearBench(string label)
        {
            List<PendingFleetAction> removed = BenchScenario.Remove(_states, label);
            int grids = 0, spawning = 0;
            foreach (PendingFleetAction f in removed)
            {
                if (f.MaterialisedEntityId > 0) grids += CloseBenchGrids(f);
                else if (f.MaterialisedEntityId == PendingFleetAction.SpawningId)
                {
                    spawning++;
                    _spawnsInFlight.Remove(FleetKey(f));
                }
                ReleaseBenchAway(f.FleetId);
                if (!string.IsNullOrEmpty(f.FleetId)) _pendingRetargetEid.Remove(f.FleetId);
            }
            grids += CloseBenchDepartures(label);
            bool reproject = false;
            if (label == null) { reproject = _benchHostile.Count > 0; _benchHostile.Clear(); }
            else reproject = _benchHostile.Remove(label);
            if (reproject) ProjectRelationsToSe("bench");
            if (removed.Count > 0 || grids > 0)
                _log.Write("BENCH_CLEARED " + (label ?? "*") + " fleets=" + removed.Count + " grids=" + grids
                           + (spawning > 0 ? " spawning=" + spawning : ""));
            return "fleets=" + removed.Count + " grids=" + grids + (spawning > 0 ? " (" + spawning + " still loading; the reaper closes them)" : "");
        }

        /// <summary>Closes a fleet's lead and escorts and forgets the host bookkeeping on them.
        /// Returns how many grids were closed.</summary>
        private int CloseBenchGrids(PendingFleetAction f)
        {
            if (_spawner == null || f.MaterialisedEntityId <= 0) return 0;
            long eid = f.MaterialisedEntityId;
            int closed = _spawner.Despawn(eid);
            int escortGrids;
            DespawnEscorts(f.EscortEntityIds, out escortGrids);
            _stalledSince.Remove(eid);
            _safezoneArrivedSince.Remove(eid);
            _engagementHeldSince.Remove(eid);
            _settleHeldReported.Remove(eid);
            return closed + escortGrids;
        }

        private void ReleaseBenchAway(string fleetId)
        {
            if (string.IsNullOrEmpty(fleetId) || !_benchAway.Remove(fleetId)) return;
            _rematerialiseAfter.Release(fleetId);
        }

        /// <summary><c>bench leave</c>: takes the scenario's grids out of the world with the player
        /// still beside them and keeps them out until <see cref="BenchReturn"/>. The fleets carry on
        /// abstractly -- the dwell clock keeps running and resolves as it would unobserved.</summary>
        internal string BenchLeave(string label)
        {
            DateTime now = DateTime.UtcNow;
            int fleets = 0, grids = 0, loading = 0;
            var line = new StringBuilder("BENCH_LEAVE ").Append(label);
            foreach (FactionState st in _states)
            {
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (!string.Equals(f.BenchLabel, label, StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.MaterialisedEntityId == PendingFleetAction.SpawningId) { loading++; continue; }
                    fleets++;
                    long eid = f.MaterialisedEntityId;
                    // What the grids carry leaves with them, as when a player flies out of range.
                    if (eid > 0) f = SettleHulls(f, eid, "bench_leave");
                    int closed = CloseBenchGrids(f);
                    grids += closed;
                    f.MaterialisedEntityId = 0;
                    f.EscortEntityIds = null;
                    st.PendingFleets[fi] = f;
                    _rematerialiseAfter.Arm(f.FleetId, now, BenchAwayHold);
                    _benchAway[f.FleetId] = label;
                    line.Append(' ').Append(f.LogId).Append(" eid=").Append(eid).Append(" closed=").Append(closed);
                }
            }
            if (fleets == 0 && loading == 0) return null;
            if (loading > 0) line.Append(" still_loading=").Append(loading);
            _log.Write(line.ToString());
            // The grids have left: the fight goes back to the duel now, not at the next arrivals pass.
            var handoff = new List<string>();
            PendingFleetResolver.ObserveFights(_states, _world, _tuning,
                new SeededRandom(unchecked((int)now.Ticks)), handoff);
            foreach (string h in handoff) _log.Write("t" + _simTick + " " + h);
            return "took " + fleets + " fleet(s), " + grids + " grid(s) out of the world"
                   + (loading > 0 ? "; " + loading + " still loading -- repeat once they are SPAWNED" : "")
                   + ". They stay out until 'factions bench return " + label + "'.";
        }

        /// <summary><c>bench damage</c>: sets every hull of the scenario to
        /// <paramref name="integrity"/> (0..1) and, for a fleet in the world, damages its grids' blocks
        /// to match and restarts their integrity watch -- what the spawn path does for a hull that
        /// materialises damaged. The fleets' bands are rescaled as for any recorded damage.</summary>
        internal string BenchDamage(string label, double integrity)
        {
            var vanilla = _spawner as VanillaFleetSpawner;
            int fleets = 0, grids = 0, blocks = 0;
            var line = new StringBuilder("BENCH_DAMAGE ").Append(label)
                .Append(" integrity=").Append(integrity.ToString("0.00", CultureInfo.InvariantCulture));
            foreach (FactionState st in _states)
            {
                for (int fi = 0; fi < st.PendingFleets.Count; fi++)
                {
                    PendingFleetAction f = st.PendingFleets[fi];
                    if (!string.Equals(f.BenchLabel, label, StringComparison.OrdinalIgnoreCase) || f.Intercepted) continue;
                    fleets++;
                    for (int slot = 0; slot < f.HullSlotCount; slot++) f.SetIntegrity(slot, integrity);
                    HullDamage.RescaleBands(ref f, _world.PrefabCatalogue);
                    st.PendingFleets[fi] = f;
                    if (vanilla != null && f.MaterialisedEntityId > 0)
                    {
                        CollectHullEids(f, f.MaterialisedEntityId);
                        foreach (long eid in _hullEids)
                        {
                            int damaged = vanilla.WatchIntegrity(eid, integrity);
                            if (damaged < 0) continue;
                            grids++;
                            blocks += damaged;
                        }
                    }
                    line.Append(' ').Append(f.LogId).Append(" hulls=").Append(HullDamage.Describe(f));
                }
            }
            if (fleets == 0) return null;
            line.Append(" grids=").Append(grids).Append(" blocks_damaged=").Append(blocks);
            _log.Write(line.ToString());
            return fleets + " fleet(s) at integrity " + integrity.ToString("0.00", CultureInfo.InvariantCulture)
                   + "; " + grids + " grid(s) in the world, " + blocks + " block(s) damaged.";
        }

        /// <summary><c>bench return</c>: lifts the <see cref="BenchLeave"/> hold, so the next frame
        /// with a player in range materialises the scenario again through the ordinary spawn path.</summary>
        internal string BenchReturn(string label)
        {
            var ids = new List<string>();
            foreach (KeyValuePair<string, string> kv in _benchAway)
                if (string.Equals(kv.Value, label, StringComparison.OrdinalIgnoreCase)) ids.Add(kv.Key);
            foreach (string id in ids) ReleaseBenchAway(id);
            if (ids.Count == 0) return null;
            _log.Write("BENCH_RETURN " + label + " fleets=" + ids.Count);
            return "released " + ids.Count + " fleet(s); they materialise on the next frame a player is within "
                   + _tuning.ObservationRadiusKm.ToString("0.#", CultureInfo.InvariantCulture) + " km.";
        }

        /// <summary><c>bench status</c>: every fleet of the scenario with its leg, time left on its
        /// hold, lead and escort grids and whether the flight controller holds each, and the distance
        /// between the two sides' leads.</summary>
        internal string BenchStatus(string label)
        {
            DateTime now = DateTime.UtcNow;
            var sb = new StringBuilder();
            Vec3? atkPos = null, defPos = null;
            int n = 0;
            foreach (FactionState st in _states)
            {
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (!string.Equals(f.BenchLabel, label, StringComparison.OrdinalIgnoreCase)) continue;
                    n++;
                    double left = (f.HoldEndsUtc(_tuning) - now).TotalSeconds;
                    sb.Append("\n  ").Append(f.LogId).Append(' ').Append(f.ActorTag).Append(" kind=").Append(f.Kind)
                      .Append(" stop=").Append(f.CurrentStopKind).Append(" @").Append(f.TargetNodeId ?? "?")
                      .Append(" hold_left=").Append(Math.Max(0, left).ToString("0", CultureInfo.InvariantCulture)).Append('s')
                      .Append(" hulls=").Append(f.HullCount)
                      .Append(" dps=").Append(SumBandDps(f.WeaponBands).ToString("0", CultureInfo.InvariantCulture));
                    if (f.Fight != null)
                        sb.Append(" fight=").Append(f.Fight.Physical ? "grids" : "duel")
                          .Append(" turns_fought=").Append(f.Fight.TurnsFought);
                    if (f.Intercepted) sb.Append(" [intercepted]");
                    if (!string.IsNullOrEmpty(f.FleetId) && _benchAway.ContainsKey(f.FleetId)) sb.Append(" [away]");
                    long eid = f.MaterialisedEntityId;
                    if (eid > 0)
                    {
                        sb.Append(" lead=").Append(eid).Append(GridState(eid));
                        Vec3? pos = _spawner != null ? _spawner.GetGridPositionKm(eid) : null;
                        if (pos.HasValue)
                        {
                            bool attacker = f.Kind == ActionKind.Raid || f.Kind == ActionKind.Retaliate || f.Kind == ActionKind.Conquer;
                            if (attacker && !atkPos.HasValue) atkPos = pos;
                            if (!attacker && !defPos.HasValue) defPos = pos;
                        }
                        if (f.EscortEntityIds != null)
                            foreach (long e in f.EscortEntityIds)
                            {
                                sb.Append(" escort=").Append(e).Append(GridState(e));
                                // How far each escort sits from its lead: a slot spacing or more in
                                // formation and in a fight, tens of metres when hulls stack.
                                Vec3? epos = pos.HasValue && _spawner != null ? _spawner.GetGridPositionKm(e) : null;
                                if (epos.HasValue)
                                    sb.Append(" d=").Append((epos.Value.DistanceTo(pos.Value) * 1000.0).ToString("0", CultureInfo.InvariantCulture)).Append('m');
                            }
                    }
                    else if (eid == PendingFleetAction.SpawningId) sb.Append(" grid=loading");
                    else sb.Append(" grid=none");
                }
            }
            if (n == 0) return null;
            string sep = atkPos.HasValue && defPos.HasValue
                ? (atkPos.Value.DistanceTo(defPos.Value) * 1000.0).ToString("0", CultureInfo.InvariantCulture) + " m"
                : "n/a (both sides need a lead grid)";
            return "bench " + label + ": " + n + " fleet(s), separation " + sep + sb;
        }

        /// <summary><c>bench tactics</c>: <paramref name="tag"/>'s <see cref="TacticMemory"/> as
        /// <see cref="TacticMemory.Describe"/> reads it, against <paramref name="opponent"/> or every
        /// opponent; null when no such faction.</summary>
        internal string BenchTactics(string tag, string opponent)
        {
            if (_states == null || string.IsNullOrEmpty(tag)) return null;
            foreach (FactionState st in _states)
                if (string.Equals(st.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    return st.TacticMemory.Describe(st.Tag, st.PersonalityId, opponent,
                                                    _world != null ? _world.UtcNow : DateTime.UtcNow);
            return null;
        }

        /// <summary>Every live scenario label, with how many fleets it has.</summary>
        internal string BenchLabels()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (FactionState st in _states)
                foreach (PendingFleetAction f in st.PendingFleets)
                {
                    if (!f.IsBench) continue;
                    int c;
                    counts.TryGetValue(f.BenchLabel, out c);
                    counts[f.BenchLabel] = c + 1;
                }
            if (counts.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in counts)
                sb.Append(sb.Length > 0 ? " " : "").Append(kv.Key).Append('=').Append(kv.Value).Append(" fleet(s);");
            return sb.ToString();
        }

        private string GridState(long eid)
        {
            if (_spawner == null) return "";
            if (!_spawner.GridExists(eid)) return "(gone)";
            double speedMs = _spawner.GetGridSpeedKmPerMin(eid) * 1000.0 / 60.0;
            return "(ctl=" + (_spawner.IsManoeuvring(eid) ? "yes" : "no")
                   + " v=" + speedMs.ToString("0", CultureInfo.InvariantCulture) + "m/s)";
        }

        private static double SumBandDps(List<WeaponBand> bands)
        {
            if (bands == null) return 0;
            double s = 0;
            foreach (WeaponBand b in bands) s += b.Dps;
            return s;
        }

        /// <summary>Called about once a second while any scenario is live: when a scenario has
        /// ended (resolved, or removed with its fleets), its hostility pairs are dropped and SE's
        /// table is projected back from the model, and its leave holds are released.</summary>
        internal void BenchSweep()
        {
            SweepBenchArms();
            if (_benchHostile.Count == 0 && _benchAway.Count == 0) return;
            List<string> ended = null;
            foreach (string label in _benchHostile.Keys)
                if (!BenchScenario.IsActive(_states, label)) (ended ?? (ended = new List<string>())).Add(label);
            if (ended != null)
            {
                foreach (string label in ended) _benchHostile.Remove(label);
                ProjectRelationsToSe("bench");
            }
            if (_benchAway.Count == 0) return;
            var gone = new List<string>();
            foreach (KeyValuePair<string, string> kv in _benchAway)
                if (!BenchScenario.IsActive(_states, kv.Value)) gone.Add(kv.Key);
            foreach (string id in gone) ReleaseBenchAway(id);
        }
    }
}
