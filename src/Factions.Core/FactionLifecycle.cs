using System;
using System.Collections.Generic;
using System.Linq;

namespace Factions.Core
{
    /// <summary>
    /// Faction lifecycle past the point of ruin: a faction stripped of all territory with its ledger pinned
    /// at the insolvency floor is <b>conquered</b> — put to sleep (dormant) rather than left to spiral. The
    /// economic floor and force-shed live in <see cref="WorldModel.AccrueIncome"/> (they only need the source
    /// owners, so they ride the per-tick income pass); this step is the part that needs the faction ROSTER and
    /// PERSISTED state, marking the collapse so the runner stops thinking for a dead faction.
    ///
    /// Dormancy is currently <b>indefinite</b> (<see cref="FactionState.DormantUntilUtc"/> = <see
    /// cref="DateTime.MaxValue"/>): revival/reincarnation is deliberately deferred — a conquered faction simply
    /// stays dormant. (The timestamp field is kept so a future timed or event-driven revival can set a finite
    /// wake time without a schema change.) Single-sourced here so the host runtime and the sim agree.
    ///
    /// Not to be confused with <see cref="ActionKind.Conquer"/> (Slice N), which is per-node capture by
    /// an attacker — a faction can lose individual nodes to Conquer while remaining active; this class
    /// only fires once a faction has lost ALL territory and is also insolvent.
    /// </summary>
    public static class FactionLifecycle
    {
        /// <summary>True while a faction is conquered/dormant (it should not think or act).
        /// <see cref="NeutralFaction.Tag"/> (UNKN) is permanently dormant regardless of
        /// <see cref="FactionState.DormantUntilUtc"/> — it is a graveyard for abandoned territory,
        /// never a real competitive actor.</summary>
        /// <summary>The per-faction RNG seed, derived from its SE faction id so the stream is stable
        /// across restarts and distinct per faction.
        ///
        /// <para>In Core because <b>both</b> registration paths need it — the NPC sweep and the
        /// Magnate claim — and a second copy at the second call site is how they drift. A faction
        /// left at seed 0 shares one stream with every other faction that was.</para></summary>
        public static int DeriveRngSeed(long seFactionId)
        {
            return (int)(seFactionId ^ (seFactionId >> 32));
        }

        public static bool IsDormant(FactionState s, DateTime nowUtc)
        {
            return s.Tag == NeutralFaction.Tag
                || (s.DormantUntilUtc > DateTime.MinValue && nowUtc < s.DormantUntilUtc);
        }

        /// <summary>True when <paramref name="s"/>'s own strategic decision loop
        /// (<c>FactionRuntime.DrainFactionTick</c>'s <c>FactionBrain.Think</c>/<c>Enact</c>) should
        /// NOT act on its behalf — a Magnate-claimed player faction has no brain choosing Expand,
        /// Raid, Conquer, Found, Develop, etc. with its treasury/territory; the player supplies those
        /// decisions instead (see docs/economy-territory.md → "Player-faction claiming").
        ///
        /// <para><b>Neither <see cref="FleetManager"/> nor <see cref="TradeManager"/> reads this
        /// predicate directly any more.</b> Both used to skip a player faction outright, on the
        /// reasoning that they consume <c>ReinforcePriority</c>/<c>TradePriority</c> lists only
        /// <c>FactionBrain.Think</c> fills, so switching them on alone would leave them enabled but
        /// inert. Both now take a per-manager opt-in with a projection behind it, which is the shape
        /// this predicate's own comment anticipated: <c>ReinforceOptIn</c> →
        /// <see cref="ProjectPlayerReinforcePriority"/> (FAC-264), and
        /// <c>TradeBuyOptIn</c>/<c>TradeSellOptIn</c> → <see cref="TradePriority"/> via
        /// <see cref="ProjectPlayerTradePriority"/> (FAC-298). Ask
        /// <see cref="TradeBuyEnabled"/>/<see cref="TradeSellEnabled"/> for trade rather than this.
        /// What stays unconditionally off is the strategic loop above.</para>
        ///
        /// <para><b>Does NOT gate</b> <see cref="ProductionManager"/>, <see cref="ShipyardManager"/>,
        /// <see cref="StoreStockManager"/>, or <see cref="OperationsManager"/> (as of 2026-08-16) —
        /// all four are self-sufficient (driven by <c>DemandRegister</c>/<c>InvestmentRegister</c>/
        /// scarcity signals a manager posts to itself, never anything <c>FactionBrain.Think</c>
        /// computes), so a Magnate-tier player faction's installed capabilities, ship builds, store
        /// restocking, and inter-node hauling all actually run without the brain ever choosing a
        /// strategic action on the player's behalf.</para>
        ///
        /// <para>Call sites should use this predicate rather than inlining
        /// <see cref="FactionState.IsPlayerFaction"/> directly — a Magnate delegating bounded
        /// Fleet/Trade work to Factions is expressed as an opt-in predicate built on top of this
        /// one, never as a second reading of the field.
        /// Unlike <see cref="IsDormant"/>, this does NOT affect rival/relations/influence visibility
        /// — a player faction must stay a real actor there, just not an autonomous one.</para></summary>
        public static bool IsAutonomyDisabled(FactionState s)
        {
            return s.IsPlayerFaction;
        }

        /// <summary>Whether <paramref name="s"/>'s fleet is decided by its own
        /// <see cref="FactionState.FleetDoctrine"/> rather than by <see cref="ShipyardManager"/>'s
        /// hull-selection heuristics. The SINGLE predicate every doctrine gate reads, for the same
        /// reason <see cref="IsAutonomyDisabled"/> exists: two call sites inlining "player faction
        /// with a non-empty list" is two places to disagree about what a doctrine is.
        ///
        /// <para>What it turns off, in <see cref="ShipyardManager"/>: proactive <c>Ship:{role}</c>
        /// demand posting, and build creation from a <c>Ship:{role}</c> demand posted anywhere else.
        /// What it does NOT touch: the demands themselves (they stay in the register as pressure),
        /// shelf builds for the store (a different purpose -- see
        /// <see cref="BuildQueueEntry.DestinedForSale"/>), dispatch, or any NPC faction.</para>
        ///
        /// <para><b>Empty means unchanged.</b> A player faction with no lines behaves exactly as it
        /// did before the doctrine existed -- there is no third state to reason about, and a player
        /// removing their last line gets the old behaviour back rather than a fleet that quietly
        /// stops growing. The player-faction half is a V1 scope limit, not a property of the
        /// evaluator: <see cref="ShipyardManager.ServiceDoctrine"/> reads the list alone.</para></summary>
        public static bool DoctrineActive(FactionState s)
        {
            return s != null && s.IsPlayerFaction && s.FleetDoctrine.Count > 0;
        }

        /// <summary>True when <paramref name="s"/> names the hulls its store sells, so
        /// <see cref="StoreStockManager"/> stops choosing them. The shelf's counterpart to
        /// <see cref="DoctrineActive"/>, and the same shape for the same reason: one predicate, so the
        /// evaluator and the release pass cannot disagree about whether a player owns the shelf.
        ///
        /// <para>What it turns off: class-balanced hull selection
        /// (<see cref="StoreShelfPlan.NextClass"/> and <see cref="ShipyardManager.SelectPrefabBom"/>'s
        /// diversification branch) and the <see cref="FactionTuning.StoreShipSlotsSmallGrid"/> /
        /// <c>...LargeGrid</c> depth targets, which a per-prefab list replaces outright. What it does
        /// NOT touch: whether the faction has a store at all
        /// (<see cref="FactionStorePolicy.HasPlayerStore"/>, a world-stated faction type), the
        /// per-station shelvability filter, or any NPC.</para>
        ///
        /// <para><b>Empty means unchanged</b>, exactly as an empty doctrine does. "Sell nothing" is
        /// <see cref="ShelfStockingEnabled"/>'s job, because a list cannot express its own
        /// absence.</para></summary>
        public static bool ShelfActive(FactionState s)
        {
            return s != null && s.IsPlayerFaction && s.Shelf.Count > 0;
        }

        /// <summary>True when <paramref name="s"/>'s store may raise ship builds at all.
        ///
        /// <para>Always true for an NPC, and true by DEFAULT for a player faction -- the inverse
        /// polarity of <see cref="TradeSellEnabled"/>, and deliberately so:
        /// <see cref="StoreStockManager"/> already runs for a player faction, so a flag that had to be
        /// switched on would close every existing player store the moment it shipped.</para></summary>
        public static bool ShelfStockingEnabled(FactionState s)
        {
            return s != null && !s.StoreShelfOptOut;
        }

        /// <summary>False only when <paramref name="s"/> does not sell <paramref name="inventoryKey"/>
        /// to the central bank at all. A target rule still answers true: how much it allows is
        /// <c>WorldModel</c>'s faction-wide arithmetic, not a yes/no.</summary>
        public static bool CentralBankBuys(FactionState s, string inventoryKey)
        {
            return s == null || !s.CentralBankDontSell.Contains(inventoryKey ?? "");
        }

        /// <summary>Turns a faction-wide "no bank sales" read from an older brain file into a don't-sell
        /// rule for each of <paramref name="bankBuyableKeys"/>, so the faction sells nothing it was
        /// not selling before. Returns how many entries it added. With no keys to expand into the
        /// flag stays set, and is written back, rather than being dropped as if it had been
        /// honoured.</summary>
        public static int ExpandLegacyBankSellOptOut(FactionState s, IEnumerable<string> bankBuyableKeys)
        {
            if (s == null || !s.LegacyCentralBankSellOptOut || bankBuyableKeys == null) return 0;
            var keys = new List<string>(bankBuyableKeys);
            if (keys.Count == 0) return 0;
            s.LegacyCentralBankSellOptOut = false;
            int added = 0;
            foreach (string key in keys)
                if (s.SetCentralBankRule(key, BankSaleMode.DontSell, 0)) added++;
            return added;
        }

        /// <summary>Rebuilds <paramref name="s"/>'s derived <see cref="FactionState.ReinforcePriority"/>
        /// from its persisted <see cref="FactionState.ReinforceOptIn"/>, dropping entries for nodes it
        /// no longer owns.
        ///
        /// <para>The player-faction counterpart to <c>FactionBrain.ComputeReinforcePriority</c>, and
        /// deliberately the same contract: the manager reads one ranked list and does not care which
        /// side filled it. An opted-in node the faction has since lost is skipped rather than
        /// removed — losing a node to a raid and taking it back should not also cost the player the
        /// instruction to hold it.</para>
        ///
        /// <para>No-op for an NPC: its brain owns that list, and a second writer would compete with
        /// it silently.</para></summary>
        public static void ProjectPlayerReinforcePriority(FactionState s, IWorldView world)
        {
            if (s == null || world == null || !s.IsPlayerFaction) return;
            s.ReinforcePriority.Clear();
            if (s.ReinforceOptIn.Count == 0) return;

            foreach (KeyValuePair<string, int> kv in s.ReinforceOptIn)
            {
                SpatialNode n;
                if (!world.Graph.Nodes.TryGetValue(kv.Key, out n)) continue;
                if (n.OwnerFactionTag != s.Tag) continue;
                s.ReinforcePriority.Add(new ReinforcePriorityEntry { NodeId = kv.Key, Score = kv.Value });
            }
            s.ReinforcePriority.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        /// <summary>Fills <see cref="CampaignObjective.Candidates"/> for every <b>player-issued</b>
        /// offensive objective <paramref name="s"/> carries — the nodes worth attacking under it,
        /// best first.
        ///
        /// <para>The player-faction counterpart to <c>FactionBrain.ComputeCampaignCandidates</c>, and
        /// deliberately the same contract as <see cref="ProjectPlayerReinforcePriority"/>: the
        /// manager reads one ranked list and does not care which side filled it. It has to exist at
        /// all because <c>FactionBrain.Think</c> never runs for a player faction, and an objective
        /// with an empty candidate list is one <c>FleetManager</c>'s offensive block skips — the
        /// directive would stand, cost nothing, and never dispatch.</para>
        ///
        /// <para>A <b>node-scoped</b> directive is its own list of one: naming the node was the
        /// player's whole decision, and ranking a set of one would only give the admission gate a
        /// second chance to silently empty it. A <b>region-scoped</b> one is ranked through
        /// <c>FactionBrain.RankCampaignCandidates</c>, the same body the brain uses.</para>
        ///
        /// <para>Bounded by what the directive already names: a node-scoped one is a dictionary
        /// lookup, and a region-scoped one is the pass the brain would have made over the same
        /// region. Nothing here scans the graph for a faction holding no directive
        /// (rule 5 in CLAUDE.md) — the common case returns on the first line.</para>
        ///
        /// <para>No-op for an NPC: its brain owns those lists, and a second writer would compete with
        /// it silently.</para></summary>
        /// <summary>True when <see cref="TradeManager"/> may fly <paramref name="s"/>'s convoys out
        /// to acquire what its own estate could not source, and when
        /// <see cref="OperationsManager.PublishUnservedDemands"/> may publish its shortage for a
        /// neighbour to serve. Always true for an NPC; opt-in for a player faction.</summary>
        public static bool TradeBuyEnabled(FactionState s)
        {
            if (s == null) return false;
            return !IsAutonomyDisabled(s) || s.TradeBuyOptIn;
        }

        /// <summary>True when <see cref="TradeManager"/> may fly <paramref name="s"/>'s surplus out
        /// to a partner that has posted a demand for it. Always true for an NPC; opt-in for a player
        /// faction.</summary>
        public static bool TradeSellEnabled(FactionState s)
        {
            if (s == null) return false;
            return !IsAutonomyDisabled(s) || s.TradeSellOptIn;
        }

        /// <summary>True when either direction of trade is live for <paramref name="s"/> -- the
        /// eligibility question <see cref="TradeManager"/> asks before doing any work.</summary>
        public static bool TradeEnabled(FactionState s)
        {
            return TradeBuyEnabled(s) || TradeSellEnabled(s);
        }

        /// <summary>Rebuilds <paramref name="s"/>'s derived <see cref="FactionState.TradePriority"/>
        /// for a player faction that has opted in to either direction of trade.
        ///
        /// <para>The player-faction counterpart to <c>FactionBrain.ComputeTradePriority</c>, and --
        /// unlike <see cref="ProjectPlayerReinforcePriority"/>, which projects a list the player
        /// hand-ranked -- it calls <b>the brain's own function</b>, because that function reads no
        /// brain state. Reach, relation and embargo are world and faction facts a player faction has
        /// as much as an NPC does. What the player opted in to is <i>whether</i> to trade, not
        /// <i>whom</i> to rank; ranking a partner list by hand would duplicate a computation that is
        /// already correct. <see cref="ProjectPlayerCampaignCandidates"/> reuses
        /// <c>RankCampaignCandidates</c> the same way.</para>
        ///
        /// <para>Embargo auto-management is switched OFF here. For an NPC the brain owns that set and
        /// rewrites it every pass; a player's embargo is a decision they made, and a projection that
        /// lifted it again next tick would make the setting untrappable.</para>
        ///
        /// <para>No-op for an NPC: its brain owns that list, and a second writer would compete with
        /// it silently. Also a no-op for a player faction with both switches off, which leaves the
        /// list empty and the manager's own eligibility gate to end the pass.</para></summary>
        public static void ProjectPlayerTradePriority(FactionState s, IWorldView world, FactionTuning tuning)
        {
            if (s == null || world == null || tuning == null || !s.IsPlayerFaction) return;
            if (!s.TradeBuyOptIn && !s.TradeSellOptIn) { s.TradePriority.Clear(); return; }
            FactionBrain.ComputeTradePriority(s, world, tuning, false);
        }

        public static void ProjectPlayerCampaignCandidates(FactionState s, IWorldView world, FactionTuning tuning)
        {
            if (s == null || world == null || tuning == null || !s.IsPlayerFaction) return;
            List<OffensiveCampaigns.Entry> directives = OffensiveCampaigns.ActivePlayer(s);
            if (directives.Count == 0) return;

            for (int i = 0; i < directives.Count; i++)
            {
                CampaignObjective objective = directives[i].Objective;
                if (string.IsNullOrEmpty(objective.TargetNodeId))
                {
                    FactionBrain.RankCampaignCandidates(s, objective, true, world, tuning);
                    continue;
                }
                objective.Candidates.Clear();
                SpatialNode target;
                if (!world.Graph.Nodes.TryGetValue(objective.TargetNodeId, out target)) continue;
                if (target.OwnerFactionTag == s.Tag) continue;   // taken: the close scores it next pass
                objective.Candidates.Add(target.Id);
            }
        }

        /// <summary>True when <see cref="ProductionManager"/> may post an <see cref="InvestmentRequest"/>
        /// (and, downstream, reserve real components toward it) for <paramref name="cap"/> on behalf of
        /// <paramref name="s"/>. Always true for an NPC faction — this gate exists only to stop a
        /// Magnate-tier player faction's components from being silently locked toward a capability the
        /// player never chose to install (found live 2026-08-16: `Comp:Computer`/etc. reserved toward
        /// `Shipyard`/`RefineryII` neither the player nor the — permanently disabled for player factions
        /// — brain would ever act on, since nothing but the player's own `develop` command or the brain
        /// can call <see cref="WorldModel.Develop"/>). A player faction must explicitly opt a capability
        /// into <see cref="FactionState.ReservationOptIn"/> (via the host's `!factions station develop
        /// &lt;capability&gt; reserve` chat-command toggle) before ProductionManager will stage
        /// materials toward it.</summary>
        public static bool CanPostInvestmentFor(FactionState s, NodeCapabilityKind cap)
        {
            return !s.IsPlayerFaction || s.ReservationOptIn.Contains(cap);
        }

        /// <summary>
        /// Run once per tick AFTER income/enactment. A faction with no territory left and a ledger at the floor
        /// is marked conquered → dormant (indefinitely, for now). The optional callback surfaces the transition
        /// for logging. Already-dormant factions are left asleep.
        /// </summary>
        public static void Step(IEnumerable<FactionState> roster, WorldModel world, FactionTuning t,
                                Action<string> onConquered = null)
        {
            foreach (FactionState s in roster)
            {
                if (s.Tag == NeutralFaction.Tag || s.DormantUntilUtc > DateTime.MinValue) continue;   // already conquered/dormant (or UNKN, permanently)

                // A human's faction is not a failed NPC economy. Collapse below sets
                // DormantUntilUtc = MaxValue, and there is no revival mechanic anywhere in the tree
                // -- nothing clears that value except a persistence round-trip. For an NPC that is
                // the designed terminal state; for a player it is permanent deregistration, and
                // EnsurePlayerFactionRegistered returns the existing state for a known tag, so
                // claiming a new node does NOT bring it back. Abandonment is designed to cost the
                // node, the same as it does an NPC -- not the faction.
                if (s.IsPlayerFaction) continue;

                // Collapse: no territory left AND the ledger is pinned at the floor (out of credit). The world
                // has already reclaimed the drains via force-shed; what's left is an empty, broke faction.
                bool landless = !world.Graph.OwnedBy(s.Tag).Any();
                if (landless && world.GetTreasury(s.Tag) <= t.InsolvencyFloor)
                {
                    s.DormantUntilUtc = DateTime.MaxValue;   // dormant until a (future) revival mechanic wakes it
                    if (onConquered != null) onConquered(s.Tag);
                }
            }
        }
    }
}
