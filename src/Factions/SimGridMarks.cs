using System;
using System.Collections.Generic;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Marks every grid the simulation puts in the world so it can be told from a player's later:
    /// the hull a prefab spawn produced, every grid mechanically attached to it (a rotor-mounted
    /// turret is its own <see cref="MyCubeGrid"/>, and a prefab can carry several), every piece that
    /// splits off a marked grid, and every grid a marked hull gives birth to at runtime.
    ///
    /// <para><b>Four carriers, each doing one job, and one of them durable.</b>
    /// <see cref="MyCubeGrid.IsNpcSpawnedGrid"/> is persisted in the sector file and copied to a
    /// split's child by SE itself (<c>MyCubeGrid.CreateSplit</c>) — but SE owns it:
    /// <c>MyNPCGridClaimSessionComponent</c> clears it on any grid a non-NPC identity owns once the
    /// claim timer expires, and every hull a player faction flies spawns owned by its founder. The
    /// entity name (<see cref="NpcGridNaming.NameFor"/>) is written to
    /// <c>MyObjectBuilder_EntityBase.Name</c>, is not shown to players, and SE never rewrites it, so
    /// it is what still answers after a restart; it carries the fleet key, which is how a record is
    /// rebuilt (<see cref="TryReadopt"/>). The display name carries <see cref="NpcGridNaming.Tag"/>
    /// so a player, or an operator reading a census, can see it, and is never a predicate. The
    /// <see cref="SimGridRegistry"/> record says <i>whose</i> for as long as this process runs.</para>
    ///
    /// <para><b>Why the split hook is per grid.</b> SE raises <c>OnGridSplit</c> on the parent, after
    /// giving the child the parent's velocity and NPC bit, and names the child <c>Small Grid 1234</c>
    /// — the same default a player's unrenamed hull carries, which is why a name match can never be
    /// the rule. Only grids this class marked are subscribed, so a player's grid splitting is never
    /// touched.</para>
    ///
    /// <para><b>Adoption is deferred.</b> A rotor top links to its base a frame or more after
    /// <c>MyEntities.OnEntityAdd</c> fires for it, and a new grid's owners are not computed yet, so a
    /// candidate is held for <see cref="AdoptWindowSeconds"/> and re-checked each tick: adopted when
    /// it shares a mechanical group with a marked grid, or when it is NPC-owned by the same identity
    /// as a marked grid within <see cref="AdoptRadiusM"/> (ordnance a hull released). The ownership
    /// rule is NPC identities only, so a player building beside their own faction's convoy is never
    /// adopted.</para>
    ///
    /// <para>Game thread only, like everything that touches <c>MyEntities</c>.</para>
    /// </summary>
    internal static class SimGridMarks
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private const double AdoptWindowSeconds = 5.0;
        private const double AdoptRadiusM       = 1000.0;

        private static readonly SimGridRegistry Registry = new SimGridRegistry();
        private static readonly List<MyCubeGrid> _groupScratch = new List<MyCubeGrid>();
        private static readonly List<long>       _eidScratch   = new List<long>();

        private struct Pending
        {
            public long Eid;
            public DateTime DeadlineUtc;
        }
        private static readonly List<Pending> _pending = new List<Pending>();

        private static bool _attached;

        public static int MarkedCount { get { return Registry.Count; } }
        public static bool IsMarked(long entityId) { return Registry.IsMarked(entityId); }
        public static bool TryGet(long entityId, out SimGridRecord record) { return Registry.TryGet(entityId, out record); }
        /// <summary>Every marked grid flying under <paramref name="fleetKey"/>, into
        /// <paramref name="into"/> (cleared first). What a despawn of that fleet closes beyond the
        /// hull's own mechanical group: split debris and released ordnance hang off nothing.</summary>
        public static void CopyEidsForFleet(string fleetKey, List<long> into) { Registry.CopyEidsForFleet(fleetKey, into); }
        /// <summary>Every marked grid, into <paramref name="into"/> (cleared first). The fight sweep's
        /// candidate set: what a fight sheds is marked when it breaks off.</summary>
        public static void CopyAllEids(List<long> into) { Registry.CopyEids(into); }

        /// <summary>Starts watching for grids born beside tracked ones. Idempotent.</summary>
        public static void Attach()
        {
            if (_attached) return;
            MyEntities.OnEntityAdd += OnEntityAdd;
            _attached = true;
        }

        /// <summary>Stops watching and forgets every record. The grids keep their bit, their entity
        /// name and their display name; the next process rebuilds the records from the entity names,
        /// re-derives what it holds from its fleets, and reaps the rest.</summary>
        public static void Detach()
        {
            if (_attached) MyEntities.OnEntityAdd -= OnEntityAdd;
            _attached = false;
            Registry.CopyEids(_eidScratch);
            for (int i = 0; i < _eidScratch.Count; i++)
            {
                MyCubeGrid grid;
                if (MyEntities.TryGetEntityById(_eidScratch[i], out grid, false) && grid != null)
                    Unsubscribe(grid);
            }
            _eidScratch.Clear();
            Registry.Clear();
            _pending.Clear();
        }

        /// <summary>Marks every grid in <paramref name="spawned"/> and every grid mechanically attached
        /// to one of them. Returns how many grids were newly marked — the <c>grids=</c> token on the
        /// spawn line, and the number a despawn is expected to close.</summary>
        public static int MarkAssembly(IList<MyCubeGrid> spawned, string fleetKey, string factionTag, string kind, DateTime nowUtc)
        {
            if (spawned == null) return 0;
            int marked = 0;
            for (int i = 0; i < spawned.Count; i++)
            {
                MyCubeGrid root = spawned[i];
                if (root == null) continue;
                CollectMechanicalGroup(root, _groupScratch);
                for (int g = 0; g < _groupScratch.Count; g++)
                {
                    MyCubeGrid member = _groupScratch[g];
                    SimGridRecord prior;
                    if (Registry.TryGet(member.EntityId, out prior) && Registry.ReleaseForeign(member.EntityId, fleetKey))
                    {
                        Unsubscribe(member);
                        if (DebugTraces.GridMarks)
                            Log.Info("SimGridMarks: grid " + member.EntityId + " reclaimed from fleet=" + prior.FleetKey
                                     + " by its own spawn fleet=" + fleetKey);
                    }
                    if (Mark(member, fleetKey, factionTag, kind, 0L, nowUtc, NpcGridNaming.Apply(member.DisplayName)))
                        marked++;
                }
                _groupScratch.Clear();
            }
            return marked;
        }

        /// <summary>Re-checks the grids seen since the last tick against the marked set. Cheap when
        /// nothing is pending, which is every tick that no grid was born.</summary>
        public static void Tick(DateTime nowUtc)
        {
            if (_pending.Count == 0) return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                MyCubeGrid grid;
                bool drop = !MyEntities.TryGetEntityById(p.Eid, out grid, false) || grid == null
                            || grid.MarkedForClose || Registry.IsMarked(p.Eid)
                            || TryAdopt(grid, nowUtc) || nowUtc >= p.DeadlineUtc;
                if (drop) _pending.RemoveAt(i);
            }
        }

        /// <summary>Rebuilds the record for a grid that came back from the save carrying this
        /// plugin's entity name. Returns true when it did.
        ///
        /// <para>The record is per-process, so after a restart nothing says <i>whose</i> a grid is —
        /// and the NPC-spawned bit does not close that gap, because SE clears it on every grid a
        /// non-NPC identity owns once <c>NPCGridClaimTimeLimit</c> passes. The entity name survives
        /// both, and carries the fleet key, so <see cref="GridAssembly"/>'s fleet-key pass and
        /// <c>FactionRuntime.HoldsMaterialisedGrid</c> answer for a restored grid exactly as they do
        /// for one marked in this process. <c>MarkedUtc</c> is this process's boot and dates nothing
        /// about the grid, so the record is flagged restored and no grace is read off it.</para></summary>
        public static bool TryReadopt(MyCubeGrid grid, DateTime bootUtc)
        {
            if (grid == null || grid.MarkedForClose) return false;
            if (Registry.IsMarked(grid.EntityId)) return false;

            long namedEid;
            string fleetKey;
            if (!NpcGridNaming.TryParseName(grid.Name, out namedEid, out fleetKey)) return false;
            if (namedEid != grid.EntityId) return false;

            bool ok = Mark(grid, fleetKey, "", "", 0L, bootUtc, NpcGridNaming.Apply(grid.DisplayName), true);
            if (ok && DebugTraces.GridMarks)
                Log.Info("SimGridMarks: grid " + grid.EntityId + " (" + grid.BlocksCount + " blocks) readopted from its name"
                         + " fleet=" + fleetKey);
            return ok;
        }

        /// <summary>Writes the durable entity name onto a grid that carries a weaker NPC signal and
        /// no name of its own — a grid that predates the marking, and which nothing in this plugin
        /// can otherwise identify after a restart. FAC-374.
        ///
        /// <para><b>Distinct from <see cref="TryReadopt"/>.</b> That one rebuilds a record from a
        /// name the grid already has, and is safe to run unattended on every boot because the name
        /// is a predicate. This one <i>creates</i> the name from evidence that is not — SE's
        /// <c>IsNpcSpawnedGrid</c>, which its claim timer clears, or the <c>[NPC]</c> display tag,
        /// which a player can type. So it is operator-driven and dry-run by default; it is never
        /// called from a tick.</para>
        ///
        /// <para>Marked <c>restored</c> for the same reason a readoption is: <c>MarkedUtc</c> would
        /// be this process's clock and dates nothing about a grid that has been in the world for
        /// days, so no grace period may be read off it.</para>
        ///
        /// <para>The fleet key is empty — an orphan has no fleet record to recover, and
        /// <c>NpcGridNaming.NameFor</c>'s colon-less form parses back to the entity id, which is all
        /// the reaper needs to see it.</para></summary>
        /// <returns>True when the name was written. False for every refusal, including a grid that
        /// already carries its own name.</returns>
        public static bool TryBackfillName(MyCubeGrid grid, DateTime nowUtc)
        {
            return TryBackfillName(grid, nowUtc, true);
        }

        /// <summary>As <see cref="TryBackfillName(MyCubeGrid,DateTime)"/>, with
        /// <paramref name="apply"/> false answering only whether it <i>would</i> write — the dry
        /// run, which must reach the same verdict by the same path rather than by a second copy of
        /// the rule.</summary>
        public static bool TryBackfillName(MyCubeGrid grid, DateTime nowUtc, bool apply)
        {
            if (grid == null || grid.MarkedForClose) return false;
            if (Registry.IsMarked(grid.EntityId)) return false;

            bool mine    = NpcGridNaming.IsSimBornName(grid.Name, grid.EntityId);
            bool foreign = !mine && NpcGridNaming.IsSimBornName(grid.Name);
            NpcGridNaming.BackfillVerdict verdict = NpcGridNaming.ShouldBackfillName(
                mine, foreign, grid.IsNpcSpawnedGrid, NpcGridNaming.IsTagged(grid.DisplayName));

            if (verdict != NpcGridNaming.BackfillVerdict.Write) return false;
            if (!apply) return true;

            bool ok = Mark(grid, "", "", "", 0L, nowUtc, NpcGridNaming.Apply(grid.DisplayName), true);
            if (ok && DebugTraces.GridMarks)
                Log.Info("SimGridMarks: grid " + grid.EntityId + " (" + grid.BlocksCount
                         + " blocks) backfilled -> " + grid.Name);
            return ok;
        }

        // ---- internals ---------------------------------------------------------------------

        private static void OnEntityAdd(MyEntity entity)
        {
            var grid = entity as MyCubeGrid;
            if (grid == null || Registry.Count == 0 || Registry.IsMarked(grid.EntityId)) return;
            _pending.Add(new Pending { Eid = grid.EntityId, DeadlineUtc = DateTime.UtcNow.AddSeconds(AdoptWindowSeconds) });
        }

        private static bool TryAdopt(MyCubeGrid grid, DateTime nowUtc)
        {
            SimGridRecord rec;

            // A part that linked to a marked grid after it was created: a rotor top, a piston head,
            // a wheel. It keeps its own name under the tag, like a prefab's subgrid does.
            CollectMechanicalGroup(grid, _groupScratch);
            for (int g = 0; g < _groupScratch.Count; g++)
            {
                MyCubeGrid peer = _groupScratch[g];
                if (peer == grid || !Registry.TryGet(peer.EntityId, out rec)) continue;
                _groupScratch.Clear();
                bool ok = Mark(grid, rec.FleetKey, rec.FactionTag, rec.Kind, peer.EntityId, nowUtc, NpcGridNaming.Apply(grid.DisplayName));
                if (ok && DebugTraces.GridMarks)
                    Log.Info("SimGridMarks: grid " + grid.EntityId + " (" + grid.BlocksCount + " blocks) attached to tracked "
                             + peer.EntityId + " fleet=" + rec.FleetKey + " -> " + grid.DisplayName);
                return ok;
            }
            _groupScratch.Clear();

            // Ordnance a hull released, or anything else it built: NPC-owned by the same identity as
            // a marked grid close by. A player identity never qualifies, whatever it owns nearby.
            long owner = PrimaryOwner(grid);
            if (owner == 0L || MySession.Static == null || MySession.Static.Players == null
                || !MySession.Static.Players.IdentityIsNpc(owner)) return false;
            if (grid.PositionComp == null) return false;
            Vector3D pos = grid.PositionComp.GetPosition();

            Registry.CopyEids(_eidScratch);
            for (int i = 0; i < _eidScratch.Count; i++)
            {
                MyCubeGrid parent;
                if (!MyEntities.TryGetEntityById(_eidScratch[i], out parent, false) || parent == null || parent.MarkedForClose) continue;
                if (PrimaryOwner(parent) != owner || parent.PositionComp == null) continue;
                if (Vector3D.Distance(pos, parent.PositionComp.GetPosition()) > AdoptRadiusM) continue;
                if (!Registry.TryGet(parent.EntityId, out rec)) continue;
                _eidScratch.Clear();
                bool ok = Mark(grid, rec.FleetKey, rec.FactionTag, rec.Kind, parent.EntityId, nowUtc, NpcGridNaming.DebrisName);
                if (ok && DebugTraces.GridMarks)
                    Log.Info("SimGridMarks: grid " + grid.EntityId + " (" + grid.BlocksCount + " blocks) born beside tracked "
                             + parent.EntityId + " fleet=" + rec.FleetKey + " -> " + NpcGridNaming.DebrisName);
                return ok;
            }
            _eidScratch.Clear();
            return false;
        }

        private static bool Mark(MyCubeGrid grid, string fleetKey, string factionTag, string kind, long parentEid, DateTime nowUtc, string name)
        {
            return Mark(grid, fleetKey, factionTag, kind, parentEid, nowUtc, name, false);
        }

        private static bool Mark(MyCubeGrid grid, string fleetKey, string factionTag, string kind, long parentEid, DateTime nowUtc, string name, bool restored)
        {
            if (grid == null || grid.MarkedForClose) return false;
            if (!Registry.Mark(grid.EntityId, fleetKey, factionTag, kind, parentEid, nowUtc, restored)) return false;

            // What splits off, attaches to or is released by an unsaved hull is not saved either: an
            // agent's hulls are kept out of the save (VanillaFleetSpawner.SetSaved).
            MyCubeGrid parent;
            if (parentEid != 0L && MyEntities.TryGetEntityById(parentEid, out parent, false) && parent != null
                && !parent.Save && grid.Save)
                grid.Save = false;

            if (!grid.IsNpcSpawnedGrid) grid.IsNpcSpawnedGrid = true;
            if (!string.Equals(grid.DisplayName, name, StringComparison.Ordinal)) grid.DisplayName = name;

            // The durable carrier. Keyed on the entity id so no two grids are ever handed the same
            // one -- MyEntities drops a second entity claiming a name already in its dictionary --
            // and so a blueprint of this grid, pasted by a player, does not read as ours.
            string entityName = NpcGridNaming.NameFor(grid.EntityId, fleetKey);
            if (!string.Equals(grid.Name, entityName, StringComparison.Ordinal)) grid.Name = entityName;

            grid.OnGridSplit += OnTrackedGridSplit;
            grid.OnClose     += OnTrackedGridClosed;
            return true;
        }

        private static void Unsubscribe(MyCubeGrid grid)
        {
            grid.OnGridSplit -= OnTrackedGridSplit;
            grid.OnClose     -= OnTrackedGridClosed;
        }

        private static void OnTrackedGridClosed(MyEntity entity)
        {
            var grid = entity as MyCubeGrid;
            if (grid != null) Unsubscribe(grid);
            Registry.Forget(entity.EntityId);
        }

        private static void OnTrackedGridSplit(MyCubeGrid parent, MyCubeGrid child)
        {
            SimGridRecord rec;
            if (parent == null || child == null || !Registry.TryGet(parent.EntityId, out rec)) return;
            bool ok = Mark(child, rec.FleetKey, rec.FactionTag, rec.Kind, parent.EntityId, DateTime.UtcNow, NpcGridNaming.DebrisName);
            if (ok && DebugTraces.GridMarks)
                Log.Info("SimGridMarks: grid " + child.EntityId + " (" + child.BlocksCount + " blocks) split from tracked "
                         + parent.EntityId + " fleet=" + rec.FleetKey + " -> " + NpcGridNaming.DebrisName);
        }

        /// <summary>Names a spawned fleet for what it is (docs/traffic-control.md, T10): the lead grid
        /// gets <paramref name="gridName"/> through SE's synced rename, and every antenna and beacon on
        /// every grid of the assembly broadcasts <paramref name="hudText"/>. A prefab's broadcast
        /// blocks carry its designer's text, which SE shows instead of the grid's name, so renaming
        /// the grid alone leaves the HUD saying what the prefab was called. Antennas stop prefixing
        /// the grid name, which would say it twice, and broadcast at <see cref="BroadcastRadiusM"/>.
        /// The setters are synced to clients and the values are saved with the grid, so a restart
        /// keeps them. Returns the blocks relabelled.</summary>
        /// <summary>The broadcast range a spawned fleet's antennas are set to. SE clamps it to the
        /// block's own maximum, so a small-grid antenna stays at its definition's cap. The scanner
        /// reads the live range (<see cref="ShipAntennas.BroadcastKm"/>), so this is also how far a
        /// player's scanner sees the hull by its broadcast.</summary>
        internal const float BroadcastRadiusM = 20000f;

        public static int LabelAssembly(IList<MyCubeGrid> spawned, MyCubeGrid lead, string gridName, string hudText)
        {
            if (lead != null && !string.IsNullOrEmpty(gridName))
            {
                if (!string.Equals(lead.DisplayName, gridName, StringComparison.Ordinal)) lead.DisplayName = gridName;
                try { lead.ChangeDisplayNameRequest(gridName); }
                catch (Exception ex) { Log.Warn(ex, "SimGridMarks: rename of grid " + lead.EntityId + " could not be sent to clients."); }
            }
            if (spawned == null || string.IsNullOrEmpty(hudText)) return 0;
            int labelled = 0;
            var seen = new HashSet<long>();
            for (int i = 0; i < spawned.Count; i++)
            {
                MyCubeGrid root = spawned[i];
                if (root == null) continue;
                CollectMechanicalGroup(root, _groupScratch);
                for (int g = 0; g < _groupScratch.Count; g++)
                {
                    MyCubeGrid member = _groupScratch[g];
                    if (member == null || !seen.Add(member.EntityId)) continue;
                    foreach (MyCubeBlock block in member.GetFatBlocks())
                    {
                        var antenna = block as Sandbox.Game.Entities.Cube.MyRadioAntenna;
                        if (antenna != null)
                        {
                            var ingame = (Sandbox.ModAPI.Ingame.IMyRadioAntenna)antenna;
                            ingame.HudText = hudText;
                            if (antenna.ShowShipName) antenna.ShowShipName = false;
                            if (ingame.Radius != BroadcastRadiusM) ingame.Radius = BroadcastRadiusM;
                            if (!antenna.EnableBroadcasting.Value) antenna.EnableBroadcasting.Value = true;
                            labelled++;
                            continue;
                        }
                        var beacon = block as Sandbox.Game.Entities.Cube.MyBeacon;
                        if (beacon != null)
                        {
                            ((Sandbox.ModAPI.Ingame.IMyBeacon)beacon).HudText = hudText;
                            labelled++;
                        }
                    }
                }
                _groupScratch.Clear();
            }
            return labelled;
        }

        /// <summary>The grid's mechanical group into <paramref name="into"/> (cleared first), the grid
        /// itself included — an unlinked grid is a group of one whichever way SE answers.</summary>
        private static void CollectMechanicalGroup(MyCubeGrid grid, List<MyCubeGrid> into)
        {
            into.Clear();
            if (MyCubeGridGroups.Static != null && MyCubeGridGroups.Static.Mechanical != null)
            {
                try { MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid, into); }
                catch (Exception ex) { Log.Warn(ex, "SimGridMarks: mechanical group lookup failed for grid " + grid.EntityId + "."); into.Clear(); }
            }
            if (!into.Contains(grid)) into.Add(grid);
        }

        private static long PrimaryOwner(MyCubeGrid grid)
        {
            List<long> owners = grid.BigOwners;
            if (owners == null || owners.Count == 0) return 0L;
            if (owners[0] != 0L) return owners[0];
            return owners.Count > 1 ? owners[1] : 0L;
        }
    }
}
