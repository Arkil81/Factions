using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>Seam for manager types that run at 10× the brain tick rate and handle
    /// logistics decisions the brain is too coarse-grained to make (prefab selection,
    /// component allocation, demand routing).</summary>
    public interface IFactionManager
    {
        void Tick(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log);
    }

    /// <summary>Result of a player-triggered build-queue mutation (see
    /// <see cref="ShipyardManager.CancelBuild"/>/<see cref="ShipyardManager.QueueBuild"/>) — mirrors
    /// the <see cref="DevelopStationResult"/> pattern so the host chat command maps to text without
    /// string-sniffing.</summary>
    public enum PlayerQueueResult
    {
        Ok, NodeNotFound, NotOwned, NoShipyard, UnknownRole, UnknownPrefab, EntryNotFound,
        /// <summary>The prefab is over the server's performance ceiling (FAC-385).</summary>
        OverPerformanceCeiling,
        /// <summary>The prefab's design is on the server's ban list (FAC-451).</summary>
        Banned,
    }

    /// <summary>
    /// Fulfils "Ship:{role}" demands by maintaining a <see cref="BuildQueueEntry"/> per node per role.
    ///
    /// <para><b>Build queue lifecycle:</b>
    /// <list type="number">
    ///   <item>A "Ship:Trade" demand appears (posted by Enact when ShipStock is empty).</item>
    ///   <item>ShipyardManager creates a <see cref="BuildQueueEntry"/>, picks the cheapest prefab,
    ///     and records its BOM.</item>
    ///   <item>Each tick it absorbs available components from <see cref="SpatialNode.Inventory"/>
    ///     into <see cref="BuildQueueEntry.Collected"/>. Absorbed items leave the node's routable
    ///     stock — OperationsManager sees an honest empty inventory and routes more supply without
    ///     any floor/reservation arithmetic.</item>
    ///   <item>When Collected ≥ BOM for all items the ship is built: ShipStock incremented, Ship
    ///     demand consumed, queue entry removed.</item>
    ///   <item>If the Ship demand disappears before the build completes (brain changed its mind, or
    ///     the entry expired), Collected is returned to node inventory and the entry is removed.</item>
    /// </list></para>
    ///
    /// <para>When components are insufficient, posts "Item:{SubTypeId}" demands with Quantity = remaining
    /// shortfall (not BOM total, since Collected already holds part of it) so OperationsManager routes
    /// supply from other faction nodes.</para>
    ///
    /// <para><b>One yard, one build at a time.</b> A node may hold an entry per role, but only the
    /// first <see cref="SpatialNode.BuildSlotCount"/> of <see cref="SpatialNode.OrderedBuildQueue"/> are
    /// worked; the rest wait (<see cref="SpatialNode.IsActiveBuild"/>). Steps 3-5 above, the
    /// <c>Item:</c> demands, ProductionManager's recipe-input reservation and
    /// <see cref="SpatialNode.BuildQueueReserved"/> all apply to the working slot only, so a yard's
    /// component intake converges on one BOM instead of being divided across every role that happens
    /// to have a demand. Waiting is not stalling: a queued entry's expiry and
    /// <see cref="BuildQueueEntry.StalledSeconds"/> clocks are rolled forward while it waits, so it
    /// is never abandoned for time it was never given. Order is priority-then-FIFO, so with the
    /// default rank the next build is simply the oldest one queued.</para>
    /// </summary>
    public sealed partial class ShipyardManager : IFactionManager
    {
        // Derived from this manager's own cadence -- see FactionTuning.PostingTtlFor.
        private static TimeSpan DemandTtlFor(FactionTuning t)
        {
            return FactionTuning.PostingTtlFor(t == null ? 0.0 : t.ShipyardIntervalSeconds, 60.0);
        }

        private static TimeSpan InvestmentTtlFor(FactionTuning t)
        {
            return FactionTuning.PostingTtlFor(t == null ? 0.0 : t.ShipyardIntervalSeconds, 120.0);
        }

        /// <summary>Largest per-sweep increment <see cref="BuildQueueEntry.StalledSeconds"/> may take:
        /// two Shipyard cadences, so one missed sweep still counts roughly its own worth while an
        /// overnight shutdown (or a starved actor) contributes that same bounded amount instead of
        /// its true wall-clock length.</summary>
        private static double MaxStallDeltaSeconds(FactionTuning t)
        {
            double interval = t == null || t.ShipyardIntervalSeconds <= 0 ? 60.0 : t.ShipyardIntervalSeconds;
            return interval * 2.0;
        }

        /// <summary>Serviced-seconds without progress after which an entry is abandoned, or
        /// <see cref="double.PositiveInfinity"/> when the operator has disabled stall abandonment.</summary>
        private static double StallLimitSeconds(FactionTuning t)
        {
            double hours = t == null ? 0.0 : t.BuildNoProgressHours;
            return hours > 0.0 ? hours * 3600.0 : double.PositiveInfinity;
        }

        /// <summary>"4 (Trade x3, Mine)" -- how many builds hold the slots <paramref name="entry"/>
        /// wants at this yard, and what they are. The count alone says a queue is deep; it does not
        /// say the depth is one role, which is the thing an operator is actually looking at when a
        /// combat order never starts.</summary>
        private static string DescribeAhead(SpatialNode node, BuildQueueEntry entry)
        {
            var roles = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (BuildQueueEntry e in node.OrderedBuildQueue())
            {
                if (ReferenceEquals(e, entry)) break;
                if (e.Bom.Count == 0) continue;
                string role = e.DestinedForSale ? (e.Role ?? "?") + "/sale"
                    : IsNamedByHull(e) ? (e.PrefabId ?? "?") : e.Role;
                int have;
                if (!counts.TryGetValue(role, out have)) roles.Add(role);
                counts[role] = have + 1;
            }
            if (roles.Count == 0) return "0";
            var sb = new StringBuilder();
            int total = 0;
            for (int i = 0; i < roles.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(roles[i]);
                int n = counts[roles[i]];
                total += n;
                if (n > 1) sb.Append(" x").Append(n.ToString(CultureInfo.InvariantCulture));
            }
            return total.ToString(CultureInfo.InvariantCulture) + " (" + sb + ")";
        }

        /// <summary>Compact "3h12m"/"45s" rendering of a serviced-seconds total, for the abandonment
        /// log line -- so an operator reads "stalled=6h02m" rather than a raw second count.</summary>
        private static string FormatHours(double seconds)
        {
            if (seconds < 60.0) return ((int)seconds).ToString() + "s";
            if (seconds < 3600.0) return ((int)(seconds / 60.0)).ToString() + "m";
            int h = (int)(seconds / 3600.0);
            int m = (int)((seconds - h * 3600.0) / 60.0);
            return h.ToString() + "h" + m.ToString("00") + "m";
        }

        /// <summary>Folds one servicing pass into <paramref name="entry"/>'s stall accounting. Called
        /// exactly once per entry per sweep — the demand loop and the orphan pass are mutually
        /// exclusive per (node, role) via <c>serviced</c>, so this never double-counts.</summary>
        private static void RecordServicing(BuildQueueEntry entry, bool madeProgress, DateTime utcNow,
            FactionTuning tuning)
        {
            double elapsed = entry.LastServicedUtc == default(DateTime)
                ? 0.0
                : (utcNow - entry.LastServicedUtc).TotalSeconds;
            if (elapsed < 0) elapsed = 0;
            double clamp = MaxStallDeltaSeconds(tuning);
            if (elapsed > clamp) elapsed = clamp;

            // Two clocks, deliberately: StalledSeconds resets on any arrival and detects a stop;
            // ServicedSeconds never resets and is the denominator of the inflow rate, which is what
            // says whether a build that IS progressing will finish in a time that matters.
            entry.ServicedSeconds += elapsed;
            if (madeProgress) entry.StalledSeconds = 0;
            else entry.StalledSeconds += elapsed;
            entry.LastServicedUtc = utcNow;
        }

        /// <summary>Roles that draw the combat pool (<see cref="ShipPool.IsSuitableFor"/>), and so
        /// the roles whose hull choice a power floor is meant to decide. Patrol is one of them: a
        /// Patrol build is a combat build.</summary>
        private static bool IsCombatRole(string role)
        {
            return string.Equals(role, "Raid", StringComparison.Ordinal)
                || string.Equals(role, "Patrol", StringComparison.Ordinal);
        }

        /// <summary>Every role a player can list/cancel/override via the
        /// <c>!factions station queue</c> chat commands. Covers both roles <see cref="Tick"/> itself
        /// proactively auto-builds (Trade/Raid/Patrol/StoreSmall/StoreLarge, via
        /// <see cref="PostProactiveShipDemands"/>) and "Mine" — not proactively demanded by this
        /// manager, but a fully live role in the same generic Ship:{role} build pipeline: real
        /// dispatched-Mine ship shortfalls (`OperationsManager`'s Mine dispatch, shipped 2026-08-16)
        /// post real `Ship:Mine` demands through this exact path (see docs/managers.md → "Mine:
        /// active extraction"). A player never has less build reach than the live system as a
        /// whole already has.</summary>
        public static readonly string[] PlayerQueueRoles = { "Trade", "Raid", "Patrol", "Mine" };

        /// <summary>The <see cref="BuildQueueEntry.Role"/> token a station refit is queued under —
        /// a founded node collecting the components to be rebuilt as another design
        /// (<see cref="QueueRefit"/>).
        ///
        /// <para><b>Deliberately not one of <see cref="PlayerQueueRoles"/>.</b> It names no fleet
        /// role and produces no hull: <see cref="QueueBuild"/> refuses it, <see cref="SelectPrefabBom"/>
        /// never picks a prefab for it, and nothing posts a <c>Ship:Station</c> demand, so the only
        /// thing that ever creates one is the player verb. Everything downstream of creation —
        /// absorption, <c>Item:</c> demands, the working slot, stall expiry, cancel, pause,
        /// priority — treats it as an ordinary entry.</para></summary>
        public const string StationRefitRole = "Station";

        /// <summary>True when <paramref name="entry"/> is a station refit rather than a hull build.
        /// The one predicate every "is this the yard building a ship" question reads, so a refit
        /// cannot be counted as fleet work by one caller and not another.</summary>
        public static bool IsStationRefit(BuildQueueEntry entry)
        {
            return entry != null && string.Equals(entry.Role, StationRefitRole, StringComparison.Ordinal);
        }

        /// <summary>True when <paramref name="entry"/> was named by hull — queued by a player or a
        /// doctrine line — and so carries no <see cref="BuildQueueEntry.Role"/>.</summary>
        public static bool IsNamedByHull(BuildQueueEntry entry)
        {
            return entry != null && string.IsNullOrEmpty(entry.Role);
        }

        /// <summary>Whether <paramref name="entry"/> builds a hull a <c>Ship:{role}</c> demand would
        /// accept. An entry that carries a role answers that role alone; one named by hull answers
        /// every role its hull is suitable for (<see cref="ShipPool.IsSuitableForRole"/>), which is
        /// the same test fleet dispatch applies to the finished hull. A combat hull answers Raid and
        /// Patrol both, and that is the truth about it rather than an ambiguity to resolve.</summary>
        public static bool EntryAnswersRole(BuildQueueEntry entry, string role, IPrefabCatalogue cat)
        {
            if (entry == null || string.IsNullOrEmpty(role)) return false;
            if (!IsNamedByHull(entry)) return string.Equals(entry.Role, role, StringComparison.Ordinal);
            return !string.IsNullOrEmpty(entry.PrefabId) && ShipPool.IsSuitableForRole(role, cat, entry.PrefabId);
        }

        /// <summary>Whether <paramref name="prefabId"/> is a hull a player may name for a build: one
        /// suitable for at least one of <see cref="PlayerQueueRoles"/>. The gate the role check on a
        /// player request used to be, without asking the player which role.</summary>
        public static bool FitsAnyPlayerRole(IPrefabCatalogue cat, string prefabId)
        {
            if (string.IsNullOrEmpty(prefabId)) return false;
            for (int i = 0; i < PlayerQueueRoles.Length; i++)
                if (ShipPool.IsSuitableForRole(PlayerQueueRoles[i], cat, prefabId)) return true;
            return false;
        }

        /// <summary>The <see cref="PlayerQueueRoles"/> <paramref name="prefabId"/> is suitable for, in
        /// table order. What a design answers, for display and for attributing demand pressure.</summary>
        public static List<string> RolesFitting(IPrefabCatalogue cat, string prefabId)
        {
            var roles = new List<string>();
            if (string.IsNullOrEmpty(prefabId)) return roles;
            for (int i = 0; i < PlayerQueueRoles.Length; i++)
                if (ShipPool.IsSuitableForRole(PlayerQueueRoles[i], cat, prefabId)) roles.Add(PlayerQueueRoles[i]);
            return roles;
        }

        /// <summary>An entry's role for a log line: "-" for a build named by hull.</summary>
        private static string LogRole(string role)
        {
            return string.IsNullOrEmpty(role) ? "-" : role;
        }

        /// <summary>Claim rank a newly created build for <paramref name="role"/> starts at, from
        /// <see cref="FactionTuning.BuildRolePriority"/>. 0 for an unknown role or absent tuning —
        /// the historical default, never a guess. A null role is a build named by hull and starts at
        /// <see cref="FactionTuning.HighestBuildRolePriority"/>.
        ///
        /// <para>Applied only at creation, so it is a starting rank and not a policy: a rank set
        /// afterwards (<see cref="SetBuildPriority"/>, <c>!factions station queue priority</c>)
        /// survives, because nothing re-derives this for an entry that already exists.</para></summary>
        public static int RolePriority(string role, FactionTuning tuning, bool destinedForSale = false)
        {
            if (tuning == null) return 0;
            // A shelf build takes its rank from its purpose, not its role: it now names a real
            // capability role, so reading the table would hand it that role's fleet priority and let
            // a shop hull outrank the fleet's own work.
            if (destinedForSale) return tuning.StoreShipBuildPriority;
            // A refit names no fleet role and has no row in the table; its rank is its own field.
            if (string.Equals(role, StationRefitRole, StringComparison.Ordinal))
                return tuning.StationRefitPriority;
            // A build named by hull ranks with the highest role: a player or a doctrine line asked for
            // this hull by name, and no automatic build outranks that.
            if (string.IsNullOrEmpty(role)) return tuning.HighestBuildRolePriority();
            if (tuning.BuildRolePriority == null) return 0;
            int priority;
            return tuning.BuildRolePriority.TryGetValue(role, out priority) ? priority : 0;
        }

        /// <summary>
        /// Optional delegate returning all prefab SubtypeId candidates for a fleet role and
        /// faction tag. The host resolves the faction type from the tag and filters the catalogue.
        /// The manager picks the cheapest candidate by total BOM unit count.
        /// Returns null or empty list to trigger free-build mode (no BOM consumed).
        /// Signature: (role, factionTag) → IList&lt;string&gt; | null
        /// </summary>
        public Func<string, string, IList<string>> PrefabListPicker;

        private readonly IItemVolumeCatalogue _volumes;

        public ShipyardManager(IItemVolumeCatalogue itemVolumes = null)
        {
            _volumes = itemVolumes;
        }

        /// <summary>Runs a full Shipyard sweep over <paramref name="states"/> to completion in one
        /// call -- the synchronous contract the offline sim and the operator burst path still rely
        /// on, preserved as a thin wrapper over <see cref="TryAdvance"/> with a deadline that never
        /// expires. Mirrors <see cref="TradeManager.Tick"/>'s exact shape.</summary>
        public void Tick(IList<FactionState> states, WorldModel world, FactionTuning tuning, List<string> log)
        {
            var deadline = FrameDeadline.Unbounded();
            while (!TryAdvance(states, world, tuning, deadline, log)) { }
        }

        /// <summary>Demand and investment expiry. Runs once at the head of a sweep, not once per
        /// faction: both registers are global, so re-pruning them mid-sweep would charge a second
        /// TTL pass against postings the factions still ahead of the cursor have not been serviced
        /// against yet.</summary>
        private static void PruneRegisters(WorldModel world, List<string> log)
        {
            var expiredShipDemands = new List<DemandEntry>();
            world.Demand.Prune(world.UtcNow, expiredShipDemands);
            world.Investments.Prune(world.UtcNow);
            LogExpiredShipDemands(expiredShipDemands, world.UtcNow, log);
        }

        /// <summary>One faction's whole shipyard pass -- the bounded unit the sweep chunks at. Every
        /// read and every commit happens inside this call with no yield point in between; see
        /// ShipyardManager.Cycle.cs's header for why that makes the faction boundary safe to yield
        /// at.</summary>
        private void TickOneFaction(FactionState st, WorldModel world, FactionTuning tuning, List<string> log)
        {
            // No IsAutonomyDisabled check -- self-sufficient (demand/investment-driven), so a
            // Magnate-tier player faction's Shipyard capability actually builds. See
            // docs/economy-territory.md -> "Player-faction claiming".
            if (FactionLifecycle.IsDormant(st, world.UtcNow)) return;

            // Proactively post Ship:{role} demands for any role that has zero stock
            // across the entire faction network. This replaces the old fail-and-demand
            // pattern in FactionRuntime.Enact — the brain no longer needs to attempt a
            // ship action and abort in order to trigger a build.
            // Must run before SnapshotShipDemands so the build loop sees the new demands
            // in the same tick.
            PostProactiveShipDemands(st, world, tuning, log);

            // Hull-bound extraction orders ask for drill hulls sized to what they lack, before the
            // snapshot below so the build is queued this tick (docs/mining-plan.md, slice 3); then
            // deposit-bound ones flown by slow drills ask for one better hull each, from what is
            // left of the same per-pass cap (slice 3b).
            int extractionHullDemands = PostExtractionHullDemands(st, world, tuning, log);
            PostExtractionUpgradeDemands(st, world, tuning,
                tuning.ExtractionMaxHullOrdersPerPass - extractionHullDemands, log);

            // Coverage is asked for by presence, not only by a failed dispatch: a region the
            // faction has founded stations in and no yard is a region whose build pipeline
            // never warms, since every Ship demand is posted per yard. Same request the
            // dispatch-failure path posts, same coverage predicate, so the two cannot disagree.
            PostRegionCoverageRequests(st, world, tuning, log);

            // The fleet doctrine re-arms here, before the queue is snapshotted and serviced
            // below, so a build a line places is worked in the same tick it is placed rather
            // than idling until the next cadence.
            ServiceDoctrine(st, world, tuning, log);

            // Read once: every demand group below asks the same question, and nothing in this pass
            // writes FleetDoctrine, so the answer cannot change mid-pass.
            bool doctrineActive = FactionLifecycle.DoctrineActive(st);

            List<DemandEntry> demands = world.Demand.SnapshotShipDemands(st.Tag);

            // What an outranking capability install is still owed, per component key. Absorption
            // below must leave that amount alone for a build ranked under it -- relaxing
            // ProductionManager's ShipBuildFloor alone would only let the install claim on its own
            // tick, with this manager re-absorbing the same component right back on the next one.
            Dictionary<string, InstallReserveFloor.Claim> installClaims = InstallReserveFloor.ByKey(
                st, world.Investments.SnapshotFor(st.Tag), world.Graph.OwnedBy(st.Tag), tuning);

            // A single node can carry many distinct-destination demands for the same role at
            // once now that DestNodeId is part of the DemandRegister key (e.g. OperationsManager
            // posts one Ship:Trade demand per blocked material vector, all at the same origin
            // Shipyard). Picking the FIRST one encountered per (node, role) to size the build --
            // this loop's old behavior -- meant whichever vector happened to post/refresh most
            // recently arbitrarily dictated the hull for every other route sharing that node.
            // Group by (node, role): the group is serviced once and retired together, and a new
            // build is sized against the demand closest to the group's median MinCargoLiters.
            Dictionary<string, DemandGroup> groupsByNodeRole = BuildRepresentativeDemands(demands);

            // (nodeId|role) pairs that currently have a live demand -- a donor in that set must
            // not be relocated down to nothing to serve someone else's identical need.
            var demandingNodeRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DemandGroup> kv in groupsByNodeRole) demandingNodeRoles.Add(kv.Key);

            // Abandon a build for LACK OF PROGRESS, not for elapsed time. A build still pulling
            // components in is working, however slowly; one that has absorbed nothing for
            // BuildNoProgressHours of serviced time is genuinely stuck and its collected stock is
            // worth more back in inventory.
            // Do NOT prune on demand absence: a demand can lapse between Think cycles without the
            // build being abandoned, and roles that never qualify for a proactive demand (Raid,
            // once combat stock clears MusterStockBaseline) are fed solely by the orphan pass.
            double stallLimit = StallLimitSeconds(tuning);
            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
            {
                // A paused yard's entries are never serviced, so they accrue no stall time either
                // -- a long pause freezes a build in place rather than quietly refunding it.
                if (node.ShipyardPaused) continue;
                for (int i = node.BuildQueue.Count - 1; i >= 0; i--)
                {
                    BuildQueueEntry e = node.BuildQueue[i];

                    // Waiting for a slot is not stalling. An entry behind the queue head is
                    // never serviced, so time spent waiting must not count against it -- roll
                    // both clocks forward exactly as PauseShipyard's resume does, since a full
                    // queue ahead of it is the same situation as a paused yard from its side.
                    if (!node.IsActiveBuild(e))
                    {
                        e.ExpiresUtc = world.UtcNow.AddHours(tuning.MaxBuildDurationHours);
                        e.LastServicedUtc = world.UtcNow;
                        continue;
                    }

                    // Lack of progress is the ONLY abandonment rule. There is deliberately no
                    // elapsed-time backstop: a build still pulling components in is working, and
                    // dropping it for having taken a long time is the exact behaviour this
                    // replaced. A slow build is a supply problem, and the stall rule catches it
                    // the moment supply actually stops.
                    // The measurement FAC-351 is sized from: what this entry has actually pulled
                    // in, over the time it has been serviced, and what that rate implies for the
                    // remainder. Emitted here because the prune loop is where every entry is
                    // visited exactly once per sweep. Off by default -- one line per build per
                    // sweep, and it exists to size a horizon that does not exist yet.
                    if (DebugTraces.BuildRate && e.ServicedSeconds > 0)
                    {
                        long bomUnits = 0, gotUnits = 0;
                        foreach (var kv in e.Bom) bomUnits += kv.Value;
                        foreach (var kv in e.Collected) gotUnits += kv.Value;
                        double perHour = gotUnits / (e.ServicedSeconds / 3600.0);
                        long remaining = bomUnits - gotUnits;
                        string eta = perHour > 0.0 && remaining > 0
                            ? FormatHours(remaining / perHour * 3600.0)
                            : (remaining <= 0 ? "done" : "never");
                        log.Add(st.Tag + " BUILD_RATE role=" + LogRole(e.Role) + " node=" + node.Id
                                + " prefab=" + (e.PrefabId ?? "free")
                                + " got=" + gotUnits.ToString(CultureInfo.InvariantCulture)
                                + "/" + bomUnits.ToString(CultureInfo.InvariantCulture)
                                + " serviced=" + FormatHours(e.ServicedSeconds)
                                + " unitsPerHour=" + perHour.ToString("0", CultureInfo.InvariantCulture)
                                + " eta=" + eta);
                    }

                    if (e.StalledSeconds < stallLimit) continue;

                    // Return collected items and any still-reserved recipe inputs
                    // (ProductionManager.ServiceShipBuildReservations) to the node's routable inventory.
                    RefundEntry(world, node, e);
                    node.BuildQueue.RemoveAt(i);
                    world.RecordEvent(FactionEventKind.BuildExpired, st.Tag, null, node.Id, 1);
                    log.Add(st.Tag + " BUILD_EXPIRED role=" + LogRole(e.Role) + " node=" + node.Id
                            + " reason=no_progress stalled=" + FormatHours(e.StalledSeconds));
                }
            }

            // Which entries hold this faction's slots, decided across all its yards at once
            // (BuildSlotAllocation). Run after the prune above so a refunded entry is not counted,
            // and before anything reads IsActiveBuild below.
            BuildSlotAllocation.Allocate(world, st.Tag, tuning, log);

            // Node+role pairs serviced via a live "Ship:{role}" demand this tick — the orphan
            // pass below must not double-service them.
            var serviced = new HashSet<string>();
            // One refusal line per faction per pass: every demand group at a yard-less faction reaches
            // the first-yard request below, and the refusal is the same answer for all of them.
            bool firstYardRefusalLogged = false;

            // Process each active (node, role) build slot once -- not once per destination-keyed
            // sibling demand sharing it, which serviced the same entry N times per tick.
            foreach (KeyValuePair<string, DemandGroup> groupEntry in groupsByNodeRole)
            {
                DemandGroup group = groupEntry.Value;
                DemandEntry demand = group.Representative;
                string role = demand.Kind.Substring(5); // "Ship:Trade" → "Trade"
                // Nothing posts Ship:Station, and this branch would answer one by picking a fleet
                // prefab and building a hull under the refit role. A refit is created by the player
                // verb alone; the orphan pass below is what services it.
                if (string.Equals(role, StationRefitRole, StringComparison.Ordinal)) continue;
                // The group is keyed on purpose, so every member shares the representative's.
                bool destinedForSale = demand.DestinedForSale;

                SpatialNode originNode;
                if (demand.NodeId == null
                    || !world.Graph.Nodes.TryGetValue(demand.NodeId, out originNode)) continue;
                if (originNode.OwnerFactionTag != st.Tag) continue;
                // Paused: no entry creation, no expiry refresh, no absorption. An externally
                // posted Ship:{role} demand (OperationsManager/TradeManager/FleetManager/
                // StoreStockManager/Enact all post them) must not re-open a queue at a yard the
                // owner just paused.
                if (originNode.ShipyardPaused) continue;

                // Find any existing build queue entry for this node+role FIRST: a mid-collection
                // build is never abandoned for a relocation, and the lookup must precede the
                // Shipyard-capability check so the relocation tier can service a demand posted at
                // a node that could never build.
                // Role alone no longer identifies a build: a shelf build and a fleet build of
                // the same role can share a yard, and matching on role would hand one demand the
                // other's entry.
                // An entry carrying the demand's own role is preferred; failing one, a build named
                // by hull whose hull this demand would accept, unless another group already took it
                // this tick -- a combat hull answers Raid and Patrol both, and one entry serviced
                // twice in a tick absorbs twice.
                BuildQueueEntry entry = null;
                foreach (BuildQueueEntry e in originNode.BuildQueue)
                    if (e.Role == role && e.DestinedForSale == destinedForSale) { entry = e; break; }
                if (entry == null && !destinedForSale)
                    foreach (BuildQueueEntry e in originNode.BuildQueue)
                        if (!e.DestinedForSale && IsNamedByHull(e)
                            && !serviced.Contains(ServicedKeyFor(originNode.Id, e))
                            && EntryAnswersRole(e, role, world.PrefabCatalogue)) { entry = e; break; }

                if (entry == null)
                {
                    // Already met: the node this demand was posted at holds a hull that satisfies
                    // the group's own recorded constraints (each member's route/via legs, and the
                    // group's LARGEST cargo requirement). Retire the whole group rather than
                    // relocating a redundant hull in or opening a build for one that exists --
                    // without this a demand has no termination condition short of a completed
                    // build, and two nodes each demanding the same role poach from each other
                    // indefinitely. Deliberately gated on entry == null: a mid-collection build is
                    // still never abandoned.
                    if (GroupSatisfiedByLocalStock(group, role, originNode, world, tuning))
                    {
                        int retired = world.Demand.ConsumeAll(st.Tag, demand.Kind, originNode.Id,
                            destinedForSale);
                        log.Add(st.Tag + " SHIP_DEMAND_MET role=" + role + " node=" + originNode.Id
                                + " retired=" + retired
                                + " age=" + DescribeAge(OldestPostedUtc(group), world.UtcNow));
                        serviced.Add(ServicedKey(originNode.Id, role, destinedForSale));
                        continue;
                    }

                    // Relocation tier: an idle, route-feasible ship elsewhere in the network is
                    // cheaper than a fresh build. While one is inbound, suppress both a second
                    // relocation and build-entry creation for this (node, role); if the inbound
                    // fleet is lost, the still-alive demand retries here next tick.
                    if (CountInFlightRelocations(st, originNode.Id, role) > 0) continue;
                    if (IsRelocatableRole(role)
                        && TryDispatchRelocation(st, world, tuning, demand, role, originNode, log,
                               demandingNodeRoles, group.MaxMinDrillYield))
                        continue;
                }

                int shipyardLevel;
                if (!originNode.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel)
                    || shipyardLevel < 1)
                {
                    // No Shipyard at this node; if none exists network-wide, request one -- through the
                    // same player opt-in gate as every other Shipyard request. A request is a full
                    // install claim on the faction's components, and the Develop menu shows only what
                    // the player opted in to (FAC-482).
                    if (!NetworkHasCapability(world, st.Tag, NodeCapabilityKind.Shipyard))
                    {
                        if (!FactionLifecycle.CanPostInvestmentFor(st, NodeCapabilityKind.Shipyard))
                        {
                            if (!firstYardRefusalLogged)
                                log.Add(st.Tag + " INVEST_REQUEST Shipyard urgency=0.80 refused=opt-in (no yard network-wide)");
                            firstYardRefusalLogged = true;
                            continue;
                        }
                        bool isNew = world.Investments.Post(new InvestmentRequest
                        {
                            FactionTag = st.Tag,
                            Cap        = NodeCapabilityKind.Shipyard,
                            Urgency    = 0.8,
                            ExpiresUtc = world.UtcNow.Add(InvestmentTtlFor(world.Tuning)),
                        });
                        if (isNew)
                            log.Add(st.Tag + " INVEST_REQUEST Shipyard urgency=0.80");
                    }
                    continue;
                }

                // The doctrine is the only builder. The demand is NOT consumed: it stays in the
                // register until its own TTL retires it, because it is the truest signal that work is
                // waiting on hulls and the player is the one who decides whether to answer it.
                // Deliberately not applied to a shelf build: StoreStockManager's hulls are a
                // different purpose (DestinedForSale) with a different buyer, and the doctrine says
                // nothing about what the shop stocks.
                if (entry == null && doctrineActive && !destinedForSale)
                {
                    LogDoctrinePressure(st, world, role, originNode.Id, group.Members.Count, log);
                    continue;
                }

                // An existing combat entry is re-examined rather than reused outright. Selection is
                // the only place a hull is chosen, and it used to run once, at creation -- so a
                // floor that arrived later, or a catalogue the faction can now afford differently,
                // reached an occupied slot and did nothing. Shelf builds keep the old contract:
                // what the shop stocks is not a fleet decision.
                // A build named by hull is never reconsidered: the hull is the instruction.
                bool combatReconsider = entry != null && !destinedForSale && IsCombatRole(role)
                                        && !IsNamedByHull(entry);
                // A mandatory-livery faction re-examines a generic hull it is building for a role
                // its livery has a hull for, and swaps it only for one of its own. A role with
                // none (the Factorum's Mine) is left to the generic hull it already has.
                bool liveryReconsider = entry != null && !destinedForSale && !IsNamedByHull(entry)
                                        && PrefabFactionHint.LiveryIsMandatory(st.PersonalityId)
                                        && !PrefabRolePool.IsOwnLivery(world.PrefabCatalogue, entry.PrefabId)
                                        && HasOwnLiveryFor(role, st, world);
                bool reconsider = combatReconsider || liveryReconsider;
                bool keepIncumbent = false;
                if (entry == null || reconsider)
                {
                    // First sighting — select prefab and compute BOM.
                    bool ownLiveryOnly;
                    IList<string> candidates = CandidatesFor(role, st, world, destinedForSale, out ownLiveryOnly);

                    // A shelf build diversifies by current per-prefab stock instead of always
                    // picking the same "best" candidate — players see a real selection at the
                    // store block, not N copies of one hull. Fleet builds pass null: cheapest/
                    // posture-power selection is unaffected, since variety is a shop property.
                    Dictionary<string, long> stockByPrefab = destinedForSale
                        ? CountNetworkPrefabStock(world, st.Tag)
                        : null;

                    // Size against the group's representative demand -- the one closest to the
                    // group's median MinCargoLiters -- so one arbitrary route among possibly dozens
                    // sharing this node doesn't alone dictate the hull. (The already-met check
                    // above deliberately uses the group MAXIMUM instead: sizing wants a typical
                    // shipment, retiring wants the binding constraint.)
                    DemandEntry sizingDemand = demand;

                    // Use the representative demand's real destination when it named one (a route-
                    // or size-aware Ship demand from a dispatch failure) so hull selection biases
                    // toward a prefab that survives a REAL route, not just being parked here.
                    // Falls back to the previous degenerate same-point route for demands with no
                    // destination (e.g. the proactive per-Shipyard "keep this zone warm" check).
                    SpatialNode destNode = null;
                    if (sizingDemand.DestNodeId != null)
                        world.Graph.Nodes.TryGetValue(sizingDemand.DestNodeId, out destNode);
                    ShipPool.RouteEnvironment buildRoute = ShipPool.RouteEnvironment.From(
                        world.Planets, originNode.Position,
                        destNode != null ? destNode.Position : originNode.Position, tuning.MinDryTwr);

                    // ViaNodeId names a real intermediate leg (e.g. Haul/Trade's Tier-3 fallback:
                    // the shipyard is nearest to the PICKUP, not the final delivery) the built
                    // hull must ALSO survive reaching -- without this, a demand whose build node
                    // happens to coincide with its destination (a common case) collapses buildRoute
                    // to a trivial same-point route that hides the real pickup leg entirely.
                    ShipPool.RouteEnvironment? viaRoute = null;
                    if (sizingDemand.ViaNodeId != null)
                    {
                        SpatialNode viaNode;
                        if (world.Graph.Nodes.TryGetValue(sizingDemand.ViaNodeId, out viaNode))
                            viaRoute = ShipPool.RouteEnvironment.From(
                                world.Planets, originNode.Position, viaNode.Position, tuning.MinDryTwr);
                    }

                    string prefabId;
                    var bom = new Dictionary<string, long>();
                    bool candidateFilterFallback;
                    bool usedUniversalFallback;
                    bool cargoFloorClamped;
                    bool dpsFloorClamped;
                    IList<string> rankedPool;
                    // A shelf build is excluded from fleet fitness: sizing it against the
                    // faction's own haulage p90 would size it by a statistic about work the
                    // faction does, not about anything a buyer wants.
                    double sizingFloor = FleetFitness.SizingFloor(st, role, tuning,
                        sizingDemand.MinCargoLiters, destinedForSale);
                    SelectPrefabBom(role, candidates, world, st.MilitaryPosture, stockByPrefab, buildRoute,
                        sizingFloor, out prefabId, bom, out candidateFilterFallback, out usedUniversalFallback,
                        out cargoFloorClamped, out rankedPool, viaRoute, sizingDemand.RequiredGridSize,
                        destinedForSale, group.MaxMinDps, out dpsFloorClamped, ownLiveryOnly);
                    if (dpsFloorClamped)
                        log.Add(st.Tag + " BOM_DPS_FLOOR_CLAMPED role=" + role + " node=" + originNode.Id
                                + " floorDps=" + group.MaxMinDps.ToString("0", CultureInfo.InvariantCulture)
                                + " built=" + (prefabId ?? "?"));
                    if (cargoFloorClamped)
                        // The one failure mode fleet-fitness sizing introduces, and it had no
                        // signal at all: a floor no flyable hull can meet is silently clamped to
                        // the largest that can. It degrades UPWARD, so the cost is BOM spent on a
                        // hull bigger than the work, not a stalled build -- but a requirement
                        // nothing can satisfy is still the thing to know about.
                        log.Add(st.Tag + " BOM_CARGO_FLOOR_CLAMPED role=" + role + " node=" + originNode.Id
                                + " floorL=" + sizingFloor.ToString("0", CultureInfo.InvariantCulture)
                                + " liveFloorL=" + sizingDemand.MinCargoLiters.ToString("0", CultureInfo.InvariantCulture)
                                + " built=" + (prefabId ?? "?"));
                    if (candidateFilterFallback)
                        log.Add(st.Tag + " BOM_ZONE_INFEASIBLE role=" + role + " node=" + originNode.Id
                                + " -- no route-viable candidate at any cargo size, built "
                                + (prefabId ?? "?") + " anyway");
                    else if (usedUniversalFallback)
                        log.Add(st.Tag + " BOM_ZONE_UNIVERSAL_FALLBACK role=" + role + " node=" + originNode.Id
                                + " -- same-hint pool zone-infeasible, built " + (prefabId ?? "?") + " from universal pool");

                    LogFleetFitnessCounterfactual(st, role, rankedPool ?? candidates, world, tuning,
                        sizingDemand.MinCargoLiters, prefabId, originNode.Id, log);

                    // Replace only when a fresh pick finishes SOONER than what is left of the
                    // incumbent -- its whole BOM against the incumbent's remainder. Strictly
                    // sooner, with no margin and no threshold: the two sides are the same quantity
                    // measured the same way, so there is nothing to calibrate and nothing that
                    // drifts when the world moves.
                    //
                    // It cannot churn. After a swap the replacement's remainder IS its whole BOM,
                    // so the comparison is an equality and the next pass declines -- and a
                    // candidate can only win once, because winning is what gives it the collected
                    // progress that then defends it.
                    //
                    // The yard's throughput deliberately does NOT appear. Both sides would divide
                    // by the same rate and it cancels, so the verdict is rate-independent: a
                    // smaller remainder finishes first at any speed. Rate belongs to the diagnosis
                    // (BUILD_RATE), where a measured inflow far under what the banks can deliver
                    // says the supply is starved rather than the hull wrong -- see FAC-352.
                    //
                    // A generic hull at a mandatory-livery faction is replaced by any hull of its
                    // own, however much larger: the livery is the requirement, not the speed. It
                    // cannot churn either -- the replacement is its own livery, so the next pass
                    // does not reconsider it on this ground.
                    bool toOwnLivery = false;
                    if (reconsider)
                    {
                        long candidateUnits = 0;
                        foreach (var kv in bom) candidateUnits += kv.Value;
                        long remainingUnits = 0;
                        foreach (var kv in entry.Bom) remainingUnits += kv.Value;
                        foreach (var kv in entry.Collected) remainingUnits -= kv.Value;

                        bool sooner = prefabId != null && candidateUnits > 0 && candidateUnits < remainingUnits;
                        toOwnLivery = liveryReconsider && prefabId != null && candidateUnits > 0
                                      && PrefabRolePool.IsOwnLivery(world.PrefabCatalogue, prefabId);
                        // Declining must fall THROUGH to the servicing below, never skip it: the
                        // incumbent still has to be fed, and an early exit here would starve every
                        // build the rule left alone.
                        keepIncumbent = !(toOwnLivery || (combatReconsider && sooner));
                    }

                    if (reconsider && !keepIncumbent)
                    {
                        long candidateUnits = 0;
                        foreach (var kv in bom) candidateUnits += kv.Value;
                        long remainingUnits = 0;
                        foreach (var kv in entry.Bom) remainingUnits += kv.Value;
                        foreach (var kv in entry.Collected) remainingUnits -= kv.Value;
                        log.Add(st.Tag + " BUILD_RESELECTED role=" + role + " node=" + originNode.Id
                                + " from=" + (entry.PrefabId ?? "free")
                                + " remaining=" + remainingUnits.ToString(CultureInfo.InvariantCulture)
                                + " to=" + prefabId
                                + " bom=" + candidateUnits.ToString(CultureInfo.InvariantCulture)
                                + " reason=" + (toOwnLivery ? "livery" : "sooner"));

                        // The collected components go back to this node's own inventory, where the
                        // replacement absorbs them straight back. That is what keeps FAC-003's
                        // "a slow build is cheaper than a wrong refund" intact: nothing is thrown
                        // away, the components become a hull the faction can actually finish.
                        RefundEntry(world, originNode, entry);
                        originNode.BuildQueue.Remove(entry);
                        entry = null;
                    }

                    if (entry == null)
                        entry = QueueSelectedBuild(st, world, tuning, originNode, role, prefabId, bom,
                            destinedForSale, log);
                }

                // No expiry keep-alive here any more: a live demand is not evidence of progress,
                // and refreshing on it was exactly what made a demand-backed entry immortal while
                // an orphan-fed one aged out on a fixed TTL regardless of how much it had
                // collected. ServiceEntry's own stall accounting is now the single rule for both.
                serviced.Add(ServicedKeyFor(originNode.Id, entry));

                // Behind the queue head: the entry exists and keeps its place, but the yard is
                // working something else. Marked serviced above so the orphan pass leaves it
                // alone too -- one yard, one build in progress.
                if (!originNode.IsActiveBuild(entry)) continue;

                ServiceEntry(st, world, originNode, entry, demand.Kind, demand.NodeId, log,
                    _volumes, installClaims, tuning);
            }

            // Orphan pass: a node's build commitment is durable even on ticks where
            // PostProactiveShipDemands isn't currently pointed at this exact node — e.g. Raid's
            // single network-wide demand placement (one node per role — see
            // ShipyardNodePreferredOverFallback_WhenBothExist) landing at a different Shipyard
            // this tick, or a role whose target is already met elsewhere. (Trade demands are now
            // posted per-Shipyard-node, but a given node's Trade demand can still stop firing the
            // moment its own zone/planet has reachable stock again, same "not this tick" gap.)
            // Without this, such an entry's Collected freezes at whatever it held when it last had
            // a live demand and never absorbs again, even with matching stock piling up right next
            // to it in Inventory. Live-observed: IDIN Alder Station's Raid:001
            // (DS_Builder_SentryDrone) sat frozen at 93/162 Comp:SteelPlate and 11/18
            // Comp:InteriorPlate for 22+ hours while those exact items accumulated untouched in
            // the node's own inventory.
            ReliveryUnservicedBuilds(st, world, tuning, serviced, log);
            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
            {
                if (node.ShipyardPaused) continue;
                // Highest-priority entry absorbs first; the per-key floors below make the outcome
                // order-independent anyway, but claiming in rank order keeps the logs readable.
                foreach (BuildQueueEntry entry in ProductionManager.EntriesByPriority(node))
                {
                    if (serviced.Contains(ServicedKeyFor(node.Id, entry))) continue;
                    if (!node.IsActiveBuild(entry)) continue;
                    ServiceEntry(st, world, node, entry, null, node.Id, log, _volumes, installClaims, tuning);
                }
            }

            // Repairs run after the builds have absorbed, and only from what those builds are not
            // still owed, so a damaged hull never starves a hull already under construction. Hulls
            // queued where there is no yard to repair them fly to one first. What each queue still
            // lacks after absorbing is then ordered, on top of the builds' rows.
            // Hulls of a decommissioned design that were flying come home here, parked or queued for
            // repair; they are dismantled before a transfer or a repair spends anything on them.
            FleetDecommission.SweepReturned(world, st, log);
            RepairTransfer.Dispatch(st, world, tuning, log);
            // Hull refits ride the same queue, behind the repairs (HullDamage.ServedOrder).
            HullRefit.Intake(st, world, log);
            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
            {
                HullDamage.ServiceRepairs(world, node, st.Tag, log);
                RepairSupply.PostDemands(world, node, st.Tag, DemandTtlFor(world.Tuning), log);
            }
        }

        /// <summary>Roles whose Ship:{role} demand may be serviced by relocating an existing idle
        /// ship instead of building a new one. Raid/Patrol demands target a network-wide suitable
        /// COUNT, which a relocation cannot change (it would churn forever); Store roles need a
        /// ListedForSale reservation a relocation doesn't create.</summary>
        private static readonly string[] RelocatableRoles = { "Trade", "Mine" };

        /// <summary>How many distinct (dest, via) leg pairs the already-met check will actually test
        /// before falling back to the representative alone. A safety rail, not the normal path: groups
        /// collapse to one or two members once retirement works, and each pair costs a pair of
        /// gravity/atmosphere samples.</summary>
        private const int MaxSatisfactionLegChecks = 8;

        /// <summary>True when <paramref name="node"/>'s own stock already satisfies every member of
        /// <paramref name="group"/> -- each member's real dest/via legs, gated on the group's LARGEST
        /// cargo requirement rather than its representative's, since the largest is the binding
        /// constraint a hull has to clear for the whole group to be genuinely met. Beyond
        /// <see cref="MaxSatisfactionLegChecks"/> distinct leg pairs only the representative's legs are
        /// tested; the rest retire via ConsumeAll or their own TTL.</summary>
        private static bool GroupSatisfiedByLocalStock(DemandGroup group, string role, SpatialNode node,
            WorldModel world, FactionTuning tuning)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            if (cat == null) return false;
            // A store role's stock lives behind a ListedForSale reservation a relocation never creates,
            // and Raid/Patrol target a network-wide count this node's own stock can't speak for.
            if (!IsRelocatableRole(role)) return false;

            var seenLegs = new HashSet<string>(StringComparer.Ordinal);
            int checkedLegs = 0;
            foreach (DemandEntry member in group.Members)
            {
                if (checkedLegs >= MaxSatisfactionLegChecks) break;
                if (!seenLegs.Add((member.DestNodeId ?? "") + "|" + (member.ViaNodeId ?? ""))) continue;
                checkedLegs++;
                if (!LegSatisfiedByLocalStock(member, role, node, group.MaxMinCargoLiters, group.MaxMinDrillYield,
                        world, tuning, cat))
                    return false;
            }
            if (checkedLegs == 0)
                return LegSatisfiedByLocalStock(group.Representative, role, node,
                    group.MaxMinCargoLiters, group.MaxMinDrillYield, world, tuning, cat);
            return true;
        }

        private static bool LegSatisfiedByLocalStock(DemandEntry demand, string role, SpatialNode node,
            double minCargoLiters, double minDrillYield, WorldModel world, FactionTuning tuning, IPrefabCatalogue cat)
        {
            // Same route/via sampling shape TryDispatchRelocation uses to pick a donor hull, so the
            // check and the thing it suppresses agree on what "suitable here" means.
            SpatialNode destNode = null;
            if (demand.DestNodeId != null) world.Graph.Nodes.TryGetValue(demand.DestNodeId, out destNode);
            ShipPool.RouteEnvironment route = ShipPool.RouteEnvironment.From(
                world.Planets, node.Position,
                destNode != null ? destNode.Position : node.Position, tuning.MinDryTwr);

            ShipPool.RouteEnvironment? viaRoute = null;
            if (demand.ViaNodeId != null)
            {
                SpatialNode viaNode;
                if (world.Graph.Nodes.TryGetValue(demand.ViaNodeId, out viaNode))
                    viaRoute = ShipPool.RouteEnvironment.From(world.Planets, node.Position,
                        viaNode.Position, tuning.MinDryTwr);
            }

            return ShipPool.HasSuitableForRole(node, role, cat, route, viaRoute, minCargoLiters, minDrillYield);
        }

        /// <summary>Earliest <see cref="DemandEntry.FirstPostedUtc"/> in the group, or
        /// <c>default(DateTime)</c> when no member carries one (every "Item:"-shaped poster leaves it
        /// unset, and a demand posted before this field shipped has no value either).</summary>
        private static DateTime OldestPostedUtc(DemandGroup group)
        {
            DateTime oldest = default(DateTime);
            foreach (DemandEntry d in group.Members)
            {
                if (d.FirstPostedUtc == default(DateTime)) continue;
                if (oldest == default(DateTime) || d.FirstPostedUtc < oldest) oldest = d.FirstPostedUtc;
            }
            return oldest;
        }

        /// <summary>Compact "3h12m"/"45s" age for a log line, or "?" when the demand carries no
        /// posting time.</summary>
        private static string DescribeAge(DateTime postedUtc, DateTime utcNow)
        {
            if (postedUtc == default(DateTime) || postedUtc > utcNow) return "?";
            TimeSpan age = utcNow - postedUtc;
            if (age.TotalHours >= 1.0)
                return ((int)age.TotalHours).ToString() + "h" + age.Minutes.ToString() + "m";
            if (age.TotalMinutes >= 1.0)
                return ((int)age.TotalMinutes).ToString() + "m" + age.Seconds.ToString() + "s";
            return ((int)age.TotalSeconds).ToString() + "s";
        }

        /// <summary>One bounded summary line per faction per tick for Ship demands that reached their
        /// TTL without ever being met -- a line per expired entry would run to tens of thousands a day
        /// at live churn. Rides the existing `factions debug shipevents` gate via the SHIP_DEMAND keyword prefix.</summary>
        private static void LogExpiredShipDemands(List<DemandEntry> expired, DateTime utcNow, List<string> log)
        {
            if (expired.Count == 0) return;
            var countByTag = new Dictionary<string, int>(StringComparer.Ordinal);
            var oldestByTag = new Dictionary<string, DemandEntry>(StringComparer.Ordinal);
            foreach (DemandEntry d in expired)
            {
                string tag = d.FactionTag ?? "";
                int count;
                countByTag.TryGetValue(tag, out count);
                countByTag[tag] = count + 1;

                DemandEntry oldest;
                if (!oldestByTag.TryGetValue(tag, out oldest)
                    || (d.FirstPostedUtc != default(DateTime)
                        && (oldest.FirstPostedUtc == default(DateTime) || d.FirstPostedUtc < oldest.FirstPostedUtc)))
                    oldestByTag[tag] = d;
            }

            var tags = new List<string>(countByTag.Keys);
            tags.Sort(StringComparer.Ordinal);
            foreach (string tag in tags)
            {
                DemandEntry oldest = oldestByTag[tag];
                log.Add(tag + " SHIP_DEMAND_EXPIRED count=" + countByTag[tag]
                        + " oldest=" + oldest.Kind + "@" + (oldest.NodeId ?? "?")
                        + " age=" + DescribeAge(oldest.FirstPostedUtc, utcNow));
            }
        }

        private static bool IsRelocatableRole(string role)
        {
            foreach (string r in RelocatableRoles)
                if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>In-flight 0-cargo relocation Hauls headed to <paramref name="nodeId"/> for
        /// <paramref name="role"/>. Relocations are single-leg, so <c>TargetNodeId</c> IS the
        /// delivery node -- none of the queued-stop ambiguity
        /// <c>OperationsManager.CountInFlightMineFleetsToward</c> has to handle.</summary>
        private static int CountInFlightRelocations(FactionState st, string nodeId, string role)
        {
            int count = 0;
            foreach (PendingFleetAction f in st.PendingFleets)
            {
                if (f.Kind != ActionKind.Haul || f.Resolved || f.Intercepted) continue;
                if (!string.Equals(f.RelocationRole, role, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(f.TargetNodeId, nodeId, StringComparison.Ordinal)) continue;
                count++;
            }
            return count;
        }

        /// <summary>Tries to service a Ship:{role} demand by moving an idle, route-feasible,
        /// suitably-sized ship from the nearest owned donor node to <paramref name="needNode"/> as a
        /// cargo-less single-leg <see cref="ActionKind.Haul"/> -- the Haul terminal resolution
        /// already credits the hauler ship to the destination's ShipStock, so no new resolution
        /// machinery is needed. Deliberately NOT <see cref="ActionKind.Ferry"/>: Ferry stays reserved
        /// for a future ship-carries-ship mechanic. The relocated ship lands in generic stock --
        /// nothing reserves it for the demanding role, and another dispatcher may consume it first;
        /// the still-alive demand then simply retries (accepted behavior). Returns false when no
        /// donor qualifies -- the caller falls through to the build path.
        /// <para><paramref name="demandingNodeRoles"/> holds every (nodeId|role) that currently has a
        /// live demand of its own. A donor in that set is skipped when donating would leave it with no
        /// role-suitable hull at all: two nodes each demanding the same role would otherwise hand their
        /// last hull to each other every tick forever, each relocation "servicing" a demand while
        /// leaving the network exactly where it started.</para></summary>
        private static bool TryDispatchRelocation(FactionState st, WorldModel world, FactionTuning tuning,
            DemandEntry demand, string role, SpatialNode needNode, List<string> log,
            HashSet<string> demandingNodeRoles, double minDrillYield = 0)
        {
            IPrefabCatalogue cat = world.PrefabCatalogue;
            var donors = new List<SpatialNode>();
            foreach (SpatialNode n in world.Graph.OwnedBy(st.Tag))
            {
                if (string.Equals(n.Id, needNode.Id, StringComparison.Ordinal)) continue;
                if (demandingNodeRoles != null && demandingNodeRoles.Contains(n.Id + "|" + role)
                    && ShipPool.SuitableCountForRole(n, role, cat) <= 1) continue;
                donors.Add(n);
            }
            donors.Sort((a, b) =>
            {
                int cmp = a.Position.DistanceTo(needNode.Position)
                    .CompareTo(b.Position.DistanceTo(needNode.Position));
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
            });

            // Same viaRoute semantics as SelectPrefabBom: when the demand names a real intermediate
            // leg (e.g. Mine's shipyard-anchored demand carrying the deposit as via), the relocated
            // hull must survive reaching it from the need node too.
            ShipPool.RouteEnvironment? viaRoute = null;
            if (demand.ViaNodeId != null)
            {
                SpatialNode viaNode;
                if (world.Graph.Nodes.TryGetValue(demand.ViaNodeId, out viaNode))
                    viaRoute = ShipPool.RouteEnvironment.From(world.Planets, needNode.Position,
                        viaNode.Position, tuning.MinDryTwr);
            }

            foreach (SpatialNode donor in donors)
            {
                ShipPool.RouteEnvironment route = ShipPool.RouteEnvironment.From(
                    world.Planets, donor.Position, needNode.Position, tuning.MinDryTwr);
                string prefabId;
                if (ShipPool.ConsumeForRole(donor, role, cat, 1, out prefabId, route, viaRoute,
                        demand.MinCargoLiters, minDrillYield) <= 0)
                    continue;
                prefabId = prefabId ?? "";

                double distKm = donor.Position.DistanceTo(needNode.Position);
                DateTime departed = world.UtcNow;
                DateTime arrival = tuning.FleetSpeedKmPerMin > 0
                    ? departed.AddMinutes(tuning.TransitTimeMins(distKm))
                    : departed;

                var reloc = new PendingFleetAction
                {
                    Kind                = ActionKind.Haul,
                    RelocationRole      = role,
                    ActorTag            = st.Tag,
                    OriginNodeId        = donor.Id,
                    TargetNodeId        = needNode.Id,
                    OriginPosKm         = donor.Position,
                    TargetPosKm         = needNode.Position,
                    DepartedUtc         = departed,
                    EstimatedArrivalUtc = arrival,
                    CargoCapacityLiters = cat != null && prefabId.Length > 0 ? cat.CargoLiters(prefabId) : 0,
                    ConvoySize          = 1,
                    ShipPrefabId        = prefabId,
                    FleetId             = PendingFleetAction.MintId(st.PendingFleets, st.Tag, departed, needNode.Id, donor.Id + ":reloc"),
                    LastResolvedSegment = -1,
                };
                reloc = PendingFleetResolver.PlanFleetLeg(reloc, world, tuning, false, null, null, log);
                FleetMunitions.Arm(ref reloc, world, tuning, log);
                st.PendingFleets.Add(reloc);

                log.Add(reloc.LogId + " " + st.Tag + " SHIP_RELOCATE_DISPATCHED role=" + role
                        + (prefabId.Length > 0 ? " prefab=" + prefabId : "")
                        + " " + donor.Id + " -> " + needNode.Id
                        + " eta=" + arrival.ToString("HH:mm:ss")
                        + (demand.DestNodeId != null ? " for_dest=" + demand.DestNodeId : ""));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Absorbs available BOM components from <paramref name="node"/>'s routable inventory into
        /// <paramref name="entry"/>, then either completes the build (credits the ship, removes the
        /// entry) or posts <c>Item:</c> demands for the remaining shortfall.
        /// <paramref name="demandKind"/> is non-null only when a live <c>Ship:</c> demand drove this
        /// call this tick — completion then retires EVERY demand sharing this (node, role) build slot
        /// (<see cref="DemandRegister.ConsumeAll"/>), not just the one destination key that happened
        /// to drive it, since this manager only ever maintains one entry per (node, role).
        /// The orphan pass in <see cref="Tick"/> passes null since no such demand targets this node
        /// this tick; <paramref name="logNodeId"/> is used for logging and for the posted
        /// <c>Item:</c> demands' <c>NodeId</c> either way.
        /// </summary>
        private static void ServiceEntry(FactionState st, WorldModel world, SpatialNode node,
            BuildQueueEntry entry, string demandKind, string logNodeId, List<string> log,
            IItemVolumeCatalogue itemVolumes, Dictionary<string, InstallReserveFloor.Claim> installClaims,
            FactionTuning tuning)
        {
            // Free build: no BOM to collect.
            if (entry.Bom.Count == 0)
            {
                // A refit whose net bill is empty owes nothing -- the node already stands as
                // everything the target design asks for. Reached on a reload of one queued that
                // way; QueueRefit itself completes it on the spot.
                if (IsStationRefit(entry))
                {
                    if (demandKind != null)
                        world.Demand.ConsumeAll(st.Tag, demandKind, logNodeId, entry.DestinedForSale);
                    CompleteStationRefit(st.Tag, world, node, entry, logNodeId, log);
                    node.BuildQueue.Remove(entry);
                    return;
                }
                CreditBuiltShip(node, entry.Role, null, entry.DestinedForSale); // free build — no known prefab
                if (demandKind != null)
                    world.Demand.ConsumeAll(st.Tag, demandKind, logNodeId, entry.DestinedForSale);
                node.BuildQueue.Remove(entry);
                world.RecordEvent(FactionEventKind.ShipBuilt, st.Tag, null, logNodeId, 1);
                log.Add(st.Tag + " SHIP_BUILT role=" + LogRole(entry.Role) + " node=" + logNodeId + " (free)");
                return;
            }

            // Any component landing in Collected this pass counts as progress and resets the entry's
            // stall clock -- the single rule deciding whether it is eventually abandoned.
            bool absorbedAnything = false;

            // Absorb available items from the node's routable inventory into this build slot.
            // Items in Collected are no longer in node.Inventory — OperationsManager
            // sees an honest empty slot and routes more without any floor math.
            //
            // This absorption is DemandHistory's other real-consumption feed point (alongside
            // ProductionManager's recipe-input use) -- credited here, never at Haul-arrival/delivery,
            // so the gravity signal is bounded by this build's own BOM-completion rate rather than a
            // self-reinforcing delivery loop (docs/material-vectors.md's dated correction note).
            foreach (var kv in entry.Bom)
            {
                string subTypeId = kv.Key;
                long needed      = kv.Value;
                long collected;  entry.Collected.TryGetValue(subTypeId, out collected);
                long stillNeed = needed - collected;
                if (stillNeed <= 0) continue;

                long available; node.Inventory.TryGetValue(subTypeId, out available);
                if (available <= 0) continue;

                // Leave whatever a stronger claim on this key is owed: a higher-priority entry at
                // this same node, any capability install ranked above this build, and any Stock order
                // ranked above it (as the last production pass built its claims). All are zero at the
                // default ranks, so absorption is unchanged until someone sets a priority.
                StockClaimFloor stockClaims = world.StockClaims(st.Tag);
                long floor = node.BuildQueueReserved(subTypeId, world.UtcNow, entry.Priority + 1)
                           + InstallReserveFloor.For(installClaims, subTypeId, entry.Priority)
                           + (stockClaims != null ? stockClaims.For(node.Id, subTypeId, entry.Priority) : 0);
                if (floor > 0)
                {
                    available -= floor;
                    if (available <= 0) continue;
                }

                long absorb = available < stillNeed ? available : stillNeed;
                world.AddNodeInventory(node.Id, subTypeId, -absorb);
                entry.Collected[subTypeId] = collected + absorb;
                if (absorb > 0) absorbedAnything = true;

                double litersPerUnit = itemVolumes != null ? itemVolumes.VolumeLitersPerUnit(subTypeId) : 0;
                double weighted = litersPerUnit > 0 ? absorb * litersPerUnit : absorb;
                world.AddPendingDemandHistoryCredit(node.Id, subTypeId, weighted);
            }

            // Check if the BOM is fully collected.
            bool canBuild = true;
            foreach (var kv in entry.Bom)
            {
                long collected; entry.Collected.TryGetValue(kv.Key, out collected);
                if (collected < kv.Value) { canBuild = false; break; }
            }

            if (canBuild)
            {
                // A refit produces no hull: the components are spent on the node's own rebuild, and
                // what completion leaves behind is an obligation on the host to stand the new design
                // up. Nothing reaches ShipStock.
                if (IsStationRefit(entry))
                {
                    if (demandKind != null)
                        world.Demand.ConsumeAll(st.Tag, demandKind, logNodeId, entry.DestinedForSale);
                    CompleteStationRefit(st.Tag, world, node, entry, logNodeId, log);
                    node.BuildQueue.Remove(entry);
                    return;
                }

                // Collected items are consumed by the build (they're already out of node.Inventory).
                // Any raw input ProductionManager.ServiceShipBuildReservations had reserved toward this
                // entry but never got to convert (e.g. the BOM finished via directly-collected components
                // instead) would otherwise be silently destroyed along with the removed entry — refund it.
                RefundReservedInputs(world, node, entry);
                CreditBuiltShip(node, entry.Role, entry.PrefabId, entry.DestinedForSale);
                if (demandKind != null)
                    world.Demand.ConsumeAll(st.Tag, demandKind, logNodeId, entry.DestinedForSale);
                node.BuildQueue.Remove(entry);
                world.RecordEvent(FactionEventKind.ShipBuilt, st.Tag, null, logNodeId, 1);
                log.Add(st.Tag + " SHIP_BUILT role=" + LogRole(entry.Role) + " node=" + logNodeId
                        + " prefab=" + (entry.PrefabId ?? "(free)"));
            }
            else
            {
                RecordServicing(entry, absorbedAnything, world.UtcNow, tuning);

                // Post Item: demands for remaining shortfall only (Collected already holds the rest).
                foreach (var kv in entry.Bom)
                {
                    long collected; entry.Collected.TryGetValue(kv.Key, out collected);
                    long shortfall = kv.Value - collected;
                    if (shortfall <= 0) continue;

                    bool isNew = world.Demand.Post(new DemandEntry
                    {
                        FactionTag = st.Tag,
                        Kind       = "Item:" + kv.Key,
                        NodeId     = logNodeId,
                        ExpiresUtc = world.UtcNow.Add(DemandTtlFor(world.Tuning)),
                        Quantity   = shortfall,
                        // The strongest class there is: these components are already sunk into this
                        // entry's Collected and a stall refunds them, so leaving it unmet costs more
                        // than any other Item: want of the same size.
                        Class      = DemandClass.ShipBuild,
                    });
                    // Same shortfall as an operational need, so PriceOracle stops calling this
                    // component oversupplied while a build waits on it. Node-keyed: a ship BOM is
                    // owed at the shipyard, not wherever the faction happens to hold stock.
                    world.Demand.Post(DemandEntry.ForNeed(st.Tag, kv.Key, logNodeId,
                        world.UtcNow.Add(DemandTtlFor(world.Tuning)), shortfall, DemandClass.ShipBuild));
                    if (isNew)
                    {
                        long have; node.Inventory.TryGetValue(kv.Key, out have);
                        log.Add(st.Tag + " ITEM_DEMAND " + kv.Key
                                + " need=" + shortfall + " have=" + have
                                + " bom=" + kv.Value + " collected=" + collected
                                + " node=" + logNodeId);
                    }
                }
            }
        }

        /// <summary>
        /// Enact handler for <see cref="ActionKind.FinishBuild"/> — the brain's deliberate
        /// "consolidate resources and finish this build now" override for a queue entry that has
        /// already crossed <see cref="FactionTuning.BuildCompletionUrgencyFraction"/>. Unlike the
        /// per-tick <see cref="ServiceEntry"/> absorption (which only ever draws from the build's own
        /// node), this pulls each remaining BOM shortfall from routable <see cref="SpatialNode.Inventory"/>
        /// across every node the faction owns — the same "across as many owned nodes as it takes"
        /// shape <see cref="WorldModel.ConsumeFactionReservedInventoryByKey"/> already uses for
        /// Develop's capability-install BOM — so a faction with no ships to physically Haul the
        /// missing components in isn't permanently stuck. Never touches
        /// <see cref="SpatialNode.ReservedInventory"/> anywhere, so it can't cannibalize stock another
        /// build or a capability install has already earmarked. Only scoped to entries already at/above
        /// the urgency threshold — this accelerates the last stretch of a near-finished build, it does
        /// not fund one from scratch.
        /// </summary>
        public void ForceCollect(FactionState st, WorldModel world, string nodeId, FactionTuning tuning, List<string> log)
        {
            SpatialNode node;
            if (nodeId == null || !world.Graph.Nodes.TryGetValue(nodeId, out node)) return;
            if (node.OwnerFactionTag != st.Tag) return;
            if (node.ShipyardPaused) return;

            List<SpatialNode> owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));

            for (int i = node.BuildQueue.Count - 1; i >= 0; i--)
            {
                BuildQueueEntry entry = node.BuildQueue[i];
                if (entry.Bom.Count == 0) continue; // free build — nothing to consolidate
                if (!node.IsActiveBuild(entry)) continue; // waiting for a slot — not this yard's current work

                long totalBom = 0, totalCollected = 0;
                foreach (var kv in entry.Bom) totalBom += kv.Value;
                foreach (var kv in entry.Collected) totalCollected += kv.Value;
                if (totalBom == 0 || (double)totalCollected / totalBom < tuning.BuildCompletionUrgencyFraction) continue;

                foreach (var kv in entry.Bom)
                {
                    string subTypeId = kv.Key;
                    long needed      = kv.Value;
                    long collected;  entry.Collected.TryGetValue(subTypeId, out collected);
                    long stillNeed = needed - collected;
                    if (stillNeed <= 0) continue;

                    foreach (SpatialNode source in owned)
                    {
                        if (stillNeed <= 0) break;
                        long available; source.Inventory.TryGetValue(subTypeId, out available);
                        if (available <= 0) continue;

                        long absorb = available < stillNeed ? available : stillNeed;
                        world.AddNodeInventory(source.Id, subTypeId, -absorb);
                        entry.Collected[subTypeId] = collected + absorb;
                        collected += absorb;
                        stillNeed -= absorb;

                        double litersPerUnit = _volumes != null ? _volumes.VolumeLitersPerUnit(subTypeId) : 0;
                        double weighted = litersPerUnit > 0 ? absorb * litersPerUnit : absorb;
                        world.AddPendingDemandHistoryCredit(node.Id, subTypeId, weighted);
                    }
                }

                bool canBuild = true;
                foreach (var kv in entry.Bom)
                {
                    long collected; entry.Collected.TryGetValue(kv.Key, out collected);
                    if (collected < kv.Value) { canBuild = false; break; }
                }

                if (canBuild)
                {
                    if (IsStationRefit(entry))
                    {
                        CompleteStationRefit(st.Tag, world, node, entry, nodeId, log);
                        node.BuildQueue.RemoveAt(i);
                        continue;
                    }
                    RefundReservedInputs(world, node, entry);
                    CreditBuiltShip(node, entry.Role, entry.PrefabId, entry.DestinedForSale);
                    node.BuildQueue.RemoveAt(i);
                    world.RecordEvent(FactionEventKind.ShipBuilt, st.Tag, null, nodeId, 1);
                    log.Add(st.Tag + " SHIP_BUILT role=" + LogRole(entry.Role) + " node=" + nodeId
                            + " prefab=" + (entry.PrefabId ?? "(free)") + " (force)");
                }
                else
                {
                    log.Add(st.Tag + " FINISH_BUILD_FORCE role=" + LogRole(entry.Role) + " node=" + nodeId
                            + " -- network-wide stock still short, build not yet complete");
                }
            }
        }

        /// <summary>Returns everything <paramref name="entry"/> is holding — both absorbed
        /// <see cref="BuildQueueEntry.Collected"/> components and any reserved recipe inputs (via
        /// <see cref="RefundReservedInputs"/>) — to <paramref name="node"/>'s routable inventory.
        /// Shared by every path that removes an entry before it completes: <see cref="Tick"/>'s
        /// expiry prune, <see cref="CancelBuild"/>, and <see cref="QueueBuild"/>'s override-in-place.</summary>
        internal static void RefundEntry(WorldModel world, SpatialNode node, BuildQueueEntry entry)
        {
            foreach (var kv in entry.Collected)
                world.AddNodeInventory(node.Id, kv.Key, kv.Value);
            entry.Collected.Clear();
            RefundReservedInputs(world, node, entry);
        }

        /// <summary>Refunds and removes every queued shelf build owned by <paramref name="factionTag"/>,
        /// returning the count removed. Selected by <see cref="BuildQueueEntry.DestinedForSale"/>,
        /// not by role name, so it keeps working once the store roles are retired.
        /// Called by <see cref="StoreStockManager"/> for a faction that no longer keeps a player
        /// store (<see cref="FactionStorePolicy.HasPlayerStore"/>).
        ///
        /// <para>Actively cancelling is not tidiness. Withholding the <c>Ship:Store*</c> demand
        /// alone leaves the entry in place, and <see cref="Tick"/>'s orphan pass services an active
        /// entry whether or not a demand points at its node — deliberately, so a build does not
        /// freeze on a tick its demand lands elsewhere. So an abandoned store build keeps absorbing
        /// components indefinitely, never stalls, and so never reaches the no-progress refund it
        /// would otherwise expire through.</para>
        ///
        /// <para>A paused yard is included: a pause freezes a build the owner still intends to
        /// finish, and this faction no longer has anything to finish it for.</para></summary>
        public static int CancelStoreBuilds(WorldModel world, string factionTag, List<string> log)
        {
            if (world == null || string.IsNullOrEmpty(factionTag)) return 0;

            int cancelled = 0;
            foreach (SpatialNode node in world.Graph.OwnedBy(factionTag))
            {
                for (int i = node.BuildQueue.Count - 1; i >= 0; i--)
                {
                    BuildQueueEntry e = node.BuildQueue[i];
                    if (!e.DestinedForSale) continue;

                    RefundEntry(world, node, e);
                    node.BuildQueue.RemoveAt(i);
                    cancelled++;

                    world.RecordEvent(FactionEventKind.BuildCancelled, factionTag, null, node.Id, 1);
                    if (log != null)
                        log.Add(factionTag + " BUILD_CANCELLED role=" + LogRole(e.Role) + " node=" + node.Id
                                + " prefab=" + (e.PrefabId ?? "(free)") + " reason=no_player_store");
                }
            }
            return cancelled;
        }

        /// <summary>Player-facing cancel for the <c>!factions station queue cancel</c> chat command
        /// (see <see cref="Commands.PlayerStationCommands"/> in the host). Refunds the entry's
        /// collected components and reserved inputs back to node inventory (nothing is destroyed)
        /// and removes it from <see cref="SpatialNode.BuildQueue"/>.</summary>
        public PlayerQueueResult CancelBuild(WorldModel world, string factionTag, string nodeId, string entryId,
            out BuildQueueEntry cancelled, List<string> log)
        {
            cancelled = null;

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return PlayerQueueResult.NodeNotFound;
            if (!string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return PlayerQueueResult.NotOwned;

            BuildQueueEntry entry = null;
            foreach (BuildQueueEntry e in node.BuildQueue)
                if (string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase)) { entry = e; break; }
            if (entry == null) return PlayerQueueResult.EntryNotFound;

            RefundEntry(world, node, entry);
            node.BuildQueue.Remove(entry);
            cancelled = entry;

            world.RecordEvent(FactionEventKind.BuildCancelled, factionTag, null, nodeId, 1);
            log.Add(factionTag + " BUILD_CANCELLED_BY_PLAYER role=" + LogRole(entry.Role) + " node=" + nodeId
                    + " prefab=" + (entry.PrefabId ?? "(free)"));
            return PlayerQueueResult.Ok;
        }

        /// <summary>Player/admin-facing pause switch for the <c>!factions station queue pause|resume</c>
        /// chat commands — flips <see cref="SpatialNode.ShipyardPaused"/> at <paramref name="nodeId"/>.
        /// A paused yard claims nothing new: no absorption, no <c>Item:</c>/<c>Ship:</c> demands, no
        /// recipe-input reservation, no entry creation, and its entries stop hiding their uncollected
        /// BOM from every other consumer (<see cref="SpatialNode.BuildQueueReserved"/>) — which is
        /// what actually frees a contested component for a stalled capability install. What each
        /// entry already physically holds stays put and stops expiring, so a pause freezes the builds
        /// rather than unwinding them; resuming re-arms every entry's deadline so a long pause can't
        /// strand a build at the moment it wakes up.</summary>
        public PlayerQueueResult PauseShipyard(WorldModel world, FactionTuning tuning, string factionTag,
            string nodeId, bool paused, List<string> log)
        {
            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return PlayerQueueResult.NodeNotFound;
            if (!string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return PlayerQueueResult.NotOwned;
            int level;
            if (!node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level) || level < 1)
                return PlayerQueueResult.NoShipyard;

            node.ShipyardPaused = paused;
            if (!paused)
                foreach (BuildQueueEntry e in node.BuildQueue)
                {
                    // Resume re-arms both clocks. LastServicedUtc especially: leaving it at the
                    // pre-pause value would hand the first post-resume sweep a delta covering the
                    // whole pause (clamped, but still a free stall increment for time the yard was
                    // deliberately stopped).
                    e.ExpiresUtc = world.UtcNow.AddHours(tuning.MaxBuildDurationHours);
                    e.LastServicedUtc = world.UtcNow;
                }

            log.Add(factionTag + " SHIPYARD_PAUSE_BY_PLAYER node=" + nodeId + " paused=" + paused
                    + " builds=" + node.BuildQueue.Count);
            return PlayerQueueResult.Ok;
        }

        /// <summary>Player/admin-facing claim rank for one build entry
        /// (<c>!factions station queue priority</c>) — see <see cref="BuildQueueEntry.Priority"/>.
        /// Higher wins a contested inventory key, against both the node's other builds and any
        /// capability install ranked on the same scale
        /// (<see cref="FactionState.InstallPriority"/>).</summary>
        public PlayerQueueResult SetBuildPriority(WorldModel world, string factionTag, string nodeId,
            string entryId, int priority, List<string> log)
        {
            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return PlayerQueueResult.NodeNotFound;
            if (!string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return PlayerQueueResult.NotOwned;

            BuildQueueEntry entry = null;
            foreach (BuildQueueEntry e in node.BuildQueue)
                if (string.Equals(e.Id, entryId, StringComparison.OrdinalIgnoreCase)) { entry = e; break; }
            if (entry == null) return PlayerQueueResult.EntryNotFound;

            int previous = entry.Priority;
            entry.Priority = priority;
            // Recorded here because this is the only place a human sets a rank, and
            // BuildSlotAllocation reads it as an instruction about the working slot. Inferring it
            // from the role table instead misreads every entry that outlived a change to that table.
            entry.PriorityByPlayer = true;
            log.Add(factionTag + " BUILD_PRIORITY_BY_PLAYER entry=" + entry.Id + " node=" + nodeId
                    + " from=" + previous + " to=" + priority);
            return PlayerQueueResult.Ok;
        }

        /// <summary>Player-facing build for the <c>!factions station queue build</c> chat command and
        /// the Build dialog — queues <paramref name="prefabId"/> at <paramref name="nodeId"/>. It
        /// APPENDS: a yard already building gains a second entry rather than losing the first, and
        /// the two queue against each other for the yard's working slots like any other pair.
        /// <paramref name="prefabId"/> is required: there is no auto-pick mode here, since that's
        /// exactly what happens automatically without this command.
        ///
        /// <para><b><paramref name="role"/> is null for a player or a doctrine line</b>: they name a
        /// hull, and the entry carries no role (<see cref="IsNamedByHull"/>). The hull must then be
        /// suitable for at least one of <see cref="PlayerQueueRoles"/>. A non-null role is the
        /// shelf's (<see cref="StoreStockManager"/>), which still builds per capability role, and the
        /// hull must be suitable for that role. Both checks read
        /// <see cref="ShipPool.IsSuitableForRole"/> — catalogue-hint-agnostic, so a player may pick
        /// any correctly-classed hull, not just the faction-hint-filtered subset the NPC brain would
        /// default to.</para>
        /// Costs no treasury, matching NPC build parity — only BOM components, collected the same way
        /// any other entry is on subsequent <see cref="Tick"/> calls (no extra wiring needed).
        ///
        /// <para><b>Nothing is displaced, so no caller has to pre-filter.</b> Clicking Build twice
        /// queues two hulls; a build that is no longer wanted is removed by <see cref="CancelBuild"/>,
        /// which addresses it by id. <see cref="ServiceDoctrine"/> calls in here for the same reason
        /// the player verb does and needs no occupancy test of its own.</para>
        ///
        /// <para><paramref name="destinedForSale"/> raises the build for the shelf instead of the
        /// fleet: it ranks <see cref="FactionTuning.StoreShipBuildPriority"/> rather than its role's,
        /// and the finished hull is reserved into <see cref="SpatialNode.ListedForSale"/> where fleet
        /// dispatch cannot reach it. <see cref="StoreStockManager"/> is the caller that sets
        /// it.</para></summary>
        public PlayerQueueResult QueueBuild(WorldModel world, FactionTuning tuning, string factionTag,
            string nodeId, string role, string prefabId, out Dictionary<string, long> bomOut, List<string> log,
            bool destinedForSale = false)
        {
            bomOut = null;

            if (string.IsNullOrEmpty(role)) role = null;
            else if (Array.IndexOf(PlayerQueueRoles, role) < 0)
                return PlayerQueueResult.UnknownRole;

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return PlayerQueueResult.NodeNotFound;
            if (!string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return PlayerQueueResult.NotOwned;

            int shipyardLevel;
            if (!node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel) || shipyardLevel < 1)
                return PlayerQueueResult.NoShipyard;

            if (string.IsNullOrEmpty(prefabId)
                || (role == null
                    ? !FitsAnyPlayerRole(world.PrefabCatalogue, prefabId)
                    : !ShipPool.IsSuitableForRole(role, world.PrefabCatalogue, prefabId)))
                return PlayerQueueResult.UnknownPrefab;

            // Every build path ends here — the player queue, doctrine placement, a sale build — so
            // this is the one place the ban and the ceiling cannot be walked around.
            if (world.IsPrefabBanned(prefabId))
                return PlayerQueueResult.Banned;
            if (world.IsPrefabOverCeiling(prefabId))
                return PlayerQueueResult.OverPerformanceCeiling;

            bool hasRecipe;
            Dictionary<string, long> bom = ProductionManager.GetPrefabBom(world.Recipes, prefabId, out hasRecipe);
            if (!hasRecipe)
                return PlayerQueueResult.UnknownPrefab;

            var newEntry = new BuildQueueEntry
            {
                Id              = node.MintBuildEntryId(role),
                Role            = role,
                PrefabId        = prefabId,
                DestinedForSale = destinedForSale,
                CreatedUtc      = world.UtcNow,
                ExpiresUtc      = world.UtcNow.AddHours(tuning.MaxBuildDurationHours),
                Priority        = RolePriority(role, tuning, destinedForSale),
            };
            foreach (var kv in bom) newEntry.Bom[kv.Key] = kv.Value;
            node.BuildQueue.Add(newEntry);

            // Same event as the demand-driven queue below -- a build was queued. Which side of the
            // manager put it there is not a distinction a feed of "what happened to my faction"
            // should draw, and for a player faction this is the half that actually gets used.
            world.RecordEvent(FactionEventKind.BuildQueued, factionTag, null, nodeId, 1);
            log.Add(factionTag + (destinedForSale ? " SHELF_BUILD_QUEUED role=" : " BUILD_QUEUED_BY_PLAYER role=")
                    + LogRole(role) + " node=" + nodeId + " prefab=" + prefabId
                    + " id=" + newEntry.Id);
            bomOut = bom;
            return PlayerQueueResult.Ok;
        }

        /// <summary>
        /// What one design costs from another, per component: <c>max(0, target - current)</c> over
        /// the TARGET's keys alone. A component the current design uses and the target does not is
        /// not refunded and does not reduce the bill -- it is simply not part of it.
        ///
        /// <para>A null or empty <paramref name="currentBom"/> means nothing is already standing
        /// here, and the net is the target's gross bill.</para>
        /// </summary>
        public static Dictionary<string, long> NetRefitBom(IReadOnlyDictionary<string, long> currentBom,
            IReadOnlyDictionary<string, long> targetBom)
        {
            var net = new Dictionary<string, long>(StringComparer.Ordinal);
            if (targetBom == null) return net;
            foreach (KeyValuePair<string, long> kv in targetBom)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                long have = 0;
                if (currentBom != null) currentBom.TryGetValue(kv.Key, out have);
                long owed = kv.Value - have;
                if (owed > 0) net[kv.Key] = owed;
            }
            return net;
        }

        /// <summary>The same difference, resolved from two prefab ids through the
        /// <c>Prefab/&lt;id&gt;</c> recipes <see cref="QueueBuild"/> reads — the one place a prefab's
        /// bill comes from. <paramref name="targetHasRecipe"/> is false when the target design has no
        /// registered recipe at all, which is not the same as an empty difference: the first is a
        /// design nothing can price, the second a design the node already stands as.</summary>
        public static Dictionary<string, long> NetRefitBom(IRecipeRegistry recipes, string currentPrefabId,
            string targetPrefabId, out bool targetHasRecipe)
        {
            bool currentHasRecipe;
            Dictionary<string, long> current = ProductionManager.GetPrefabBom(recipes, currentPrefabId,
                out currentHasRecipe);
            Dictionary<string, long> target = ProductionManager.GetPrefabBom(recipes, targetPrefabId,
                out targetHasRecipe);
            return NetRefitBom(current, target);
        }

        /// <summary>
        /// Player-facing station refit for <c>!factions station refit</c> — queues the components
        /// this node has to collect to be rebuilt as <paramref name="prefabId"/>. Gated by
        /// <see cref="RefitStationValidator"/>, which also computes the bill.
        ///
        /// <para><b>No Shipyard needed.</b> Unlike <see cref="QueueBuild"/>, a founded owned node is
        /// enough: <see cref="Tick"/>'s orphan pass services every owned node's queue, and the
        /// Shipyard gate lives in the hull-building verb rather than in the sweep. At a node that is
        /// also a yard the refit takes the one working slot while it collects, which is what
        /// <paramref name="force"/> is for.</para>
        ///
        /// <para><b>A second refit needs <paramref name="force"/>, and force never displaces a ship
        /// build.</b> While a refit is queued here, another one — including a repeat of the same
        /// design — is REFUSED (<see cref="StationRefitResult.RefitInProgress"/>) rather than
        /// silently restarting a half-collected rebuild; with force the queued one is refunded and
        /// replaced. A ship build mid-collection is never touched either way: it keeps everything it
        /// has collected, and what force acknowledges there is that the refit's rank
        /// (<see cref="FactionTuning.StationRefitPriority"/>) will take the working slot from it
        /// until the rebuild is paid for.</para>
        ///
        /// <para>A bill that comes out empty — the node already stands as everything the target asks
        /// for — completes immediately through the same path a collected one does, rather than
        /// leaving a zero-cost entry for the next sweep.</para>
        /// </summary>
        public static StationRefitResult QueueRefit(WorldModel world, FactionTuning tuning, string factionTag,
            string nodeId, string prefabId, bool force, out BuildQueueEntry queued, List<string> log)
        {
            queued = null;

            SpatialNode node;
            Dictionary<string, long> netBom;
            BuildQueueEntry blocking;
            StationRefitResult verdict = RefitStationValidator.Validate(world, factionTag, nodeId, prefabId,
                force, out node, out netBom, out blocking);
            if (verdict != StationRefitResult.Ok) return verdict;

            string previousPrefabId = node.StationPrefabId;

            // Override: refund and remove a refit already queued here. Nothing is destroyed -- its
            // collected components return to routable inventory.
            for (int i = node.BuildQueue.Count - 1; i >= 0; i--)
            {
                BuildQueueEntry existing = node.BuildQueue[i];
                if (!IsStationRefit(existing)) continue;
                RefundEntry(world, node, existing);
                node.BuildQueue.RemoveAt(i);
                world.RecordEvent(FactionEventKind.BuildOverridden, factionTag, null, node.Id, 1);
                if (log != null)
                    log.Add(factionTag + " BUILD_OVERRIDDEN_BY_PLAYER role=" + StationRefitRole
                            + " node=" + node.Id + " old_prefab=" + (existing.PrefabId ?? "(free)"));
            }

            var entry = new BuildQueueEntry
            {
                Id              = node.MintBuildEntryId(StationRefitRole),
                Role            = StationRefitRole,
                PrefabId        = prefabId,
                DestinedForSale = false,
                CreatedUtc      = world.UtcNow,
                ExpiresUtc      = world.UtcNow.AddHours(tuning == null ? 24.0 : tuning.MaxBuildDurationHours),
                Priority        = RolePriority(StationRefitRole, tuning),
            };
            long units = 0;
            foreach (KeyValuePair<string, long> kv in netBom)
            {
                entry.Bom[kv.Key] = kv.Value;
                units += kv.Value;
            }
            node.BuildQueue.Add(entry);
            queued = entry;

            world.RecordEvent(FactionEventKind.BuildQueued, factionTag, null, node.Id, 1);
            if (log != null)
                log.Add(factionTag + " STATION_REFIT_QUEUED @" + node.Id
                        + " " + (previousPrefabId ?? "(none)") + " -> " + prefabId
                        + " bom=" + units.ToString(CultureInfo.InvariantCulture));

            if (entry.Bom.Count == 0)
            {
                CompleteStationRefit(factionTag, world, node, entry, node.Id, log);
                node.BuildQueue.Remove(entry);
            }
            return StationRefitResult.Ok;
        }

        /// <summary>Drops every station rebuild at <paramref name="node"/> that its new owner is not
        /// allowed to build — a queued refit, and a completed one still waiting for the host to
        /// stand it up — refunding what a queued one had collected into node inventory.
        ///
        /// <para>Called when a node changes hands. A refit's target may be a design captured under a
        /// faction restriction, and that restriction does not travel with the node: leaving the
        /// entry in place would have the conqueror finish, and the host then stand up, a rival's
        /// private design. Only a restricted target is touched — a vanilla design or an
        /// <c>all</c>-scope capture is buildable by anyone and the rebuild carries on.</para>
        ///
        /// <para>The refund lands in the node's own inventory, which the new owner now holds. That is
        /// the same way every other thing the node was carrying changes hands, and the alternative —
        /// destroying it — would make conquest a way to burn a rival's components.</para></summary>
        public static int DropRefitsUnusableBy(WorldModel world, SpatialNode node, string newOwnerTag)
        {
            if (world == null || node == null) return 0;
            IOwnedPrefabCatalogue owned = world.PrefabCatalogue as IOwnedPrefabCatalogue;
            if (owned == null) return 0;

            int dropped = 0;
            for (int i = node.BuildQueue.Count - 1; i >= 0; i--)
            {
                BuildQueueEntry entry = node.BuildQueue[i];
                if (!IsStationRefit(entry)) continue;
                if (!IsRestrictedAwayFrom(owned, entry.PrefabId, newOwnerTag)) continue;
                RefundEntry(world, node, entry);
                node.BuildQueue.RemoveAt(i);
                dropped++;
            }

            if (IsRestrictedAwayFrom(owned, node.PendingStationPrefabId, newOwnerTag))
            {
                node.PendingStationPrefabId = null;
                dropped++;
            }

            if (dropped > 0)
                world.RecordEvent(FactionEventKind.BuildOverridden, newOwnerTag, null, node.Id, dropped);
            return dropped;
        }

        /// <summary>Whether <paramref name="prefabId"/> is a capture restricted to some faction other
        /// than <paramref name="tag"/>. An unrestricted design and an unknown id both answer false:
        /// the question is only ever asked to take something away.</summary>
        private static bool IsRestrictedAwayFrom(IOwnedPrefabCatalogue owned, string prefabId, string tag)
        {
            if (owned == null || string.IsNullOrEmpty(prefabId)) return false;
            string restrictedTo = owned.OwnerFactionTag(prefabId);
            return !string.IsNullOrEmpty(restrictedTo)
                   && !string.Equals(restrictedTo, tag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Closes out a paid-for refit: the components are spent, and what is left is an
        /// obligation the host discharges on the game thread by actually standing the new design up
        /// (<see cref="SpatialNode.PendingStationPrefabId"/>, persisted, so a restart between the
        /// two halves never strands one).
        ///
        /// <para>Does NOT remove the entry from the queue — the caller does, since the two call
        /// sites walk the queue differently. Reserved recipe inputs are refunded first for the same
        /// reason a finished hull refunds them: the bucket is scoped to this entry, and anything
        /// still in it when the entry goes would be destroyed.</para></summary>
        private static void CompleteStationRefit(string factionTag, WorldModel world, SpatialNode node,
            BuildQueueEntry entry, string logNodeId, List<string> log)
        {
            RefundReservedInputs(world, node, entry);
            node.PendingStationPrefabId = entry.PrefabId;
            world.RecordEvent(FactionEventKind.StationRefitReady, factionTag, null, logNodeId, 1);
            if (log != null)
                log.Add(factionTag + " STATION_REFIT_READY @" + logNodeId + " prefab=" + entry.PrefabId);
        }

        /// <summary>Evaluates this faction's <see cref="FactionState.FleetDoctrine"/> -- "own N of
        /// this prefab, M building at a time, here or anywhere" -- and places at most the shortfall
        /// as real <see cref="BuildQueueEntry"/>s through <see cref="QueueBuild"/>, the same primitive
        /// <c>!factions station queue build</c> uses. Ranked: a higher-priority line is offered the
        /// eligible shipyards first.
        ///
        /// <para><b>While a doctrine exists, this is the ONLY thing that puts a build on a yard.</b>
        /// <see cref="PostProactiveShipDemands"/> returns immediately and a <c>Ship:{role}</c> demand
        /// never creates an entry (see <see cref="FactionLifecycle.DoctrineActive"/>), so the target
        /// is a ceiling as well as a floor: a line at its target with work waiting reports
        /// <c>DOCTRINE_PRESSURE</c> and builds nothing. Growing the fleet is the player's call.</para>
        ///
        /// <para><b>The line re-arms from a live count; it never pre-spends.</b> Queueing N entries up
        /// front would have them compete with each other for the same components through
        /// <see cref="SpatialNode.BuildQueueReserved"/>, would let them stall out and be abandoned
        /// (<see cref="BuildQueueEntry.StalledSeconds"/> / <see cref="FactionTuning.BuildNoProgressHours"/>),
        /// and is precisely not "one at a time". So every pass counts what exists, counts what is in
        /// progress, and asks for the difference.</para>
        ///
        /// <para><b>What counts as "have".</b> Parked <see cref="SpatialNode.ShipStock"/> in scope
        /// less the store-listed slice (<see cref="CountDoctrineParkedStock"/>), PLUS the hulls of the
        /// same prefab flying on this faction's <see cref="FactionState.PendingFleets"/>
        /// (<see cref="CountDoctrineHullsInFlight"/>). The in-flight term is what makes this a fleet
        /// size rather than a shelf level: without it a doctrine of four haulers reads zero the moment
        /// all four are dispatched and orders four more. Both are model counts, never an SE entity
        /// sweep -- the Assets tab already runs one for player-built grids and is careful to do it
        /// only while someone has the tab open, and a manager cadence must not add a second.</para>
        ///
        /// <para><b>What counts as "in progress"</b> is any FLEET queue entry in scope for the same
        /// prefab, whoever queued it -- a hand-queued hull of the same type satisfies the same
        /// threshold, so counting only the line's own would over-build. A shelf build
        /// (<see cref="BuildQueueEntry.DestinedForSale"/>) is excluded: its hull is reserved for sale
        /// and never reaches the fleet.</para>
        ///
        /// <para><b>Why a line places at most one build per node PER PASS.</b> Placement walks the
        /// eligible yards once and stops at <c>want</c>, so a line never opens two entries at one yard
        /// in a single evaluation. It may still reach its full <see cref="DoctrineLine.MaxConcurrent"/>
        /// at fewer yards than that across passes, because <c>building</c> rises as entries are
        /// placed and is what the shortfall is measured against -- the concurrency is a property of
        /// the line, not of how many yards it has. The per-line log line reports the placement count
        /// so that is visible rather than mysterious.</para>
        ///
        /// <para><b>Route feasibility is the one surviving filter on the hull.</b> The player chose the
        /// prefab, so the cargo and Dps floors are advisory; but a yard the hull cannot fly out of
        /// (<see cref="CanFlyFromYard"/>) would produce a stranded grid, so it is not placeable for
        /// that line. A line with no placeable yard for that reason alone says so once, through
        /// <c>DOCTRINE_LINE_BLOCKED ... reason=route</c>.</para></summary>
        private void ServiceDoctrine(FactionState st, WorldModel world, FactionTuning tuning, List<string> log)
        {
            if (st.FleetDoctrine.Count == 0) return;

            foreach (DoctrineLine line in st.DoctrineByRank())
            {
                if (line.Paused) continue;
                if (string.IsNullOrEmpty(line.PrefabId) || line.TargetCount <= 0) continue;
                // A line for a hull that is banned or over the performance ceiling places nothing.
                // Skipped here rather than rejected per yard by QueueBuild, which would log once per
                // pass forever; the Fleet window shows why (DoctrineLineReport.Exclusion).
                // A line names a design, not a revision: it counts every version and builds the
                // latest this faction may use.
                string buildId = PrefabVersions.LatestFor(world.PrefabCatalogue, line.PrefabId, st.Tag);
                if (world.PrefabExclusion(buildId) != null) continue;

                long flying = CountDoctrineHullsInFlight(st, line, world.PrefabCatalogue);
                long repair = CountDoctrineHullsInRepair(world, st.Tag, line);
                long have = CountDoctrineParkedStock(world, st.Tag, line) + flying + repair;
                // Through the shared helper, not a second count in this loop: the Fleet window shows
                // this number beside the line, and a window counting differently would say a line was
                // busy while the evaluator placed another build.
                int building = CountDoctrineBuildsInProgress(world, st.Tag, line);
                int routeBlocked = 0;
                var placeable = new List<SpatialNode>();

                foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
                {
                    if (!DoctrineScopeCovers(line, node.Id)) continue;

                    int shipyardLevel;
                    if (node.ShipyardPaused
                        || !node.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out shipyardLevel)
                        || shipyardLevel < 1)
                        continue;

                    // The hull the player named must be able to leave this yard. Counted rather than
                    // skipped silently: "no placeable yard" and "no yard this hull can fly from" are
                    // different problems and only one of them is fixed by building a shipyard.
                    if (!CanFlyFromYard(world, tuning, node, buildId)) { routeBlocked++; continue; }

                    placeable.Add(node);
                }

                // Deterministic placement order: world.Graph.OwnedBy's enumeration order is not a
                // contract, and the sim and the host must place the same build given the same world.
                placeable.Sort(CompareNodeById);

                int concurrency = line.MaxConcurrent > 0 ? line.MaxConcurrent : 1;
                long shortfall = line.TargetCount - have - building;
                int want = (int)Math.Min(shortfall, concurrency - building);
                if (want > placeable.Count) want = placeable.Count;

                int placed = 0;
                for (int i = 0; i < want; i++)
                {
                    Dictionary<string, long> bom;
                    PlayerQueueResult result = QueueBuild(world, tuning, st.Tag, placeable[i].Id,
                        null, buildId, out bom, log);
                    if (result == PlayerQueueResult.Ok) { placed++; continue; }

                    // A rejection is a property of the LINE (unknown prefab, a hull that fits no role), not
                    // of this node, so retrying the same line at the next node would repeat it once
                    // per shipyard. Report it once and move to the next line.
                    log.Add(st.Tag + " DOCTRINE_BUILD_REJECTED line=" + line.Id
                            + " prefab=" + buildId
                            + " node=" + placeable[i].Id + " reason=" + result);
                    break;
                }

                string scope = string.IsNullOrEmpty(line.ScopeNodeId) ? "any" : line.ScopeNodeId;
                string signature = "line=" + line.Id
                    + " prefab=" + buildId
                    + " scope=" + scope
                    + " have=" + have
                    + " flying=" + flying
                    + " repair=" + repair
                    + " building=" + building
                    + " target=" + line.TargetCount
                    + " concurrency=" + concurrency
                    + " placeable=" + placeable.Count
                    + " placed=" + placed;
                // Log-on-change, same rule as MINE_AUTO_SCAN: a satisfied line is re-evaluated every
                // Shipyard tick and must stay silent while nothing about it moves.
                if (placed > 0 || !string.Equals(signature, line.LastLogSignature, StringComparison.Ordinal))
                {
                    line.LastLogSignature = signature;
                    log.Add(st.Tag + " DOCTRINE_BUILD " + signature);
                }

                // Its own log-on-change, so a line blocked for a day does not re-announce it every
                // time an unrelated count moves. Only when the route check is the WHOLE reason there
                // is nowhere to place: a line whose yards are merely busy is not blocked, it is
                // waiting.
                string blocked = placeable.Count == 0 && routeBlocked > 0
                    ? "line=" + line.Id + " prefab=" + line.PrefabId + " reason=route"
                    : null;
                if (!string.Equals(blocked, line.LastBlockedLogSignature, StringComparison.Ordinal))
                {
                    line.LastBlockedLogSignature = blocked;
                    if (blocked != null) log.Add(st.Tag + " DOCTRINE_LINE_BLOCKED " + blocked);
                }
            }
        }

        /// <summary>Whether <paramref name="line"/> counts and builds at <paramref name="nodeId"/>:
        /// true for an unscoped line, and for a scoped one only at its own node. One predicate so
        /// counting and placement cannot drift apart.
        ///
        /// <para>Internal rather than private because <see cref="FleetDoctrineReport"/> attributes
        /// demand pressure by the same scope rule. A second implementation there would let the
        /// window blame a line for pressure the evaluator would never answer.</para></summary>
        internal static bool DoctrineScopeCovers(DoctrineLine line, string nodeId)
        {
            return string.IsNullOrEmpty(line.ScopeNodeId)
                || string.Equals(nodeId, line.ScopeNodeId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reports work waiting on hulls that the doctrine has decided not to answer:
        /// <c>DOCTRINE_PRESSURE role=... node=... demands=... line=... have=.../target=...</c>. The
        /// counterpart to the silence a gate produces -- without it, "no build was queued" and "no
        /// demand was posted" look identical in the log, and the ceiling is exactly the case where
        /// they differ.
        ///
        /// <para><paramref name="demands"/> is the whole (node, role) demand group, not one entry: a
        /// node can carry a destination-keyed demand per blocked material vector, and the number of
        /// routes waiting is the size of the pressure.</para>
        ///
        /// <para><c>line=</c> names the highest-ranked unpaused line that could have built for this
        /// role here, with what it currently owns against what it wants, or <c>none</c> when the
        /// doctrine has nothing for this role in this scope -- the silent case
        /// (docs/fleet-doctrine-plan.md: a role with no line is not built), which this line is what
        /// makes visible. Log-on-change per (role, node), the same rule as DOCTRINE_BUILD; the
        /// signature lives on <see cref="FactionState.DoctrinePressureLogSignatures"/> because a
        /// (role, node) pair is not an object that outlives the tick.</para></summary>
        private static void LogDoctrinePressure(FactionState st, WorldModel world, string role,
            string nodeId, int demands, List<string> log)
        {
            DoctrineLine line = HighestRankedLineFor(st, role, nodeId, world.PrefabCatalogue);
            string signature = "role=" + role
                + " node=" + nodeId
                + " demands=" + demands.ToString(CultureInfo.InvariantCulture)
                + " line=" + (line != null ? line.Id : "none")
                + (line != null
                    ? " have=" + CountDoctrineHave(world, st, line) + "/" + line.TargetCount
                    : "");

            string key = role + "|" + nodeId;
            string previous;
            if (st.DoctrinePressureLogSignatures.TryGetValue(key, out previous)
                && string.Equals(previous, signature, StringComparison.Ordinal)) return;
            st.DoctrinePressureLogSignatures[key] = signature;
            log.Add(st.Tag + " DOCTRINE_PRESSURE " + signature);
        }

        /// <summary>The line that would build for <paramref name="role"/> at <paramref name="nodeId"/>:
        /// highest-ranked, unpaused, whose scope covers the node and whose hull is suitable for the
        /// role (<see cref="ShipPool.IsSuitableForRole"/>). A line names a hull, not a role, so a
        /// combat line answers Raid and Patrol pressure both. Null when the doctrine has none --
        /// which is a real answer, not an error: a role with no line is a role the faction has decided
        /// not to own.
        ///
        /// <para>Internal rather than private because <see cref="FleetDoctrineReport"/> attributes
        /// every pressure group with it, so the window names the same line the log line does.</para></summary>
        internal static DoctrineLine HighestRankedLineFor(FactionState st, string role, string nodeId,
            IPrefabCatalogue cat)
        {
            foreach (DoctrineLine line in st.DoctrineByRank())
            {
                if (line.Paused) continue;
                if (string.IsNullOrEmpty(line.PrefabId)
                    || !ShipPool.IsSuitableForRole(role, cat, line.PrefabId)) continue;
                if (!DoctrineScopeCovers(line, nodeId)) continue;
                return line;
            }
            return null;
        }

        /// <summary>Whether <paramref name="prefabId"/> could actually leave <paramref name="yard"/> --
        /// a degenerate same-point <see cref="ShipPool.RouteEnvironment"/> at the yard, exactly the
        /// check <see cref="PostProactiveShipDemands"/> makes about local stock and the one
        /// <see cref="FilterZoneFeasible"/> applies to a selection pool. The doctrine keeps this and
        /// drops every other filter: the player chose the hull, but a hull that cannot fly out of the
        /// yard it is built at is a stranded grid and a wasted BOM.
        ///
        /// <para>Internal rather than private because the Build dialog's pick-list reports the same
        /// judgement per hull (<see cref="BuildOptionsCatalogue"/>). A second implementation there
        /// would be a second answer to "can this leave here", and the two would drift.</para></summary>
        internal static bool CanFlyFromYard(WorldModel world, FactionTuning tuning, SpatialNode yard, string prefabId)
        {
            if (world == null || world.PrefabCatalogue == null || yard == null) return true;
            ShipPool.RouteEnvironment localRoute = ShipPool.RouteEnvironment.From(
                world.Planets, yard.Position, yard.Position, tuning.MinDryTwr);
            return ShipPool.IsRouteFeasible(world.PrefabCatalogue, prefabId, localRoute);
        }

        /// <summary>Whether <paramref name="prefabId"/> has a build recipe at all -- exactly the check
        /// <see cref="QueueBuild"/> makes before it will create an entry. Public because
        /// <c>ProductionManager.GetPrefabBom</c> is <c>internal</c> to this assembly and the host has
        /// to make the same judgement one layer up: a doctrine line must reject an unbuildable
        /// prefab at CREATION time, or it sits in the player's list looking healthy and silently never
        /// places anything.</summary>
        public static bool HasBuildRecipe(IRecipeRegistry recipes, string prefabId)
        {
            if (recipes == null || string.IsNullOrEmpty(prefabId)) return false;
            bool hasRecipe;
            ProductionManager.GetPrefabBom(recipes, prefabId, out hasRecipe);
            return hasRecipe;
        }

        /// <summary>How many of <paramref name="line"/>'s prefab <paramref name="factionTag"/> holds
        /// PARKED within the line's scope -- the shelf half of "have". Store-listed stock is always
        /// subtracted: a hull reserved for a player to buy is not a hull the fleet has, and making
        /// that optional (as the retired Build goal did) only ever let a doctrine count the same hull
        /// twice.
        ///
        /// <para>Public and shared: the player-facing listing shows this number beside the line, and a
        /// listing that computed it a second way could tell a player a line was satisfied while the
        /// manager kept building. Pair it with <see cref="CountDoctrineHullsInFlight"/> -- parked alone
        /// is not what the line targets.</para></summary>
        public static long CountDoctrineParkedStock(WorldModel world, string factionTag, DoctrineLine line)
        {
            if (world == null || line == null || string.IsNullOrEmpty(line.PrefabId)) return 0;
            long have = 0;
            foreach (SpatialNode node in world.Graph.OwnedBy(factionTag))
            {
                if (!DoctrineScopeCovers(line, node.Id)) continue;
                have += DoctrineStockAt(node, line, world.PrefabCatalogue);
            }
            return have;
        }

        /// <summary>This node's contribution to a line's parked count: stock of every version of
        /// the line's design, less the store-reserved slice.</summary>
        private static long DoctrineStockAt(SpatialNode node, DoctrineLine line, IPrefabCatalogue catalogue)
        {
            if (!(catalogue is IPrefabVersionCatalogue)) return UnlistedStockAt(node, line.PrefabId);
            long have = 0;
            foreach (KeyValuePair<string, int> kv in node.ShipStock)
                if (PrefabVersions.SameFamily(catalogue, kv.Key, line.PrefabId))
                    have += UnlistedStockAt(node, kv.Key);
            return have;
        }

        /// <summary>Stock of <paramref name="prefabId"/> parked at <paramref name="node"/>, less the
        /// slice reserved for the store. Never negative -- the <c>ListedForSale &lt;= ShipStock</c>
        /// invariant makes that unreachable, and clamping is cheaper than depending on it here.
        ///
        /// <para>Public because the fleet census (<see cref="FleetDoctrineReport"/>) counts every
        /// prefab a faction holds by the same rule a line counts its own by. A hull on the shelf is
        /// never "ours" for doctrine purposes (docs/fleet-doctrine-plan.md, decision 6), and a census
        /// that included it would disagree with every line beside it.</para></summary>
        public static long UnlistedStockAt(SpatialNode node, string prefabId)
        {
            if (node == null || string.IsNullOrEmpty(prefabId)) return 0;
            int stock;
            if (!node.ShipStock.TryGetValue(prefabId, out stock) || stock <= 0) return 0;
            int listed;
            if (node.ListedForSale.TryGetValue(prefabId, out listed)) stock -= listed;
            return stock > 0 ? stock : 0;
        }

        /// <summary>Builds of <paramref name="line"/>'s hull already in progress in its scope,
        /// whoever queued them -- a hand-queued hull counts, exactly as it always did. The third
        /// term of the line's shortfall, and the one the Fleet window shows as "building n".
        ///
        /// <para>Public and shared for the reason <see cref="CountDoctrineHave"/> is: the evaluator
        /// and the window must not be able to disagree about whether a line is already working.</para></summary>
        public static int CountDoctrineBuildsInProgress(WorldModel world, string factionTag, DoctrineLine line)
        {
            if (world == null || line == null || string.IsNullOrEmpty(line.PrefabId)) return 0;
            int building = 0;
            foreach (SpatialNode node in world.Graph.OwnedBy(factionTag))
            {
                if (!DoctrineScopeCovers(line, node.Id)) continue;
                foreach (BuildQueueEntry e in node.BuildQueue)
                {
                    // A shelf build of the same hull is not this line's progress: its output is
                    // reserved into ListedForSale, which CountDoctrineParkedStock subtracts straight
                    // back out. Counted here the line would wait out a build it gets nothing from.
                    if (e.DestinedForSale) continue;
                    if (PrefabVersions.SameFamily(world.PrefabCatalogue, e.PrefabId, line.PrefabId)) building++;
                }
            }
            return building;
        }

        /// <summary>Hulls of <paramref name="line"/>'s prefab currently FLYING on
        /// <paramref name="st"/>'s pending fleets -- the half of "have" that makes a doctrine a fleet
        /// size rather than a shelf level. Without it a doctrine of four haulers reads zero the moment
        /// all four are dispatched and orders four more.
        ///
        /// <para>Counts a plain convoy as <see cref="PendingFleetAction.ConvoySize"/> when its
        /// <see cref="PendingFleetAction.ShipPrefabId"/> matches, and a mixed
        /// <see cref="PendingFleetAction.Squadron"/> member by member -- the same two shapes
        /// <c>ShipPool.Return</c> credits back. A resolved or intercepted fleet is excluded for the
        /// same reason <see cref="CentralBankSnapshot.InFlightShipCount"/> excludes it: its hulls have
        /// already gone back to <see cref="SpatialNode.ShipStock"/> and would be counted twice.</para>
        ///
        /// <para>Scoped by where the hull comes HOME to (<see cref="PendingFleetAction.ReturnNodeId"/>,
        /// falling back to <see cref="PendingFleetAction.OriginNodeId"/> exactly as the terminal leg
        /// does), not by where it is going: a scoped line counts the stock that will land back in its
        /// own scope.</para></summary>
        public static long CountDoctrineHullsInFlight(FactionState st, DoctrineLine line,
                                                      IPrefabCatalogue catalogue = null)
        {
            if (st == null || line == null || string.IsNullOrEmpty(line.PrefabId)) return 0;
            long flying = 0;
            for (int i = 0; i < st.PendingFleets.Count; i++)
            {
                PendingFleetAction fleet = st.PendingFleets[i];
                if (fleet.Resolved || fleet.Intercepted || fleet.IsBench) continue;
                if (!DoctrineScopeCovers(line, fleet.ReturnNodeId ?? fleet.OriginNodeId)) continue;

                if (fleet.Squadron != null && fleet.Squadron.Count > 0)
                {
                    for (int m = 0; m < fleet.Squadron.Count; m++)
                        if (PrefabVersions.SameFamily(catalogue, fleet.Squadron[m], line.PrefabId))
                            flying++;
                    continue;
                }

                if (PrefabVersions.SameFamily(catalogue, fleet.ShipPrefabId, line.PrefabId))
                    flying += Math.Max(1, fleet.ConvoySize);
            }
            return flying;
        }

        /// <summary>Hulls of <paramref name="line"/>'s prefab in <see cref="SpatialNode.DamagedHulls"/>
        /// at owned nodes in the line's scope, waiting for parts or with their parts in -- the third
        /// half of "have". A hull in the yard is coming back to stock, so a line that did not count
        /// it would build its replacement.
        ///
        /// <para>Every queued hull counts, whatever state its yard is in: it is the faction's hull,
        /// and a damaged hull is in <see cref="SpatialNode.DamagedHulls"/> or in
        /// <see cref="SpatialNode.ShipStock"/>, never both, so nothing is counted twice. Same
        /// per-node rule as <see cref="HullDamage.CountRepairs"/>, which the Fleet window's repair
        /// column reads.</para></summary>
        public static long CountDoctrineHullsInRepair(WorldModel world, string factionTag, DoctrineLine line)
        {
            if (world == null || line == null || string.IsNullOrEmpty(line.PrefabId)) return 0;
            int waiting = 0, repairing = 0;
            foreach (SpatialNode node in world.Graph.OwnedBy(factionTag))
            {
                if (node.DamagedHulls.Count == 0 || !DoctrineScopeCovers(line, node.Id)) continue;
                HullDamage.CountRepairs(node, line.PrefabId, ref waiting, ref repairing, world.PrefabCatalogue);
            }
            return waiting + repairing;
        }

        /// <summary>Everything <paramref name="line"/> counts as owned: parked in scope, flying home
        /// to it, and in repair in it. The one number the evaluator compares against
        /// <see cref="DoctrineLine.TargetCount"/>, exposed so a listing cannot compute it a second
        /// way.</summary>
        public static long CountDoctrineHave(WorldModel world, FactionState st, DoctrineLine line)
        {
            string tag = st != null ? st.Tag : null;
            return CountDoctrineParkedStock(world, tag, line)
                 + CountDoctrineHullsInFlight(st, line, world != null ? world.PrefabCatalogue : null)
                 + CountDoctrineHullsInRepair(world, tag, line);
        }

        private static int CompareNodeById(SpatialNode a, SpatialNode b)
        {
            return string.CompareOrdinal(a.Id, b.Id);
        }

        /// <summary>Returns any raw recipe input <see cref="ProductionManager.ServiceShipBuildReservations"/>
        /// had reserved into <paramref name="entry"/>'s own isolated bucket back to
        /// <paramref name="node"/>'s routable inventory, and clears the entry's reservation/progress
        /// state. Called whenever <paramref name="entry"/> is about to leave <see cref="SpatialNode.BuildQueue"/>
        /// for any reason (expired, removed, or completed) — the bucket is scoped to this entry alone, so
        /// once the entry is gone any stock still sitting in it would otherwise be silently destroyed.</summary>
        private static void RefundReservedInputs(WorldModel world, SpatialNode node, BuildQueueEntry entry)
        {
            foreach (var kv in entry.ReservedInputs)
                world.AddNodeInventory(node.Id, kv.Key, kv.Value);
            entry.ReservedInputs.Clear();
            entry.RecipeProgress.Clear();
        }

        /// <summary>Credits one freshly-built ship into the unified pool. A build raised for the
        /// shelf also reserves it via <see cref="SpatialNode.ListedForSale"/>, so fleet dispatch
        /// cannot draw it down. Keyed on the build's own purpose rather than its role name — with
        /// the store roles retired the role no longer says who the hull is for.</summary>
        private static void CreditBuiltShip(SpatialNode node, string role, string prefabId,
            bool destinedForSale)
        {
            string key = prefabId ?? "";
            int stock;
            node.ShipStock.TryGetValue(key, out stock);
            node.ShipStock[key] = stock + 1;

            if (destinedForSale)
            {
                int reserved;
                node.ListedForSale.TryGetValue(key, out reserved);
                node.ListedForSale[key] = reserved + 1;
            }
        }

        /// <summary>
        /// Posts Ship:{role} demands so the build pipeline stays warm without requiring a failed
        /// brain action to trigger it. This runs every manager tick.
        ///
        /// <para>Trade/cargo is checked PER Shipyard node, not network-wide: "any cargo-suitable ship
        /// anywhere in the network" is not a meaningful floor once a faction has Shipyards spread
        /// across multiple planets/zones -- e.g. an Ion-only hauler stranded in DeepSpace would
        /// satisfy that check while leaving a Surface Shipyard with nothing it could ever dispatch
        /// locally. Each Shipyard node's own environment is checked independently via a degenerate
        /// <see cref="ShipPool.RouteEnvironment"/> (same "can a hull survive being built/used here"
        /// shape <see cref="SelectPrefabBom"/>'s own buildRoute already uses), so a freshly-placed,
        /// zone-correct Shipyard gets its own demand the very next tick after it's granted, instead
        /// of waiting on a single network-wide "at least one exists" gate that a zone-mismatched ship
        /// elsewhere in the network could satisfy forever. Still a low floor per node ("at least one
        /// reachable cargo hull exists for THIS zone") -- a peacetime faction still doesn't stockpile
        /// beyond that, just correctly scoped per environment instead of per network.</para>
        ///
        /// <para>Raid and Patrol stay a single combined, network-wide pooled combat-ship target (see
        /// PostCombatShipDemand) since MilitaryPosture is "how much fleet do we want," not "how much
        /// offense vs. defense," and combat squadrons are already route-gated per-dispatch at consume
        /// time -- pooling combat power network-wide is a deliberate, separate design decision from
        /// cargo reachability.</para>
        /// </summary>
        /// <summary>
        /// Asks for a Shipyard in every region the faction holds founded stations in and no yard —
        /// the proactive half of Shipyard coverage. The dispatch-failure poster
        /// (<see cref="ShipPool.MaybePostLocalShipyardInvestment"/>) only ever fires for a leg that
        /// could not fly, so a region whose legs all fly on ferried-in hulls, or that generates no
        /// legs of its own, never asked; and since <see cref="PostProactiveShipDemands"/> posts one
        /// Trade demand PER yard, a region without one has no standing build order and nothing pulls
        /// components toward it. Live 2026-09-05: FCTM held six nests, two yards both in one of them,
        /// and the three far nests saw one haul an hour between them.
        ///
        /// <para><b>Coverage only, never saturation.</b> <see cref="ShipyardCoverage.Wants"/> also
        /// says yes to a region whose yards are all working; that arm stays with the dispatch-failure
        /// path, which has a leg as evidence that the region's capacity is short. Presence alone is
        /// evidence of nothing beyond "no yard here", so this posts only for
        /// <see cref="ShipyardCensus.Yards"/> == 0. Open deep space is no region
        /// (<see cref="NodeRegion.None"/>) and is never asked for.</para>
        ///
        /// <para>Same urgency and TTL as the dispatch-failure poster, and the same register key, so a
        /// region both paths ask for holds one request. Re-posted every tick while the gap stands;
        /// the register's TTL retires it once the yard lands.</para>
        /// </summary>
        private static void PostRegionCoverageRequests(FactionState st, WorldModel world, FactionTuning tuning, List<string> log)
        {
            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));
            if (owned.Count == 0) return;
            if (!FactionLifecycle.CanPostInvestmentFor(st, NodeCapabilityKind.Shipyard)) return;

            foreach (KeyValuePair<NodeRegion, ShipyardCensus> kv in ShipyardCoverage.Census(owned, tuning))
            {
                ShipyardCensus c = kv.Value;
                if (c.Yards > 0 || !ShipyardCoverage.Wants(c, owned)) continue;

                bool isNew = world.Investments.Post(kv.Key.Scope(new InvestmentRequest
                {
                    FactionTag = st.Tag,
                    Cap        = NodeCapabilityKind.Shipyard,
                    Urgency    = 0.8,
                    ExpiresUtc = world.UtcNow.Add(tuning.SharedPostingTtl(120.0)),
                }));
                if (isNew)
                    log.Add(st.Tag + " INVEST_REQUEST Shipyard " + kv.Key.Describe()
                            + " yards=0/0 nodes=" + c.Nodes.ToString(CultureInfo.InvariantCulture)
                            + " (coverage: founded stations, no yard)");
            }
        }

        private static void PostProactiveShipDemands(FactionState st, WorldModel world, FactionTuning tuning, List<string> log)
        {
            // A faction that says what its fleet is made of does not get told what it is short of.
            // Every branch below -- the per-yard Trade floor, the posture combat target, the defence
            // and terror Dps budgets -- exists to pick a hull the player has already picked, and each
            // one would add a demand that ServiceDoctrine is then forbidden to answer.
            if (FactionLifecycle.DoctrineActive(st)) return;

            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));

            var shipyardNodes = new List<SpatialNode>();
            string fallbackNodeId = null;
            foreach (SpatialNode n in owned)
            {
                if (fallbackNodeId == null && n.StableStationId != 0)
                    fallbackNodeId = n.Id;
                int level;
                // A paused yard is not a place a Ship demand can be serviced: it never creates or
                // services an entry, so a demand posted there would sit unfillable while suppressing
                // nothing and pulling supply toward a station that will not use it.
                if (!n.ShipyardPaused
                    && n.Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level) && level >= 1)
                    shipyardNodes.Add(n);
            }

            if (shipyardNodes.Count > 0)
            {
                foreach (SpatialNode yard in shipyardNodes)
                {
                    ShipPool.RouteEnvironment localRoute = ShipPool.RouteEnvironment.From(
                        world.Planets, yard.Position, yard.Position, tuning.MinDryTwr);
                    // "Local" is the yard's REGION, not only its environment. The route filter alone
                    // was written for zone feasibility -- an Ion hull stranded in deep space must not
                    // satisfy a Surface yard -- and in deep space it admits every hull, so a far-nest
                    // yard read "stocked" while the faction's only cargo hulls sat in its home nest
                    // (FCTM N046, first yard outside Cinder, never posted a build order). A yard in
                    // no region (open space) keeps the whole network, as before.
                    bool hasLocalTradeStock = ShipPool.NetworkHasSuitable(
                        StockPoolFor(yard, owned), ActionKind.Trade, world.PrefabCatalogue, localRoute);
                    if (!hasLocalTradeStock)
                        PostShipDemand(st.Tag, "Trade", yard.Id, world, log,
                            minCargoLiters: 1, requiredPlanetId: yard.PlanetId, requiredZone: yard.Zone);
                }
            }
            else if (fallbackNodeId != null)
            {
                // Bootstrap: no Shipyard anywhere yet. One demand at any station triggers Tick's
                // "no Shipyard here" branch, which posts the unrestricted Shipyard InvestmentRequest
                // that gets this whole loop started -- see the chicken-and-egg note on Tick's caller.
                bool hasTradeStock = ShipPool.NetworkHasSuitable(owned, ActionKind.Trade, world.PrefabCatalogue);
                if (!hasTradeStock)
                    PostShipDemand(st.Tag, "Trade", fallbackNodeId, world, log,
                        minCargoLiters: 1, requiredPlanetId: null, requiredZone: null);
            }

            // Raid/Patrol: one network-wide pooled combat target, posted at one node -- the yard
            // with the most free slots rather than whichever came first in graph order. A demand
            // creates its build entry at the node it was posted at, so posting every combat order at
            // one yard queues them all behind that yard's single slot while the faction's other
            // yards hold nothing. Live 2026-09-15: EXP held 8 entries at N012 and six idle yards.
            string demandNodeId = shipyardNodes.Count > 0
                ? EmptiestYard(shipyardNodes).Id
                : fallbackNodeId;
            if (demandNodeId != null)
            {
                // Raid and Patrol are the same combat-suitable pool now (see ShipPool.IsSuitableFor) —
                // one count, not two summed together (that would double-count every combat ship).
                int combatStock = ShipPool.NetworkSuitableCount(owned, ActionKind.Raid, world.PrefabCatalogue);

                // Combined target scales with MilitaryPosture (0 at posture=0, matching today's
                // "any nonzero stock" floor exactly since MusterStockBaseline defaults to 1).
                double posture = Math.Max(0.0, Math.Min(1.0, st.MilitaryPosture));
                int combatTarget = tuning.MusterStockBaseline
                    + (int)Math.Round(tuning.MusterStockMaxBonus * posture);
                // A terror campaign carries its own fleet requirement -- the one combat channel that
                // is not posture-derived, for a faction that never Musters (FAC-268).
                combatTarget += TerrorCampaigns.WantedHulls(st);
                // Ground has to be covered. The two constants above are one number for every estate,
                // so a 122-node holding and a one-node outpost wanted the same five hulls; posture
                // still decides how much of that cover the faction is currently paying for.
                if (tuning.MusterStockPerNode > 0)
                    combatTarget += (int)Math.Round(owned.Count * tuning.MusterStockPerNode * posture);
                // And what the fight ledger says is actually coming, counted in hulls of the
                // strongest thing this catalogue can build -- the conservative conversion, so the
                // term reads as "at least this many" rather than inflating on a weak pool.
                long defenceHullDps = DefenceBudget.WantedDps(st, tuning, world.UtcNow);
                if (defenceHullDps > 0)
                {
                    long perHull = ShipPool.LargestCombatDpsForRole(world.PrefabCatalogue, "Raid");
                    if (perHull > 0)
                        combatTarget += (int)Math.Ceiling(defenceHullDps / (double)perHull);
                }
                if (tuning.MusterStockCap > 0 && combatTarget > tuning.MusterStockCap)
                    combatTarget = tuning.MusterStockCap;

                // The hull count is the floor; the campaign's real requirement is a DPS budget
                // (FAC-271), measured against the strongest squadron one node can field -- one big
                // hull or a swarm of small ones meet it alike, up to what a squadron carries. The
                // gap is posted as a power floor on the next hull: shared across the open slots
                // while the roster is filling (a swarm of mid hulls), and once it is full, the
                // weakest hull is what the new one has to beat -- a real fleet's shape, escorts
                // first and a flagship when the escorts are not enough.
                //
                // Two budgets feed one floor and the larger wins: the campaign's (an antagonist
                // sizing its offence against the region it means to hit) and the defensive one
                // every faction carries -- the strongest attacker its own fight ledger remembers.
                // The posted line names which set the floor.
                //
                // The floor is NOT clamped to the strongest hull the role can build. A gap wider
                // than any one hull is the signal that the answer is several, and SelectPrefabBom
                // reads exactly that: a floor no candidate clears switches its ranking to Dps per
                // BOM unit. Clamping here erased the signal before it arrived -- the floor came out
                // equal to the biggest hull's Dps, that hull cleared it, and the pool collapsed to
                // the one warship in the catalogue with the worst power per unit built.
                string powerBudget;
                double powerFloor = CombatPowerFloor(st, world, tuning, out powerBudget);

                // One post, not two. Both asks share a register key, so posting the bare hull-count
                // demand first merged the power floor into it silently -- the floor still reached the
                // entry (Merge keeps the larger MinDps) but `isNew` was false by then and the line
                // naming it was never written. The evidence an operator reads a build order by must
                // not depend on which of two conditions happened to fire first.
                if (combatStock < combatTarget || powerFloor > 0)
                    PostShipDemand(st.Tag, "Raid", demandNodeId, world, log,
                                   minDps: powerFloor, budget: powerBudget);
            }

            PostGarrisonDemands(st, world, tuning, log);
        }

        /// <summary>The combat power floor this faction's next Raid hull has to clear, and which
        /// budget set it — the campaign's (<see cref="TerrorCampaigns.WantedDps"/>) or the defensive
        /// one every faction carries (<see cref="DefenceBudget.WantedDps"/>), larger wins. 0 with a
        /// null budget when nothing is owed, or when the roster is already made of the strongest hull
        /// the role can build.
        ///
        /// <para><b>A property of the faction, not of a yard.</b> The proactive poster asks for it at
        /// one node — the emptiest yard — but a Ship:Raid demand is also posted at every origin whose
        /// dispatch failed, and those outnumber it. Read from one place so a build cannot be chosen
        /// with a floor at one node and without one at the next: a floorless combat demand resolves on
        /// the posture blend, which at a high posture takes the strongest hull in the pool, which is
        /// exactly the flagship the floor's own ranking exists to rule out.</para>
        ///
        /// <para>Deliberately NOT clamped to the strongest buildable hull — see
        /// <see cref="SelectPrefabBom"/>: a gap no single hull can close is the signal that the
        /// answer is several of them.</para></summary>
        internal static double CombatPowerFloor(FactionState st, WorldModel world, FactionTuning tuning,
            out string budget)
        {
            budget = null;
            if (st == null || world == null || tuning == null) return 0;
            long terrorDps = TerrorCampaigns.WantedDps(st);
            long defenceDps = DefenceBudget.WantedDps(st, tuning, world.UtcNow);
            long wantedDps = Math.Max(terrorDps, defenceDps);
            if (wantedDps <= 0) return 0;

            var owned = new List<SpatialNode>(world.Graph.OwnedBy(st.Tag));
            ShipPool.SquadronStrength best = ShipPool.BestSquadron(owned, world.PrefabCatalogue, tuning.RaidSquadronMaxFighters);
            if (best.Dps >= wantedDps) return 0;

            long ceiling = ShipPool.LargestCombatDpsForRole(world.PrefabCatalogue, "Raid");
            if (ceiling > 0 && best.OpenSlots == 0 && best.WeakestDps >= ceiling) return 0;   // as strong as it gets

            // FAC-467: a squadron's worth of combat hulls parked beyond the best squadron. Those spares
            // are what earlier builds toward this floor produced and the squadron could not take in:
            // past the cap a hull raises best.Dps only by displacing one in it, and an open fighter slot
            // that only small hulls fill stays open however many large ones are built (FCTM: 437
            // DS_Sentinel, one squadron of one). Without this a budget beyond any squadron posted a
            // build every yard tick, forever: SPRT parked 3,388 combat hulls and FCTM 437 in five days.
            // Measured against the squadron's capacity, not the hulls it fields, so a roster that
            // cannot fill its slots still has a limit. Hulls lost bring the reserve down and the floor back.
            int capacity = 1 + Math.Max(0, tuning.RaidSquadronMaxFighters);
            if (best.Hulls > 0
                && ShipPool.NetworkSuitableCount(owned, ActionKind.Raid, world.PrefabCatalogue) - best.Hulls >= capacity)
                return 0;

            double gap = wantedDps - best.Dps;
            double floor = best.OpenSlots > 0 ? gap / best.OpenSlots : Math.Max(gap, best.WeakestDps + 1);
            budget = terrorDps > defenceDps ? "terror" : "defence";
            return Math.Ceiling(floor);
        }

        /// <summary>The yard among <paramref name="yards"/> with the most unheld build slots, then the
        /// shortest queue, then the given order -- so a single-yard faction is unaffected.
        ///
        /// <para><b>Free capacity first, queue depth only as the tie-break.</b> A level-2 yard working
        /// two builds has less room than a level-1 yard working one, which queue length alone would
        /// get backwards. Depth is what separates yards once every one of them is working something,
        /// which is the state a faction with more roles than yards sits in permanently; without it
        /// the same yard wins every pass and the queue re-piles behind one slot.</para></summary>
        private static SpatialNode EmptiestYard(List<SpatialNode> yards)
        {
            SpatialNode best = null;
            int bestFree = 0, bestDepth = 0;
            for (int i = 0; i < yards.Count; i++)
            {
                int free = yards[i].BuildSlotCount();
                int depth = 0;
                foreach (BuildQueueEntry e in yards[i].BuildQueue)
                {
                    if (e.Bom.Count == 0) continue;
                    depth++;
                    if (yards[i].IsActiveBuild(e)) free--;
                }
                // Depth breaks the tie once every yard is working something, which is the state a
                // faction with more roles than yards sits in permanently -- without it the same yard
                // wins every pass again and the queue re-piles behind one slot.
                if (best == null || free > bestFree || (free == bestFree && depth < bestDepth))
                {
                    best = yards[i]; bestFree = free; bestDepth = depth;
                }
            }
            return best;
        }

        /// <summary>Asks each node that has been attacked recently to hold a garrison strong enough to
        /// meet what hit it, and posts the shortfall there rather than at a yard.
        ///
        /// <para>The network-wide budget above answers "how strong a hull should this faction own" and
        /// posts it at one yard. That is the right question for building and the wrong one for
        /// standing: a defender is worth having at the place it is needed, and
        /// <see cref="FleetManager"/>'s Defend block draws from the threatened node first because a
        /// hull already parked there mans the guns for zero transit. This posts the same kind of
        /// demand per node, which <see cref="TryDispatchRelocation"/> serves by moving an idle hull
        /// from the nearest donor before the yard is asked to build one.</para>
        ///
        /// <para><b>Bounded by the ledger, not by the estate.</b> The candidate set is the nodes this
        /// faction has actually been attacked at inside
        /// <see cref="FactionTuning.DefenceMemoryWindow"/> — capped by
        /// <see cref="FactionTuning.FightHistoryCap"/> — so a faction nobody has touched does no work
        /// at all and a peaceful world costs nothing (CLAUDE.md rule 5).</para>
        ///
        /// <para>The node's own station counts toward its garrison, the same reading
        /// <see cref="FleetManager"/>'s commit gate takes: a fortified node needs fewer hulls standing
        /// on it, and asking for them anyway would buy defence the place already has.</para></summary>
        private static void PostGarrisonDemands(FactionState st, WorldModel world, FactionTuning tuning, List<string> log)
        {
            if (tuning.DefenceMemoryWindow <= TimeSpan.Zero) return;
            List<string> attacked = DefenceBudget.AttackedNodesSince(st, world.UtcNow - tuning.DefenceMemoryWindow);
            if (attacked.Count == 0) return;

            long ceiling = ShipPool.LargestCombatDpsForRole(world.PrefabCatalogue, "Patrol");
            int posted = 0;
            for (int i = 0; i < attacked.Count && posted < tuning.GarrisonNodesPerPass; i++)
            {
                SpatialNode node;
                if (!world.Graph.Nodes.TryGetValue(attacked[i], out node)) continue;
                if (node.OwnerFactionTag != st.Tag) continue;   // lost since it was attacked

                long wanted = DefenceBudget.WantedDpsAt(st, node.Id, tuning, world.UtcNow);
                if (wanted <= 0) continue;

                // What stands here now: the strongest squadron this node alone can field, plus the
                // station. Per node, because a garrison is drawn from one node's stock -- the network
                // total would count hulls that are somewhere else when the raid lands.
                ShipPool.SquadronStrength here = ShipPool.BestSquadron(
                    new[] { node }, world.PrefabCatalogue, tuning.RaidSquadronMaxFighters);
                double have = here.Dps + CombatStrength.OfStation(node, tuning);
                if (have >= wanted) continue;

                double gap = wanted - have;
                double floor = here.OpenSlots > 0 ? gap / here.OpenSlots : Math.Max(gap, here.WeakestDps + 1);
                bool maxed = ceiling > 0 && here.OpenSlots == 0 && here.WeakestDps >= ceiling;
                if (ceiling > 0 && floor > ceiling) floor = ceiling;
                if (maxed) continue;

                PostShipDemand(st.Tag, "Patrol", node.Id, world, log,
                               minDps: Math.Ceiling(floor), budget: "garrison");
                posted++;
            }
        }

        /// <summary>The nodes whose ship stock counts as local to <paramref name="yard"/>: those in
        /// its <see cref="NodeRegion"/>, or the whole network when it stands in none.</summary>
        private static List<SpatialNode> StockPoolFor(SpatialNode yard, List<SpatialNode> owned)
        {
            NodeRegion region = NodeRegion.Of(yard);
            if (region.IsNone) return owned;
            var pool = new List<SpatialNode>();
            foreach (SpatialNode n in owned)
                if (region.Contains(n)) pool.Add(n);
            return pool;
        }

        /// <summary>Groups a faction's active Ship demands by (NodeId, Role) and picks, per group,
        /// the single demand whose MinCargoLiters is closest to that group's median -- the
        /// representative shipment size a freshly-built hull for that node+role should target,
        /// instead of whichever demand a naive first-match scan happens to hit first. A group of one
        /// (the common case: most node+role pairs carry a single demand) just returns that entry.</summary>
        /// <summary>Every live <c>Ship:{role}</c> demand sharing one (node, role) build slot, plus the
        /// one that stands for the group. A node can carry many destination-keyed demands for the same
        /// role at once (<see cref="DemandRegister"/> keys on <see cref="DemandEntry.DestNodeId"/>,
        /// <see cref="OperationsManager"/> posts one per blocked material vector), but this manager
        /// maintains exactly ONE <see cref="BuildQueueEntry"/> per (node, role) — so the group is a
        /// single unit of need, serviced once and retired together.</summary>
        private sealed class DemandGroup
        {
            internal readonly List<DemandEntry> Members = new List<DemandEntry>();
            /// <summary>Closest-to-median <see cref="DemandEntry.MinCargoLiters"/> — sizes the build,
            /// so one arbitrary route among possibly dozens doesn't alone dictate the hull.</summary>
            internal DemandEntry Representative;
            /// <summary>The group's LARGEST cargo requirement — the real binding constraint, so the
            /// already-satisfied check can't retire a group on the strength of a hull that only
            /// clears its median member. Same "strongest wins" principle
            /// <see cref="DemandEntry.PreserveOnRefresh"/> applies to a refresh.</summary>
            internal double MaxMinCargoLiters;
            /// <summary>The group's LARGEST power requirement — the combat counterpart of
            /// <see cref="MaxMinCargoLiters"/>, and unlike the cargo case it also SIZES the build: a
            /// DPS floor is a gap to close, so the strongest ask is the one to build for.</summary>
            internal double MaxMinDps;
            /// <summary>The group's LARGEST drill requirement (<see cref="DemandEntry.MinDrillYield"/>):
            /// the already-met check and the relocation tier only accept a hull that clears it.</summary>
            internal double MaxMinDrillYield;
        }

        /// <summary>Key for the per-tick "already serviced" set. Carries purpose for the same
        /// reason the demand and group keys do: a shelf build and a fleet build of one role share a
        /// node, and a single key would let either silence the orphan pass for the other.</summary>
        private static string ServicedKey(string nodeId, string role, bool destinedForSale)
        {
            return nodeId + "|" + role + (destinedForSale ? "|sale" : "");
        }

        /// <summary><see cref="ServicedKey"/> for one entry. A build named by hull has no role to key
        /// on and is keyed on its id, so servicing it silences the orphan pass for it alone.</summary>
        private static string ServicedKeyFor(string nodeId, BuildQueueEntry entry)
        {
            return IsNamedByHull(entry)
                ? nodeId + "|#" + entry.Id
                : ServicedKey(nodeId, entry.Role, entry.DestinedForSale);
        }

        private static Dictionary<string, DemandGroup> BuildRepresentativeDemands(List<DemandEntry> demands)
        {
            var byGroup = new Dictionary<string, DemandGroup>(StringComparer.Ordinal);
            foreach (DemandEntry d in demands)
            {
                if (d.NodeId == null) continue;
                // Purpose is part of the group key for the same reason it is part of the demand key:
                // one representative per (node, role) would otherwise carry whichever purpose won,
                // and SelectPrefabBom sizes the build from the representative.
                string key = d.NodeId + "|" + d.Kind.Substring(5) + (d.DestinedForSale ? "|sale" : "");
                DemandGroup group;
                if (!byGroup.TryGetValue(key, out group))
                    byGroup[key] = group = new DemandGroup();
                group.Members.Add(d);
                if (d.MinCargoLiters > group.MaxMinCargoLiters) group.MaxMinCargoLiters = d.MinCargoLiters;
                if (d.MinDps > group.MaxMinDps) group.MaxMinDps = d.MinDps;
                if (d.MinDrillYield > group.MaxMinDrillYield) group.MaxMinDrillYield = d.MinDrillYield;
            }

            foreach (KeyValuePair<string, DemandGroup> kv in byGroup)
                kv.Value.Representative = ClosestToMedianCargo(kv.Value.Members);
            return byGroup;
        }

        private static DemandEntry ClosestToMedianCargo(List<DemandEntry> group)
        {
            if (group.Count == 1) return group[0];

            var sortedCargo = new List<double>(group.Count);
            foreach (DemandEntry d in group) sortedCargo.Add(d.MinCargoLiters);
            sortedCargo.Sort();
            double median = sortedCargo.Count % 2 == 1
                ? sortedCargo[sortedCargo.Count / 2]
                : (sortedCargo[sortedCargo.Count / 2 - 1] + sortedCargo[sortedCargo.Count / 2]) / 2.0;

            DemandEntry best = group[0];
            double bestDist = double.MaxValue;
            foreach (DemandEntry d in group)
            {
                double dist = Math.Abs(d.MinCargoLiters - median);
                if (dist < bestDist) { bestDist = dist; best = d; }
            }
            return best;
        }

        /// <summary><paramref name="budget"/> names which budget set <paramref name="minDps"/> --
        /// "terror" or "defence" -- and is logged only on a demand that carries a power floor, so a
        /// reader can tell an antagonist's campaign build from a civilian arming itself.</summary>
        private static void PostShipDemand(string factionTag, string role, string nodeId, WorldModel world, List<string> log,
            double minCargoLiters = 0, string requiredPlanetId = null, NodeZone? requiredZone = null, double minDps = 0,
            string budget = null)
        {
            bool isNew = world.Demand.Post(DemandEntry.ForShip(factionTag, role, nodeId, world.UtcNow.Add(DemandTtlFor(world.Tuning)),
                minCargoLiters: minCargoLiters, requiredPlanetId: requiredPlanetId, requiredZone: requiredZone,
                postedUtc: world.UtcNow, minDps: minDps));
            if (isNew)
                log.Add(factionTag + " SHIP_DEMAND_PROACTIVE role=" + role + " node=" + nodeId
                        + (requiredZone.HasValue ? " zone=" + requiredZone.Value : "")
                        + (requiredPlanetId != null ? " planet=" + requiredPlanetId : "")
                        + (minDps > 0 ? " minDps=" + minDps.ToString("0", CultureInfo.InvariantCulture) : "")
                        + (minDps > 0 && budget != null ? " budget=" + budget : ""));
        }

        private static bool NetworkHasCapability(WorldModel world, string factionTag, NodeCapabilityKind cap)
        {
            foreach (SpatialNode n in world.Graph.OwnedBy(factionTag))
            {
                int level;
                if (n.Capabilities.TryGetValue(cap, out level) && level >= 1) return true;
            }
            return false;
        }

        /// <summary>Counts <see cref="SpatialNode.ListedForSale"/> entries matching <paramref
        /// name="role"/> (via <see cref="ShipPool.IsSuitableForRole"/>) per distinct prefab ID,
        /// across every node the faction owns — same enumeration idiom as
        /// <see cref="StoreStockManager"/>'s flat count, but keyed per prefab so
        /// <see cref="SelectPrefabBom"/> can diversify instead of always picking whichever
        /// candidate is cheapest/strongest.</summary>
        private static Dictionary<string, long> CountNetworkPrefabStock(WorldModel world, string factionTag)
        {
            // Counts ListedForSale (the reserved-for-sale slice), not raw ShipStock — a
            // diversification target should reflect what is actually offered for sale, not a
            // faction's whole unified pool (which also holds unrelated military/trade stock).
            // Unfiltered by role: the shelf's variety is across everything on it, not within one
            // capability class, and the class mix is StoreShelfPlan's job upstream of here.
            var counts = new Dictionary<string, long>();
            foreach (SpatialNode n in world.Graph.OwnedBy(factionTag))
                foreach (var kv in n.ListedForSale)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    long cur;
                    counts.TryGetValue(kv.Key, out cur);
                    counts[kv.Key] = cur + kv.Value;
                }
            return counts;
        }

        /// <summary>
        /// Picks a candidate prefab and returns its BOM. When <paramref name="stockByPrefab"/> is
        /// supplied (Store roles only — see the call site), selection diversifies toward whichever
        /// candidate currently has the fewest units in network stock, ties broken by cost, so a
        /// store block offers a real selection instead of N copies of one hull. Otherwise: at
        /// <paramref name="militaryPosture"/> ≈ 0 (peacetime baseline — also the regression guard:
        /// unchanged from the original behavior) this is purely cheapest-first by total BOM unit
        /// count, same as before Muster existed. As posture rises, selection blends in combat power
        /// (summed WeaponBands Dps, via <see cref="WorldModel.PrefabCatalogue"/>) so a faction
        /// actively building up its fleet tolerates a costlier hull for more Dps instead of always
        /// taking the cheapest available. No affordability check — the build queue accumulates
        /// components over time. Sets <paramref name="chosenId"/> to null and leaves
        /// <paramref name="bomOut"/> empty for free builds.
        /// <para><paramref name="buildRoute"/> null preserves today's exact behavior. When supplied
        /// — a degenerate same-point <see cref="ShipPool.RouteEnvironment"/> at the
        /// building node, collapsing <see cref="ShipPool.IsRouteFeasible"/>'s origin-and-destination
        /// check to an origin-only "can this hull even survive being built here" check — candidates
        /// that would be born stranded (same real dry-TWR physics the dispatch-time route gate uses)
        /// are excluded from every branch below. If nothing in the same-hint <paramref
        /// name="candidates"/> set survives, the universal <c>"All"</c>-hint pool (queried via
        /// <paramref name="role"/>) is tried next, zone-filtered the same way — a faction-hint bucket
        /// that happens to be uniformly one propulsion type (e.g. all-Ion) is often rescued by the
        /// more thrust-diverse universal pool (<paramref name="usedUniversalFallback"/> reports when
        /// that happened). Only if nothing anywhere can fly the legs does the full unfiltered
        /// same-hint set get used (a demand the faction genuinely cannot fulfill well still builds
        /// something rather than stalling forever — <paramref name="zoneInfeasibleFallback"/> reports
        /// that terminal case). Store roles (<paramref name="stockByPrefab"/> != null) are never
        /// filtered — a store-listed ship is bought and flown by a player, not dispatched by <see
        /// cref="ShipPool"/>/NPC AI.</para>
        /// <para>The route check is a hard gate and runs first; <paramref name="minCargoLiters"/>
        /// &gt; 0 then narrows what survived it. When the floor clears neither pool it is
        /// <b>clamped, not dropped</b>: to the largest <see cref="IPrefabCatalogue.CargoLiters"/>
        /// among the hulls that can actually fly these legs (across both pools), and the two passes
        /// re-run against that. Dropping the floor instead hands the entire route-feasible pool to
        /// the cheapest-first ranker below, which picks the SMALLEST hull — so a shipment bigger than
        /// anything that can fly the leg got the smallest thing that could, and hull size stopped
        /// tracking shipping volume at the top of its range. Clamping keeps it monotonic. A ceiling
        /// of 0 (no route-feasible candidate carries any cargo — a combat role against the nominal
        /// 1-litre sentinel) leaves route feasibility alone to decide, as before.</para>
        /// </summary>
        /// <summary>Slice 0 of docs/fleet-fitness-plan.md: says what the requirement would have asked
        /// for and which hull that implies, WITHOUT changing what is built.
        ///
        /// <para>An ignored slice earns its place by answering "would this change anything" before
        /// anything is at risk. Logging the counterfactual beside the real choice makes that a
        /// reading rather than a prediction — and if the two agree, that is a result too: converging
        /// on today's hull is correct for a faction whose jobs really are small, and the point of
        /// this design is knowing WHY the choice was made, not necessarily making a different
        /// one.</para>
        ///
        /// <para>Silent until the requirement is warm (<see cref="RoleRequirement.IsWarm"/>), so a
        /// fresh faction does not emit a line per build saying it has no opinion.</para>
        /// <para><b>It ranks the pool the real chooser actually ranked</b> (<c>rankedPool</c>), not the
        /// raw same-hint candidate list. Those differ whenever the same-hint pool is zone-infeasible
        /// and <see cref="SelectPrefabBom"/> rescues from the universal <c>"All"</c> pool — and the
        /// first version of this line took the raw list, so `implied=` could name a hull that cannot
        /// fly the legs and every such build read as a disagreement. Measured 2026-09-03: 25
        /// universal fallbacks in a day, and every armed-hull `DIFFERS` on a cargo role coincided
        /// with one. Comparing the same pool is what makes this a comparison of RANKING RULES rather
        /// than of candidate sets.</para>
        /// <para><b>`posture=` is on the line because it is the one thing the counterfactual does not
        /// model.</b> Above zero the real chooser blends weapon Dps into its ranking, so a `DIFFERS`
        /// at high posture may be the Muster blend rather than the requirement. Read those
        /// separately from the `posture=0` ones.</para></summary>
        private static void LogFleetFitnessCounterfactual(FactionState st, string role, IList<string> candidates,
            WorldModel world, FactionTuning tuning, double liveFloorLiters, string chosenId, string nodeId,
            List<string> log)
        {
            if (st == null || log == null || world == null || world.PrefabCatalogue == null) return;
            if (tuning == null || tuning.FleetRequirementPercentile <= 0) return;

            RoleRequirement requirement;
            if (!st.FleetRequirements.TryGetValue(role ?? "", out requirement) || requirement == null) return;
            if (!requirement.IsWarm) return;

            double required = requirement.SizeAtPercentile(tuning.FleetRequirementPercentile);
            bool degraded;
            // Rank on the SAME measure the real chooser uses -- summed BOM units, not the catalogue's
            // component-price Cost. Comparing a choice made on one scale against a choice made on
            // another manufactures disagreements that say nothing about the requirement, which is the
            // only thing this line exists to report.
            string implied = FleetFitness.Choose(world.PrefabCatalogue, candidates, required, out degraded,
                id =>
                {
                    bool hasRecipe;
                    Dictionary<string, long> b = ProductionManager.GetPrefabBom(world.Recipes, id, out hasRecipe);
                    long units = 0;
                    if (b != null) foreach (var kv in b) units += kv.Value;
                    return units;
                });

            // Dps of both hulls, because the question a DIFFERS raises is never "is that a warship"
            // -- it is "was it ranked on Dps". Reading it off hull NAMES misled this investigation
            // twice (2026-09-03); every hull in the sample that looked armed by name was armed, and
            // so was the one fleet fitness preferred, which is what made the names useless. With
            // both figures on the line, FAC-239's decisive check -- a post-gate cargo build at
            // posture >= 0.57 that does not take the max-Dps candidate -- reads directly.
            IPrefabCatalogue cat = world.PrefabCatalogue;
            double builtDps = chosenId != null ? ShipPool.TotalDps(cat.WeaponBands(chosenId)) : 0;
            double impliedDps = implied != null ? ShipPool.TotalDps(cat.WeaponBands(implied)) : 0;

            CultureInfo inv = CultureInfo.InvariantCulture;
            log.Add(st.Tag + " FLEET_FITNESS role=" + role + " node=" + nodeId
                    + " requiredL=" + required.ToString("0", inv)
                    + " liveFloorL=" + liveFloorLiters.ToString("0", inv)
                    + " drives=" + (tuning.FleetRequirementDrivesBuild ? "1" : "0")
                    + " samples=" + requirement.Samples
                    + " unmet=" + requirement.UnmetEma.ToString("0.###", inv)
                    + " posture=" + st.MilitaryPosture.ToString("0.##", inv)
                    + " built=" + (chosenId ?? "?")
                    + " builtDps=" + builtDps.ToString("0", inv)
                    + " implied=" + (implied ?? "?")
                    + " impliedDps=" + impliedDps.ToString("0", inv)
                    + (degraded ? " implied_degraded" : "")
                    + (string.Equals(chosenId, implied, StringComparison.Ordinal) ? " agree" : " DIFFERS"));
        }

        private static readonly IReadOnlyList<string> NoCandidates = new string[0];

        /// <summary>Adds a fleet or shelf build for <paramref name="role"/> at <paramref name="node"/>,
        /// lets it compete for a slot now, and reports what it got.</summary>
        private static BuildQueueEntry QueueSelectedBuild(FactionState st, WorldModel world, FactionTuning tuning,
            SpatialNode node, string role, string prefabId, Dictionary<string, long> bom, bool destinedForSale,
            List<string> log)
        {
            var entry = new BuildQueueEntry
            {
                Id         = node.MintBuildEntryId(role),
                Role       = role,
                PrefabId   = prefabId,
                DestinedForSale = destinedForSale,
                CreatedUtc = world.UtcNow,
                ExpiresUtc = world.UtcNow.AddHours(tuning.MaxBuildDurationHours),
                Priority   = RolePriority(role, tuning, destinedForSale),
            };
            foreach (var kv in bom) entry.Bom[kv.Key] = kv.Value;
            node.BuildQueue.Add(entry);
            // The new entry competes for a slot now, not on the next cadence -- and the line below
            // reports what it actually got rather than what it would have got.
            BuildSlotAllocation.Allocate(world, st.Tag, tuning);

            world.RecordEvent(FactionEventKind.BuildQueued, st.Tag, null, node.Id, 1);
            log.Add(st.Tag + " BUILD_QUEUED role=" + role + " node=" + node.Id
                    + (prefabId != null ? " prefab=" + prefabId : " (free)")
                    + " slot=" + (node.IsActiveBuild(entry)
                                  ? "working"
                                  : "waiting behind " + DescribeAhead(node, entry)));
            return entry;
        }

        /// <summary>The livery swap for builds no demand reached this pass. A mandatory-livery faction's
        /// generic fleet build for a role its livery has a hull for is replaced by its own hull, chosen
        /// as the demand path chooses but against the yard itself: no destination, no Dps floor, the
        /// faction's own cargo floor for the role. Kept when no hull of its own can stand at the yard.
        /// Its collected components go back to the node, where the replacement absorbs them.</summary>
        private void ReliveryUnservicedBuilds(FactionState st, WorldModel world, FactionTuning tuning,
            HashSet<string> serviced, List<string> log)
        {
            if (!PrefabFactionHint.LiveryIsMandatory(st.PersonalityId) || world.PrefabCatalogue == null) return;

            foreach (SpatialNode node in world.Graph.OwnedBy(st.Tag))
            {
                if (node.ShipyardPaused || node.BuildQueue.Count == 0) continue;
                foreach (BuildQueueEntry entry in node.BuildQueue.ToArray())
                {
                    if (entry.DestinedForSale || IsNamedByHull(entry) || IsStationRefit(entry)) continue;
                    if (serviced.Contains(ServicedKeyFor(node.Id, entry))) continue;
                    if (PrefabRolePool.IsOwnLivery(world.PrefabCatalogue, entry.PrefabId)) continue;

                    bool ownLiveryOnly;
                    IList<string> candidates = CandidatesFor(entry.Role, st, world, false, out ownLiveryOnly);
                    if (!ownLiveryOnly) continue;

                    ShipPool.RouteEnvironment atYard = ShipPool.RouteEnvironment.From(
                        world.Planets, node.Position, node.Position, tuning.MinDryTwr);
                    string prefabId;
                    var bom = new Dictionary<string, long>();
                    bool zoneFallback, universalFallback, cargoClamped, dpsClamped;
                    IList<string> rankedPool;
                    SelectPrefabBom(entry.Role, candidates, world, st.MilitaryPosture, null, atYard,
                        FleetFitness.SizingFloor(st, entry.Role, tuning, 0), out prefabId, bom,
                        out zoneFallback, out universalFallback, out cargoClamped, out rankedPool,
                        null, null, false, 0, out dpsClamped, true);
                    if (prefabId == null || !PrefabRolePool.IsOwnLivery(world.PrefabCatalogue, prefabId)) continue;

                    long candidateUnits = 0;
                    foreach (var kv in bom) candidateUnits += kv.Value;
                    if (candidateUnits <= 0) continue;
                    long remainingUnits = 0;
                    foreach (var kv in entry.Bom) remainingUnits += kv.Value;
                    foreach (var kv in entry.Collected) remainingUnits -= kv.Value;
                    log.Add(st.Tag + " BUILD_RESELECTED role=" + entry.Role + " node=" + node.Id
                            + " from=" + (entry.PrefabId ?? "free")
                            + " remaining=" + remainingUnits.ToString(CultureInfo.InvariantCulture)
                            + " to=" + prefabId
                            + " bom=" + candidateUnits.ToString(CultureInfo.InvariantCulture)
                            + " reason=livery demand=none");

                    RefundEntry(world, node, entry);
                    node.BuildQueue.Remove(entry);
                    QueueSelectedBuild(st, world, tuning, node, entry.Role, prefabId, bom, false, log);
                }
            }
        }

        /// <summary>The hulls a build for <paramref name="role"/> chooses among: the picker's pool,
        /// narrowed to the faction's own livery when <see cref="PrefabFactionHint.LiveryIsMandatory"/>
        /// says so and the role has a hull of it. <paramref name="ownLiveryOnly"/> reports that the
        /// narrowing happened, which is what keeps <see cref="SelectPrefabBom"/>'s universal rescue
        /// closed while an own hull can fly the legs. A shelf build keeps the whole pool: what the
        /// shop stocks is not a fleet decision.</summary>
        private IList<string> CandidatesFor(string role, FactionState st, WorldModel world,
            bool destinedForSale, out bool ownLiveryOnly)
        {
            ownLiveryOnly = false;
            IList<string> candidates = PrefabListPicker != null ? PrefabListPicker(role, st.Tag) : null;
            if (candidates == null || candidates.Count == 0 || destinedForSale) return candidates;
            if (!PrefabFactionHint.LiveryIsMandatory(st.PersonalityId)) return candidates;

            List<string> own = PrefabRolePool.OwnLivery(candidates, world.PrefabCatalogue);
            if (own.Count == 0) return candidates;
            ownLiveryOnly = true;
            return own;
        }

        /// <summary>Whether <paramref name="st"/> builds <paramref name="role"/> from its own livery
        /// alone (<see cref="CandidatesFor"/>).</summary>
        private bool HasOwnLiveryFor(string role, FactionState st, WorldModel world)
        {
            bool ownLiveryOnly;
            CandidatesFor(role, st, world, false, out ownLiveryOnly);
            return ownLiveryOnly;
        }

        private static void SelectPrefabBom(
            string role,
            IList<string> candidates,
            WorldModel world,
            double militaryPosture,
            IReadOnlyDictionary<string, long> stockByPrefab,
            ShipPool.RouteEnvironment? buildRoute,
            double minCargoLiters,
            out string chosenId,
            Dictionary<string, long> bomOut,
            out bool zoneInfeasibleFallback,
            out bool usedUniversalFallback,
            out bool cargoFloorClamped,
            out IList<string> rankedPool,
            ShipPool.RouteEnvironment? viaRoute,
            string requiredGridSize,
            bool destinedForSale,
            double minDps,
            out bool dpsFloorClamped,
            bool ownLiveryOnly = false)
        {
            zoneInfeasibleFallback = false;
            usedUniversalFallback = false;
            cargoFloorClamped = false;
            dpsFloorClamped = false;
            rankedPool = null;
            if (candidates == null || candidates.Count == 0) { chosenId = null; return; }

            IList<string> filtered = candidates;
            // Route feasibility at the yard is asked of every build, shelf ones included. A shelf
            // hull that goes unsold is handed back to the fleet pool by StoreShelfRelease at the node
            // it was built at, so a hull that cannot lift off that node is stranded stock the moment
            // its reservation lapses -- and it is withheld from that node's own shop meanwhile
            // (StoreShelfPlan.IsShelfableAt, the same physics evaluated at the selling station).
            if (buildRoute != null && world.PrefabCatalogue != null)
            {
                // Route is a hard gate and cargo is only a preference -- a hull too small for this
                // demand still flies its legs and serves a later, smaller one, while a
                // route-infeasible hull is stranded on arrival and occupies the node's stock forever,
                // re-blocking the very dispatch that asked for it. So the route filter runs first and
                // unconditionally; the cargo floor only ever narrows what survives it.
                //
                // Same-hint pool first (cheapest first within it), then the universal "All" pool (not
                // filtered to one faction hint, so it's usually more thrust-diverse). When the floor
                // clears neither, it is clamped to the largest cargo capacity among the hulls that can
                // actually fly THESE legs and the two passes are retried -- not dropped. Dropping it
                // hands the whole route-feasible pool to a cheapest-first ranker, so the demand that
                // wanted the biggest hull gets the smallest one.
                List<string> hintFeasible = OfGridSize(
                    FilterZoneFeasible(candidates, world, buildRoute, viaRoute), world, requiredGridSize);
                List<string> pick = AtLeastCargo(hintFeasible, world, minCargoLiters);
                bool universalPool = false;

                if (pick.Count == 0)
                {
                    // A mandatory-livery faction whose own hulls can fly these legs is not rescued
                    // from the universal pool: its floor is clamped within its own hulls below.
                    // Only when none of them can fly here does the universal pool open.
                    IReadOnlyList<string> universal = ownLiveryOnly && hintFeasible.Count > 0
                        ? NoCandidates
                        : world.PrefabCatalogue.GetNamesForRole(role, "All");
                    List<string> universalFeasible = OfGridSize(
                        FilterZoneFeasible(universal, world, buildRoute, viaRoute), world, requiredGridSize);
                    pick = AtLeastCargo(universalFeasible, world, minCargoLiters);
                    universalPool = pick.Count > 0;

                    if (pick.Count == 0)
                    {
                        // The floor is unreachable on these legs. Clamp it to the biggest hull that
                        // CAN fly them -- across both pools, since the universal pool is the rescue
                        // pool and "biggest that can fly this leg" is the whole point of the clamp.
                        long ceiling = LargestCargo(hintFeasible, world);
                        long universalCeiling = LargestCargo(universalFeasible, world);
                        if (universalCeiling > ceiling) ceiling = universalCeiling;
                        cargoFloorClamped = ceiling > 0 && ceiling < minCargoLiters;

                        if (ceiling > 0)
                        {
                            // Guaranteed non-empty: whichever pool holds the ceiling hull clears it.
                            pick = AtLeastCargo(hintFeasible, world, ceiling);
                            if (pick.Count == 0)
                            {
                                pick = AtLeastCargo(universalFeasible, world, ceiling);
                                universalPool = pick.Count > 0;
                            }
                        }
                        else
                        {
                            // No route-feasible candidate carries any cargo at all (a combat role
                            // against the nominal 1-litre "must be cargo-capable" sentinel). There is
                            // no ceiling to clamp to, so route feasibility alone decides.
                            pick = hintFeasible;
                            if (pick.Count == 0)
                            {
                                pick = universalFeasible;
                                universalPool = pick.Count > 0;
                            }
                        }
                    }
                }

                if (pick.Count > 0)
                {
                    filtered = pick;
                    usedUniversalFallback = universalPool;
                }
                else
                {
                    zoneInfeasibleFallback = true; // nothing flies these legs -- fall back to the full pool
                }
            }

            // The power floor narrows what survived the route and cargo passes. A floor one hull can
            // actually clear is a requirement and is applied as one. A floor no hull can clear is not
            // a requirement at all -- it is a GAP, and the question stops being "which hull is
            // biggest" and becomes "which hull closes the most of it per unit built". The pool is
            // therefore left whole (minus the unarmed) and the ranking below switches to Dps per BOM
            // unit, so the gap is closed by a swarm that arrives while it still matters instead of by
            // one flagship that does not.
            if (minDps > 0 && world.PrefabCatalogue != null && filtered.Count > 0)
            {
                List<string> strongEnough = AtLeastDps(filtered, world, minDps);
                if (strongEnough.Count > 0)
                    filtered = strongEnough;
                else
                {
                    List<string> armed = AtLeastDps(filtered, world, double.Epsilon);
                    if (armed.Count > 0)
                    {
                        filtered = armed;
                        dpsFloorClamped = true;
                    }
                }
            }

            rankedPool = filtered;
            var scored = new List<CandidateEntry>(filtered.Count);
            foreach (string cid in filtered)
            {
                bool cHasRecipe;
                Dictionary<string, long> cbom = ProductionManager.GetPrefabBom(world.Recipes, cid, out cHasRecipe);
                long total = 0;
                foreach (var kv in cbom) total += kv.Value;
                double powerDps = 0;
                if (world.PrefabCatalogue != null)
                {
                    IReadOnlyList<WeaponBand> bands = world.PrefabCatalogue.WeaponBands(cid);
                    for (int i = 0; i < bands.Count; i++) powerDps += bands[i].Dps;
                }
                double duelDamage = world.PrefabCatalogue != null
                    ? CombatStrength.DuelDamage(world.PrefabCatalogue.WeaponBands(cid), null, world.Tuning)
                    : 0;
                double drillScore = world.PrefabCatalogue != null ? world.PrefabCatalogue.DrillYieldScore(cid) : 0;
                scored.Add(new CandidateEntry { Id = cid, TotalCost = total, PowerDps = powerDps, DuelDamage = duelDamage, DrillScore = drillScore, Bom = cbom, HasRecipe = cHasRecipe, Order = scored.Count });
            }

            bool hasAnyPower = false;
            foreach (CandidateEntry c in scored)
                if (c.PowerDps > 0) { hasAnyPower = true; break; }

            CandidateEntry chosen;
            if (dpsFloorClamped)
            {
                // Closing a gap no single hull can close: the most duel damage per unit built wins.
                // Damage rather than raw Dps because the duel is range-bracketed -- a hull whose guns
                // miss the long brackets is shot at for a turn before it answers, and counting its
                // full Dps would rank it as if it had fought all three. Ties fall to the cheaper hull,
                // then to pool position, so a faction keeps its own hull and the choice is
                // reproducible.
                chosen = scored[0];
                double bestValue = double.NegativeInfinity;
                foreach (CandidateEntry c in scored)
                {
                    if (c.TotalCost <= 0) continue;
                    double value = c.DuelDamage / c.TotalCost;
                    if (value > bestValue
                        || (value == bestValue && (c.TotalCost < chosen.TotalCost
                            || (c.TotalCost == chosen.TotalCost && c.Order < chosen.Order))))
                    { bestValue = value; chosen = c; }
                }
            }
            else if (stockByPrefab != null)
            {
                // Diversify: prefer whichever candidate currently has the fewest units in network
                // stock (ties -> cheapest) — players see a real selection with an inventory of
                // each, not one hull repeated. Every candidate starts tied at 0, so the very first
                // build for a role still matches cheapest-first (day-one behavior unchanged).
                long bestCount = long.MaxValue;
                chosen = scored[0];
                foreach (CandidateEntry c in scored)
                {
                    long count;
                    stockByPrefab.TryGetValue(c.Id, out count);
                    if (count < bestCount || (count == bestCount && c.TotalCost < chosen.TotalCost))
                    { bestCount = count; chosen = c; }
                }
            }
            else if (role == MineRole && TryPickByDrillRatePerCost(scored, out chosen))
            {
                // Chosen in the helper: a drill hull is built for what it extracts per unit it costs,
                // not for being cheapest. See TryPickByDrillRatePerCost.
            }
            else if (!ShipPool.ShouldRankOnCombatPower(role, militaryPosture, scored.Count, hasAnyPower, destinedForSale))
            {
                // Cheapest-first for a peacetime posture, a single candidate, a set with no Dps to
                // differentiate on -- and for any role whose JOB is not combat.
                //
                // That last clause is FAC-239. This method is shared across every Ship: role, and
                // the guard used to be `!hasAnyPower` alone: the reasoning was that a Trade
                // candidate set contains no armed hulls, so the blend could never engage for it.
                // That stopped being true. Measured live 2026-09-03, every hull built for a `Trade`
                // role in the sample carried Trade AND Raid/Patrol/Guard with real Dps (Nagamaki
                // 23,387; Alsvidr 48,428), so `hasAnyPower` was true and cargo builds were being
                // resolved on weapon power -- IGSL built its ordinary trade drone at six nodes in
                // one tick and warships at two others, same role, same tick.
                //
                // "The set contains something armed" was always a proxy for "the job wants combat
                // power". Asking the role directly is the question that was meant.
                //
                // A shelf build never reaches this branch: it takes the diversification branch
                // above. Stated as a property of the purpose, not of stockByPrefab being non-null --
                // that coupling is what made one flag quietly gate three mechanisms.
                // Cost first, then pool position. FAC-237: the pool is now the union of the
                // faction's own hulls and the universal ones, specific first, so the second key is
                // what keeps a faction on its own hull when a generic one costs the same. It also
                // makes an equal-cost field resolve the same way every time -- List<T>.Sort is an
                // unstable introsort, so before this the winner among tied candidates was not
                // determined by anything a reader could name.
                scored.Sort((a, b) => PrefabRolePool.CompareByCostThenPoolPosition(
                    a.TotalCost, a.Order, b.TotalCost, b.Order));
                chosen = scored[0];
            }
            else
            {
                // Blend normalized cost (lower better) and normalized power (higher better) across
                // this candidate set only — posture linearly interpolates the two: 0 = pure cheapest
                // (matches the branch above), 1 = pure highest-power regardless of cost.
                long minCost = long.MaxValue, maxCost = long.MinValue;
                double minPower = double.MaxValue, maxPower = double.MinValue;
                foreach (CandidateEntry c in scored)
                {
                    if (c.TotalCost < minCost) minCost = c.TotalCost;
                    if (c.TotalCost > maxCost) maxCost = c.TotalCost;
                    if (c.PowerDps < minPower) minPower = c.PowerDps;
                    if (c.PowerDps > maxPower) maxPower = c.PowerDps;
                }
                long costSpan = Math.Max(1, maxCost - minCost);
                double powerSpan = Math.Max(1e-6, maxPower - minPower);

                double bestScore = double.MaxValue;
                chosen = scored[0];
                foreach (CandidateEntry c in scored)
                {
                    double normCost = (c.TotalCost - minCost) / (double)costSpan;
                    double normPower = (c.PowerDps - minPower) / powerSpan;
                    double score = (1.0 - militaryPosture) * normCost - militaryPosture * normPower;
                    if (score < bestScore) { bestScore = score; chosen = c; }
                }
            }

            chosenId = chosen.HasRecipe ? chosen.Id : null;
            if (chosen.HasRecipe)
                foreach (var kv in chosen.Bom) bomOut[kv.Key] = kv.Value;
        }

        /// <summary>Candidates whose real dry-TWR clears <paramref name="buildRoute"/> at both ends
        /// (<see cref="ShipPool.IsRouteFeasible"/>) — the hard gate <see cref="SelectPrefabBom"/>
        /// applies once per candidate pool, before <see cref="AtLeastCargo"/> narrows the survivors by
        /// the (clampable) cargo floor. <paramref name="viaRoute"/>,
        /// when present, is a second leg a candidate must ALSO clear (e.g. the shipyard-to-pickup leg
        /// of a Haul/Trade Tier-3 fallback build, distinct from the shipyard-to-final-delivery leg
        /// <paramref name="buildRoute"/> already models) — a candidate must survive both legs, not
        /// just one, since the built hull will actually fly the whole real route, not a single pair.</summary>
        /// <summary>The subset of <paramref name="candidates"/> whose cargo capacity meets
        /// <paramref name="minCargoLiters"/>. Split out from <see cref="FilterZoneFeasible"/> so the
        /// route gate (expensive, and a hard requirement) runs once per pool while the cargo floor
        /// (cheap, and a preference that may be clamped and re-applied) can be re-evaluated against
        /// that same already-filtered list.</summary>
        /// <summary>Narrows to one grid size, or returns the list untouched when none is asked
        /// for. Only the store asks: its shelf keeps a small/large composition, and grid size is a
        /// real property of a hull rather than one of the retired role names. Applied like the cargo
        /// floor -- a preference over the route gate, never instead of it -- and dropped the same way
        /// when it clears nothing, so an unsatisfiable size cannot stall a build.</summary>
        private static List<string> OfGridSize(List<string> candidates, WorldModel world, string gridSize)
        {
            if (string.IsNullOrEmpty(gridSize) || candidates == null || world.PrefabCatalogue == null)
                return candidates;
            var result = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
                if (string.Equals(world.PrefabCatalogue.GridSize(candidates[i]), gridSize,
                                  StringComparison.OrdinalIgnoreCase))
                    result.Add(candidates[i]);
            return result.Count > 0 ? result : candidates;
        }

        private static List<string> AtLeastCargo(List<string> candidates, WorldModel world, double minCargoLiters)
        {
            if (minCargoLiters <= 0) return candidates;
            var result = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
                if (world.PrefabCatalogue.CargoLiters(candidates[i]) >= minCargoLiters)
                    result.Add(candidates[i]);
            return result;
        }

        private static List<string> AtLeastDps(IList<string> candidates, WorldModel world, double minDps)
        {
            var result = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
                if (PrefabDps(candidates[i], world) >= minDps) result.Add(candidates[i]);
            return result;
        }

        /// <summary>Strongest summed weapon DPS in an already-filtered candidate list. 0 when nothing
        /// in it is armed.</summary>
        private static double LargestDps(IList<string> candidates, WorldModel world)
        {
            double max = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                double dps = PrefabDps(candidates[i], world);
                if (dps > max) max = dps;
            }
            return max;
        }

        private static double PrefabDps(string prefabId, WorldModel world)
        {
            IReadOnlyList<WeaponBand> bands = world.PrefabCatalogue.WeaponBands(prefabId);
            double sum = 0;
            if (bands != null) for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }

        /// <summary>Largest cargo capacity in an already route-filtered candidate list -- "the biggest
        /// hull that can fly this leg". 0 when the list is empty or nothing in it carries cargo.</summary>
        private static long LargestCargo(List<string> candidates, WorldModel world)
        {
            long max = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                long liters = world.PrefabCatalogue.CargoLiters(candidates[i]);
                if (liters > max) max = liters;
            }
            return max;
        }

        private static List<string> FilterZoneFeasible(IEnumerable<string> candidates, WorldModel world,
            ShipPool.RouteEnvironment? buildRoute, ShipPool.RouteEnvironment? viaRoute = null)
        {
            var result = new List<string>();
            foreach (string cid in candidates)
                if (ShipPool.IsRouteFeasible(world.PrefabCatalogue, cid, buildRoute)
                    && ShipPool.IsRouteFeasible(world.PrefabCatalogue, cid, viaRoute))
                    result.Add(cid);
            return result;
        }

        /// <summary>The <c>Ship:</c> role whose build is ranked by drill rate per cost.</summary>
        private const string MineRole = "Mine";

        /// <summary>The Mine role's build rank (FAC-395): the highest
        /// <see cref="IPrefabCatalogue.DrillYieldScore"/> per unit of BOM, among the candidates that
        /// survived the route gate and the cargo floor. Ties go to the cheaper hull, then to pool
        /// position, the same tie-break as cheapest-first.
        ///
        /// <para>Drill rate is <c>DrillYieldScore × MineDrillLitersPerScorePoint × MineDutyCycle</c>
        /// (<c>OperationsManager.ResolveDrillRate</c>), and both factors are constants, so the score
        /// ranks rate per cost exactly without reading tuning here.</para>
        ///
        /// <para>Why not cheapest-first: extraction is bounded by drill time on a deposit, and a
        /// deposit's cooldown equals the dwell. A cheap hull at ~8 L/s occupies the ship and the seam
        /// two hundred times longer than a ~1,600 L/s one for the same load, so cheapest-first bought
        /// the one hull that could not raise output. Measured 2026-09-27: four small factions flew
        /// Spadi-Atmo almost exclusively.</para>
        ///
        /// <para>A candidate with no drill or no costed BOM is not ranked. When none qualifies the
        /// caller falls through to its cheapest-first branch, as before.</para></summary>
        private static bool TryPickByDrillRatePerCost(List<CandidateEntry> scored, out CandidateEntry chosen)
        {
            chosen = default(CandidateEntry);
            bool found = false;
            double bestValue = 0;
            foreach (CandidateEntry c in scored)
            {
                if (c.TotalCost <= 0 || c.DrillScore <= 0) continue;
                double value = c.DrillScore / c.TotalCost;
                if (!found || value > bestValue
                    || (value == bestValue && (c.TotalCost < chosen.TotalCost
                        || (c.TotalCost == chosen.TotalCost && c.Order < chosen.Order))))
                {
                    chosen = c;
                    bestValue = value;
                    found = true;
                }
            }
            return found;
        }

        private struct CandidateEntry
        {
            public string Id;
            public long TotalCost;
            public double PowerDps;
            /// <summary><see cref="IPrefabCatalogue.DrillYieldScore"/>; ranks a Mine build
            /// (<see cref="TryPickByDrillRatePerCost"/>) and nothing else.</summary>
            public double DrillScore;
            /// <summary>Damage this hull deals over a whole duel, range-bracketed exactly as
            /// <c>ResolveShipDuel</c> will fight it. <see cref="PowerDps"/> counts guns a hull owns;
            /// this counts the ones that reach, which is what separates a swarm that trades from a
            /// swarm that is shot at for a turn before it can answer.</summary>
            public double DuelDamage;
            public Dictionary<string, long> Bom;
            public bool HasRecipe;
            /// <summary>Position in the candidate pool. The pool arrives faction-specific-first
            /// (see <see cref="PrefabRolePool"/>), so this is what makes the cost sort prefer a
            /// faction's own hull over a generic one at equal cost -- and what makes the choice
            /// deterministic when costs tie, which an unstable sort did not guarantee.</summary>
            public int Order;
        }
    }
}
