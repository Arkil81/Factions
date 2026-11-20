using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Torch.Managers.ChatManager;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>What one debris sweep did, or would do. The dry-run form is the whole diagnostic
    /// channel for this feature: a sweep that reclaims nothing is expected most hours, so the
    /// refusal counts are what distinguish "nothing was lying around" from "everything was
    /// refused".</summary>
    internal sealed class DebrisAbsorbReport
    {
        public bool Applied;
        public int  GridsSeen;          // cube grids the sweep looked at
        public int  Absorbed;           // grids closed, or that would have been
        public int  Blocks;
        public long Units;
        public int  NodesTouched;
        public double Milliseconds;

        // Why candidates were refused, so a zero result is readable.
        public int SkippedNotTrash;     // SE's own criteria save it for a reason other than a nearby player
        public int SkippedPlayerOwned;  // some part of it belongs to a non-NPC identity
        public int SkippedTracked;      // a hull the fleet engine still means to fly
        public int SkippedNoNodeInReach;
        public int SkippedByCap;    // eligible, and left for the next sweep because the cap was full

        public readonly List<string> Details = new List<string>();

        /// <summary>One line per wreck, populated on a dry run only. The per-node totals in
        /// <see cref="Details"/> say how much; a sweep that is about to close grids on a live world
        /// has to be able to say <b>which</b>, by name and entity id, before anyone commits to it.
        /// An applied pass writes the same facts to the log as it goes, so it does not repeat them
        /// here.</summary>
        public readonly List<string> Wrecks = new List<string>();
    }

    /// <summary>
    /// Reclaims orphaned wreckage lying around founded nodes, crediting its parts to the node
    /// instead of letting it accumulate as PCU and simulation load.
    ///
    /// <para><b>What counts as debris is SE's answer, not ours.</b>
    /// <c>MySessionComponentTrash.GetTrashState</c> returns the criterion that *saved* a grid, so
    /// a grid saved by nothing at all — or saved only by <see cref="MyTrashRemovalFlags
    /// .DistanceFromPlayer"/> — is one SE would collect. Everything else it names (a static grid,
    /// one with power, a cockpit, a production block, a medbay, twenty or more blocks, or a peer in
    /// its physical group carrying any of those) is left alone. A turret bolted to a hull is
    /// therefore untouchable: the group check hands back the parent's protection.</para>
    ///
    /// <para><b>Why the proximity criterion is the one we override.</b> It is the reason this
    /// wreckage survives at all. <c>IsCloseToPlayerOrCamera</c> reports "close" — protected —
    /// whenever no online player is controlling an entity, so SE's collector is inert on an empty
    /// server, and within <c>PlayerDistanceThreshold</c> of anyone who is on. Around an inhabited
    /// station the union of those two is close to permanent. The sweep's long interval is what
    /// replaces that protection: an hour is a player's window to grind the same wreck first.</para>
    ///
    /// <para><b>Two guards SE does not have.</b> SE's criteria ignore ownership entirely, so a small
    /// unpowered grid someone placed on purpose is trash to it; every grid in the group must belong
    /// to NPC identities or nobody. And a hull this engine still means to fly is never judged on
    /// its physics, however stalled it looks.</para>
    /// </summary>
    internal sealed class DebrisAbsorbManager
    {
        private const double MessageRadiusKm = 3.0; // "plausibly close enough to have seen the wreck"
        private const string WhisperAuthor   = "Factions";
        private const int    MaxDetailLines  = 20;  // a bridge/chat result is one string
        private const int    MaxWreckLines   = 60;  // ditto, but a census is worth more of it

        private readonly Func<SpatialGraph> _graph;
        private readonly FactionTuning _tuning;
        private readonly DecisionLog   _log;
        private readonly Func<long, bool> _isTrackedGrid;
        private readonly Action<string>   _recordActivity;
        private readonly Func<Vec3, double, IEnumerable<ulong>> _playersWithin;

        private ChatManagerServer _chat;

        // Reused across passes; the sweep runs hourly, but it runs inside one Torch frame.
        private readonly List<MyCubeGrid> _candidates = new List<MyCubeGrid>();
        private readonly List<MyCubeGrid> _group      = new List<MyCubeGrid>();
        private readonly HashSet<long>    _handled    = new HashSet<long>();
        private readonly Dictionary<string, NodeTally> _tallies =
            new Dictionary<string, NodeTally>(StringComparer.Ordinal);

        private DateTime _nextDueUtc = DateTime.MinValue;
        private double   _lastPassMs;

        public DebrisAbsorbManager(
            WorldModel world,
            FactionTuning tuning,
            DecisionLog log,
            Func<long, bool> isTrackedGrid,
            Action<string> recordActivity,
            Func<Vec3, double, IEnumerable<ulong>> playersWithin)
            : this(() => world != null ? world.Graph : null, tuning, log, isTrackedGrid, recordActivity, playersWithin)
        {
        }

        /// <param name="graph">The nodes a wreck may be credited to; on a Nexus agent, the
        /// authority's snapshot.</param>
        public DebrisAbsorbManager(
            Func<SpatialGraph> graph,
            FactionTuning tuning,
            DecisionLog log,
            Func<long, bool> isTrackedGrid,
            Action<string> recordActivity,
            Func<Vec3, double, IEnumerable<ulong>> playersWithin)
        {
            _graph          = graph;
            _tuning         = tuning;
            _log            = log;
            _isTrackedGrid  = isTrackedGrid;
            _recordActivity = recordActivity;
            _playersWithin  = playersWithin;
        }

        /// <summary>On a Nexus agent: the node's stock is the authority's, so an applied sweep closes
        /// the wrecks here and hands each node's census there (node id, grids, blocks, components,
        /// cargo) instead of crediting it. Null credits locally.</summary>
        public Action<string, int, int, Dictionary<string, long>, Dictionary<string, long>> RemoteCredit;

        public void AttachChat(ChatManagerServer chat) { _chat = chat; }
        public void DetachChat() { _chat = null; }

        /// <summary>Runs a real sweep once every
        /// <see cref="FactionTuning.DebrisAbsorbIntervalSeconds"/>. Scheduled against the wall clock
        /// rather than a tick count, because the caller is itself on a sub-interval: a tick counter
        /// here would silently mean "the interval times the caller's own period".
        ///
        /// <para>The first sweep is one full interval after this manager was built, so the boot storm
        /// is never the frame this runs in.</para></summary>
        public void Tick()
        {
            if (_tuning == null || !_tuning.DebrisAbsorbEnabled) return;

            double intervalSeconds = _tuning.DebrisAbsorbIntervalSeconds;
            if (intervalSeconds <= 0) return;

            DateTime now = DateTime.UtcNow;
            if (_nextDueUtc == DateTime.MinValue)
            {
                _nextDueUtc = now.AddSeconds(intervalSeconds);
                return;
            }
            if (now < _nextDueUtc) return;
            _nextDueUtc = now.AddSeconds(intervalSeconds);

            Sweep(true);
        }

        /// <summary>One sweep of the whole estate. With <paramref name="apply"/> false nothing is
        /// credited and nothing is closed, and the report says what would have happened.</summary>
        public DebrisAbsorbReport Sweep(bool apply)
        {
            var report = new DebrisAbsorbReport { Applied = apply };
            SpatialGraph graph = _graph != null ? _graph() : null;
            if (graph == null || MySession.Static == null) return report;

            MySessionComponentTrash trash = MySession.Static.GetComponent<MySessionComponentTrash>();
            if (trash == null)
            {
                // Without SE's own criteria there is no predicate; refusing is the only safe answer.
                FactionsPlugin.Log.Warn("DebrisAbsorb: MySessionComponentTrash is not present; sweep skipped.");
                return report;
            }

            long started = Stopwatch.GetTimestamp();
            _candidates.Clear();
            _handled.Clear();
            _tallies.Clear();

            CollectCandidates(report);

            int cap = _tuning.DebrisAbsorbMaxGridsPerPass;
            if (cap <= 0) cap = int.MaxValue;
            double radiusKm = _tuning.DebrisAbsorbRadiusKm;
            double fraction = _tuning.SalvageRecoveryFraction;

            foreach (MyCubeGrid grid in _candidates)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (_handled.Contains(grid.EntityId)) continue;

                // SE's verdict first: it is the cheapest of the three gates that can refuse, and the
                // only one whose answer this feature is defined in terms of.
                MyTrashRemovalFlags state;
                try { state = trash.GetTrashState(grid); }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(ex, "DebrisAbsorb: GetTrashState failed for grid " + grid.EntityId + ".");
                    continue;
                }
                if ((state & ~MyTrashRemovalFlags.DistanceFromPlayer) != MyTrashRemovalFlags.None)
                {
                    report.SkippedNotTrash++;
                    continue;
                }

                // The physical group is the unit throughout: it is what GetTrashState just judged,
                // and what MySessionComponentTrash.RemoveGrid will close.
                _group.Clear();
                if (MyCubeGridGroups.Static.Physical != null)
                    MyCubeGridGroups.Static.Physical.GetGroupNodes(grid, _group);
                if (_group.Count == 0) _group.Add(grid);

                foreach (MyCubeGrid member in _group)
                    if (member != null) _handled.Add(member.EntityId);

                if (GroupTouchesPlayerProperty(_group)) { report.SkippedPlayerOwned++; continue; }
                if (GroupIsTracked(_group))             { report.SkippedTracked++;     continue; }

                Vec3 posKm = PositionKm(grid);
                SpatialNode node = DebrisAbsorption.NearestAbsorbingNode(graph, posKm, radiusKm);
                if (node == null) { report.SkippedNoNodeInReach++; continue; }

                // The cap is tested here, past every refusal, so the count it produces is the number
                // of wrecks genuinely waiting for the next sweep rather than the number of grids the
                // loop stopped looking at.
                if (report.Absorbed >= cap) { report.SkippedByCap++; continue; }

                var components = new Dictionary<string, long>(StringComparer.Ordinal);
                var cargo      = new Dictionary<string, long>(StringComparer.Ordinal);
                int blocks;
                try { blocks = ServicesTerminalHelpers.CensusGrids(_group, components, cargo); }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(ex, "DebrisAbsorb: census failed for grid " + grid.EntityId
                                              + "; left in place rather than deleted uncounted.");
                    continue;
                }

                ServiceSettlement.SalvageCredit comps;
                ServiceSettlement.SalvageCredit holds;
                bool remote = apply && RemoteCredit != null;
                if (remote)
                {
                    var scratch = new Dictionary<string, long>(StringComparer.Ordinal);
                    comps = ServiceSettlement.CreditSalvage(scratch, components, fraction);
                    holds = ServiceSettlement.CreditSalvage(scratch, cargo, fraction);
                    if (!CloseGroup(grid)) continue;
                }
                else if (apply)
                {
                    comps = ServiceSettlement.CreditSalvage(node.Inventory, components, fraction);
                    holds = ServiceSettlement.CreditSalvage(node.Inventory, cargo, fraction);
                    if (!CloseGroup(grid)) continue;
                    if (comps.Units > 0 || holds.Units > 0) _recordActivity?.Invoke(node.Id);
                }
                else
                {
                    // Same arithmetic against a throwaway inventory, so the dry run reports the real
                    // numbers rather than the census before the recovery fraction is applied.
                    var scratch = new Dictionary<string, long>(StringComparer.Ordinal);
                    comps = ServiceSettlement.CreditSalvage(scratch, components, fraction);
                    holds = ServiceSettlement.CreditSalvage(scratch, cargo, fraction);
                }

                report.Absorbed++;
                report.Blocks += blocks;
                report.Units  += comps.Units + holds.Units;
                Tally(node, posKm, blocks, comps.Units + holds.Units, remote ? components : null, remote ? cargo : null);

                if (!apply && report.Wrecks.Count < MaxWreckLines)
                    report.Wrecks.Add(DescribeWreck(grid, _group, blocks,
                        comps.Units + holds.Units, node, posKm));
            }

            report.NodesTouched = _tallies.Count;
            Finish(report, apply, started);
            if (apply) WritePassLine(report);
            return report;
        }

        // ---- candidate collection ------------------------------------------------------------

        /// <summary>Every cube grid worth asking SE about, snapshotted into a list before anything is
        /// closed. Mirrors <c>MySessionComponentTrash.UpdateTrash</c>'s own cheap refusals; the whole
        /// entity set is walked once per sweep rather than once per frame, which is why this reaches
        /// for <c>MyEntities.GetEntities</c> at all.</summary>
        private void CollectCandidates(DebrisAbsorbReport report)
        {
            foreach (MyEntity entity in MyEntities.GetEntities())
            {
                var grid = entity as MyCubeGrid;
                if (grid == null) continue;
                report.GridsSeen++;

                if (grid.MarkedForClose || !grid.InScene || grid.MarkedAsTrash || grid.IsPreview) continue;
                if (grid.Projector != null) continue;

                // IsGenerated covers this plugin's own station grids (StationMaterializer sets it
                // for exactly this reason) alongside vanilla's. IsNpcSpawnedGrid is every grid the
                // simulation put in the world, and GridReaper owns those: it closes them on its own
                // cadence against whether a fleet still holds them, which is a question this sweep
                // cannot ask and SE's trash criteria answer wrongly (a 35-block turret head with a
                // gatling gun is "kept"). Salvage credit is for wreckage players could have ground
                // themselves; our own litter is not that.
                if (grid.IsGenerated || grid.IsNpcSpawnedGrid) continue;

                if (!MySession.IsInsideWorld(grid.PositionComp.GetPosition())) continue;

                _candidates.Add(grid);
            }
        }

        // ---- guards SE does not have ---------------------------------------------------------

        private static bool GroupTouchesPlayerProperty(List<MyCubeGrid> group)
        {
            MyPlayerCollection players = MySession.Static != null ? MySession.Static.Players : null;
            if (players == null) return true; // cannot tell whose it is -- refuse

            foreach (MyCubeGrid member in group)
            {
                if (member == null) continue;
                if (OwnersIncludePlayer(players, member.BigOwners)) return true;
                if (OwnersIncludePlayer(players, member.SmallOwners)) return true;
            }
            return false;
        }

        private static bool OwnersIncludePlayer(MyPlayerCollection players, List<long> owners)
        {
            if (owners == null) return false;
            for (int i = 0; i < owners.Count; i++)
            {
                long id = owners[i];
                if (id == 0) continue;              // unowned blocks belong to nobody
                if (!players.IdentityIsNpc(id)) return true;
            }
            return false;
        }

        private bool GroupIsTracked(List<MyCubeGrid> group)
        {
            if (_isTrackedGrid == null) return false;
            foreach (MyCubeGrid member in group)
                if (member != null && _isTrackedGrid(member.EntityId)) return true;
            return false;
        }

        // ---- acting ---------------------------------------------------------------------------

        /// <summary>Hands the group to SE's own remover, which dismounts cockpits, closes every grid
        /// in the physical group and drops an abandoned identity. Returns whether the grid actually
        /// went: a credit already happened, so a failure here has to be visible rather than silent.</summary>
        private static bool CloseGroup(MyCubeGrid grid)
        {
            try
            {
                MySessionComponentTrash.RemoveGrid(grid);
                return true;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "DebrisAbsorb: RemoveGrid failed for grid " + grid.EntityId
                                          + "; its parts were already credited to the node.");
                return false;
            }
        }

        // ---- reporting ------------------------------------------------------------------------

        private sealed class NodeTally
        {
            public string NodeId;
            public string FactionTag;
            public Vec3   PosKm;
            public int    Grids;
            public int    Blocks;
            public long   Units;
            /// <summary>The raw census, kept only when the credit is the authority's.</summary>
            public Dictionary<string, long> Components;
            public Dictionary<string, long> Cargo;
        }

        private static void AddAll(ref Dictionary<string, long> into, Dictionary<string, long> from)
        {
            if (from == null) return;
            if (into == null) into = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> kv in from)
            {
                long c; into.TryGetValue(kv.Key, out c);
                into[kv.Key] = c + kv.Value;
            }
        }

        private void Tally(SpatialNode node, Vec3 posKm, int blocks, long units,
                           Dictionary<string, long> components, Dictionary<string, long> cargo)
        {
            NodeTally tally;
            if (!_tallies.TryGetValue(node.Id, out tally))
            {
                tally = new NodeTally { NodeId = node.Id, FactionTag = node.OwnerFactionTag, PosKm = posKm };
                _tallies[node.Id] = tally;
            }
            tally.Grids++;
            tally.Blocks += blocks;
            tally.Units  += units;
            AddAll(ref tally.Components, components);
            AddAll(ref tally.Cargo, cargo);
        }

        private void Finish(DebrisAbsorbReport report, bool apply, long startedTimestamp)
        {
            foreach (KeyValuePair<string, NodeTally> entry in _tallies)
            {
                NodeTally tally = entry.Value;
                if (report.Details.Count < MaxDetailLines)
                    report.Details.Add(tally.NodeId + " (" + tally.FactionTag + ") "
                                     + tally.Grids + " wreck(s), " + tally.Blocks + " block(s), "
                                     + tally.Units + " unit(s)");

                if (!apply) continue;
                if (RemoteCredit != null)
                {
                    RemoteCredit(tally.NodeId, tally.Grids, tally.Blocks, tally.Components, tally.Cargo);
                    Write("DEBRIS_CLOSED node=" + tally.NodeId + " faction=" + tally.FactionTag
                          + " grids=" + tally.Grids + " blocks=" + tally.Blocks + " books=authority");
                }
                else
                    Write("DEBRIS_ABSORBED node=" + tally.NodeId + " faction=" + tally.FactionTag
                          + " grids=" + tally.Grids + " blocks=" + tally.Blocks
                          + " units=" + tally.Units
                          + " frac=" + _tuning.SalvageRecoveryFraction.ToString("F2", CultureInfo.InvariantCulture));
                Whisper(tally);
            }

            report.Details.Sort(StringComparer.Ordinal);
            _candidates.Clear();
            _group.Clear();
            _handled.Clear();
            _tallies.Clear();

            _lastPassMs = (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / Stopwatch.Frequency;
            report.Milliseconds = _lastPassMs;
        }

        public double LastPassMilliseconds { get { return _lastPassMs; } }

        private void Write(string line)
        {
            if (_log != null) _log.Write(line);
        }

        /// <summary>Authority: credits a node with wreckage a Nexus agent closed near it, by the same
        /// recovery fraction as a sweep here. Returns the units credited.</summary>
        public long CreditRemote(SpatialNode node, byte server, int grids, int blocks,
                                 IDictionary<string, long> components, IDictionary<string, long> cargo)
        {
            if (node == null) return 0;
            double fraction = _tuning.SalvageRecoveryFraction;
            ServiceSettlement.SalvageCredit comps = ServiceSettlement.CreditSalvage(node.Inventory, components, fraction);
            ServiceSettlement.SalvageCredit holds = ServiceSettlement.CreditSalvage(node.Inventory, cargo, fraction);
            long units = comps.Units + holds.Units;
            if (units > 0) _recordActivity?.Invoke(node.Id);
            Write("DEBRIS_ABSORBED node=" + node.Id + " faction=" + node.OwnerFactionTag
                  + " grids=" + grids + " blocks=" + blocks + " units=" + units
                  + " frac=" + fraction.ToString("F2", CultureInfo.InvariantCulture) + " server=" + server);
            return units;
        }

        /// <summary>Tells players near the node what happened to the wreckage they could see, so a
        /// hull someone meant to grind does not simply vanish. Mirrors the fleet-progress whisper.</summary>
        private void Whisper(NodeTally tally)
        {
            if (_chat == null || _playersWithin == null) return;

            string message = "[" + tally.FactionTag + "] reclamation crews have stripped "
                           + tally.Grids + (tally.Grids == 1 ? " derelict hulk" : " derelict hulks")
                           + " at " + tally.NodeId + ".";
            foreach (ulong steamId in _playersWithin(tally.PosKm, MessageRadiusKm))
                _chat.SendMessageAsOther(WhisperAuthor, message, Color.CornflowerBlue, steamId);
        }

        /// <summary>One line per sweep, including the empty ones: a pass that refused everything and a
        /// pass with nothing to refuse are the two outcomes this feature has to be able to tell
        /// apart, and only the refusal counts do that.</summary>
        private void WritePassLine(DebrisAbsorbReport report)
        {
            Write("DEBRIS_SWEEP grids=" + report.GridsSeen
                      + " absorbed=" + report.Absorbed
                      + " blocks=" + report.Blocks
                      + " units=" + report.Units
                      + " nodes=" + report.NodesTouched
                      + " skip_kept=" + report.SkippedNotTrash
                      + " skip_owned=" + report.SkippedPlayerOwned
                      + " skip_tracked=" + report.SkippedTracked
                      + " skip_noreach=" + report.SkippedNoNodeInReach
                      + " skip_cap=" + report.SkippedByCap
                      + " ms=" + _lastPassMs.ToString("F1", CultureInfo.InvariantCulture));
        }

        public static string Describe(DebrisAbsorbReport report)
        {
            if (report == null) return "debris: no report.";

            string head = (report.Applied ? "debris absorbed: " : "debris (dry run) would absorb: ")
                        + report.Absorbed + " wreck(s), " + report.Blocks + " block(s), "
                        + report.Units + " unit(s) across " + report.NodesTouched + " node(s)"
                        + " of " + report.GridsSeen + " grid(s) seen.";

            string skips = " Refused: " + report.SkippedNotTrash + " kept by SE's own criteria, "
                         + report.SkippedPlayerOwned + " player-owned, "
                         + report.SkippedTracked + " tracked by the fleet engine, "
                         + report.SkippedNoNodeInReach + " with no node in reach, "
                         + report.SkippedByCap + " deferred to the next sweep by the per-pass cap.";

            string detail = "";
            if (report.Details.Count > 0)
                detail = "\n  " + string.Join("\n  ", report.Details.ToArray());
            if (report.Wrecks.Count > 0)
                detail += "\n  " + string.Join("\n  ", report.Wrecks.ToArray());

            return head + skips
                 + " Pass took " + report.Milliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms."
                 + detail;
        }

        /// <summary>What one wreck is, in the terms an operator needs to recognise it: what SE calls
        /// it, its entity id (so <c>factions closegrid</c> can reach it), how big it is, whether it
        /// is a group, and how far it sits from the node about to claim it.</summary>
        private static string DescribeWreck(MyCubeGrid grid, List<MyCubeGrid> group, int blocks,
                                            long units, SpatialNode node, Vec3 posKm)
        {
            double distanceM = node.Position.DistanceTo(posKm) * 1000.0;
            string name = string.IsNullOrEmpty(grid.DisplayName) ? (grid.Name ?? "?") : grid.DisplayName;
            return "eid=" + grid.EntityId
                 + " \"" + name + "\""
                 + " blocks=" + blocks
                 + (group.Count > 1 ? " grids=" + group.Count : "")
                 + " size=" + grid.GridSizeEnum
                 + " units=" + units
                 + " -> " + node.Id + " (" + node.OwnerFactionTag + ") at "
                 + distanceM.ToString("F0", CultureInfo.InvariantCulture) + "m";
        }

        private static Vec3 PositionKm(MyCubeGrid grid)
        {
            Vector3D p = grid.PositionComp.GetPosition();
            return new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0);
        }
    }
}
