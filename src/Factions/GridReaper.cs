using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>What one reaper pass did, or would do. The refusal counts are the diagnostic
    /// channel: a pass that closed nothing and a pass with nothing to close are the two outcomes
    /// that have to be tellable apart from one line.</summary>
    internal sealed class GridReapReport
    {
        public bool Applied;
        public int  GridsSeen;      // cube grids walked
        public int  SimBornGroups;  // mechanical groups carrying the record, the bit or the name
        public int  Reaped;         // grids closed, or that would have been
        public int  Blocks;
        public int  Readopted;      // records rebuilt from the grid's entity name after a restart

        public int  SkippedHeld;
        public int  SkippedStation; // a founded station, an SE-generated grid, or a respawn grid
        public int  SkippedOwned;   // an unmarked member belongs to a non-NPC identity
        public int  SkippedYoung;
        public int  SkippedByCap;

        public double Milliseconds;

        /// <summary>One line per group, on a dry run only — name and entity id, because a pass about
        /// to close grids on a live world has to be able to say <b>which</b> before anyone commits
        /// to it. An applied pass writes its totals to the log instead.</summary>
        public readonly List<string> Victims = new List<string>();
    }

    /// <summary>What one fight sweep did, or would do (FAC-425).</summary>
    internal sealed class FightDebrisReport
    {
        public bool Applied;
        public int  Sites;
        public int  ClosedGroups;
        public int  Closed;         // grids closed, or that would have been
        public int  Blocks;
        public int  SkippedHeld;
        public int  SkippedSettling;
        public int  SkippedSalvage;
        public int  SkippedOwned;   // an unmarked player member, or a captured hull
        public int  SkippedByCap;
        public double Milliseconds;
    }

    /// <summary>One name bucket in the census.</summary>
    internal sealed class GridCensusRow
    {
        public string Name;
        public int Count;
        public int Marked;
        public int BitOnly;
        public int NameOnly;
        public int Held;
        public int Station;
        public int PlayerOwned;
        public int Moving;
        public int Blocks;
    }

    /// <summary>Every grid in the world, bucketed by name. The operator's instrument: the reaper's
    /// own counters say what a pass did, and this says what is left.</summary>
    internal sealed class GridCensusReport
    {
        public int Grids;
        public int Marked;
        public int BitOnly;
        public int NameOnly;
        public int Held;
        public int Station;
        public int PlayerOwned;
        public int Moving;
        public int Blocks;
        public int Blocks1To5;
        public int Blocks6To19;
        public int Blocks20To99;
        public int Blocks100Plus;
        public double Milliseconds;
        public readonly List<GridCensusRow> Rows = new List<GridCensusRow>();
    }

    /// <summary>What a name backfill pass found or did. FAC-374.</summary>
    internal sealed class GridBackfillReport
    {
        public int GridsSeen;
        /// <summary>Grids the rule said to write a name onto.</summary>
        public int Eligible;
        /// <summary>Of those, the ones the write actually took on. Below <see cref="Eligible"/>
        /// means a grid refused the mark, which is a fault worth seeing rather than averaging away.</summary>
        public int Written;
        /// <summary>Reached only through a mechanical group, not on their own evidence — a turret
        /// subgrid whose hull carried the tag.</summary>
        public int ViaGroup;
        public int Blocks;
        /// <summary>Already carrying a correct name: nothing to do, and the number that should rise
        /// to meet <see cref="Eligible"/> on a second pass.</summary>
        public int AlreadyNamed;
        /// <summary>Our prefix under another grid's id — a pasted blueprint of an NPC hull.
        /// Refused, and counted, because it is the one case the display tag actively misleads on.</summary>
        public int ForeignName;
        public double Milliseconds;
        public readonly List<string> Sample = new List<string>();
    }

    /// <summary>
    /// Closes the grids the simulation put in the world once nothing holds them.
    ///
    /// <para><b>It walks the world, not the ledger.</b> The fleet engine knows the ids it spawned;
    /// it does not know the rotor-mounted turret a prefab carried, the piece a hull shed in a fight,
    /// or anything at all after a restart, because the <see cref="SimGridRegistry"/> record is
    /// per-process. What survives a restart is on the grid itself: <c>IsNpcSpawnedGrid</c>, which SE
    /// clears once a non-NPC identity has owned the grid for <c>NPCGridClaimTimeLimit</c>, and the
    /// <c>factions:npc:</c> entity name, which SE never touches. So the pass starts from every grid
    /// in the world, rebuilds the record of every grid carrying the name
    /// (<see cref="SimGridMarks.TryReadopt"/>), and asks each one whether anything still holds it.
    /// That is also what closes the "orphan survived two restarts" class the boot pass exists for.</para>
    ///
    /// <para><b>The mechanical group is the unit.</b> Judging grids one at a time would reap the
    /// turrets off a hull a fleet is still flying — only the lead's id is in
    /// <c>MaterialisedEntityId</c> — and would close half of an assembly a player is docked to. Each
    /// grid is judged with its whole group, and the group is closed or kept together.</para>
    ///
    /// <para><b>The mark trumps the owner.</b> A hull spawns as <c>faction.FounderId</c>, so a player
    /// faction's convoy is owned by a player identity by construction and an ownership predicate
    /// would refuse to close every one of them. The ownership guard therefore applies only to
    /// <b>unmarked</b> members of the group — a player docked to a convoy, or building on it.</para>
    ///
    /// <para>Closing goes through <see cref="GridAssembly"/>, the same helper a despawn uses: it
    /// walks the <i>mechanical</i> group rather than <c>MySessionComponentTrash.RemoveGrid</c>'s
    /// physical one, which holds a player's ship docked by connector.</para>
    ///
    /// <para>Game thread only, like everything that touches <c>MyEntities</c>.</para>
    /// </summary>
    internal sealed class GridReaper
    {
        private const int    MaxVictimLines = 60;  // a bridge/chat result is one string
        private const int    MaxCensusRows  = 60;
        private const double MovingMs       = 0.5; // below this a grid is parked, not coasting

        private readonly FactionTuning _tuning;
        private readonly DecisionLog   _log;
        private readonly Func<long, bool> _isHeld;
        private readonly Func<long, bool> _isStationGrid;
        private readonly Func<Vec3, double, bool> _anyPlayerWithinKm;

        // Reused across passes; a pass runs inside one Torch frame.
        private readonly List<MyCubeGrid> _candidates = new List<MyCubeGrid>();
        private readonly List<MyCubeGrid> _group      = new List<MyCubeGrid>();
        private readonly HashSet<long>    _handled    = new HashSet<long>();

        private readonly DateTime _bootUtc = DateTime.UtcNow;

        private DateTime _nextDueUtc    = DateTime.MinValue;
        private bool     _bootPassDue;
        private double   _lastPassMs;

        public GridReaper(
            FactionTuning tuning,
            DecisionLog log,
            Func<long, bool> isHeld,
            Func<long, bool> isStationGrid,
            Func<Vec3, double, bool> anyPlayerWithinKm)
        {
            _tuning            = tuning;
            _log               = log;
            _isHeld            = isHeld;
            _isStationGrid     = isStationGrid;
            _anyPlayerWithinKm = anyPlayerWithinKm;
        }

        public double LastPassMilliseconds { get { return _lastPassMs; } }

        /// <summary>Asks for one pass on the next tick. The plugin calls this once, after the runtime
        /// has loaded and reconciled its fleets: until then every saved hull reads as unheld, and
        /// after it the grids no surviving fleet claims are exactly what this closes.</summary>
        public void RequestBootPass() { _bootPassDue = true; }

        /// <summary>Runs the boot pass once, then a real pass every
        /// <see cref="FactionTuning.GridReapIntervalSeconds"/>. Scheduled against the wall clock
        /// rather than a tick count, because the caller is itself on a sub-interval.</summary>
        public void Tick()
        {
            if (_tuning == null || !_tuning.GridReapEnabled) return;

            double intervalSeconds = _tuning.GridReapIntervalSeconds;
            if (intervalSeconds <= 0) return;

            DateTime now = DateTime.UtcNow;
            if (_bootPassDue)
            {
                _bootPassDue = false;
                _nextDueUtc  = now.AddSeconds(intervalSeconds);
                Sweep(true);
                return;
            }

            if (_nextDueUtc == DateTime.MinValue) { _nextDueUtc = now.AddSeconds(intervalSeconds); return; }
            if (now < _nextDueUtc) return;
            _nextDueUtc = now.AddSeconds(intervalSeconds);

            Sweep(true);
        }

        /// <summary>One pass over every grid in the world. With <paramref name="apply"/> false nothing
        /// is closed and the report says what would have been.</summary>
        public GridReapReport Sweep(bool apply)
        {
            var report = new GridReapReport { Applied = apply };
            if (_tuning == null || MySession.Static == null) return report;

            long started = Stopwatch.GetTimestamp();
            _handled.Clear();
            CollectGrids(report);

            int cap = _tuning.GridReapMaxGridsPerPass;
            if (cap <= 0) cap = int.MaxValue;
            double grace    = _tuning.GridReapGraceSeconds;
            double radiusKm = _tuning.ObservationRadiusKm + FactionRuntime.DespawnHysteresisKm;
            DateTime now    = DateTime.UtcNow;

            for (int i = 0; i < _candidates.Count; i++)
            {
                MyCubeGrid grid = _candidates[i];
                if (grid == null || grid.MarkedForClose) continue;
                if (_handled.Contains(grid.EntityId)) continue;

                CollectMechanicalGroup(grid, _group);
                for (int g = 0; g < _group.Count; g++)
                {
                    MyCubeGrid member = _group[g];
                    if (member == null) continue;
                    _handled.Add(member.EntityId);
                    if (SimGridMarks.TryReadopt(member, _bootUtc)) report.Readopted++;
                }

                ReapCandidate candidate = Describe(grid, _group, now, radiusKm);
                ReapVerdict verdict = GridReaping.Judge(candidate, grace);

                if (verdict != ReapVerdict.NotSimBorn) report.SimBornGroups++;

                switch (verdict)
                {
                    case ReapVerdict.NotSimBorn: continue;
                    case ReapVerdict.Held:       report.SkippedHeld++;    continue;
                    case ReapVerdict.Station:
                    case ReapVerdict.Respawn:    report.SkippedStation++; continue;
                    case ReapVerdict.PlayerOwned:report.SkippedOwned++;   continue;
                    case ReapVerdict.TooYoung:   report.SkippedYoung++;   continue;
                }

                // The cap is tested past every refusal, so the count it produces is the number of
                // groups genuinely waiting for the next pass rather than the number the loop stopped
                // looking at.
                if (report.Reaped >= cap) { report.SkippedByCap++; continue; }

                if (!apply && report.Victims.Count < MaxVictimLines)
                    report.Victims.Add(Describe(grid, _group, candidate));

                if (apply && DebugTraces.GridMarks)
                    FactionsPlugin.Log.Info("GridReaper: closing " + Describe(grid, _group, candidate));

                int closed = apply ? GridAssembly.Close(grid) : CountLive(_group);
                report.Reaped += closed;
                report.Blocks += candidate.Blocks;
            }

            _candidates.Clear();
            _group.Clear();
            _handled.Clear();

            _lastPassMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            report.Milliseconds = _lastPassMs;

            // Silent when the world holds nothing of ours at all: a line every five minutes saying
            // there was nothing to do is noise, and the census verb answers that question on demand.
            if (apply && report.SimBornGroups > 0) WritePassLine(report);
            return report;
        }

        /// <summary>Writes the durable <c>factions:npc:</c> entity name onto grids that predate the
        /// marking, so the reaper can see them at all. FAC-374. Dry run unless
        /// <paramref name="apply"/>.
        ///
        /// <para><b>Why this is operator-driven and not a boot pass.</b> <see cref="SimGridMarks"/>'s
        /// readoption runs unattended because it reads a name that is already a predicate. This
        /// promotes evidence that is not one — SE's claim-clearable bit, or a display tag a player
        /// can type — so the list is shown before anything is written. What a wrongly promoted grid
        /// then faces is the reaper's own refusals (held, station, player-owned, grace), not
        /// deletion.</para>
        ///
        /// <para>The mechanical group is walked so a turret subgrid is not left behind when its hull
        /// is named; those are counted separately, because a grid reached only through its group is
        /// a weaker claim than one that carried the tag itself.</para></summary>
        public GridBackfillReport BackfillNames(bool apply)
        {
            var report = new GridBackfillReport();
            if (MySession.Static == null) return report;

            long started = Stopwatch.GetTimestamp();
            DateTime now = DateTime.UtcNow;
            var seen = new HashSet<long>();
            var roots = new List<MyCubeGrid>();

            MyAPIGateway.Entities.GetEntities(null, entity =>
            {
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.MarkedForClose) return false;
                report.GridsSeen++;
                roots.Add(grid);
                return false;
            });

            // Collected first, then mutated. Writing grid.Name re-keys SE's entity-name dictionary
            // (MyEntities.SetEntityName removes the old key), and doing that inside its own
            // enumeration is not a thing to find out about on a live server.
            var group = new List<MyCubeGrid>();
            for (int i = 0; i < roots.Count; i++)
            {
                MyCubeGrid root = roots[i];
                if (root == null || root.MarkedForClose) continue;

                bool rootEligible = SimGridMarks.TryBackfillName(root, now, false);
                if (!rootEligible)
                {
                    if (NpcGridNaming.IsSimBornName(root.Name, root.EntityId)) report.AlreadyNamed++;
                    else if (NpcGridNaming.IsSimBornName(root.Name)) report.ForeignName++;
                    continue;
                }

                CollectMechanicalGroup(root, group);
                for (int g = 0; g < group.Count; g++)
                {
                    MyCubeGrid member = group[g];
                    if (member == null || member.MarkedForClose) continue;
                    if (!seen.Add(member.EntityId)) continue;
                    if (!SimGridMarks.TryBackfillName(member, now, false)) continue;

                    report.Eligible++;
                    report.Blocks += member.BlocksCount;
                    if (member != root) report.ViaGroup++;
                    if (report.Sample.Count < 12)
                        report.Sample.Add(member.EntityId + " " + GridReaping.NameBucket(member.DisplayName)
                                          + " (" + member.BlocksCount + "b)");
                    if (apply && SimGridMarks.TryBackfillName(member, now)) report.Written++;
                }
                group.Clear();
            }

            report.Milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (apply)
                FactionsPlugin.Log.Info("GRID_BACKFILL seen=" + report.GridsSeen
                    + " eligible=" + report.Eligible + " written=" + report.Written
                    + " viaGroup=" + report.ViaGroup + " blocks=" + report.Blocks);
            return report;
        }

        public static string Describe(GridBackfillReport r)
        {
            if (r == null) return "grids backfill: no report.";
            string s = "grids backfill: " + r.Eligible + " grid(s) eligible, " + r.Blocks + " block(s), of "
                     + r.GridsSeen + " seen; " + r.ViaGroup + " reached only through a mechanical group; "
                     + r.AlreadyNamed + " already named, " + r.ForeignName + " carrying our prefix under "
                     + "another grid's id (refused). Written: " + r.Written + ". Pass took "
                     + r.Milliseconds.ToString("0.0") + " ms.";
            if (r.Sample.Count > 0) s += " | " + string.Join(" | ", r.Sample.ToArray());
            if (r.Eligible > r.Written && r.Written > 0)
                s += " | NOTE: " + (r.Eligible - r.Written) + " eligible grid(s) refused the mark.";
            return s;
        }

        /// <summary>Every grid in the world, bucketed by name. Walks the same set the reaper does and
        /// judges every grid, not only the sim-born ones, so a bucket that should have emptied and
        /// has not is visible beside the reason. <c>marked</c> / <c>bit</c> / <c>name</c> are
        /// exclusive and answer <i>which carrier identified this grid</i>, cheapest-to-lose first: a
        /// record this process wrote, else <c>IsNpcSpawnedGrid</c>, else the <c>factions:npc:</c>
        /// entity name — the one SE cannot clear, and so the only one left on a grid that came back
        /// from the save with its bit already claimed away.</summary>
        public GridCensusReport Census()
        {
            var report = new GridCensusReport();
            if (MySession.Static == null) return report;

            long started = Stopwatch.GetTimestamp();
            var buckets = new Dictionary<string, GridCensusRow>(StringComparer.Ordinal);
            MyPlayerCollection players = MySession.Static.Players;

            MyAPIGateway.Entities.GetEntities(null, entity =>
            {
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.MarkedForClose) return false;

                SimGridRecord record;
                bool hasRecord = SimGridMarks.TryGet(grid.EntityId, out record) && record != null;
                bool marked  = hasRecord && !record.Restored;
                bool bit     = grid.IsNpcSpawnedGrid;
                bool named   = (hasRecord && record.Restored)
                               || NpcGridNaming.IsSimBornName(grid.Name, grid.EntityId);
                bool held    = _isHeld != null && _isHeld(grid.EntityId);
                bool station = grid.IsGenerated || (_isStationGrid != null && _isStationGrid(grid.EntityId));
                bool owned   = TouchesPlayerProperty(players, grid);
                bool moving  = SpeedMs(grid) > MovingMs;
                int  blocks  = grid.BlocksCount;

                report.Grids++;
                if (marked) report.Marked++;
                else if (bit) report.BitOnly++;
                else if (named) report.NameOnly++;
                if (held) report.Held++;
                if (station) report.Station++;
                if (owned) report.PlayerOwned++;
                if (moving) report.Moving++;
                report.Blocks += blocks;
                if (blocks < 6) report.Blocks1To5++;
                else if (blocks < 20) report.Blocks6To19++;
                else if (blocks < 100) report.Blocks20To99++;
                else report.Blocks100Plus++;

                string key = GridReaping.NameBucket(grid.DisplayName);
                GridCensusRow row;
                if (!buckets.TryGetValue(key, out row))
                {
                    row = new GridCensusRow { Name = key };
                    buckets[key] = row;
                }
                row.Count++;
                if (marked) row.Marked++;
                else if (bit) row.BitOnly++;
                else if (named) row.NameOnly++;
                if (held) row.Held++;
                if (station) row.Station++;
                if (owned) row.PlayerOwned++;
                if (moving) row.Moving++;
                row.Blocks += blocks;
                return false;
            });

            foreach (KeyValuePair<string, GridCensusRow> kv in buckets) report.Rows.Add(kv.Value);
            report.Rows.Sort((a, b) =>
            {
                int byCount = b.Count.CompareTo(a.Count);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Name, b.Name);
            });
            report.Milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            return report;
        }

        // ---- collection ------------------------------------------------------------------------

        private void CollectGrids(GridReapReport report)
        {
            _candidates.Clear();
            int seen = 0;
            MyAPIGateway.Entities.GetEntities(null, entity =>
            {
                var grid = entity as MyCubeGrid;
                if (grid == null) return false;
                seen++;
                if (grid.MarkedForClose || grid.IsPreview || grid.Projector != null) return false;
                _candidates.Add(grid);
                return false;
            });
            report.GridsSeen = seen;
        }

        /// <summary>The grid's mechanical group into <paramref name="into"/> (cleared first), the grid
        /// itself included — an unlinked grid is a group of one whichever way SE answers.</summary>
        private static void CollectMechanicalGroup(MyCubeGrid grid, List<MyCubeGrid> into)
        {
            into.Clear();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Mechanical != null)
            {
                try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, into); }
                catch (Exception ex)
                {
                    FactionsPlugin.Log.Warn(ex, "GridReaper: mechanical group lookup failed for grid "
                                              + grid.EntityId + ".");
                    into.Clear();
                }
            }
            if (!into.Contains(grid)) into.Add(grid);
        }

        // ---- the facts the rule is written in terms of -------------------------------------------

        private ReapCandidate Describe(MyCubeGrid root, List<MyCubeGrid> group, DateTime nowUtc, double radiusKm)
        {
            MyPlayerCollection players = MySession.Static != null ? MySession.Static.Players : null;
            var c = new ReapCandidate { EntityId = root.EntityId, AgeSeconds = double.MaxValue };

            for (int i = 0; i < group.Count; i++)
            {
                MyCubeGrid member = group[i];
                if (member == null) continue;

                SimGridRecord record;
                bool hasRecord = SimGridMarks.TryGet(member.EntityId, out record) && record != null;
                bool restored  = hasRecord && record.Restored;
                bool bit       = member.IsNpcSpawnedGrid;
                bool named     = restored || NpcGridNaming.IsSimBornName(member.Name, member.EntityId);

                // A restored record dates nothing: its MarkedUtc is this process's boot, so the
                // group reads as old, which is what a grid that came back from the save is.
                if (hasRecord && !restored)
                {
                    c.Marked = true;
                    double age = (nowUtc - record.MarkedUtc).TotalSeconds;
                    if (age < c.AgeSeconds) c.AgeSeconds = age;
                }
                if (bit)   c.Bit = true;
                if (named) c.NamedSimBorn = true;

                // The ownership guard is on the members carrying none of the three carriers: a
                // sim-born grid is ours whatever its BigOwners says, because a player faction's
                // convoy spawns owned by the player's own identity.
                if (!hasRecord && !bit && !named && TouchesPlayerProperty(players, member))
                    c.UnmarkedPlayerOwned = true;

                if (_isHeld != null && _isHeld(member.EntityId)) c.Held = true;
                if (member.IsRespawnGrid) c.Respawn = true;
                if (member.IsGenerated || (_isStationGrid != null && _isStationGrid(member.EntityId)))
                    c.Station = true;

                c.Blocks += member.BlocksCount;
            }
            c.Captured = AnyCaptured(group);

            if (_anyPlayerWithinKm != null && root.PositionComp != null)
                c.PlayerNear = _anyPlayerWithinKm(PositionKm(root), radiusKm);
            return c;
        }

        private static bool TouchesPlayerProperty(MyPlayerCollection players, MyCubeGrid grid)
        {
            if (players == null) return true;   // cannot tell whose it is -- refuse
            return OwnersIncludePlayer(players, grid.BigOwners)
                || OwnersIncludePlayer(players, grid.SmallOwners);
        }

        /// <summary>Whether any block on this grid belongs to a non-NPC identity. Shared with the
        /// emission sampler so "whose grid is this" has one answer in this plugin.</summary>
        internal static bool IsPlayerOwned(MyPlayerCollection players, MyCubeGrid grid)
        {
            if (grid == null) return false;
            return TouchesPlayerProperty(players, grid);
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

        // ---- the fight sweep (FAC-425) -----------------------------------------------------------

        private readonly List<long> _fightEids = new List<long>();

        /// <summary>One pass of the fight sweep: every marked grid within
        /// <see cref="FightDebris.RadiusKm"/> of a site in <paramref name="sites"/>, judged with its
        /// mechanical group by <see cref="FightDebris.Judge"/> and closed through
        /// <see cref="GridAssembly"/>. Walks the marked set rather than the world: what a fight sheds is
        /// marked as it breaks off (<see cref="SimGridMarks"/>), and the world walk is the 300 s reaper's.</summary>
        public FightDebrisReport SweepFight(FightSiteMemory sites, bool simFloorTripped, bool apply)
        {
            var report = new FightDebrisReport { Applied = apply, Sites = sites != null ? sites.Count : 0 };
            if (sites == null || sites.Count == 0 || MySession.Static == null) return report;

            long started = Stopwatch.GetTimestamp();
            _handled.Clear();
            SimGridMarks.CopyAllEids(_fightEids);
            DateTime now = DateTime.UtcNow;
            double radiusKm = _tuning != null ? _tuning.ObservationRadiusKm + FactionRuntime.DespawnHysteresisKm : 16.0;

            for (int i = 0; i < _fightEids.Count; i++)
            {
                MyCubeGrid grid;
                if (!MyEntities.TryGetEntityById(_fightEids[i], out grid, false) || grid == null || grid.MarkedForClose) continue;
                if (_handled.Contains(grid.EntityId) || grid.PositionComp == null) continue;

                double distanceKm = sites.NearestKm(PositionKm(grid));
                if (!(distanceKm <= FightDebris.RadiusKm)) continue;

                CollectMechanicalGroup(grid, _group);
                for (int g = 0; g < _group.Count; g++)
                    if (_group[g] != null) _handled.Add(_group[g].EntityId);

                var facts = new FightDebrisFacts
                {
                    Group = Describe(grid, _group, now, radiusKm),
                    DistanceKm = distanceKm,
                };
                facts.Captured = facts.Group.Captured;
                FightDebrisVerdict verdict = FightDebris.Judge(facts, simFloorTripped);
                switch (verdict)
                {
                    case FightDebrisVerdict.Held:     report.SkippedHeld++;     continue;
                    case FightDebrisVerdict.Settling: report.SkippedSettling++; continue;
                    case FightDebrisVerdict.Salvage:  report.SkippedSalvage++;  continue;
                    case FightDebrisVerdict.Captured:
                    case FightDebrisVerdict.PlayerOwned: report.SkippedOwned++; continue;
                    case FightDebrisVerdict.Close: break;
                    default: continue;
                }

                if (report.ClosedGroups >= FightDebris.MaxGroupsPerPass) { report.SkippedByCap++; continue; }
                report.ClosedGroups++;
                report.Blocks += facts.Group.Blocks;
                report.Closed += apply ? GridAssembly.Close(grid) : CountLive(_group);
            }

            _fightEids.Clear();
            _group.Clear();
            _handled.Clear();
            report.Milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (apply && report.ClosedGroups > 0)
                _log?.Write("FIGHT_DEBRIS_CLOSED grids=" + report.Closed
                          + " groups=" + report.ClosedGroups
                          + " blocks=" + report.Blocks
                          + " sites=" + report.Sites
                          + " skip_held=" + report.SkippedHeld
                          + " skip_settling=" + report.SkippedSettling
                          + " skip_salvage=" + report.SkippedSalvage
                          + " skip_owned=" + report.SkippedOwned
                          + " skip_cap=" + report.SkippedByCap
                          + (simFloorTripped ? " sim_floor" : "")
                          + " ms=" + report.Milliseconds.ToString("F1", CultureInfo.InvariantCulture));
            return report;
        }

        /// <summary>Some marked member is owned by a faction other than the one it was marked for, or by a
        /// factionless player — a player took the hull, and its pieces carry the new owner's blocks.
        /// Unowned members say nothing.</summary>
        private static bool AnyCaptured(List<MyCubeGrid> group)
        {
            var factions = MySession.Static != null ? MySession.Static.Factions : null;
            if (factions == null) return false;
            for (int i = 0; i < group.Count; i++)
            {
                MyCubeGrid member = group[i];
                SimGridRecord record;
                if (member == null || !SimGridMarks.TryGet(member.EntityId, out record) || record == null
                    || string.IsNullOrEmpty(record.FactionTag)) continue;
                List<long> owners = member.BigOwners;
                if (owners == null) continue;
                for (int o = 0; o < owners.Count; o++)
                {
                    if (owners[o] == 0) continue;
                    var faction = factions.TryGetPlayerFaction(owners[o]);
                    // A factionless owner says nothing unless it is a player: a factionless player
                    // who took the hull has captured it as surely as one in a faction.
                    if (faction == null)
                    {
                        var players = MySession.Static.Players;
                        if (players != null && !players.IdentityIsNpc(owners[o])) return true;
                        continue;
                    }
                    if (!string.Equals(faction.Tag, record.FactionTag, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        // ---- acting ------------------------------------------------------------------------------

        private static int CountLive(List<MyCubeGrid> group)
        {
            int n = 0;
            for (int i = 0; i < group.Count; i++)
                if (group[i] != null && !group[i].MarkedForClose) n++;
            return n;
        }

        // ---- reporting ---------------------------------------------------------------------------

        /// <summary>To the DecisionLog; a Nexus agent's goes to the authority's, tagged server=.</summary>
        private void WritePassLine(GridReapReport report)
        {
            string line = "GRID_REAPED grids=" + report.GridsSeen
                      + " reaped=" + report.Reaped
                      + " blocks=" + report.Blocks
                      + " skip_held=" + report.SkippedHeld
                      + " skip_station=" + report.SkippedStation
                      + " skip_owned=" + report.SkippedOwned
                      + " skip_young=" + report.SkippedYoung
                      + " skip_cap=" + report.SkippedByCap
                      + " readopted=" + report.Readopted
                      + " ms=" + report.Milliseconds.ToString("F1", CultureInfo.InvariantCulture);
            if (_log != null) _log.Write(line);
        }

        private static string Describe(MyCubeGrid root, List<MyCubeGrid> group, ReapCandidate c)
        {
            string name = string.IsNullOrEmpty(root.DisplayName) ? (root.Name ?? "?") : root.DisplayName;
            return "eid=" + root.EntityId + " \"" + name + "\""
                 + " blocks=" + c.Blocks
                 + (group.Count > 1 ? " grids=" + group.Count : "")
                 + (c.Marked ? " marked" : c.Bit ? " bit" : " name")
                 + (c.AgeSeconds >= double.MaxValue ? " age=unknown"
                    : " age=" + c.AgeSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s")
                 + (c.PlayerNear ? " player_near" : "");
        }

        public static string Describe(GridReapReport report)
        {
            if (report == null) return "grids reap: no report.";

            string head = (report.Applied ? "grids reaped: " : "grids (dry run) would reap: ")
                        + report.Reaped + " grid(s), " + report.Blocks + " block(s)"
                        + " of " + report.GridsSeen + " grid(s) seen, "
                        + report.SimBornGroups + " simulation-born group(s), "
                        + report.Readopted + " record(s) rebuilt from a grid's entity name.";

            string skips = " Refused: " + report.SkippedHeld + " held by a fleet, "
                         + report.SkippedStation + " station or respawn, "
                         + report.SkippedOwned + " with an unmarked player-owned member, "
                         + report.SkippedYoung + " inside the grace with a player in range, "
                         + report.SkippedByCap + " deferred to the next pass by the per-pass cap.";

            string detail = report.Victims.Count > 0
                ? "\n  " + string.Join("\n  ", report.Victims.ToArray())
                : "";

            return head + skips
                 + " Pass took " + report.Milliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms."
                 + detail;
        }

        public static string Describe(GridCensusReport report)
        {
            if (report == null) return "grids census: no report.";

            var sb = new System.Text.StringBuilder();
            sb.Append("grids census: ").Append(report.Grids).Append(" grid(s), ")
              .Append(report.Blocks).Append(" block(s); marked=").Append(report.Marked)
              .Append(" bit_only=").Append(report.BitOnly)
              .Append(" name_only=").Append(report.NameOnly)
              .Append(" held=").Append(report.Held)
              .Append(" station=").Append(report.Station)
              .Append(" player_owned=").Append(report.PlayerOwned)
              .Append(" moving=").Append(report.Moving)
              .Append("; blocks 1-5=").Append(report.Blocks1To5)
              .Append(" 6-19=").Append(report.Blocks6To19)
              .Append(" 20-99=").Append(report.Blocks20To99)
              .Append(" 100+=").Append(report.Blocks100Plus)
              .Append(". Walk took ").Append(report.Milliseconds.ToString("F1", CultureInfo.InvariantCulture))
              .Append(" ms.");

            int shown = report.Rows.Count < MaxCensusRows ? report.Rows.Count : MaxCensusRows;
            for (int i = 0; i < shown; i++)
            {
                GridCensusRow r = report.Rows[i];
                sb.Append("\n  ").Append(r.Count.ToString(CultureInfo.InvariantCulture).PadLeft(4))
                  .Append("  ").Append(Pad(r.Name, 34))
                  .Append(" marked=").Append(r.Marked)
                  .Append(" bit=").Append(r.BitOnly)
                  .Append(" name=").Append(r.NameOnly)
                  .Append(" held=").Append(r.Held)
                  .Append(" stn=").Append(r.Station)
                  .Append(" plr=").Append(r.PlayerOwned)
                  .Append(" mov=").Append(r.Moving)
                  .Append(" blocks=").Append(r.Blocks);
            }
            if (report.Rows.Count > shown)
                sb.Append("\n  (").Append(report.Rows.Count - shown).Append(" more name bucket(s))");
            return sb.ToString();
        }

        private static string Pad(string s, int width)
        {
            if (s == null) s = "";
            if (s.Length >= width) return s.Substring(0, width);
            return s.PadRight(width);
        }

        private static double SpeedMs(MyCubeGrid grid)
        {
            return grid.LinearVelocity.Length();
        }

        private static Vec3 PositionKm(MyCubeGrid grid)
        {
            Vector3D p = grid.PositionComp.GetPosition();
            return new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0);
        }
    }
}
