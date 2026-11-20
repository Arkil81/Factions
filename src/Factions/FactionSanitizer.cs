using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.ModAPI;

namespace Factions
{
    /// <summary>
    /// SE adapter for the one-shot registry cleanup behind <c>factions sanitize</c>: reduce a world's
    /// SE faction list to the economy factions that actually hold stations, deleting the grids the
    /// removed factions own. Read-only until <see cref="Apply"/> runs.
    ///
    /// A faction is kept for one of three reasons:
    /// <list type="bullet">
    /// <item>it holds at least one linked station — the economy factions this world is being
    /// reduced to;</item>
    /// <item>its tag is an engine-default (<see cref="MyDefinitionManager.GetDefaultFactions"/>):
    /// SE's <c>CreateDefaultFactions</c> runs on every session load and recreates a missing default
    /// by tag, so removing one only orphans its assets and yields a new faction with a new id;</item>
    /// <item>its tag is on the ignore-list (<c>FactionsConfig.IgnoredTags</c>). That list means the
    /// faction is excluded from discovery and the sim, not that it should leave the world — the
    /// animal factions it exists for (spiders' SPID) are world content Factions simply does not
    /// drive. <c>factions ignore add &lt;tag&gt;</c> therefore also protects a tag from this sweep.</item>
    /// </list>
    ///
    /// Member identities are deliberately left in place. Removing a faction does not remove them,
    /// and an identity with no faction and no grids is inert; identity cleanup is a separate
    /// operation with its own blast radius.
    ///
    /// MUST run on the game thread (it enumerates entities and mutates the faction collection).
    /// </summary>
    internal static class FactionSanitizer
    {
        internal sealed class FactionPlan
        {
            public string Tag;
            public long FactionId;
            public string FactionType;
            public int Members;
            public int Stations;
            public readonly List<long> GridsToDelete = new List<long>();
            public int BlocksToDelete;
            public readonly List<string> GridNames = new List<string>();
        }

        internal sealed class Plan
        {
            public readonly List<FactionPlan> Remove = new List<FactionPlan>();
            public readonly List<string> KeptWithStations = new List<string>();
            public readonly List<string> KeptEngineDefault = new List<string>();
            public readonly List<string> KeptIgnored = new List<string>();
            /// <summary>Grids whose blocks are owned partly by a removed faction and partly by
            /// something surviving. Never deleted — reported so a shared grid is a decision, not a
            /// silent casualty.</summary>
            public readonly List<string> MixedOwnershipGrids = new List<string>();
            public int TotalGrids;
            public int TotalBlocks;
        }

        /// <summary>Build the keep/remove plan without touching anything.
        /// <paramref name="ignoredTags"/> is the host's ignore-list; those factions are protected.</summary>
        internal static Plan Build(IEnumerable<string> ignoredTags)
        {
            var plan = new Plan();
            if (MySession.Static == null || MySession.Static.Factions == null) return plan;

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ignoredTags != null)
                foreach (string t in ignoredTags)
                    if (!string.IsNullOrEmpty(t)) ignored.Add(t);

            var engineDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<MyFactionDefinition> defaults = MyDefinitionManager.Static.GetDefaultFactions();
            if (defaults != null)
                foreach (MyFactionDefinition d in defaults)
                    if (d != null && !string.IsNullOrEmpty(d.Tag)) engineDefault.Add(d.Tag);

            // identity -> faction being removed, so a grid can be attributed in one pass
            var doomedIdentity = new Dictionary<long, FactionPlan>();

            foreach (KeyValuePair<long, MyFaction> kv in MySession.Static.Factions)
            {
                MyFaction f = kv.Value;
                if (f == null) continue;

                if (f.Stations.Count > 0) { plan.KeptWithStations.Add(f.Tag); continue; }
                if (engineDefault.Contains(f.Tag)) { plan.KeptEngineDefault.Add(f.Tag); continue; }
                if (ignored.Contains(f.Tag)) { plan.KeptIgnored.Add(f.Tag); continue; }

                var fp = new FactionPlan
                {
                    Tag = f.Tag,
                    FactionId = f.FactionId,
                    FactionType = f.FactionTypeString ?? "None",
                    Stations = 0
                };
                foreach (var member in f.Members)
                {
                    fp.Members++;
                    doomedIdentity[member.Key] = fp;
                }
                plan.Remove.Add(fp);
            }

            if (doomedIdentity.Count > 0) AttributeGrids(plan, doomedIdentity);

            plan.Remove.Sort((a, b) => string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase));
            plan.KeptWithStations.Sort(StringComparer.OrdinalIgnoreCase);
            plan.KeptEngineDefault.Sort(StringComparer.OrdinalIgnoreCase);
            plan.KeptIgnored.Sort(StringComparer.OrdinalIgnoreCase);
            return plan;
        }

        /// <summary>One world pass: a grid is queued for deletion only when every owned block on it
        /// belongs to a faction being removed.</summary>
        private static void AttributeGrids(Plan plan, Dictionary<long, FactionPlan> doomedIdentity)
        {
            MyAPIGateway.Entities.GetEntities(null, e =>
            {
                var grid = e as MyCubeGrid;
                if (grid == null || grid.Projector != null) return false;

                FactionPlan owner = null;
                bool mixed = false;
                int ownedBlocks = 0;
                foreach (var block in grid.GetBlocks())
                {
                    long ownerId = block.OwnerId;
                    if (ownerId == 0L) continue;
                    ownedBlocks++;
                    FactionPlan fp;
                    if (!doomedIdentity.TryGetValue(ownerId, out fp)) { mixed = true; break; }
                    if (owner == null) owner = fp;
                }

                if (owner == null || ownedBlocks == 0) return false;
                if (mixed)
                {
                    plan.MixedOwnershipGrids.Add(grid.DisplayName + " (" + grid.EntityId + ")");
                    return false;
                }

                owner.GridsToDelete.Add(grid.EntityId);
                owner.BlocksToDelete += grid.BlocksCount;
                if (owner.GridNames.Count < 8) owner.GridNames.Add(grid.DisplayName);
                plan.TotalGrids++;
                plan.TotalBlocks += grid.BlocksCount;
                return false;
            });
        }

        /// <summary>Execute a plan: delete the attributed grids, then remove each faction through
        /// SE's own state-change path (which also clears relations, requests, player reputations,
        /// visibility and the faction's banking account).</summary>
        internal static void Apply(Plan plan, out int gridsClosed, out int factionsRemoved)
        {
            gridsClosed = 0;
            factionsRemoved = 0;
            if (plan == null) return;

            MySessionComponentEconomy economy = MySession.Static.GetComponent<MySessionComponentEconomy>();

            foreach (FactionPlan fp in plan.Remove)
            {
                foreach (long entityId in fp.GridsToDelete)
                {
                    MyCubeGrid grid;
                    if (!MyEntities.TryGetEntityById(entityId, out grid, false) || grid == null) continue;
                    grid.Close();
                    if (economy != null) economy.RemoveStationGrid(entityId);
                    gridsClosed++;
                }
                MyFactionCollection.RemoveFaction(fp.FactionId);
            }

            // Count what actually went, rather than what was asked for: RemoveFaction routes through
            // a server event, so an unapplied change would otherwise be reported as a success.
            foreach (FactionPlan fp in plan.Remove)
                if (MySession.Static.Factions.TryGetFactionById(fp.FactionId) == null) factionsRemoved++;
        }

        internal static string Describe(Plan plan, bool applied, int gridsClosed, int factionsRemoved)
        {
            var sb = new StringBuilder();
            sb.Append(applied ? "sanitize applied: " : "sanitize (dry run): ");
            sb.Append("keep ").Append(plan.KeptWithStations.Count).Append(" with stations, ")
              .Append(plan.KeptEngineDefault.Count).Append(" engine-default, ")
              .Append(plan.KeptIgnored.Count).Append(" ignored; remove ")
              .Append(plan.Remove.Count).Append(" faction(s) and ")
              .Append(plan.TotalGrids).Append(" grid(s)/").Append(plan.TotalBlocks).Append(" block(s).");

            if (applied)
                sb.Append("\n  removed ").Append(factionsRemoved).Append('/').Append(plan.Remove.Count)
                  .Append(" faction(s), closed ").Append(gridsClosed).Append(" grid(s).");

            if (plan.KeptEngineDefault.Count > 0)
                sb.Append("\n  kept (engine-default, recreated on load): ")
                  .Append(string.Join(", ", plan.KeptEngineDefault));

            if (plan.KeptIgnored.Count > 0)
                sb.Append("\n  kept (on the ignore-list, world content the sim does not drive): ")
                  .Append(string.Join(", ", plan.KeptIgnored));

            if (plan.MixedOwnershipGrids.Count > 0)
            {
                sb.Append("\n  SKIPPED ").Append(plan.MixedOwnershipGrids.Count)
                  .Append(" grid(s) owned partly by a surviving faction:");
                int shown = 0;
                foreach (string g in plan.MixedOwnershipGrids)
                {
                    if (shown++ == 10) { sb.Append("\n    ... and ").Append(plan.MixedOwnershipGrids.Count - 10).Append(" more"); break; }
                    sb.Append("\n    ").Append(g);
                }
            }

            foreach (FactionPlan fp in plan.Remove)
            {
                sb.Append("\n  - ").Append(fp.Tag).Append(" [").Append(fp.FactionType).Append("] members=")
                  .Append(fp.Members).Append(" grids=").Append(fp.GridsToDelete.Count)
                  .Append(" blocks=").Append(fp.BlocksToDelete);
                if (fp.GridNames.Count > 0)
                    sb.Append("  (").Append(string.Join("; ", fp.GridNames))
                      .Append(fp.GridsToDelete.Count > fp.GridNames.Count ? "; ..." : "").Append(')');
            }

            if (!applied)
                sb.Append("\n  Nothing changed. Run 'factions sanitize apply' to execute, then 'save'.");
            return sb.ToString();
        }
    }
}
