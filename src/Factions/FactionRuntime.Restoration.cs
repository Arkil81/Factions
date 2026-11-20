using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Restoration: what a live grid is restored against, and the read-only plan against it
    /// (<see cref="RestorationDiff"/>). Nothing here changes a grid. See docs/ship-restoration.md.
    ///
    /// <para><b>Where the reference comes from</b>, first found wins:
    /// an explicit prefab (the bench's <c>prefab=</c>); the grid's registered design; the prefab a
    /// Factions spawn produced it from (this process's spawner record, else its mark's fleet key);
    /// the prefab a Factions store sold it as; and, when nothing marks it, a pattern match against
    /// the prefab catalogue, the captures and the registered designs
    /// (FactionRuntime.RestorationMatch.cs). A registered design outranks the grid's origin
    /// because it is the owner's own snapshot of the ship as they want it back, additions
    /// included.</para>
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>Below this share of the live grid matched, the transform is not trusted.</summary>
        private const double RestoreFitWarnShare = 0.5;

        private const int RestoreBillLines = 8;
        private const int RestoreMissingLines = 6;

        private sealed class RestorationReference
        {
            /// <summary><c>prefab:&lt;id&gt;</c> or <c>design:&lt;entity id&gt;</c>.</summary>
            public string Source;
            /// <summary>How the reference was found: explicit, registered, spawn, fleet or store.</summary>
            public string Via;
            public string Fingerprint;
            public MyObjectBuilder_CubeGrid[] Grids;
        }

        private sealed class RestorationFitResult
        {
            public List<RestorationBlock> Reference;
            /// <summary>Each reference block's own object builder, at the same index.</summary>
            public List<MyObjectBuilder_CubeBlock> Sources;
            public int GridIndex;
            public int Unknown;
            public RestorationFit Fit;
        }

        /// <summary><c>factions bench restore-plan &lt;gridEntityId|looking&gt; [prefab=&lt;id&gt;]</c>. A grid
        /// nothing marks, with no <c>prefab=</c>, is pattern matched against the catalogue over the
        /// next frames (<see cref="StartRestoreMatch"/>); its plan arrives in the bridge outbox.</summary>
        internal string BenchRestorePlan(string target, string prefabArg)
        {
            MyCubeGrid grid;
            long identityId;
            string error;
            if (!TryFindRestoreTarget(target, out grid, out identityId, out error)) return "restore-plan: " + error;

            RestorationReference reference;
            bool unmarked;
            if (!TryResolveRestorationReference(grid, prefabArg, out reference, out unmarked, out error))
            {
                if (unmarked) return StartRestoreMatch(grid);
                return "restore-plan: grid " + grid.EntityId + " '" + grid.DisplayName + "': " + error;
            }

            List<RestorationBlock> current = RestorationGrids.FromGrid(grid);
            RestorationFitResult best = BestReferenceGrid(reference, current);
            if (best == null) return "restore-plan: the reference " + reference.Source + " holds no blocks this world knows.";
            return RenderRestorePlan(grid.EntityId, grid.DisplayName, reference, reference.Grids.Length, best, current, null);
        }

        /// <summary>The plan of <paramref name="current"/> against <paramref name="best"/>: logs
        /// <c>RESTORE_PLAN</c> and returns the bench response. <paramref name="matchFields"/> is
        /// appended to the log line (a match's score and runner-up), and null otherwise.</summary>
        private static string RenderRestorePlan(long gridId, string gridName, RestorationReference reference, int referenceGrids,
                                                RestorationFitResult best, List<RestorationBlock> current, string matchFields)
        {
            RestorationPlan plan = RestorationDiff.Diff(best.Reference, current, best.Fit.Transform);
            long billTotal = 0;
            foreach (KeyValuePair<string, long> kv in plan.Bill) billTotal += kv.Value;

            FactionsPlugin.Log.Info("RESTORE_PLAN grid=" + gridId
                + " source=" + reference.Source + " found=" + reference.Via
                + " refGrid=" + best.GridIndex + "/" + referenceGrids
                + " blocks=" + plan.ReferenceBlocks + "/" + plan.CurrentBlocks
                + " matched=" + plan.Matched + " via=" + best.Fit.Via
                + " intact=" + plan.Intact + " damaged=" + plan.Damaged.Count
                + " deformedOnly=" + plan.DeformedOnly + " missing=" + plan.Missing.Count
                + " blocked=" + plan.Blocked.Count + " extra=" + plan.Extra.Count
                + " unknown=" + best.Unknown + " components=" + billTotal
                + (matchFields ?? ""));

            var sb = new StringBuilder();
            sb.Append("restore-plan: grid ").Append(gridId).Append(" '").Append(gridName).Append("'\n");
            sb.Append("  reference ").Append(reference.Source).Append(" (").Append(reference.Via)
              .Append(reference.Fingerprint != null ? ", fingerprint " + reference.Fingerprint : "")
              .Append(matchFields != null ? "," + matchFields : "").Append("), grid ").Append(best.GridIndex + 1)
              .Append(" of ").Append(referenceGrids).Append(" (").Append(plan.ReferenceBlocks).Append(" blocks");
            if (best.Unknown > 0) sb.Append(", ").Append(best.Unknown).Append(" of unknown definition skipped");
            sb.Append(")\n");
            sb.Append("  transform ").Append(best.Fit.Transform).Append(" via ").Append(best.Fit.Via)
              .Append(" (").Append(best.Fit.Candidates).Append(" tried), matched ").Append(plan.Matched)
              .Append(" of ").Append(plan.CurrentBlocks).Append(" live blocks\n");
            if (plan.MatchedShareOfCurrent < RestoreFitWarnShare)
                sb.Append("  WARNING: under half the live grid matches this reference; the transform or the reference is wrong.\n");
            sb.Append("  intact ").Append(plan.Intact)
              .Append(", damaged ").Append(plan.Damaged.Count).Append(" (deformed only ").Append(plan.DeformedOnly).Append(")")
              .Append(", missing ").Append(plan.Missing.Count)
              .Append(", blocked ").Append(plan.Blocked.Count)
              .Append(", extra ").Append(plan.Extra.Count).Append("\n");

            List<KeyValuePair<string, long>> lines = plan.BillLines();
            sb.Append("  bill ").Append(billTotal).Append(" components");
            for (int i = 0; i < lines.Count && i < RestoreBillLines; i++)
                sb.Append(i == 0 ? ": " : ", ").Append(lines[i].Key).Append(" ").Append(lines[i].Value);
            if (lines.Count > RestoreBillLines) sb.Append(", +").Append(lines.Count - RestoreBillLines).Append(" more");
            sb.Append("\n");
            AppendEntries(sb, "missing", plan.Missing);
            AppendEntries(sb, "blocked", plan.Blocked);
            return sb.ToString().TrimEnd('\n');
        }

        private static void AppendEntries(StringBuilder sb, string label, List<RestorationEntry> entries)
        {
            if (entries.Count == 0) return;
            sb.Append("  ").Append(label).Append(":");
            for (int i = 0; i < entries.Count && i < RestoreMissingLines; i++)
                sb.Append(i == 0 ? " " : ", ").Append(ShortDef(entries[i].Placement.DefinitionId)).Append("@").Append(entries[i].Placement.Min);
            if (entries.Count > RestoreMissingLines) sb.Append(", +").Append(entries.Count - RestoreMissingLines).Append(" more");
            sb.Append("\n");
        }

        private static string ShortDef(string definitionId)
        {
            if (string.IsNullOrEmpty(definitionId)) return "?";
            int slash = definitionId.IndexOf('/');
            string type = slash > 0 ? definitionId.Substring(0, slash) : definitionId;
            string sub = slash >= 0 ? definitionId.Substring(slash + 1) : "";
            if (type.StartsWith("MyObjectBuilder_", StringComparison.Ordinal)) type = type.Substring("MyObjectBuilder_".Length);
            return sub.Length > 0 ? sub : type;
        }

        /// <summary><c>factions bench register-design &lt;gridEntityId|looking&gt; [force=1]</c>: snapshots
        /// the grid as its own design. Refused for a grid with any block below full integrity, and
        /// for one missing blocks against the reference it already has — <c>force=1</c> skips only
        /// the second rule, so a bench can re-register a grid it deliberately cut down.</summary>
        internal string BenchRegisterDesign(string target, bool force)
        {
            MyCubeGrid grid;
            long identityId;
            string error;
            if (!TryFindRestoreTarget(target, out grid, out identityId, out error)) return "register-design: " + error;

            RestorationLiveGroup live;
            if (!CheckRegistrable(grid, force, out live, out error)) return "register-design: " + error;
            string fingerprint;
            bool persisted = CommitRegistration(live, identityId, 0, out fingerprint);
            return "register-design: grid " + grid.EntityId + " '" + grid.DisplayName + "' registered, "
                 + live.BlockCount + " blocks in " + live.Grids.Count + " grid(s), fingerprint " + fingerprint
                 + (persisted ? "." : " — NOT written to disk; it lasts until restart.");
        }

        /// <summary>Whether <paramref name="grid"/> may be registered as its own design: a ship, every
        /// block of its mechanical group at full integrity, and — unless <paramref name="force"/> —
        /// nothing missing against the reference it already has. <paramref name="live"/> is the group
        /// as it stands, <paramref name="grid"/> first.</summary>
        internal bool CheckRegistrable(MyCubeGrid grid, bool force, out RestorationLiveGroup live, out string refusal)
        {
            live = null;
            refusal = null;
            if (_customPrefabStore == null) { refusal = "no capture store is loaded."; return false; }
            if (grid.IsStatic) { refusal = "grid " + grid.EntityId + " is a station; designs are for ships."; return false; }

            live = RestorationGrids.LiveGroup(grid);
            int below = 0;
            RestorationBlock first = null;
            foreach (RestorationBlock b in live.AllBlocks())
            {
                if (!b.BelowFullIntegrity) continue;
                below++;
                if (first == null) first = b;
            }
            if (below > 0)
            {
                refusal = "refused, " + below + " block(s) below full integrity (first: "
                        + ShortDef(first.DefinitionId) + "@" + first.Min + "). Repair the ship first.";
                return false;
            }

            RestorationReference existing;
            string ignored;
            bool unmarked;
            if (!force && TryResolveRestorationReference(grid, null, out existing, out unmarked, out ignored))
            {
                RestorationFitResult fit = BestReferenceGrid(existing, live.Root);
                if (fit != null)
                {
                    RestorationDesign design = RestorationGrids.Design(existing.Grids);
                    RestorationGroupPlan plan = RestorationGroup.Plan(design.Assembly, fit.GridIndex, live.Assembly, 0, fit.Fit.Transform);
                    int missing = plan.Sequence.Places + plan.Sequence.Attaches + plan.Sequence.Unreachable;
                    if (missing > 0)
                    {
                        refusal = "refused, " + missing + " block(s) missing against its reference "
                                + existing.Source + ". Restore it first.";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Snapshots <paramref name="live"/> — the grid and its subgrids — as its design and
        /// logs <c>DESIGN_REGISTERED</c> with the <paramref name="fee"/> charged for it. False when
        /// the design could not be written to disk; it is then usable until restart.</summary>
        internal bool CommitRegistration(RestorationLiveGroup live, long identityId, long fee, out string fingerprint)
        {
            MyCubeGrid grid = live.Grids[0];
            var builders = new MyObjectBuilder_CubeGrid[live.Grids.Count];
            for (int i = 0; i < live.Grids.Count; i++) builders[i] = (MyObjectBuilder_CubeGrid)live.Grids[i].GetObjectBuilder(true);
            fingerprint = RestorationDiff.Fingerprint(live.Assembly.Grids);
            bool persisted = _customPrefabStore.RegisterDesign(grid.EntityId, fingerprint, builders, grid.DisplayName,
                                                               identityId, DateTime.UtcNow);

            FactionsPlugin.Log.Info("DESIGN_REGISTERED grid=" + grid.EntityId + " blocks=" + live.BlockCount
                                  + " grids=" + live.Grids.Count
                                  + " fingerprint=" + fingerprint + " by=" + identityId + " fee=" + fee
                                  + (persisted ? "" : " persisted=false"));
            return persisted;
        }

        /// <summary>The fingerprint of <paramref name="grid"/>'s registered design; null when it has none.</summary>
        internal string RegisteredFingerprint(long gridEntityId)
        {
            CustomPrefabStore.DesignRecord record;
            MyObjectBuilder_CubeGrid[] ignored;
            return _customPrefabStore != null && _customPrefabStore.TryGetDesign(gridEntityId, out record, out ignored)
                ? record.Fingerprint : null;
        }

        /// <summary><paramref name="target"/> is an entity id, or <c>looking</c> for the grid the first
        /// online player's view ray hits (the capture command's raycast and range) — the main grid of
        /// its mechanical group, as vanilla's Repair tab lists it.</summary>
        internal bool TryFindRestoreTarget(string target, out MyCubeGrid grid, out long identityId, out string error)
        {
            grid = null;
            identityId = 0;
            error = null;
            if (string.IsNullOrEmpty(target)) { error = "name a grid entity id or 'looking'."; return false; }

            if (string.Equals(target, "looking", StringComparison.OrdinalIgnoreCase))
            {
                MyPlayer player = null;
                if (MySession.Static != null && MySession.Static.Players != null)
                    foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
                        if (p != null && !p.IsBot && p.Character != null) { player = p; break; }
                if (player == null) { error = "no online player to look from."; return false; }
                identityId = player.Identity != null ? player.Identity.IdentityId : 0;
                if (!ShipCaptureScanner.TryFindTargetedGrid(player, _tuning.ShipCaptureMaxRangeM, out grid, out error)) return false;
                // A look can land on a turret or a rotor head; the ship is its mechanical group's main grid.
                grid = RestorationGrids.MainOf(grid);
                return true;
            }

            long eid;
            if (!long.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out eid))
            {
                error = "'" + target + "' is neither a grid entity id nor 'looking'.";
                return false;
            }
            if (!MyEntities.TryGetEntityById(eid, out grid, false) || grid == null || grid.MarkedForClose)
            {
                grid = null;
                error = "no grid with entity id " + eid + ".";
                return false;
            }
            return true;
        }

        /// <summary>The grid's marked reference, first found wins. <paramref name="unmarked"/> when
        /// nothing names one — the case pattern matching is for — as opposed to a named one that
        /// cannot be read.</summary>
        private bool TryResolveRestorationReference(MyCubeGrid grid, string explicitPrefab, out RestorationReference reference,
                                                    out bool unmarked, out string error)
        {
            reference = null;
            unmarked = false;
            error = null;
            MyObjectBuilder_CubeGrid[] grids;

            if (!string.IsNullOrEmpty(explicitPrefab))
            {
                string resolved;
                if (!TryRestorationPrefabGrids(explicitPrefab, out resolved, out grids, out error)) return false;
                reference = new RestorationReference { Source = "prefab:" + resolved, Via = "explicit", Grids = grids };
                return true;
            }

            long eid = grid.EntityId;
            CustomPrefabStore.DesignRecord design;
            MyObjectBuilder_CubeGrid[] designGrids;
            if (_customPrefabStore != null && _customPrefabStore.TryGetDesign(eid, out design, out designGrids))
            {
                reference = new RestorationReference
                {
                    Source = "design:" + eid,
                    Via = "registered",
                    Fingerprint = design.Fingerprint,
                    Grids = designGrids,
                };
                return true;
            }

            string via;
            string prefabId = SpawnedPrefabOf(eid, out via);
            if (prefabId == null && _customPrefabStore != null)
            {
                CustomPrefabStore.SoldShipRecord sold;
                if (_customPrefabStore.TryGetSoldShip(eid, out sold)) { prefabId = sold.PrefabId; via = "store"; }
            }
            if (prefabId != null)
            {
                string resolved;
                if (!TryRestorationPrefabGrids(prefabId, out resolved, out grids, out error)) return false;
                reference = new RestorationReference { Source = "prefab:" + resolved, Via = via, Grids = grids };
                return true;
            }

            unmarked = true;
            error = "no reference: not a Factions spawn, not sold by a Factions store, and no registered design."
                  + " Name one with prefab=<id>, or register the ship while it is whole.";
            return false;
        }

        /// <summary>The prefab a Factions spawn produced <paramref name="eid"/> from: this process's
        /// spawner record, else the grid's mark, whose fleet key names the fleet and — for a squadron
        /// hull — its slot (<c>key#i</c>). Null for a grid this plugin did not spawn.</summary>
        private string SpawnedPrefabOf(long eid, out string via)
        {
            via = null;
            var vanilla = _spawner as VanillaFleetSpawner;
            string prefab = vanilla != null ? vanilla.GridPrefabId(eid) : null;
            if (!string.IsNullOrEmpty(prefab)) { via = "spawn"; return prefab; }

            SimGridRecord record;
            if (!SimGridMarks.TryGet(eid, out record) || record == null || string.IsNullOrEmpty(record.FleetKey)) return null;
            string recordKey = record.FleetKey;
            foreach (FactionState st in _states)
                foreach (PendingFleetAction fleet in st.PendingFleets)
                {
                    string key = FleetKey(fleet);
                    if (!FleetKeyCovers(key, recordKey)) continue;
                    via = "fleet";
                    int hash = recordKey.Length > key.Length ? key.Length : -1;
                    int slot;
                    if (hash >= 0 && int.TryParse(recordKey.Substring(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out slot))
                    {
                        IReadOnlyList<string> hulls = SpawnHulls(fleet);
                        if (hulls != null && slot < hulls.Count && !string.IsNullOrEmpty(hulls[slot])) return hulls[slot];
                    }
                    return string.IsNullOrEmpty(fleet.ShipPrefabId) ? null : fleet.ShipPrefabId;
                }
            return null;
        }

        /// <summary>A prefab's grids by id — a player capture, else an SE prefab definition, matched
        /// exactly and then ignoring case. An unknown id answers with the ids that contain it.</summary>
        private bool TryRestorationPrefabGrids(string prefabId, out string resolved, out MyObjectBuilder_CubeGrid[] grids, out string error)
        {
            resolved = prefabId;
            error = null;
            if (_customPrefabStore != null && _customPrefabStore.TryGetGrids(prefabId, out grids) && grids != null && grids.Length > 0)
                return true;

            grids = null;
            if (MyDefinitionManager.Static == null) { error = "no definitions loaded."; return false; }
            MyPrefabDefinition def = null;
            try { def = MyDefinitionManager.Static.GetPrefabDefinition(prefabId); }
            catch { def = null; }

            var near = new List<string>();
            if (def == null)
            {
                foreach (KeyValuePair<string, MyPrefabDefinition> kv in MyDefinitionManager.Static.GetPrefabDefinitions())
                {
                    if (string.Equals(kv.Key, prefabId, StringComparison.OrdinalIgnoreCase)) { def = kv.Value; resolved = kv.Key; break; }
                    if (kv.Key.IndexOf(prefabId, StringComparison.OrdinalIgnoreCase) >= 0) near.Add(kv.Key);
                }
            }
            if (def == null || def.CubeGrids == null || def.CubeGrids.Length == 0)
            {
                near.Sort(StringComparer.Ordinal);
                error = "no prefab '" + prefabId + "'"
                      + (near.Count > 0 ? "; ids containing it: " + string.Join(", ", near.GetRange(0, Math.Min(near.Count, 10))) : "") + ".";
                return false;
            }
            grids = def.CubeGrids;
            return true;
        }

        /// <summary>The reference grid, of the several a prefab can carry, whose best transform
        /// matches the most live blocks; the earliest on a tie. Null when none holds a known block.</summary>
        private static RestorationFitResult BestReferenceGrid(RestorationReference reference, List<RestorationBlock> current)
        {
            RestorationFitResult best = null;
            for (int i = 0; i < reference.Grids.Length; i++)
            {
                int unknown;
                var sources = new List<MyObjectBuilder_CubeBlock>();
                List<RestorationBlock> blocks = RestorationGrids.FromBuilder(reference.Grids[i], out unknown, sources);
                if (blocks.Count == 0) continue;
                RestorationFit fit = RestorationDiff.FindTransform(blocks, current);
                if (best == null || fit.Matched > best.Fit.Matched)
                    best = new RestorationFitResult { Reference = blocks, Sources = sources, GridIndex = i, Unknown = unknown, Fit = fit };
            }
            return best;
        }
    }
}
