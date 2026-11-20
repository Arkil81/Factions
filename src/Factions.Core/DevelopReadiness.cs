using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Cascade-prerequisite mapping for capability installation, single-sourced so the
    /// brain's own exploratory Develop scoring (<see cref="FactionBrain.AddDevelopCandidates"/>) and
    /// the Magnate-tier player-triggered Develop command (<see cref="DevelopStationValidator"/>)
    /// never drift apart.</summary>
    public static class CapabilityPrerequisites
    {
        public static NodeCapabilityKind? For(NodeCapabilityKind cap)
        {
            switch (cap)
            {
                case NodeCapabilityKind.Manufactory:   return NodeCapabilityKind.Refinery;
                case NodeCapabilityKind.ManufactoryII: return NodeCapabilityKind.Manufactory;
                case NodeCapabilityKind.Shipyard:      return NodeCapabilityKind.Manufactory;
                case NodeCapabilityKind.RefineryII:    return NodeCapabilityKind.Refinery;
                // Prototech machines sit above their vanilla counterparts, not beside them: the forge
                // only makes prototech components and the prototech refinery only refines faster, so
                // neither is a substitute for the tier it upgrades.
                case NodeCapabilityKind.PrototechForge:    return NodeCapabilityKind.ManufactoryII;
                case NodeCapabilityKind.PrototechRefinery: return NodeCapabilityKind.RefineryII;
                // A FoodProcessor turns crops into meals; with no Farm anywhere in the faction it has
                // nothing to cook. Faction-wide, like every prerequisite here, so the crops may be
                // hauled in from a farm on another node.
                case NodeCapabilityKind.FoodProcessor:     return NodeCapabilityKind.Farm;
                default:                               return null;
            }
        }

        /// <summary>Network-wide gate: true if any node in <paramref name="owned"/> already has
        /// <paramref name="cap"/> installed.</summary>
        public static bool IsInstalledAnywhere(IEnumerable<SpatialNode> owned, NodeCapabilityKind cap)
        {
            foreach (SpatialNode n in owned)
            {
                int lvl;
                if (n.Capabilities.TryGetValue(cap, out lvl) && lvl >= 1) return true;
            }
            return false;
        }
    }

    public enum DevelopStationResult
    {
        Ok, NodeNotFound, NotOwned, NotFounded, AlreadyInstalled,
        PrerequisiteMissing, InsufficientTreasury, InsufficientItemStock,
        /// <summary>The capability exists but is not a player's to install — see
        /// <see cref="CapabilityTokens.PlayerDevelopable"/>.</summary>
        NotPlayerDevelopable
    }

    /// <summary>Magnate-tier player-triggered Develop readiness check (see
    /// docs/economy-territory.md -> "Player-faction claiming" -> Managing (post-Found)). Mirrors
    /// <c>FactionRuntime.Enact</c>'s brain-driven Develop gate exactly (flat
    /// <see cref="FactionTuning.DevelopCost"/> credits, plus real item stock via a registered BOM
    /// or flat <see cref="FactionTuning.InstallCostFor"/> costs) so a player-triggered install is
    /// never cheaper than the same install would be for an NPC faction. Also mirrors the brain's own
    /// <see cref="FactionTuning.MaxLevelFor"/> upgrade ceiling (used by
    /// <c>InvestmentTargeting</c>) rather than capping every capability at level 1, so
    /// a player can repeat this command to push Refinery/Manufactory past their initial install up to
    /// the same per-level slot cap an NPC faction gets. Pure policy, single-sourced and
    /// offline-testable.</summary>
    public static class DevelopStationValidator
    {
        public static DevelopStationResult Validate(IWorldView world, FactionTuning tuning, string factionTag,
            string nodeId, NodeCapabilityKind capability,
            out NodeCapabilityKind? missingPrereq, out long treasuryCost, out string missingItemKey,
            out long missingItemHave, out long missingItemNeed)
        {
            missingPrereq = null;
            treasuryCost = 0;
            missingItemKey = null;
            missingItemHave = 0;
            missingItemNeed = 0;

            if (!CapabilityTokens.IsPlayerDevelopable(capability))
                return DevelopStationResult.NotPlayerDevelopable;

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return DevelopStationResult.NodeNotFound;
            if (!string.Equals(node.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return DevelopStationResult.NotOwned;
            if (node.StableStationId == 0)
                return DevelopStationResult.NotFounded;

            int level;
            node.Capabilities.TryGetValue(capability, out level);
            if (level >= tuning.MaxLevelFor(capability)) return DevelopStationResult.AlreadyInstalled;

            NodeCapabilityKind? prereq = CapabilityPrerequisites.For(capability);
            if (prereq.HasValue && !CapabilityPrerequisites.IsInstalledAnywhere(world.Graph.OwnedBy(factionTag), prereq.Value))
            {
                missingPrereq = prereq;
                return DevelopStationResult.PrerequisiteMissing;
            }

            treasuryCost = tuning.DevelopCost;
            if (world.GetTreasury(factionTag) < treasuryCost) return DevelopStationResult.InsufficientTreasury;

            // Item-stock gate — mirrors FactionRuntime.Enact's Develop branch: BOM-backed
            // capabilities check exact component keys; otherwise flat Ore/Ingot/ProcessedGood/
            // Hydrogen via InstallCostFor. No explicit deduction happens here or in Enact itself —
            // this mirrors the NPC path's own (check-only) behavior rather than introducing new
            // consumption semantics.
            Dictionary<string, long> bom;
            if (tuning.TryGetInstallBom(capability, out bom))
            {
                foreach (KeyValuePair<string, long> kv in bom)
                {
                    long have = world.GetFactionInventoryByKey(factionTag, kv.Key)
                              + world.GetFactionReservedInventoryByKey(factionTag, kv.Key);
                    if (have < kv.Value)
                    {
                        missingItemKey = kv.Key;
                        missingItemHave = have;
                        missingItemNeed = kv.Value;
                        return DevelopStationResult.InsufficientItemStock;
                    }
                }
            }
            else
            {
                ItemKind[] kinds = { ItemKind.Ore, ItemKind.Ingot, ItemKind.ProcessedGood, ItemKind.Hydrogen };
                foreach (ItemKind kind in kinds)
                {
                    long need = tuning.InstallCostFor(capability, kind);
                    if (need <= 0) continue;
                    long have = world.GetInventory(factionTag, kind) + world.GetReservedInventory(factionTag, kind);
                    if (have < need)
                    {
                        missingItemKey = kind.ToString();
                        missingItemHave = have;
                        missingItemNeed = need;
                        return DevelopStationResult.InsufficientItemStock;
                    }
                }
            }

            return DevelopStationResult.Ok;
        }
    }

    /// <summary>One capability's install readiness at one node, as a menu row rather than a verdict
    /// on a capability the caller already picked. <see cref="BlockHave"/>/<see cref="BlockNeed"/>
    /// carry the pair behind whichever shortfall <see cref="Result"/> names, so a caller can say how
    /// far short the install is without re-deriving the number the gate already computed.</summary>
    public struct DevelopOption
    {
        public NodeCapabilityKind Capability;
        /// <summary>Level installed at this node now, and the ceiling
        /// <see cref="FactionTuning.MaxLevelFor"/> puts on it.</summary>
        public int Level;
        public int MaxLevel;
        /// <summary>Credits the next install would cost. Zero when no install is possible at all —
        /// the gate stops before it reaches the price.</summary>
        public long TreasuryCost;
        public DevelopStationResult Result;
        /// <summary>The prerequisite's name for <see cref="DevelopStationResult.PrerequisiteMissing"/>,
        /// the item key for <see cref="DevelopStationResult.InsufficientItemStock"/>, empty otherwise
        /// — a treasury shortfall is named by its two numbers alone.</summary>
        public string BlockKey;
        public long BlockHave;
        public long BlockNeed;
    }

    /// <summary>
    /// Every capability's install readiness at one node in a single pass — what a player choosing
    /// what to develop needs to see before clicking, rather than after.
    ///
    /// <para>It calls <see cref="DevelopStationValidator.Validate"/> once per capability rather than
    /// re-deriving the cascade: the preview and the install must never disagree about whether an
    /// install would be refused, and the only way to guarantee that is for both to run the same
    /// gate. The extra cost is one owned-node scan per prerequisite-bearing capability, paid only
    /// while a player has the develop menu open.</para>
    ///
    /// <para>The menu is <see cref="CapabilityTokens.PlayerDevelopable"/>, not
    /// <see cref="FactionTuning.AllCapabilities"/>, which omits <c>SensorArray</c> because it lists
    /// what the brain's own exploratory Develop scoring walks. A player can already install a
    /// SensorArray by naming it, so a menu built from the brain's list would hide a capability the
    /// verb behind it accepts.</para>
    /// </summary>
    public static class DevelopStationPreview
    {
        public static List<DevelopOption> For(IWorldView world, FactionTuning tuning, string factionTag,
                                              string nodeId)
        {
            var options = new List<DevelopOption>(CapabilityTokens.PlayerDevelopable.Length);
            if (world == null || tuning == null || string.IsNullOrEmpty(factionTag)) return options;

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return options;

            foreach (NodeCapabilityKind cap in CapabilityTokens.PlayerDevelopable)
            {
                NodeCapabilityKind? missingPrereq;
                long cost;
                string missingItemKey;
                long missingItemHave, missingItemNeed;
                DevelopStationResult result = DevelopStationValidator.Validate(
                    world, tuning, factionTag, nodeId, cap, out missingPrereq, out cost,
                    out missingItemKey, out missingItemHave, out missingItemNeed);

                int level;
                node.Capabilities.TryGetValue(cap, out level);

                var option = new DevelopOption
                {
                    Capability = cap,
                    Level = level,
                    MaxLevel = tuning.MaxLevelFor(cap),
                    TreasuryCost = cost,
                    Result = result,
                    BlockKey = "",
                };

                if (result == DevelopStationResult.PrerequisiteMissing && missingPrereq.HasValue)
                {
                    option.BlockKey = missingPrereq.Value.ToString();
                }
                else if (result == DevelopStationResult.InsufficientItemStock)
                {
                    option.BlockKey = missingItemKey ?? "";
                    option.BlockHave = missingItemHave;
                    option.BlockNeed = missingItemNeed;
                }
                else if (result == DevelopStationResult.InsufficientTreasury)
                {
                    // Validate stops at the treasury gate with the cost already resolved, so the
                    // shortfall is the pair (what the faction holds, what the install wants).
                    option.BlockHave = world.GetTreasury(factionTag);
                    option.BlockNeed = cost;
                }

                options.Add(option);
            }
            return options;
        }
    }

    /// <summary>Capability-token parsing for the admin grant form, kept off
    /// <see cref="FactionTuning.AllCapabilities"/> on purpose. That array is the set the brain's own
    /// exploratory Develop scoring walks, and it is shorter than
    /// <see cref="NodeCapabilityKind"/> — it never grew a <c>SensorArray</c> entry, so nothing the
    /// brain proposes can ever be one. The grant form is the operator's lever, not the brain's, and
    /// <c>SensorArray</c> is a live capability that <c>Knowledge.NodeStrengthKm</c> and
    /// <c>FleetSensing</c> both read, so it has to be reachable and listed here.
    ///
    /// <para>A bare <c>Enum.TryParse</c> is not enough: it accepts any numeric string, so "99"
    /// parses to <c>(NodeCapabilityKind)99</c> and would be written into the node's persisted
    /// capability map as a key no reader has a name for.</para></summary>
    public static class CapabilityTokens
    {
        /// <summary>Every capability an operator may grant — the whole enum, in declaration
        /// order.</summary>
        public static readonly NodeCapabilityKind[] Grantable =
            (NodeCapabilityKind[])Enum.GetValues(typeof(NodeCapabilityKind));

        /// <summary>Every capability a player faction may install for itself — the develop menu's
        /// rows and what <see cref="DevelopStationValidator"/> accepts.
        ///
        /// <para>The prototech tier is absent. Both machines exist to make the Factorum's own hulls
        /// buildable (docs/economy-territory.md -> "The prototech tier"): a forge is the only source
        /// of <c>Comp:PrototechFrame</c>, and the prototech refinery's install BOM is itself a
        /// prototech block, so a player faction offered the pair could reach neither — the forge's
        /// own BOM is the thing no player-reachable production can fill. Offering them as menu rows
        /// advertised a tier that is not the players'.</para>
        ///
        /// <para>This is not <see cref="Grantable"/>: an operator may still grant either outright,
        /// and the brain's own investment path is untouched, which is how FCTM gets them.</para></summary>
        public static readonly NodeCapabilityKind[] PlayerDevelopable = BuildPlayerDevelopable();

        /// <summary>True if <paramref name="cap"/> is in <see cref="PlayerDevelopable"/>.</summary>
        public static bool IsPlayerDevelopable(NodeCapabilityKind cap)
        {
            return cap != NodeCapabilityKind.PrototechForge
                && cap != NodeCapabilityKind.PrototechRefinery
                && Enum.IsDefined(typeof(NodeCapabilityKind), cap);
        }

        private static NodeCapabilityKind[] BuildPlayerDevelopable()
        {
            var list = new List<NodeCapabilityKind>(Grantable.Length);
            foreach (NodeCapabilityKind cap in Grantable)
                if (IsPlayerDevelopable(cap)) list.Add(cap);
            return list.ToArray();
        }

        public static bool TryParse(string token, out NodeCapabilityKind capability)
        {
            capability = default(NodeCapabilityKind);
            if (string.IsNullOrEmpty(token)) return false;
            NodeCapabilityKind parsed;
            if (!Enum.TryParse(token, true, out parsed)) return false;
            if (!Enum.IsDefined(typeof(NodeCapabilityKind), parsed)) return false;
            capability = parsed;
            return true;
        }

        /// <summary>Comma-separated list for a usage/error line.</summary>
        public static string Names()
        {
            return string.Join(", ", Enum.GetNames(typeof(NodeCapabilityKind)));
        }

        /// <summary>Comma-separated <see cref="PlayerDevelopable"/> — the list a player-facing usage
        /// or error line quotes, so it never names a capability the verb behind it refuses.</summary>
        public static string PlayerNames()
        {
            var names = new List<string>(PlayerDevelopable.Length);
            foreach (NodeCapabilityKind cap in PlayerDevelopable) names.Add(cap.ToString());
            return string.Join(", ", names);
        }
    }

    public enum CapabilityGrantResult { Ok, NodeNotFound, Unowned, NotFounded }

    /// <summary>Readiness check for an operator-driven capability grant — the policy behind the
    /// admin form of `develop`, `&lt;nodeId&gt; &lt;capability&gt; &lt;level&gt;`. Deliberately NOT
    /// <see cref="DevelopStationValidator"/>: a grant names a level outright and charges nothing, so
    /// the prerequisite cascade, the treasury cost and the component BOM are all absent. What it
    /// keeps is the pair of structural checks the rest of the engine depends on — a real faction
    /// holds the node (a derelict <see cref="NeutralFaction"/> node has no brain to run the
    /// capability), and it has a founded station, because a capability on an unfounded node still
    /// satisfies <c>ShipyardManager.NetworkHasCapability</c> and the brain's own network-wide
    /// prerequisite gate.
    ///
    /// <para>The owner is read off the node rather than named by the caller: unlike the player verb
    /// there is no caller faction to check the node against, and an admin naming a node someone else
    /// holds is the point of the form.</para>
    ///
    /// <para>The requested level is clamped to [0, <see cref="FactionTuning.MaxLevelFor"/>] rather
    /// than refused: 0 removes the capability, and levels above the cap are unreachable by every
    /// other path and mean nothing to the yield tables.</para></summary>
    public static class CapabilityGrantValidator
    {
        public static CapabilityGrantResult Validate(IWorldView world, FactionTuning tuning,
            string nodeId, NodeCapabilityKind capability, int requestedLevel,
            out string ownerTag, out int currentLevel, out int grantedLevel, out int maxLevel)
        {
            ownerTag = null;
            currentLevel = 0;
            grantedLevel = 0;
            maxLevel = tuning.MaxLevelFor(capability);

            SpatialNode node;
            if (string.IsNullOrEmpty(nodeId) || !world.Graph.Nodes.TryGetValue(nodeId, out node))
                return CapabilityGrantResult.NodeNotFound;
            if (NeutralFaction.IsClaimable(node.OwnerFactionTag))
                return CapabilityGrantResult.Unowned;
            if (node.StableStationId == 0)
                return CapabilityGrantResult.NotFounded;

            ownerTag = node.OwnerFactionTag;
            node.Capabilities.TryGetValue(capability, out currentLevel);
            grantedLevel = requestedLevel < 0 ? 0 : (requestedLevel > maxLevel ? maxLevel : requestedLevel);
            return CapabilityGrantResult.Ok;
        }
    }
}
