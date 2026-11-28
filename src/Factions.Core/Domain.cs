using System;
using System.Collections.Generic;
using System.Linq;

namespace Factions.Core
{
    // ---- primitives ----------------------------------------------------------------------------

    /// <summary>Plain domain coordinate (no VRageMath ÔÇö the engine stays SE-free).</summary>
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double DistanceTo(Vec3 o)
        {
            double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        public double DistanceSquaredTo(Vec3 o)
        {
            double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return dx * dx + dy * dy + dz * dz;
        }
        public override string ToString() => "(" + (int)X + "," + (int)Y + "," + (int)Z + ")";
    }

    public enum Drive { Security = 0, Growth = 1, Wealth = 2, Rivalry = 3, Caution = 4 }

    /// <summary>Abstract item types for the item-economy layer. Deliberately coarse ÔÇö SE item
    /// categories map to these tiers in a lookup table.</summary>
    public enum ItemKind { Ore = 0, ProcessedGood = 1, Hydrogen = 2, Oxygen = 3, RareOre = 4, Ingot = 5 }

    /// <summary>How the central bank may buy one item from one faction. Every mode keeps the bank's own
    /// rule -- it buys only a node's oversupply (scarcity at the floor) -- and narrows it.</summary>
    public enum BankSaleMode : byte
    {
        /// <summary>The default: the bank buys a slice of any node's oversupply.</summary>
        SellOversupply = 0,
        /// <summary>As above, but never below a faction-wide stock target.</summary>
        AboveTarget = 1,
        /// <summary>The bank does not buy it.</summary>
        DontSell = 2,
    }

    /// <summary>Sub-classification within <see cref="ItemKind.ProcessedGood"/> used for store/production
    /// cap sizing. Component covers generic components and anything unrecognized.</summary>
    public enum ProcessedGoodCategory { Component = 0, Weapon = 1, Ammunition = 2, Consumable = 3 }

    /// <summary>Spatial context of a node: drives separation thresholds and surface-snapping.
    /// Assigned by <c>WorldModel.ExpandFrom</c> from live SE gravity at placement time.
    /// Scan-backed (E###) nodes inherit the zone of their real-world position on the next tick.</summary>
    public enum NodeZone { DeepSpace = 0, Orbit = 1, Surface = 2 }

    /// <summary>Infrastructure capabilities that can be installed on owned nodes via the Develop action.
    /// Each capability provides a yield bonus and/or unlock for later slices.
    /// Level ÔëÑ 1 = installed; level 0 (absent) = not installed. Level granularity unused until Slice 6.</summary>
    /// <summary>Ordinals are persisted and are a wire contract: <see cref="NodeDisplay.CapabilityMask"/>
    /// packs one bit per ordinal into a 16-bit field, so every kind up to ordinal 15 can badge.
    /// <see cref="Farm"/> and <see cref="FoodProcessor"/> are the food chain
    /// (docs/food-and-population.md): a Farm grows crops from ice, a FoodProcessor turns crops into
    /// meals and seeds.</summary>
    public enum NodeCapabilityKind { Refinery = 0, Manufactory = 1, Shipyard = 2, FuelDepot = 3, ManufactoryII = 4, RefineryII = 5, SensorArray = 6, PrototechForge = 7, PrototechRefinery = 8, Farm = 9, FoodProcessor = 10, Habitat = 11 }

    /// <summary>One entry in a node's per-tick SubTypeId yield table.
    /// Stored in <see cref="FactionTuning.NodeYieldTable"/> and hot-reloaded via tuning.json.
    /// All entries for the same <see cref="ItemKind"/> should have weights summing to 1.0.</summary>
    public sealed class NodeYieldEntry
    {
        public string SubTypeId;
        public string ItemKind; // "Ore" | "ProcessedGood" | "Hydrogen" | "Oxygen" | "RareOre" | "Ingot"
        public double Weight;

        /// <summary>Obtainable only by an actual Mine run, never by the passive per-tick income mint.
        /// <see cref="WorldModel"/>'s income pass skips these entries entirely; Mine reads the table
        /// unchanged, so the material is a normal mining target with a normal yield weight. For a
        /// material whose real-world supply is effectively unlimited (stone), an effort gate is what
        /// bounds it -- a passive faucet would not.
        /// <para>Only <see cref="ItemKind"/>.Ore and RareOre entries are ever minted by a Mine run, so
        /// an entry on any other channel with this flag set is minted by nobody. That is not a way to
        /// retire a channel: delete the row and its <see cref="FactionTuning.FreeMintScale"/> key
        /// instead, or the kind keeps its income valuation with no supply behind it (FAC-233).</para></summary>
        public bool MineOnly;
    }

    /// <summary>Physical asteroid-field sizing. Count/size scale with a node's
    /// <see cref="SpatialNode.ResourceValue"/> up to <see cref="MaxCount"/> -- the cap, not RV alone,
    /// is what keeps a high-RV facility node (e.g. Refinery) from being buried in rock just because
    /// its RV happens to be high. Stored in <see cref="FactionTuning.AsteroidField"/>.</summary>
    public sealed class NodeKindAsteroidFieldEntry
    {
        public int    MaxCount;
        public double SizeMin;
        public double SizeMax;
        public double RadiusKm;
    }

    /// <summary>Fixed-size 0..1 pressure per drive ÔÇö tiny, no allocation churn.</summary>
    public sealed class DriveVector
    {
        public const int Count = 5;
        private readonly double[] _v = new double[Count];
        public double this[Drive d] { get { return _v[(int)d]; } set { _v[(int)d] = value; } }
        public override string ToString()
            => "sec " + _v[0].ToString("0.00") + " gro " + _v[1].ToString("0.00") +
               " wea " + _v[2].ToString("0.00") + " riv " + _v[3].ToString("0.00") +
               " cau " + _v[4].ToString("0.00");
    }

    public enum ActorKind { Player = 0, Faction = 1, Neutral = 2 }

    public enum ActionKind { Idle = 0, Expand = 1, Claim = 2, Reinforce = 3, Guard = 4, Withdraw = 5, Retaliate = 6, Develop = 7, Raid = 8, Negotiate = 9, Trade = 10, Found = 11, Gift = 12, Patrol = 13, Ferry = 14, DismantleCapability = 15, FinishBuild = 16, ProposePact = 17, Campaign = 18, Conquer = 19, Muster = 20, Haul = 21, Mine = 22 }

    /// <summary>Why a faction holds a grudge against another.</summary>
    public enum GrudgeKind { TruceBreached = 0 }

    /// <summary>A time-bounded grudge record written when a faction breaches a truce.</summary>
    public struct GrudgeRecord
    {
        public GrudgeKind Kind;
        public DateTime ExpiresUtc;
    }

    /// <summary>Diplomatic intent carried inside a <see cref="DiplomacyMessage"/>.</summary>
    public enum DiplomacyVerb
    {
        Truce        = 0,   // unconditional ceasefire (mirrors existing Negotiate)
        NonAggression = 1,  // truce + credit payment; Param = credit amount as string
        Alliance     = 2,   // formal mutual-defense pact; bilateral acceptance required
        SuggestWar   = 3,   // recommend war against Param = target faction tag (unilateral advisory)
    }

    /// <summary>A single diplomatic term: what is being proposed and an optional string parameter.
    /// Multiple terms can be bundled in one <see cref="DiplomacyMessage"/> to express compound offers
    /// (e.g. truce + payment, or two simultaneous suggestions).</summary>
    public struct DiplomacyTerm
    {
        public DiplomacyVerb Verb;
        public string Param;  // verb-specific: empty for Truce/Alliance; credits for NonAggression; faction tag for SuggestWar
    }

    /// <summary>A message sent from one faction to another via <see cref="Diplomacy.Propose"/>.
    /// Stored in <see cref="FactionState.Proposals"/> (keyed by recipient tag) and pruned when stale
    /// or resolved.</summary>
    public sealed class DiplomacyMessage
    {
        public DiplomacyTerm[] Terms;
        public DateTime IssuedUtc;
    }

    /// <summary>RNG abstraction so simulations are reproducible under a fixed seed.</summary>
    public interface IRandom { double NextDouble(); }

    public sealed class SeededRandom : IRandom
    {
        private readonly Random _r;
        public SeededRandom(int seed) { _r = new Random(seed); }
        public double NextDouble() { return _r.NextDouble(); }
    }

    internal static class MathUtil
    {
        public static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // ---- world model (dynamic graph) -----------------------------------------------------------

    /// <summary>An actor sensed at a node. Hostility is an explicit aggressive act ÔÇö NOT implied by
    /// merely being a player/foreigner. Whether neutral presence matters is decided by faction traits.</summary>
    public sealed class Presence
    {
        public string ActorId;
        public ActorKind Kind;
        public bool Hostile;
    }

    /// <summary>A hull that came home damaged and waits at a node for repair instead of rejoining
    /// <see cref="SpatialNode.ShipStock"/>. <see cref="Bill"/> is the share of the prefab's BOM its
    /// missing integrity costs (<see cref="HullDamage.RepairBill"/>), fixed when it lands; the
    /// yard absorbs it into <see cref="Collected"/> from node inventory, then works the hull for
    /// <see cref="HullDamage.RepairTime"/> from <see cref="PartsCompleteUtc"/>, then credits it to
    /// stock whole. See <see cref="ShipyardManager"/>'s repair pass.</summary>
    public sealed class DamagedHull
    {
        public string PrefabId;
        /// <summary>0..1 integrity the hull landed with.</summary>
        public double Integrity;
        public DateTime QueuedUtc;
        /// <summary>When the last component of <see cref="Bill"/> was absorbed; default until then.</summary>
        public DateTime PartsCompleteUtc;
        public readonly Dictionary<string, long> Bill = new Dictionary<string, long>();
        public readonly Dictionary<string, long> Collected = new Dictionary<string, long>();

        /// <summary>The version this hull is being refit to (<see cref="HullRefit"/>); null for a
        /// repair. A refit's <see cref="Bill"/> is the net difference between the two designs, and
        /// the hull rejoins stock as this id.</summary>
        public string RefitTo;
        /// <summary>For a refit, the share of the target design's bill that changes, 0..1: what sets
        /// its yard time in place of <see cref="Integrity"/>.</summary>
        public double RefitShare;

        public bool IsRefit { get { return !string.IsNullOrEmpty(RefitTo); } }

        /// <summary>The whole bill, in units.</summary>
        public long BillUnits()
        {
            long units = 0;
            foreach (KeyValuePair<string, long> kv in Bill) if (kv.Value > 0) units += kv.Value;
            return units;
        }

        /// <summary>Units of <see cref="Bill"/> not yet in <see cref="Collected"/> -- what the yard
        /// still has to find before it can start work. Zero once the parts are in.</summary>
        public long OutstandingUnits()
        {
            long units = 0;
            foreach (KeyValuePair<string, long> kv in Bill)
            {
                long have;
                Collected.TryGetValue(kv.Key, out have);
                if (kv.Value > have) units += kv.Value - have;
            }
            return units;
        }

        /// <summary>Units absorbed toward <see cref="Bill"/>, never more than the bill asks for.</summary>
        public long CollectedUnits()
        {
            long units = BillUnits() - OutstandingUnits();
            return units < 0 ? 0 : units;
        }

        /// <summary>Every component is in and the yard is working the hull.</summary>
        public bool PartsComplete { get { return PartsCompleteUtc != default(DateTime); } }
    }

    /// <summary>
    /// A pending ship build at a Shipyard node. Holds its own component inventory
    /// (<see cref="Collected"/>) so earmarked materials are physically separate from the
    /// node's routable stock ÔÇö OperationsManager never needs to know about reservations.
    /// <see cref="ReservedInputs"/> extends the same isolation one level upstream: raw/intermediate
    /// recipe inputs earmarked toward producing this entry's still-missing BOM components, held out
    /// of routable stock the same way (see ProductionManager's ship-build reservation pass).
    /// Persisted in the brain JSON so multi-day builds survive reboots.
    /// </summary>
    public sealed class BuildQueueEntry
    {
        /// <summary>Stable ID: "role:seq" e.g. "Trade:001", or "B:seq" for a build that carries no
        /// role (see <see cref="SpatialNode.MintBuildEntryId"/>).</summary>
        public string Id;
        /// <summary>The <c>Ship:{role}</c> demand this build was opened for, the shelf role it
        /// stocks, or <see cref="ShipyardManager.StationRefitRole"/>. <b>Null for a build a player or
        /// a doctrine line named by hull</b>: what such a build answers is read off the hull
        /// (<see cref="ShipyardManager.EntryAnswersRole"/>), because a combat hull fits Raid and
        /// Patrol alike and no role written on the entry could say which.</summary>
        public string Role;
        public string PrefabId;   // null = free build (no BOM)
        /// <summary>True when this build stocks a player store rather than the faction's own fleet.
        /// Carried from the <c>DemandEntry.DestinedForSale</c> that opened it, and part of the
        /// entry's identity: one yard may work a shelf build and a fleet build of the SAME role at
        /// once, because with the store roles retired the role string no longer tells them apart.
        /// Read when the finished hull is credited (a shelf build reserves it into
        /// <see cref="SpatialNode.ListedForSale"/>) and when a store build is cancelled.</summary>
        public bool DestinedForSale;
        /// <summary>Claim rank on one shared scale with <see cref="FactionState.InstallPriority"/>.
        /// Higher wins a contested inventory key. 0 (the default for both sides) reproduces the
        /// original behaviour: a build blocks a capability install when
        /// <c>Priority &gt;= installRank</c>, so a tie goes to the build. Enforced as a floor via
        /// <see cref="SpatialNode.BuildQueueReserved"/>'s <c>minPriority</c>, never as an iteration
        /// order, so it holds regardless of which loop visits an entry first.</summary>
        public int Priority;
        /// <summary>True when a human set <see cref="Priority"/> by hand
        /// (<c>!factions station queue priority</c>), rather than it being the role-derived rank
        /// <c>ShipyardManager.RolePriority</c> gave the entry at creation.
        ///
        /// <para><b>Recorded rather than inferred, because the inference is wrong.</b>
        /// <see cref="BuildSlotAllocation"/> treats a hand-set rank as an instruction about the
        /// working slot, and comparing <see cref="Priority"/> against today's
        /// <see cref="FactionTuning.BuildRolePriority"/> to detect one misreads every entry that
        /// outlived a change to that table: a shelf build queued under the retired <c>StoreSmall: 2</c>
        /// row still carries rank 2 where the table now answers 1, and read as a human instruction it
        /// was admitted ahead of the whole fleet.</para>
        ///
        /// <para>Absent from an older brain, which loads as false — the entry then falls back to its
        /// natural band, which is what such an entry always meant.</para></summary>
        public bool PriorityByPlayer;
        /// <summary>Full BOM: inventory key ÔåÆ total units required (same key format as node.Inventory).</summary>
        public readonly Dictionary<string, long> Bom       = new Dictionary<string, long>();
        /// <summary>Components already absorbed from node inventory into this build slot.</summary>
        public readonly Dictionary<string, long> Collected = new Dictionary<string, long>();

        /// <summary>Total serviced seconds this entry has existed for, progress or not — the
        /// denominator of its inflow rate, where <see cref="StalledSeconds"/> is a gap detector that
        /// resets on every arrival.
        ///
        /// <para><b>Why a second clock rather than a reading of the first.</b> The stall rule asks
        /// "did anything arrive?" and a build absorbing a trickle answers yes forever. The rate asks
        /// "will this finish in a time that matters?", and that needs elapsed serviced time that does
        /// NOT reset. Accumulated with the same per-sweep clamp
        /// (<c>ShipyardManager.MaxStallDeltaSeconds</c>) and for the same reason: the host is a
        /// desktop that sleeps overnight, and raw wall-clock would read a shutdown as build time.</para>
        ///
        /// <para>The numerator is the sum of <see cref="Collected"/>, so <b>both</b> supply paths
        /// count — absorption from node inventory and <c>ProductionManager</c> manufacturing straight
        /// into this dictionary. Deriving the rate from the absorb loop's own counter instead would
        /// repeat the defect where a build accrued stall time while visibly filling up.</para></summary>
        public double ServicedSeconds;
        /// <summary>Raw/intermediate recipe inputs reserved out of the node's routable inventory,
        /// earmarked toward recipes that produce this entry's still-outstanding BOM components.
        /// Inventory key ÔåÆ units held. Never touched by any consumer other than this entry's own
        /// production pass; refunded to routable inventory when the entry expires or is removed.</summary>
        public readonly Dictionary<string, long> ReservedInputs = new Dictionary<string, long>();
        /// <summary>Per-recipe production accumulator for this entry's own dedicated first-pass
        /// production (BlueprintId ÔåÆ progress) ÔÇö mirrors ProductionManager.MachineSlot's Accum/LastTick
        /// shape but scoped to this build entry instead of a shared capability-bank machine slot.</summary>
        public readonly Dictionary<string, BuildRecipeSlot> RecipeProgress = new Dictionary<string, BuildRecipeSlot>();
        public DateTime CreatedUtc;
        /// <summary>Vestigial. No longer consulted for abandonment — <see cref="StalledSeconds"/> is
        /// the only rule, so a build that is still absorbing components is never dropped however long
        /// it takes. Still written and loaded so an older brain doc round-trips unchanged, and still
        /// stamped at creation for display; remove once nothing reads it.</summary>
        public DateTime ExpiresUtc;

        /// <summary>Accumulated *serviced* time this entry has gone without absorbing anything, in
        /// seconds. Reset to 0 the moment any component lands in <see cref="Collected"/>; abandoned
        /// once it crosses <see cref="FactionTuning.BuildNoProgressHours"/>.
        /// <para>Accumulated as a per-sweep delta CLAMPED to a small multiple of the Shipyard
        /// cadence, never as raw wall-clock. The host runs on a desktop that is shut down overnight
        /// and <c>WorldModel.UtcNow</c> is real UTC, so an un-clamped elapsed check would read a
        /// 10-hour shutdown as 10 hours of no progress and abandon every build in the network on the
        /// next boot. The clamp also covers the inverse case a load-time downtime correction would
        /// miss: a process that is alive but whose Shipyard actor is starved accrues only what it is
        /// actually serviced.</para></summary>
        public double StalledSeconds;

        /// <summary>When <see cref="ShipyardManager"/> last serviced this entry — the baseline the
        /// clamped <see cref="StalledSeconds"/> delta is measured from. <c>default(DateTime)</c> on a
        /// freshly loaded or newly created entry, which the clamp turns into one interval rather than
        /// an enormous first delta, so no special load-migration path is needed.</summary>
        public DateTime LastServicedUtc;
    }

    /// <summary>One recipe's fractional-batch production progress within a single
    /// <see cref="BuildQueueEntry"/>'s dedicated reservation pass. See <see cref="BuildQueueEntry.RecipeProgress"/>.</summary>
    public sealed class BuildRecipeSlot
    {
        public double Accum;
        public DateTime LastTick;
    }

    public sealed class SpatialNode
    {
        public string Id;
        public Vec3 Position;
        public string OwnerFactionTag;        // null = unclaimed (transient); NeutralFaction.Tag ("UNKN") = abandoned, claimable
        public double HoldStrength;           // 0..1, owner's grip (also the influence-source strength)
        /// <summary>Explicit influence-radius override in km, for constructed worlds only (the
        /// offline sim's scenarios set it deliberately). <b>0 = derive it</b> from what the node is,
        /// which is what every host-side node does — see <see cref="InfluenceRadius"/>. Never
        /// persisted: a stored radius would pin a node to what it was worth the day it was saved.</summary>
        public double Radius;
        public double ResourceValue;          // 0..1, attractiveness

        /// <summary>How much ore is under this node, 0..1 — the physical quantity every yield and
        /// drill-rate path scales by. Derived from the deposit's measured density by
        /// <see cref="NodeContent.RichnessFor"/> and refreshed wherever the deposit changes; never
        /// assigned directly outside that path.
        ///
        /// <para><b>Not <see cref="ResourceValue"/>, and the pair is the point.</b> Worth is
        /// price-weighted and answers "is this ground worth owning"; richness is price-blind and
        /// answers "how much rock is here". Measured live 2026-08-31 the two are close to inversely
        /// ordered — rare seams run ~8 band-metres per probe against iron's and ice's ~12 — so one
        /// number could not serve both readers. See docs/issues/FAC-219.</para></summary>
        public double Richness;               // 0..1, measured ore density

        /// <summary>0..1, raw hostile-activity signal. Raised by a raid landing
        /// (<see cref="FactionTuning.RaidThreat"/>), by player damage
        /// (<see cref="FactionTuning.PlayerAggressionThreatPerFlush"/>) and by a capture; lowered by
        /// <see cref="FactionTuning.ThreatDecayPerHour"/> in the global decay pass and by 0.05 on a
        /// defensive arrival. Persisted, so it survives a restart; it does not accrue decay across
        /// downtime, matching <see cref="HoldStrength"/>, which is also only decayed by that pass.</summary>
        public double Threat;
        public readonly List<string> Neighbors = new List<string>();
        public readonly List<Presence> Presences = new List<Presence>();
        public string TerritoryRef;           // OPTIONAL Groups Territory mapping; null if unmapped
        public DateTime LastReinforcedUtc;    // sim-clock of last Reinforce (or plant); decay is suppressed within ReinforceGracePeriod

        /// <summary>While in the future, this node's station safe zone is DOWN — a raid got through
        /// and the protection has not come back yet. Set when a Raid/Conquer resolves against the
        /// node (see <see cref="FactionTuning.RaidSafeZoneDownSeconds"/>); default means protected.
        /// <para>Persisted, and it has to be: the physical safe zone is re-created from scratch every
        /// time the station materialises, so a restart or a despawn/respawn cycle would otherwise
        /// hand a just-raided station its protection back for free. The host reads this both when
        /// creating a zone and on its own tick.</para></summary>
        public DateTime SafeZoneDownUntilUtc;

        /// <summary>The faction whose raid last extended <see cref="SafeZoneDownUntilUtc"/>: an NPC
        /// fleet's actor tag, or a player's own SE faction tag when their raid succeeded here. Null
        /// when nothing attributable took the zone down. Only meaningful while the window is open;
        /// <see cref="PlayerSiege.MayAddRaidDamage"/> reads it so that the raiding faction's fire on
        /// the opened station counts as phase 2 of a player raid.</summary>
        public string SafeZoneDownByTag;

        /// <summary>While in the future, this node is CONTESTED: a raid has landed on it recently and
        /// its own traffic no longer counts as undisputed presence, so
        /// <see cref="ShipActivity.HoldGainForInterval"/> is withheld and only decay applies. Stamped
        /// by every landed attack — NPC raid, conquest, and the player's two raid paths alike. Default
        /// means uncontested, which is almost every node almost always.
        ///
        /// <para>Deliberately its own clock rather than a reading of
        /// <see cref="SafeZoneDownUntilUtc"/> or <see cref="Threat"/>. The safe-zone window answers
        /// what the host should draw and is sized for that; threat is a decaying scalar a dozen
        /// consumers read and never expires cleanly. Neither can be re-tuned without moving something
        /// else. Persisted for the same reason the safe-zone window is: a restart inside the window
        /// would otherwise hand a besieged node its regeneration back for free.</para></summary>
        public DateTime ContestedUntilUtc;

        /// <summary>Who landed the attack that last set <see cref="ContestedUntilUtc"/>. Null means a
        /// player did it on their own account — Core models no player, so there is no tag to write.
        ///
        /// <para>Read by <see cref="NeutralFaction.IsClaimableBy"/>: a node ground down to
        /// abandonment inside its contested window belongs to whoever ground it down until the
        /// window lapses, so a third faction's free <see cref="WorldModel.Claim"/> cannot take the
        /// prize off a siege that is still standing on it. The reservation rides the contest clock
        /// rather than one of its own, so <see cref="FactionTuning.ContestedHoldGainSuppressedSeconds"/>
        /// sizes both — they answer the same question, "is a siege live here right now".</para></summary>
        public string ContestedByTag;

        /// <summary>When this node's hold last reached 0 while an owner that holds it could still
        /// answer. Default means hold is above 0, or the clock has not started. Written only by
        /// <see cref="WorldModel.DecayInfluence(double,double,IList{FactionState},double)"/>, which
        /// abandons the node once <see cref="FactionTuning.HoldZeroGraceSeconds"/> have passed from
        /// here and the contest window is closed, and cleared by the same pass when hold is back.
        ///
        /// <para>Not persisted: a restart inside the window starts the clock again from the first
        /// pass, which can only lengthen the owner's grace, never shorten it.</para></summary>
        public DateTime HoldZeroSinceUtc;

        /// <summary>When a player last did damage to this node's station. Default means never.
        /// Distinct from <see cref="Threat"/>, which this also raises: threat is a decaying scalar
        /// every consumer reads, while this is the timestamp a defender uses to tell "a player is
        /// shooting at us right now" from "something happened here a while ago".</summary>
        public DateTime LastPlayerAggressionUtc;

        // ---- player siege / raid (see PlayerSiege) ----------------------------------------------
        /// <summary>Zone integrity remaining, 0..<see cref="FactionTuning.RaidZoneIntegrityPool"/>.
        /// Stored with <see cref="ZoneIntegrityAtUtc"/> as a value-plus-timestamp pair rather than a
        /// number something has to tick: the regeneration is linear, so the current level is derived
        /// on read. Nothing runs per frame and a restart cannot lose progress it never wrote.
        /// <para>Negative means "never besieged" and reads as full.</para></summary>
        public double ZoneIntegrity = -1;
        public DateTime ZoneIntegrityAtUtc;
        /// <summary>When the siege dwell started -- the clock that, together with an empty pool,
        /// drops the zone. Default means no siege in progress.</summary>
        public DateTime SiegeStartedUtc;
        /// <summary>When the pool last stood at full during a siege, which is what the lapse timer
        /// measures from. Cleared the moment the pool drops below full again.</summary>
        public DateTime SiegeLapseSinceUtc;
        /// <summary>Cumulative player block damage against this node's station during the current
        /// raid. Reset when the raid lapses -- the raid is the unit, not the session.</summary>
        public double PlayerRaidBlockDamage;
        /// <summary>Set when a player raid FIRST meets its success criterion. Starts the conquer
        /// clock; cleared when the raid lapses. Later successes within the same raid leave it alone,
        /// so an attacker who keeps working cannot push their own conquer offer away.</summary>
        public DateTime PlayerRaidSucceededUtc;
        /// <summary>When a presence raid last stripped hold. A presence raid has no damage to count,
        /// so its quantum is time on station: one strip per
        /// <see cref="FactionTuning.RaidZoneSiegeDwellSeconds"/> held, charged at the start of each
        /// interval. Cleared when the raid lapses.</summary>
        public DateTime PlayerPresenceStripUtc;
        /// <summary>Hold strips this presence raid has charged, the opening one included: the clock
        /// the owner's answer to an occupation runs on (<see cref="Occupation"/>). Not persisted: the
        /// attacker is held in host memory only, so a restarted server lapses the raid anyway.
        /// Cleared when the raid lapses.</summary>
        public int PresenceStrips;
        /// <summary>When the siege broke through and the zone went down. Default means it has not.
        /// While set, the integrity pool is FROZEN -- phase 2 is a fight with the station, not with
        /// the shield, so an attacker does not have to keep suppressing it.</summary>
        public DateTime ZoneBreachedUtc;
        /// <summary>Ship-traffic credit, 0..<see cref="FactionTuning.ShipActivityCreditCap"/>.
        /// Stored with <see cref="ShipActivityAtUtc"/> as a value-plus-timestamp pair rather than a
        /// number something has to tick: the decay is exponential on
        /// <see cref="FactionTuning.ShipActivityHalfLifeSeconds"/>, so the current level is derived
        /// on read (<see cref="ShipActivity.CreditsOf"/>). Raised when a fleet this node's owner
        /// sent resolves a stop here; read by the global decay pass, which nets a proportional hold
        /// gain against that pass's decay.
        /// <para>Persisted: it is an accumulator, rebuildable from nothing else.</para></summary>
        public double ShipActivityCredits;
        public DateTime ShipActivityAtUtc;
        public DateTime AbandonedUtc;         // sim-clock of the transfer to NeutralFaction.Tag (Withdraw or decay expiry); DateTime.MinValue = never abandoned. See WorldModel.GetExpiredUnknNodeIds/FactionTuning.UnknGraceDuration.

        // ---- DemandHistory / neglect clocks (docs/material-vectors.md's DemandHistory addendum) ---
        /// <summary>Sim-clock of the last Haul convoy dispatched FROM this node (set by
        /// <see cref="OperationsManager.DispatchHaul"/>). <see cref="DateTime.MinValue"/> = never —
        /// treated as immediately overdue by <c>OperationsManager.IsOutboundHot</c>, not a "no data"
        /// case needing a fallback (unlike <see cref="LastReinforcedUtc"/>'s scan-backed default).</summary>
        public DateTime LastHaulDepartedUtc;
        /// <summary>Sim-clock of the last Haul convoy resolved TO this node (set unconditionally by
        /// <c>FleetResolution.ResolveSingle</c>'s Haul case, even on a near-zero interception ratio —
        /// "traffic touched this node" is the point, independent of what actually got delivered).</summary>
        public DateTime LastHaulArrivedUtc;
        /// <summary>Sim-clock of the last Mine dwell that resolved at THIS node (set at dwell
        /// resolution, not dispatch). Pure telemetry — cooldown gating reads
        /// <see cref="MineCooldownUntilUtc"/> instead, not this timestamp plus a shared constant.</summary>
        public DateTime LastMinedUtc;
        /// <summary>Absolute deadline before which this node cannot be mined again. A real trip sets
        /// it at Mine dwell resolution (<c>FleetResolution.PendingFleetResolver</c>'s Mine pre-check
        /// block) to <c>now + dwellSeconds * FactionTuning.MineCooldownMultiplier</c> -- so a longer
        /// dwell (more volume extracted) earns a proportionally longer cooldown instead of every
        /// dispatch, large or small, incurring the same flat penalty.
        /// <para>The one other writer is <see cref="OperationsManager"/>'s bootstrap Mine mint, which
        /// has no dwell to scale and stamps
        /// <see cref="FactionTuning.MineBootstrapCooldownSeconds"/> -- the only thing rate-limiting
        /// that fallback. Nothing else in Operations writes it.</para></summary>
        public DateTime MineCooldownUntilUtc;

        // ---- item inventory (Phase 3) -----------------------------------------------------------
        /// <summary>Per-node item stock. Keyed by SE SubTypeId (e.g. "Iron", "SteelPlate");
        /// value is unit count ÔëÑ 0. Minted by <c>AccrueIncome</c> and consumed by upkeep, trade
        /// dispatch, and action costs. Aggregated by ItemKind via <see cref="IItemRegistry"/> for
        /// brain-facing reads. Initialised lazily ÔÇö no allocation until the first write.</summary>
        public readonly Dictionary<string, long> Inventory = new Dictionary<string, long>();

        /// <summary>Per-node stock physically moved out of routable inventory and earmarked for
        /// capability installation. Production/trade/operations should not consume this stock; a
        /// successful Develop action consumes it before falling back to routable inventory.</summary>
        public readonly Dictionary<string, long> ReservedInventory = new Dictionary<string, long>();

        // ---- workers (docs/food-and-population.md, FAC-493) -------------------------------------
        /// <summary>People living at this node. Not inventory: they are not freight, not for sale and
        /// not loot, so they sit beside <see cref="Inventory"/> where nothing that walks every stock
        /// key can trade, haul or price them. Fractional, because the unfed leave a share at a time
        /// (<see cref="WorkerUpkeep"/>); read <see cref="WorkerUpkeep.Headcount"/> for the whole
        /// people a player sees. Capped by the Habitat's beds.</summary>
        public double Workers;

        /// <summary>When <see cref="WorkerUpkeep"/> last charged this node's workers their food and
        /// air. Default means never: the first pass only stamps it, so a node owes nothing for time
        /// before it had workers.</summary>
        public DateTime WorkersAtUtc;

        /// <summary>Nutrition already eaten and not yet owed. Stock moves in whole meals while a
        /// pass's appetite does not — a worker eats a tenth of a steak dinner in three minutes — so a
        /// pass eats whole units to cover what it owes and carries the rest here, always below the
        /// last unit eaten.</summary>
        public double WorkerFoodBank;

        /// <summary>Decaying, item-volume-weighted record of real deliveries received, keyed by
        /// SubTypeId — the "gravity" term in <see cref="OperationsManager.MatchVectors"/>'s selection
        /// score (docs/material-vectors.md's DemandHistory addendum). Want-tied by construction: an
        /// entry only ever exists because a matched <see cref="MaterialVector"/> (itself born from a
        /// live <c>Item:</c> demand) credited it via <see cref="WorldModel.AddPendingDemandHistoryCredit"/>
        /// — never posted directly, never invents a want. Sparse; folded/decayed once per faction per
        /// <see cref="OperationsManager.Tick"/>.</summary>
        public readonly Dictionary<string, double> DemandHistory = new Dictionary<string, double>();

        /// <summary>Decaying record of units this node has actually SOLD, keyed by inventory key —
        /// the "units sold per unit of time" term in the target-stock ranking
        /// (<see cref="PriceOracle"/>). Credited from the two channels where a real buyer with a
        /// real budget takes the goods: a cross-faction trade settling at the seller's origin node
        /// (<c>FleetResolution</c>) and a player buying from the station's store
        /// (<c>StationStoreManager.OnPlayerBought</c>).
        /// <para><b>The central bank is deliberately not a channel.</b> Its asset purchase is
        /// <c>CentralBankBuyFraction x stock</c> from any glut node with no bound tied to demand, so
        /// crediting it would make sales a function of stock: more stock, more "sold", higher rank,
        /// higher target, more production, more stock. Operator, 2026-09-20 — <i>"attaching to the
        /// bank would cause an unstable curve because the bank has no bounds for buying"</i>. The two
        /// channels above are each bounded by a counterparty's own demand and budget, which is the
        /// external anchor the loop needs.</para>
        /// <para>Folded and decayed alongside <see cref="DemandHistory"/>, once per faction per
        /// <see cref="OperationsManager.Tick"/>, on the same EMA period. Sparse; omitted from the
        /// brain file when empty.</para></summary>
        public readonly Dictionary<string, double> SalesHistory = new Dictionary<string, double>();

        // ---- fleet fuel (docs/fuel-plan.md, "Every founded node asks for a reserve") -------------
        /// <summary>Units of each fuel key this node hands to departing hulls per hour, as a
        /// time-decayed estimate — the sink that sizes the reserve it asks for. Written only by
        /// <see cref="FuelDraw"/>, so it exists only where hulls really depart; empty everywhere
        /// else and omitted from the brain file when empty.
        /// <para>Stored as a value-and-timestamp pair with <see cref="FuelBurnEmaAtUtc"/> rather
        /// than a number something ticks: the decay is exponential on
        /// <see cref="FactionTuning.FuelBurnEmaHours"/>, so the current level is derived on read by
        /// <see cref="FuelDraw.BurnRateAt"/>. Nothing runs per frame and a restart cannot lose
        /// progress it never wrote.</para></summary>
        public readonly Dictionary<string, double> FuelBurnEma = new Dictionary<string, double>();

        /// <summary>When <see cref="FuelBurnEma"/> was last written. Default means never, which
        /// reads as "no decay owed" rather than as an infinitely old estimate.</summary>
        public DateTime FuelBurnEmaAtUtc;

        /// <summary>The fraction of a unit of each fuel key this node's departures have burned but
        /// not yet drawn, always below 1. Stock moves in whole units while a leg's burn does not — an
        /// ion hull's cruise is a few hundredths of a kilogram of uranium — so <see cref="FuelDraw"/>
        /// draws the whole units owed and carries the rest to the next departure. In memory only: a
        /// restart forgives less than one unit per key.</summary>
        public readonly Dictionary<string, double> FuelDrawCarry = new Dictionary<string, double>();

        // ---- NPC extraction ramp (docs/mining-plan.md, "The NPC planner") ----------------------
        /// <summary>Units of each ore key Mine fleets have delivered to this node per hour, as a
        /// time-decayed estimate: the <i>mined</i> term of <see cref="ExtractionPlanner"/>'s flow gap.
        /// Written only by <see cref="ExtractionFlow.CreditMined"/> at a Mine delivery, so it exists
        /// only at nodes that receive mined ore; empty everywhere else and omitted from the brain
        /// file when empty.
        /// <para>Stored as a value-and-timestamp pair with <see cref="MinedEmaAtUtc"/>, the shape
        /// <see cref="FuelBurnEma"/> uses: the decay is exponential on
        /// <see cref="FactionTuning.ExtractionMinedEmaHours"/> and derived on read by
        /// <see cref="ExtractionFlow.MinedRateAt"/>, so nothing ticks it.</para></summary>
        public readonly Dictionary<string, double> MinedEma = new Dictionary<string, double>();

        /// <summary>When <see cref="MinedEma"/> was last written. Default means never.</summary>
        public DateTime MinedEmaAtUtc;

        // ---- node capabilities (Slice 2) --------------------------------------------------------
        /// <summary>Installed infrastructure. Absent = not installed; value ÔëÑ 1 = level.
        /// Installed via the Develop action.</summary>
        public readonly Dictionary<NodeCapabilityKind, int> Capabilities = new Dictionary<NodeCapabilityKind, int>();

        // ---- ship stock (Phase 3 Slice 3 / ShipyardManager; unified 2026-08-01) -----------------
        /// <summary>Ships built here and ready to dispatch, keyed by prefab SubtypeId (not role —
        /// a ship's purpose is decided at query time by <see cref="ShipPool"/> from its catalogue
        /// tags, not baked in at build time). "" means free-built (no BOM / unknown prefab).
        /// Populated by <see cref="ShipyardManager"/> each manager tick; consumed via
        /// <see cref="ShipPool.Consume"/> when a fleet departs. Persisted in
        /// <see cref="Persistence.SourceDoc.ShipStock"/>.</summary>
        public readonly Dictionary<string, int> ShipStock = new Dictionary<string, int>();

        /// <summary>Of each prefab's <see cref="ShipStock"/> count, how many are reserved for
        /// player purchase at a store node — a hard floor, not just a display counter:
        /// <see cref="ShipPool.Consume"/> only ever draws from <see cref="ShipPool.Available"/>
        /// (<c>ShipStock - ListedForSale</c>), so Raid/Trade/Patrol/Guard dispatch can never dip
        /// into reserved stock even when it's the only stock of that prefab a node has. Only a
        /// store purchase (<c>StationStoreManager.OnPlayerBoughtShip</c>) draws from this slice,
        /// decrementing it alongside <see cref="ShipStock"/>. Populated by <see
        /// cref="ShipyardManager"/> when a fulfilled build demand's role was StoreSmall/StoreLarge.
        /// Invariant: <c>ListedForSale[id] &lt;= ShipStock[id]</c> always.</summary>
        public readonly Dictionary<string, int> ListedForSale = new Dictionary<string, int>();

        // ---- build queue (ShipyardManager) -----------------------------------------------------
        /// <summary>Queued ship builds at this node, in creation order. Only the first
        /// <see cref="BuildSlotCount"/> of <see cref="OrderedBuildQueue"/> are worked (see
        /// <see cref="IsActiveBuild"/>); the rest wait their turn. Each working entry holds its own
        /// collected components (absorbed from <see cref="Inventory"/>), making them unavailable for
        /// other routing. Populated and consumed by <see cref="ShipyardManager"/>; persisted across
        /// reboots so multi-day builds survive server restarts.</summary>
        public readonly List<BuildQueueEntry> BuildQueue = new List<BuildQueueEntry>();

        /// <summary>Hulls that landed here damaged and are not in <see cref="ShipStock"/> until
        /// repaired -- see <see cref="DamagedHull"/>. Owned by the node exactly as its stock is.
        /// Persisted (omitted when empty).</summary>
        public readonly List<DamagedHull> DamagedHulls = new List<DamagedHull>();

        /// <summary>How many builds this node works at once: its <see cref="NodeCapabilityKind.Shipyard"/>
        /// level, floored at one. A yard converges its whole component intake on that many BOMs and
        /// everything else queues behind them, so a level-1 yard behaves exactly as the constant this
        /// replaced did.
        ///
        /// <para><b>The floor is not cosmetic.</b> A station refit is queued at any founded owned
        /// node, Shipyard or not (<c>ShipyardManager.QueueRefit</c>), and is serviced by the orphan
        /// pass. Returning 0 for a yardless node would leave every refit permanently waiting for a
        /// slot that cannot exist.</para>
        ///
        /// <para>Nothing else in the model bounds how many entries a node may hold. Admission is
        /// bounded by what creates them -- one per live <c>(role, DestinedForSale)</c> demand group,
        /// and a doctrine line's own <c>DoctrineLine.MaxConcurrent</c> -- so this count governs only
        /// how many are WORKED, which is the property <see cref="IsActiveBuild"/> exists to
        /// express.</para></summary>
        public int BuildSlotCount()
        {
            int level;
            if (!Capabilities.TryGetValue(NodeCapabilityKind.Shipyard, out level) || level < 1) return 1;
            return level;
        }

        /// <summary>Monotonic per-node counter behind <see cref="MintBuildEntryId"/>. Persisted, and
        /// raised on load past every id the node already carries, so a brain written before it
        /// existed cannot re-mint one.</summary>
        public int BuildSeq;

        /// <summary>Id prefix for a build that carries no <see cref="BuildQueueEntry.Role"/>.</summary>
        public const string RolelessBuildIdPrefix = "B";

        /// <summary>The next unique <see cref="BuildQueueEntry.Id"/> for this node.
        ///
        /// <para>Minted from <see cref="BuildSeq"/> and not from <c>BuildQueue.Count</c>, which is
        /// reused the moment an entry is removed. The whole production side addresses a build by
        /// <c>(nodeId, entryId)</c> -- <c>IProductionExecutionContext.MoveInventoryToBuildReserved</c>,
        /// <c>GetBuildCollected</c>, <c>GetBuildRecipeAccum</c> -- resolving it to the FIRST match,
        /// and the staged path keys a whole overlay dictionary on it. Two entries sharing an id put
        /// one's reserved inputs and recipe progress into the other's bucket, silently.</para></summary>
        public string MintBuildEntryId(string role)
        {
            // A build named by hull has no role to put in its id; "B" keeps the "prefix:seq" shape
            // ParseBuildEntrySeq reads, and an id a player wrote down before still resolves as-is.
            if (string.IsNullOrEmpty(role)) role = RolelessBuildIdPrefix;
            // Skipping an id already in use makes uniqueness a property of this method rather than of
            // every writer keeping BuildSeq ahead of the queue. Entries also arrive from persistence
            // and from test fixtures with ids this counter never issued, and a collision is silent:
            // the production side resolves (nodeId, entryId) to the FIRST match.
            for (;;)
            {
                BuildSeq++;
                string candidate = role + ":" + BuildSeq.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                bool taken = false;
                for (int i = 0; i < BuildQueue.Count; i++)
                    if (string.Equals(BuildQueue[i].Id, candidate, StringComparison.Ordinal)) { taken = true; break; }
                if (!taken) return candidate;
            }
        }

        /// <summary>The sequence number inside an id <see cref="MintBuildEntryId"/> produced, or 0 for
        /// anything it did not -- including the bare role string a doc written before ids existed
        /// loads as. The parse half of the format, kept beside the mint so the two cannot drift.</summary>
        public static int ParseBuildEntrySeq(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            int colon = id.LastIndexOf(':');
            if (colon < 0 || colon == id.Length - 1) return 0;
            int seq;
            return int.TryParse(id.Substring(colon + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out seq) ? seq : 0;
        }

        /// <summary>Which of this node's entries currently hold its working slots, chosen across the
        /// whole faction's yards by <see cref="BuildSlotAllocation"/>. Derived and <b>not
        /// persisted</b>: null until the owning faction's first allocation, and null again for a
        /// node with an empty queue or a paused yard.
        ///
        /// <para>While it is null <see cref="OrderedBuildQueue"/> falls back to ranking on
        /// <see cref="BuildQueueEntry.Priority"/> alone, which is what this node did before slot
        /// choice became a faction-wide question -- so a boot tick, the offline sim and any test
        /// fixture that never allocates behave exactly as they did.</para></summary>
        public HashSet<string> ActiveBuildIds;

        /// <summary>This node's build queue ranked by claim: highest
        /// <see cref="BuildQueueEntry.Priority"/> first, ties keeping list order -- which is creation
        /// order, so a default-priority queue is plain FIFO.
        ///
        /// <para>This is the <b>component</b> question -- who gets the contested ingot
        /// (<see cref="BuildQueueReserved"/>, <c>ProductionManager.EntriesByPriority</c>) -- and it is
        /// not the same question as which entry the yard works. <see cref="OrderedBuildQueue"/> is
        /// that one.</para></summary>
        public List<BuildQueueEntry> ClaimOrderedBuildQueue()
        {
            var ordered = new List<BuildQueueEntry>(BuildQueue);
            if (ordered.Count < 2) return ordered;
            var index = new Dictionary<BuildQueueEntry, int>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++) index[ordered[i]] = i;
            ordered.Sort(delegate (BuildQueueEntry a, BuildQueueEntry b)
            {
                int byPriority = b.Priority.CompareTo(a.Priority);
                return byPriority != 0 ? byPriority : index[a].CompareTo(index[b]);
            });
            return ordered;
        }

        /// <summary>This node's build queue in working order: the entries
        /// <see cref="BuildSlotAllocation"/> gave this node's slots to first, then the rest, each
        /// group in <see cref="ClaimOrderedBuildQueue"/>'s order. The first
        /// <see cref="BuildSlotCount"/> entries are the ones actually being worked, which is the
        /// property <see cref="IsActiveBuild"/> reads.
        ///
        /// <para>The allocator never gives a node more entries than it has slots, and always fills a
        /// slot it can fill, so "in the chosen set" and "inside the first
        /// <see cref="BuildSlotCount"/>" are the same statement -- which is why
        /// <see cref="IsActiveBuild"/> can stay a position test and cannot disagree with the
        /// allocation.</para></summary>
        public List<BuildQueueEntry> OrderedBuildQueue()
        {
            List<BuildQueueEntry> ordered = ClaimOrderedBuildQueue();
            if (ActiveBuildIds == null || ordered.Count < 2) return ordered;
            var rank = new Dictionary<BuildQueueEntry, int>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
                rank[ordered[i]] = (ordered[i].Bom.Count == 0 || ActiveBuildIds.Contains(ordered[i].Id))
                                   ? i : i + ordered.Count;
            ordered.Sort(delegate (BuildQueueEntry a, BuildQueueEntry b)
            {
                return rank[a].CompareTo(rank[b]);
            });
            return ordered;
        }

        /// <summary>Whether <paramref name="entry"/> currently occupies one of this node's
        /// <see cref="BuildSlotCount"/> working slots. A waiting entry absorbs nothing, reserves
        /// nothing, posts no <c>Item:</c> demand, accrues no
        /// <see cref="BuildQueueEntry.StalledSeconds"/>, and is invisible to
        /// <see cref="BuildQueueReserved"/> -- so the stock it will eventually need stays routable
        /// for whoever needs it now.</summary>
        public bool IsActiveBuild(BuildQueueEntry entry)
        {
            if (entry == null) return false;
            // A free build has no BOM to converge on: it neither needs a slot nor occupies one, and
            // completes on its next servicing whatever else the yard is working on. Only entries
            // that actually collect components queue against each other.
            if (entry.Bom.Count == 0) return BuildQueue.Contains(entry);
            int ahead = BuildsAheadOf(entry);
            return ahead >= 0 && ahead < BuildSlotCount();
        }

        /// <summary>How many entries that actually collect components sit ahead of
        /// <paramref name="entry"/> in claim order, or -1 when it is not one of them (a free build,
        /// or an entry this node does not hold).
        ///
        /// <para>The counting rule is <see cref="IsActiveBuild"/>'s own, which is why that method
        /// reads it rather than repeating it: free builds are SKIPPED, so a queue led by one does
        /// not push the first real build out of the working slot. Position in
        /// <see cref="OrderedBuildQueue"/> is not the same number, and a surface that says "waiting
        /// behind n" from the index is wrong exactly when a free build is queued.</para></summary>
        public int BuildsAheadOf(BuildQueueEntry entry)
        {
            if (entry == null || entry.Bom.Count == 0) return -1;
            int ahead = 0;
            foreach (BuildQueueEntry e in OrderedBuildQueue())
            {
                if (e.Bom.Count == 0) continue;
                if (ReferenceEquals(e, entry)) return ahead;
                ahead++;
            }
            return -1;
        }

        /// <summary>True while this station's shipyard is paused: it claims nothing new — no
        /// absorption into <see cref="BuildQueueEntry.Collected"/>, no <c>Item:</c>/<c>Ship:</c>
        /// demands, no recipe-input reservation, no entry creation — and its entries drop out of
        /// <see cref="BuildQueueReserved"/>, releasing the still-uncollected BOM share back to every
        /// other consumer. What an entry already physically holds (<c>Collected</c>,
        /// <c>ReservedInputs</c>) stays with it, and paused entries are exempt from expiry pruning,
        /// so a pause freezes a build rather than unwinding it.</summary>
        public bool ShipyardPaused;

        /// <summary>Sum of outstanding BOM shortfall (Bom - Collected, floored at 0) for
        /// <paramref name="invKey"/> across the qualifying entries in <see cref="BuildQueue"/> — the
        /// slice of this node's own routable <see cref="Inventory"/> a pending ship build still needs
        /// before <see cref="ShipyardManager"/>'s next tick can absorb it into <see cref="BuildQueueEntry.Collected"/>.
        /// Other consumers that draw down routable inventory (central bank asset purchases,
        /// <see cref="OperationsManager"/> surplus routing) must treat this amount as off-limits so
        /// they cannot win the race against Shipyard's own absorption tick.
        ///
        /// <para>Expired entries, entries merely WAITING for a slot (see <see cref="IsActiveBuild"/>),
        /// and every entry at a <see cref="ShipyardPaused"/> node are skipped: none of them will absorb
        /// on the next tick, so holding inventory off-limits for them starves the rest of the network
        /// for nothing. <paramref name="minPriority"/> additionally narrows the sum to entries
        /// ranking at or above a competing claim (see <see cref="BuildQueueEntry.Priority"/>) — the
        /// single primitive behind both the capability-install floor and the cross-entry floor, so a
        /// priority decision never depends on which loop reaches an entry first.</para></summary>
        public long BuildQueueReserved(string invKey, DateTime nowUtc, int minPriority = int.MinValue)
        {
            if (ShipyardPaused) return 0;
            long reserved = 0;
            foreach (BuildQueueEntry e in BuildQueue)
            {
                if (e.ExpiresUtc <= nowUtc || e.Priority < minPriority) continue;
                if (!IsActiveBuild(e)) continue;
                long needed;
                if (!e.Bom.TryGetValue(invKey, out needed)) continue;
                long collected;
                e.Collected.TryGetValue(invKey, out collected);
                long shortfall = needed - collected;
                if (shortfall > 0) reserved += shortfall;
            }
            return reserved;
        }

        /// <summary>Spatial context: surface, orbital, or deep space.
        /// Set by <c>WorldModel.ExpandFrom</c> from live SE gravity at placement time.
        /// Drives minimum-separation enforcement and surface position snapping.</summary>
        public NodeZone Zone;

        /// <summary>
        /// What kind of place this node stands in — on terrain, in a gravity well, or in open space.
        /// Derived and <b>not persisted</b>, exactly like <see cref="Zone"/> and
        /// <see cref="PlanetId"/>: <c>WorldModel.ReclassifyNodes</c> rebuilds it on every boot from
        /// <see cref="StationSiting.Classify"/>.
        ///
        /// <para><b>This is what asks the ground; <see cref="Zone"/> is what asks the gravity.</b>
        /// They disagree wherever an atmosphere reaches far past its terrain — relative gravity reads
        /// "surface" for hundreds of km of open air on a gas giant. Consumers that mean "on this
        /// body's ground" read this; <see cref="Zone"/>'s remaining job is node separation. See
        /// docs/issues/FAC-096 stage 2.</para>
        ///
        /// <para>Cached rather than recomputed per query because the classification costs a
        /// terrain lookup on the live host and these are read in per-node loops. The one consumer
        /// that deliberately does not use it is <see cref="FleetRules.IsSamePlanetSurface"/>, which
        /// removes a physical requirement rather than expressing a preference and so pays for a
        /// live answer.</para>
        /// </summary>
        public StationSite Site;

        /// <summary>Nearest <see cref="PlanetRecord.Id"/> when <see cref="Zone"/> is <see
        /// cref="NodeZone.Surface"/> or <see cref="NodeZone.Orbit"/>; null for <see
        /// cref="NodeZone.DeepSpace"/> (no planet applies) or when no planet is in range at all. Set
        /// alongside <see cref="Zone"/> by <see cref="WorldModel.NearestPlanetId"/> — planets are
        /// physically isolated environments, so this (not the coarser <see cref="Zone"/> bucket alone)
        /// is what identifies "the same planet" for local, intra-planet logistics/investment
        /// decisions (e.g. a Shipyard on Terra does not serve Mars just because both are Surface).</summary>
        public string PlanetId;

        /// <summary>Name of the authored <see cref="WorldFeature"/> that claims this node's position
        /// (<see cref="WorldFeaturePlacement.TryFeatureAt"/> — innermost claimant wins, the same
        /// tie-break placement and the ore seam use), or null outside every feature. Derived, never
        /// persisted, and rebuilt alongside <see cref="Zone"/>/<see cref="PlanetId"/>/<see cref="Site"/>
        /// by <see cref="WorldModel.ReclassifyNodes"/> on every boot, so a feature renamed or
        /// re-authored in <c>world_features.json</c> reaches the whole estate.
        ///
        /// <para>This is what lets deep space have regions. A node inside a planet's well answers
        /// "where am I" with <see cref="PlanetId"/>; a node in open space has no body to name, and
        /// before this field nothing on it recorded which belt or nest it stood in — so a faction whose
        /// whole estate is six far-apart features read as one undifferentiated region to every
        /// coverage question (<see cref="NodeRegion"/>).</para></summary>
        public string FeatureKey;

        /// <summary>What this node's ground actually holds, sampled once from the world rather than
        /// the free-mint table. Null means never sampled; a non-null but barren
        /// deposit means sampled and found nothing, which is a different and equally real answer.
        /// <para>Persisted, unlike <see cref="Zone"/> and <see cref="PlanetId"/>: those are a
        /// gravity read that is cheaper to redo than to store, while this is an area sample over the
        /// body's ore cubemap. It is durable fact about a static world, so it is stored rather than
        /// recomputed on every boot. See docs/ore-geography.md.</para></summary>
        public NodeDeposit Deposit;

        /// <summary>The re-survey of a <see cref="NodeDeposit.IsStale"/> deposit, accumulating in
        /// today's units while <see cref="Deposit"/> keeps serving the old readings. Swapped in once
        /// it has covered as much ground as the old one had (<see cref="WorldModel.TryResurveyStep"/>).
        /// <para><b>Not persisted.</b> A restart drops it and the re-survey starts over, which costs
        /// minutes of probing and never mixes units: the stale deposit is still on disk in its own.</para></summary>
        public NodeDeposit PendingDeposit;

        // Derived yield entries, cached against the inputs they were built from. Not persisted --
        // rebuilt on demand by NodeYieldResolver, which owns every one of these fields.
        internal NodeYieldEntry[] EffectiveYieldCache;
        internal object           EffectiveYieldDepositKey;
        internal object           EffectiveYieldTuningKey;

        // ---- station materialization (Slice 1) -------------------------------------------------
        /// <summary>SE <c>MyStation.Id</c> for E### (economy station) nodes. 0 for all other nodes.
        /// Assigned once at scan time, never changes. Used to locate the <c>MyStation</c> record and
        /// to name the spawned grid / safe zone via the vanilla format string.</summary>
        public long StableStationId;
        /// <summary>Nonzero once a physical asteroid field has been materialized near this node
        /// (0 = not yet). Assigned once by the host-side asteroid materializer and never re-rolled --
        /// existence, not the value itself, is what prevents a duplicate field appearing on the next
        /// world-graph reload. See <see cref="AsteroidFieldPlanner"/>.</summary>
        public int AsteroidFieldSeed;

        /// <summary>Prefab SubtypeId for a station-backed node when known. Null otherwise.</summary>
        public string StationPrefabId;
        /// <summary>The design this node has paid for and is waiting to be rebuilt as — set when a
        /// <c>Station</c> build entry completes (<see cref="ShipyardManager.QueueRefit"/>), cleared by
        /// the host once the grid has actually been replaced. Null whenever no rebuild is owed.
        ///
        /// <para>Persisted, because the components are already consumed by the time it is written:
        /// the intent has been paid for and must survive a restart even though the swap itself can
        /// only happen on the game thread, and only while nobody is standing in the station.</para></summary>
        public string PendingStationPrefabId;
        /// <summary>Cached station prefab book value in credits when known. Null otherwise.</summary>
        public long? StationBookValue;
        /// <summary>Cached station prefab weapon loadout (one <see cref="WeaponBand"/> per distinct
        /// weapon range present), when known. Null/empty for stations with no detected weapon blocks
        /// or not yet scanned. Combined with <see cref="FactionTuning.StationDefensePower"/> (a flat,
        /// always-on floor) to make every station-backed node an implicit combat defender, independent
        /// of whether a Guard fleet is present.</summary>
        public List<WeaponBand> StationWeapons;
        /// <summary>Magazines drawn from this node's stock into its materialised station and not yet
        /// credited back, by inventory key — see <see cref="Factions.Core.StationMunitions"/>. Null
        /// whenever no station grid holds any, which is every node nobody is near.</summary>
        public Dictionary<string, long> StationMunitions;
        /// <summary>Loot drawn from this node's stock into its materialised station's containers and
        /// not yet credited back, by inventory key — see <see cref="Factions.Core.StationLoot"/>. Null
        /// whenever no station grid holds any.</summary>
        public Dictionary<string, long> StationLoot;
        /// <summary>Until when this node's station stands with empty loot blocks, because players
        /// took loot from it — see <see cref="Factions.Core.StationLoot.RestockCooldown"/>. Default
        /// when it restocks freely.</summary>
        public DateTime LootRestockUtc;
        /// <summary>Human-readable grid name for E### nodes, populated at scan time and backfilled on reboot.
        /// Null for N### brain-generated nodes and for unspawned economy stations.</summary>
        public string DisplayName;

        /// <summary>Canonical spawn position (km, world-space). Equal to <see cref="Position"/> for
        /// E### nodes. Never drifts across despawn/respawn cycles. Exposed as a named field so
        /// downstream consumers (e.g. a future CrunchEcon gateway) can find the spawned grid via a
        /// 2 km sphere scan without having to know about the E### id scheme.</summary>
        public Vec3 GpsAnchor;
    }

    /// <summary>
    /// The faction-owned spatial graph. Nodes are created dynamically / on-demand (e.g. as factions
    /// expand) ÔÇö it is NOT seeded from any fixed sector layout, and mapping a node to a Groups
    /// Territory is optional (<see cref="SpatialNode.TerritoryRef"/>).
    /// </summary>
    public sealed class SpatialGraph
    {
        public readonly Dictionary<string, SpatialNode> Nodes = new Dictionary<string, SpatialNode>();
        private int _seq;

        public SpatialNode Add(Vec3 pos, double resourceValue, string id = null)
        {
            if (id == null)
                do { id = "N" + (++_seq).ToString("D3"); } while (Nodes.ContainsKey(id)); // skip ids reloaded from disk
            var n = new SpatialNode { Id = id, Position = pos, ResourceValue = resourceValue };
            Nodes[id] = n;
            return n;
        }

        public void Connect(string a, string b)
        {
            SpatialNode na, nb;
            if (Nodes.TryGetValue(a, out na) && Nodes.TryGetValue(b, out nb))
            {
                if (!na.Neighbors.Contains(b)) na.Neighbors.Add(b);
                if (!nb.Neighbors.Contains(a)) nb.Neighbors.Add(a);
            }
        }

        /// <summary>Removes in-memory nodes that have no matching durable SourceDoc and cleans every
        /// retained neighbor list. This is intentionally ID-only: topology is never reconstructed from
        /// spatial proximity.</summary>
        public int TrimToSourceIds(ISet<string> sourceIds)
        {
            if (sourceIds == null) sourceIds = new HashSet<string>();
            var remove = Nodes.Keys.Where(id => !sourceIds.Contains(id)).ToList();
            foreach (string id in remove) Nodes.Remove(id);
            var retained = new HashSet<string>(Nodes.Keys);
            foreach (SpatialNode node in Nodes.Values)
                node.Neighbors.RemoveAll(id => !retained.Contains(id) || id == node.Id);
            return remove.Count;
        }

        public IEnumerable<SpatialNode> OwnedBy(string factionTag)
        {
            return Nodes.Values.Where(n => n.OwnerFactionTag == factionTag);
        }

        public int OwnedNeighborCount(string factionTag, SpatialNode node)
        {
            int count = 0;
            foreach (string nbId in node.Neighbors)
            {
                SpatialNode neighbor;
                if (Nodes.TryGetValue(nbId, out neighbor) && neighbor.OwnerFactionTag == factionTag)
                    count++;
            }
            return count;
        }

        public bool IsFrontier(string factionTag, SpatialNode node, int maxOwnedNeighbors)
        {
            return OwnedNeighborCount(factionTag, node) <= maxOwnedNeighbors;
        }

        /// <summary>Unclaimed (or abandoned/UNKN-owned) nodes adjacent to a faction's holdings — its
        /// expansion frontier. UNKN-owned nodes count as claimable here so a faction's normal Claim
        /// path also reclaims derelict territory, not just virgin space.</summary>
        public IEnumerable<SpatialNode> Frontier(string factionTag)
        {
            var seen = new HashSet<string>();
            foreach (var owned in OwnedBy(factionTag))
                foreach (var nbId in owned.Neighbors)
                {
                    SpatialNode node;
                    if (Nodes.TryGetValue(nbId, out node)
                        && (node.OwnerFactionTag == null || node.OwnerFactionTag == NeutralFaction.Tag)
                        && seen.Add(nbId))
                        yield return node;
                }
        }
    }

    // ---- faction model -------------------------------------------------------------------------

    /// <summary>Interpretive modifiers (0..1). Traits change how the SAME world is read into drives.</summary>
    public sealed class Traits
    {
        public double Xenophobia;    // foreign (player/other) presence reads as threat
        public double Mercantilism;  // foreign presence reads as trade opportunity
        public double Aggression;    // bias toward forceful / retaliatory actions
        public double Expansionism;  // bias toward claiming new ground

        /// <summary>Holds every other faction at maximum hostility, permanently. Standing toward any
        /// present faction is pinned at -1 each economy quantum instead of drifting with border
        /// pressure, no truce concludes in either direction, an incoming gift buys nothing, and no war
        /// fatigue accrues (so the faction never sues for peace and never tires into passivity). Unlike
        /// the 0..1 traits this is not a bias but an absolute: it models an actor that does not do
        /// diplomacy at all, rather than one that is merely very bad at it.</summary>
        public bool Implacable;
    }

    public sealed class Personality
    {
        public string Id;
        public DriveVector Weights = new DriveVector();
        public double Temperature = 0.5;     // softmax temperature: higher = more erratic
        public Traits Traits = new Traits();
    }

    /// <summary>Per-faction "brain" state ÔÇö what would live in brains/&lt;tag&gt;.json.</summary>
    // ---- in-flight fleet events ----------------------------------------------------------------

    /// <summary>One weapon system's contribution to a ship or station's combat loadout: how much
    /// damage per second it deals, and the range within which it can deal it. A prefab is cached as
    /// a list of these (one per distinct range present on the hull) rather than a single collapsed
    /// power/range pair, so a ship carrying both short-range and long-range weapons only brings its
    /// long-range band to bear during a long-range engagement turn. See
    /// <see cref="PendingFleetResolver.ResolveShipDuel"/>.</summary>
    public struct WeaponBand
    {
        public float Dps;
        public float RangeM;
    }

    /// <summary>A thruster block's mounted/exhaust-facing direction, matching SE's own
    /// <c>Base6Directions.Direction</c> by name (<c>MyThrust.ThrustForwardVector =
    /// Base6Directions.GetIntVector(Orientation.Forward)</c>). This is the community's own naming
    /// convention too: a "down thruster" (<c>Down</c>) is what provides lift; a "backward
    /// thruster" (<c>Backward</c>) is what provides forward acceleration. The key carries the raw
    /// mounted-facing value, not the resulting push direction — see <see cref="ThrustBand"/>.</summary>
    public enum ThrustDirection { Forward, Backward, Up, Down, Left, Right }

    /// <summary>One group of a prefab's thrusters sharing an exact propulsion-curve signature,
    /// mounted facing the same <see cref="ThrustDirection"/> — mirrors <see cref="WeaponBand"/>.
    /// Fields are raw <c>MyThrustDefinition</c> constants copied verbatim at scan time (see
    /// <see cref="ThrustPhysics.ForceMultiplier"/> for how they combine); grouped by exact match
    /// since these are definition constants, not derived/jittered floats.</summary>
    public struct ThrustBand
    {
        public string ThrusterType;                 // MyStringHash.ToString(): "Atmospheric","Hydrogen","Ion", or a modded value
        public float  ForceMagnitudeN;               // summed max thrust (N) across blocks sharing this curve, in this direction
        public float  MinPlanetaryInfluence;
        public float  MaxPlanetaryInfluence;
        public float  EffectivenessAtMinInfluence;
        public float  EffectivenessAtMaxInfluence;
        public bool   NeedsAtmosphereForInfluence;

        // ---- consumption (docs/fuel-plan.md) ------------------------------------------------
        // SE draws MaxPowerConsumption x ForceMultiplier for ForceMagnitude x ForceMultiplier of
        // thrust, so consumption per delivered newton-second is a per-block constant; these three
        // fields are what that constant is made of. All are 0/empty for a band scanned before
        // they existed, which every reader must treat as "unknown, no burn".
        /// <summary>Summed <c>MaxPowerConsumption</c> (MW) across the blocks in this band — the
        /// draw at full thrust, whichever resource it is drawn in.</summary>
        public float  MaxPowerMw;
        /// <summary>Inventory key of the gas the band burns (<c>"Gas:Hydrogen"</c> or a modded gas),
        /// from <c>FuelConverter.FuelId</c>. Null or empty for an electric band (Ion, Atmospheric):
        /// its draw is electricity.</summary>
        public string FuelId;
        /// <summary><c>FuelConverter.Efficiency</c>; 1 when the band is electric.</summary>
        public float  FuelEfficiency;
        /// <summary>The fuel gas's <c>EnergyDensity</c> in MWh per litre (hydrogen 0.001556),
        /// carried on the band so Core needs no gas table. 0 for an electric band.</summary>
        public float  FuelEnergyDensityMwhPerL;
    }

    /// <summary>Semantic purpose of a fleet's current or queued stop. Service stops apply the
    /// mission's economic/diplomatic/combat effect; return stops only reconcile surviving cargo
    /// and ships at the fleet's home node; pickup stops load cargo from the stop's node into the
    /// convoy's hold, then continue — no terminal effect of their own.</summary>
    public enum FleetStopKind
    {
        Service,
        Return,
        Navigation,
        Pickup,
        /// <summary>Stationary dwell at the stop's node for a real, precomputed duration — unlike
        /// every other stop kind, this one's arrival time is set from an explicit duration, not from
        /// travel distance (see <see cref="FleetStop.DurationSeconds"/>). Yield is minted and the
        /// node's Mine cooldown is set at resolution, not at dispatch — see
        /// <see cref="FleetResolution.PendingFleetResolver"/>'s Mine pre-check block.</summary>
        Mine,
        /// <summary>A Raid/Retaliate/Conquer holding at its target for the engagement dwell: the
        /// combat itself, and the window in which a player can reach the fleet and fight it. Timed
        /// from <see cref="FleetStop.DurationSeconds"/> exactly as <see cref="Mine"/> is, but unlike
        /// every other duration or transit stop this one IS the mission destination -- the defence
        /// duel and the raid's own effect both fire when this leg resolves, not when the fleet made
        /// contact. A fleet cut short mid-dwell resolves at the fraction of the dwell it completed.</summary>
        Engage,
        /// <summary>A convoy and the raid that caught it, both held at the meeting node for
        /// <see cref="FactionTuning.InterceptWindowSeconds"/> before the encounter is decided. Timed
        /// from <see cref="FleetStop.DurationSeconds"/> like <see cref="Engage"/>; the leg that was
        /// interrupted is re-queued behind it and flown afterwards. The window is what lets a player
        /// reach the two grids and settle the matter first: a hull destroyed or captured inside it
        /// resolves as that loss, and an untouched window falls through to the abstract rules.
        /// Persisted by name; a loader that does not know it reads <see cref="Service"/>.
        /// <para>Provenance: FAC-307.</para></summary>
        Intercept
    }

    /// <summary>One future stop in an abstract fleet itinerary. The current leg continues to use
    /// <see cref="PendingFleetAction.TargetNodeId"/> so existing transit, encounter, and observation
    /// code remains leg-oriented; this list supplies later legs.</summary>
    public struct FleetStop
    {
        public string NodeId;
        public FleetStopKind Kind;
        /// <summary>Explicit world coordinate for a computed navigation leg; NodeId is null in this case.</summary>
        public Vec3 PositionKm;
        public bool HasPosition;
        /// <summary>For <see cref="FleetStopKind.Pickup"/> only: the SubTypeId to load from
        /// <see cref="NodeId"/>'s inventory on arrival.</summary>
        public string PickupItemKey;
        /// <summary>For <see cref="FleetStopKind.Pickup"/> only: requested quantity to load, capped
        /// by remaining cargo capacity and the node's actual available stock at load time.</summary>
        public long PickupQuantity;
        /// <summary>Dwell length for the two stop kinds that are timed rather than flown. For
        /// <see cref="FleetStopKind.Mine"/>: gather + overburden, set at dispatch time from the
        /// requested target volume and the drill's effective rate. For
        /// <see cref="FleetStopKind.Engage"/>: <see cref="FactionTuning.RaidDwellSeconds"/>, the
        /// engagement window. Either way it drives this stop's arrival time directly, bypassing the
        /// usual distance-derived transit calculation.</summary>
        public double DurationSeconds;
        /// <summary>For <see cref="FleetStopKind.Mine"/> only: normalized SubTypeId -&gt; fraction of
        /// <see cref="MineTargetLiters"/> to extract as that ore. Sums to 1.0.</summary>
        public Dictionary<string, double> MineWeights;
        /// <summary>For <see cref="FleetStopKind.Mine"/> only: total raw liters to extract this
        /// dwell, split across <see cref="MineWeights"/>.</summary>
        public double MineTargetLiters;
    }

    /// <summary>An abstract in-flight fleet event (Raid, Trade, or Patrol) that travels from its
    /// origin to its target in sim-time and resolves on arrival. Physical grid is materialised (via
    /// MES) only when a player is close enough to observe the interpolated position (Cornerstone II).
    /// Not persisted: <see cref="MaterialisedEntityId"/> (transient), <see cref="Intercepted"/>
    /// (removed before flush). <see cref="Resolved"/> IS persisted so an active patrol doesn't
    /// re-apply its effect across a reboot.</summary>
    public struct PendingFleetAction
    {
        public ActionKind Kind;             // Trade, Raid, or Patrol
        public string ActorTag;
        public string TargetTag;            // owning faction at destination (null for Patrol)
        public string OriginNodeId;
        public string TargetNodeId;
        public Vec3 OriginPosKm;
        public Vec3 TargetPosKm;
        public DateTime DepartedUtc;        // real UTC time of departure
        public DateTime EstimatedArrivalUtc;// real UTC time of arrival (= DepartedUtc for immediate)
        /// <summary>This fleet was dispatched to resolve without transit time (the NullFleetSpawner
        /// fast path, and the load-time migration that force-resolves a fleet with unusable
        /// timestamps), so every stop on its itinerary arrives the instant it departs.
        /// <para>Not inferred from the previous leg's timestamps and not persisted. A leg CAN
        /// legitimately have zero duration without the fleet being immediate — two stops at one
        /// node — and reading that back as "immediate" turned the rest of a real itinerary into
        /// teleportation. Either way an immediate fleet never outlives the tick that made it.</para></summary>
        public bool ImmediateItinerary;
        public long CargoValue;             // credits at stake: Cargo book value for Haul and Trade (what was paid, for a procurement), 0 for Patrol
        public Dictionary<string, long> Cargo; // item manifest for Trade convoys (SubTypeId ÔåÆ amount); null for other kinds
        public double TradeMagnitude;       // un-normalised lane weight (╬ú reach├ùresourceValue); seeds convoy-class selection later
        public int ConvoySize;              // ships to materialise: 1 = solo (trickle), >1 = coordinated fleet (burst)
        /// <summary>
        /// Lifecycle states for <see cref="MaterialisedEntityId"/>:
        /// <list type="bullet">
        ///   <item>0 ÔÇö not yet spawned</item>
        ///   <item><see cref="SpawningId"/> (ÔêÆ2) ÔÇö async spawn requested; callback not yet fired</item>
        ///   <item>&gt; 0 ÔÇö live grid entity ID</item>
        ///   <item>ÔêÆ1 ÔÇö grid was destroyed/stalled; force-resolves next tick</item>
        /// </list>
        /// Only 0 and &gt; 0 are persisted to brain JSON.
        /// </summary>
        public long MaterialisedEntityId;
        /// <summary>Sentinel: spawn has been requested but the async callback has not fired yet.
        /// <see cref="FleetResolution.PendingFleetResolver.ResolveArrivals"/> defers while this is set,
        /// preventing abstract resolution from racing the in-flight spawn.</summary>
        public const long SpawningId = -2L;
        /// <summary>For <see cref="ActionKind.Ferry"/> only: the ship role being repositioned
        /// (e.g. "Trade", "Raid"). Persisted so the delivery node and stock transfer survive a reboot.</summary>
        public string FerryRole;
        /// <summary>Non-null only for a 0-cargo repositioning Haul dispatched by
        /// <see cref="ShipyardManager"/> to move an idle, route-feasible ship toward a
        /// <c>Ship:{role}</c> demand instead of queueing a fresh build -- holds that role
        /// ("Trade"/"Mine") for in-flight dedup and log clarity. Deliberately a Haul variant:
        /// <see cref="ActionKind.Ferry"/> and its FERRY_DELIVERED resolution stay reserved for a
        /// future ship-carries-ship mechanic and are not reused for relocation. Persisted
        /// (null omitted) so the dedup guard survives a reboot.</summary>
        public string RelocationRole;
        /// <summary>Set on an <see cref="ActionKind.Trade"/> convoy flying the acquisition direction:
        /// depart from an owned node, <see cref="FleetStopKind.Pickup"/> at a *partner's* node where
        /// the goods are bought and paid for, then <see cref="FleetStopKind.Return"/> home with them.
        /// The ordinary sell direction leaves this false and keeps its Service (sell at the partner)
        /// + Return (unsold cargo home) shape. The distinction only ever matters at the Pickup leg —
        /// a foreign pickup node is purchased from rather than drawn down for free — so it is a flag
        /// on Trade rather than its own <see cref="ActionKind"/>. Persisted (false omitted) so a
        /// convoy mid-flight at reboot still pays on arrival instead of looting.</summary>
        public bool IsProcurement;
        /// <summary>Prefab SubtypeId of the ship(s) in this convoy. "" for free-built (no BOM).
        /// Persisted so a returning ship credits the right prefab back to <see
        /// cref="SpatialNode.ShipStock"/> via <see cref="ShipPool.Return"/>.</summary>
        public string ShipPrefabId;
        /// <summary>Total cargo capacity of this convoy in litres (all ships combined), derived from
        /// physical cargo-container definitions at dispatch time. 0 when unknown (triggers count-cap fallback).</summary>
        public long CargoCapacityLiters;
        /// <summary>Set when the cargo manifest was cut below its volume cap by the loaded-TWR mass
        /// constraint (see <c>ThrustPhysics.MaxCargoMassKg</c>) — drives the <c>cargo_mass_trimmed</c>
        /// log suffix at the dispatch call sites that build a manifest (Trade/Raid loot). Never
        /// persisted as meaningful state beyond the dispatch log line itself.</summary>
        public bool CargoMassTrimmed;
        /// <summary>This convoy's combined weapon loadout: one <see cref="WeaponBand"/> per distinct
        /// weapon range present, each already scaled by <see cref="ConvoySize"/> (every ship in the
        /// convoy contributes its full per-ship band set). Derived from the ship prefab's cached
        /// stats at dispatch time via <c>IPrefabCatalogue.WeaponBands</c>. Null/empty for unarmed
        /// convoys (Trade, Ferry) or when the prefab is unknown to the catalogue.</summary>
        public List<WeaponBand> WeaponBands;
        /// <summary>Magazines aboard, keyed by magazine inventory key — what
        /// <see cref="FleetMunitions.Arm"/> drew from the departure node, less what duels and a
        /// materialised fight have spent since. A key at 0 is a magazine the hulls fire and do not
        /// have. Only meaningful when <see cref="MunitionsArmed"/> is set. Persisted (zero entries
        /// and an empty map omitted).</summary>
        public Dictionary<string, long> Munitions;
        /// <summary>This fleet was armed from node stock at dispatch, so it fires only
        /// <see cref="Munitions"/>. False for an unarmed fleet, one dispatched with
        /// <see cref="FactionTuning.MunitionDrawMode"/> off, and one dispatched before munitions
        /// existed — all of which fight on their bands without limit. Persisted (false omitted), and
        /// kept apart from the map because a fleet armed with nothing has an empty one.</summary>
        public bool MunitionsArmed;
        /// <summary>Why this fleet broke off and is going home, or null. Set by
        /// <see cref="PendingFleetResolver.BeginRetreat"/>, today only for <c>out_of_ammo</c>: a
        /// materialised fleet whose weapons ran dry while it held station. A retreating fleet's
        /// Engage leg resolves with no effect and no loot, and it is not duelled on the way out.
        /// Persisted (null omitted) so a restart mid-retreat does not turn it back into an attack.</summary>
        public string RetreatReason;
        /// <summary>Mixed-role Raid/Retaliate/Conquer squadron composition — the prefab ID of each
        /// individual ship making up <see cref="ShipPrefabId"/>/<see cref="ConvoySize"/>'s aggregate.
        /// Ship stock isn't role-bucketed (see <see cref="SpatialNode.ShipStock"/>/<see
        /// cref="ShipPool"/>), so return just credits each entry's prefab back to the pool — no role
        /// routing needed. Null/empty for every non-squadron fleet (Trade, Patrol, Guard, Gift,
        /// Ferry, and any Raid that only ever found a single fighter) — those keep using
        /// <see cref="ShipPrefabId"/>/<see cref="ConvoySize"/> alone, exactly as before this field
        /// existed.</summary>
        public List<string> Squadron;
        /// <summary>Live grid entity IDs of this fleet's squadron members other than the lead --
        /// <see cref="MaterialisedEntityId"/> stays the lead and keeps its four-state encoding.
        /// Only ever holds IDs &gt; 0: an escort that is despawned, destroyed or captured is removed
        /// from the list rather than being marked with a sentinel. Escorts exist only while the lead
        /// does, so every path that despawns, loses or dematerialises the lead despawns the escorts
        /// and nulls this list. Null when the fleet has none, which is every non-squadron fleet and
        /// every squadron that has not materialised. Persisted (null/empty omitted) so a restart
        /// mid-observation can still find the grids to despawn.</summary>
        public List<long> EscortEntityIds;
        /// <summary>A Guard sent against an intruder (<see cref="TrespassLadder"/>) rather than an
        /// attack: it holds fire while the node is at <see cref="TrespassRung.Shadow"/> and goes home
        /// once the intruder is gone (<see cref="TrespassLadder.GuardDutyOver"/>). False for every
        /// other fleet. Persisted, omitted when false.</summary>
        public bool AgainstIntruder;
        /// <summary>Prefab IDs of the <see cref="Squadron"/> members whose grid has not been lost --
        /// the hulls that are still eligible to be credited back to <see cref="ShipPool"/> at the
        /// terminal leg. Null means "all of <see cref="Squadron"/>"; it is seeded lazily by
        /// <see cref="MarkHullLost"/>, which copies <see cref="Squadron"/> and removes one occurrence
        /// of the lost prefab, so a fleet that never loses a hull writes nothing. Only meaningful
        /// when <see cref="Squadron"/> is non-empty. Persisted (null omitted).</summary>
        public List<string> SurvivingHulls;
        /// <summary>Per-hull structural integrity, 0..1, one entry per hull slot: parallel to
        /// <see cref="HullsToReturn"/> for a squadron (so to <see cref="Squadron"/> until the first
        /// loss, to <see cref="SurvivingHulls"/> after), and a single entry for every other fleet,
        /// which flies its <see cref="ConvoySize"/> hulls as one grid and so carries one reading for
        /// them all. Null means every hull is whole. Written from the grid when it despawns and from a duel's
        /// surviving fraction when the fight is abstract (<see cref="HullDamage"/>); read when the
        /// fleet's bands are rebuilt, when it spawns, and when its hulls land. Persisted (null
        /// omitted); a list whose length does not match the slots is dropped on load.</summary>
        public List<double> HullIntegrity;
        public bool Resolved;              // effects have been applied (persisted so patrol doesn't re-fire)
        public bool Intercepted;            // defeated in combat ÔÇö removed without effect (never persisted)
        public string FleetId;              // 6-hex short ID, unique within the actor's roster; minted at dispatch (MintId)
        /// <summary>When set, overrides <see cref="CompletionRatio"/> at resolution time.
        /// Set to 0.0 when a physical grid is defeated (stall/destroy) so the faction gains no
        /// effects from a raid whose in-world ship never completed its mission. Null = normal.</summary>
        public double? ForcedRatio;
        /// <summary>Why <see cref="ForcedRatio"/> was set, as a short writer tag ("grid_destroyed",
        /// "grid_captured", "grid_stalled", "spawn_dedup", "sibling_dismiss", "guard_duel",
        /// "station_duel", "triple_duel"). Printed on the loss lines so a ratio of 0 can be
        /// attributed to the mechanism that produced it. Persisted alongside
        /// <see cref="ForcedRatio"/>; null when the ratio is the ordinary elapsed one.</summary>
        public string ForcedRatioCause;
        /// <summary>Summed defender DPS this fleet duelled at its mission arrival — Guard bands,
        /// station bands and the flat station floor together — so the fight can be recorded with
        /// what it met once the outcome is known. Set by the duel, read by <see cref="FightLedger"/>
        /// in the same resolution pass; 0 when the arrival was unopposed. Not persisted: the duel
        /// and the terminal it feeds happen in one <c>ResolveArrivals</c> call.</summary>
        public long OpposedDps;
        /// <summary>How the duel at this fleet's mission arrival was fought — both arms and the
        /// brackets — read by <see cref="FightLedger"/> beside <see cref="OpposedDps"/>. Null when
        /// range was not contested. Not persisted, for the same reason.</summary>
        public DuelTactic DuelTactic;
        /// <summary>How the current engagement dwell has been fought so far when part of it was
        /// fought by the grids (<see cref="FightProgress"/>): turns accounted for, each side's
        /// capacity when it began, and whether the grids hold it now. Carried on the attacker only,
        /// null for every fleet whose fight has only ever been abstract, and dropped when the fleet
        /// starts its next leg. Persisted (omitted when null).</summary>
        public FightProgress Fight;
        /// <summary>The last dwell meeting this fleet fought was decided by the grids
        /// (<c>src=physical</c>): the hulls it still carries are exactly the ones that were not
        /// destroyed, so its return leg lands them whatever its ratio. Set or cleared at every
        /// meeting; persisted (omitted when false).</summary>
        public bool HullsMeasured;
        /// <summary>The duel that last decided this fleet at a mission ratio the hull-return
        /// threshold would write off wrote its losses off hull by hull instead, by the capacity it
        /// spent (<see cref="PendingFleetResolver.SettleDuelHulls"/>): the hulls it still carries are
        /// the ones that came out alive, so its terminal lands them whatever its ratio. Persisted
        /// (omitted when false).</summary>
        public bool HullsSettled;
        /// <summary>Damage this fleet's grids dealt and took in the world since the last fight it
        /// was part of resolved, in SE block integrity, as the host's damage hook measured it
        /// (<see cref="PhysicalFightOutcome"/>). Banked by the host as grids leave the world and
        /// before a resolution pass. Not persisted.</summary>
        public double FightDamageDealt;
        public double FightDamageTaken;
        /// <summary>Damage this fleet's rounds would have done to the target's station had its safe
        /// zone not absorbed them, over the same window as <see cref="FightDamageDealt"/>. Credited by
        /// the host's absorbed-fire seams; what lets a raid on a protected station count as having
        /// fired at it. Not persisted.</summary>
        public double FightZoneHits;

        /// <summary>The node this fleet's surviving hull comes home to at its terminal leg --
        /// pinned to the last real node it departed from, so a leg that starts at a clearance
        /// waypoint (<see cref="FleetStopKind.Navigation"/>, where <see cref="OriginNodeId"/> is
        /// null by design because the leg genuinely began at a coordinate) does not erase it. For
        /// <see cref="ActionKind.Mine"/> that is the dwell site: the drill comes home to the deposit
        /// it worked, not to the node it delivered to. Persisted (null omitted); a fleet written
        /// before this field existed loads it null and the terminal falls back to
        /// <see cref="OriginNodeId"/>, which is what it always read.
        /// <para>Provenance: FAC-277 -- a routed mine trip used to lose its drill with no
        /// SHIP_RETURNED and no loss line.</para></summary>
        public string ReturnNodeId;

        /// <summary>The <see cref="FleetId"/> of the raid that holds, or last held, this convoy.
        /// While the convoy is on an <see cref="FleetStopKind.Intercept"/> leg the window's terminal
        /// looks the raid up by it and decides the encounter against it; a raid that is gone by then
        /// (destroyed, repelled, or moved on) frees the convoy. Kept after the verdict so the same
        /// raid never meets this convoy again at a later shared node; a different raid overwrites
        /// it. Null until a convoy has been caught. Persisted (null omitted).
        /// <para>Provenance: FAC-307.</para></summary>
        public string InterceptedByFleetId;

        /// <summary>The bench scenario this fleet belongs to (<c>factions bench fight|raid</c>), or
        /// null for every fleet the simulation dispatched. A bench fleet runs the ordinary pipeline --
        /// materialisation, flight, the meeting duel, the dwell -- but consumed no stock, cost
        /// nothing, and lands no world effect: see <see cref="BenchScenario"/> for the predicates
        /// every gate reads. Never persisted: a restart ends every scenario.</summary>
        public string BenchLabel;
        /// <summary>The effects a bench fleet is explicitly allowed; <see cref="Factions.Core.BenchFlags.None"/>
        /// for every fleet that is not one. Never persisted.</summary>
        public BenchFlags BenchFlags;
        /// <summary>True for a bench scenario fleet. See <see cref="BenchLabel"/>.</summary>
        public bool IsBench { get { return BenchLabel != null; } }
        /// <summary>When a bench attacker's dwell ended and it started to leave the fight
        /// (<see cref="BenchScenario.BeginAftermath"/>); null before, and for every other fleet.
        /// Never persisted.</summary>
        public DateTime? BenchAftermathSinceUtc;
        /// <summary>The scenario's <c>BENCH_RESOLVED</c> line has been written for this attacker.
        /// Never persisted.</summary>
        public bool BenchWrittenUp;
        /// <summary>For an attack its meeting stopped -- broken off, or beaten with hulls still
        /// standing -- how many of its hulls survived to leave (<see cref="PendingFleetResolver"/>'s
        /// broken-attack settlement). Their grids fly out of the world before they are closed
        /// (<see cref="DepartingHulls"/>); what they are worth was credited when the attack was
        /// stopped. Zero for every other fleet. Never persisted: the fleet is removed in the pass
        /// that sets it.</summary>
        public int HullsLeaving;

        /// <summary>How the meeting stopped this attack: <c>broke_off</c> (it left below its break-off
        /// fraction) or <c>beaten</c> (the defence won); null for every other fleet. Read by the
        /// scenario write-up and the host's departure. Never persisted.</summary>
        public string StoppedHow;
        /// <summary>For a <c>broke_off</c> attack, the ratio it left the fight at. Never persisted.</summary>
        public double StoppedRatio;
        /// <summary>What decided the stop: <c>duel</c>, <c>physical</c> or <c>handoff</c>. Never persisted.</summary>
        public string StoppedSource;

        /// <summary>The prefab of each hull of a stopped attack that leaves in the world
        /// (<see cref="HullsLeaving"/>): the squadron's surviving members, or that many of the convoy's
        /// prefab. Empty for a fleet that was not stopped, or whose every hull died.</summary>
        public List<string> DepartingHulls()
        {
            var hulls = new List<string>();
            if (!Intercepted || HullsLeaving <= 0) return hulls;
            if (Squadron != null && Squadron.Count > 0)
            {
                IReadOnlyList<string> standing = HullsToReturn;
                for (int i = 0; i < standing.Count && hulls.Count < HullsLeaving; i++) hulls.Add(standing[i]);
            }
            else
                for (int i = 0; i < HullsLeaving; i++) hulls.Add(ShipPrefabId ?? "");
            return hulls;
        }

        /// <summary>Takes one hull of <paramref name="prefabId"/> off <paramref name="departing"/>
        /// (<see cref="DepartingHulls"/>) for a grid about to be closed, answering whether that grid
        /// leaves instead. A grid whose prefab is unknown (null) takes any hull left.</summary>
        public static bool TakeDeparting(List<string> departing, string prefabId)
        {
            if (departing == null || departing.Count == 0) return false;
            if (prefabId == null) { departing.RemoveAt(0); return true; }
            for (int i = 0; i < departing.Count; i++)
                if (string.Equals(departing[i], prefabId, StringComparison.Ordinal)) { departing.RemoveAt(i); return true; }
            return false;
        }

        /// <summary>A wing commander held a hull of this fleet under an order while it fought
        /// (<see cref="WingOrder"/>): its duel fights this tactic and is learned as a demonstration.
        /// The latest order's tactic wins. Null for a fight nobody commanded. Never persisted: a
        /// restart ends every order.</summary>
        public CommandedTactic? Commanded;

        // --- itinerary routing (service stop → return stop) ---
        /// <summary>Semantic role of the currently active leg. Old persisted fleets have the enum
        /// default (<see cref="FleetStopKind.Service"/>) and no queued stops, so reboot reconciliation
        /// completes them as their legacy single-leg mission instead of migrating live state.</summary>
        public FleetStopKind CurrentStopKind;
        /// <summary>Future legs after the current destination. Returnable missions are created with
        /// one <see cref="FleetStopKind.Return"/> stop for their dispatch origin; Guard and Ferry
        /// remain one-way and leave this null/empty. Persisted.</summary>
        public List<FleetStop> RemainingStops;
        /// <summary>The circuit a Patrol set aside to stand beside a defender already holding a node
        /// a raid is aimed at. It fights there as a Guard and flies these stops again once no raid is
        /// aimed at the node; null for every other fleet. Persisted.
        ///
        /// <para>Provenance: FAC-321.</para></summary>
        public List<FleetStop> SuspendedStops;
        /// <summary>When <see cref="CurrentStopKind"/> is <see cref="FleetStopKind.Pickup"/>, the
        /// SubTypeId/quantity to load at <see cref="TargetNodeId"/> on arrival — carried on the
        /// fleet itself (mirroring how <see cref="TargetNodeId"/>/<see cref="TargetPosKm"/> already
        /// hold "current leg" data rather than a stop) so <c>ResolveSingle</c> can read it without
        /// re-deriving it from an already-dequeued <see cref="FleetStop"/>. Set by a dispatcher for
        /// the initial leg, or by <c>AdvanceToNextStop</c> when it pops a <see
        /// cref="FleetStopKind.Pickup"/>-kind stop; cleared for every other stop kind. Persisted.</summary>
        public string PickupItemKey;
        public long PickupQuantity;
        /// <summary>When <see cref="CurrentStopKind"/> is <see cref="FleetStopKind.Mine"/>, the
        /// normalized SubTypeId-&gt;fraction split and total target liters for the active dwell --
        /// carried on the fleet itself for the same reason <see cref="PickupItemKey"/>/<see
        /// cref="PickupQuantity"/> are (so resolution can read them without re-deriving from an
        /// already-dequeued <see cref="FleetStop"/>). Set by a dispatcher for the initial leg, or by
        /// <c>AdvanceToNextStop</c> when it pops a <see cref="FleetStopKind.Mine"/>-kind stop; cleared
        /// once the dwell resolves. Persisted.</summary>
        public Dictionary<string, double> MineWeights;
        public double MineTargetLiters;
        /// <summary>On a Mine dwell whose deposit sits in gravity, the planet's up axis at the
        /// deposit: the ring <see cref="MineWander"/> flies lies in the tangent plane it defines.
        /// Null in deep space and on every other leg. Set when the dwell starts. Persisted.</summary>
        public Vec3? MineWanderUp;
        /// <summary>Total distance and absolute clock for the active clearance polyline. These let
        /// each persisted navigation leg retain one acceleration profile instead of restarting it
        /// at every waypoint.</summary>
        public double RouteDistanceKm;
        public double RouteCoveredKm;
        public DateTime RouteDepartedUtc;
        /// <summary>Which speed tier each stretch of the geometry this leg was scheduled against
        /// falls in — the whole clearance polyline when one is flying, the straight origin-to-target
        /// line otherwise. Null for a leg that is entirely open space, which is the common case and
        /// what keeps it out of the JSON.
        /// <para>Persisted because the two places that have to read a leg's schedule cannot rebuild
        /// it: <see cref="CurrentPosKm(DateTime, FactionTuning)"/> has no planet catalogue, and the
        /// dashboard is another process with no world at all. Rebuilt by
        /// <c>ReschedulePendingFleetTransits</c> when the transit contract changes.</para></summary>
        public LegZoneProfile LegZones;

        // --- mid-leg retarget (see PendingFleetResolver.Retarget) ---
        /// <summary>How many times this fleet has been redirected mid-leg. Persisted: it bounds
        /// churn across a reboot, and a fleet that has already been pulled around twice should not
        /// come back from disk with a fresh budget.</summary>
        public int RetargetCount;
        /// <summary>When the last mid-leg retarget happened; default when never. Persisted for the
        /// same reason as <see cref="RetargetCount"/> -- it is the cooldown a redirect decision
        /// reads, and losing it on reboot re-opens the oscillation it exists to damp.</summary>
        public DateTime LastRetargetUtc;

        // --- path routing (Cornerstone II / mid-transit encounters) ---
        /// <summary>Ordered graph node IDs from origin to target (inclusive). Null when no graph path
        /// was found (disconnected nodes) or for immediate fast-path fleets. Persisted.</summary>
        public string[] PathNodeIds;
        /// <summary>Index of the last path segment for which a mid-transit meeting was already
        /// checked. Transient ÔÇö initialised to -1 at creation, fast-forwarded on load so past
        /// segments are never re-triggered.</summary>
        public int LastResolvedSegment;

        /// <summary>How many physical hulls this fleet is carrying — the number
        /// <see cref="ShipPool"/> consumed from <see cref="SpatialNode.ShipStock"/> at dispatch and
        /// credits back on a successful return. A mixed <see cref="Squadron"/> counts its members;
        /// every other fleet is <see cref="ConvoySize"/>, floored at 1 because legacy and free-built
        /// fleets may have it unset (the same floor <c>FleetResolution</c>'s return path applies).</summary>
        public int HullCount
        {
            get { return Squadron != null && Squadron.Count > 0 ? Squadron.Count : Math.Max(1, ConvoySize); }
        }

        /// <summary>Whether <see cref="ForcedRatioCause"/> names a loss of the physical grid itself
        /// -- destroyed, captured, stalled or disabled. The bookkeeping causes ("spawn_dedup",
        /// "sibling_dismiss") are not physical losses: nothing about their hulls was observed, and a
        /// sibling-dismissed entry is recalled home with its hull and cargo
        /// (<see cref="PendingFleetResolver.RecallToOrigin"/>). False when no ratio was forced.</summary>
        public bool IsPhysicalLoss
        {
            get
            {
                return ForcedRatioCause == "grid_destroyed"
                    || ForcedRatioCause == "grid_captured"
                    || ForcedRatioCause == "grid_stalled"
                    || ForcedRatioCause == DisabledHull.Cause;
            }
        }

        /// <summary>The hulls this fleet can still credit back: <see cref="SurvivingHulls"/> once a
        /// member has been lost, and the whole <see cref="Squadron"/> before that. Empty (never null)
        /// for a non-squadron fleet, whose hull accounting runs off
        /// <see cref="ShipPrefabId"/>/<see cref="ConvoySize"/> instead.</summary>
        public IReadOnlyList<string> HullsToReturn
        {
            get { return SurvivingHulls ?? Squadron ?? EmptyHulls; }
        }

        private static readonly List<string> EmptyHulls = new List<string>();

        /// <summary>Records that the grid flying <paramref name="prefabId"/> is gone, so the terminal
        /// leg credits back one fewer of that prefab. Seeds <see cref="SurvivingHulls"/> from
        /// <see cref="Squadron"/> on the first call and removes one occurrence -- a squadron carrying
        /// the same prefab twice loses one of the pair, not both. A no-op when this fleet has no
        /// squadron or when that prefab is not among the hulls still standing.
        /// <para><see cref="PendingFleetAction"/> is a struct: the seeding call assigns a field, so a
        /// caller that reads a fleet out of <see cref="FactionState.PendingFleets"/>, calls this and
        /// does not write the value back loses that first casualty.</para></summary>
        public void MarkHullLost(string prefabId)
        {
            if (Squadron == null || Squadron.Count == 0 || prefabId == null) return;
            if (SurvivingHulls == null) SurvivingHulls = new List<string>(Squadron);
            // Of several hulls of that prefab, the most damaged one is the one that died; with no
            // damage recorded that is the first occurrence, which is what List.Remove takes.
            int at = -1;
            double worst = double.MaxValue;
            for (int i = 0; i < SurvivingHulls.Count; i++)
            {
                if (!string.Equals(SurvivingHulls[i], prefabId, StringComparison.Ordinal)) continue;
                double integrity = IntegrityOf(i);
                if (integrity < worst) { worst = integrity; at = i; }
            }
            if (at >= 0) RemoveHullSlot(at);
        }

        /// <summary>Records that the hull in slot <paramref name="slot"/> of
        /// <see cref="HullsToReturn"/> is gone -- the host's form of <see cref="MarkHullLost"/>, for
        /// when it knows which grid died and so which of several same-prefab hulls it was. A no-op
        /// for a fleet with no squadron or a slot out of range. Same struct caveat.</summary>
        public void MarkHullLostAt(int slot)
        {
            if (Squadron == null || Squadron.Count == 0) return;
            if (SurvivingHulls == null) SurvivingHulls = new List<string>(Squadron);
            if (slot < 0 || slot >= SurvivingHulls.Count) return;
            RemoveHullSlot(slot);
        }

        /// <summary>Removes hull slot <paramref name="slot"/> from <see cref="SurvivingHulls"/> and
        /// its <see cref="HullIntegrity"/> entry together, so the two stay parallel. Callers have
        /// already seeded <see cref="SurvivingHulls"/>.</summary>
        private void RemoveHullSlot(int slot)
        {
            SurvivingHulls.RemoveAt(slot);
            if (HullIntegrity != null)
            {
                // A copy: struct copies of this fleet share the list.
                HullIntegrity = new List<double>(HullIntegrity);
                if (slot < HullIntegrity.Count) HullIntegrity.RemoveAt(slot);
                if (HullIntegrity.Count != SurvivingHulls.Count || AllWhole(HullIntegrity)) HullIntegrity = null;
            }
        }

        /// <summary>How many integrity slots this fleet has: one per surviving squadron member, and
        /// one for any other fleet.</summary>
        public int HullSlotCount
        {
            get { return Squadron != null && Squadron.Count > 0 ? HullsToReturn.Count : 1; }
        }

        /// <summary>Integrity of hull slot <paramref name="slot"/>, 1 when nothing is recorded for it.</summary>
        public double IntegrityOf(int slot)
        {
            if (HullIntegrity == null || slot < 0 || slot >= HullIntegrity.Count) return 1.0;
            double v = HullIntegrity[slot];
            return double.IsNaN(v) ? 1.0 : (v < 0 ? 0 : (v > 1 ? 1 : v));
        }

        /// <summary>Records <paramref name="integrity"/> for hull slot <paramref name="slot"/>. A value
        /// at or above <see cref="HullDamage.WholeAt"/> is written as whole, and a fleet whose every
        /// slot is whole goes back to a null list. Ignores a slot outside <see cref="HullSlotCount"/>.
        /// <para>A struct method, like <see cref="MarkHullLost"/>: the first write assigns the list,
        /// so a caller that does not store the fleet back loses it.</para></summary>
        public void SetIntegrity(int slot, double integrity)
        {
            int slots = HullSlotCount;
            if (slot < 0 || slot >= slots) return;
            double v = double.IsNaN(integrity) ? 1.0 : (integrity < 0 ? 0 : (integrity > 1 ? 1 : integrity));
            if (v >= HullDamage.WholeAt) v = 1.0;
            if (HullIntegrity == null || HullIntegrity.Count != slots)
            {
                if (v >= 1.0) return;
                var fresh = new List<double>(slots);
                for (int i = 0; i < slots; i++) fresh.Add(1.0);
                HullIntegrity = fresh;
            }
            else
            {
                // A copy, never an edit in place: struct copies of this fleet share the list.
                HullIntegrity = new List<double>(HullIntegrity);
            }
            HullIntegrity[slot] = v;
            if (AllWhole(HullIntegrity)) HullIntegrity = null;
        }

        private static bool AllWhole(List<double> integrity)
        {
            for (int i = 0; i < integrity.Count; i++) if (!(integrity[i] >= 1.0)) return false;
            return true;
        }

        /// <summary>0..1 fraction of the journey completed. Returns 1.0 when already arrived or on
        /// the immediate fast-path (EstimatedArrivalUtc == DepartedUtc).</summary>
        public double CompletionRatio(DateTime utcNow)
        {
            double total = (EstimatedArrivalUtc - DepartedUtc).TotalMinutes;
            if (total <= 0) return 1.0;
            double elapsed = (utcNow - DepartedUtc).TotalMinutes;
            return elapsed < 0 ? 0 : (elapsed >= total ? 1.0 : elapsed / total);
        }

        /// <summary>Interpolated world position at <paramref name="utcNow"/> using the live
        /// transit acceleration profile.</summary>
        public Vec3 CurrentPosKm(DateTime utcNow, FactionTuning tuning)
        {
            // A miner on its dwell is out on the ring around the deposit, not parked on it.
            if (MineWander.Applies(this)) return MineWander.PositionAt(this, utcNow, tuning);
            double elapsedMins = (utcNow - DepartedUtc).TotalMinutes;
            double r;
            if (tuning == null) r = CompletionRatio(utcNow);
            else if (LegZones != null && LegZones.HasGeometry && RouteDistanceKm <= 0)
                r = tuning.TransitProgressRatio(LegZones, elapsedMins);
            else r = tuning.TransitProgressRatio(OriginPosKm.DistanceTo(TargetPosKm), elapsedMins);
            return new Vec3(
                OriginPosKm.X + (TargetPosKm.X - OriginPosKm.X) * r,
                OriginPosKm.Y + (TargetPosKm.Y - OriginPosKm.Y) * r,
                OriginPosKm.Z + (TargetPosKm.Z - OriginPosKm.Z) * r);
        }

        /// <summary>Legacy time-linear position helper retained for callers without tuning.</summary>
        public Vec3 CurrentPosKm(DateTime utcNow)
        {
            double r = CompletionRatio(utcNow);
            return new Vec3(
                OriginPosKm.X + (TargetPosKm.X - OriginPosKm.X) * r,
                OriginPosKm.Y + (TargetPosKm.Y - OriginPosKm.Y) * r,
                OriginPosKm.Z + (TargetPosKm.Z - OriginPosKm.Z) * r);
        }

        public bool HasArrived(DateTime utcNow) { return utcNow >= EstimatedArrivalUtc; }

        /// <summary>0-based index of the path segment the convoy is currently on
        /// (segment i = PathNodeIds[i] ÔåÆ PathNodeIds[i+1]). Returns -1 if PathNodeIds is null or
        /// too short. Clamps to the last segment once the fleet has arrived.</summary>
        public int CurrentSegmentIdx(DateTime utcNow, SpatialGraph graph)
        {
            if (PathNodeIds == null || PathNodeIds.Length < 2) return -1;
            double total = (EstimatedArrivalUtc - DepartedUtc).TotalMinutes;
            if (total <= 0) return PathNodeIds.Length - 2;
            double elapsed = (utcNow - DepartedUtc).TotalMinutes;
            if (elapsed <= 0) return 0;
            if (elapsed >= total) return PathNodeIds.Length - 2;

            // First pass: total path distance (sum of edge lengths)
            double pathDist = GraphPathfinder.PathDistance(PathNodeIds, graph);
            if (pathDist <= 0) return 0;

            // Second pass: find which segment the covered distance falls in
            double coveredDist = (elapsed / total) * pathDist;
            double cumDist = 0;
            for (int i = 0; i < PathNodeIds.Length - 1; i++)
            {
                SpatialNode a, b;
                double segDist = 0;
                if (graph.Nodes.TryGetValue(PathNodeIds[i], out a) && graph.Nodes.TryGetValue(PathNodeIds[i + 1], out b))
                    segDist = a.Position.DistanceTo(b.Position);
                cumDist += segDist;
                if (coveredDist <= cumDist) return i;
            }
            return PathNodeIds.Length - 2;
        }

        /// <summary>True if this guard has arrived and is still within its active defence window
        /// (the window within which it will intercept incoming raids). A Guard whose grid was
        /// destroyed, captured or stalled (<see cref="IsPhysicalLoss"/>) is out of the fight: it does
        /// not go on defending in the abstract duel.</summary>
        public bool IsActiveGuard(DateTime utcNow, FactionTuning tuning)
        {
            return Kind == ActionKind.Guard && HasArrived(utcNow) && !Intercepted && !IsPhysicalLoss
                   && utcNow < EstimatedArrivalUtc.Add(tuning.GuardDuration);
        }

        /// <summary>The kinds a node's defence is drawn from when a raid reaches it: a
        /// <see cref="ActionKind.Guard"/> standing its duty window, and a
        /// <see cref="ActionKind.Reinforce"/> holding its <see cref="FleetStopKind.Engage"/> dwell.
        /// One list, read by both meeting paths, so which kinds defend cannot drift between the
        /// arrival meeting and the mid-transit one.</summary>
        public static bool IsDefenceKind(ActionKind kind)
        {
            return kind == ActionKind.Guard || kind == ActionKind.Reinforce;
        }

        /// <summary>True while this fleet is standing at a node as part of its defence -- a Guard
        /// inside <see cref="FactionTuning.GuardDuration"/>, or a Reinforce holding its
        /// <see cref="FleetStopKind.Engage"/> dwell at the node it came to reinforce. A reinforce
        /// arrives armed and waits; while it waits it fights whatever attacks the node, and one
        /// destroyed there delivers nothing.
        /// <para>A Guard is deliberately allowed to be <see cref="Resolved"/>: it applies its
        /// arrival effect once and then stands its window. A Reinforce is not -- its effect lands at
        /// the end of the dwell, so a resolved one has already left.</para></summary>
        public bool StandsAsDefender(DateTime utcNow, FactionTuning tuning)
        {
            if (Kind == ActionKind.Guard) return IsActiveGuard(utcNow, tuning);
            // Being ON the Engage leg is what standing at the node means -- the same test
            // BuildDwellingHostileIndex and HoldsStation make. HasArrived is deliberately NOT part
            // of it: a dwell leg's EstimatedArrivalUtc is the END of the window, so requiring it
            // would make the fleet a defender only in the instant the dwell expires.
            if (Kind == ActionKind.Reinforce)
                return !Resolved && !Intercepted && !IsPhysicalLoss && CurrentStopKind == FleetStopKind.Engage;
            return false;
        }

        /// <summary>True while a grid materialised for this fleet must be held at its target rather
        /// than completing the leg: an <see cref="FleetStopKind.Engage"/> or
        /// <see cref="FleetStopKind.Intercept"/> dwell, or a Guard standing its duty window. The
        /// host keeps such a grid in the world, under control and shootable, until
        /// <see cref="HoldEndsUtc"/>; every other arrived fleet with a grid completes its leg.</summary>
        public bool HoldsStation(DateTime utcNow, FactionTuning tuning)
        {
            return CurrentStopKind == FleetStopKind.Engage
                || CurrentStopKind == FleetStopKind.Intercept
                || IsActiveGuard(utcNow, tuning);
        }

        /// <summary>When <see cref="HoldsStation"/> ends: the duty window's end for a Guard, the
        /// leg's own arrival time for a timed dwell.</summary>
        public DateTime HoldEndsUtc(FactionTuning tuning)
        {
            return Kind == ActionKind.Guard ? EstimatedArrivalUtc.Add(tuning.GuardDuration) : EstimatedArrivalUtc;
        }

        /// <summary>Bracketed short ID for log lines, e.g. <c>[A3F2]</c>.
        /// Falls back to a derived hash if <see cref="FleetId"/> is null (fleets loaded from old brain JSON).</summary>
        public string LogId { get { return "[" + (FleetId ?? DeriveId(ActorTag, DepartedUtc)) + "]"; } }

        /// <summary>The 6-hex hash of a dispatch's inputs. Not unique on its own: two fleets one
        /// faction dispatches in the same tick to the same first-leg target hash identically unless
        /// <paramref name="extra"/> differs, and <paramref name="extra"/> is only a caller's guess at
        /// what tells them apart. A new fleet takes its id from
        /// <see cref="MintId(Func{string, bool}, string, DateTime, string, string)"/>, which adds a
        /// dispatch ordinal whenever this hash is already held.</summary>
        internal static string DeriveId(string actorTag, DateTime departedUtc, string targetId = null, string extra = null)
        {
            int h = (actorTag  != null ? actorTag.GetHashCode()  : 0)
                  ^ (int)(departedUtc.Ticks)
                  ^ (int)(departedUtc.Ticks >> 32)
                  ^ (targetId != null ? targetId.GetHashCode() : 0)
                  ^ (extra != null ? extra.GetHashCode() : 0);
            return ((uint)h & 0xFFFFFFu).ToString("X6");
        }

        /// <summary>Highest ordinal <see cref="MintId(Func{string, bool}, string, DateTime, string, string)"/>
        /// tries before settling for the last id it derived. A faction's roster is a few hundred
        /// entries against a 24-bit id space, so the loop ends on its first or second pass.</summary>
        public const int MaxMintOrdinal = 256;

        /// <summary>A <see cref="FleetId"/> for a new dispatch that <paramref name="isHeld"/> does not
        /// report as taken: <see cref="DeriveId"/> over the dispatch's inputs, and when that id is
        /// held, the same with a dispatch ordinal (<c>#1</c>, <c>#2</c>, ...) appended to
        /// <paramref name="extra"/> until one is free. The ordinal is what separates the convoys of
        /// one consolidated haul, which leave in one tick for one pickup and share every other
        /// input. Deterministic: the same roster and inputs always mint the same id. An id no
        /// entry holds is exactly <see cref="DeriveId"/>'s.</summary>
        public static string MintId(Func<string, bool> isHeld, string actorTag, DateTime departedUtc,
                                      string targetId = null, string extra = null)
        {
            string id = DeriveId(actorTag, departedUtc, targetId, extra);
            if (isHeld == null) return id;
            for (int ordinal = 1; ordinal <= MaxMintOrdinal && isHeld(id); ordinal++)
                id = DeriveId(actorTag, departedUtc, targetId,
                              (extra ?? "") + "#" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return id;
        }

        /// <summary><see cref="MintId(Func{string, bool}, string, DateTime, string, string)"/> against
        /// one faction's <paramref name="roster"/>: unique among every entry it holds, resolved or
        /// not. A null roster answers <see cref="DeriveId"/>.</summary>
        public static string MintId(IList<PendingFleetAction> roster, string actorTag, DateTime departedUtc,
                                      string targetId = null, string extra = null)
        {
            if (roster == null) return DeriveId(actorTag, departedUtc, targetId, extra);
            return MintId(id => HoldsId(roster, id), actorTag, departedUtc, targetId, extra);
        }

        /// <summary>Whether any entry of <paramref name="roster"/> carries <paramref name="fleetId"/>.</summary>
        public static bool HoldsId(IList<PendingFleetAction> roster, string fleetId)
        {
            if (roster == null || fleetId == null) return false;
            for (int i = 0; i < roster.Count; i++)
                if (string.Equals(roster[i].FleetId, fleetId, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Whether <paramref name="other"/> is a copy of this fleet entry rather than a fleet
        /// of its own: same actor, departure tick, kind, current target and <see cref="FleetId"/>.
        /// Ids are minted unique within a roster, so two fleets dispatched in one tick to one
        /// target -- the convoys of a consolidated haul -- differ here on <see cref="FleetId"/>.
        /// The host dismisses a copy when the original's grid is lost.</summary>
        public bool IsDuplicateOf(PendingFleetAction other)
        {
            return string.Equals(ActorTag, other.ActorTag, StringComparison.Ordinal)
                && DepartedUtc.Ticks == other.DepartedUtc.Ticks
                && Kind == other.Kind
                && string.Equals(TargetNodeId ?? TargetTag ?? "?", other.TargetNodeId ?? other.TargetTag ?? "?",
                                 StringComparison.Ordinal)
                && string.Equals(FleetId ?? "", other.FleetId ?? "", StringComparison.Ordinal);
        }

        /// <summary>Gives every entry of <paramref name="roster"/> whose <see cref="FleetId"/> repeats
        /// an earlier entry's a freshly minted one, and answers how many changed. The first holder
        /// keeps its id; entries without one are left alone. A brain can carry several distinct
        /// fleets under one id -- each with its own hull and cargo -- and the host keys grid
        /// ownership, stashes and duplicate detection on the id, so they must not load shared.</summary>
        public static int RemintDuplicateIds(IList<PendingFleetAction> roster)
        {
            if (roster == null || roster.Count < 2) return 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int changed = 0;
            for (int i = 0; i < roster.Count; i++)
            {
                PendingFleetAction f = roster[i];
                if (f.FleetId == null || seen.Add(f.FleetId)) continue;
                f.FleetId = MintId(id => HoldsId(roster, id) || seen.Contains(id), f.ActorTag, f.DepartedUtc,
                                   f.TargetNodeId ?? f.TargetTag, f.FleetId);
                seen.Add(f.FleetId);
                roster[i] = f;
                changed++;
            }
            return changed;
        }

        public override string ToString()
        {
            return Kind + " " + ActorTag + "->" + (TargetNodeId ?? TargetTag ?? "?")
                   + " arr@" + EstimatedArrivalUtc.ToString("HH:mm")
                   + (Resolved ? " [done]" : "") + (Intercepted ? " [intercepted]" : "");
        }
    }

    /// <summary>One demand row a faction's own Operations pass found no source for inside its own
    /// estate. Published on <see cref="FactionState.UnservedDemands"/> at the end of that pass and
    /// read by <c>TradeManager</c> to decide which rows may be served across the ownership boundary.
    /// See docs/trade-haul-shaped.md.</summary>
    public struct UnservedDemand
    {
        /// <summary>The node the "Item:" row named — <see cref="MaterialVector.NeedNodeId"/>'s
        /// counterpart for a row that never became a vector.</summary>
        public string NodeId;
        /// <summary>Concrete SubTypeId of the wanted item.</summary>
        public string ItemKey;
        /// <summary>Units still wanted: the row's shortfall less whatever this pass matched toward
        /// it from the owner's own estate.</summary>
        public long Remaining;
        /// <summary>The owner's own estate matched part of the shortfall (under
        /// <see cref="FactionTuning.UnservedPublishCoverage"/>) and this is the remainder. Its owner's
        /// own haul is in flight toward it, so only a Trade convoy or a contract claims it.</summary>
        public bool Partial;
        /// <summary>What raised the row, carried straight from the <see cref="DemandEntry"/>.</summary>
        public DemandClass Class;
        /// <summary><see cref="DemandUrgency"/> score of the row, the same number its owner ranks
        /// its own vectors by.</summary>
        public double Urgency;
    }

    /// <summary>One ranked entry in a faction's inter-faction trade priority list.
    /// Produced each brain Think() cycle by <c>FactionBrain.ComputeTradePriority</c>;
    /// consumed each manager tick by <c>TradeManager</c>.</summary>
    public struct TradePriorityEntry
    {
        /// <summary>Tag of the partner faction to trade with.</summary>
        public string PartnerTag;
        /// <summary>Unnormalised desirability score (╬ú reach ├ù resourceValue ├ù relFactor across partner nodes).
        /// Higher = more attractive lane; list is sorted descending before TradeManager reads it.
        /// <para>It orders partners when several could serve one demand row, and nothing else --
        /// no term of a dispatch decision is multiplied by it. See docs/trade-haul-shaped.md.</para></summary>
        public double Score;
    }

    /// <summary>One ranked entry in a faction's hold-maintenance reinforcement list.
    /// Produced each brain Think() cycle by <c>FactionBrain.ComputeReinforcePriority</c>;
    /// consumed each manager tick by <c>FleetManager</c>.</summary>
    public struct ReinforcePriorityEntry
    {
        /// <summary>Id of the owned node to reinforce.</summary>
        public string NodeId;
        /// <summary>Unnormalised desirability score. Higher = reinforce earlier; list is sorted
        /// descending before FleetManager reads it.</summary>
        public double Score;
    }

    /// <summary>What a <see cref="StandingOrder"/> tells the managers to keep doing. Persisted by
    /// NAME (<see cref="Persistence.StandingOrderDoc.Kind"/>), never by ordinal, so reordering this
    /// enum cannot silently reinterpret an existing brain file.
    ///
    /// <para><b>Build is retired.</b> "Keep N of this hull" is now a <see cref="DoctrineLine"/> on
    /// <see cref="FactionState.FleetDoctrine"/>: a goal that only ever ADDED to whatever the managers
    /// built anyway could not express "these hulls and no others". A brain file still carrying
    /// <c>Kind: "Build"</c> is migrated to a doctrine line on load
    /// (<see cref="Persistence.FactionStore.ApplyBrainDoc"/>) and never written back.</para>
    ///
    /// <para><b>Stock</b> keeps <see cref="StandingOrder.TargetQuantity"/> of one item at one node,
    /// or at every founded station (<see cref="StandingOrder.AllStations"/>): see
    /// <see cref="StockOrders"/>.</para></summary>
    public enum StandingOrderKind { Mine = 0, Stock = 1 }

    /// <summary>One Magnate-tier standing order on <see cref="FactionState.StandingOrders"/> -- the
    /// list a player faction expresses recurring mining and stock-keeping intent through. See that field's doc comment
    /// for the model, the ranking and where it is evaluated.
    ///
    /// <para><see cref="Kind"/> survives the retirement of its second value because the list is
    /// persisted by kind NAME and a new kind of recurring intent is still meant to become a new
    /// <see cref="StandingOrderKind"/> rather than a new collection. Fleet composition is the
    /// exception and has its own list: see <see cref="DoctrineLine"/>.</para></summary>
    public sealed class StandingOrder
    {
        /// <summary>Stable per-faction id ("SO-001"), minted from
        /// <see cref="FactionState.StandingOrderSeq"/> and never reused -- removing one order must
        /// not renumber the ids a player already has written down for the others.</summary>
        public string Id;
        public StandingOrderKind Kind;
        /// <summary>Rank among this faction's standing orders: higher is evaluated first, and first
        /// is what wins a contested resource (a catchment deposit two Mine orders both reach, a
        /// shipyard slot two Build goals both want). Deliberately on the SAME scale as
        /// <see cref="BuildQueueEntry.Priority"/> and <see cref="FactionState.InstallPriority"/> --
        /// a build a goal queues competes for components with the builds and capability installs
        /// already there, so a separate scale would silently lose those contests. A Stock order's
        /// rank claims its item and the item's recipe inputs against those builds and installs,
        /// lifted above every build role when set above 0 (<see cref="StockClaimFloor.ClaimRank"/>).</summary>
        public int Priority;
        /// <summary>Player-set: the order stays in the list, keeps its id and rank, and is skipped
        /// by evaluation. Distinct from removing it, the same way <see cref="SpatialNode.ShipyardPaused"/>
        /// is distinct from cancelling a build.</summary>
        public bool Paused;
        public DateTime CreatedUtc;

        // ---- Kind == Mine ----------------------------------------------------------------------

        /// <summary>Destination hub this order is anchored at. NOT a key: several Mine orders may
        /// share one hub (that is the whole point of the list), and which of them gets a catchment
        /// deposit contested in the same pass is decided by <see cref="Priority"/>.</summary>
        public string HubNodeId;
        /// <summary>Player-set ceiling on the fallback poll rate (see
        /// <see cref="OperationsManager.ComputeNextScanUtc"/>) -- not a floor, since the real lower
        /// bound on a useful rescan is dynamic (whichever catchment node clears its own
        /// <see cref="SpatialNode.MineCooldownUntilUtc"/> soonest), not a constant.</summary>
        public double PeriodSeconds;
        /// <summary>Optional player-requested ore composition (SubTypeId -&gt; percentage, not
        /// necessarily normalized) applied to every catchment node this order fires at -- see
        /// <see cref="MineTargeting.ComputeTargetWeights"/>. Null/empty = each catchment node's own
        /// natural full mix. Two orders on one hub differing only here is the case the old
        /// one-order-per-hub dictionary could not express.</summary>
        public Dictionary<string, double> OreWeights;
        /// <summary>Manager-owned cursor, set adaptively by <see cref="OperationsManager.ComputeNextScanUtc"/>
        /// after each scan. Not player-set.</summary>
        public DateTime NextScanUtc;

        // ---- Kind == Stock ---------------------------------------------------------------------

        /// <summary>Node that keeps the stock. The order does nothing while the faction does not own
        /// it, and resumes if the faction takes it back. Empty when <see cref="AllStations"/>.</summary>
        public string NodeId;
        /// <summary>The target applies at every founded station the faction owns
        /// (<see cref="Economy.HasPhysicalStation"/>), resolved each production pass, instead of at
        /// <see cref="NodeId"/>. A per-node order for the same item at a station overrides it there
        /// (<see cref="StockOrders.Targets"/>).</summary>
        public bool AllStations;
        /// <summary>Inventory key of the item, in the form <see cref="SpatialNode.Inventory"/> uses
        /// (<c>Ingot:Iron</c>, <c>Comp:SteelPlate</c>, a bare magazine subtype).</summary>
        public string ItemKey;
        /// <summary>Units to keep at <see cref="NodeId"/>. Both a demand target and a hold: the node
        /// asks for the gap and gives up only what it holds above this.</summary>
        public long TargetQuantity;

        // ---- manager-owned, not persisted ------------------------------------------------------

        /// <summary>Last scan/evaluation summary logged for this order -- log-on-change dedup only.
        /// Deliberately NOT persisted (see <see cref="Persistence.StandingOrderDoc"/>) and NOT part
        /// of <c>OperationsCycleCommitGuard</c>'s fingerprint: it changes on every scan whose outcome
        /// shifts, which would perpetually invalidate staged cycles.</summary>
        public string LastScanLogSignature;
    }

    /// <summary>One line of a faction's fleet doctrine: "own <see cref="TargetCount"/> of
    /// <see cref="PrefabId"/>, <see cref="MaxConcurrent"/> building at a time, here or anywhere".
    /// Ranked; see <see cref="FactionState.FleetDoctrine"/> for what the list as a whole does and
    /// <see cref="ShipyardManager.ServiceDoctrine"/> for the evaluator.
    ///
    /// <para>The line names ONE hull. Selection heuristics (<c>ShipyardManager.SelectPrefabBom</c>)
    /// are never consulted for a doctrine-active faction -- that is the whole point: the cargo and
    /// Dps floors become advisory numbers a player reads while choosing, not filters that overrule
    /// the choice. The only gate that still applies at placement is route feasibility, since a hull
    /// that cannot fly out of a yard is not a hull that yard can usefully build.</para></summary>
    /// <summary>One line of a player faction's <see cref="FactionState.Shelf"/>: "keep N of this hull
    /// on sale". The store's counterpart to <see cref="DoctrineLine"/>, and deliberately the same
    /// shape -- a player who has learned one list has learned both.
    ///
    /// <para><b>The two lists never compete for the same hull.</b> A doctrine line counts what the
    /// fleet OWNS (<c>ShipStock</c> less the store-reserved slice); a shelf line counts what is
    /// RESERVED FOR SALE (<see cref="SpatialNode.ListedForSale"/>), which fleet dispatch may not
    /// touch. The same prefab may appear in both lists and the counts stay disjoint.</para>
    ///
    /// <para>No <c>Role</c> field: the role a shelf build is queued under is derived from the hull
    /// itself (<see cref="StoreShelfPlan.ClassOf"/>), because a player choosing a hull to sell is not
    /// also choosing what the yard files it as. No <c>ScopeNodeId</c> either -- the shelf is
    /// faction-wide by construction, since <c>StationStoreManager</c> offers every owned node's
    /// reservation at every station that can physically take it.</para></summary>
    public sealed class ShelfLine
    {
        /// <summary>Stable per-faction id ("SH-001"), minted from <see cref="FactionState.ShelfSeq"/>
        /// and never reused, for the reason <see cref="DoctrineLine.Id"/> is not.</summary>
        public string Id;
        /// <summary>Prefab SubtypeId this line sells -- the key <see cref="SpatialNode.ListedForSale"/>
        /// counts by.</summary>
        public string PrefabId;
        /// <summary>How many of <see cref="PrefabId"/> to keep reserved for sale across the estate.
        /// A threshold and a ceiling: the shelf neither grows past it nor is topped up beyond it.</summary>
        public int TargetCount;
        /// <summary>Player-set: the line keeps its id and place and is skipped by the evaluator.
        /// Distinct from removing it, which releases the reservation.</summary>
        public bool Paused;
        public DateTime CreatedUtc;

        // ---- manager-owned, not persisted ------------------------------------------------------

        /// <summary>Log-on-change dedup for this line's <c>SHELF_BUILD</c> line. Rebuilt every run;
        /// a satisfied line must not re-announce itself on every store cadence.</summary>
        public string LastLogSignature;
        /// <summary>Log-on-change dedup for <c>SHELF_LINE_BLOCKED</c>, which is a different question
        /// from the counts and must not be reset by them moving.</summary>
        public string LastBlockedLogSignature;
    }

    public sealed class DoctrineLine
    {
        /// <summary>Stable per-faction id ("FD-001"), minted from
        /// <see cref="FactionState.DoctrineSeq"/> and never reused -- removing one line must not
        /// renumber the ids a player already has written down for the others.</summary>
        public string Id;
        /// <summary>Prefab SubtypeId this line owns -- the same key <see cref="SpatialNode.ShipStock"/>
        /// counts by, which is what makes the line countable against the model rather than against an
        /// SE entity sweep.</summary>
        public string PrefabId;
        /// <summary>How many of <see cref="PrefabId"/> the faction should OWN: parked in scope plus
        /// flying on this faction's pending fleets. A threshold, not a build-N-and-retire counter --
        /// and a CEILING as well as a floor, because a demand no longer creates a build for a
        /// doctrine-active faction.</summary>
        public int TargetCount;
        /// <summary>Ceiling on how many builds of <see cref="PrefabId"/> this line may have in
        /// progress at once. 1 is "one at a time". A line places at most one build per yard per pass,
        /// so reaching a concurrency above 1 in a single evaluation needs that many eligible Shipyard
        /// nodes in scope; across passes it can reach the full count at fewer.</summary>
        public int MaxConcurrent;
        /// <summary>Node the line is confined to, or null for "any owned Shipyard node". Scopes
        /// counting AND placement consistently -- parked stock, in-flight hulls and where a build may
        /// be placed all read the same scope.</summary>
        public string ScopeNodeId;
        /// <summary>Rank among this faction's doctrine lines: higher is evaluated first, and first is
        /// what wins a contested shipyard slot. Deliberately on the SAME scale as
        /// <see cref="BuildQueueEntry.Priority"/> and <see cref="FactionState.InstallPriority"/> -- a
        /// build a line queues competes for components with the builds and capability installs
        /// already there.</summary>
        public int Priority;
        /// <summary>Player-set: the line stays in the list, keeps its id and rank, and is skipped by
        /// the evaluator. Distinct from removing it, the same way <see cref="SpatialNode.ShipyardPaused"/>
        /// is distinct from cancelling a build. A paused line also stops answering demand pressure,
        /// so the pressure line reports it as unlined.</summary>
        public bool Paused;
        public DateTime CreatedUtc;

        // ---- manager-owned, not persisted ------------------------------------------------------

        /// <summary>Last evaluation summary logged for this line -- log-on-change dedup only.
        /// Deliberately NOT persisted (see <see cref="Persistence.DoctrineLineDoc"/>): it changes on
        /// every pass whose outcome shifts and is state about logging, not about the fleet.</summary>
        public string LastLogSignature;
        /// <summary>Last <c>DOCTRINE_LINE_BLOCKED</c> summary logged for this line. A second field
        /// rather than folding the blocked state into <see cref="LastLogSignature"/>, so a line that
        /// is route-blocked for a day does not re-announce it every time an unrelated count (have,
        /// building) moves. Not persisted, same reason.</summary>
        public string LastBlockedLogSignature;
    }

    public sealed class FactionState
    {
        public string Tag;
        public long FactionId;               // SE faction id (adapter-side); unused in sim
        public string PersonalityId;
        public int RngSeed;
        /// <summary>Persisted topology schema carried through runtime flushes. No longer gates edge
        /// restoration (<see cref="FactionStore.RestoreTopology"/> replays every doc's
        /// <c>Neighbors</c> regardless) — retained solely so
        /// <see cref="TerritoryReconciliationPlan.Create"/> can tell whether UNKN's one-shot
        /// <c>factions reconcileterritory apply</c> transaction has already certified its sources.
        /// Ratcheted to at least 1 for every non-UNKN faction on load (<see cref="FactionStore.ApplyBrainDoc"/>).</summary>
        public int TopologyVersion = 1;
        public DriveVector Drives = new DriveVector();
        public readonly Dictionary<string, double> Relations = new Dictionary<string, double>(); // actorId -> -1..1
        /// <summary>The <b>event-driven</b> component of <see cref="Relations"/>: what this faction's
        /// own acts toward another actor have earned, and nothing else. Raid and Conquer sour it,
        /// Trade and Gift lift it, a concluded truce resets it. Border pressure never touches it.
        ///
        /// <para><b>Why it is separate.</b> Against another NPC, standing legitimately IS current
        /// territorial posture — recompute the pressure and the past is gone, and that is the right
        /// model for two simulations. Against a <i>person</i> it is wrong: a player who raids for a
        /// week and withdraws had their standing recomputed from present pressure and climbed back on
        /// their own. Nobody forgave them; the function stopped looking (FAC-189). This ledger is what
        /// keeps looking, so five raids sit five times lower than one indefinitely.</para>
        ///
        /// <para>Maintained for every pair, consulted by <see cref="Diplomacy.Drift"/> only where the
        /// other party has no traits of its own to predict it by. Keeping the writers species-blind is
        /// deliberate — see the seam invariants in docs/player-progression.md.</para>
        ///
        /// <para>Absent entry reads as the current <see cref="Relations"/> value, so a world saved
        /// before this existed carries its standings forward unchanged.</para></summary>
        public readonly Dictionary<string, double> EarnedRelations = new Dictionary<string, double>();
        public readonly Dictionary<string, double> Fatigue = new Dictionary<string, double>();   // war-weariness toward an enemy, 0..1
        public readonly Dictionary<string, DateTime> TruceUntil = new Dictionary<string, DateTime>(); // non-aggression window per enemy
        /// <summary>When this faction last exchanged blows with each other party — stamped by
        /// <see cref="Diplomacy.HitFatigue"/>, the single funnel every raid and conquer resolution
        /// already passes through for BOTH sides. Read only by
        /// <see cref="Diplomacy.AccrueFatigue"/>, to tell a war from a dislike.
        /// <para>Absent entry means "never fought", which is the honest reading for a pair that has
        /// not, and for a world saved before this existed.</para></summary>
        public readonly Dictionary<string, DateTime> LastHostileActUtc = new Dictionary<string, DateTime>();
        /// <summary>Outgoing diplomatic proposals, keyed by recipient tag. Pruned each global pass on
        /// <see cref="Diplomacy.OfferTtl"/>, and persisted, so an offer inside the day-long player window
        /// survives a restart. Use <see cref="Diplomacy.Propose"/> to add; <see cref="Diplomacy.ProcessProposals"/>
        /// to resolve bilateral agreements.</summary>
        public readonly Dictionary<string, DiplomacyMessage> Proposals = new Dictionary<string, DiplomacyMessage>();
        /// <summary>Tags of factions this faction has a concluded formal alliance with.
        /// Persisted in BrainDoc; cleared when a pact is broken (future: pact-break mechanic).</summary>
        public readonly HashSet<string> PactWith = new HashSet<string>();
        /// <summary>Per-enemy grudge records. Written when a faction breaches a truce; causes the brain to
        /// deprioritize gifts and boost raid quality against grudge-tagged enemies. Pruned lazily on TTL expiry.</summary>
        public readonly Dictionary<string, GrudgeRecord> Grudges = new Dictionary<string, GrudgeRecord>();

        /// <summary>What each enemy region's space has cost this faction's lanes, decaying: the weight
        /// conquest ranking reads (<see cref="TransitDenialBook"/>). Not persisted.</summary>
        public readonly TransitDenialBook TransitDenial = new TransitDenialBook();
        /// <summary>Faction tags this faction refuses to trade with, regardless of relation or proximity.
        /// For an NPC, auto-managed by the brain: added when relation falls below EmbargoRelationThreshold,
        /// removed when relation recovers above the threshold plus hysteresis. For a player faction, set only
        /// by its leader (<see cref="PlayerDiplomacy.SetEmbargo"/>). Read from both sides by
        /// <see cref="Diplomacy.TradeBarred"/>.</summary>
        public readonly HashSet<string> Embargoes = new HashSet<string>();
        /// <summary>What this faction has detected, and when. Read through <see cref="KnowledgeView"/>,
        /// which adds the owned-and-adjacent floor this dictionary deliberately does not store --
        /// keeping the floor a read-time rule means it stays correct as territory changes instead of
        /// being frozen by whatever was true when it was written.</summary>
        public readonly FactionKnowledge Knowledge = new FactionKnowledge();

        /// <summary>Players this faction can see right now. <b>Derived and never persisted</b> —
        /// rebuilt by <see cref="PlayerSensing.Detect"/> on every knowledge sweep and empty on load,
        /// for the same reason <see cref="Knowledge"/> never records a fleet: a remembered position
        /// of a moving thing is worse than no position.
        ///
        /// <para>Read-only to the decision layer today. Nothing steers on it — that lands with fleet
        /// retargeting — so it is deliberately a fact the brain could consult rather than one it
        /// does.</para></summary>
        public readonly List<PlayerContact> SensedPlayers = new List<PlayerContact>();
        public readonly Dictionary<ActionKind, DateTime> Cooldowns = new Dictionary<ActionKind, DateTime>();
        public readonly Dictionary<ActionKind, double> ActionRepetition = new Dictionary<ActionKind, double>();
        // Per-partner destination cooldowns for Trade: partnerTag -> next-allowed-dispatch time.
        // Prevents flooding a single trade lane while other lanes remain open.
        public readonly Dictionary<string, DateTime> TradeDestCooldowns = new Dictionary<string, DateTime>();
        /// <summary>Per-target-node dispatch cooldowns for FleetManager's Campaign-focus Patrol
        /// dispatch: nodeId -> next-allowed-dispatch time. Deliberately keyed by node, not the
        /// shared per-kind <see cref="Cooldowns"/> dict — a faction-wide Patrol cooldown would block
        /// a genuinely distinct flashpoint from getting its own patrol while an unrelated node's
        /// cooldown was still active. Only re-dispatching to the SAME node before its previous
        /// patrol has had a chance to land is what this prevents.</summary>
        public readonly Dictionary<string, DateTime> PatrolNodeCooldowns = new Dictionary<string, DateTime>();
        /// <summary>Per-target-node dispatch cooldowns for FleetManager's Reinforce dispatch, the
        /// same shape and for the same reason as <see cref="PatrolNodeCooldowns"/>: a reinforce is a
        /// flying mission now, so a node that already has one inbound must not be sent a second one
        /// on the next tick while the first is still in the air.</summary>
        public readonly Dictionary<string, DateTime> ReinforceNodeCooldowns = new Dictionary<string, DateTime>();
        /// <summary>Magnate-tier presence-as-Reinforce accumulator: nodeId -> continuous seconds of
        /// this faction's own member presence accrued toward one <see cref="ActionKind.Reinforce"/>
        /// (see <see cref="FactionTuning.PresenceReinforceTargetSeconds"/>). Persisted so a player
        /// disconnect or server restart doesn't erase progress — reset to 0 for a node once it fires.
        /// Only ever populated for a faction with <see cref="IsPlayerFaction"/> true.</summary>
        public readonly Dictionary<string, double> ReinforcePresenceSeconds = new Dictionary<string, double>();
        public readonly List<string> Log = new List<string>();

        /// <summary>Recent history for this faction, newest last, bounded by
        /// <see cref="FactionTuning.RecentEventCap"/>. Written only by
        /// <see cref="FactionEventLog.Append"/> and persisted, so a raid that lands while the player
        /// is offline is still there when they log in — which is the case the Events tab exists for.</summary>
        public readonly List<FactionEvent> RecentEvents = new List<FactionEvent>();

        /// <summary>Bumped on every append to <see cref="RecentEvents"/>. Derived, never persisted:
        /// it exists so a subscriber can be sent a snapshot only when the ring actually moved.</summary>
        public int EventsRevision;
        /// <summary>Ranked partner factions by trade desirability, recomputed each brain Think() cycle.
        /// TradeManager reads this list each manager tick to decide whether and where to dispatch.
        /// Brain writes, TradeManager reads ÔÇö never the reverse.</summary>
        public readonly List<TradePriorityEntry> TradePriority = new List<TradePriorityEntry>();
        /// <summary>Demand rows this faction's last Operations pass could not source from its own
        /// nodes, rebuilt from scratch every pass and empty for a dormant or autonomy-disabled
        /// faction. OperationsManager writes, TradeManager reads — never the reverse. Derived and
        /// never persisted, exactly like <see cref="TradePriority"/>.</summary>
        public readonly List<UnservedDemand> UnservedDemands = new List<UnservedDemand>();
        /// <summary>The extraction orders this faction's last committed Operations pass planned
        /// (<see cref="ExtractionPlanner"/>); empty for a player faction. OperationsManager writes,
        /// ShipyardManager reads for capacity growth — never the reverse. Derived and never persisted,
        /// like <see cref="UnservedDemands"/>.</summary>
        public readonly List<StandingExtractionOrder> ExtractionOrders = new List<StandingExtractionOrder>();
        /// <summary>Hubs whose extraction is deposit-bound — the reason and the ore for new ground
        /// (<see cref="ExtractionCapacity.DeriveGroundSignals"/>, docs/mining-plan.md "Capacity
        /// growth"). Rebuilt by OperationsManager every committed pass; the expansion path reads it.
        /// Derived and never persisted.</summary>
        public readonly List<ExtractionGroundSignal> ExtractionGroundSignals = new List<ExtractionGroundSignal>();
        /// <summary>Ranked owned nodes by reinforcement desirability, recomputed each brain Think()
        /// cycle. FleetManager reads this list each manager tick for hold maintenance.
        /// Brain writes, FleetManager reads ÔÇö never the reverse.</summary>
        public readonly List<ReinforcePriorityEntry> ReinforcePriority = new List<ReinforcePriorityEntry>();
        /// <summary>Diagnostic demand signal per ItemKind. No longer updated at runtime (surplus
        /// conversion removed). Kept for persistence compat; may be repurposed by a future
        /// bank/market mechanic.</summary>
        public readonly Dictionary<ItemKind, double> DemandPressure = new Dictionary<ItemKind, double>();
        public readonly List<PendingFleetAction> PendingFleets = new List<PendingFleetAction>(); // in-flight convoys/raiders/patrols; reconciled/resolved on session load
        /// <summary>(NodeId, ItemKey) pairs currently claimed by an accepted player Acquisition/Haul
        /// contract — <see cref="OperationsManager"/> treats these exactly like an in-flight NPC Haul
        /// convoy and skips dispatching a duplicate. Deliberately transient: NOT mapped in
        /// <c>Persistence.cs</c>/<c>BrainDoc</c>, mirrors the host's own in-memory-only completion-hook
        /// tracking for Reinforce-via-Repair/Guard-via-PvEBounty (see docs/contracts.md) — a claim
        /// still open across a Torch restart simply loses this suppression until the contract
        /// completes, same known limitation as those two precedents.</summary>
        public readonly HashSet<(string NodeId, string ItemKey)> ExternalHaulClaims = new HashSet<(string, string)>();
        public DateTime DormantUntilUtc;     // > MinValue while conquered/dormant (indefinite for now; revival deferred)
        /// <summary>True for a Magnate-claimed faction: it has no brain/personality choosing
        /// strategic actions (Expand/Raid/Conquer/Found/Develop/etc. — see
        /// <see cref="FactionLifecycle.IsAutonomyDisabled"/>), and <see cref="FleetManager"/>/
        /// <see cref="TradeManager"/> also leave it alone (both depend on brain-computed priority
        /// lists) — the human player supplies those decisions instead. `Production`/`Shipyard`/
        /// `Store`/`OperationsManager` are NOT gated by this flag (as of 2026-08-16): they are
        /// self-sufficient and run for a player faction same as an NPC. Deliberately NOT folded into
        /// <see cref="DormantUntilUtc"/>/<see cref="FactionLifecycle.IsDormant"/>: that predicate
        /// also excludes a faction from rival/relations/influence math, which a player faction must
        /// stay visible for. See <see cref="FactionLifecycle.IsAutonomyDisabled"/> for the gate this
        /// flag feeds — go through that predicate, not this field directly, at any new call site.</summary>
        public bool IsPlayerFaction;
        /// <summary>Magnate-tier opt-in: capabilities this player faction has explicitly enabled
        /// <see cref="ProductionManager"/>'s proactive investment-request/reservation behavior for
        /// (see docs/economy-territory.md → "Player-faction claiming"). Empty by default — a player
        /// faction's components are never silently reserved toward a capability they didn't ask for
        /// (see <see cref="FactionLifecycle.CanPostInvestmentFor"/>, the gate this feeds). Only ever
        /// populated for a faction with <see cref="IsPlayerFaction"/> true; an NPC faction's
        /// investment posting is unaffected by this set's contents regardless.</summary>
        public readonly HashSet<NodeCapabilityKind> ReservationOptIn = new HashSet<NodeCapabilityKind>();
        /// <summary>Claim rank per capability for <see cref="ProductionManager"/>'s capability-install
        /// reservation, on the same scale as <see cref="BuildQueueEntry.Priority"/>. An absent
        /// capability reads as 0, and a build blocks an install on a tie, so an empty dictionary
        /// reproduces the original "an in-progress build always outranks a capability upgrade"
        /// behaviour exactly. Raising a capability above the build queues competing for the same
        /// component is what lets a stalled install actually fund itself. Applies to NPC and player
        /// factions alike (see <see cref="InstallRankFor"/>).</summary>
        public readonly Dictionary<NodeCapabilityKind, int> InstallPriority = new Dictionary<NodeCapabilityKind, int>();

        /// <summary>Nodes this faction has asked to have kept at hold, and the rank they are served
        /// in — node id → rank, higher first, on the same scale as <see cref="InstallPriority"/>.
        ///
        /// <para><b>Player-set and persisted, unlike <see cref="ReinforcePriority"/>.</b> That list
        /// is derived: an NPC's brain rebuilds it every <c>Think()</c>, so it correctly survives
        /// nothing. A player's intent has to outlive a restart or the upkeep it buys stops at the
        /// next one, so the durable half lives here and is projected into the derived list each tick
        /// (<see cref="ProjectPlayerReinforcePriority"/>) exactly as the brain projects for an
        /// NPC.</para>
        ///
        /// <para>Empty for an NPC faction and never read for one — the brain's ranking already
        /// covers the whole estate, and a second source would silently compete with it.</para></summary>
        public readonly Dictionary<string, int> ReinforceOptIn = new Dictionary<string, int>();

        /// <summary>Per-faction overrides for the three reinforce spend knobs; null means "use the
        /// world's tuning". Set by a player deciding how much of their own money upkeep may take.
        ///
        /// <para><b>Null, not zero, is the unset value</b>, and the difference matters:
        /// <see cref="SpendBudget.CanSpend"/> treats an income fraction and a floor that are BOTH
        /// zero as <i>uncapped</i>, not as "spend nothing". So a defaulted-to-zero override would
        /// silently hand a faction unlimited upkeep spending — the opposite of what a zero reads
        /// like. The surface that sets these refuses that pair for the same reason.</para>
        ///
        /// <para>The treasury is still the hard limit either way: a reinforce is skipped outright
        /// when the faction cannot afford <see cref="FactionTuning.ReinforceCost"/>, whatever the
        /// budget says.</para></summary>
        public double? ReinforceIncomeFractionOverride;
        public long? ReinforceBudgetFloorOverride;
        public double? ReinforceWealthFractionOverride;

        /// <summary>This faction's reinforce income-fraction, its own if set, the world's otherwise.</summary>
        public double ReinforceIncomeFraction(FactionTuning t)
        {
            return ReinforceIncomeFractionOverride.HasValue
                ? ReinforceIncomeFractionOverride.Value
                : (t != null ? t.ReinforceMaxIncomeFractionPerWindow : 0);
        }

        /// <summary>This faction's reinforce budget floor, its own if set, the world's otherwise.</summary>
        public long ReinforceBudgetFloorFor(FactionTuning t)
        {
            return ReinforceBudgetFloorOverride.HasValue
                ? ReinforceBudgetFloorOverride.Value
                : (t != null ? t.ReinforceBudgetFloor : 0);
        }

        /// <summary>This faction's reinforce treasury-surplus fraction, its own if set, the world's
        /// otherwise.</summary>
        public double ReinforceWealthFraction(FactionTuning t)
        {
            return ReinforceWealthFractionOverride.HasValue
                ? ReinforceWealthFractionOverride.Value
                : (t != null ? t.ReinforceWealthFractionPerWindow : 0);
        }

        /// <summary>True when this faction has set any reinforce budget override.</summary>
        public bool HasReinforceBudgetOverride
        {
            get
            {
                return ReinforceIncomeFractionOverride.HasValue
                    || ReinforceBudgetFloorOverride.HasValue
                    || ReinforceWealthFractionOverride.HasValue;
            }
        }

        /// <summary>Magnate-tier opt-in: whether <see cref="TradeManager"/> may fly this player
        /// faction's own convoys out to BUY what its estate could not source
        /// (<c>RunBuyPass</c>), and whether an NPC may see its shortage at all.
        ///
        /// <para><b>Two switches rather than one</b>, because the directions cost different things.
        /// Selling moves goods the faction already has and earns; buying spends the treasury. A
        /// player willing to do the first is not thereby willing to do the second, and the manager
        /// already splits along exactly this line.</para>
        ///
        /// <para>This flag also releases <c>OperationsManager.PublishUnservedDemands</c>, which is
        /// what lets a neighbour's sell pass see the shortage. Both halves of acquisition therefore
        /// hang off this one flag: our convoy going out, and theirs coming in.</para>
        ///
        /// <para>Empty default (false) for both. Only ever true for a faction with
        /// <see cref="IsPlayerFaction"/> true; an NPC trades on its brain's ranking regardless of
        /// what these hold. Go through <see cref="FactionLifecycle.TradeBuyEnabled"/> /
        /// <see cref="FactionLifecycle.TradeSellEnabled"/> rather than reading the fields.</para></summary>
        public bool TradeBuyOptIn;

        /// <summary>The sell half of <see cref="TradeBuyOptIn"/>: whether <see cref="TradeManager"/>
        /// may fly this player faction's surplus out to a partner that has posted a demand for it.
        ///
        /// <para>Needs strictly less than the buy half. <c>RunSellPass</c> reads the PARTNER's
        /// unserved rows, which are published for an NPC already, so this direction needs only the
        /// manager gate and a partner list.</para></summary>
        public bool TradeSellOptIn;

        /// <summary>Per-faction overrides for the three trade spend knobs; null means "use the
        /// world's tuning". The trade counterpart of
        /// <see cref="ReinforceIncomeFractionOverride"/> and friends, and null carries the same
        /// meaning for the same reason: <see cref="SpendBudget.CanSpend"/> reads a zero
        /// income-fraction and a zero floor as UNCAPPED, so a defaulted zero would grant unlimited
        /// spending rather than none. The surface that sets these refuses that pair.
        ///
        /// <para>The treasury remains the hard limit regardless: a pass is skipped outright when the
        /// faction cannot afford <see cref="FactionTuning.TradeCost"/>.</para>
        ///
        /// <para>These bound what the faction spends DISPATCHING. What it pays for the goods is
        /// settled at the stop against the recipient-side inbound budget, which is world-global and
        /// not overridden here.</para></summary>
        public double? TradeIncomeFractionOverride;
        public long? TradeBudgetFloorOverride;
        public double? TradeWealthFractionOverride;

        /// <summary>This faction's trade income-fraction, its own if set, the world's otherwise.</summary>
        public double TradeIncomeFraction(FactionTuning t)
        {
            return TradeIncomeFractionOverride.HasValue
                ? TradeIncomeFractionOverride.Value
                : (t != null ? t.TradeMaxIncomeFractionPerWindow : 0);
        }

        /// <summary>This faction's trade budget floor, its own if set, the world's otherwise.</summary>
        public long TradeBudgetFloorFor(FactionTuning t)
        {
            return TradeBudgetFloorOverride.HasValue
                ? TradeBudgetFloorOverride.Value
                : (t != null ? t.TradeBudgetFloor : 0);
        }

        /// <summary>This faction's trade treasury-surplus fraction, its own if set, the world's
        /// otherwise.</summary>
        public double TradeWealthFraction(FactionTuning t)
        {
            return TradeWealthFractionOverride.HasValue
                ? TradeWealthFractionOverride.Value
                : (t != null ? t.TradeWealthFractionPerWindow : 0);
        }

        /// <summary>True when this faction has set any trade budget override.</summary>
        public bool HasTradeBudgetOverride
        {
            get
            {
                return TradeIncomeFractionOverride.HasValue
                    || TradeBudgetFloorOverride.HasValue
                    || TradeWealthFractionOverride.HasValue;
            }
        }

        /// <summary>This faction's claim rank for installing <paramref name="cap"/>; 0 when unset.
        ///
        /// <para>A rank the operator actually set is lifted by
        /// <see cref="FactionTuning.HighestBuildRolePriority"/> when <paramref name="tuning"/> is
        /// supplied, so <c>develop &lt;cap&gt; priority 1</c> keeps meaning "outrank an ordinary
        /// build" now that builds start at a role-derived rank rather than 0. Without the lift the
        /// same 1 would silently lose to every build, and the lever would need a number matching an
        /// internal table nobody sees.</para>
        ///
        /// <para><b>An unset capability stays at 0</b> and loses to every build, exactly as before.
        /// Lifting that too would have an install nobody ranked start beating builds — the opposite
        /// of the all-default behaviour this preserves. An explicit 0 is likewise left alone: it
        /// reads as "do not prioritise this".</para></summary>
        public int InstallRankFor(NodeCapabilityKind cap, FactionTuning tuning = null)
        {
            int rank;
            if (!InstallPriority.TryGetValue(cap, out rank)) return 0;
            if (rank <= 0 || tuning == null) return rank;
            return rank + tuning.HighestBuildRolePriority();
        }
        /// <summary>Magnate-tier standing orders -- the ONE list a player faction expresses recurring
        /// intent through, replacing the old <c>hubNodeId -&gt; MineAutoOrder</c> dictionary (migrated
        /// on load by <see cref="Persistence.FactionStore"/>). A second store beside this one is
        /// exactly what this list exists to avoid, so a new kind of recurring intent becomes a new
        /// <see cref="StandingOrderKind"/>, not a new collection.
        ///
        /// <para><b>Mine</b> orders are anchored at the DESTINATION (e.g. a refinery cluster), not a
        /// single source: each scan (<see cref="OperationsManager.ScanStandingMineOrder"/>)
        /// auto-discovers every owned mineable node within
        /// <see cref="FactionTuning.MineSearchRadiusKm"/> of the hub -- the same reach an NPC
        /// extraction order walks -- and tops up in-flight Mine fleets toward the hub,
        /// capped at one fleet per catchment node. Several orders may share a hub; they are scanned
        /// in <see cref="StandingOrder.Priority"/> order and a deposit claimed by the higher-ranked
        /// order in a pass is not offered to a lower-ranked one, which is what keeps two overlapping
        /// catchments from dispatching twice against the same deposit before either dwell resolves.</para>
        ///
        /// <para><b>Stock</b> orders keep a quantity of one item at one owned node: the production pass
        /// posts the gap as demand and the node keeps the target back from hauls and sales
        /// (<see cref="StockOrders"/>). Rank does not apply to them.</para>
        ///
        /// <para><b>Build goals are no longer here.</b> "Keep N of this hull" moved to
        /// <see cref="FleetDoctrine"/>, which is a pool and a ceiling rather than an additive goal.
        /// A brain file carrying the old kind is migrated on load and never written back.</para>
        ///
        /// <para>Bounded by <see cref="FactionTuning.StandingOrderCap"/> and persisted in
        /// <c>BrainDoc.StandingOrders</c>, omitted when empty. Only ever populated for a faction with
        /// <see cref="IsPlayerFaction"/> true -- an NPC faction's recurring intent is what
        /// <c>FactionBrain</c> is for.</para></summary>
        public readonly List<StandingOrder> StandingOrders = new List<StandingOrder>();

        /// <summary>This faction's fleet doctrine: the ranked list of <see cref="DoctrineLine"/>s
        /// saying what the fleet is made of. Empty is the default and means "behave exactly as
        /// before" -- see <see cref="FactionLifecycle.DoctrineActive"/>, the one predicate everything
        /// downstream reads.
        ///
        /// <para><b>While a doctrine exists it is the only thing that puts a build on a yard.</b>
        /// <see cref="ShipyardManager.PostProactiveShipDemands"/> posts nothing, and a
        /// <c>Ship:{role}</c> demand from anywhere else never creates a build -- it stays in the
        /// register as PRESSURE (work waiting on hulls) until its TTL retires it. So a line at its
        /// target with a blocked haul stays at its target: growing the fleet is the player's call,
        /// not a dispatcher's.</para>
        ///
        /// <para>Deliberately species-blind: the evaluator reads this list and does not care who
        /// wrote it, so an NPC's doctrine can later be authored from its personality without moving
        /// the evaluator. Only the two "autonomy off" gates ask
        /// <see cref="FactionState.IsPlayerFaction"/>.</para>
        ///
        /// <para>Bounded by <see cref="FactionTuning.FleetDoctrineCap"/> and persisted in
        /// <c>BrainDoc.FleetDoctrine</c>, omitted when empty.</para></summary>
        public readonly List<DoctrineLine> FleetDoctrine = new List<DoctrineLine>();

        /// <summary>Monotonic counter behind <see cref="DoctrineLine.Id"/>. Persisted alongside the
        /// list so ids stay unique across a restart, and never decremented on removal.</summary>
        public int DoctrineSeq;

        /// <summary>Bumped on every player change to <see cref="FleetDoctrine"/>. Derived, never
        /// persisted -- the same revision-driven send <see cref="StandingOrdersRevision"/> serves.</summary>
        public int FleetDoctrineRevision;

        /// <summary>Log-on-change dedup for <c>DOCTRINE_PRESSURE</c>, keyed <c>role|nodeId</c>.
        /// Manager-owned and never persisted: unlike a doctrine line, a (role, node) pair with
        /// waiting work is not an object that outlives the tick, so the signature has nowhere else to
        /// live. Bounded by the role vocabulary times the owned-node count.</summary>
        public readonly Dictionary<string, string> DoctrinePressureLogSignatures =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Adds <paramref name="line"/> and marks the doctrine changed.</summary>
        public void AddDoctrineLine(DoctrineLine line)
        {
            if (line == null) return;
            FleetDoctrine.Add(line);
            FleetDoctrineRevision++;
        }

        /// <summary>Removes <paramref name="line"/>, marking the doctrine changed if it was there.</summary>
        public bool RemoveDoctrineLine(DoctrineLine line)
        {
            if (line == null || !FleetDoctrine.Remove(line)) return false;
            FleetDoctrineRevision++;
            return true;
        }

        /// <summary>Re-ranks <paramref name="line"/> and marks the doctrine changed. A rank set to
        /// what it already was still counts: the player asked for it.</summary>
        public void SetDoctrineLinePriority(DoctrineLine line, int rank)
        {
            if (line == null) return;
            line.Priority = rank;
            FleetDoctrineRevision++;
        }

        /// <summary>Pauses or resumes <paramref name="line"/> and marks the doctrine changed.</summary>
        public void SetDoctrineLinePaused(DoctrineLine line, bool paused)
        {
            if (line == null) return;
            line.Paused = paused;
            FleetDoctrineRevision++;
        }

        /// <summary>Sets how many hulls <paramref name="line"/> wants owned and marks the doctrine
        /// changed. The target is a ceiling as well as a floor, so lowering it does not scrap
        /// anything -- it stops replacing losses.</summary>
        public void SetDoctrineLineTarget(DoctrineLine line, int targetCount)
        {
            if (line == null) return;
            line.TargetCount = targetCount;
            FleetDoctrineRevision++;
        }

        /// <summary>Sets how many builds of <paramref name="line"/>'s hull may be in progress at
        /// once and marks the doctrine changed. A line places at most one build per yard per pass, so
        /// a concurrency above 1 is reached across passes rather than in one.</summary>
        public void SetDoctrineLineConcurrency(DoctrineLine line, int maxConcurrent)
        {
            if (line == null) return;
            line.MaxConcurrent = maxConcurrent;
            FleetDoctrineRevision++;
        }

        /// <summary>What this player faction keeps on sale: "N of this hull", faction-wide. Empty is
        /// the default and means unchanged -- <see cref="StoreStockManager"/> chooses hulls by class
        /// balance and fills to the <see cref="FactionTuning.StoreShipSlotsSmallGrid"/> /
        /// <c>...LargeGrid</c> targets exactly as it does for an NPC. A non-empty list replaces BOTH:
        /// the player names the hulls and the depth, and the two grid targets stop applying to this
        /// faction.
        ///
        /// <para>Emptiness cannot express "sell nothing", which is why
        /// <see cref="StoreShelfOptOut"/> exists beside it rather than being encoded here. Same
        /// reasoning as <see cref="FleetDoctrine"/>: a player removing their last line gets the old
        /// behaviour back, not a shop that silently stops restocking.</para>
        ///
        /// <para>Bounded by <see cref="FactionTuning.PlayerShelfCap"/> and persisted in
        /// <c>BrainDoc.Shelf</c>, omitted when empty.</para></summary>
        public readonly List<ShelfLine> Shelf = new List<ShelfLine>();

        /// <summary>Design family → how many of its hulls may be in refit at once
        /// (<see cref="HullRefit"/>). Player-set; a family with no entry is never refit.</summary>
        public readonly Dictionary<string, int> HullRefitCaps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Design family → how many of its hulls, flying when the design was decommissioned,
        /// are still to be dismantled when they come home (<see cref="FleetDecommission.SweepReturned"/>).
        /// Set by a decommission of every hull; empty for almost every faction.</summary>
        public readonly Dictionary<string, int> DecommissionOnReturn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Monotonic counter behind <see cref="ShelfLine.Id"/>. Persisted alongside the list
        /// so ids stay unique across a restart, and never decremented on removal.</summary>
        public int ShelfSeq;

        /// <summary>Bumped on every player change to <see cref="Shelf"/> or
        /// <see cref="StoreShelfOptOut"/>. Derived, never persisted.</summary>
        public int ShelfRevision;

        /// <summary>The player has switched their store's ship restocking off entirely.
        ///
        /// <para><b>Opt-OUT, not opt-in, and the polarity is the whole point.</b>
        /// <see cref="TradeBuyOptIn"/>/<see cref="TradeSellOptIn"/> are opt-in because
        /// <see cref="TradeManager"/> does not run for a player faction at all without them;
        /// <see cref="StoreStockManager"/> does run, so a flag defaulting to "off" here would silently
        /// close every player store that exists. The pattern is shared -- one faction-wide flag, a
        /// chat verb, a field on the node detail -- the default is not.</para>
        ///
        /// <para>Set, the shelf stops being restocked and every store build in flight is refunded,
        /// the same handling a faction type that sells nothing already gets. It does not un-list what
        /// is already on sale: a hull a player could have bought a minute ago does not vanish because
        /// the owner stopped ordering more.</para></summary>
        public bool StoreShelfOptOut;

        /// <summary>Inventory keys (<c>Comp:SteelPlate</c>) the central bank may not buy from this
        /// faction at all (<see cref="BankSaleMode.DontSell"/>). Change it through
        /// <see cref="SetCentralBankRule"/>, which keeps a key in at most one of this and
        /// <see cref="CentralBankTargets"/>.</summary>
        public readonly HashSet<string> CentralBankDontSell = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Inventory key to a faction-wide stock target the central bank may not buy below
        /// (<see cref="BankSaleMode.AboveTarget"/>): it still buys only a node's oversupply, and only
        /// what the faction holds past the target across every node it owns.</summary>
        public readonly Dictionary<string, long> CentralBankTargets = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Read from a brain file written while the bank switch was faction-wide, and set
        /// only until the host's load pass has turned it into a don't-sell rule for every bank-buyable
        /// item (<see cref="FactionLifecycle.ExpandLegacyBankSellOptOut"/>). Written back only while
        /// it is still unexpanded.</summary>
        public bool LegacyCentralBankSellOptOut;

        /// <summary>Sets how the central bank may buy <paramref name="inventoryKey"/> from this
        /// faction. <paramref name="target"/> is read only for <see cref="BankSaleMode.AboveTarget"/>,
        /// and a target of zero or less is the same as <see cref="BankSaleMode.SellOversupply"/>, so
        /// it is stored as that. Returns true when the rule changed.</summary>
        public bool SetCentralBankRule(string inventoryKey, BankSaleMode mode, long target)
        {
            if (string.IsNullOrEmpty(inventoryKey)) return false;
            if (mode == BankSaleMode.AboveTarget && target <= 0) mode = BankSaleMode.SellOversupply;

            long oldTarget = 0;
            BankSaleMode old = CentralBankDontSell.Contains(inventoryKey) ? BankSaleMode.DontSell
                : CentralBankTargets.TryGetValue(inventoryKey, out oldTarget) ? BankSaleMode.AboveTarget
                : BankSaleMode.SellOversupply;
            if (old == mode && (mode != BankSaleMode.AboveTarget || oldTarget == target)) return false;

            CentralBankDontSell.Remove(inventoryKey);
            CentralBankTargets.Remove(inventoryKey);
            if (mode == BankSaleMode.DontSell) CentralBankDontSell.Add(inventoryKey);
            else if (mode == BankSaleMode.AboveTarget) CentralBankTargets[inventoryKey] = target;
            return true;
        }

        /// <summary>The rule for <paramref name="inventoryKey"/>; <paramref name="target"/> is zero
        /// unless it is <see cref="BankSaleMode.AboveTarget"/>.</summary>
        public BankSaleMode CentralBankRuleFor(string inventoryKey, out long target)
        {
            target = 0;
            if (string.IsNullOrEmpty(inventoryKey)) return BankSaleMode.SellOversupply;
            if (CentralBankDontSell.Contains(inventoryKey)) return BankSaleMode.DontSell;
            return CentralBankTargets.TryGetValue(inventoryKey, out target)
                ? BankSaleMode.AboveTarget : BankSaleMode.SellOversupply;
        }

        /// <summary>Items with a rule other than the default.</summary>
        public int CentralBankRuleCount { get { return CentralBankDontSell.Count + CentralBankTargets.Count; } }

        /// <summary>Adds <paramref name="line"/> and marks the shelf changed.</summary>
        public void AddShelfLine(ShelfLine line)
        {
            if (line == null) return;
            Shelf.Add(line);
            ShelfRevision++;
        }

        /// <summary>Removes <paramref name="line"/>, marking the shelf changed if it was there. The
        /// reservation it was holding is handed back by <see cref="StoreShelfRelease"/>'s next pass,
        /// not here -- releasing stock is the release pass's job and it has a budget.</summary>
        public bool RemoveShelfLine(ShelfLine line)
        {
            if (line == null || !Shelf.Remove(line)) return false;
            ShelfRevision++;
            return true;
        }

        /// <summary>Sets how many of <paramref name="line"/>'s hull to keep on sale and marks the
        /// shelf changed. Lowering it does not scrap anything; the release pass hands the excess
        /// back over its own budget.</summary>
        public void SetShelfLineTarget(ShelfLine line, int targetCount)
        {
            if (line == null) return;
            line.TargetCount = targetCount;
            ShelfRevision++;
        }

        /// <summary>Pauses or resumes <paramref name="line"/> and marks the shelf changed. A paused
        /// line stops restocking and keeps what it already holds -- it is not a release.</summary>
        public void SetShelfLinePaused(ShelfLine line, bool paused)
        {
            if (line == null) return;
            line.Paused = paused;
            ShelfRevision++;
        }

        /// <summary>Switches this faction's shelf restocking off or back on.</summary>
        public void SetStoreShelfOptOut(bool optOut)
        {
            StoreShelfOptOut = optOut;
            ShelfRevision++;
        }

        /// <summary>Mints the next never-reused <see cref="ShelfLine.Id"/> for this faction.</summary>
        public string MintShelfLineId()
        {
            ShelfSeq++;
            return "SH-" + ShelfSeq.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The line naming <paramref name="prefabId"/>, or null. One lookup so the evaluator
        /// and the release pass cannot disagree about whether a listed hull is still wanted.</summary>
        public ShelfLine ShelfLineFor(string prefabId)
        {
            if (string.IsNullOrEmpty(prefabId)) return null;
            for (int i = 0; i < Shelf.Count; i++)
                if (string.Equals(Shelf[i].PrefabId, prefabId, StringComparison.OrdinalIgnoreCase))
                    return Shelf[i];
            return null;
        }

        /// <summary>Mints the next never-reused <see cref="DoctrineLine.Id"/> for this faction.</summary>
        public string MintDoctrineLineId()
        {
            DoctrineSeq++;
            return "FD-" + DoctrineSeq.ToString("D3");
        }

        /// <summary>This faction's doctrine in evaluation order: highest
        /// <see cref="DoctrineLine.Priority"/> first, id as the tie-break. A total order with no
        /// dependence on list insertion order, so the sim and the host place the same build given the
        /// same world -- the id rather than the creation time, since two lines added in one command
        /// batch can share a timestamp and the id cannot.</summary>
        public List<DoctrineLine> DoctrineByRank()
        {
            var result = new List<DoctrineLine>(FleetDoctrine);
            result.Sort(CompareDoctrineLines);
            return result;
        }

        private static int CompareDoctrineLines(DoctrineLine a, DoctrineLine b)
        {
            int byRank = b.Priority.CompareTo(a.Priority);
            if (byRank != 0) return byRank;
            return string.CompareOrdinal(a.Id, b.Id);
        }

        /// <summary>The doctrine line with <paramref name="id"/> (case-insensitive, since a player
        /// types it), or null.</summary>
        public DoctrineLine FindDoctrineLine(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (DoctrineLine line in FleetDoctrine)
                if (string.Equals(line.Id, id, StringComparison.OrdinalIgnoreCase)) return line;
            return null;
        }

        /// <summary>What this faction's jobs of each role have required, keyed by role name
        /// ("Trade", "Mine", "Raid", ...). Bounded by the role vocabulary, not by the prefab
        /// catalogue — deliberately, since a per-prefab record could only ever rank hulls already
        /// owned and never recommend the class a fleet is missing. See docs/fleet-fitness-plan.md.</summary>
        public readonly Dictionary<string, RoleRequirement> FleetRequirements =
            new Dictionary<string, RoleRequirement>(StringComparer.Ordinal);

        /// <summary>Fetches or creates this faction's requirement record for <paramref name="role"/>.</summary>
        public RoleRequirement RequirementFor(string role)
        {
            if (string.IsNullOrEmpty(role)) role = "Trade";
            RoleRequirement r;
            if (!FleetRequirements.TryGetValue(role, out r))
            {
                r = new RoleRequirement();
                FleetRequirements[role] = r;
            }
            return r;
        }
        /// <summary>Monotonic counter behind <see cref="StandingOrder.Id"/>. Persisted alongside the
        /// list so ids stay unique across a restart, and never decremented on removal.</summary>
        public int StandingOrderSeq;

        /// <summary>Bumped on every change to <see cref="StandingOrders"/> that a player made —
        /// added, removed, re-ranked, paused, resumed. Derived, never persisted: it exists so a
        /// subscriber can be sent a snapshot only when the list actually moved, the same way
        /// <see cref="EventsRevision"/> serves the Events feed.
        ///
        /// <para>Deliberately NOT bumped by <see cref="StandingOrder.NextScanUtc"/>, which the
        /// manager rewrites after every scan. A viewer wanting a live countdown gets the timestamp
        /// itself and does the arithmetic locally; bumping on a cursor would send the whole list to
        /// every open window on every scan for a number the client can derive.</para></summary>
        public int StandingOrdersRevision;

        /// <summary>Adds <paramref name="order"/> and marks the list changed. Use this rather than
        /// touching <see cref="StandingOrders"/> directly, so the revision cannot drift from the
        /// contents.</summary>
        public void AddStandingOrder(StandingOrder order)
        {
            if (order == null) return;
            StandingOrders.Add(order);
            StandingOrdersRevision++;
        }

        /// <summary>Removes <paramref name="order"/>, marking the list changed if it was there.</summary>
        public bool RemoveStandingOrder(StandingOrder order)
        {
            if (order == null || !StandingOrders.Remove(order)) return false;
            StandingOrdersRevision++;
            return true;
        }

        /// <summary>Re-ranks <paramref name="order"/> (see <see cref="StandingOrder.Priority"/>) and
        /// marks the list changed. A rank set to what it already was still counts: the player asked
        /// for it, and a no-op that skipped the bump would leave an open window showing the old
        /// value until something else moved.</summary>
        public void SetStandingOrderPriority(StandingOrder order, int rank)
        {
            if (order == null) return;
            order.Priority = rank;
            StandingOrdersRevision++;
        }

        /// <summary>Pauses or resumes <paramref name="order"/> and marks the list changed.</summary>
        public void SetStandingOrderPaused(StandingOrder order, bool paused)
        {
            if (order == null) return;
            order.Paused = paused;
            StandingOrdersRevision++;
        }

        /// <summary>Mints the next never-reused <see cref="StandingOrder.Id"/> for this faction.</summary>
        public string MintStandingOrderId()
        {
            StandingOrderSeq++;
            return "SO-" + StandingOrderSeq.ToString("D3");
        }

        /// <summary>This faction's standing orders in evaluation order: highest
        /// <see cref="StandingOrder.Priority"/> first, oldest first within a rank, id as the final
        /// tie-break. A total order with no dependence on list insertion order, so the same roster
        /// evaluates identically on the host and in the sim, and a resumed operations cycle sees the
        /// same sequence it started with. Optionally filtered to one <paramref name="kind"/>.</summary>
        public List<StandingOrder> StandingOrdersByRank(StandingOrderKind? kind = null)
        {
            var result = new List<StandingOrder>(StandingOrders.Count);
            foreach (StandingOrder o in StandingOrders)
                if (kind == null || o.Kind == kind.Value) result.Add(o);
            result.Sort(CompareStandingOrders);
            return result;
        }

        private static int CompareStandingOrders(StandingOrder a, StandingOrder b)
        {
            int byRank = b.Priority.CompareTo(a.Priority);
            if (byRank != 0) return byRank;
            int byAge = a.CreatedUtc.CompareTo(b.CreatedUtc);
            if (byAge != 0) return byAge;
            return string.CompareOrdinal(a.Id, b.Id);
        }

        /// <summary>The standing order with <paramref name="id"/> (case-insensitive, since a player
        /// types it), or null.</summary>
        public StandingOrder FindStandingOrder(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (StandingOrder o in StandingOrders)
                if (string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)) return o;
            return null;
        }
        public DateTime CentralBankSupportCooldownUntilUtc; // > UtcNow while central-bank rescue lending is cooling down
        public DateTime CentralBankGrantCooldownUntilUtc;   // > UtcNow while central-bank grants are cooling down
        public long CentralBankSupportDebt;   // rescue-loan principal still outstanding; tracked separately from legacy punitive debt
        /// <summary>EMA of real, non-debt income (central-bank grants, asset-purchase proceeds, trade
        /// settlement proceeds, raid loot) sampled once per AccrueIncome pass. Gates new support-loan
        /// issuance against <see cref="FactionTuning.CentralBankDebtServiceIncomeCapFraction"/>, and
        /// anchors the per-manager treasury spend budgets (<see cref="FactionTuning.
        /// ReinforceMaxIncomeFractionPerWindow"/> and siblings) ÔÇö in both cases so a faction can't
        /// keep spending/borrowing faster than it actually earns. Starts at 0; loan principal never
        /// contributes to it (would be circular).</summary>
        public double CentralBankRecentIncome;

        /// <summary>Strategic campaign focus set by the brain: the single owned node with the highest
        /// perceived threat. FleetManager reads this to bias (not gate — Patrol fires on raw threat
        /// regardless) Reinforce/Patrol scoring toward <see cref="CampaignFocusRegionMembers"/>. The
        /// brain regenerates campaign focus every tick; the 1h cooldown prevents identical focus from
        /// being set twice.</summary>
        public string CampaignFocusRegion;    // node ID the faction is concentrating on
        public double CampaignFocusPriority;  // threat level of the campaign (0..1)
        public DateTime CampaignSetAtUtc;     // time the current campaign focus was set
        /// <summary>The actual "region": <see cref="CampaignFocusRegion"/> plus every owned node within
        /// <see cref="FactionTuning.CampaignRegionTraverseDepth"/> proximity hops (see
        /// <see cref="WorldModel.ComputeCampaignRegionMembers"/>). Computed once when the campaign is
        /// set (<c>FactionRuntime.Enact</c>'s <c>ActionKind.Campaign</c> case), not every tick. Always
        /// includes the center itself. Null/empty when no campaign is active.</summary>
        public readonly HashSet<string> CampaignFocusRegionMembers = new HashSet<string>();
        /// <summary>Persisted defensive campaign focuses. This collection is the multi-focus replacement
        /// for the legacy singleton fields above; the singleton remains temporarily so older callers can
        /// be migrated one seam at a time.</summary>
        public readonly List<CampaignFocus> CampaignFoci = new List<CampaignFocus>();
        /// <summary>Per-owned-node bonus from the brain's <c>PerceivedThreat</c> formula (the
        /// personality-driven xenophobia/foreign-presence term, not raw <see cref="SpatialNode.Threat"/>
        /// — FleetManager already has that). Recomputed fresh every <c>Think()</c> cycle by
        /// <c>FactionBrain.ComputePerceivedThreatBonus</c>, same as <see cref="ReinforcePriority"/>/
        /// <see cref="TradePriority"/> — brain writes, FleetManager reads, never persisted.</summary>
        public readonly Dictionary<string, double> PerceivedThreatBonusByNode = new Dictionary<string, double>();

        /// <summary>General "we want a stronger fleet" floor, 0..1. Raised by either firing mode of
        /// ActionKind.Muster (Security-driven untargeted, or Rivalry-driven targeted — a targeted
        /// build-up still counts toward general strength). Consumed by ShipyardManager.SelectPrefabBom
        /// (prefab tier bias) and FactionRuntime.Enact's ConvoySize computation.</summary>
        public double MilitaryPosture;
        /// <summary>Rival this faction is actively trying to out-build, set only by the Rivalry-driven
        /// (targeted) Muster firing mode. Null when the faction has no active targeted build-up —
        /// MilitaryPosture alone still applies in that case.</summary>
        public string MusterFocusTag;
        public double MusterFocusPriority;    // 0..1, urgency of the targeted build-up
        public DateTime MusterSetAtUtc;       // time the current Muster focus was set
        /// <summary>When the last terror campaign (<see cref="CampaignObjectiveKind.Terrorise"/>)
        /// started — the cadence clock ("from time to time"). Persisted; MinValue = never.</summary>
        public DateTime LastTerrorCampaignUtc;
        /// <summary>The region key the last terror campaign targeted, so the next one goes
        /// somewhere else. Persisted; null = none yet.</summary>
        public string LastTerrorRegionKey;
        /// <summary>How many steps the antagonist has escalated, 0..<see cref="FactionTuning.TerrorEscalationCap"/>.
        /// Raised by one when a campaign is repelled, lowered by one when a campaign succeeds and by
        /// one per <see cref="FactionTuning.TerrorEscalationDecay"/> left alone. Multiplies the next
        /// campaign's DPS budget (<see cref="CampaignObjective.WantedCombatDps"/>). Persisted.</summary>
        public int TerrorEscalation;
        /// <summary>The recorded voice this faction's stations speak in (<see cref="ControllerVoice"/>),
        /// 0..<see cref="ControllerVoice.Speakers"/>-1, or -1 before one is assigned. Persisted, so a
        /// faction keeps its voice across restarts and roster changes.</summary>
        public int ControllerSpeaker = -1;
        /// <summary>When <see cref="TerrorEscalation"/> last changed, so idle decay is timed from the
        /// last change rather than from boot. Persisted; MinValue = never.</summary>
        public DateTime TerrorEscalationUtc;
        /// <summary>The last campaign was repelled, so the next one goes back to
        /// <see cref="LastTerrorRegionKey"/> instead of rotating away. One campaign's worth: cleared
        /// when the next starts. Persisted.</summary>
        public bool TerrorRevenge;
        /// <summary>The strongest opposition (summed DPS) the last campaign's raids actually met, from
        /// this faction's own <see cref="Fights"/>. What the faction learned about the region's
        /// defenders, as opposed to what the census guessed; the next budget takes whichever is
        /// larger. Persisted; 0 = unopposed or no campaign yet.</summary>
        public long TerrorMetDps;
        /// <summary>Combat DPS the yard keeps stocked BETWEEN campaigns while <see cref="TerrorEscalation"/>
        /// is above zero — a faction that keeps losing keeps a bigger fleet on hand instead of
        /// rebuilding toward the peacetime floor. 0 at escalation 0. Persisted.</summary>
        public long TerrorStandingDps;
        /// <summary>This faction's recent fights, both sides of each, newest last; bounded by
        /// <see cref="FactionTuning.FightHistoryCap"/>. Written by <see cref="FightLedger"/> at
        /// every Raid/Retaliate/Conquer terminal; the record a faction learns from. Persisted.</summary>
        public readonly List<FightRecord> Fights = new List<FightRecord>();
        /// <summary>Which tactic has worked against whom (<see cref="Factions.Core.TacticMemory"/>).
        /// Read and written while tactic learning is switched on, and by bench scenarios. Persisted,
        /// omitted when empty.</summary>
        public readonly TacticMemory TacticMemory = new TacticMemory();

        /// <summary>Wall-clock time at which this faction's next combined economy+brain tick is due.
        /// MinValue (default) = immediately due. Set by FactionRuntime; not persisted.</summary>
        public DateTime NextTickRealUtc;

        public double RelationTo(string actorId)
        {
            double v;
            return Relations.TryGetValue(actorId, out v) ? v : 0.0; // default: neutral
        }

        /// <summary>The event-driven ledger toward <paramref name="actorId"/>, falling back to the
        /// current standing when no ledger entry exists yet. The fallback is the migration: a faction
        /// loaded from a world saved before <see cref="EarnedRelations"/> existed keeps the standing
        /// it had, rather than resetting to neutral the first time drift runs.</summary>
        public double EarnedRelationTo(string actorId)
        {
            double v;
            return EarnedRelations.TryGetValue(actorId ?? "", out v) ? v : RelationTo(actorId);
        }

        /// <summary>The conduct ledger's own value, and <b>0 when there is no entry</b> — deliberately
        /// NOT <see cref="EarnedRelationTo"/>, whose absent-entry fallback to the current standing is
        /// right for reading a standing forward and catastrophic for computing a drift target from it:
        /// a target of <c>ledger + bias</c> built on that fallback reads back the value it just wrote
        /// and ratchets by <c>bias</c> every pass until it hits the clamp.
        /// <para>0 is the honest answer for a pair nothing has happened between, and for a world saved
        /// before the ledger existed — its <see cref="Relations"/> value is a chased pressure number,
        /// not a record of conduct, and copying it in would bake a geometry artifact into the ledger
        /// permanently.</para></summary>
        public double EarnedLedgerTo(string actorId)
        {
            double v;
            return EarnedRelations.TryGetValue(actorId ?? "", out v) ? v : 0.0;
        }

        public double FatigueTo(string actorId)
        {
            double v;
            return Fatigue.TryGetValue(actorId, out v) ? v : 0.0;   // default: not tired of them
        }
    }

    public enum CampaignObjectiveKind
    {
        /// <summary>Hold what is already held. The original, and for a long time the only one.</summary>
        DefendRegion = 0,

        /// <summary>Take a region the faction does not hold. **The operational half the defensive
        /// foundation left room for**, and what a colonisation beachhead exists to pursue: when the
        /// richest source of a needed material has no room, the faction stages next to it and this
        /// objective is the reason it keeps pushing rather than settling for the outpost
        /// (FAC-106).</summary>
        SeizeRegion = 1,

        /// <summary>Raid a region the faction will never hold. The antagonist's shape (FAC-104,
        /// FAC-268): an implacable faction that from time to time picks a region of space and
        /// enacts a terror campaign on it — raids only, never conquest — and, unlike the other two
        /// kinds, carries a <b>fleet requirement</b> (<see cref="CampaignObjective.WantedCombatHulls"/>)
        /// so the yard builds toward it before the first raid can enact.</summary>
        Terrorise = 2
    }

    /// <summary>Why an offensive campaign objective exists. Recorded on the objective, persisted by
    /// name beside <see cref="CampaignObjective.Kind"/>, and reported in the campaign's log lines —
    /// the same region can be worth attacking for very different reasons, and only the reason tells
    /// a reader whether a raid wave is appetite, a debt, an answer, or the antagonist.
    ///
    /// <para><b>Retaliation is a cause, not a kind.</b> A retaliation is a raid campaign against the
    /// attacker's region; the fleets it dispatches keep <see cref="ActionKind.Retaliate"/>, so every
    /// magnitude, <see cref="FightRecord"/> and resolver path is unchanged.</para></summary>
    public enum CampaignCause
    {
        /// <summary>Appetite: a weak, valuable neighbour, taken because the faction is a raider.</summary>
        Predation = 0,

        /// <summary>A debt: the owner is a known treaty-breaker (<see cref="FactionState.Grudges"/>).</summary>
        Grudge = 1,

        /// <summary>An answer: the owner attacked us inside the war-fatigue window, or stands behind
        /// the threat on one of our own nodes.</summary>
        Retaliation = 2,

        /// <summary>The antagonist's own shape — an <see cref="Traits.Implacable"/> faction picking a
        /// region to terrorise on a cadence of its own (<see cref="TerrorCampaigns"/>).</summary>
        Terror = 3,

        /// <summary>An order: a player told their own faction to take or hit this place
        /// (<see cref="CampaignFocus.PlayerIssued"/>). A value of its own rather than a reuse of
        /// <see cref="Predation"/> because every other cause is a reason the brain <i>found</i>, and
        /// the brain never ran for the faction carrying this one — a log line reading
        /// <c>cause=Predation</c> on a player faction would name an appetite nothing computed.</summary>
        Directive = 4
    }

    /// <summary>Persisted strategic intent attached to a campaign focus.</summary>
    public sealed class CampaignObjective
    {
        public CampaignObjectiveKind Kind;

        /// <summary>The node this objective names. For <see cref="CampaignObjectiveKind.DefendRegion"/>
        /// the focus centre. For <see cref="CampaignObjectiveKind.SeizeRegion"/> it makes the objective
        /// <b>node-scoped</b>: only that node advances it, whatever region it stands in — the
        /// beachhead's last step, and the shape a player directive naming one node takes. Null on a
        /// region-scoped objective, which names its region through the three fields below.</summary>
        public string TargetNodeId;

        /// <summary>Why this objective exists. Meaningful for the offensive kinds
        /// (<see cref="CampaignObjectiveKind.Terrorise"/>, <see cref="CampaignObjectiveKind.SeizeRegion"/>);
        /// <see cref="CampaignCause.Predation"/> is the zero value and is what a defensive objective
        /// carries by default.</summary>
        public CampaignCause Cause;

        /// <summary>Nodes worth attacking under this objective, best first — the brain's answer to
        /// "which of them, in what order", rebuilt from scratch every <c>Think()</c> and read by
        /// <c>FleetManager</c>'s offensive block. The same brain-writes / manager-reads contract as
        /// <see cref="FactionState.ReinforcePriority"/> and <see cref="FactionState.TradePriority"/>.
        ///
        /// <para><b>Not persisted, on purpose.</b> It is a cache of a ranking over a world that has
        /// moved by the time a save is read back, and it costs the brain one pass to rebuild. An
        /// empty list after a load simply means the manager dispatches nothing under this objective
        /// until the faction's next think.</para></summary>
        public readonly List<string> Candidates = new List<string>();

        /// <summary>When this objective last dispatched a fleet. The tempo clock
        /// (<see cref="FactionTuning.OffensiveTempo"/>) is measured from it.
        ///
        /// <para><b>Persisted</b>, unlike <see cref="Candidates"/>: a cooldown stamp that does not
        /// survive a restart is not a cooldown — every boot would fire a fresh wave from every
        /// standing objective at once.</para></summary>
        public DateTime LastDispatchUtc;

        /// <summary>For <see cref="CampaignObjectiveKind.SeizeRegion"/>: the region wanted, as a
        /// <see cref="PlacementRegionKind"/> ordinal. Null for the defensive kind, which names a node.
        ///
        /// <para>A region rather than a node because the objective exists precisely where the faction
        /// holds nothing — a body whose surface was full is a place it has no node to name. The nodes
        /// worth taking there belong to somebody else and change as the campaign runs.</para></summary>
        public PlacementRegionKind? TargetRegionKind;

        /// <summary>For <see cref="CampaignObjectiveKind.SeizeRegion"/>: the region's key — a planet's
        /// **generator subtype** (<c>Agni</c>) or a feature's name. Same keying as
        /// <see cref="EstateRegion"/> and <see cref="SurfaceNodePolicy"/>, and for the same reason:
        /// <c>PlanetRecord.Name</c> reads <c>"Planet"</c> for every body at runtime.
        /// <para>Authoring and reporting identity. Matching uses <see cref="TargetPlanetId"/>.</para></summary>
        public string TargetRegionKey;

        /// <summary>The body this objective resolved to — <see cref="PlanetRecord.Id"/>, which is what
        /// <see cref="SpatialNode.PlanetId"/> already carries.
        ///
        /// <para>Resolved **once**, where a planet catalogue is at hand, rather than looked up while
        /// scoring: the brain tests every node in the graph against every objective on every pass, and
        /// a catalogue scan inside that loop is the shape this codebase has had to undo before. It is
        /// also why <see cref="SeizeObjectives.Advances"/> needs no catalogue at all.</para>
        ///
        /// <para>Does not survive a world regenerate — an SE <c>EntityId</c> names this world's body
        /// only, the same caveat a feature's <c>AnchorPlanetId</c> carries.</para></summary>
        public string TargetPlanetId;

        /// <summary>For <see cref="CampaignObjectiveKind.Terrorise"/>: combat hulls the objective
        /// wants stocked network-wide while it stands. Read by <c>ShipyardManager</c> on top of the
        /// posture-derived target — the one fleet channel that is not posture-derived, which is
        /// what a faction that never Musters needs (FAC-268). 0 for the other kinds.</summary>
        public int WantedCombatHulls;

        /// <summary>For <see cref="CampaignObjectiveKind.Terrorise"/>: summed combat DPS the
        /// objective wants stocked network-wide while it stands — the requirement's real unit. A
        /// hull count treats a 500-DPS drone and a 48,000-DPS cruiser as the same "1"; live on
        /// v0.177.0 that read as eleven hulls against a target of four while every raid squadron
        /// was one drone. Sized by <see cref="TerrorCampaigns.Budget"/> from a census of the target
        /// region's defenders and the opposition met last time, times the escalation multiplier.
        /// <see cref="WantedCombatHulls"/> stays as the floor. 0 for the other kinds.</summary>
        public long WantedCombatDps;

        /// <summary>What came back from the last attempt under this objective, as distinct from
        /// <see cref="LastDispatchUtc"/>, which only says one was made. A refusal stamps the tempo
        /// clock exactly as a dispatch does, so without this a campaign that refused thirteen times
        /// running is indistinguishable from one striking once a minute.</summary>
        public CampaignOutcome LastOutcome;

        /// <summary>When <see cref="LastOutcome"/> was decided. Later than
        /// <see cref="LastDispatchUtc"/> for a fleet that flew and then lost; equal to it for a
        /// refusal, which is decided where it is attempted.</summary>
        public DateTime LastOutcomeUtc;

        /// <summary>The duel turn that decided the outcome (<see cref="CombatStrength.DuelBracket"/>):
        /// the range in metres at which the defence most out-reached the attack, and both sides'
        /// output there. Chosen over the DPS totals because reach is what the duel resolves on — a
        /// squadron outgunned 6k to 74k at 2000 m and even at point blank loses on the first turn,
        /// and only the bracket says which hulls would have changed that. Zero for
        /// <see cref="CampaignOutcome.Landed"/>, which is not decided at a bracket.</summary>
        public double LastOutcomeRangeM;
        public double LastOutcomeOwnDps;
        public double LastOutcomeOpposedDps;

        /// <summary>Hulls the last attempt committed and lost. Both zero for a refusal, which spends
        /// none.</summary>
        public int LastOutcomeHullsLost;
        public int LastOutcomeHullsTotal;

        /// <summary><see cref="PendingFleetAction.FleetId"/> of the fleet this objective last
        /// dispatched, and the only link from a fleet meeting its end to the row that sent it: a
        /// fleet carries no objective identity, and matching on the target node instead would credit
        /// the outcome to every objective ranking that node.
        ///
        /// <para><b>Persisted</b>, for the same reason <see cref="LastDispatchUtc"/> is: fleets
        /// survive a restart, and an objective that forgot which one it sent would take a loss as
        /// having no outcome at all.</para></summary>
        public string LastDispatchFleetId;
    }

    /// <summary>What became of a campaign objective's last attempt. Ordinals cross the wire as
    /// <c>WireCampaign.LastOutcome</c> and are persisted by name.</summary>
    public enum CampaignOutcome
    {
        /// <summary>Nothing has been attempted, or nothing has come back yet.</summary>
        None = 0,
        /// <summary>The dispatch was refused before the fleet existed: what could be assembled would
        /// not have survived what is standing at the target.</summary>
        Refused = 1,
        /// <summary>The fleet flew and was destroyed before it landed anything.</summary>
        Intercepted = 2,
        /// <summary>The fleet reached the target and took effect, whatever it cost.</summary>
        Landed = 3,
    }

    /// <summary>Persisted defensive strategic focus. The active center node is its stable identity in
    /// this defensive-only foundation; a future operational layer may add richer objective identity.</summary>
    public sealed class CampaignFocus
    {
        public string Id;
        public string CenterNodeId;
        public double Priority;
        public DateTime SetAtUtc;
        public DateTime ExpiresAtUtc;
        public readonly HashSet<string> RegionMembers = new HashSet<string>();
        public readonly List<CampaignObjective> Objectives = new List<CampaignObjective>();

        /// <summary>A player placed this focus by hand rather than a brain nominating it
        /// (<c>docs/campaigns.md</c> → A player faction). It is the gate <c>FleetManager</c>'s
        /// offensive block reads for a faction whose brain is switched off
        /// (<see cref="FactionLifecycle.IsAutonomyDisabled"/>): such a faction dispatches under a
        /// focus carrying this flag and under nothing else.
        ///
        /// <para><b>Persisted</b>, and omitted from the JSON when false — a directive that did not
        /// survive a restart would leave the objective standing with nothing willing to fight it.
        /// Absent reads as false, which is what every focus written before this existed is.</para></summary>
        public bool PlayerIssued;
    }

    /// <summary>Global tuning knobs (costs / thresholds / budgets).</summary>
    public sealed class FactionTuning
    {
        /// <summary>Version of the tuning defaults this data was loaded against. 0 = unversioned (stale). Increments on every defaults change.</summary>
        public int TuneVersion = 29;

        public long ComfortTreasury; // treasury at/above which Wealth pressure ~ 0 (legacy; use ComfortLedger for new Sense logic)
        public long ComfortLedger;   // ledger (treasury + inventory market value) at/above which Wealth pressure ~ 0
        public double DebtRatio;        // max debt/ledger ratio before auto-borrow stops (e.g. 0.4)
        public double DebtInterestRate; // per-economy-quantum compound interest on outstanding debt
        public long LoanAmount;      // legacy punitive auto-borrow ceiling (kept for compatibility)
        public long ReserveTreasury;  // spending below this is discouraged
        public long ExpandCost;
        /// <summary>What a player faction pays to create a node of its own, charged to an SE wallet
        /// rather than to <see cref="ExpandCost"/>'s private ledger — a player faction is registered
        /// with no seed treasury, so a ledger-priced Expand would be a loop with no entrance
        /// (docs/economy-territory.md → Expanding). Carries an inline default because a missing JSON
        /// key must not make the only irreversible player purchase in the game free; purely additive
        /// vs the current <c>TuneVersion</c>, so no bump.
        /// <para>Repricing this depends on <see cref="SuppressVanillaFactionBalanceRecovery"/> still
        /// being true: with vanilla's recovery running, a station-owning faction's wallet is pulled
        /// back toward its target every economy tick and the faction tier stops being a cost.</para></summary>
        public long PlayerExpandCost = 1000000;
        public long ClaimCost;
        public long ReinforceCost;
        public double ReinforceStep;  // hold gained per Reinforce action
        // Minimum share of ReinforceStep a reinforce must actually deliver to be worth its flat
        // cost. <=0 reinforces anything below full hold, which is what shipped before FAC-195.
        public double ReinforceMinFillFraction = 0.3;
        /// <summary>How long a Reinforce holds at the node it came to reinforce before its hold
        /// gain lands (<see cref="FleetStopKind.Engage"/>) -- the same window a raid holds station
        /// for. The effect fires when that leg resolves, so a reinforce is contestable for its whole
        /// length. Inline default: a missing JSON key must not collapse the window to zero.</summary>
        public double ReinforceDwellSeconds = 300.0;
        /// <summary>Un-resolved Reinforce fleets one faction may have in the air at once. An estate
        /// with dozens of below-threshold nodes would otherwise commit its whole military stock in a
        /// single tick, since every one of them clears the same gates on the same pass.</summary>
        public int ReinforceMaxConcurrent = 3;
        /// <summary>How long a node stays off the Reinforce ranking after one has been sent to it --
        /// <see cref="PatrolNodeCooldown"/>'s counterpart, and for the same reason: the manager ticks
        /// far faster than a fleet flies.</summary>
        public TimeSpan ReinforceNodeCooldown = TimeSpan.FromSeconds(900);
        public double PresenceReinforceTargetSeconds = 1800.0; // ~30 min of continuous player presence at an owned node = one Reinforce action (inline default: missing JSON key must not zero it)
        public int PresenceReinforceMaxHops = 3; // graph-hops out from the node a member stands at that also accrue presence-as-Reinforce; 0 = the presence node alone (inline default: missing JSON key must not zero it)
        public double PresenceReinforceHopFalloff = 0.5; // accrual-rate multiplier per hop (hop 1 = 0.5x, hop 2 = 0.25x, hop 3 = 0.125x); purely additive vs the current TuneVersion, so no bump needed
        public int RecentEventCap = 60; // per-faction FactionState.RecentEvents ring size -- what the Events tab can show and what a brain file carries (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public int StandingOrderCap = 32;  // per-faction FactionState.StandingOrders list size -- a player-authored list, so the cap is a bound on a persisted collection, not a gameplay balance knob (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public int FleetDoctrineCap = 32;  // per-faction FactionState.FleetDoctrine list size -- same reasoning as StandingOrderCap: a bound on a player-authored persisted collection, not a balance knob (inline default: missing JSON key must not zero it; a NEW field, so no TuneVersion bump)
        public int PlayerShelfCap = 32;  // per-faction FactionState.Shelf list size -- same reasoning as FleetDoctrineCap: a bound on a player-authored persisted collection, not a balance knob (inline default: missing JSON key must not zero it; a NEW field, so no TuneVersion bump)
        public int FeatureRockGeneratorVersion = 4; // SE asteroid generator VERSION passed to CreateAsteroidShape's 4th argument -- NOT an index. Pinned rather than read from MySession.Static.Settings.VoxelGeneratorVersion so a world-settings change cannot reshape rock that is regenerated later, and so a client rebuilding the same rock uses the same number. Must be > 2: MyCompositeShapes' constructor returns early below that, skipping the material lists entirely (inline default: missing JSON key must not zero it -- 0 selects a legacy generator with no material control; purely additive vs the current TuneVersion, so no bump needed)
        public int MaxFeatureRockSpawnsPerTick = 8; // per-tick cap on newly spawned feature-rock OBJECTS (not cells) in the lazy view ring; the time-critical sync ring gets 4x this. Counted per object because at belt densities one cell is several synchronous AddVoxelMap calls, and an unbounded burst is the documented voxel-stall cause (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockCellSizeKm = 5.0; // side of the cubic cell the rock grid spawns and despawns in units of. Sized against the world's ViewDistance (15 km): a 15 km ball spans ~113 of these, so the per-frame walk stays small while a cell is still coarse enough that one holds several objects at typical density (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockViewKm = 0.0; // radius around each player within which feature rock is PLANNED and named to linked clients, km. 0 = SE's ViewDistance. Rock the server actually spawns is the smaller set inside the rock shells (FeatureRockShell*) while the 'rock-shell' switch is on (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockShellSideKm = 3.0; // rock shell radius at rest and lateral semi-axis when moving, km. Sized to the farthest server-side voxel query from the body that asks: vanilla autopilot avoidance (stopping distance capped at 3 km), the solar-shadow ray (3 km), mod turret targeting (2.8 km), AI Offensive and target lock (2.5 km) (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockShellBackKm = 3.0; // rock shell semi-axis behind a body moving at or above FeatureRockShellMinSpeedMs, km (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockShellLeadSeconds = 10.0; // seconds of travel added ahead of FeatureRockShellSideKm for a body moving at or above FeatureRockShellMinSpeedMs: 13 km ahead at 1,000 m/s (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockShellMinSpeedMs = 100.0; // below this speed a rock shell is a sphere of FeatureRockShellSideKm (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockShellHysteresis = 1.15; // a rock spawns inside its shell shrunk by this factor and is closed only outside the full shell, so a body on the boundary does not cycle it (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public int FeatureRockShellEvalFrames = 10; // frames between rock-shell passes: the anchors are re-read and spawned rock outside every shell is closed (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockShellAnchorRangeKm = 15.0; // a moving grid or character within this of a player (or phantom) carries its own rock shell, so NPC hulls flying on vanilla autopilot avoidance have rock to avoid (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double StationDespawnGraceSeconds = 0.0; // a materialised station closes this long after no observer is within ObservationRadiusKm + 1 km (the fleet-hull despawn radius). Was a fixed 300 s (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double StationQuietKm = 5.0; // a materialised station with no player or phantom within this distance stops receiving entity updates (switch 'factions feature station-quiet'); collision, damage and replication are untouched. Farther than every turret's targeting range (mod max 2.8 km) (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double StationQuietWakeCombatKm = 3.0; // a quiet station also wakes while a moving grid other than its own is within this distance, so its turrets answer a raid no player is watching (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double StationQuietHysteresisKm = 0.5; // a station wakes inside StationQuietKm and quiets again only past StationQuietKm + this (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockSpawnBudgetMs = 1.0; // per-frame time budget for spawning feature rock, ms; at least one rock is spawned when any is queued, the rest wait for the next frames. One AddVoxelMap measured ~0.4 ms, so a pass's rock arrives over a few frames instead of as one 2-5 ms frame (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public int FeatureRockPlanCellsPerTick = 16; // per-tick cap on feature-rock cells PLANNED (objects listed, nothing spawned) around players (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockPlacementRadiusM = 1000.0; // before a free-space probe (space respawn, fleet spawn) rock is spawned this far around the candidate point, so the probe sees rock outside every shell (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public int FeatureRockPlacementMaxSpawns = 64; // cap on rocks spawned synchronously for one placement probe (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureRockPlacementHoldSeconds = 30.0; // how long rock spawned for a placement probe is kept as if a body stood there, until what was placed carries its own shell (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public int FeatureRockReclaimPerTick = 32; // voxels examined per tick by the once-per-session sweep that reclaims feature rock reloaded from the save. Rock that survived a restart belongs to no live record, so nothing else can ever close it; the sweep runs to completion before the first spawn of the session and then costs nothing. Budgeted because Close() is cheap but AnythingStandingOn's pruning-structure query is not, and this runs on the update tick (inline default: missing JSON key must not zero it -- 0 disables reclamation entirely; purely additive vs the current TuneVersion, so no bump needed)
        public double FeatureMaxDensityPer1000Km3 = 8.0; // objects per 1000 km3 that a WorldFeature at Density 1.0 asks for -- the ceiling a feature's 0..1 Density scales against, so a world retunes its whole rock population from one key. 8.0 is roughly one object per 5 km of space (inline default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so no bump needed)
        public int NexusAuthorityServerId = 0; // Nexus server ID allowed to run the simulation; 0 = unset. On a cluster every server shares one banking/faction/reputation state, so exactly one may simulate -- 0 defers all of them rather than guessing (inline default: missing JSON key must not zero it, 0 IS the intended default; purely additive vs the current TuneVersion, so no bump needed)
        public string DataDirectory = ""; // where brains/indexes/catalogues live; empty = this Torch instance's own plugin storage. On a Nexus cluster the instances share one data folder, so point all of them at it. tuning.json itself always stays per-instance -- it is the file naming this path. Relative values resolve against the instance dir (inline default: missing JSON key must not null it, "" IS the intended default; purely additive vs the current TuneVersion, so no bump needed)
        /// <summary>Whether reinforcing a node at <paramref name="holdStrength"/> buys enough to be
        /// worth the flat <see cref="ReinforceCost"/>.
        ///
        /// <para>A reinforce adds <see cref="ReinforceStep"/> and clamps at 1.0, so what it actually
        /// buys is the <i>deficit</i>, not the step — at hold 0.99 a full 50,000 buys 0.01. The gate
        /// is therefore a share of the step rather than a second absolute threshold: retuning the
        /// step moves the trigger with it, instead of leaving two constants to drift apart.</para>
        ///
        /// <para>At the shipped values (step 0.7, fraction 0.3) this triggers at hold ≤ 0.79, just
        /// under <see cref="HoldMaintenanceThreshold"/> — deliberately, because
        /// <c>ExpansionHealthScale</c> counts nodes below that threshold and a node has to dip under
        /// it briefly to be topped up at all. Raising the fraction buys more hold per credit and
        /// leaves the estate reading less healthy for longer; that is the trade the knob exposes.</para>
        ///
        /// <para><see cref="ReinforceMinFillFraction"/> ≤ 0 restores "reinforce anything below
        /// 1.0".</para></summary>
        public bool ReinforceBuysEnough(double holdStrength)
        {
            if (holdStrength >= 1.0) return false;
            if (ReinforceMinFillFraction <= 0) return true;
            return (1.0 - holdStrength) >= ReinforceStep * ReinforceMinFillFraction;
        }

        public double HoldMaintenanceThreshold = 0.8; // FleetManager reinforces owned nodes whose hold falls below this (inline default: missing JSON key must not zero it)
        public double ClaimHoldHealthWeight = 0.75; // how strongly an under-maintained estate damps Claim/Expand candidate quality: scale = 1 - weight * (share of owned nodes below HoldMaintenanceThreshold); <=0 = no damping
        public double ContractRewardPremiumMax = 0.5; // max fractional bonus a player-completable maintenance contract's reward gets scaled by, at max urgency (e.g. 0.5 = up to +50% over the mirrored action cost)
        public double RepairGridMinDistanceM = 300;  // inner cutout for the Reinforce-via-Repair contract's spawned wreck, meters from the station
        public double RepairGridMaxDistanceM = 2000; // outer radius for the same, meters from the station
        public TimeSpan PvEBountyTimeout = TimeSpan.FromHours(3); // not consumed by Core -- read by FactionContractStrategy to set the Guard-via-PvEBounty SE contract's own RemainingTimeInS; vanilla's own contract lifecycle owns cleanup of an abandoned/unaccepted offer, no Core-side pause or timeout involved
        // Player Haul contract economics (docs/contracts.md) -- not consumed by Core, read by
        // FactionContractStrategy when generating the vanilla Hauling/"Courier" contract flavor.
        public long HaulContractRewardPerKm = 50; // flat credits added to the reward per km of source->need distance, on top of the same basePrice x scarcity x amount term Acquisition already uses
        public double HaulContractTimeMultiplier = 3.0; // RemainingTimeInS = TransitTimeMins(distKm) * 60 * this -- a player isn't as direct as an abstract NPC convoy
        public double HaulContractMinTimeoutMinutes = 15; // floor so a short hop still gives a player reasonable time
        // DemandHistory / neglect-clock tuning (docs/material-vectors.md's DemandHistory addendum,
        // Slice F). All three purely additive vs the current TuneVersion -- no bump needed (LoadTuning
        // only overwrites JSON-present keys; an old tuning.json lacking these simply never touches them).
        public TimeSpan HaulNeglectThreshold;         // default set in tuning_defaults.json (600s = 10 min); <= 0 disables the hot/cold override entirely
        public double DemandHistoryEmaPeriods = 30.0; // memory length, in OperationsManager.Tick passes -- mirrors CentralBankIncomeEmaPeriods's shape

        /// <summary>Percentile of observed job size a role's hull requirement is read at
        /// (docs/fleet-fitness-plan.md). Deliberately high: a median sizes for the typical job and
        /// leaves the tail unserved, and the tail is what a hull has to be bought for. 0 disables the
        /// requirement entirely, which is the pre-fleet-fitness behaviour.
        /// <para><b>It sizes for the tail only when set above the tail's share.</b> With large jobs at
        /// exactly 10% of the sample, p90 lands on the last small job and buys the drone — which is
        /// arithmetically correct and still probably not what a reader expects. Raise it if the
        /// requirement reads low against jobs that are visibly present.</para>
        /// <para>Purely additive vs the current TuneVersion -- a tuning.json lacking the key keeps
        /// this inline default, so no bump.</para></summary>
        public double FleetRequirementPercentile = 0.9;

        /// <summary>Memory length of a role requirement, in Operations passes. Longer than
        /// <see cref="DemandHistoryEmaPeriods"/> on purpose: a hull is owned for days, so sizing it
        /// from the last few minutes of traffic buys for a moment that has already passed.</summary>
        public double FleetRequirementEmaPeriods = 300.0;

        /// <summary>Whether a warm role requirement is allowed to raise the cargo floor a new build is
        /// sized against (docs/fleet-fitness-plan.md, slice 1). False keeps recording and the
        /// <c>FLEET_FITNESS</c> counterfactual while changing no build — the slice-0 behaviour — so
        /// this can be backed out live from tuning.json without a deploy if the sizing turns out
        /// wrong.
        /// <para>It only ever RAISES the floor: the value used is the larger of the live
        /// per-shipment floor and the requirement, so a build can never be sized smaller than it is
        /// today. And <c>SelectPrefabBom</c> already drops an unreachable floor and ranks the
        /// unfiltered pool, so a requirement no hull can meet degrades instead of blocking.</para>
        /// <para>Purely additive vs the current TuneVersion — a tuning.json lacking the key keeps
        /// this inline default, so no bump.</para></summary>
        public bool FleetRequirementDrivesBuild = true;
        public double HaulMaxBatchFraction = 1.0;     // fraction of a shortfall one vector may carry; 1.0 = no cap (today's behavior). In (0,1) biases toward smaller/more frequent Haul trips for the "busy feel" goal -- live-tune down once there's real telemetry, not guessed at here

        // ---- demand urgency (docs/material-vectors.md, "Ranking a demand") ----------------------
        // How a shortfall is ranked against every other one: it orders the Haul vector list and
        // tilts the Haul-vs-Mine choice. All additive vs the current TuneVersion -- an old
        // tuning.json simply lacks the keys, so these initialisers stand and no gate is needed.

        /// <summary>Sharpens the starvation term. 1.0 = linear in the fraction of the want that is
        /// unmet; &gt;1 makes a nearly-met demand fall away faster so a genuinely empty one leads by
        /// more. 0 removes starvation from the score entirely.</summary>
        public double UrgencyStarvationExponent = 1.5;
        /// <summary>Hours of continuously-unmet age at which the age term reaches its full
        /// <see cref="UrgencyAgeWeight"/>. Measured from <see cref="DemandEntry.FirstPostedUtc"/>,
        /// which survives TTL refresh, so this is real neglect and not time-since-last-post.</summary>
        public double UrgencyAgeHorizonHours = 6.0;
        /// <summary>How much a fully-aged demand is multiplied by: score x (1 + this x age01).
        /// 0 disables the age term.</summary>
        public double UrgencyAgeWeight = 1.0;
        /// <summary>How much a historically-hungry consumer is multiplied by:
        /// score x (1 + this x h/(h + <see cref="UrgencyHistorySaturation"/>)). 0 disables it.
        /// <para>Saturating rather than linear because <see cref="SpatialNode.DemandHistory"/> is an
        /// unbounded EMA in weighted liters -- a linear term would let one high-throughput node's
        /// scale dominate every other component of the score.</para></summary>
        public double UrgencyHistoryWeight = 0.5;
        /// <summary>The history value at which the history term reaches half of
        /// <see cref="UrgencyHistoryWeight"/>. Must be &gt; 0.</summary>
        public double UrgencyHistorySaturation = 500.0;

        /// <summary>Per-<see cref="DemandClass"/> multiplier, indexed by the enum's own value, so a
        /// stalled ship BOM outranks a store top-up of the same size and starvation. Index 0
        /// (<see cref="DemandClass.Unclassified"/>) deliberately matches
        /// <see cref="DemandClass.Catalog"/>: an unstamped poster must read as an ordinary want, not
        /// as zero urgency.
        /// <para>Seven entries since <see cref="DemandClass.Fuel"/> was inserted at index 4, eight
        /// since <see cref="DemandClass.Upkeep"/> was appended at 7; a host still carrying a shorter
        /// array reads <see cref="DemandUrgency.DefaultClassWeight"/> for whatever falls off the end,
        /// which is why the array comes through the TuneVersion gate rather than the field
        /// initialiser.</para></summary>
        public double[] UrgencyClassWeights = new double[] { 1.0, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 2.5 };

        /// <summary>Superseded -- read by nothing since the Mine-vs-Haul comparison it tilted was
        /// retired (FAC-395 slice 2b): an NPC mines on standing extraction orders the planner
        /// authors from the measured flow gap, and Haul no longer competes with Mine for a basket.
        /// Kept only so a stale key in an existing tuning.json is silently ignored.</summary>
        public double MineUrgencyBias = 1.0;

        /// <summary>Claim rank a new <see cref="BuildQueueEntry"/> starts at, by role. Higher wins a
        /// contested component against this station's other builds — the rank is enforced as a floor
        /// (<see cref="SpatialNode.BuildQueueReserved"/>) and as the recipe-input claim order
        /// (<c>ProductionManager.EntriesByPriority</c>), so a hull the network is waiting on is fed
        /// before one it merely wants.
        ///
        /// <para>A role with no row here starts at 0, the historical default. The six rows below are
        /// exactly <c>ShipyardManager.PlayerQueueRoles</c>, which is the whole buildable set.</para>
        ///
        /// <para><b>Shared scale, so the install lever moves with it.</b> These ranks sit on the same
        /// integer scale as <see cref="InstallPriority"/> and <c>StandingOrder.Priority</c>. A build
        /// blocks an install when <c>Priority &gt;= installRank</c>, so raising every build off 0
        /// does not change the all-default outcome (a build already won that tie) — but an install
        /// that wants to outrank a Mine build now needs rank 7, where 1 used to suffice. Kept to
        /// single digits for exactly that reason: a wide spread would push the install lever
        /// somewhere no operator would think to look.</para></summary>
        public Dictionary<string, int> BuildRolePriority = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "Mine",       6 },   // unblocks the ore chain everything else is downstream of
            { "Trade",      5 },
            { "Patrol",     4 },
            { "Raid",       3 },
        };

        /// <summary>Starting claim rank for a build raised for the player store, whatever capability
        /// role it names. Kept below every fleet role for the reason the retired StoreSmall/StoreLarge
        /// rows carried: a shop hull is the one thing nothing else is waiting on. A separate field
        /// rather than a table row because purpose is no longer expressible as a role name.</summary>
        public int StoreShipBuildPriority = 1;

        /// <summary>Starting claim rank for a station refit (<see cref="ShipyardManager.QueueRefit"/>),
        /// which names no fleet role and so has no row in <see cref="BuildRolePriority"/>.
        ///
        /// <para>One above the table's highest row, and that single point is the whole mechanism:
        /// <see cref="SpatialNode.OrderedBuildQueue"/> breaks a rank tie by creation order, so a
        /// refit merely LEVEL with the top row would never take the working slot from a Mine build
        /// that was queued first — while the refusal text promises <c>force</c> does exactly
        /// that. A station rebuild is work the owner asked for by hand and outranks every hull the
        /// fleet wants.</para>
        ///
        /// <para><see cref="HighestBuildRolePriority"/> stays derived from the table alone, so the
        /// develop-priority lever (<see cref="FactionState.InstallRankFor"/>) is unmoved: an install
        /// ranked 1 lifts to 6 + 1 and ties the refit, which a tie resolves in the build's favour
        /// (<see cref="SpatialNode.BuildQueueReserved"/> keeps every entry whose rank is not BELOW
        /// the install's). Rank 2 is what outranks a refit.</para></summary>
        public int StationRefitPriority = 7;

        /// <summary>The largest rank any build starts at. Derived from
        /// <see cref="BuildRolePriority"/> rather than configured beside it, so editing the table
        /// cannot leave the install lever pointing at a number that no longer clears it.
        /// <para>0 for an empty or absent table, which restores the original raw-scale comparison.</para></summary>
        public int HighestBuildRolePriority()
        {
            if (BuildRolePriority == null) return 0;
            int max = 0;
            foreach (KeyValuePair<string, int> kv in BuildRolePriority)
                if (kv.Value > max) max = kv.Value;
            return max;
        }
        /// <summary>Radius in km, measured from a Haul convoy's first pickup node, within which
        /// OTHER vectors matched the same tick for the SAME destination may be folded into that one
        /// convoy as extra <see cref="FleetStopKind.Pickup"/> stops instead of each dispatching a
        /// convoy of its own. 0 disables consolidation entirely. See docs/material-vectors.md's
        /// multi-stop consolidation. (Purely additive vs the current TuneVersion -- an old
        /// tuning.json simply lacks the key, so this initialiser stands and no gate is needed.)</summary>
        public double HaulConsolidationRadiusKm = 500.0;
        /// <summary>Ceiling on how many nodes one Haul convoy collects from, counting the vector's
        /// own source. 1 disables consolidation the same way a 0 radius does. Bounds both the
        /// route-ordering work per dispatch and how long a delivery can be delayed by detours.</summary>
        public int HaulMaxPickupNodes = 4;
        /// <summary>Fraction of a blocked Haul shipment's volume that its Tier-3 <c>Ship:Trade</c>
        /// demand asks one hull to carry. A single vector's quantity is a local-shortage snapshot,
        /// not the faction's sustained shipping rate: a node that has gone without for a while posts
        /// one large catch-up shipment, and sizing a hull 1:1 against it builds for the spike rather
        /// than the flow. 0.2 asks for a hull that moves the shipment in about five trips. The floor
        /// never goes below the nominal 1-litre "must be cargo-capable" sentinel. Distinct from
        /// <see cref="HaulMaxBatchFraction"/>, which caps what one vector may *carry*; this scales
        /// only what a *build* is sized against. (Inline default: a missing JSON key must not zero
        /// it. Purely additive vs the current TuneVersion -- an old tuning.json simply lacks the key,
        /// so the initialiser stands and no version gate is needed.)</summary>
        public double HaulShipDemandCargoShare = 0.2;

        /// <summary>Least fraction of a hull's volume a planned Haul convoy must carry to be worth
        /// flying. A convoy below it is held — its hull returned, a right-sized hull fetched from
        /// elsewhere if one exists (<see cref="HaulSmallHullSearchNodes"/>), otherwise the vector
        /// waits for a convoy to fold it in — UNLESS its demand's urgency reaches
        /// <see cref="HaulHoldUrgency"/>. See docs/issues/FAC-232 and FAC-393.
        /// <para>Measured live 2026-09-25 (v0.338.0, 20,086 solid local-hull hauls): the median flew
        /// at 0.5% of the hold and 78% were under 10%.</para>
        /// <para>0 disables the gate entirely. Purely additive vs the current TuneVersion.</para></summary>
        public double HaulMinLoadFraction = 0.10;

        /// <summary>Urgency (<see cref="DemandUrgency.Score"/>, carried on
        /// <see cref="MaterialVector.Urgency"/>) at which a light convoy flies anyway rather than
        /// wait for a better-sized hull or a convoy to fold it in. The score is class weight x
        /// starvation x (1 + age) x (1 + history): a fresh, fully starved catalogue want scores 1.0
        /// and so waits until it ages, a starved recipe input (2.0), fuel (2.5), install (3.0) or
        /// ship build (4.0) flies at once, and a partly stocked want waits. Purely additive vs the
        /// current TuneVersion.</summary>
        public double HaulHoldUrgency = 2.0;

        /// <summary>Whether a haul picks its hull by the fuel credits per litre it would carry
        /// (<see cref="HaulHullCost"/>) rather than by the largest hold. False restores
        /// largest-hold-first. Purely additive vs the current TuneVersion.</summary>
        public bool HaulRankHullsByFuel = true;

        /// <summary>How many of the network's nearest haul-stock nodes a light convoy (under
        /// <see cref="HaulMinLoadFraction"/>) searches for a hull small enough that its load fills
        /// that fraction, to fetch instead of the hull at the source. Urgent or not: urgency decides
        /// only whether the local hull flies when no such hull exists. 0 searches none. 4 → 8 at
        /// TuneVersion 56: measured 2026-09-25, fetches 572 → 610/h and near-empty big-hull flights
        /// 161 → 133 per 30 min, with fetch legs no longer.</summary>
        public int HaulSmallHullSearchNodes = 8;

        /// <summary>A haul whose fuel, valued at the departing node, costs more than this multiple of
        /// its cargo's value at the destination is refused, urgent or not (<see cref="HaulHullCost"/>).
        /// Measured 2026-09-25: the median solid haul burned 0.4% of its cargo value and 13% burned
        /// more than it carried. 0 turns the check off. Purely additive vs the current TuneVersion.</summary>
        public double FuelValueCeiling = 1.0;
        /// <summary>Seconds after a load during which ProductionManager will not release install
        /// reservations whose InvestmentRequest is missing. ReservedInventory survives a restart in
        /// the brain files but InvestmentRegister does not, so the managers need a window to re-post
        /// before an unbacked reservation is judged stale. Purely additive -- an old tuning.json
        /// lacking the key keeps this inline default, so no TuneVersion bump. 0 disables the window.
        /// See ProductionManager.BeginReleaseGrace.</summary>
        public double InstallReservationReleaseGraceSeconds = 300.0;
        /// <summary>Hold removed from an owned node per real hour after grace suppression ends.
        /// Runtime converts this to its actual global-pass amount.</summary>
        public double HoldDecayPerHour = 0.24;
        public double HoldDecayForIntervalSeconds(double intervalSeconds)
        {
            if (HoldDecayPerHour <= 0 || intervalSeconds <= 0) return 0;
            return HoldDecayPerHour * intervalSeconds / 3600.0;
        }
        /// <summary>Same as <see cref="HoldDecayPerHour"/> but applied only to nodes owned by a
        /// <see cref="FactionState.IsPlayerFaction"/> faction, so player-held territory can decay at a
        /// different pace than NPC-held territory.</summary>
        public double PlayerHoldDecayPerHour = 0.24;
        public double PlayerHoldDecayForIntervalSeconds(double intervalSeconds)
        {
            if (PlayerHoldDecayPerHour <= 0 || intervalSeconds <= 0) return 0;
            return PlayerHoldDecayPerHour * intervalSeconds / 3600.0;
        }
        /// <summary><see cref="SpatialNode.Threat"/> removed from every node per real hour, linear,
        /// the same shape as <see cref="HoldDecayPerHour"/> and applied in the same global pass.
        /// Runtime converts it to the pass's own amount. 0 disables decay, restoring the ratchet.
        ///
        /// <para>The default is set against the measured cadence of the thing it forgets. Raids
        /// landing on one node ran a median <b>6.2 minutes</b> apart live over 2026-09-10/11
        /// (n=977 gaps, 80% under 10 min, p95 43.5 min), each adding <see cref="RaidThreat"/>
        /// = 0.35. At 0.7/h a node crosses the 0.5 defensive trigger about 43 minutes after its
        /// attacker leaves -- so an assault at even the 95th-percentile cadence keeps the node
        /// above the line, while one that has actually stopped clears inside the hour and a half.
        /// A single unanswered raid decays out in 30 minutes; two inside the median gap still sum
        /// past 0.5, which is the "roughly two raids in a short window" the trigger always meant
        /// and could not previously express, because nothing made the window close.</para>
        ///
        /// <para>Purely additive vs the current TuneVersion -- a tuning.json lacking the key keeps
        /// this inline default rather than zeroing it.</para></summary>
        public double ThreatDecayPerHour = 0.7;
        public double ThreatDecayForIntervalSeconds(double intervalSeconds)
        {
            if (ThreatDecayPerHour <= 0 || intervalSeconds <= 0) return 0;
            return ThreatDecayPerHour * intervalSeconds / 3600.0;
        }
        /// <summary>Credit a node gains each time a fleet its owner sent resolves a stop there —
        /// the fleet-traffic counterpart of player presence. See <see cref="ShipActivity"/>.
        /// Non-positive switches the channel off.
        /// <para>Purely additive vs the current TuneVersion.</para></summary>
        public double ShipActivityCreditPerVisit = 1.0;
        /// <summary>Ceiling on <see cref="SpatialNode.ShipActivityCredits"/>. A node at this level
        /// gains the full <see cref="ShipActivityHoldPerHour"/>; below it the gain is the saturated
        /// share, so the cap is also how selective the channel is. At
        /// <see cref="ShipActivityCreditPerVisit"/> 1.0 it is the number of owning-faction fleet
        /// stops a node must hold inside <see cref="ShipActivityHalfLifeSeconds"/> to be carried by
        /// traffic alone.</summary>
        public double ShipActivityCreditCap = 10.0;
        /// <summary>Half-life of <see cref="SpatialNode.ShipActivityCredits"/> in seconds. Credit is
        /// halved once per this span since it was last written.
        ///
        /// <para>This is what makes the channel measure a traffic <b>rate</b> rather than a count of
        /// visits, and so it, not <see cref="ShipActivityCreditCap"/>, is the selectivity knob. A
        /// node's credit settles at about
        /// <c>1.44 x ShipActivityCreditPerVisit x stopsPerHour x (this / 3600)</c>; the cap is a
        /// ceiling on that number, not a selector for it, so any node whose settled level exceeds the
        /// cap reaches it given time however high the cap is set.</para></summary>
        public double ShipActivityHalfLifeSeconds = 900.0;
        /// <summary>Hold per real hour a node at saturated credit gains, before that pass's decay is
        /// subtracted from it.
        ///
        /// <para><b>This is where break-even sits, and that is the job it does.</b> A node is in net
        /// credit when <c>(credits / cap) x this &gt;= <see cref="HoldDecayPerHour"/></c>, i.e. at a
        /// credit fraction of <c>HoldDecayPerHour / this</c>. At 0.30 against a 0.24 decay that
        /// fraction is <b>0.8</b> — a node had to sit at 8 of 10 merely to stand still, which once
        /// credit became rate-limited put almost the whole estate in deficit. At 1.20 it is
        /// <b>0.2</b>: break-even lands at 2 of 10, about 5.6 owning-faction fleet stops an hour
        /// against a measured mean of ~7, so a typical node is modestly in credit and a quiet one
        /// bleeds at the full decay rate.</para>
        ///
        /// <para>Pairs with <see cref="ShipActivityHalfLifeSeconds"/>, which decides <i>what</i> is
        /// measured (recent rate rather than cumulative visits). That one separates the nodes; this
        /// one decides where the line between gaining and losing falls.</para></summary>
        public double ShipActivityHoldPerHour = 1.20;
        public TimeSpan ReinforceGracePeriod; // decay suppressed while within this window of the last Reinforce
        public TimeSpan UnknGraceDuration = TimeSpan.FromHours(8); // an unclaimed UNKN node is deleted once AbandonedUtc is this old (see WorldModel.GetExpiredUnknNodeIds) -- a time-based bound on UNKN territory independent of whether Claim-based reclaim keeps pace

        // ---- fog of war / sensing (docs/fog-of-war.md) -------------------------------------------
        // All four are purely additive vs the current TuneVersion, so no bump is needed -- no build
        // has ever persisted these keys, so an existing tuning.json cannot override them. Each one
        // therefore MUST carry an inline initialiser: LoadTuning leaves a field absent from both
        // files at its C# zero-value, and a zero DiscoveryHorizon or a zero range is inert rather
        // than obviously broken. MineOverburdenFraction shipped that way and sat at 0.0 unnoticed.
        /// <summary>How long a detected node stays discovered without being re-detected. Past this
        /// the entry lapses and the node reverts to unknown -- off the map, not merely stale.
        /// Evaluated at read (see <see cref="FactionKnowledge.HasLive"/>), never by a sweep.
        /// Non-positive is treated as "never expires", so a missing key blinds nobody.</summary>
        public TimeSpan DiscoveryHorizon = TimeSpan.FromDays(30);
        /// <summary>Sensor range every owned node has just by existing, against a reference-signature
        /// object. Deliberately small: owning a node is not the same as watching the space around it,
        /// and that gap is what <see cref="NodeCapabilityKind.SensorArray"/> is for.</summary>
        /// <summary>References for the two <b>absolute</b> excitation terms — operator direction,
        /// 2026-08-27: emission should track ship size, because power draw and thrust both scale with
        /// hull volume and the model is meant to favour small purpose-built ships.
        ///
        /// <para>Absolute rather than normalised against the hull's own capability, which would have
        /// made both terms pure utilisation and size-blind. The consequence is deliberate: a large hull
        /// saturates its activity terms while merely running, and a small one has to work to. Sized so
        /// a ~2,000 t ship at 1 g sits mid-scale rather than at either end.</para></summary>
        public double SignatureThrustReferenceN = 40_000_000.0;
        public double SignaturePowerReferenceMw = 30.0;

        /// <summary>The speed band that reads as supercruise, m/s. The floor sits far above any vanilla
        /// speed (~100 m/s) so ordinary flight can never trip it; the ceiling sits above the mod's own
        /// 2,000 km/s but far below the implied speed of a <b>jump</b>, which teleports and would
        /// otherwise register as an absurdly fast cruise. Both bounds are needed: the floor rejects
        /// flying, the ceiling rejects jumping.</summary>
        public double SignatureSupercruiseMinMps = 1_000.0;
        public double SignatureSupercruiseMaxMps = 3_000_000.0;

        /// <summary>Speed at which the supercruise glow is fully lit. Below the mod's 2,000 km/s
        /// maximum on purpose, so most of the usable range is spent spreading rather than saturated.</summary>
        public double SignatureSupercruiseReferenceMps = 500_000.0;

        /// <summary>Optional upper bound on ship signature. <b>0 means unbounded, and that is the
        /// shipped default.</b> The exponents already flatten absurd input, and the values a ceiling
        /// would actually cut are the legitimate ones — a 50,000 t capital at 40,000 DPS scores 8.9 at
        /// rest and 26.7 under jump, and lighting up at interplanetary range is the intended mechanic.
        /// Present as a dial for an operator who wants one, not as a guard the model needs.</summary>
        public double SignatureShipCeiling = 0.0;

        /// <summary>Minimum signature a fleet reads at while fighting, whatever it is.
        /// <para>A stealthy <b>approach</b> is a tactic the signature model supports and should: a
        /// small, unarmed, empty hull is genuinely hard to see coming. A stealthy <b>raid</b> is not
        /// — once the shooting starts it is an event, not a secret. A multiplier alone cannot express
        /// that, because a quiet enough hull stays quiet however much you multiply it; only a floor
        /// does. 3.0 puts a firefight above a station (2.0 in <see cref="Signature.ForNode"/>) and
        /// below a 100-kilotonne hull (6.4), and makes a ship with no antenna (the 25 km
        /// <see cref="SensorFloorKm"/>) detect one at 75 km, and one reference antenna broadcasting at
        /// full radius (100 km) at 300 km. 0 disables the floor and leaves the multiplier alone.</para></summary>
        public double SignatureEngagementFloor = 3.00;

        /// <summary>Excitation weights: how much each activity adds at full intensity. The multiplier
        /// is 1 + the weighted sum, so these are additive headroom over a hull at rest.</summary>
        public double SignatureWeightThrust = 0.35;
        public double SignatureWeightAntennaPower = 0.20;
        /// <summary>A beacon at its full 200 km. Deliberately above the antenna's 0.20: the beacon
        /// declares four times the reach, it has no receive side to trade against, and the model is
        /// meant to charge for being visible. See <see cref="SignatureSource.BeaconPower"/>.</summary>
        public double SignatureWeightBeaconPower = 0.35;
        public double SignatureWeightWeaponsFire = 0.60;

        /// <summary>The drives. Deliberately far heavier than the other sources: a big hull under jump
        /// should light up like a pulsar and be seen at interplanetary range. A 50,000 t capital at 40,000
        /// DPS sits at 8.9 at rest, so a x5 flash puts it near 45 — roughly 4,500 km on a 100 km sensor,
        /// which is the intended mechanic rather than an overflow.</summary>
        public double SignatureWeightPowerDraw = 0.25;

        /// <summary>Abstract-fleet activity. Only two terms, because only two can differ between one
        /// convoy and another: every fleet in transit is cruising, so a thrust or supercruise term
        /// would be on for the whole population and separate nobody. A fresh departure and a drilling
        /// dwell are the discriminating states.</summary>
        public double SignatureWeightFleetDeparture = 1.20;
        public double SignatureWeightFleetMining = 0.80;
        /// <summary>A fleet on its <see cref="FleetStopKind.Engage"/> dwell — the loudest thing a
        /// fleet does. Paired with <see cref="SignatureEngagementFloor"/>: this half keeps a big
        /// squadron louder than a lone fighter, that half stops a cheap raider being inaudible.</summary>
        public double SignatureWeightFleetEngagement = 2.00;
        /// <summary>FSD supercruise. A travel mode from an installed mod (RC.4 FSD Supercruise), not
        /// the vanilla jump drive — the two are separate systems and both are live here.</summary>
        public double SignatureWeightSupercruise = 1.00;
        public double SignatureWeightSupercruiseEntry = 1.50;

        public double SignatureWeightJumpCharge = 0.80;
        public double SignatureWeightJumpFlash = 4.00;

        /// <summary>Per-source decay windows. These differ because the sources differ in kind: an
        /// antenna radius is a steady-state setting and gets <b>no tail</b> (0 = counts only on the
        /// tick it is observed), while weapons fire is an event that must linger or being seen
        /// shooting carries no consequence.</summary>
        public double SignatureDecayThrustSeconds = 10.0;
        public double SignatureDecayAntennaPowerSeconds = 0.0;
        /// <summary>A beacon is a steady advertisement, not an event: 0 tracks the slider live, with
        /// no tail, the same as the antenna.</summary>
        public double SignatureDecayBeaconPowerSeconds = 0.0;
        public double SignatureDecayWeaponsFireSeconds = 15.0;

        /// <summary>Charging is a state and tracks live; the jump itself is an event and is
        /// deliberately <b>short</b> — bright and brief, not a trail to follow.</summary>
        public double SignatureDecayPowerDrawSeconds = 5.0;

        /// <summary>How long a convoy stays conspicuous after departing. Far longer than a player-side
        /// tail because the sampling that reads it is the map cadence and a fleet's whole life is
        /// hours — a 15 s burst on that timescale would be invisible.</summary>
        public double SignatureDecayFleetDepartureSeconds = 120.0;
        public double SignatureDecaySupercruiseSeconds = 0.0;
        public double SignatureDecaySupercruiseEntrySeconds = 10.0;

        public double SignatureDecayJumpChargeSeconds = 0.0;
        public double SignatureDecayJumpFlashSeconds = 5.0;

        public double SensorNodePassiveRangeKm = 25.0;
        /// <summary>Range each installed SensorArray level adds to its node.</summary>
        public double SensorArrayRangeKmPerLevel = 75.0;
        /// <summary>Sensor range an in-flight fleet carries. Small -- a convoy is not a picket line;
        /// what it buys is that routes get surveyed as a side effect of traffic.</summary>
        public double SensorFleetRangeKm = 15.0;

        /// <summary>How far a ship's radio reaches with no antenna aboard (FAC-419, docs/atc-voice.md):
        /// a player seated at a ship's controls hears a station's callouts and a pilot's calls inside
        /// it. Antennas raise it (<see cref="RadioPath.StationRangeKm"/>). Also the reference the
        /// antenna gain is measured from, so the static at the edge of every tier is the same. New
        /// key: no TuneVersion gate.</summary>
        public double AtcRadioRangeKm = 30.0;
        /// <summary>How far a spacesuit's radio reaches: a player out of the seat hears only this far,
        /// antennas or not (operator, 2026-09-29).</summary>
        public double AtcSuitRadioRangeKm = 10.0;
        /// <summary>An antenna dish's gain on its axis, dB (operator, 2026-09-29): pointing one at a
        /// station or a pilot lengthens the reach that way by 10^(gain/20) -- 12 dB is about 4x -- and
        /// lifts the signal with it (<see cref="RadioPath.DirectionalGainDb"/>). Radio only: the
        /// scanner stays omnidirectional (FAC-136).</summary>
        public double AtcDishGainDb = 12.0;
        /// <summary>A dish's full half-power beamwidth, degrees: the gain is 3 dB down at half of it
        /// off axis, and falls on a parabola in dB to nothing beyond.</summary>
        public double AtcDishBeamwidthDeg = 30.0;
        /// <summary>The Parabolic Antenna's gain on its axis, dB: a smaller reflector than the dish,
        /// so a weaker, wider beam (operator, 2026-09-29).</summary>
        public double AtcParabolicGainDb = 8.0;
        /// <summary>The Parabolic Antenna's full half-power beamwidth, degrees.</summary>
        public double AtcParabolicBeamwidthDeg = 45.0;

        /// <summary>Pilot radio physics (FAC-419 slice 5, <see cref="RadioPath"/>). The operator's values
        /// from the radio physics bench, 2026-09-29: a sideband link at 450 MHz. New keys: no gate.</summary>
        public double AtcCarrierMHz = 450.0;
        /// <summary>Signal-to-noise of a pilot's call at 1 km in clear sky; falls 6 dB per doubling.</summary>
        public double AtcSnrAt1kmDb = 35.0;
        /// <summary>Below this instantaneous signal-to-noise the receiver cuts out.</summary>
        public double AtcSquelchDb = 3.0;
        /// <summary>The muffle a diffracted path gets: this low-pass at <see cref="AtcMuffleFloorAtLossDb"/>.</summary>
        public double AtcMuffleFloorHz = 700.0;
        public double AtcMuffleFloorAtLossDb = 20.0;
        /// <summary>A clear pilot call's level against the controller.</summary>
        public double AtcPilotLevelDb = -10.0;
        /// <summary>How much of the signal arrives by reflection (0 open space .. 1 all reflected): sets
        /// how deep multipath fades go. Not modelled per place; the bench's value.</summary>
        public double AtcReflections = 0.3;
        /// <summary>How far out on final approach an arriving pilot calls in (operator, 2026-09-29).</summary>
        public double AtcPilotCallDistanceKm = 5.0;

        /// <summary>How far a sensor and its target are each raised off the body they stand on before
        /// a planet is tested for blocking the line between them (<see cref="SensorOcclusion"/>).
        ///
        /// <para><b>Zero would make every surface node blind on its own body</b>, because two points
        /// exactly on a sphere have no sightline through it at all. Two masts of height <c>h</c> on a
        /// body of radius <c>R</c> see each other out to <c>2·sqrt(2Rh)</c>: at 2 km that is 22 km
        /// across Tellus, 16 km across Agni and 13 km across Kor — the same order as the 25 km
        /// passive envelope, so a surface estate keeps its neighbourhood and loses the far side.</para>
        ///
        /// <para>Not a claim about antenna height. It is the one dial that says how much of a body's
        /// curvature a sensor sees over, and raising it is how surface factions are given back
        /// reach.</para></summary>
        public double SensorHorizonLiftKm = 2.0;

        /// <summary>A player grid's sensor strength with no working antenna. The same for every hull,
        /// small or large: grid size decides only which antenna fits, never the range itself.
        /// See <see cref="ShipSensor"/>.</summary>
        public double SensorFloorKm = 25.0;
        /// <summary>What a reference antenna (aperture 1.0) adds while merely working -- listening.
        /// Costs no signature: a receiver does not transmit.</summary>
        public double SensorPassiveKm = 25.0;
        /// <summary>What a reference antenna adds broadcasting at <see cref="SensorAntennaReferenceM"/>.
        /// Scales linearly with the configured radius, the same dial the emission term reads.</summary>
        public double SensorActiveKm = 50.0;
        /// <summary>Compresses antenna block volume into aperture. Vanilla antennas span 4,688:1 in
        /// volume; 0.25 turns that into 0.19 (small-grid antenna) .. 1.58 (dish).</summary>
        public double SensorApertureExponent = 0.25;
        /// <summary>Block volume, m³, of the aperture-1.0 antenna: <c>LargeBlockRadioAntenna</c>,
        /// 12 large cubes.</summary>
        public double SensorAntennaReferenceVolumeM3 = 187.5;
        /// <summary>Broadcast radius, m, at which the active term is fully lit: SE's
        /// <c>MaxBroadcastRadius</c> class default, which no vanilla antenna overrides.</summary>
        public double SensorAntennaReferenceM = 50000.0;

        /// <summary>Signal-to-noise margin, dB, at which a contact opens the Class tier: hull count
        /// and grid class, or station-or-field and economic stage. 6 dB is half the detection range.
        /// See <see cref="IntelGrade"/> and docs/fog-of-war.md → Intelligence grades.</summary>
        public double IntelClassDb = 6.0;
        /// <summary>Margin at which a contact opens the Identity tier without a transponder: owner,
        /// relation colour, a node's name. 10 dB is 32% of the detection range.</summary>
        public double IntelIdentityDb = 10.0;
        /// <summary>Margin at which a contact opens the Armament tier: strength band, a node's
        /// defence band. 16 dB is 16% of the detection range.</summary>
        public double IntelArmamentDb = 16.0;
        /// <summary>How far below its threshold a tier a contact already holds stays open, dB. The ATC
        /// squelch's rule, so a contact on a boundary does not flicker.</summary>
        public double IntelHysteresisDb = 2.0;
        /// <summary>Economic score at which a station reads as a Hub on the map: the sum of its
        /// capability levels plus a tenth of its headcount (<see cref="NodeStages.Score"/>). 10 was
        /// read off the live graph on 2026-11-25 (FAC-529): 367 of 485 stations score 0, 60 score
        /// exactly 5 (one seeded facility), 32 score 10 or more.</summary>
        public double NodeHubScore = 10.0;
        /// <summary>How long a remembered node grade stays authoritative, hours. A lower-tier
        /// reading replaces it only once it is older than this, so passing at long range does not
        /// erase a close scan and a close scan does not stay true forever.</summary>
        public double IntelStaleAfterHours = 72.0;

        /// <summary>Node defence (the offensive gate's <c>OfNodeDefence</c>, with committed Guards) at
        /// which a scan reads two pips rather than one. 2026-11-26 census: p75 7k, p90 49k across 506
        /// stations; raid squadrons carry 55k–290k (FAC-531).</summary>
        public double IntelDefenceContestedDps = 20000.0;
        /// <summary>…and three pips: beyond what most raid squadrons carry, about the top 5%.</summary>
        public double IntelDefenceFortressDps = 100000.0;
        /// <summary>A grid scanned at Armament reads <c>heavily armed</c> from this weapon DPS up,
        /// <c>armed</c> below it, <c>unarmed</c> at none (FAC-533). The default is a contested node's
        /// defence: one hull that could hold a station on its own.</summary>
        public double IntelGridHeavyDps = 20000.0;

        /// <summary>How long a fleet sighting counts toward a faction's estimated strength on the
        /// Factions page, hours: the strongest armed fleet seen at Armament inside it (FAC-532).</summary>
        public double IntelFleetWindowHours = 72.0;
        /// <summary>Estimated-wealth edges on the Factions page: the stage score of the viewer's known
        /// nodes (Outpost 1, Settlement 2, Hub 4, Capital 8). 2026-11-26 census, full intel across 10
        /// factions: 10, 12, 16, 16, 46, 52, 122, 240, 246, 358; two factions a band (FAC-532).</summary>
        public double IntelWealthModestScore = 15.0;
        public double IntelWealthComfortableScore = 40.0;
        public double IntelWealthProsperousScore = 100.0;
        public double IntelWealthRichScore = 200.0;
        /// <summary>Estimated-strength edges: Σ the viewer's remembered node defence plus the
        /// strongest fleet sighting. 2026-11-26 census, full intel: 147k to 1.95M (FAC-532).</summary>
        public double IntelStrengthLightDps = 150000.0;
        public double IntelStrengthModerateDps = 450000.0;
        public double IntelStrengthStrongDps = 650000.0;
        public double IntelStrengthOverwhelmingDps = 900000.0;

        /// <summary>Smallest player-owned grid that is a detection target, in blocks. Debris and
        /// one-block scrap are not contacts; anything a player would call a ship is.</summary>
        public int SignatureGridMinBlocks = 5;
        /// <summary>How often an unseated player-owned grid is re-sampled, seconds, and how often the
        /// grid enumeration behind it is refreshed. Shorter than the weapons-fire decay window
        /// (<see cref="SignatureDecayWeaponsFireSeconds"/>, 15 s) so an unmanned turret firing is
        /// still caught; a seated grid is sampled every pass regardless.</summary>
        public double SignatureGridSampleSeconds = 10.0;
        /// <summary>FAC-534: how long after a faction's last human member logs off before damage to its
        /// grids counts as an offline raid, minutes. Damage inside the window is the fight that was
        /// already happening.</summary>
        public double OfflineRaidGraceMinutes = 15.0;
        /// <summary>FAC-534: how often an offline raider is shown the warning again during one raid, minutes.</summary>
        public double OfflineRaidWarnRepeatMinutes = 5.0;
        /// <summary>FAC-534: a raid ends once its attacker has not hit the victim for this long, minutes;
        /// the summary is logged then.</summary>
        public double OfflineRaidEndAfterMinutes = 5.0;
        /// <summary>FAC-534: a faction going wholly offline within this many minutes of hostile player
        /// damage to its grids is combat logging, and its attackers are not offline raiders for that fight.</summary>
        public double CombatLogWindowMinutes = 5.0;
        /// <summary>How far a graph neighbour still counts as something you simply know about.
        ///
        /// <para>Adjacency alone is not a knowledge relation: the node graph is linked by *routing*
        /// reach (<c>NodeSeparationDeepSpaceKm</c> x <c>NodeExpansionReachPct</c> = 625 km in deep
        /// space), so borrowing it wholesale would let a routing knob silently widen what every
        /// faction knows. Bounding it here keeps the two independent.</para>
        ///
        /// <para>Must exceed the passive sensor's effective reach or the clause is a no-op -- the
        /// sweep would find those nodes anyway. Measured against 217 live scan-backed stations:
        /// at 50 km this adds a median of 7 foreign stations (which the sensor already sees), at
        /// 100 km a median of 20, at the full 625 km a median of 56.</para></summary>
        public double BirthrightRangeKm = 100.0;

        /// <summary>Slice C's gate: when true, a faction generates candidate actions only for nodes
        /// it knows, and reads a rival node's owner, hold and resource value as they were at last
        /// detection rather than as they are now (<see cref="KnowledgeGate"/>).
        ///
        /// <para><b>Off by default, and off is the pre-slice-C behaviour exactly</b> -- the gate
        /// yields every node with live values, so turning this back off on a live server is a real
        /// revert and needs no deploy. A faction that knows nothing does nothing, which is how a
        /// knowledge-gated economy stalls; do not turn this on anywhere without a sim run behind
        /// it.</para></summary>
        public bool KnowledgeGatedBrains = false;
        public double ReinforceMaxIncomeFractionPerWindow = 0.3; // max share of CentralBankRecentIncome spendable on reinforcement per SpendBudgetWindowSeconds, on top of the floor below
        public long ReinforceBudgetFloor = 50000; // income-independent minimum reinforce budget per window (= ReinforceCost: >=1 reinforcement/window even at zero income); both this and the fraction above <=0 = uncapped
        public double ReinforceWealthFractionPerWindow = 0.001; // share of treasury surplus above the operational reserve (Economy.SpendableTreasurySurplus) spendable on reinforcement per window, on top of income+floor; <=0 = income-only budget
        public double PatrolWealthFractionPerWindow = 0;  // same wealth term for Patrol dispatch; ships disabled (patrols spawn physical fleets — enable deliberately via tuning)
        public double TradeWealthFractionPerWindow = 0;   // same wealth term for Trade dispatch; ships disabled
        public double OperationalReserveRunwayTicks = 60.0; // when net flow is negative, the operational reserve grows to cover this many AccrueIncome passes of burn (floor: ReserveTreasury)
        public double SpendBudgetWindowSeconds = 30.0; // window length shared by the reinforce/Patrol/trade spend budgets; <=0 = uncapped (inline default: missing JSON key must not zero it)
        public long GuardCost;
        // Reactive Guard dispatch (FleetManager's defend block). Guard is the only fleet kind that
        // can intercept an incoming Raid/Retaliate/Conquer, so these gate the one path that turns a
        // detected inbound raid into a defender standing at the node.
        public double GuardMaxIncomeFractionPerWindow = 0.3; // max share of CentralBankRecentIncome spendable on Guard dispatch per SpendBudgetWindowSeconds, on top of the floor below
        public long GuardBudgetFloor = 100000; // income-independent minimum Guard budget per window (= GuardCost); both this and the fraction above <=0 = uncapped
        public double GuardWealthFractionPerWindow = 0.001; // share of treasury surplus spendable on Guard dispatch per window, on top of income+floor; <=0 = income-only budget
        /// <summary>Sanity bound on how far a defender is ever sent, in transit minutes. This is
        /// the SECOND gate, not the deciding one: whether a Guard is worth dispatching is answered
        /// by whether it can arrive before the raid's effect lands, which is derived per-raid from
        /// its own engagement dwell rather than from any constant here.
        /// <para>5.0 is chosen against measured pacing, not guessed: raid transit on
        /// awl-standalone-test over 2026-09-02/03 ran n=61, p50=20s, p90=78s, max=80s. A cap in
        /// tens of minutes would admit every node in the network and decide nothing.</para></summary>
        public double GuardResponseMaxTransitMinutes = 5.0;
        /// <summary>Hysteresis on redirecting a Guard already in flight: the new threat must land
        /// at least this many seconds sooner than the one the Guard is currently flying to. Without
        /// a margin a defender ping-pongs between two flashpoints whose ETAs cross each tick and
        /// never reaches either.</summary>
        public double GuardRetargetUrgencyMarginSeconds = 120.0;
        /// <summary>Minimum gap between two retargets of the same fleet.</summary>
        public double GuardRetargetCooldownSeconds = 300.0;
        /// <summary>Hard ceiling on how often one fleet may be pulled around before it is left to
        /// finish the leg it is on, whatever else happens. The cooldown paces churn; this bounds it.</summary>
        public int GuardMaxRetargets = 2;
        public long PatrolCost;         // perimeter-patrol dispatch cost
        public double PatrolMaxIncomeFractionPerWindow = 0.3; // max share of CentralBankRecentIncome spendable on Patrol dispatch per SpendBudgetWindowSeconds, on top of the floor below
        public long PatrolBudgetFloor = 5000; // income-independent minimum Patrol budget per window (= PatrolCost); both this and the fraction above <=0 = uncapped
        public long CampaignCost;        // set a strategic campaign focus (new campaign action cost)
        public double CampaignCostBaseHoldDecay;  // cost increase per day hold is below 0.5 in the campaign region
        public int CampaignFocusBaseCapacity = 1;                 // missing JSON must retain homeland focus
        public long CampaignFocusCombatPowerPerAdditional = 100;  // fleet power needed for each extra focus
        public int CampaignFocusMaxConcurrent = 3;                // hard operational cap
        public double StandingPicketNeed = 0.6;                   // need floor for the defend focus offered to a faction holding none; <=0 = pressure-gated only

        // Muster — deterrence fleet build-up (see FactionBrain's Security/Rivalry-scored Muster block).
        public long MusterCost;                    // set/raise military posture (mirrors CampaignCost's shape)
        public double MilitaryPostureGain;          // posture raised per Muster dispatch, 0..1
        public double MilitaryPostureDecayPerHour;  // posture erosion when not reinforced — a floor, not a one-way ratchet
        public double MusterConvoyScaleMax;         // ConvoySize multiplier ceiling at MilitaryPosture/MusterFocusPriority=1 (1.0 = no scaling)
        // Combined Raid+Patrol standing-stock target (ShipyardManager.PostProactiveShipDemands) —
        // one shared pool, not two independent per-role ceilings, since MilitaryPosture means
        // "how much fleet" not "how much offense vs. defense."
        public int MusterStockBaseline;             // combined target at MilitaryPosture=0 (1 matches pre-Muster "any nonzero stock" behavior)
        public int MusterStockMaxBonus;              // additional combined target at MilitaryPosture=1
        // Border-pressure dampening (FactionBrain.Sense's PowerDampening) — relative fleet strength
        // scales how much territorial Pressure actually contributes to Security/Rivalry.
        public double BorderPressureParityFloor;     // multiplier at exact fleet-power parity — never 0, avoids a stable cold war
        public double BorderPressureWeaknessCeiling; // multiplier ceiling when badly outclassed
        /// <summary>The rival share of the influence mass standing over a faction's own estate at
        /// which <see cref="IInfluenceField.Pressure"/> reads 1.0. 0.5 means "half the influence over
        /// my territory is theirs" is total contest. Lower = the same overlap reads as more pressure.
        /// This sets the scale of the whole Security/relation-drift chain, so it is the knob to reach
        /// for when the world is uniformly too tense or too complacent.
        /// <para>Inline default because a bare <c>new FactionTuning()</c> (sim, tests) must not read
        /// zero here — zero would make every contested estate read maximum pressure.</para></summary>
        public double BorderPressureFullContestShare = DefaultBorderPressureFullContestShare;
        public const double DefaultBorderPressureFullContestShare = 0.5;
        // ---- Layer-2 influence radius (km), single-sourced in InfluenceRadius (FAC-250) ----------
        /// <summary>What a bare claim projects before anything is built on it.</summary>
        public double InfluenceRadiusFloorKm = 12.0;
        /// <summary>What a founded station projects. Founding is the large step; this is the value
        /// every node on the map carried when radius was one hardcoded constant.</summary>
        public double InfluenceRadiusFoundedKm = 24.0;
        /// <summary>Added per installed capability <i>level</i>, so developing a station keeps paying
        /// off in reach rather than only in output.</summary>
        public double InfluenceRadiusPerCapabilityLevelKm = 2.0;
        /// <summary>Ceiling. 40 km was the old dormant scan-backed constant; it survives here as what
        /// a fully developed station reaches rather than as a second, contradictory default.</summary>
        public double InfluenceRadiusMaxKm = 40.0;
        /// <summary>How far present border tension may move an NPC-vs-NPC standing away from what the
        /// two have actually DONE to each other. The situational term is a bias on the conduct ledger,
        /// not a replacement for it, and without a bound it is not even close to one: its own range is
        /// about -1.45..+0.20 before this clamp, so at high pressure it alone drives a standing to the
        /// floor and conduct stops mattering — which is the state FAC-249 measured.
        /// <para>Raise it to let geography speak louder than history, lower it to make the record
        /// decisive. Inline default so a bare <c>new FactionTuning()</c> does not read zero, which
        /// would silently delete the border-tension term entirely.</para></summary>
        public double MaxBorderPressureStandingBias = DefaultMaxBorderPressureStandingBias;
        public const double DefaultMaxBorderPressureStandingBias = 0.35;
        /// <summary>How long after a blow is exchanged a pair still counts as actively at war, and so
        /// keeps accruing war fatigue. Past it, a sour standing alone is a dislike, not a war, and
        /// fatigue decays.
        /// <para>Measured, not chosen: across 88 raid/conquer resolutions over 79 live hours
        /// (2026-08-31..09-03) the per-pair inter-arrival gap ran p25 28 min, median 72 min, p75
        /// 163 min. 14400 s (4 h) spans <b>80%</b> of those gaps, so a real war stays hot while a
        /// pair that has stopped fighting cools. 0 or less disables the gate and restores the old
        /// standing-only behaviour.</para></summary>
        public double WarFatigueActWindowSeconds = DefaultWarFatigueActWindowSeconds;
        public const double DefaultWarFatigueActWindowSeconds = 14400.0;

        // income loop (minted territorial yield -> private ledger; see Economy)
        public long BaseYield;           // income/economy quantum a source mints at value=1, hold=1, full control
        public long UpkeepPerSource;     // flat credit cost/economy quantum per physical station
        public double ManagerIntervalSeconds; // active real seconds between operational manager passes
        public double EconomyIntervalSeconds; // active real seconds represented by one income/upkeep quantum
        public double RunwayComfortTicks;  // bleeding alarms below this many economy quanta of runway
        public long DevelopCost;        // install a capability at a founded node (station sub-module)
        public long FoundCost;              // one-time cost to plant a new SE station on a brain-owned node
        /// <summary>How much a claim on a body that recruits (<see cref="PlanetRecord.Recruits"/>) adds
        /// to its Worth when the brain picks which claim to found and how strongly to want it. Worth
        /// reads mining value, and the one body workers come from has thin ground, so without this
        /// its claims would be founded last. Added to <see cref="SpatialNode.ResourceValue"/> before
        /// the <c>0.5 + 0.5 × worth</c> quality map, which clamps at 1.</summary>
        public double FoundRecruitingBodyBonus = 0.5;
        public double FoundHoldThreshold;     // node must hold at least this strength before founding is offered (design intent ~0.6)

        // ---- build queue (ShipyardManager) -----------------------------------------------------
        /// <summary>Maximum hours a build queue entry may spend collecting components before being
        /// abandoned and its collected items returned to node inventory. Supports long builds
        /// (e.g. a capital ship accumulating over days) without exposing a magic tick-timer.</summary>
        public double MaxBuildDurationHours;

        /// <summary>Hours a build queue entry may go WITHOUT absorbing a single component before it
        /// is abandoned and refunded. This is the real abandonment rule — <see cref="MaxBuildDurationHours"/>
        /// is only an absolute backstop. Measured in serviced time, not wall-clock: see
        /// <see cref="BuildQueueEntry.StalledSeconds"/> for why a desktop host's overnight shutdown
        /// must not count. 0 or less disables stall abandonment and leaves only the backstop.</summary>
        public double BuildNoProgressHours;

        /// <summary>How long a Shipyard's active build may go without absorbing a component and
        /// still count as working, in serviced seconds (see
        /// <see cref="BuildQueueEntry.StalledSeconds"/>). Read only by
        /// <see cref="ShipyardCoverage.IsWorking"/>, which decides whether a region that already has
        /// a Shipyard should ask for another: a yard whose one slot is held by a build nothing is
        /// arriving for is not producing hulls, and a second yard beside it splits the same starved
        /// component stream rather than adding output.
        /// <para>Far below <see cref="BuildNoProgressHours"/> on purpose — that is when a build is
        /// given up on, this is when it stops counting as evidence of throughput.</para></summary>
        public double ShipyardWorkingStallSeconds = 600.0;

        // ---- free ore model (OreField / IceField / RareOreField nodes) -------------------------
        /// <summary>SubTypeId of the miner block used to derive cargo volume at session load.
        /// The host plugin resolves the SE block definition and writes <see cref="MinerCargoVolumeLiters"/>.</summary>
        public string FreeMinerCargoSubTypeId;
        /// <summary>Litres of ore one free miner can carry per run. Zero until resolved by the host
        /// from <see cref="FreeMinerCargoSubTypeId"/> at session load. Never hardcoded.</summary>
        public long FreeMinerCargoVolumeLiters;
        /// <summary>Ticks between free mining runs per ore-bearing node.
        /// Yield/tick = MinerCargoVolumeLiters / FreeMiningRunIntervalTicks.
        /// Default 30 ticks = 5 real minutes at 0.1 tick/s.</summary>
        public int FreeMiningRunIntervalTicks;  // 30 @ 0.1 tick/s = 5 real min

        // ---- Mine action (OperationsManager-dispatched active extraction, see docs/managers.md) ----
        /// <summary>Max distance (km) from a Mine order's hub within which an owned mineable node is
        /// in its catchment (<c>OperationsManager.FindMineCatchment</c>, <see cref="MineTargeting.IsInCatchment"/>),
        /// and within which <see cref="ExtractionPlanner"/> credits a deposit to a hub.</summary>
        public double MineSearchRadiusKm;
        /// <summary>Superseded -- read by nothing since the NPC demand-row Mine trigger and its
        /// single-source pick were retired (FAC-395 slice 2b); a catchment
        /// walk orders deposits richest first, nearest among equal scores. Kept only so a stale key
        /// in an existing tuning.json is silently ignored. It was: how much worse than the best
        /// candidate a Mine source may score
        /// (<see cref="MineTargeting.SelectionScore"/>) and still count as equally rich, letting
        /// distance decide between them. 0.15 = "within 15% of the best". Exists because the two costs
        /// are not comparable in magnitude: the whole <see cref="MineSearchRadiusKm"/> is well under a
        /// minute of transit at <see cref="FleetSpeedKmPerMin"/>, while node kind swings the dwell by
        /// up to 20x (a rare-ore basket scores 2.0 at a RareOreField against an OreField's 0.1) and
        /// the cooldown with it, so richness has to lead and distance can only break a near-tie.
        /// 1.0 or more makes every eligible candidate equivalent, restoring nearest-wins.
        /// Purely additive vs the current TuneVersion, so no bump needed -- a tuning.json lacking the
        /// key keeps this inline default rather than zeroing it, which would demand an exact score
        /// match and leave distance almost never breaking a tie.</summary>
        public double MineRichnessTolerance = 0.15;
        /// <summary>Superseded by <see cref="MineCooldownMultiplier"/> (a duration-scaled cooldown,
        /// not a flat one) -- no longer read anywhere as of the dwell-based Mine redesign. Kept only
        /// so a stale field in an existing tuning.json is silently ignored rather than erroring.</summary>
        public double MineCooldownSeconds;
        /// <summary>Superseded -- no longer read anywhere, and its name is the trap. It scaled
        /// <c>OperationsManager.EstimateMineYield</c>, which fed only the Mine-vs-Haul comparison and
        /// never a minted unit; that estimate was deleted when the comparison moved to
        /// <c>MineHaulChoice.Weigh</c>, itself retired with the comparison (FAC-395 slice 2b).
        ///
        /// <para>There is no successor and no equivalent knob, because a trip's mint is not a formula
        /// to scale: <c>FleetResolution</c> mints <c>MineTargetLiters</c> split by weight, and
        /// <c>OperationsManager.ClampTargetLiters</c> fills the drill ship's hold. Mining throughput moves via
        /// <see cref="MineRateMultiplier"/> (trips per hour) and
        /// <see cref="MineCooldownMultiplier"/> (node availability), not trip size.</para>
        ///
        /// <para>Kept only so a stale key in an existing tuning.json is silently ignored rather than
        /// erroring, matching <see cref="MineCooldownSeconds"/> and
        /// <see cref="MineDrillLitersPerCutUnit"/>.</para></summary>
        public double MineYieldTripMultiplier = 1.0;
        /// <summary>Fraction of a Mine dwell's volume-derived gather time added as "overburden" --
        /// time spent stripping down to the ore before extraction can begin -- e.g. 0.15 means a trip
        /// that would otherwise take 100s of real gathering spends 115s dwelling in total. Deliberately
        /// a fraction of gather time, not a flat constant or a node-richness term: this way overburden
        /// also naturally scales with drill speed (a slower drill spends proportionally longer on both
        /// gathering and stripping), and with target volume (more asked for -&gt; more overburden too).</summary>
        public double MineOverburdenFraction;
        /// <summary>Multiplier applied to a Mine dwell's REAL elapsed duration (gather + overburden)
        /// to produce that node's next <see cref="SpatialNode.MineCooldownUntilUtc"/> -- e.g. 1.0
        /// means an hour spent mining earns a one-hour cooldown on that node. Replaces the old flat
        /// <see cref="MineCooldownSeconds"/> gate so a huge haul and a token top-up no longer incur
        /// the identical cooldown penalty -- cooldown now scales with how much was actually extracted.
        ///
        /// <para>At 1.0 a node rests exactly as long as it was worked, so a deposit is available
        /// half the wall-clock time a single miner can occupy it. Values above 1.0 make the rest
        /// longer than the dwell.</para></summary>
        public double MineCooldownMultiplier = 1.0;
        /// <summary>Superseded -- no longer read anywhere. Originally calibrated a liters/second
        /// extraction rate derived from a drill block's real <c>CutOutRadius</c>/<c>Speed</c>
        /// (scanned at session load); dropped when decompiled SE source (`MyShipDrill.cs`) showed
        /// <c>Speed</c> is a rendering-only field (drives the drill head's visual spin animation via
        /// <c>Render.UpdateSpeed</c>), not a cutting-rate multiplier -- the whole physical-reading
        /// path was built on a wrong assumption about what that field means, not just an uncalibrated
        /// one. Kept only so a stale field in an existing tuning.json is silently ignored rather than
        /// erroring, matching <see cref="MineCooldownSeconds"/>'s own precedent.</summary>
        public double MineDrillLitersPerCutUnit = 1.0;
        /// <summary>Sole conversion from <see cref="IPrefabCatalogue.DrillYieldScore"/> (the summed
        /// swept cross-section of a prefab's drill blocks -- see
        /// <see cref="MineTargeting.DrillBlockScore"/>) to a liters/second extraction rate:
        /// <c>rate = DrillYieldScore(prefabId) * MineDrillFallbackLitersPerScorePoint</c>.
        /// Deliberately the ONLY rate source (see <see cref="MineDrillLitersPerCutUnit"/>'s doc
        /// comment for why the "real physical reading" alternative was dropped) -- a value entirely
        /// owned by Factions' own tuning, not coupled to any live SE/mod-adjustable mining mechanic
        /// (e.g. a server-side "mining tick rate" plugin), so external changes to actual in-game
        /// drilling speed can never silently reshape Mine's own pacing.
        ///
        /// <para><b>Superseded by <see cref="MineDrillLitersPerScorePoint"/> (FAC-228)</b> and read by
        /// nothing. It was 1.9 -- the vanilla drill's <c>CutOutRadius</c> -- and its stated job was
        /// that "a vanilla large drill keeps the rate it had", preserving the output of the retired
        /// <c>CutOutRadius^3 * Speed</c> formula rather than deriving a rate. Dimensionally it never
        /// closed: an <c>r^2</c> score times a radius is <c>m^3</c>, a volume, consumed as litres per
        /// second, dropping the m^3-to-litre factor of 1000 and asserting "per second" with no time
        /// term. Kept only so a stale key in an existing tuning.json is ignored rather than
        /// erroring.</para></summary>
        public double MineDrillFallbackLitersPerScorePoint = 1.9;

        /// <summary>Converts a prefab's <see cref="IPrefabCatalogue.DrillYieldScore"/> -- the summed
        /// <c>CutOutRadius^3</c> of its drill blocks, in m^3 -- to litres of ore per second, before
        /// composition and <see cref="MineDutyCycle"/>. Derived from SE's own mechanic rather than
        /// fitted:
        ///
        /// <para><c>MyShipDrill.UpdateBeforeSimulation10</c> gates on <c>m_drillFrameCountdown</c>
        /// (<c>-= 10</c>, return while positive, <c>+= 90</c>), so one cut-out every 90 frames = 1.5 s
        /// at 60 fps. The cut is a sphere of <c>CutOutRadius</c>, and <c>MyDrillBase</c> harvests
        /// <c>removedAmount / 255 * VoxelHarvestRatio * MinedOreRatio</c> cubic metres of ore, with
        /// <c>VoxelHarvestRatio = 0.009f</c>. So per unit of <c>r^3</c>:</para>
        ///
        /// <para><c>(4/3)*pi * 0.009 / 1.5 * 1000 L/m^3 = 25.1327</c> litres per second, per unit of
        /// <see cref="OreDensityRatio"/>. A vanilla LargeBlockDrill on iron is then
        /// <c>1.9^3 * 25.1327 * 5 = 861.9 L/s</c> at full duty -- SE's peak, which
        /// <see cref="MineDutyCycle"/> discounts to something a real ship achieves.</para>
        ///
        /// <para>It omits the world's <c>HarvestRatioMultiplier</c>, which SE's ship drill folds into
        /// <c>VoxelHarvestRatio</c>. That is kept as a feature: NPC miners out-drill a player by
        /// <c>1 / HarvestRatioMultiplier</c> (1.25x at the live world's 0.8). Unintended when derived;
        /// left standing 2026-10-17 as the seed of a difficulty slider (docs/todo.md). Do not "fix" it
        /// by multiplying the setting in.</para></summary>
        public double MineDrillLitersPerScorePoint = 25.1327;

        /// <summary>Fraction of a Mine dwell a ship spends actually cutting fresh, ore-bearing voxel.
        /// The one free parameter in the extraction model, and deliberately the ONLY one: every other
        /// term is derived from SE's mechanic or read off the node.
        ///
        /// <para>It exists because <see cref="MineDrillLitersPerScorePoint"/> is a peak. It assumes
        /// every cut lands on fresh solid ore; a stationary drill re-cuts spent space and yields
        /// nothing, and a real miner spends much of its time approaching, aligning, turning and
        /// returning. 0.5 is an operator balance choice (2026-09-02), not a measurement -- the duty
        /// cycle of a real mining ship has not been observed. See FAC-228.</para>
        ///
        /// <para>For scale: the pre-FAC-228 model was arithmetically equivalent to a duty cycle of
        /// about 0.092, reached through a factor-of-1000 unit error, a x2 economy weight and a
        /// diplomacy term rather than by choice.</para></summary>
        public double MineDutyCycle = 0.5;

        /// <summary>The NPC extraction planner's stop rule (docs/mining-plan.md, D2): a standing
        /// extraction order pauses once its hub, with its catchment, holds this many hours of the
        /// hub's measured consumption of that ore. See <see cref="ExtractionPlanner"/>. A new key, so
        /// no <c>TuneVersion</c> gate; the inline default covers a tuning.json that lacks it.</summary>
        public double ExtractionStockHours = 24.0;

        /// <summary>The planner's resume threshold, as a fraction of <see cref="ExtractionStockHours"/>:
        /// a paused order resumes once stock falls below <c>ExtractionStockHours x this</c>. Below
        /// 1.0 is the hysteresis band that stops a hub flapping at its ceiling; 1.0 or more removes
        /// the band.</summary>
        public double ExtractionResumeFraction = 0.75;

        /// <summary>Time constant, in hours, of <see cref="SpatialNode.MinedEma"/>: a node that stops
        /// receiving mined ore sees its estimate fall by <c>e</c> over this span. Matches
        /// <see cref="FuelBurnEmaHours"/> by default so the ice path's two measured terms age alike.</summary>
        public double ExtractionMinedEmaHours = 6.0;

        /// <summary>Capacity growth (docs/mining-plan.md, "Capacity growth"): the most drill hulls one
        /// Shipyard pass asks for, per faction, for its hull-bound extraction orders. Each is one
        /// proactive <c>Ship:Mine</c> demand at a yard with no Mine build or demand of its own, so it
        /// also caps how many yards one pass can turn to Mine work. 0 disables proactive Mine builds;
        /// the dispatch fallback still stages one. A new key, so no <c>TuneVersion</c> gate.</summary>
        public int ExtractionMaxHullOrdersPerPass = 2;

        /// <summary>Pacing of the NPC extraction walk (docs/mining-plan.md, "Slice 2b as built"): the
        /// most Mine trips one staged Operations step attempts before it yields, resuming the same
        /// order's catchment walk on the next step. Each attempt searches the estate for a drill, so an
        /// order walking a large catchment of deposits no hull can reach does unbounded work in one
        /// step without it. Pacing only: the pass attempts the same trips in the same order, and the
        /// synchronous tick, which has no steps, ignores it. 1, the default, makes a Mine trip the grain
        /// a Haul vector already is (one per step); a step costs microseconds of overhead, so many
        /// cheap ones still fit one frame. 0 or less: no limit. A new key, so no <c>TuneVersion</c>
        /// gate.</summary>
        public int ExtractionMineAttemptsPerStep = 1;

        /// <summary>Drill-quality upgrade (docs/mining-plan.md, "Slice 3b as built"): a deposit-bound
        /// NPC extraction order asks for one better drill hull when the Mine build its nearest free
        /// yard would pick drills at least this many times faster than the median hull working or
        /// parked in its catchment. Under the per-deposit cooldown (cooldown = dwell) a faster drill
        /// shortens both, so drill quality is what limits a hub that has no free ground. At or below
        /// 1.0 upgrades are off: a ratio of 1 would re-order the hull the catchment already flies. A new
        /// key, so no <c>TuneVersion</c> gate.</summary>
        public double ExtractionUpgradeRateRatio = 2.0;

        /// <summary>How long, in seconds, a drill-quality upgrade that vanished without a build waits
        /// before its (hub, ore) may post again. An upgrade is expected to show up as a build entry,
        /// a live demand or an idle hull by the next Shipyard pass; one that does not was answered or
        /// dropped some other way, and re-posting it every pass only repeats that. 1800 is ten
        /// Shipyard passes. 0: no backoff. A new key, so no <c>TuneVersion</c> gate.</summary>
        public double ExtractionUpgradeRetrySeconds = 1800.0;

        /// <summary>How long, in seconds, a deposit-bound (hub, ore) keeps the <c>SinceUtc</c> of its
        /// ground signal while it is not signalled (<see cref="ExtractionCapacity.DeriveGroundSignals"/>).
        /// A pass where an in-flight trip covers the target, the bound reads none, the order is absent
        /// or paused drops the signal for that pass; a gap no longer than this keeps its first
        /// <c>since</c>. 1800 bridges the longest gap measured live on 2026-09-27 (~27 min) with one
        /// Operations pass to spare. 0: <c>since</c> resets after one pass off, as slice 3 shipped. A new
        /// key, so no <c>TuneVersion</c> gate.</summary>
        public double ExtractionGroundGraceSeconds = 1800.0;

        /// <summary>Litres of ore per cubic metre of rock removed, per ore -- SE's own
        /// <c>MinedOreRatio</c>, keyed by inventory SubTypeId (<c>Ore:Iron</c>). Multiplies the drill
        /// rate, composition-weighted across what a dwell is targeting, so a basket of platinum
        /// genuinely takes longer per litre delivered than one of iron.
        ///
        /// <para>Replaces <see cref="FreeMintScale"/> in the extraction-rate path (FAC-228). That
        /// table is the PASSIVE-income mint weight and had no business setting a drill's speed; worse,
        /// its <c>RareOre 0.1</c> against <c>Ore 2.0</c> imposed a 20x penalty on rare baskets where
        /// SE's own numbers say 5x (Iron 5, Gold/Platinum/Silver 1) and 16.7x only for Uranium.</para>
        ///
        /// <para>Values are read from vanilla <c>VoxelMaterials*.sbc</c>, taking the ore-bearing ROCK
        /// material: <c>Stone_01..05</c> and <c>Ice_01..03</c> are 5, while the 1s and 2s sharing
        /// those ore names are soil, grass, sand and snow surfaces. A modded ore absent from this
        /// table falls back to <see cref="DefaultOreDensityRatio"/>.</para>
        ///
        /// <para><b>The fallback, not the source.</b> A deposit surveyed in current units carries each
        /// material's own ratio, and <see cref="MineTargeting.OreDensityAt"/> reads that first; this
        /// table answers only for a deposit measured before it was.</para></summary>
        public Dictionary<string, double> OreDensityRatio;

        /// <summary>Fallback for an ore with no <see cref="OreDensityRatio"/> row -- a modded ore, or
        /// a table that failed to load. 1.0 is SE's own value for the precious ores rather than a
        /// neutral-looking guess, so an unknown ore mines like gold rather than like iron.</summary>
        public double DefaultOreDensityRatio = 1.0;

        /// <summary>Superseded by <see cref="MineDutyCycle"/> (FAC-228) and read by nothing. It was a
        /// second scalar on the same rate, shipped in v0.127.0 to make mining 4x faster before the
        /// model underneath it was re-derived. Two multiplicative knobs doing one job is the shape
        /// this ticket exists to remove, so the duty cycle -- which at least names a physical
        /// quantity -- is the one that survived. Kept only so a stale key in an existing tuning.json
        /// is ignored rather than erroring.</summary>
        public double MineRateMultiplier = 4.0;

        /// <summary>Share of <see cref="FreeMinerCargoVolumeLiters"/> one bootstrap Mine mint
        /// delivers to the destination when the faction owns no Mine-suitable hull anywhere. 0
        /// disables the fallback entirely, leaving a drill-less faction's ore demand permanently
        /// blocked on <c>no_ship_anywhere</c>.
        ///
        /// <para>Sized off the free-miner hold rather than any real hull's, because the faction this
        /// fires for has no hull to read a capacity from. Half of one free-miner load per source per
        /// <see cref="MineBootstrapCooldownSeconds"/> is deliberately meagre: enough iron to feed a
        /// refinery and finish the Mine hull already at the head of the shipyard queue, not enough to
        /// be a substitute for owning one.</para>
        ///
        /// <para>Purely additive vs the current TuneVersion, so no bump needed -- a tuning.json
        /// lacking the key keeps this inline default.</para></summary>
        public double MineBootstrapYieldFraction = 0.5;

        /// <summary>Cooldown stamped on the SOURCE deposit after a bootstrap Mine mint, in seconds --
        /// what rate-limits the fallback, since <c>OperationsManager.TryFireMine</c> refuses a
        /// cooling node. Not derived from a dwell the way <see cref="MineCooldownMultiplier"/>
        /// derives a real trip's cooldown, because no hull flew and there is no dwell to scale.
        ///
        /// <para>Purely additive vs the current TuneVersion, so no bump needed.</para></summary>
        public double MineBootstrapCooldownSeconds = 1800;

        /// <summary>How many sim ticks between persist+snapshot flushes. Default 50 = ~8.3 min at 0.1 tick/s.
        /// Superseded by FlushEveryRealSeconds in the wall-clock scheduler; retained for compatibility.</summary>
        public int FlushEverySimTicks;          // 50 @ 0.1 tick/s Ôëê 8.3 real min

        // ---- per-faction wall-clock tick scheduling (replaces global brain+economy cadence) ------
        /// <summary>Real-time seconds between each faction's combined economy+brain tick.
        /// One faction fires per Update() frame; at 60 fps and 13 factions that is ~1 tick every 4.6 s per faction.
        /// Default 60 s so each faction thinks and earns once per real minute.</summary>
        public double FactionTickIntervalSeconds;   // 60.0 default

        /// <summary>Real-time seconds between global passes: BuildSnapshot + DecayInfluence + ResolveArrivals + diplomacy step.
        /// Default 30 s. Should be Ôëñ FactionTickIntervalSeconds to keep the snapshot fresh.</summary>
        /// <summary>How often fleet arrivals are resolved. Independent of the global pass: arrival
        /// resolution reads no influence data, so it must not inherit the economy's cadence.</summary>
        public double ArrivalsIntervalSeconds;      // 10.0 default

        /// <summary>Lifetime a demand or investment posted by a manager on <paramref name="intervalSeconds"/>
        /// must have. It has to outlive the gap to the next sweep or the register is empty when the
        /// consumer looks; the x2 means one missed sweep still does not drop it. StoreStockManager has
        /// always derived its TTL this way -- this is that rule made shared rather than local.</summary>
        public static TimeSpan PostingTtlFor(double intervalSeconds, double floorSeconds)
        {
            double byInterval = intervalSeconds > 0 ? intervalSeconds * 2.0 : 0.0;
            return TimeSpan.FromSeconds(Math.Max(floorSeconds, byInterval));
        }

        /// <summary>The longest cadence among the managers that can post a shared cross-manager
        /// investment, so its lifetime covers whichever one actually posted it.</summary>
        public TimeSpan SharedPostingTtl(double floorSeconds)
        {
            double widest = OpsIntervalSeconds;
            if (TradeIntervalSeconds > widest) widest = TradeIntervalSeconds;
            if (ShipyardIntervalSeconds > widest) widest = ShipyardIntervalSeconds;
            if (FleetIntervalSeconds > widest) widest = FleetIntervalSeconds;
            return PostingTtlFor(widest, floorSeconds);
        }


        public double GlobalPassIntervalSeconds;    // 30.0 default

        /// <summary>Length in real-time seconds of one performance-telemetry window: the period over
        /// which per-actor call counts and timings accumulate before <c>perf.json</c> and the `perf`
        /// log line are written and the counters reset. Default 60 s.
        /// <para>A window shorter than the widest actor cadence leaves that actor with zero calls in
        /// most windows, so raising this to at least the largest of the manager intervals makes every
        /// row report on every window.</para></summary>
        public double PerfWindowSeconds;            // 60.0 default

        /// <summary>Real-time seconds between persist+snapshot flushes. Replaces FlushEverySimTicks.
        /// Default 30 s so the persisted dashboard/API snapshot remains current.</summary>
        public double FlushEveryRealSeconds;        // 30.0 default

        /// <summary>Soft time budget in milliseconds for one Update() frame's manager/global-pass/
        /// faction-tick work. A manager capable of bounded resumable work (see ProductionManager)
        /// must stop advancing and yield to a later frame once a FrameDeadline built from this value
        /// expires, rather than exceeding it. Default 0.40 ms.</summary>
        public double FrameSoftBudgetMilliseconds;  // 0.40 default

        /// <summary>Master switch for the shared frame scheduler. When false the host falls back to
        /// the legacy four-drain sequence verbatim. Kept only until the scheduler has soaked; see
        /// docs/todo.md for the dated removal.</summary>
        public bool FrameSchedulerEnabled = true;

        /// <summary>When false, the scheduler admits every due actor as if it were the frame's first
        /// -- never denying one for want of budget. Reproduces the previous "each actor gets its own
        /// shot" behaviour through the same code path, and is the surgical rollback if the shared
        /// budget is found starving something live.
        /// <para>The restructure shipped and was verified separately first, so a regression from here
        /// is attributable to the budget rather than to the wiring.</para></summary>
        public bool FrameSharedBudgetEnabled = true;

        /// <summary>Hard cap on admissions per frame, guarding against a run of very cheap actors
        /// monopolising one. 0 uses the scheduler's own default.</summary>
        public int FrameMaxUnitsPerFrame;

        // ---- per-manager independent intervals ----------------------------------------------------
        public double ProductionIntervalSeconds;    // 30.0 default
        public double OpsIntervalSeconds;           // 10.0 default
        public double ShipyardIntervalSeconds;      // 60.0 default
        public double FleetIntervalSeconds;         // 60.0 default -- Reinforce/Patrol dispatch only.
        // NOT player observation: that is UpdateObservation/UpdateStationObservation/
        // UpdateAsteroidFieldObservation, which run outside this manager entirely. The old
        // "proximity triggers must stay fast" note described the Position:-demand Ferry path,
        // removed when Haul/Trade started dispatching Pickup legs directly. Hold decays per
        // HOUR, so sub-minute evaluation was never doing anything.
        public double TradeIntervalSeconds;         // 60.0 default
        public double StoreRefreshIntervalSeconds;  // 120.0 default

        // insolvency / lifecycle (the private ledger has a floor; a fully-collapsed faction is conquered -> dormant)
        /// <summary>Suppress vanilla SE's per-economy-tick faction-balance recovery (FAC-187).
        /// <c>MySessionComponentEconomy.UpdateUpdateFactionBalance</c> pulls every station-owning
        /// faction 40% of the way back to <c>PerFactionInitialCurrency</c> (1e9) on each tick, which
        /// fights <see cref="WorldModel"/>'s treasury mirror and strands any faction the mirror does
        /// not re-push. Host-only: enforced by a Harmony prefix, so the offline sim ignores it.
        /// Set false to restore vanilla behaviour without a rebuild.</summary>
        public bool SuppressVanillaFactionBalanceRecovery = true;
        public long SeedTreasury;      // one-time starting capital (granted at registration)
        public long InsolvencyFloor;   // hard floor on the ledger ÔÇö a faction may run ~one ReserveTreasury into "debt"

        // central bank / money-supply targeting
        public double CentralBankReserveRatio = 0.4; // target money supply as a fraction of world collateral value
        public double CentralBankMaxAdjustmentFractionPerTick = 0.01; // reserved for later slices: max fractional move toward target per economy quantum
        public long CentralBankTerritoryNodeValue = 250000; // collateral contribution of any owned node
        public long CentralBankFoundedNodeValue = 500000; // additional collateral contribution of a founded / physical-station node
        public long CentralBankCapabilityLevelValue = 100000; // collateral contribution per installed capability level
        public long CentralBankShipValue = 125000; // fallback collateral per hull whose prefab has no catalogue Cost; a priced hull counts at its Cost
        public double CentralBankSupportDebtFraction = 0.25; // max outstanding debt as a fraction of faction collateral for rescue lending
        public long CentralBankSupportLoanAmount = 250000; // max credits issued per rescue-loan event
        public long CentralBankSupportTreasuryBuffer = 50000; // working-cash treasury target after a rescue loan
        public double CentralBankSupportRunwayTicks = 12.0; // when bleeding, target at least this many economy quanta of runway
        public TimeSpan CentralBankSupportCooldown = TimeSpan.FromMinutes(2); // minimum time between rescue loans to the same faction
        public double CentralBankAssetPurchasePriceMultiplier = 1.0; // policy price multiplier for non-debt processed-good purchases
        public long CentralBankGrantAmount = 1000000; // max credits issued per grant event
        public long CentralBankGrantTreasuryBuffer = 75000; // treasury target after a grant
        public double CentralBankGrantRunwayTicks = 12.0; // when bleeding, target at least this many economy quanta of runway
        public double CentralBankGrantCollateralFraction = 0.02; // grant treasury target floor as a fraction of collateral
        public double CentralBankGrantMemberWealthFraction = 1.0; // purely additive vs the current TuneVersion, so no bump. Share of a player faction's members' own wallet balances counted as faction liquidity against its grant target; 0 ignores them
        public TimeSpan CentralBankGrantCooldown = TimeSpan.FromMinutes(1); // minimum time between grants to the same faction
        // Lending-standard gate on new support-loan issuance: a bank shouldn't keep extending debt once
        // the resulting interest would outrun what the faction actually earns. Periods are expressed in
        // AccrueIncome passes so the EMA's memory scales automatically with whatever cadence interest
        // itself accrues at, rather than an independent wall-clock timer.
        public double CentralBankIncomeEmaPeriods = 15.0; // memory length (in AccrueIncome passes) for FactionState.CentralBankRecentIncome
        public double CentralBankDebtServiceIncomeCapFraction = 0.5; // max fraction of smoothed real income that a new support loan's resulting interest may consume
        /// <summary>How fast a fall in world collateral reaches the money-supply target. The target is
        /// set against an anchor that follows collateral up at once and down only by this half-life, so
        /// hulls or stations lost in a war and rebuilt within a few half-lives never register as excess
        /// money and never trigger a levy. Zero or negative: the anchor is the instantaneous collateral
        /// (the behaviour before the anchor existed).</summary>
        public TimeSpan CentralBankAnchorHalfLife = TimeSpan.FromHours(6);
        /// <summary>Tolerance above target before the bank contracts, as a fraction of target. Money
        /// within <c>target x (1 + band)</c> is left alone; above it the levy is sized against the
        /// excess beyond the band edge, not against the target itself.</summary>
        public double CentralBankContractionBand = 0.05;

        // conflict & diplomacy
        // Per-arrival effect values are scaled to 1/50 of their original defaults to compensate for
        // the 50├ù fleet-speed increase (FleetSpeedKmPerMin 1 ÔåÆ 50). Continuous diplomacy values run
        // once per economy quantum; dispatch costs remain per action.
        public long RaidCost;                  // fielding a raiding force against a rival source (dispatch cost ÔÇö unchanged)
        /// <summary>Hold stripped from a raided source by a raid that arrives intact, and the
        /// curve that scales that down as the raid arrives more damaged. See
        /// <see cref="RaidHoldStrip"/>.</summary>
        public double RaidHoldStripMax = 0.35;
        public double RaidHoldStripExponent = 1.5;
        public double RaidThreat;              // threat added to the raided source
        public double RaidReachThreshold;      // min influence we must project at a target to strike it
        /// <summary>Fraction of the gap to the drift target closed each time <c>Diplomacy.Drift</c>
        /// runs — which is once per <b>global pass</b> (<c>GlobalPassPhase.DriftingDiplomacy</c>,
        /// <see cref="GlobalPassIntervalSeconds"/>), NOT once per economy quantum. At the shipped
        /// 0.1 and a 180 s pass that is a ~20 minute half-life, so a change to what the target is
        /// takes about an hour to be visible live.</summary>
        public double RelationDriftRate;
        public double RaidRelationHit;         // how hard being raided sours the victim's standing
        public double HostileRelationThreshold; // standing below this = hostile (a valid raid target)
        public double AllyRelationThreshold;   // standing above which ProposePact candidates are generated
        public TimeSpan PactOfferTtl;          // how long an unanswered alliance proposal stays on the table
        /// <summary>Standing the owner of a defended node gains toward an ally whose Guard destroys
        /// a raid on it. Only a pact partner's Guard earns it, and only on an intercept — a defence
        /// that fails moves nothing. Sized to match <see cref="RaidRelationHit"/> so one exercised
        /// pact offsets one raid, which is what makes an alliance self-reinforcing rather than a
        /// standing cost. <c>&lt;= 0</c> disables the lift.
        /// <para>Provenance: FAC-276.</para></summary>
        public double PactDefenceRelationBoost;

        // ---- player reputation (FAC-184) ---------------------------------------------------------
        // The two-level model in docs/player-progression.md -> Reputation and standing. Every key
        // here is NEW, so each survives on its C# initialiser and none needs a TuneVersion gate --
        // an existing tuning.json simply lacks them. Real values belong in tuning_defaults.json.

        /// <summary>Weighted trade value one reputation step costs. The accumulator carries the
        /// remainder (see <see cref="TradeReputationLedger"/>), so splitting one large trade into
        /// several buys nothing. <c>&lt;= 0</c> disables trade-driven reputation entirely.
        /// <para><b>This is the magnitude knob for trade-driven reputation, and the only one that can
        /// hold an arbitrary rate.</b> What a player actually earns is
        /// <see cref="TradeReputationStep"/> ÷ this, and the step is an <c>int</c> — so a rate change
        /// that is not a whole-number factor of 25 has to land here. Operator, 2026-08-30: take the
        /// trade boost to 5% of what it was, 50,000 → 1,000,000, leaving the step and its granularity
        /// alone.</para></summary>
        public double TradeReputationThreshold = 1000000.0;
        /// <summary>Multiplier on trade value when the player SUPPLIES a station (they sell, the
        /// faction pays). Provision is meant to outweigh consumption: bringing a faction what it
        /// lacks is worth more standing than buying what it has spare.</summary>
        public double TradeProvisionWeight = 1.0;
        /// <summary>Multiplier on trade value when the player DRAWS DOWN a station (they buy).</summary>
        public double TradeConsumptionWeight = 0.35;
        /// <summary>SE reputation points granted per threshold crossed. On the vanilla scale
        /// (-1500..1500) 25 makes a full neutral-to-friendly climb roughly twenty threshold crossings,
        /// so standing is earned over a trading relationship rather than in one delivery.
        /// <para>Unchanged by the 2026-08-30 rate cut: twenty crossings is a statement about
        /// granularity, and it still holds. What changed is what a crossing costs — see
        /// <see cref="TradeReputationThreshold"/>.</para></summary>
        public int TradeReputationStep = 25;
        /// <summary>Days after which a (player, faction) trade accumulator with no activity is
        /// dropped. Bounds the file; costs at most a partial step. <c>&lt;= 0</c> keeps everything.</summary>
        public double TradeReputationRetentionDays = 90.0;
        /// <summary>Share of the member-standing aggregate taken from the roster mean; the rest comes
        /// from its worst member (see <see cref="MemberStandingAggregate.TryCompute"/>). 0 judges a
        /// faction purely by its worst conduct, 1 purely by its average.</summary>
        public double MemberStandingAlpha = 0.5;
        /// <summary>Days over which an absent member's contribution to the aggregate halves, measured
        /// against the roster's most recently active member. <c>&lt;= 0</c> weights everyone equally.</summary>
        public double MemberStandingHalfLifeDays = 14.0;
        /// <summary>How hard the member-standing aggregate pulls an NPC faction's drift target toward
        /// itself, beside border pressure. 0 disables the term, leaving player-faction drift identical
        /// to NPC-vs-NPC drift. Only ever applied to a player-faction target.</summary>
        public double MemberStandingDriftWeight = 0.6;

        // ---- member reputation display (FAC-394 slice 4) ------------------------------------------
        // docs/diplomacy-plan.md rule 4: while a player is a member of a registered player faction,
        // SE's personal-reputation field shows each managed NPC's standing toward the faction, and
        // the member's own record lives in member_reputation.json. All three keys are NEW.

        /// <summary>Master switch for the projection. Off writes every stored record back into SE's
        /// field on the next economy quantum and stops projecting — the kill switch, not a pause.</summary>
        public bool MemberReputationSyncEnabled = true;
        /// <summary>Smallest move of a member's projected field worth a write, in SE reputation
        /// points. Below 1 reads as 1.</summary>
        public int MemberReputationSyncMinDelta = 1;
        /// <summary>SE field writes one economy quantum may make across every member and managed NPC.
        /// Each write raises a replicated event, so a large roster is spread over several quanta
        /// rather than landing in one frame. <c>&lt;= 0</c> is unbounded.</summary>
        public int MemberReputationSyncMaxWritesPerPass = 256;

        // conquer — territorial capture, the finishing move after Raid/Retaliate softening.
        public long ConquerCost;                       // fielding a conquest force — a much bigger commitment than RaidCost
        public double ConquerHoldCeiling;              // node HoldStrength must be at/below this to be conquerable outright
        public double ConquerCapturedHoldStrength;     // HoldStrength a captured node starts at under its new owner
        public double ConquerCapturedThreat;           // residual unrest Threat a freshly-captured node starts at (not 0)
        public double ConquerRelationHit;              // how hard being conquered sours the victim's standing (harsher than RaidRelationHit)
        public double ConquerFatigue;                  // extra fatigue both sides gain per conquest exchanged (harsher than RaidFatigue)
        public double ConquerTruceBreachQualityPenalty; // score multiplier for a Conquer candidate targeting an active truce partner (harsher/lower than TruceBreachQualityPenalty)
        public int ConquerBurstSize;                   // ships committed per conquest strike (a single decisive strike, not a raiding convoy)
        public TimeSpan ConquerCooldown;                // per-faction cooldown after a Conquer dispatch

        /// <summary>Hold a landed raid strips from its target, from the attacker's surviving HP
        /// fraction after the arrival duel (<c>PendingFleetAction.ForcedRatio</c>). The duel has
        /// already weighed the squadron's DPS against the node's guard bands, station bands and
        /// <see cref="StationDefensePower"/>, so this is the only place raid damage is sized — a
        /// second power model beside it would drift from the fight that was actually resolved.
        ///
        /// <para>At the shipped 0.35 / 1.5 a raid that arrives intact (ratio 1.00) strips 0.35, so
        /// three take a full-hold node to <see cref="ConquerHoldCeiling"/>; a mauled raid
        /// (ratio 0.30) strips 0.06. A non-positive ratio strips nothing.</para></summary>
        public double RaidHoldStrip(double survivingRatio)
        {
            if (survivingRatio <= 0) return 0;
            return RaidHoldStripMax * Math.Pow(MathUtil.Clamp01(survivingRatio), RaidHoldStripExponent);
        }

        // trade (peaceful interaction; negative actions are remembered ~10├ù longer than positive ones)
        public long TradeCost;                 // small outlay ÔÇö sending goods / a trade convoy
        public double TradeMaxIncomeFractionPerWindow = 0.3; // max share of CentralBankRecentIncome spendable on trade dispatch per SpendBudgetWindowSeconds, on top of the floor below
        public long TradeBudgetFloor = 60; // income-independent minimum trade budget per window (= TradeCost); both this and the fraction above <=0 = uncapped

        // ---- station buy-order budget (FAC-159) --------------------------------------------------
        /// <summary>Share of <c>CentralBankRecentIncome</c> a faction may commit across ALL of one
        /// station's buy orders per <see cref="SpendBudgetWindowSeconds"/>. A station store had no
        /// balance term at all: quantity was <c>min(demand headroom, listingCap - stock)</c> and price
        /// <c>base x scarcity</c>, so ISBT N007 advertised 999,487 Ore:Stone at 30 — 30,000,000
        /// credits against a 350,000 treasury.
        ///
        /// <para>New keys, so no <c>TuneVersion</c> gate; both carry inline initialisers. Setting the
        /// fraction and the floor both <c>&lt;= 0</c> is the rollback lever, matching
        /// <see cref="SpendBudget"/>'s own uncapped contract.</para></summary>
        public double StationBuyMaxIncomeFractionPerWindow = 0.3;

        /// <summary>Income-independent minimum buy budget per window, so a station with no recent
        /// income can still stock a small order rather than listing nothing at all.</summary>
        public long StationBuyBudgetFloor = 50000;

        /// <summary>Share of treasury surplus above the operational reserve
        /// (<see cref="Economy.SpendableTreasurySurplus"/>) that may fund buy orders, on top of
        /// income + floor. Default 0: like Patrol and Trade dispatch, letting a store draw on stored
        /// wealth is a deliberate live-tuning decision rather than a default.</summary>
        public double StationBuyWealthFractionPerWindow = 0.0;

        // ---- Services Terminal settlement --------------------------------------------------------
        /// <summary>Master switch for diverting the vanilla Services Terminal's three money flows -
        /// salvage payout, repair fee, grid-storage expedite fee - into the faction ledger and the
        /// node inventory. Off restores vanilla: credits are minted to or burned from the player's
        /// wallet with no counterparty, and a salvaged wreck's parts simply cease to exist.
        /// <para>New key, so no <c>TuneVersion</c> gate and no version-gated default.</para></summary>
        public bool ServicesTerminalSettlement = true;

        /// <summary>Share of a salvaged wreck's <b>present</b> components (and the cargo aboard it)
        /// that lands in the yard's node inventory, 0..1. Vanilla pays the seller a fraction of the
        /// hull's minimal price and deletes the hull; this is the other half of that trade, and the
        /// balancing lever on it. 1.0 recovers everything the wreck still carried, which is what the
        /// payout was priced against. Rounded down per inventory key.</summary>
        public double SalvageRecoveryFraction = 1.0;

        /// <summary>Master switch for Refuel and Rearm at a Services Terminal: a ship docked to the
        /// station by a connector buys hydrogen, oxygen and ammunition out of the node's own stock,
        /// at the station's store price and down to the store's own offer floor. Off, the server
        /// refuses every request with a sentence the player sees; the buttons stay drawn because the
        /// client cannot know.
        /// <para>New key, so no <c>TuneVersion</c> gate and no version-gated default.</para></summary>
        public bool ServicesTerminalDockSupply = true;

        /// <summary>Seconds between two "trade refused" chat messages to the same player about the
        /// same station owner. The refusal at a barred station's store block or Services Terminal is
        /// not rate-limited, only the telling: a player clicking Buy repeatedly is told once per
        /// window. 0 tells on every refusal. docs/diplomacy-plan.md, rule 2.
        /// <para>New key, so no <c>TuneVersion</c> gate and no version-gated default.</para></summary>
        public double StoreRefusalNoticeCooldownSeconds = 30.0;

        // ---- debris absorption -------------------------------------------------------------------
        // Four new keys, so each survives on its C# initialiser and none needs a TuneVersion gate --
        // a tuning.json that has never heard of them overrides nothing.

        /// <summary>Whether a node periodically reclaims the orphaned wreckage lying around it.
        /// Off leaves every grid to SE's own trash collector.</summary>
        public bool DebrisAbsorbEnabled = true;

        /// <summary>Seconds between two debris sweeps. Long on purpose: the gap is the window in
        /// which a player can grind the same wreckage themselves, so shortening it takes salvage
        /// away from players rather than making the world tidier.</summary>
        public double DebrisAbsorbIntervalSeconds = 3600.0;

        /// <summary>How far from a founded node a wreck may lie and still be reclaimed by it, km.
        /// A wreck outside every node's reach is left to SE.</summary>
        public double DebrisAbsorbRadiusKm = 5.0;

        /// <summary>Ceiling on grids closed in one sweep, across the whole estate. The sweep is
        /// hourly rather than per-frame, so this bounds the single frame it runs in; a backlog
        /// larger than the cap simply drains over the following sweeps.</summary>
        public int DebrisAbsorbMaxGridsPerPass = 25;

        // ---- the grid reaper ---------------------------------------------------------------------
        // Four new keys, so each survives on its C# initialiser and none needs a TuneVersion gate.

        /// <summary>Whether the simulation closes the grids it put in the world once nothing holds
        /// them. Off leaves every marked grid standing for the life of the save.</summary>
        public bool GridReapEnabled = true;

        /// <summary>Seconds between two reaper passes. Short compared with the debris sweep, because
        /// this reclaims nothing and offers players nothing: a subgrid left behind by a despawn is
        /// simulation load from the frame it is orphaned.</summary>
        public double GridReapIntervalSeconds = 300.0;

        /// <summary>How long a marked grid is left alone after it was marked, while a player is close
        /// enough to be watching it. A missile in a fight is content, not litter; with nobody in
        /// range there is nothing for the grace to protect and it does not apply.</summary>
        public double GridReapGraceSeconds = 600.0;

        /// <summary>Ceiling on grids closed in one pass, across the whole world. A backlog larger
        /// than the cap drains over the following passes.</summary>
        public int GridReapMaxGridsPerPass = 50;

        // ---- the scarcity value gate (FAC-159 slice 2) --------------------------------------------

        // ---- contract reward budget (FAC-156) ----------------------------------------------------
        /// <summary>Share of <c>CentralBankRecentIncome</c> a faction may promise across all the
        /// item-value contracts one station offers per <see cref="SpendBudgetWindowSeconds"/>.
        /// Item-value contracts (`ObtainAndDeliver`, `BulkAcquisition`, `Haul`) had **no**
        /// affordability term — reward is <c>round(basePrice x scarcity) x amount</c>, and the only
        /// treasury check in `FactionContractStrategy` gates the Reinforce type.
        ///
        /// <para>New keys, no <c>TuneVersion</c> gate, inline initialisers. Fraction and floor both
        /// <c>&lt;= 0</c> is the rollback lever, matching <see cref="SpendBudget"/>'s uncapped
        /// contract.</para></summary>
        public double ContractRewardMaxIncomeFractionPerWindow = 0.3;

        /// <summary>Income-independent minimum contract-reward budget per window, so a faction with
        /// no recent income can still post a small contract rather than none at all.</summary>
        public long ContractRewardBudgetFloor = 50000;

        /// <summary>Share of treasury surplus above the operational reserve that may fund contract
        /// rewards. Default 0, like Patrol and Trade dispatch.</summary>
        public double ContractRewardWealthFractionPerWindow = 0.0;

        /// <summary>Fraction of one window's budget any single **contract** offer may promise.
        /// SE asks for one contract at a time, so there is no batch to allocate across; this is what
        /// stops the first contract of a tick consuming the window and refusing every later one —
        /// live 2026-08-29 every skipped contract read <c>budget=0</c> for exactly that reason
        /// (FAC-160). Read by <c>FactionContractStrategy.ContractRewardSliceFor</c>.
        ///
        /// <para><b>No longer applies to store buy rows.</b> Those are sized independently against
        /// the whole budget (operator, 2026-08-30 — rows are advertisements, not reservations), so
        /// there is no shared pot left for a share to be a share *of*. It governed both until then,
        /// and the pairing was the FAC-176 defect: against a <c>BudgetLowerTierReserve</c> of 0.25
        /// this cap was measured on the whole budget, so at any value >= the reserve it could not
        /// bind on the tier it existed to govern.</para>
        ///
        /// <para>Values at or below 0, or at 1.0, disable the cap — a mis-set key must not silently
        /// zero every contract.</para></summary>
        public double BudgetMaxSharePerRow = 0.5;

        // ---- scarcity time ramp (FAC-161) --------------------------------------------------------
        /// <summary>Ore units a region must hold, with no refining installed in it, before the
        /// throughput stage asks for a Refinery THERE (a region-scoped <see cref="InvestmentRequest"/>).
        /// Below it a region is not hoarding, only holding. 50,000 is a few hours of one RefineryII's
        /// feed; FCTM's far nests sat at 4-6 million each on 2026-09-05 (inline default: a missing JSON
        /// key must not zero it -- 0 would make every founded region ask; purely additive vs the
        /// current TuneVersion, so no bump needed).</summary>
        public long RegionOreHoardFloor = 50000;
        /// <summary>The share of the faction's ore a region must hold, on top of
        /// <see cref="RegionOreHoardFloor"/>, before it is a hoard worth a refinery of its own. Read
        /// live on the first pass of v0.174.0: IGSL installed a Refinery for a three-node surface
        /// region holding 55,000 ore -- 0.2% of its estate -- because the absolute floor alone
        /// admitted it and any standing request outbids exploratory development. 0.05 keeps the far
        /// nests (0.10-0.27) and drops that (inline default: missing key must not zero it; purely
        /// additive, no TuneVersion bump).</summary>
        public double RegionOreHoardMinShare = 0.05;

        /// <summary>Weights how much of an item a station puts on its **sell** shelf by how short of
        /// it the node is itself (FAC-162). Offers were `min(stock, cap)` — the whole stock — so ISBT
        /// N007 listed all 558 kg of the `Ore:Iron` its own refinery needed while holding zero
        /// `Ingot:Iron` and posting `REFINE_DEMAND` for it.
        ///
        /// <para>The **price** already carried the need signal (`base x ScarcityMultiplier`; that iron
        /// listed at 1.88x base while glutted ice sat at the 0.5 floor). Only the quantity ignored it.
        /// Weighted rather than blocked, per operator direction — *"some sales is acceptable"*.</para>
        ///
        /// <para>Defaults **true**: this is a defect fix, not an economic lever, and the flag exists
        /// only as a rollback. New key, no <c>TuneVersion</c> gate.</para></summary>
        public bool OffersNeedWeighted = true;

        /// <summary>Serve station buy orders for ore ahead of every other row, whatever their
        /// relative need scores. Selling ore is the one thing a player can do from the moment they
        /// spawn, so a station holding any ore need must surface it rather than lose the credit
        /// window to manufactured goods, whose reference stocks give them the larger shortfall score
        /// nearly always (operator, 2026-08-29).</summary>
        public bool StationOreOrderPriority = true;
        public double TradeRelationBoost;      // standing gain per trade for the INITIATOR
        public double TradeRelationBoostRecipient; // small warmth the receiving faction gains per trade arrival
        public double TradeReachThreshold;     // min influence projected at a neighbour's source to trade

        /// <summary>Minimum destination scarcity (IPriceOracle.ScarcityMultiplier) an item must have to be
        /// sold at Trade settlement. At or below this floor the destination is considered genuinely
        /// oversupplied on that item, so it isn't force-sold — it rides back to origin instead. Mirrors
        /// CentralBankBuyScarcityFloor's "genuine oversupply" convention (same default, separate field
        /// since the two gate different mechanisms).</summary>
        public double TradeScarcityFloor = 0.50;

        /// <summary>Dead config: nothing reads this. The acquisition direction of Trade is no
        /// longer a switchable mode — it is the buy walk of the sweep, and what decides whether a
        /// faction flies it is whether it owns a suitable hull. Kept only so a persisted tuning.json
        /// carrying the key is not surprising; a removal is a separate cleanup.</summary>
        public bool ProcurementEnabled = true;
        /// <summary>Maximum scarcity (IPriceOracle.ScarcityMultiplier) an item may have at the
        /// *seller's* node for that seller to part with it to an inbound procurement convoy. Above
        /// this the seller needs it themselves and refuses. The exact mirror of
        /// <see cref="TradeScarcityFloor"/>, which stops a sell convoy force-feeding a destination
        /// that is already oversupplied — same "genuine oversupply" convention and same default,
        /// separate field because the two gate opposite sides of the transaction.</summary>
        public double ProcurementSellerScarcityCeiling = 0.50;

        /// <summary>A demand row is offered to trade partners when its owner's own estate covers less
        /// than this share of the shortfall, and what is offered is the remainder. 0 offers only rows
        /// nothing internal could touch. Operator, 2026-09-26: 0.5. A faction mining ice in small lots
        /// at many nodes had every depot's ice want part-matched by a trickle and never offered to the
        /// faction holding 569 M of it (FAC-109). Purely additive vs the current TuneVersion.</summary>
        public double UnservedPublishCoverage = 0.5;

        /// <summary>Whether a node sells stock that none of its installed capabilities uses (no
        /// reference stock, so the oracle has no opinion) and that it has no open <c>Item:</c> row for.
        /// False restores the old refusal (<see cref="TradeConsent"/>). Purely additive vs the current
        /// TuneVersion.</summary>
        public bool TradeSellUnusedStock = true;

        /// <summary>Max share of a recipient's CentralBankRecentIncome that can be charged against them
        /// by incoming Trade settlements per SpendBudgetWindowSeconds, on top of the floor below. Shared
        /// across every sender trading with them in the same window — protects the recipient's own
        /// spending capacity from a trade flood, same shape as ReinforceMaxIncomeFractionPerWindow.</summary>
        public double TradeInboundMaxIncomeFractionPerWindow = 0.3;
        /// <summary>Income-independent minimum inbound trade budget per window; both this and the
        /// fraction above &lt;=0 = uncapped.</summary>
        public long TradeInboundBudgetFloor = 50000;

        /// <summary>Share of treasury surplus above the operational reserve
        /// (<see cref="Economy.SpendableTreasurySurplus"/>) that a recipient may spend on arriving
        /// trade cargo per window, on top of income + floor.
        ///
        /// <para><b>Unlike the other wealth fractions this one defaults ON</b>, because the inbound
        /// budget is the only one of the five whose call sites did not pass a wealth term at all —
        /// a cash-rich faction could commit nothing but income to goods it had itself asked for.
        /// Live 2026-09-12: the refused orders ran p50 8.9M, p90 66M, max 223M credits against
        /// window budgets of ~1.2M for the richest factions, so a median order was seven times what
        /// its buyer was allowed to pay and 138 of 235 settlements refused on the window.</para>
        ///
        /// <para><b>The rising share the operator asked for needs no curve.</b> The allowance is a
        /// flat fraction of the surplus, and the surplus is treasury less the reserve — so as a
        /// share of <em>treasury</em> it starts at zero on the reserve and climbs toward this
        /// fraction as the faction gets richer. At the live 50,000,000 reserve a faction holding
        /// 100M commits half of this fraction and one holding 2.4B commits 98% of it. A faction
        /// below the reserve gets a surplus of 0 and is unaffected, which is correct: it cannot
        /// afford the order.</para>
        ///
        /// <para>0.03 is sized from those measurements rather than chosen: it covers a p90 order for
        /// a faction in the low billions (EXP 2.36B → ~69M per window). It deliberately does not
        /// cover the 223M maximum — the window still exists to stop one partner flooding another,
        /// and this lifts it to the size of a real order rather than removing it. Hot-reloadable.</para></summary>
        public double TradeInboundWealthFractionPerWindow = 0.03;

        // FinishBuild force-consolidation (P1 build-completion urgency) -- credit cost per total BOM
        // unit, so a bigger stalled build costs more to force through. No inline default (same
        // convention as DevelopCost/TradeCost/GiftCost) -- real value lives in tuning_defaults.json.
        public double FinishBuildCostPerBomUnit;

        // gift / reparation (Security/Caution action ÔÇö high-cost goodwill gesture; lifts RECIPIENT's view of sender)
        public long GiftCost;                  // expensive: ~167├ù a trade convoy
        public double GiftRelationBoost;       // large one-time lift to recipient's standing toward sender
        public TimeSpan GiftDestCooldown; // before gifting the same faction again

        // war fatigue & diplomacy (sustained war tires BOTH sides -> a negotiated truce resolves the stalemate)
        public double WarFatigueRate;      // fatigue gained per economy quantum while actively hostile
        public double RaidFatigue;         // extra fatigue both sides gain per raid exchanged
        public double WarFatigueDecay;     // fatigue shed per peaceful economy quantum
        public double PeaceThreshold;      // own fatigue at which a faction sues for peace (offers a truce)
        public double AcceptPeaceThreshold;  // fatigue at which a faction accepts a standing peace offer
        public long NegotiateCost;         // suing for peace is cheap ÔÇö a broke, war-weary faction must afford it
        public double TruceRelation;       // standing both sides settle to when a truce concludes (a wary peace)
        public TimeSpan TruceDuration; // non-aggression window after a truce (no raids / no souring)
        public TimeSpan PeaceOfferTtl; // how long an unanswered overture stays on the table
        /// <summary>How long any offer (truce, non-aggression or alliance) stays on the table when
        /// either side is a player faction: a person has to be online, read it and answer, so the
        /// NPC TTLs (<see cref="PeaceOfferTtl"/>, <see cref="PactOfferTtl"/>) are far too short. Real
        /// time on the host. NPC-to-NPC offers keep the NPC TTLs. At or below zero the player window
        /// is off and the NPC TTLs apply to every pair. Read through <see cref="Diplomacy.OfferTtl"/>.
        /// New key, so no TuneVersion gate.</summary>
        public TimeSpan PlayerOfferTtl = TimeSpan.FromHours(24);

        // ---- node placement: zone classification + separation enforcement ----------------------
        // Any natural gravity at the candidate position is Surface (the whole well), none is
        // DeepSpace, and Orbit is authored: a planet's orbit band, or a region like Cauldron's ring
        // (WorldModel.ClassifyZone, FAC-537). The per-zone minimum separation is then checked
        // against all existing nodes, and Surface nodes are snapped to the ground.
        public double OrbitGravityMin;         // RELATIVE gravity (g / that planet's surface g) of the planet-route shell (default 0.05), and of the Orbit snap for a body that forbids surface stations and has no authored region there (WorldModel.SnapToOrbitBand)
        // Minimum dry-hull (no cargo) thrust-to-weight ratio a prefab's Down-facing thrusters must
        // clear, at both origin and destination gravity, to be route-eligible in ShipPool — see
        // ThrustPhysics.DryTwr. WARNING: the C# zero-value (0) disables this gate entirely (DryTwr
        // >= 0 is always true) — tests building FactionTuning directly, not via tuning_defaults.json,
        // must set this explicitly to exercise the gate. Default ships in tuning_defaults.json.
        public double MinDryTwr;

        // Minimum stats a prefab must clear to be capability-classified into Trade/Ferry/Gift, Raid/
        // Patrol/Guard, or Mine build/spawn-candidate pools (PrefabCatalogueScanner.Scan) -- not just
        // nonzero. Cargo is split by grid size because ScanPrefab's fallback cargo-volume formula
        // (Size x cellM, uncompensated bounding box) makes a SINGLE large-grid block already worth
        // 15,625 L and a single small-grid block 125 L -- a shared flat floor can't tell "one
        // incidental block" from "a real cargo bay" on the large grid without over-rejecting small-
        // grid haulers. WARNING: 0 (the C# default) reduces every one of these to the old nonzero-
        // only gate; tests constructing FactionTuning directly (not via tuning_defaults.json) must
        // set these explicitly to exercise the floor. First-pass placeholders in tuning_defaults.json
        // -- not yet observed live, same caveat as the combat-tuning fields below.
        public double MinCargoLitersForRoleClassificationSmall;
        public double MinCargoLitersForRoleClassificationLarge;
        public double MinCombatDpsForRoleClassification;

        /// <summary>Share of a bursting round's declared shrapnel payload counted toward its damage.
        /// 1.0 counts the framework's own min/max average with no adjustment, which is the honest
        /// default: the children are real ammo delivering real damage.
        /// <para>A balance lever, not a correction for a missing input — how much of a burst actually
        /// connects is a hit-rate question and is deliberately not modelled here. Lower it only to
        /// change balance, never to stand in for accuracy.</para></summary>
        public double ShrapnelDamageFraction = 1.0;

        /// <summary>Range cap, in metres, on Synthetic Vision Systems' server-side cockpit ray
        /// (<c>SvsRayRangeCap</c>). The mod casts up to 1,000 km in server physics, which finds materialised
        /// hulls the fog of war withholds; 3,000 matches the world's sync distance, inside which the client
        /// already holds every grid. 0 or less leaves the mod's own range. Read at session load.</summary>
        public double SvsRayMaxRangeM = 3000.0;

        /// <summary>Arms the weapon fire/hit instrument (<c>WeaponFireTelemetry</c>). Off by default:
        /// the shot hook sits on a path a firing turret runs many times a second, and this exists for
        /// short controlled tests rather than standing collection. Requires a restart — the hook is
        /// installed at plugin init.</summary>
        public bool WeaponTelemetryEnabled;
        public double MinDrillYieldForRoleClassification;

        // `!factions ship capture` (player-captured prefabs): raycast reach and a block-count
        // ceiling to keep a single capture from stalling the scan or flooding the shipyard system.
        public double ShipCaptureMaxRangeM = 500.0;
        public int ShipCaptureMaxBlocks = 5000;

        /// <summary>Block ceiling for `!factions station scan`, the station counterpart of
        /// <see cref="ShipCaptureMaxBlocks"/>. Separate from it because the two designs are not the
        /// same shape: a vanilla economy station sits around 1000-1300 blocks, and a player fortress
        /// is expected to be larger than the hulls it defends against.</summary>
        public int StationCaptureMaxBlocks = 5000;

        public double NodeSeparationSurfaceKm; // minimum inter-node distance for Surface zone (km)
        public double NodeSeparationOrbitKm;   // minimum inter-node distance for Orbit zone (km)
        public double NodeSeparationDeepSpaceKm; // minimum inter-node distance for DeepSpace (km)
        public int NodeFrontierMaxOwnedNeighbors = 3; // owned nodes at/under this same-faction neighbor count count as frontier
        public double NodeExpansionReachPct = 0.25; // preferred roominess margin beyond min separation, as a percentage of per-zone minSep
        public int NodeExpansionSampleCount = 4; // legal candidate samples to score before choosing the roomiest expansion position

        public double NodeSeparationFor(NodeZone zone)
        {
            switch (zone)
            {
                case NodeZone.Surface:  return NodeSeparationSurfaceKm;
                case NodeZone.Orbit:    return NodeSeparationOrbitKm;
                default:                return NodeSeparationDeepSpaceKm;
            }
        }

        /// <summary>Radius of the area sample that decides a node's deposit, in km, given the
        /// separation actually in force where that node sits:
        /// <see cref="OreSampleSeparationFraction"/> of it.
        /// <para><b>Takes the separation rather than looking one up.</b> Separation is authored per
        /// body and per feature, and only <c>WorldModel.SeparationAtKm</c> knows which applies at a
        /// position — a zone is not enough. Resolving it here would silently return the global
        /// fallback for every authored body, over-covering the ones packed more tightly (neighbouring
        /// catchments then sample the same ground and both count it) and under-covering the ones
        /// spread wider. Neither shows up as an error, so tuning owns the fraction and the world owns
        /// the separation.</para>
        /// <para>Only the planet path reads this; a feature-backed node's composition is authored,
        /// so <c>WorldFeatureGeography</c> ignores the radius entirely.</para></summary>
        public double OreSampleRadiusForSeparation(double separationKm)
        {
            if (separationKm <= 0.0 || OreSampleSeparationFraction <= 0.0) return OreSampleRadiusKm;
            return separationKm * OreSampleSeparationFraction;
        }

        /// <summary>Hop count for <see cref="WorldModel.ComputeCampaignRegionMembers"/>'s BFS out from
        /// a Campaign focus's center node — how many neighbor-of-neighbor steps count as "the same
        /// region" for Reinforce/Patrol scoring purposes.</summary>
        public int CampaignRegionTraverseDepth = 2;

        // fleet transit + meeting mechanics

        /// <summary>Operator override for SE's own large-grid speed limit, in m/s, applied to
        /// <c>MySector.EnvironmentDefinition</c> at session load and by `factions gridspeed`.
        /// <see cref="GridSpeedPolicy.NoOverride"/> (0) leaves whatever the world loaded alone.
        /// <para>SE does not persist this with the save — the sector stores only the environment
        /// definition's Id and resolves it against each machine's local definitions — so this field
        /// is the persistence. See <see cref="GridSpeedPolicy"/>.</para></summary>
        public double GridMaxSpeedLarge = GridSpeedPolicy.NoOverride;

        /// <summary>Small-grid counterpart of <see cref="GridMaxSpeedLarge"/>.</summary>
        public double GridMaxSpeedSmall = GridSpeedPolicy.NoOverride;

        /// <summary>Operator multiplier on every missile-class munition's speed, applied to the
        /// loaded ammo definitions at session load and by `factions missilespeed`.
        /// <see cref="MunitionSpeedPolicy.NoScale"/> (1) leaves the definitions alone.
        /// <para>SE's ammo content is tuned against a 100 m/s grid limit while
        /// <see cref="GridMaxSpeedLarge"/> raises the world well past it, and a
        /// <c>MissileAmmoDefinition</c> round is hard-capped at its own <c>DesiredSpeed</c> — so
        /// vanilla artillery fires at exactly the large-grid limit and never closes on a receding
        /// grid. Projectile ammo (gatling, autocannon) is deliberately NOT scaled: it already
        /// inherits the firer's velocity and moves with the world on its own. See
        /// <see cref="MunitionSpeedPolicy"/>.</para></summary>
        public double MissileSpeedScale = MunitionSpeedPolicy.NoScale;

        /// <summary>Search area of SE's vanilla Search contract, metres: how far from the target its
        /// GPS marker may land. Written onto the loaded <c>Find</c> <c>MyContractTypeFindDefinition</c> at
        /// session load (<see cref="SearchContractPolicy"/>). <see cref="SearchContractPolicy.NoOverride"/>
        /// (0) leaves the loaded definition alone; vanilla content says 3000.</summary>
        public double SearchContractAreaM = SearchContractPolicy.DefaultAreaM;

        public double FleetSpeedKmPerMin;     // cruise (max) speed, and the DeepSpace tier's cap
        public double FleetSpeedMinKmPerMin;  // departure/arrival speed, and the Gravity tier's cap; overridden at session load from SE LargeShipMaxSpeed
        /// <summary>Cap inside an authored <see cref="NodeZone.Orbit"/> region that is not itself in
        /// gravity — a ring, a belt — in km/min. Between
        /// <see cref="FleetSpeedMinKmPerMin"/> and <see cref="FleetSpeedKmPerMin"/>; a value outside
        /// that range, or 0, leaves such a region flying as the open space around it, which is what
        /// the tier does on a world that authors no zones. See <see cref="TransitTier"/>.</summary>
        public double FleetSpeedOrbitKmPerMin;
        /// <summary>Mean acceleration over the speed ramp (km/min┬▓).
        /// The ramp distance is derived as d_ramp = (v_maxÔêÆv_min)┬▓/(2┬Àa), giving intuitive physics units
        /// while preserving the linear-speed-vs-distance profile. Larger a = shorter ramp = cruise reached sooner.
        /// Shipped defaults are defined in tuning_defaults.json and version-migrated into existing
        /// live tuning files when this transit contract changes.</summary>
        public double FleetAccelKmPerMin2;
        /// <summary>Distance at which a Raid target's base quality is halved. Zero disables the distance discount.</summary>
        public double RaidDistanceHalfValueKm = 3000.0;

        // ---- fleet fuel (docs/fuel-plan.md) -----------------------------------------------------
        // Burn is impulse x per-band specific consumption; the constants below are the FSD mod's
        // own (RC.4 FSD Supercruise, read from the live FSDriveConfig.cfg) and SE's reactor
        // stoichiometry. New fields: no TuneVersion gate needed, the loader backfills them.
        /// <summary>Multiplier on every modelled burn. 1.0 is the physics as read; the value is set
        /// from the verification ratios in docs/fuel-plan.md, never guessed.</summary>
        public double FuelBurnScale = 1.0;
        /// <summary>Speed in m/s a hull flies under thrusters: inside a gravity well, and between
        /// undock and FSD engagement. Every thruster phase is billed at it — hover seconds are the
        /// stretch's length divided by this, and one thruster stretch costs <c>2 ×</c> this in Δv.
        /// <see cref="ApproachCruiseSpeedMs"/> is the materialised counterpart, the speed the flight
        /// controller actually commands a real grid, and the two should agree; they are separate
        /// fields so the abstract burn model does not read a controller knob. It is not the world's
        /// grid speed limit (<see cref="FleetSpeedMinKmPerMin"/>): a hull under autopilot flies well
        /// below that.</summary>
        public double ThrusterCruiseSpeedMs = 100.0;
        /// <summary>Natural gravity, in g, above which the FSD refuses and drops out
        /// (<c>AllowInGravity=false</c>, <c>GridGravityCalc &gt; 0.01</c>). Every leg inside this
        /// boundary is thrusters; everything outside it is FSD cruise on power.</summary>
        public double FsdGravityDropoutG = 0.01;
        /// <summary>FSD <c>baseRequiredPower</c> (MW), large grid.</summary>
        public double FsdBaseRequiredPowerMw = 26.0;
        /// <summary>FSD <c>powerRequirementMultiplier</c> — the speed-fraction term's weight.</summary>
        public double FsdPowerRequirementMultiplier = 2.0;
        /// <summary>FSD <c>powerRequirementBySpeedDeviderLarge</c> — divides the whole active draw.</summary>
        public double FsdPowerBySpeedDivider = 8.0;
        /// <summary>FSD <c>maxSpeed</c> in km/s (2,000 km/s live).</summary>
        public double FsdMaxSpeedKmPerS = 2000.0;
        /// <summary>FSD <c>DelayJump</c>: seconds the drive charges before entry, at the charge draw.</summary>
        public double FsdChargeSeconds = 10.0;
        /// <summary>Charge draw's mass term: <c>2.1 MW per 100,000 kg</c> in the mod, i.e. MW per kg.</summary>
        public double FsdChargeMwPerKg = 2.1e-5;
        /// <summary>Kilograms of reactor fuel per MWh: SE's large reactor <c>FuelInfo</c> is Mass 1,
        /// Ratio 1, so 1 kg of <c>Ingot:Uranium</c> is 1 MWh.</summary>
        public double ReactorFuelKgPerMwh = 1.0;
        /// <summary>Speed the braking end of a thruster stretch is flown at, m/s — a descent into a
        /// well, which nothing caps until braking starts. Measured live on v0.256.0: approaches peak
        /// at 420.1 m/s (p50, n=18) against 100.1 for launches (n=291), and hover goes as 1/v, so
        /// one speed for both over-charges the descent about fourfold. 0 falls back to
        /// <see cref="ThrusterCruiseSpeedMs"/>, which is the behaviour before this field.</summary>
        public double ThrusterApproachSpeedMs = 420.0;

        /// <summary>How far the fuel model is wired in: <see cref="FuelDrawModes.Off"/> (0),
        /// <see cref="FuelDrawModes.Measure"/> (1) or <see cref="FuelDrawModes.Draw"/> (2).
        /// <para>Off costs nothing — the mode check precedes every computation. Measure prices every
        /// leg the world flies into <see cref="FuelLedger"/> and touches no inventory, credit,
        /// demand row or dispatch decision; <c>factions fuel</c> reads the totals. Draw additionally
        /// takes the burn from the departing node's stock, buys the shortfall through the treasury
        /// when <see cref="FuelImportPremium"/> is above 0, posts what is still missing as demand at
        /// the node (<see cref="FuelShortfallDemandMinutes"/>) and posts a reserve at every founded
        /// node.</para>
        /// <para><b>The default is Draw with the import fallback off</b>, the way ammunition is
        /// drawn: a hull flies short rather than waiting or buying, and the shortfall is a demand the
        /// supply side ramps up to. Buying it was never affordable — measured 2026-09-19, ~146 GL of
        /// hydrogen a day would cost ~439 billion credits a day against a 20.4 billion world
        /// treasury.</para></summary>
        public int FuelDrawMode = FuelDrawModes.Draw;

        /// <summary>Whether fleets carry real ammunition: <see cref="MunitionDrawModes.Off"/> (0), every
        /// fleet fires its bands without limit; <see cref="MunitionDrawModes.Draw"/> (1), every armed
        /// fleet draws a full load of magazines from its departure node, fires only what it drew,
        /// and returns the rest where its hulls land (<see cref="FleetMunitions"/>). A combat fleet
        /// the node cannot fully arm departs short and says so.</summary>
        public int MunitionDrawMode = MunitionDrawModes.Draw;

        /// <summary>Seconds of sustained fire one duel turn stands for — what turns a slot's
        /// magazines-per-second into the magazines a turn spends. At 30 a vanilla large gatling
        /// turret (24 magazines, 700 rpm, 75% duty cycle) spends about 1.9 magazines a turn it is in
        /// range for — ~3.8 of its 24 across the default duel, whose 2000 m bracket it cannot
        /// reach.</summary>
        public double CombatTurnFireSeconds = 30.0;

        /// <summary>How long a combat departure's ammunition shortfall stays posted as demand at the
        /// node it left, in minutes (<see cref="FleetMunitions.PostShortfall"/>). Long enough for a
        /// haul to be matched and arrive; the next short departure from the node refreshes it. 0 posts
        /// nothing.</summary>
        public double MunitionShortfallDemandMinutes = 60.0;

        /// <summary>Full loads a founded node asks to hold for the combat hulls parked at it — its
        /// standing ammunition reserve (<see cref="FleetMunitions.PostReserveForNode"/>), posted every
        /// production cycle beside the fuel reserve so a fleet finds its magazines at the node before
        /// it leaves. 0 posts no standing reserve; the departure shortfall still posts.</summary>
        public double MunitionReserveLoads = 1.0;

        /// <summary>Seconds between reads of a standing station's magazines: each read books what its
        /// guns fired as spent and tops the station back up to its load from node stock
        /// (<see cref="StationMunitions"/>). 0 loads a station once, when it stands, and reads it
        /// again only when it despawns.</summary>
        public double StationMunitionTopUpSeconds = 60.0;

        /// <summary>Full duels of estimated fire a fleet's ammunition load is sized for
        /// (<see cref="FleetMunitions.SortieLoad"/>): each slot's magazines per second over
        /// <see cref="CombatTurnFireSeconds"/> for every turn it is in range of, times this. A slot
        /// never takes less than its weapons' own inventories hold. 0 sizes every load by those
        /// inventories alone.</summary>
        public double MunitionSortieDuels = 1.0;

        /// <summary>Patrol hulls are chosen by fuel cost for the circuit, sized to the circuit's
        /// threat (<see cref="SquadronAssembler.AssembleByCost"/>). False restores the strongest-first
        /// assembly, posture-sized.</summary>
        public bool PatrolFuelRanking = true;

        /// <summary>How far back, in hours, an attack recorded at a circuit node counts toward the
        /// DPS a patrol on it is sized to meet. 0 counts only raids inbound or holding now.</summary>
        public double PatrolThreatMemoryHours = 6.0;

        /// <summary>Hours of measured burn a founded node asks to hold in stock. The reserve it
        /// posts is <c>max(FuelBurnEma × this, FuelReserveFloor) − stock</c>.</summary>
        public double FuelReserveHours = 6.0;

        /// <summary>Units of each fuel key a founded node asks for before it has any burn history —
        /// what a cold node stands ready with, and the floor the reserve never falls below once the
        /// history is there. Every founded node posts it, not only the ones holding hulls: a node
        /// with none is where a hull lands empty after a delivery.</summary>
        public long FuelReserveFloor = 500000;

        /// <summary><see cref="FuelReserveFloor"/> for reactor fuel, in kilograms. A litre of gas
        /// and a kilogram of uranium are not the same size of want: measured live 2026-09-25 over
        /// 1h40m, the network burned 8.89 GL of hydrogen and 149 kg of reactor fuel.</summary>
        public long FuelReserveFloorReactorKg = 10;

        /// <summary>Multiplier on the item's price when a departure short of stock buys the
        /// shortfall through the treasury. Above 1 so importing is always dearer than hauling, which
        /// is what makes the imported share fall as the chain closes. At or below 0, the default,
        /// the fallback is off: the hull flies without the shortfall and the node asks for it
        /// (<see cref="FuelShortfallDemandMinutes"/>).</summary>
        public double FuelImportPremium;

        /// <summary>How long a departure's fuel shortfall stays posted as demand at the node it left,
        /// in minutes (<see cref="ShortfallDemand.Post"/>). The node's reserve reacts to the same
        /// burn over <see cref="FuelBurnEmaHours"/>; this is the signal that arrives with the first
        /// short departure. The next short departure from the node refreshes it. 0 posts
        /// nothing.</summary>
        public double FuelShortfallDemandMinutes = 60.0;

        /// <summary>Time constant of <see cref="SpatialNode.FuelBurnEma"/>, in hours: the estimate
        /// decays by a factor of <c>e</c> over this span, so it describes a node's recent dispatch
        /// rate rather than its lifetime total.</summary>
        public double FuelBurnEmaHours = 6.0;

        /// <summary>How much a parked hull raises the urgency of a node's
        /// <see cref="DemandClass.Fuel"/> row. A dry shipyard moves nothing; a dry outpost is merely
        /// unready. 0 makes hull count irrelevant, which is the behaviour before the field.</summary>
        public double FuelUrgencyPerParkedHull = 0.25;

        /// <summary>Ceiling on the hull-count multiplier above, so one yard holding a hundred hulls
        /// cannot outrank every other claim the faction has.</summary>
        public double FuelUrgencyHullCap = 4.0;

        /// <summary>Hours between a node falling short of a fuel key and the next haul of it
        /// arriving — the refill lead a departure node has to bridge from its own stock. The node's
        /// departure buffer is its measured burn per hour (<see cref="SpatialNode.FuelBurnEma"/>)
        /// times this (<see cref="FuelReserve.BufferTarget"/>).
        /// <para>Measured live 2026-09-29 00:00–02:00 box-local, from each node's first
        /// <c>FUEL_SHORT</c> to its next <c>HAUL_RESOLVED … Gas:Hydrogen</c>: p50 6.5 min, p90 17.3,
        /// p95 22.9 (1,904 refills). Dispatch-to-delivery transit alone is p50 1.7 min, p99 15.6. 0.5
        /// covers p95 with margin; a lead read off the node's own deliveries would need new
        /// per-node persisted state for a distribution that narrow. 0 turns the buffer off.</para></summary>
        public double FuelBufferLeadHours = 0.5;

        /// <summary>Ceiling on a node's departure buffer, as a multiple of the key's reserve floor
        /// (<see cref="FuelReserveFloor"/> litres of gas, <see cref="FuelReserveFloorReactorKg"/>
        /// kilograms of reactor fuel): at the defaults 50 ML of hydrogen and 1 t of uranium. A guard
        /// against one burst of departures asking the network for an unbounded row, not a sizing
        /// term — the busiest measured node (23.8 ML/h, 2026-09-29) buffers 11.9 ML at the default
        /// lead. 0 or below leaves the buffer uncapped.</summary>
        public double FuelBufferMaxFloors = 100.0;

        /// <summary>Weight on the fuel term when ranking candidate routes: a route's cost is
        /// <c>minutes + this x (its burn priced at import credits)</c>. <b>0, the default, ranks on
        /// time alone</b> and skips pricing the candidates entirely.
        /// <para>Time already carries most of the fuel saving, because both are dominated by seconds
        /// spent inside a gravity well — hover is charged per second there. The weight is for the
        /// cases where the two diverge, and it is set live rather than at deploy.</para></summary>
        public double RouteFuelWeightPerMinute;

        // fleet planet-route planning (PlanetRoutePlanner)
        /// <summary>Arc altitude above a body's surface radius for the low-pass profile, offered only
        /// to hulls whose scanned thrust can still hold them up down there. A floor, not a nominal:
        /// sampled chords touch it at their midpoints and vertices sit slightly higher.</summary>
        public double PlanetRouteLowAltitudeKm = 2.0;
        /// <summary>Clearance above max(surface, atmosphere) used when a body has no usable gravity
        /// baseline to solve an orbit band from (airless rock, malformed record, offline sim).</summary>
        public double PlanetRouteClearanceKm = 10.0;
        /// <summary>Arc sampling step in degrees. 36 gives ten vertices for a full orbit.</summary>
        public double PlanetRouteArcDegreesPerVertex = 36.0;
        /// <summary>Ceiling on generated waypoints for one route plan. Exceeding it fails the plan,
        /// which is logged and leaves the fleet undispatched rather than flown through a body.</summary>
        public int PlanetRouteMaxWaypoints = 24;
        /// <summary>Fewest vertices an arc is sampled into, however shallow its sweep. A distant
        /// flyby only sweeps a few degrees between its tangents; without a floor it would emit two
        /// vertices and read as a pair of corners rather than a curve.</summary>
        public int PlanetRouteMinArcVertices = 3;

        /// <summary>How long a leg of <paramref name="distKm"/> takes through open space. The form
        /// every caller that has only a distance uses; <see cref="TransitTimeMins(LegZoneProfile)"/>
        /// is the one that reads the ground the leg actually crosses.</summary>
        public double TransitTimeMins(double distKm)
        {
            PlainTransit transit;
            return PlainTransit.TryBuild(distKm, this, out transit) ? transit.TotalMins : 0;
        }

        /// <summary>How long a leg takes across the tiers it actually crosses.</summary>
        public double TransitTimeMins(LegZoneProfile zones)
        {
            TransitSchedule schedule;
            if (!TransitSchedule.TryGet(zones, this, out schedule)) return 0;
            return schedule.TotalMins;
        }

        /// <summary>Distance fraction covered after elapsed real minutes, on the same profile that
        /// schedules <see cref="TransitTimeMins(double)"/>.</summary>
        public double TransitProgressRatio(double distKm, double elapsedMins)
        {
            PlainTransit transit;
            if (!PlainTransit.TryBuild(distKm, this, out transit)) return elapsedMins > 0 ? 1.0 : 0.0;
            return transit.ProgressRatio(elapsedMins);
        }

        /// <summary>Distance fraction covered after elapsed real minutes, across the leg's tiers.</summary>
        public double TransitProgressRatio(LegZoneProfile zones, double elapsedMins)
        {
            TransitSchedule schedule;
            if (!TransitSchedule.TryGet(zones, this, out schedule)) return elapsedMins > 0 ? 1.0 : 0.0;
            return schedule.ProgressRatio(elapsedMins);
        }

        /// <summary>Inverse of <see cref="TransitProgressRatio(double, double)"/>, for preserving an
        /// in-flight fleet's spatial position while a new transit contract is applied.</summary>
        public double TransitElapsedMinsAtDistance(double distKm, double coveredKm)
        {
            PlainTransit transit;
            if (!PlainTransit.TryBuild(distKm, this, out transit)) return 0.0;
            return transit.MinsAtKm(coveredKm);
        }

        /// <summary>Inverse of <see cref="TransitProgressRatio(LegZoneProfile, double)"/>.</summary>
        public double TransitElapsedMinsAtDistance(LegZoneProfile zones, double coveredKm)
        {
            TransitSchedule schedule;
            if (!TransitSchedule.TryGet(zones, this, out schedule)) return 0.0;
            return schedule.MinsAtKm(coveredKm);
        }
        public double ObservationRadiusKm;       // player proximity radius that triggers MES materialisation
        /// <summary>Within this distance of a player a fleet spawns whatever holds it back: the load
        /// governor, the traffic presence budget and the station queue. Under any of those the
        /// trigger radius shrinks from <see cref="ObservationRadiusKm"/> to this one rather than the
        /// spawn being refused outright, so a player who closes in still gets the ship. 3 km is the
        /// sync distance; the spawn also goes to the head of the spawn queue. 0 turns it off.
        /// (Inline default: a missing JSON key must not zero it; new key, so no TuneVersion bump.)</summary>
        public double ForceSpawnRadiusKm = 3.0;
        /// <summary>How far from the nearest player a fleet starts braking (<see cref="ChaseBrake"/>):
        /// cruise speed here, falling at a constant deceleration to
        /// <see cref="ChaseBrakeFloorKmPerMin"/> at <see cref="ObservationRadiusKm"/>. 0 turns the brake
        /// off. (Inline default; new key, so no TuneVersion bump.)</summary>
        public double ChaseBrakeStartKm = 100.0;
        /// <summary>The speed a braked fleet holds inside <see cref="ObservationRadiusKm"/>, km/min.
        /// Below the world's ship limit (30 km/min on production), so a player at full speed closes
        /// on it even from behind. Its own key because <see cref="FleetSpeedMinKmPerMin"/> is set at
        /// session load from SE and also caps every leg in gravity. 0 falls back to that.
        /// (Inline default; new key, so no TuneVersion bump.)</summary>
        public double ChaseBrakeFloorKmPerMin = 25.0;
        public TimeSpan GuardDuration; // how long a guard actively defends after arrival

        // ---- native flight controller (terminal approach) --------------------------------------
        // A materialised grid cruises under the vanilla RC autopilot and hands over to the native
        // controller once inside the computed approach horizon. See FlightManoeuvre (Core) for the
        // profile and FleetFlightController (host) for the SE-side driving.
        public double ApproachStopDistanceM    = 50.0;  // come to rest this far short of the target
        public double ApproachArriveToleranceM =  5.0;  // treat as stopped within this of the stop point
        public double ApproachHorizonMargin    =  1.5;  // safety factor on the computed stopping distance
        public double ApproachMinSpeedMs       =  5.0;  // floor on the commanded speed cap while approaching
        public double ApproachArriveSpeedMs    =  2.0;  // must be this slow, not just close, to count as stopped
        public double ApproachCruiseSpeedMs    = 100.0; // matches VanillaFleetSpawner.AutopilotSpeedMs
        // Fraction of the grid's max opposing thrust the braking profile plans against. Below 1.0
        // because live thrust can be lower than the block maximum (power browning out, damage), and
        // planning too optimistically means arriving too fast to stop. Calibrated against live
        // telemetry: a 143 t hauler planning 30 m/s^2 actually achieved ~19 m/s^2 over a real
        // approach, so the raw maximum overstates what a decelerating grid delivers by roughly 2.5x.
        public double ApproachDecelSafetyFactor = 0.4;

        // ---- launch manoeuvre (the departure bookend of the approach above) ---------------------
        // A freshly spawned grid is at rest, and the host's stall detector force-fails any grid that
        // stays under FactionRuntime.StallSpeedKmPerMin (0.5 km/min = 8.33 m/s) for StallTimeout
        // (15 s), destroying hull and cargo. The native controller therefore owns a grid off its
        // origin the same way it owns one into its destination, and hands back once it is clearly
        // moving. New keys, so no TuneVersion gate.
        public bool   LaunchManoeuvreEnabled = true;   // kill switch; false restores pre-FAC-170 behaviour
        /// <summary>Speed at which a launch hands the grid back to the cruise autopilot.
        /// <b>Must stay comfortably above the host's 8.33 m/s stall threshold</b> — handing back at
        /// or below it returns the grid straight into the force-fail condition. 15 m/s is 1.8x.</summary>
        public double LaunchHandoffSpeedMs   = 15.0;
        /// <summary>Speed below which a grid is considered not to have got going, and may be taken
        /// onto a launch. <b>Must be well below <see cref="LaunchHandoffSpeedMs"/></b>: the gap
        /// between the two is the hysteresis band that stops the engage gate and the handback
        /// chattering against each other every frame. Equal values are refused outright by
        /// <c>FlightManoeuvre.ShouldBeginLaunch</c> rather than allowed to oscillate.</summary>
        public double LaunchEngageSpeedMs    =  2.0;
        /// <summary>Engage speed for a grid no launch has held yet. <see cref="LaunchEngageSpeedMs"/>
        /// only has to stop a handback re-engaging, and a fresh grid has had none; a spawn already
        /// creeping when first asked was otherwise never launched and died to the 8.33 m/s stall
        /// detector. Above that stall speed so every grid the detector could count is covered, and
        /// clamped below <see cref="LaunchHandoffSpeedMs"/> by <c>FlightManoeuvre.EngageLimitMs</c>.
        /// New key, so no TuneVersion gate.</summary>
        public double LaunchFirstEngageSpeedMs = 10.0;
        /// <summary>Whether a lift gate reads the thrusters that hold a hull up in the attitude it
        /// flies (its <see cref="HullFrame"/>) rather than the <c>Down</c> bucket. False restores the
        /// belly-down assumption every gate made before FAC-215 — the kill switch for a candidate pool
        /// that turns out to shrink. New key, so no TuneVersion gate.</summary>
        public bool   LiftFromHullFrame      = true;
        /// <summary>Upper bound on how long a launch may hold a grid. A grid that cannot reach
        /// <see cref="LaunchHandoffSpeedMs"/> in this long is not slow, it is stuck — releasing it
        /// hands it back to the stall detector, which is the correct outcome for genuinely immobile
        /// debris. Longer than the 15 s stall timeout on purpose: shorter would let the detector fire
        /// mid-launch, which is the defect this manoeuvre exists to remove.</summary>
        public double LaunchTimeoutSeconds   = 45.0;
        /// <summary>How far a launch must carry the grid from where it engaged before it may hand
        /// back, in metres. Speed alone was not enough: the cruise autopilot adopts an obstacle from
        /// anything overlapping the grid's own volume sphere, so a grid released while still parked
        /// beside station geometry is released into that condition however fast it is moving, and
        /// can be pinned there. Sized to the clearance a *spawn* gets (300-600 m via
        /// <c>FindFreePlaceCustom</c>), which is the one launch that has never been observed to fail.
        /// Zero restores speed-only handback.</summary>
        public double LaunchClearanceM       = 400.0;
        /// <summary>How long after a handback to check that the cruise autopilot actually took the
        /// grid. Long enough that a grid genuinely under way has visibly gained ground, short enough
        /// that a failed one has not yet been sitting still for the 15 s stall timeout.</summary>
        public double HandbackVerifySeconds  = 6.0;
        /// <summary>A handback counts as failed if the grid is below this fraction of
        /// <see cref="LaunchHandoffSpeedMs"/> when the verify window closes. A grid the cruise
        /// autopilot is flying is accelerating toward cruise by then; one it declined to thrust is
        /// being dampened to a dead stop, so the two are far apart and the threshold is not
        /// delicate.</summary>
        public double HandbackMinSpeedFrac   = 0.5;
        /// <summary>A launch in gravity hands its grid to the cruise autopilot once the grid's
        /// current attitude cannot hold it up: the thrust it can apply straight up, over its weight,
        /// falls below this. With <see cref="FlightGravityAlignEnabled"/> a launch climbs level on the
        /// hull's lift thrusters and this rarely fires; it is the net for a hull that cannot hold
        /// itself up level either, or for a grid whose remote control is gone. 1.0 is the physical
        /// line, below which the grid is losing altitude however it is commanded. New key, so no
        /// TuneVersion gate.</summary>
        public double LaunchAbortLiftTwr     = 1.0;
        /// <summary>In gravity, a native launch or approach holds the hull's remote-control up away
        /// from the planet with its nose level, the attitude SE's own cruise autopilot flies
        /// (<c>AlignToGravity</c>). False restores facing the bearing with grid Forward and the current
        /// roll. New key, so no TuneVersion gate.</summary>
        public bool   FlightGravityAlignEnabled = true;
        /// <summary>How many times to re-launch a grid whose handback failed before giving up on
        /// flying it physically and resolving the leg abstractly. Each retry holds the grid on the
        /// native controller, so it is never left to the stall detector while this plays out.</summary>
        public int    HandbackMaxRetries     = 2;
        // Clearance probe for the stop point: before settling ApproachStopDistanceM short of the
        // node centre, the host probes that spot for parked grids/terrain (FleetFlightController)
        // and ApproachClearance (Core) picks a nearby clear point when it is occupied. The chosen
        // point never sits further than ApproachStopDistanceM + ApproachMaxStandoffM from the
        // target; leg arrival accepts a controller-reported stop within that same envelope.
        public bool   ApproachClearanceEnabled      = true;  // kill switch for the whole probe
        public double ApproachClearanceRadiusFactor = 1.5;   // probe radius = grid radius x this + margin
        public double ApproachClearanceMarginM      = 15.0;  // additive slack on the probe radius
        /// <summary>Radius at which a materialised convoy counts as having passed a clearance
        /// waypoint. Wider than a station's safezone because the grid flies through at cruise speed
        /// instead of braking to rest on it.</summary>
        public double NavigationFlyThroughRadiusM = 500.0;
        public double ApproachMaxStandoffM          = 120.0; // max extra deviation beyond the stop distance
        public double ApproachProbeIntervalSec      = 2.0;   // min seconds between re-probes mid-approach
        public double ApproachProbeFreezeM          = 300.0; // inside this of the stop point, stop re-probing

        // ---- terminal bubble and the dynamic cruise clamp ---------------------------------------
        // Cruise speed is no longer one constant for every hull. Each grid is granted the speed it
        // can still shed before it reaches the bubble, computed from the braking authority it has
        // actually been observed to deliver. See FlightManoeuvre.CruiseCapMs / PlanningDecelMs2.
        /// <summary>Radius around a node inside which every approach holds
        /// <see cref="ApproachTerminalSpeedMs"/>, whatever it could brake at. This is the volume
        /// that holds station geometry and parked traffic.</summary>
        public double ApproachTerminalRadiusM  = 300.0;
        /// <summary>Flat speed cap inside <see cref="ApproachTerminalRadiusM"/>, m/s. Sits above
        /// the stall threshold, though that only matters if the exemption for a manoeuvring grid
        /// is ever removed — inside the bubble the grid is always under native control.</summary>
        public double ApproachTerminalSpeedMs  =  10.0;
        /// <summary>Floor on the per-grid cruise clamp, m/s. Without one, a grid whose braking
        /// authority reads near zero is granted a crawl and takes the whole leg at it — the exact
        /// failure a low estimate caused before (a grid crawling 15 km at 5 m/s).</summary>
        public double CruiseSpeedFloorMs       =  40.0;
        /// <summary>Ceiling on the per-grid cruise clamp, m/s. 0 follows the world's own grid speed
        /// limit for the hull's size, which is the only speed SE will actually let it reach.</summary>
        public double CruiseSpeedCeilingMs     =   0.0;
        /// <summary>Multiplier on an observed braking figure before it is allowed to hold the
        /// estimate down. <b>1.0 by default, and raising it plans optimistically.</b>
        ///
        /// <para>The obvious reason to want slack — a sample caught while the grid was still
        /// swinging onto its retro attitude, understating what it can do — is already handled by
        /// keeping the best sample rather than a mean. Multiplying on top of that grants speed the
        /// grid has never been seen to shed: at the live 15 km horizon, a hull delivering
        /// 0.09 m/s² planned with 1.5x slack is cleared for 52 m/s and reaches the bubble at 31,
        /// not 10. Left configurable for a load that changes mid-leg, where the honest fix is the
        /// fresh observation that arrives on the next brake.</para></summary>
        public double BrakeObservationSlack    =   1.0;
        /// <summary>Braking samples a grid must produce before its observation is trusted to clamp
        /// the estimate. Until then the grid is held to <see cref="ApproachCruiseSpeedMs"/> — the
        /// speed that has been flying all along — so no grid ever goes faster than today before it
        /// has demonstrated that it can stop.</summary>
        public int    BrakeObservationMinSamples = 4;
        /// <summary>Seconds of <b>continuous</b> braking that make up one observation, m/s² being
        /// derived from the speed lost across the whole window.
        ///
        /// <para>Never sample per tick. A single frame's velocity delta is dominated by physics
        /// noise, and the record keeps a maximum, so per-tick sampling converges on the largest
        /// one-frame artifact instead of on braking authority — measured live at 60 samples/s it
        /// reported 71 m/s² where thrust allowed 11, which put the record above the estimate on
        /// every grid and made the clamp inert.</para></summary>
        public double BrakeSampleWindowSeconds   = 1.0;

        // ---- launch clearance -------------------------------------------------------------------
        /// <summary>Kill switch for the departure-bearing probe (LaunchClearance). Off restores a
        /// launch that thrusts straight at the destination from the moment it engages.</summary>
        public bool   LaunchClearanceProbeEnabled = true;
        /// <summary>Samples taken along a candidate departure corridor. The corridor is
        /// <see cref="LaunchClearanceM"/> long, so this sets how finely it is checked.</summary>
        public int    LaunchClearanceProbeSamples = 6;

        // ---- in-flight avoidance ----------------------------------------------------------------
        // A steer-around term composed into every native manoeuvre's thrust direction
        // (FlightAvoidance). The host feeds it the bounding spheres of dynamic grids and characters
        // near the controlled grid; static grids and voxels are the clearance probes' job. New keys,
        // so no TuneVersion gate.
        /// <summary>Kill switch. Off restores manoeuvres that steer around nothing.</summary>
        public bool   FlightAvoidanceEnabled      = true;
        /// <summary>How far ahead the closest-approach test looks, seconds. Longer starts the
        /// sidestep earlier and gentler; shorter lets grids pass closer before reacting.</summary>
        public double FlightAvoidLookaheadSec     = 8.0;
        /// <summary>Clearance kept between the two bounding spheres, metres.</summary>
        public double FlightAvoidMarginM          = 25.0;
        /// <summary>Seconds between refreshes of which entities are near a controlled grid. Their
        /// positions and velocities are read every tick; only the membership is cached.</summary>
        public double FlightAvoidProbeIntervalSec = 0.5;
        /// <summary>Closing speed assumed for an obstacle when sizing the neighbourhood query, m/s:
        /// the query reaches (own speed + this) x look-ahead, so something coming the other way is
        /// already in the set when it becomes a threat.</summary>
        public double FlightAvoidObstacleSpeedMs  = 100.0;
        /// <summary>Upper bound on the neighbourhood query radius, metres.</summary>
        public double FlightAvoidQueryMaxRadiusM  = 2000.0;
        /// <summary>Most obstacles one grid tracks at once; the nearest are kept.</summary>
        public int    FlightAvoidMaxObstacles     = 32;

        // trade lane tuning
        public int MaxTradeFleets;                 // hard cap: in-flight Trade fleets per faction
        public TimeSpan TradeDestCooldown;         // before re-dispatching to same partner ÔÇö forces cycling across partners
        public TimeSpan FleetCooldownJitter;       // random additive jitter per fleet dispatch

        // Convoy size ("clumpiness") per fleet kind.
        // Low size + short cooldown  ÔåÆ trickle (many solo ships spread over time).
        // High size + long cooldown  ÔåÆ burst   (infrequent coordinated fleet strikes).
        // The spawner reads ConvoySize on PendingFleetAction to materialise N ships per event.
        // ---- price oracle / scarcity routing (TradeManager) ----------------------------------------
        // A per-node, per-ItemKind scarcity multiplier is computed from live node inventory vs. the
        // demand pressure floor. TradeManager uses it to route surplus to whichever partner needs it
        // most, regardless of proximity. ScarcityTargetMultiple ├ù DemandPressureFloor = "target stock"
        // at which the multiplier equals 1.0 (neutral). Below target ÔåÆ multiplier > 1 (scarce, pay more).
        // Above target ÔåÆ multiplier < 1 (surplus, discount). PricePressureWeight caps the score boost.
        public double ScarcityTargetMultiple;  // target stock = DemandPressureFloor ├ù this
        public double ScarcityMin;             // floor: over-stocked suppresses price to 50%
        public double ScarcityMax;             // ceiling: empty stock triples price
        public double PricePressureWeight;     // additive weight on partner score: score ├ù (1 + weight ├ù urgency)

        // Per-ItemKind reference stock bootstrap floors. PriceOracle applies these as a minimum so
        // even nodes with no installed capabilities respond to Ore/PG depletion. Also represents the
        // "healthy stock level" for demand-register quantity defaults.
        public long ReferenceStockOre           = 200;
        public long ReferenceStockProcessedGood = 100;
        public long ReferenceStockRareOre       =  40;
        public long ReferenceStockIngot         =  60;
        // Gas is counted in litres, so these are orders larger than the ore/ingot ones and mean the
        // same thing. For gas the floor IS the shelf: 10,000,000 L of hydrogen and 2,000,000 L of
        // oxygen, the figures the operator pinned on 2026-08-29 as the throttle on how much of the
        // world's ice the chain may consume. They used to be reached through the capability credit
        // budget (2000 x 10 x 1500 / 3 and 1000 x 10 x 1000 / 5); that budget is gone and the numbers
        // are now stated once, here. TuneVersion 53.
        //
        // Gas is also the one chain whose bootstrap cannot come from a want -- PriceOracle applies
        // these two unconditionally, where every other kind's floor needs a live want or a glut.
        public long ReferenceStockHydrogen      = 10_000_000;
        public long ReferenceStockOxygen        =  2_000_000;

        // ---- target stock from observed flow ---------------------------------------------------
        // What a node should hold of an item, ranked the way a warehouse manager would: by how much
        // of it flows past per unit of time, weighted by how many further refinement steps stand on
        // it and in what quantity. The flow term is SpatialNode.DemandHistory -- a decaying,
        // volume-weighted record of real consumption -- so the decay the rank needs is the EMA's own
        // and nothing new is persisted.
        //
        // This replaces the capability-throughput credit budget above as the target-stock rule. Those
        // fields still price the kind-level budget TradeManager reads, and the per-kind floors still
        // apply -- but only where something actually wants the item, rather than to every key at
        // every node.
        //
        // There is deliberately no enable flag. ScarcityValueGateEnabled shipped inert and stayed
        // inert, and it is one reason an earlier attempt at this never landed; a second switch in the
        // same file would be the same mistake. It is retired here because the ranking answers its
        // question unconditionally: a key nothing consumes and nothing wants targets zero.
        //
        // All three are purely additive vs the current TuneVersion, so no bump is needed. Each carries
        // an inline default because a missing JSON key must not zero it.
        // How many DemandHistory periods of observed consumption a node keeps on the shelf. The EMA
        // reads as units consumed per OperationsManager.Tick pass, so this is a coverage horizon in
        // passes: 3 means "hold enough for the next three passes at the rate we are actually using
        // it".
        public double TargetStockCoveragePeriods = 3.0;
        // Largest multiplier the downstream term can add. An item nothing is made from scores 1.0;
        // the most widely and deeply consumed key in the catalogue scores 1 + this. Iron ingot
        // against concrete is the case this exists to separate.
        public double TargetStockDownstreamWeight = 3.0;
        // Below this much observed flow a node is treated as not consuming the item at all, so one
        // stray batch cannot mint a standing target. In units per pass, after the EMA's volume
        // weighting is divided back out. Also gates the sale term.
        public double TargetStockFlowFloorUnits = 1.0;
        // Coverage horizon for the sale term -- units really bought from this node per pass, from
        // cross-faction trade and player store purchases. Deliberately shorter than the consumption
        // horizon: a shelf stocked for customers is a smaller claim than one feeding a machine, and
        // a terminal product held only because it sells should sit low and trend to the floor.
        public double TargetStockSaleCoveragePeriods = 2.0;

        // ---- station store listing caps (per item category) ------------------------------------
        // Maximum quantity of a single item listed in one StoreBlock offer slot.
        // Sized by how players actually transact: bulk materials move in millions,
        // weapons in handfuls. All caps are tunable via tuning.json.
        public long StoreListingCapBulk       = 1_000_000; // Ore, Ingot, Component
        public long StoreListingCapAmmunition =    10_000; // AmmoMagazine
        public long StoreListingCapConsumable =     1_000; // ConsumableItem, SeedItem, gas/O2 bottles, Datapad
        public long StoreListingCapWeapon     =       100; // PhysicalGunObject
        // Gas has no listing cap: an offer row carries the node's whole offerable stock, so one refuel
        // is bounded by the buyer's tank space and wallet (StationStoreManager.ListingCap).

        // ---- store ship quotas (StoreStockManager) -----------------------------------------
        // Per-Shipyard node: how many built copies of each size to keep queued for sale.
        public int StoreShipSlotsSmallGrid    =     5;     // ships (small grid) listed per node
        public int StoreShipSlotsLargeGrid    =     2;     // ships (large grid) listed per node
        public double StoreStockIntervalSeconds = 60.0;

        /// <summary>Dead band, in slots, between stocking and releasing. Stocking stops at the slot
        /// target; releasing does not begin until the shelf is this far ABOVE it. The two thresholds
        /// must not be the same number: one comparison read in both directions is how a hull
        /// oscillates on and off the shelf every cadence, which is the failure the operator named
        /// (2026-09-03: "it should not be churn"). 0 collapses the band and re-creates it.</summary>
        public int StoreShelfReleaseMarginSlots = 1;

        /// <summary>Most reservations one faction may hand back per release pass. A rate bound, not
        /// a policy: it means a shelf drains toward its target over several cadences instead of in
        /// one step, so a mis-set target cannot empty a shop between two player visits.</summary>
        public int StoreShelfReleasesPerPass = 1;

        /// <summary>SE faction types whose factions keep no player store at all -- no sell offers,
        /// no buy orders, no ship listings, and no store-stock builds. Matched ordinal-ignore-case
        /// against <see cref="IFactionTypeCatalogue.TryGetFactionType"/>'s answer, so it names a
        /// world-stated type rather than a roster of tags.
        ///
        /// <para>The default excludes <c>None</c> alone. That is the type SE gives a faction with no
        /// economic role -- the deep-space antagonist FCTM, and the neutral UNKN. Operator direction
        /// 2026-08-29: <i>"SPRT should be selling, FCTM should not."</i> Pirate therefore stays a
        /// trading type; a player can buy a hull from the pirates.</para>
        ///
        /// <para>An empty or null list disables the gate entirely, which is the pre-FAC-152
        /// behaviour. New key, so no <c>TuneVersion</c> gate -- the gate exists for changing a
        /// default that is already persisted, and no build has ever written this one.</para></summary>
        public string[] StoreExcludedFactionTypes = { "None" };

        // ---- catalog demand shaping (ProductionManager) ------------------------------------
        // Demand posted per tick = ceil(MissingFraction ├ù (cap - stock) + MinFraction ├ù cap).
        // At zero stock and 1 M cap: ceil(0.01├ù1M + 0.001├ù1M) = 11 000 units/tick.
        public double CatalogDemandMissingFraction = 0.01;  // fraction of deficit to demand per tick
        public double CatalogDemandMinFraction     = 0.001; // fraction of cap as minimum standing demand
        public double CatalogDemandHysteresis      = 0.10;  // suppressed demand resumes only below cap × (1 - this)

        // ---- central bank asset purchases --------------------------------------------------
        // Bank buys CentralBankBuyFraction × stock per cycle from PG glut nodes whose
        // scarcity has fallen below the configured floor, with a minimum lot to avoid trivial buys.
        public double CentralBankBuyFraction = 0.01;  // 1 % of current stock per cycle
        public double CentralBankBuyScarcityFloor = 0.50;
        public long CentralBankBuyMinLot = 100;

        // Optional standing PG surplus intent for ProductionManager.
        public double ProductionSurplusTargetMultiple = 2.0;
        public bool ProductionSurplusIntentEnabled = true;

        /// <summary>Returns the store listing cap for the given Core item kind and, for
        /// ProcessedGood, its finer <see cref="ProcessedGoodCategory"/> — weapons, ammo, and
        /// consumables are sized very differently from bulk components.</summary>
        public long StoreListingCapForItemKind(ItemKind kind, ProcessedGoodCategory category)
        {
            switch (kind)
            {
                case ItemKind.Ore:
                case ItemKind.RareOre:
                case ItemKind.Ingot:
                    return StoreListingCapBulk;
                case ItemKind.ProcessedGood:
                    switch (category)
                    {
                        case ProcessedGoodCategory.Weapon:     return StoreListingCapWeapon;
                        case ProcessedGoodCategory.Ammunition: return StoreListingCapAmmunition;
                        case ProcessedGoodCategory.Consumable: return StoreListingCapConsumable;
                        default:                                return StoreListingCapBulk; // Component
                    }
                default:
                    return StoreListingCapConsumable;
            }
        }

        public int TradeBurstSize;
        public int GuardBurstSize;
        public int PatrolBurstSize;
        public int RaidBurstSize;

        /// <summary>How long a node stays off a Patrol's circuit after one has been sent to it.
        /// Read at the dispatch site rather than through <see cref="CooldownFor"/>, which answers a
        /// different question: that one is the brain's per-action cooldown, and a Patrol intent
        /// stamps <c>FactionState.Cooldowns</c> with it. Both used <see cref="FleetCooldown"/>
        /// before, which at 10 s against a 60 s tick meant a node could be re-patrolled on
        /// consecutive ticks -- METC dispatched to N022 nine times in nine minutes on
        /// 2026-09-11.</summary>
        public TimeSpan PatrolNodeCooldown = TimeSpan.FromSeconds(900);

        /// <summary>Threat a node must still carry, after the arriving sweep's own -0.05, for a Patrol
        /// to stay and garrison it when no raid is actually inbound. <c>0</c> restores the old
        /// stay-wherever-anything-is-left behaviour.
        ///
        /// <para>Defaults to one landed raid's worth (<see cref="RaidThreat"/> 0.35), so at
        /// <see cref="ThreatDecayPerHour"/> 0.7 it means "a raid landed here within the last half
        /// hour". The previous rule was <c>&gt; 0</c>, which was sound while a patrol could only reach
        /// a node scoring above 0.5; a circuit now visits every member of its focus region, and live
        /// on v0.224.0 that put 10 of 12 stands at threat 0.22 or below, five of them below
        /// 0.005.</para></summary>
        public double PatrolStandThreatFloor = 0.35;

        /// <summary>Most nodes one patrol circuit visits before turning for home, the return leg
        /// excluded. A circuit is what makes interception possible at all: a leg's mid-transit
        /// meeting check needs <c>PathNodeIds.Length &gt;= 3</c>, so a sweep with one leg can only
        /// meet something if that single leg happens to route through an intermediate node.</summary>
        public int PatrolCircuitMaxStops = 4;
        /// <summary>Cap on small-grid fighters in a Raid/Retaliate/Conquer squadron's fighter slots
        /// (separate from the 1 large-combat + 1 cargo slot). The posture-scaled desired convoy size
        /// (today's RaidBurstSize × Muster scaling) is clamped to this before squadron assembly.</summary>
        public int RaidSquadronMaxFighters;

        /// <summary>Ceiling on hulls in one Raid/Retaliate/Conquer squadron once it is sized against
        /// the defence it is walking into (<see cref="RaidOverwhelmMargin"/>) rather than against a
        /// fixed roster. <see cref="RaidSquadronMaxFighters"/> still shapes the preferred composition;
        /// this bounds the top-up that follows it, so a swarm of cheap hulls can actually be fielded
        /// where a fighter cap of four would have refused. At or below zero the top-up never runs and
        /// a squadron is exactly the roster it always was. Purely additive vs the current
        /// TuneVersion.</summary>
        public int RaidSquadronMaxHulls = 24;

        /// <summary>The offensive mirror of <see cref="GuardOverwhelmMargin"/>: how far past the
        /// defender's effective HP an attack is sized, so it destroys the defence rather than merely
        /// outlasting it on an average roll. Read through
        /// <see cref="CombatStrength.DamageToDestroy"/>, the same function the defensive marshal uses,
        /// so the two sides of a duel are sized by one rule. At or below zero the dispatch keeps its
        /// posture-scaled roster and asks nothing about the target. Purely additive vs the current
        /// TuneVersion.</summary>
        public double RaidOverwhelmMargin = 1.25;

        /// <summary>Combat hulls wanted per owned node, on top of
        /// <see cref="MusterStockBaseline"/>/<see cref="MusterStockMaxBonus"/> and scaled by
        /// <see cref="FactionState.MilitaryPosture"/>. Ground has to be covered, and a flat target
        /// covers a 122-node estate as thinly as it covers a one-node outpost. 0 restores the flat
        /// target. Purely additive vs the current TuneVersion.</summary>
        public double MusterStockPerNode = 0.05;

        /// <summary>Floor on the quality multiplier war-weariness applies to a retaliation or a
        /// conquest against the rival a faction is tired of. Exhaustion should make answering an
        /// attacker less attractive than it would otherwise be; it must never make it impossible,
        /// which is what a hard <c>fatigue &gt;= 1</c> exclusion did — the faction being conquered was
        /// the one forbidden to take ground back. At 1.0 weariness stops weighing on these two
        /// candidate kinds at all. Purely additive vs the current TuneVersion.</summary>
        public double WarWearyQualityFloor = 0.15;

        /// <summary>Hard ceiling on the combat hull target however the terms above add up — what
        /// stops a large estate under sustained attack from asking its yards for a fleet it would
        /// spend the rest of the world's production on. At or below zero the target is unbounded.
        /// Purely additive vs the current TuneVersion.</summary>
        public int MusterStockCap = 60;

        public int BurstSizeFor(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Trade:  return TradeBurstSize;
                case ActionKind.Guard:  return GuardBurstSize;
                // A reinforce flies a defensive squadron assembled the way a Guard's is, so it is
                // sized by the same knob rather than by one of its own.
                case ActionKind.Reinforce: return GuardBurstSize;
                case ActionKind.Patrol: return PatrolBurstSize;
                case ActionKind.Raid:   return RaidBurstSize;
                case ActionKind.Conquer: return ConquerBurstSize;
                case ActionKind.Gift:   return 1;
                default:                return 1;
            }
        }
        public double RaidInterceptsTradeChance; // base probability a Raid intercepts a Trade at the same node
        public double RaidLootFraction;          // fraction of CargoValue looted from an intercepted Trade

        // Convoy encounter (FAC-306): once the intercept roll passes, the outcome is decided by the
        // victim's firepower against the pirate's (victim DPS / pirate DPS), not by a certainty.
        // An unarmed convoy has no DPS and is always the weaker side.
        public double ConvoyEqualBandLower = 0.67;              // ratio below this: victim weaker
        public double ConvoyEqualBandUpper = 1.5;               // ratio above this: victim stronger; between: a duel
        public double ConvoyWeakHullLossChance = 0.15;          // weaker victim: robbed, and loses the hull at this chance
        public double ConvoyWeakHullLossChanceFriendly = 0.0;   // ... at a node the victim owns or has a pact with
        public double ConvoyDuelHullLossChance = 0.70;          // victim lost the duel: robbed, hull lost at this chance
        public double ConvoyDuelHullLossChanceFriendly = 0.30;  // ... at a friendly node
        public double[] PirateHullLossChances = { 0.9, 0.5, 0.1 }; // a repelled pirate rolls each hull, smallest DPS first; the last entry repeats

        // Route pricing through hostile space (TransitRisk / TransitPlanner): what a carrier expects a
        // trespass catch to cost it, and the bounds on what it does about it. Read only while the
        // fleet-trespass switch is on. New keys, so no TuneVersion gate.

        /// <summary>The chance a carrier whose stay in hostile space outlasts the shadow time is met by
        /// a Guard standing at the node: the catch needs one on duty there at that moment, which the
        /// route cannot know. A starting point to calibrate from <c>TRESPASS_CATCH</c> against
        /// <c>TRANSIT_LEG ... caught=</c> counts.</summary>
        public double TransitCatchOdds = 0.5;

        /// <summary>The longest detour a carrier will consider, as a multiple of the route it replaces.
        /// A bound on the geometry, not a price: within it the fuel decides.</summary>
        public double TransitDetourMaxStretch = 2.0;

        // ship combat: turn-based, range-gated duel (see PendingFleetResolver.ResolveShipDuel).
        // Replaces the old flat GuardCombatChance coin flip — outcomes now depend on each side's
        // WeaponBands (per-weapon DPS/range pairs) instead of a single probability.
        public int CombatTurns;                        // number of resolution turns (range brackets)
        public double[] CombatRangeBracketsM;           // descending range thresholds, one per turn; last should be 0 (point-blank, always in range)
        public double CombatBaseDamageFraction;         // fraction of a side's in-range DPS dealt as damage per turn
        public double CombatVariance;                   // +/- jitter applied per turn's damage roll
        public long StationDefensePower;                // flat, non-range-gated defender contribution for any station-backed node
        /// <summary>How far a Guard's defence must outweigh the attack it is sent against before the
        /// hull is committed. <b>0 or below disables the gate</b>, restoring the always-send
        /// behaviour that preceded it.
        ///
        /// <para><b>Set from the duel's measured outcomes, not from parity.</b> At the shipped combat
        /// tuning a near-parity duel rarely destroys either side — what a defender buys is a reduced
        /// <see cref="PendingFleetAction.ForcedRatio"/>, and what it risks is itself. Against an
        /// attacker of 2500 with a 20-point station: at 0.50 the defender prevents 36% of the damage
        /// and is lost every time; at 0.75, 53% and lost 17% of the time; at <b>0.90, 64% and never
        /// lost</b>; at 1.20, 85%. The cliff is the defender's own survival between 0.6 and 0.9, not
        /// parity — so 0.9 is where a defender stops trading itself away and still prevents most of
        /// the raid.</para>
        ///
        /// <para>The station counts toward the defence before this is applied
        /// (<see cref="CombatStrength.OfDefence"/>), so a node with turrets refuses far less often
        /// than a bare claim does.</para></summary>
        public double GuardCommitRatio = 0.9;
        /// <summary>How far past killing an attacker on the average roll a defence is sized, when
        /// the hulls to do it are there. <b>At or below zero is parity</b> — destruction on the mean
        /// roll, which is a coin flip in practice.
        ///
        /// <para><b>A repelled raider is not a solved problem.</b> <see cref="GuardCommitRatio"/>
        /// above answers whether an exchange is worth taking at all and is deliberately near the
        /// defender's survival cliff; it buys a reduced <see cref="PendingFleetAction.ForcedRatio"/>
        /// and leaves the attacker alive to repair and come back. This is the other bar: where a
        /// faction has the hulls, it fields enough to end the raider instead, which
        /// <see cref="CombatStrength.DuelDamage"/> answers exactly rather than by proxy.</para>
        ///
        /// <para>1.25 is <c>1 / (1 - <see cref="CombatVariance"/>)</c> at the shipped variance of
        /// 0.2: a defence that rolls low on every turn still delivers the attacker's whole effective
        /// HP. Retune the variance and this is the number that has to move with it. Purely additive
        /// vs the current TuneVersion.</para></summary>
        public double GuardOverwhelmMargin = 1.25;

        /// <summary>The offensive commit gate's switch. <b>At or below zero the gate is off</b> and
        /// every assembled squadron is dispatched whatever is standing at its target.
        ///
        /// <para><b>Only the sign is read.</b> What the gate asks is
        /// <see cref="CombatStrength.Survives"/> — the duel's own range-bracketed arithmetic, scaled
        /// by <see cref="RaidSurvivalMargin"/> — because a DPS ratio cannot represent a bracket one
        /// side shoots into and the other cannot. Set the bar with
        /// <see cref="RaidSurvivalMargin"/>; set it to fire at all here.</para></summary>
        public double RaidCommitRatio = 0.6;

        /// <summary>How much of its own effective HP a raid keeps in hand against the defence it can
        /// see, read through <see cref="CombatStrength.Survives"/>. Below 1 it commits only to fights
        /// it wins with room to spare; at 1 it accepts the mean roll and loses half of them.
        ///
        /// <para><b>The reciprocal of <see cref="RaidOverwhelmMargin"/>, and it has to be.</b> That
        /// one sizes a squadron to destroy a defence <em>even rolling low</em>, so it scales the bar
        /// up. This one asks whether the defence kills the squadron <em>rolling high</em>, so it
        /// scales the same bar down. At variance 0.2 a side that rolls high throughout delivers 1.2
        /// of <see cref="CombatStrength.DuelDamage"/>, which 0.8 covers.</para>
        ///
        /// <para>What it costs is legible: the attacker's effective HP is its own total DPS and the
        /// defence gets <see cref="CombatTurns"/> bracketed turns at
        /// <see cref="CombatBaseDamageFraction"/>, so break-even against a defence that reaches every
        /// turn is 1.05x its DPS and against one that misses the longest bracket is 0.70x. This
        /// number is what keeps the commit bar on the far side of that. Purely additive vs the
        /// current TuneVersion.</para></summary>
        public double RaidSurvivalMargin = 0.8;
        /// <summary>Ceiling on the convoys marshalled to one threatened node in one pass. A fleet
        /// carries one origin, so reaching the margin above can take more than one draw — this is
        /// what stops "enough to destroy it" from emptying an estate into a single flashpoint, and
        /// it is the knob to pull back on if defensive spend runs hot. 1 restores the single
        /// dispatch per node that preceded the marshal. Purely additive vs the current
        /// TuneVersion.</summary>
        public int GuardMaxConvoysPerThreat = 3;
        /// <summary>How many nodes may be given a garrison demand in one shipyard pass. The candidate
        /// set is already bounded — only nodes attacked inside
        /// <see cref="DefenceMemoryWindow"/>, themselves capped by <see cref="FightHistoryCap"/> — so
        /// this bounds the demand register rather than the work: a faction under attack everywhere
        /// asks for its worst places first, most recently hit first, instead of filling the register
        /// with a build order per node in one tick. Purely additive vs the current TuneVersion.</summary>
        public int GarrisonNodesPerPass = 3;

        /// <summary>How long a Raid/Retaliate/Conquer holds at its target before its effect lands --
        /// the engagement itself, and the window in which a player can reach the fleet and fight it
        /// (<see cref="FleetStopKind.Engage"/>). The defence duel and the hold damage both fire at
        /// the END of this window, so a raid is contestable for its whole length rather than
        /// resolving on contact. Inline default: a missing JSON key must not collapse the window to
        /// zero. 0 disables the dwell entirely -- raids then resolve on contact, as they did before
        /// this existed -- which is the kill switch if it ever needs turning off live.</summary>
        public double RaidDwellSeconds = 300.0;

        /// <summary>How many <see cref="FleetStopKind.Engage"/> dwells one Raid/Retaliate/Conquer
        /// holds before it goes home — the siege, rather than a single blow. Each dwell fights its
        /// own defence duel and strips its own <see cref="RaidHoldStrip"/>, so an unopposed raid
        /// takes a full-hold node to <see cref="ConquerHoldCeiling"/> in this many windows and the
        /// campaign's next dispatch can be the Conquer that finishes it.
        ///
        /// <para>The siege ends early, before its cycles are spent, when the fleet is down to half
        /// strength, when the node is already at or under the conquer ceiling, or when it has
        /// changed hands. Loot is collected once, on the cycle the raiders actually leave on.</para>
        ///
        /// <para><b>This is the accumulation knob.</b> At 1 a raid behaves exactly as it did before
        /// the siege existed: one strip, then home. Raising it scales how fast territory moves for
        /// every faction, not just against a player. Inline default so a missing JSON key cannot
        /// silently disable the mechanic; values below 1 are read as 1.</para></summary>
        public int RaidSiegeCycles = 3;

        /// <summary>How long a landed attack leaves a node CONTESTED
        /// (<see cref="SpatialNode.ContestedUntilUtc"/>), during which its ship traffic earns no
        /// hold. Decay is unaffected — a contested node loses ground at the ordinary rate and simply
        /// stops healing, which is the opposite of the reinforce grace period's both-off treatment.
        ///
        /// <para><b>Sized against the siege, not against the raid.</b> It has to outlast the gap
        /// between one <see cref="RaidDwellSeconds"/> dwell and the next or a besieged node heals
        /// between its own cycles; much beyond that and one raid mothballs a node's recovery for
        /// hours. <c>&lt;= 0</c> disables the mechanic and restores the previous behaviour exactly,
        /// which is the kill switch if the balance is wrong.</para></summary>
        public double ContestedHoldGainSuppressedSeconds = 900.0;

        /// <summary>How long a node at hold 0 stays with an owner that holds it and can pay, before
        /// decay hands it to <c>UNKN</c>. Counted from when hold reached 0, and the node is not
        /// abandoned while its contest window (<see cref="SpatialNode.ContestedUntilUtc"/>) is still
        /// open either. Inside it the reinforce manager pays for the node the first pass no attack
        /// is in progress (<c>why=hold_grace</c>).
        ///
        /// <para>Abandonment by decay is the path for an owner that does not respond. Without the
        /// window it also fired on a node a raid siege had just stripped — three ratio-1.0 cycles
        /// remove 1.05 hold — one pass after the last cycle, before the paid reinforce that is
        /// refused during the attack could run. <c>&lt;= 0</c> restores that behaviour exactly.</para></summary>
        public double HoldZeroGraceSeconds = 600.0;

        /// <summary>How long an intercepted convoy and the raid that caught it hold at the meeting
        /// node before the encounter is decided (<see cref="FleetStopKind.Intercept"/>). The window
        /// in which a player can reach the two grids and settle it. 0 turns the window off and
        /// resolves the encounter on contact, as it did before FAC-307.</summary>
        public double InterceptWindowSeconds = 300.0;

        /// <summary>Threat added to the meeting node when an interception window opens there
        /// (<see cref="WorldModel.AddThreat(string, double)"/>). A hold is a visit, not a landing:
        /// well under <see cref="RaidThreat"/>, and below the decay's break-even so a corridor node
        /// fades unless raids actually land on it. 0 writes nothing.</summary>
        public double InterceptThreat = 0.15;
        /// <summary>How long a station's safe zone stays DOWN after a raid resolves against its node.
        /// <c>&lt;= 0</c> turns the whole mechanic off and no zone is ever disabled.
        /// <para>A raid that lands is otherwise invisible in-world: the hold number moves and nothing
        /// a player can see changes. A downed safe zone is the consequence made physical, and it is
        /// also a real one — the station is shootable and grindable until it comes back.</para>
        /// <para>Set it very large for "a raided station never recovers protection". 1800 s matches
        /// <see cref="GuardDuration"/>, so a Guard dispatched in response outlives the window it is
        /// covering.</para></summary>
        public double RaidSafeZoneDownSeconds = 1800.0;

        // Player aggression -- a player shooting a faction's station. Until v0.152.0 no such signal
        // reached Core at all: the host's only damage hook tracked materialised FLEET grids, for
        // telling a destroyed convoy from a vanished one.
        /// <summary>Threat added to a node per player-damage flush. The host accumulates raw SE damage
        /// over a tick and flushes once, so this is per flush rather than per shot -- a burst of
        /// autocannon fire and one artillery shell are the same signal at this resolution, which is
        /// the point: what matters is that someone is shooting, not the calibre.</summary>
        public double PlayerAggressionThreatPerFlush = 0.20;
        /// <summary>Minimum raw damage in one flush before it counts as aggression at all. Filters
        /// collision scrapes and a stray round from a real attack.</summary>
        public double PlayerAggressionMinDamage = 100.0;
        /// <summary>How long after a player's last damage a node still counts as under attack for
        /// defender dispatch. Not a decay rate -- <see cref="SpatialNode.Threat"/> decays on its own
        /// at <see cref="ThreatDecayPerHour"/>, an order of magnitude slower than this window; this
        /// bounds the span in which a Guard may be sent specifically at a player.</summary>
        public double PlayerAggressionResponseWindowSeconds = 600.0;

        // ---- player siege of a station safe zone -------------------------------------------------
        // A protected station cannot be damaged at all (its zone forbids Damage, Shooting and
        // Grinding), so without this there is no way for a player to start a fight with a faction
        // that owns one. The siege is how a player initiates; see docs/player-raids.md.
        /// <summary>Damage a player must land on a station's safe zone to empty it. Absorbed rounds
        /// count at their full would-be damage even though none of it reaches the station.</summary>
        public double RaidZoneIntegrityPool = 1000.0;
        /// <summary>Empty-to-full regeneration time when the zone is not being fired on. The pool
        /// refills linearly, so this also sets how much respite a lull buys the attacker.</summary>
        public double RaidZoneIntegrityRegenSeconds = 30.0;
        /// <summary>How long the pool may sit at full before the siege is abandoned. Any fresh
        /// damage clears it; expiry resets the siege dwell to zero and the attacker starts over.</summary>
        public double RaidZoneRaidLapseSeconds = 30.0;
        /// <summary>How long the pool must be held below full before the zone can drop. Mirrors
        /// <see cref="RaidDwellSeconds"/> by design -- breaking a safe zone should cost a player the
        /// same time an NPC raid spends holding station -- but is deliberately its OWN key rather
        /// than a read of that one, because that constant is set by NPC pacing concerns and someone
        /// retuning it should not silently retune this.</summary>
        public double RaidZoneSiegeDwellSeconds = 300.0;
        /// <summary>Block damage a player must do to a station for the raid to succeed. The other
        /// route is powering the station down; either alone is enough.</summary>
        public double PlayerRaidBlockDamageForSuccess = 20000.0;
        /// <summary>How long a player must hold a successfully raided node before it is offered to
        /// them outright.</summary>
        public double PlayerConquerOfferMinutes = 10.0;
        /// <summary>How close a player must come to a hostile node carrying no station before their
        /// presence alone begins a raid on it. An unfounded node has nothing to shoot, so presence
        /// is the whole attack -- see <see cref="PlayerSiege.BeginPresenceRaid"/>. Zero or less
        /// switches the mechanic off entirely.
        /// <para>Its own key rather than a read of <see cref="ObservationRadiusKm"/> (15 km, "close
        /// enough for this node to be a live place") or of PlayerPresenceManager's 5 km "at a
        /// station": taking ground should ask a player to stand on it, which is nearer than
        /// either.</para></summary>
        public double PresenceRaidRadiusKm = 3.0;
        /// <summary>Extra distance a presence raid is held at, past
        /// <see cref="PresenceRaidRadiusKm"/>. A player parked on the boundary would otherwise start
        /// and drop the same raid on alternate ticks; the spawn/despawn pair around
        /// <see cref="ObservationRadiusKm"/> is separated for the same reason.</summary>
        public double PresenceRaidHysteresisKm = 0.5;

        public int TopN;

        // Per-action-kind cooldowns (all real-time), in tuning_defaults.json only -- no inline default.
        // They pace how often a faction may take a structural action, not whether it may: treasury,
        // item stock, ResourceValue and hold each gate the action on its own, so the cooldown is a
        // throttle on re-selection rather than a second pacing layer.
        public TimeSpan ExpandCooldown;
        public TimeSpan ClaimCooldown;
        public TimeSpan DevelopCooldown;
        public TimeSpan WithdrawCooldown;
        public TimeSpan FleetCooldown;
        public TimeSpan NegotiateCooldown;
        public TimeSpan FoundCooldown;
        public TimeSpan ActionCooldown;
        public TimeSpan CampaignCooldown; // new: strategic campaign focus lifetime
        public TimeSpan MusterCooldown;   // per-faction cooldown after a Muster dispatch (mirrors CampaignCooldown's shape)
        // Dedicated FinishBuild cooldown (was: shared FleetCooldown, which at 10s live default was
        // shorter than FactionTickIntervalSeconds and so never actually throttled re-selection).
        // No inline default -- real value lives in tuning_defaults.json, same convention as FleetCooldown.
        public TimeSpan FinishBuildCooldown;

        public double IdleUtility;

        // ---- brain audit fixes (2026-06-25) --------------------------------------------------------
        public double BuildCompletionUrgencyFraction = 0.70; // ship build progress above which FinishBuild fires
        public double RetaliateThreatThreshold       = 0.25; // node threat above which Retaliate is considered
        /// <summary>Multiplier on a Retaliate target's score when we are already hostile to its
        /// owner, so a standing enemy still outranks an equally valuable neutral one when both have
        /// attacked us inside the memory window. 1.0 makes the two indistinguishable.
        /// <para>Provenance: FAC-275 slice 2.</para></summary>
        public double RetaliateHostileTargetQualityBoost = 1.25;
        public int    PersonalityDriftEnemyCount     = 2;    // active-war enemy count before settler/merc correction
        public double PersonalityDriftCautionBoost   = 1.5;  // caution multiplier for over-extended non-aggressive factions
        public double StabilityThreshold             = 0.30; // all-drives ceiling that triggers the Idle steady-state boost
        public double StabilityIdleMultiplier        = 2.0;  // IdleUtility multiplier when all drives are stable

        // P9: grudge memory + truce-breaking
        public double GrudgeRaidQualityBoost  = 0.15; // quality bonus when raiding a known truce-breacher

        /// <summary>Quality bonus on a Raid or Conquer whose target sits in a region an active
        /// <see cref="CampaignObjectiveKind.SeizeRegion"/> objective names — the same shape as
        /// <see cref="GrudgeRaidQualityBoost"/>, and deliberately a **weighting rather than a filter**:
        /// a faction pursuing a beachhead's objective should lean toward it, not ignore a rival
        /// breaking through its back door.
        ///
        /// <para>Larger than the grudge boost because a seize objective is a standing strategic
        /// decision the faction paid a colonisation cost to make, where a grudge is a reaction.</para>
        ///
        /// <para>New key, so no <c>TuneVersion</c> gate: <c>LoadTuning</c> merges defaults and then
        /// lets <c>tuning.json</c> override, and a file that has never heard of this field overrides
        /// nothing. The gate exists for changing a default that is already persisted.</para></summary>
        public double SeizeObjectiveQualityBoost = 0.25;

        /// <summary>Most quality a conquest gains for a region whose space costs this faction's lanes
        /// (<see cref="FactionState.TransitDenial"/>). Half of it applies when the lanes have cost what a
        /// conquest does (<see cref="ConquerCost"/>), approaching all of it beyond. Between the grudge
        /// and the seize boosts: a standing economic reason, not yet a decision paid for. New key, so
        /// no <c>TuneVersion</c> gate.</summary>
        public double LaneConquestQualityBoost = 0.2;

        // ---- offensive campaigns: the tempo and the purse the manager dispatches under -------
        // All four keys are NEW, so each survives on its C# initialiser and none needs a
        // TuneVersion gate -- a tuning.json that has never heard of them overrides nothing.

        /// <summary>Minimum spacing between two dispatches under the <b>same</b> offensive campaign
        /// objective. Replaces the per-faction Raid/Conquer action cooldown as the thing that paces
        /// an offence: a faction running two campaigns fights on two clocks, which is what a campaign
        /// being a place rather than a verb means.
        ///
        /// <para>The inline default matches <see cref="FleetCooldown"/>, the cooldown a brain-chosen
        /// Raid used to take, so the first live measurement starts near the old cadence. It is
        /// <b>not</b> a number to preserve — the real limiter is the spend budget below, and the
        /// resulting raid frequency is meant to be measured live and then tuned.</para></summary>
        public TimeSpan OffensiveTempo = TimeSpan.FromSeconds(10);

        /// <summary>Max share of <c>CentralBankRecentIncome</c> spendable on offensive dispatch per
        /// <see cref="SpendBudgetWindowSeconds"/>, on top of the floor below. The Guard/Patrol
        /// <see cref="SpendBudget"/> triple, applied to the offensive block.</summary>
        public double RaidMaxIncomeFractionPerWindow = 0.3;

        /// <summary>Income-independent minimum offensive budget per window. Sized at
        /// <c>RaidCost</c>'s shipped value so a faction with no income can still mount one raid a
        /// window; both this and the fraction above <c>&lt;= 0</c> = uncapped.</summary>
        public long RaidBudgetFloor = 12000;

        /// <summary>Share of treasury surplus (<see cref="Economy.SpendableTreasurySurplus"/>)
        /// spendable on offensive dispatch per window, on top of income+floor. <c>&lt;= 0</c> = an
        /// income-only budget, which is what Guard ships with.</summary>
        public double RaidWealthFractionPerWindow = 0;

        /// <summary>How long a raid or conquest campaign's focus stands before it closes as expired.
        /// Terror keeps <see cref="TerrorCampaignDuration"/> — the antagonist's cadence is a separate
        /// balance question from an ordinary faction's, and one knob for both would tie them.</summary>
        public TimeSpan OffensiveCampaignDuration = TimeSpan.FromHours(2);

        /// <summary>How long a <b>player-issued</b> campaign objective stands before it closes as
        /// expired. Deliberately longer than <see cref="OffensiveCampaignDuration"/>: an NPC restates
        /// its intent every think, while a player states theirs once and may not be online again
        /// within two hours — an expiry sized for a brain's cadence silently discards a directive
        /// between sessions. Long enough to survive a session gap, short enough that a forgotten
        /// order eventually stops costing the faction money.
        /// <para>Inline default because a missing JSON key must not zero it (a zero duration expires
        /// every directive on the tick it was placed); purely additive vs the current
        /// <see cref="TuneVersion"/>, so no bump.</para></summary>
        public TimeSpan PlayerCampaignDuration = TimeSpan.FromHours(6);

        /// <summary>How many player-issued campaign objectives one faction may hold at once. Sized at
        /// the ceiling of <see cref="CampaignFocusCapacity"/>, which answers the same question for the
        /// defensive side: how many places a faction can be concentrating on.
        /// <para>Inline default because a missing JSON key must not zero it, which would refuse every
        /// directive; purely additive vs the current <see cref="TuneVersion"/>, so no bump.</para></summary>
        public int PlayerCampaignCap = 3;

        // ---- terror campaigns (FAC-268): an implacable faction's offensive shape ---------------
        /// <summary>How long after one terror campaign starts before the next may. "From time to
        /// time" (inline default 6 h; a missing JSON key must not zero it, which would make the
        /// antagonist campaign continuously; purely additive, no TuneVersion bump).</summary>
        public TimeSpan TerrorCampaignCooldown = TimeSpan.FromHours(6);
        /// <summary>How long a terror campaign's focus stands. Raids against the region are
        /// biased for this long; the fleet requirement lasts as long (inline default 2 h).</summary>
        public TimeSpan TerrorCampaignDuration = TimeSpan.FromHours(2);
        /// <summary>Combat hulls a terror campaign wants stocked network-wide while it stands, on
        /// top of the posture-derived target (inline default 3; 0 disables the fleet requirement).</summary>
        public int TerrorCampaignHulls = 3;
        /// <summary>Hostile-owned nodes a region must hold to be worth a campaign (inline default 3;
        /// a single outpost is a raid, not a campaign).</summary>
        public int TerrorCampaignMinRegionNodes = 3;
        /// <summary>Highest <see cref="FactionState.TerrorEscalation"/> (inline default 3). At the cap
        /// a campaign wants (1 + cap × <see cref="TerrorEscalationStep"/>) times its base budget.</summary>
        public int TerrorEscalationCap = 3;
        /// <summary>How much of the base DPS budget each escalation step adds (inline default 0.5:
        /// escalation 2 wants twice the base).</summary>
        public double TerrorEscalationStep = 0.5;
        /// <summary>How long an escalated antagonist left alone takes to calm down by one step
        /// (inline default 24 h). Timed from the last change, not from boot.</summary>
        public TimeSpan TerrorEscalationDecay = TimeSpan.FromHours(24);
        /// <summary>Base budget as a multiple of the target region's defender census — the summed
        /// combat-ship DPS every other faction has stationed there plus the stations' own guns
        /// (inline default 1.0: match what stands in the region). The census is a guess; the
        /// opposition actually met last time (<see cref="FactionState.TerrorMetDps"/>) overrides it
        /// when larger.</summary>
        public double TerrorBudgetRatio = 1.0;
        /// <summary>Fights kept per faction in <see cref="FactionState.Fights"/> (inline default 48).
        /// A campaign of a few hours dispatches a handful; the ring must hold at least one whole
        /// campaign to score it.</summary>
        public int FightHistoryCap = 48;

        // ---- defensive budget (FAC-275): every faction arms against what it has met -------------
        /// <summary>The defensive DPS budget as a multiple of the strongest attacker a faction has
        /// met inside <see cref="DefenceMemoryWindow"/> (inline default 1.0: build to match what
        /// came, the same yardstick <see cref="TerrorBudgetRatio"/> sets for the offence). At or
        /// below 0 no faction carries a defensive budget and the yard builds hull count alone.
        ///
        /// <para>New key, so no <c>TuneVersion</c> gate — the gate exists for changing a default
        /// that is already persisted.</para></summary>
        public double DefenceBudgetRatio = 1.0;
        /// <summary>How far back <see cref="DefenceBudget"/> reads a faction's defending fight
        /// records (inline default 24 h). An attacker met longer ago than this is forgotten, so a
        /// faction left alone lets its budget lapse rather than arming against an old war. At or
        /// below zero the defensive budget is off.</summary>
        public TimeSpan DefenceMemoryWindow = TimeSpan.FromHours(24);

        public TimeSpan GrudgeTtl;                     // how long a grudge persists (default 7 days; set in tuning_defaults.json)
        public double TruceBreachQualityPenalty = 0.35; // score multiplier on a raid candidate targeting an active truce partner

        // ---- predatory raiding (FAC-273) ----------------------------------------------------------
        /// <summary><see cref="Traits.Aggression"/> at or below which a faction mounts no <b>predatory</b>
        /// raid at all — it still raids owners whose standing is below
        /// <see cref="HostileRelationThreshold"/>, which is the whole pre-FAC-273 rule, but it never picks
        /// a target it has no quarrel with. Above the floor, predation ramps linearly to 1.0 at maximum
        /// aggression and multiplies the candidate's want × weakness score.
        ///
        /// <para>0.5 separates the world's two halves as authored: raider (0.9) and factorum (0.9) prey;
        /// default (0.4), settler (0.3) and mercantile personalities do not. Lowering it makes ordinary
        /// factions opportunistic; raising it past 0.9 switches predation off entirely.</para>
        ///
        /// <para>New key, so no <c>TuneVersion</c> gate — the gate exists for changing a default that is
        /// already persisted, and no build has ever written this one. The inline initialiser matters
        /// because a field absent from both tuning files keeps its C# value, and 0 here would make every
        /// faction in the world a predator.</para></summary>
        public double RaidPredationAggressionFloor = 0.5;

        /// <summary>Score multiplier on a predatory raid whose owner the raider holds at a <b>warm</b>
        /// standing, ramped by how warm: no penalty at 0, the full multiplier at or above
        /// <see cref="AllyRelationThreshold"/>. Same shape and value as
        /// <see cref="TruceBreachQualityPenalty"/> and for the same reason — a raider with a fat enough
        /// target will occasionally bite the hand, and a merchant never gets this far because predation
        /// already zeroed its score. Standing modulates the raid; it does not gate it (FAC-273).
        ///
        /// <para>New key, so no <c>TuneVersion</c> gate; see
        /// <see cref="RaidPredationAggressionFloor"/> for why it still carries an inline default.</para></summary>
        public double RaidWarmTargetQualityPenalty = 0.35;

        // P10: embargo mechanism
        public double EmbargoRelationThreshold = -0.15; // relation below this triggers auto-embargo (cold-war band)
        public double EmbargoHysteresis        = 0.05;  // embargo lifts when relation recovers above threshold + hysteresis

        // P12: supply-chain chain bonus
        public double DevelopChainBonus = 0.15; // quality added to Phase-2 Develop when installing cap completes an adjacency chain

        // ---- item economy (Phase 2) ----------------------------------------------------------------
        // Credits are now ONLY earned by selling item surplus above the demand threshold.
        // UpkeepPerSource is retained as a pure credit drain.

        public double SurplusConversionRate;

        public long ExpandItemCostProcessedGood;
        public long FoundItemCostProcessedGood;
        public long FoundItemCostHydrogen;

        // ---- node capability system (Slice 2) ---------------------------------------------------

        /// <summary>Maximum level to which Refinery and RefineryII can be upgraded via the Develop
        /// action. Level 1 = the initial install (6 slots). Each additional level adds 6 more slots.
        /// Default 15 ÔåÆ up to 90 slots per node. Set to 1 to disable upgrades.</summary>
        public int MaxRefineryUpgrades = 15;

        /// <summary>Maximum level to which Manufactory and ManufactoryII can be upgraded via the
        /// Develop action. Same slot-per-level formula as <see cref="MaxRefineryUpgrades"/>.</summary>
        public int MaxManufactoryUpgrades = 5;

        /// <summary>Maximum level to which a Shipyard can be upgraded via the Develop action. Level 1
        /// = the initial install (one concurrent build). Each additional level adds one, through
        /// <see cref="SpatialNode.BuildSlotCount"/>. Set to 1 to disable upgrades.
        ///
        /// <para><b>Raising this past 1 turns on a path, not just a number.</b> Every consumer of
        /// <see cref="MaxLevelFor"/> starts treating a level-1 yard as unfinished:
        /// <c>DevelopReadiness</c> stops answering <c>AlreadyInstalled</c>, the brain's Develop
        /// candidates and <c>InvestmentTargeting</c> can aim at an upgrade, and
        /// <c>ShipyardCoverage.Census</c> marks a station that already has a yard as
        /// <c>Hostable</c>. That last one shares a gate with the anti-sprawl rule, which is why
        /// <c>ShipyardCoverage.Wants</c> still has to refuse a region whose yards are idle -- a
        /// second yard and a bigger yard are both capacity, and neither is earned by a yard nobody
        /// is feeding.</para></summary>
        public int MaxShipyardUpgrades = 3;

        /// <summary>Maximum level to which a FuelDepot can be upgraded via the Develop action. Level
        /// 1 = the initial install (6 O2/H2 Generators). Same slot-per-level formula as
        /// <see cref="MaxRefineryUpgrades"/>.
        ///
        /// <para>A level is 6 slots at 500 L of hydrogen per second each, so 3,000 L/s = 259.2 ML a
        /// day off 12.96 M units of ice. At 1 a depot node's gas output is fixed whatever the burn
        /// is, which is what this key exists to unfix: one level is three orders of magnitude under
        /// the fleet fuel draw <c>docs/fuel-plan.md</c> measures.</para></summary>
        public int MaxFuelDepotUpgrades = 15;

        /// <summary>The ice cover <c>ProductionManager</c>'s FuelDepot growth stage uses when the
        /// region it would build in holds less ice than this share of one level-hour, but the
        /// faction has an open ice extraction order (<see cref="FactionState.ExtractionOrders"/>).
        /// The order is ice on its way, so the request posts, scaled by this share rather than by the
        /// near-zero stock. 0 turns the order path off: a region with no ice held then waits
        /// (<c>THROUGHPUT_HOLD FuelDepot reason=no-ice</c>). A new key, so it needs no
        /// <c>TuneVersion</c> gate.</summary>
        public double FuelDepotOrderedIceCover = 0.1;

        /// <summary>Maximum level to which a Farm can be upgraded via the Develop action. A level is
        /// one irrigation system and the plots it waters (<see cref="MachinePhysics"/>). A new key,
        /// so it needs no <c>TuneVersion</c> gate.</summary>
        public int MaxFarmUpgrades = 15;

        /// <summary>Ice a Farm burns per litre of water SE's farm plot draws. SE's own figures
        /// (<c>PlantWaterConsumptionPerMinute</c> × the seed's <c>WaterConsumptionMultiplier</c> ×
        /// growth time, one ice to one litre through the irrigation system) put a fruit at about 467
        /// ice net of its replant seed — some 23,000 credits of ice at a 50-credit unit for a
        /// 1,240-credit crop. This scales every crop by one factor, so SE's ratios between crops stand
        /// and only the level moves: at 0.025 a fruit costs about 12 ice, roughly half its price.
        /// Applied when the host resolves Farm recipes, so it reaches <c>production_model.json</c>
        /// and the simulator with them. A new key, so it needs no <c>TuneVersion</c> gate.</summary>
        public double FarmIceScale = 0.025;

        /// <summary>Maximum level to which a Habitat can be upgraded. A level is the beds, kitchen and
        /// medical room it installs, and houses <see cref="HabitatBedsPerLevel"/> workers. A new key,
        /// so it needs no <c>TuneVersion</c> gate.</summary>
        public int MaxHabitatUpgrades = 15;

        /// <summary>Workers one Habitat level houses: the beds among the blocks a level installs
        /// (<c>InstallBlocks</c>/<c>InstallExtraBlocks</c> in <c>capability_recipes.json</c>), a bed
        /// being any <c>CryoChamber</c>-typed block. Stamped by the host when it resolves the install
        /// BOMs, like <see cref="InstallBoms"/>; this value stands only where nothing resolved — the
        /// simulator, the tests, or a host whose Habitat blocks are missing. FAC-493.</summary>
        public int HabitatBedsPerLevel = 20;

        /// <summary>Nutrition one worker eats per hour, in SE's <c>Food</c> stat units (1.00 = one
        /// <c>MealPack_SteakDinner</c>, a full stomach). SE's <c>SpaceCharacterFood</c> stat runs 0-100
        /// and decays 0.062 a second, so a full stomach lasts 26.9 minutes: 2.23 an hour, unscaled
        /// (operator, 2026-10-15). FAC-493.</summary>
        public double WorkerNutritionPerHour = 2.23;

        /// <summary>Litres of oxygen one worker breathes per hour: SE's character
        /// <c>OxygenConsumption</c> of 0.063 a second, unscaled (operator, 2026-10-15). FAC-493.</summary>
        public double WorkerOxygenPerHour = 226.8;

        /// <summary>Share of the workers a node cannot feed or give air to that leave per hour. Only
        /// the unfed share leaves, so a short shortage thins a node rather than emptying it; the
        /// count still reaches zero if nothing arrives (operator, 2026-10-15). FAC-493.</summary>
        public double WorkerUnfedLeavePerHour = 0.25;

        /// <summary>Hours of food a Habitat asks to hold for the workers living there, and keeps back
        /// from haulers (<see cref="WorkerFoodDemand"/>). The same horizon as
        /// <see cref="FuelReserveHours"/>: long enough to bridge a haul, short enough that a Farm's
        /// crops are not all held at the node that grew them.</summary>
        public double WorkerFoodReserveHours = 6.0;

        /// <summary>Credits a recruit costs at a node whose body has a civilian pool
        /// (<see cref="PlanetRecord.Recruits"/>). The price of adding a worker to the population;
        /// moving one afterwards is the Taxi's fare (operator, 2026-10-14). FAC-493.</summary>
        public long WorkerRecruitPrice = 100000;

        /// <summary>Returns the maximum capability level for a given kind. The two SensorArray-shaped
        /// capabilities are fixed at 1 (no slot-based upgrades).</summary>
        public int MaxLevelFor(NodeCapabilityKind cap)
        {
            switch (cap)
            {
                case NodeCapabilityKind.Refinery:
                case NodeCapabilityKind.RefineryII:
                case NodeCapabilityKind.PrototechRefinery:
                    return MaxRefineryUpgrades;
                case NodeCapabilityKind.Manufactory:
                case NodeCapabilityKind.ManufactoryII:
                case NodeCapabilityKind.PrototechForge:
                case NodeCapabilityKind.FoodProcessor:
                    return MaxManufactoryUpgrades;
                case NodeCapabilityKind.Shipyard:
                    return MaxShipyardUpgrades;
                case NodeCapabilityKind.FuelDepot:
                    return MaxFuelDepotUpgrades;
                case NodeCapabilityKind.Farm:
                    return MaxFarmUpgrades;
                case NodeCapabilityKind.Habitat:
                    return MaxHabitatUpgrades;
                default:
                    return 1;
            }
        }

        /// <summary>The kinds the brain's exploratory Develop pass may install with nobody asking.
        /// Not every kind: SensorArray is installed by its own path, and <see cref="NodeCapabilityKind.Farm"/>
        /// and <see cref="NodeCapabilityKind.FoodProcessor"/> are demand-driven only (operator,
        /// 2026-10-14) — they install when something wants food, not on speculation. A
        /// <see cref="NodeCapabilityKind.Habitat"/> is a player's install only until workers produce
        /// something (operator, 2026-10-15; FAC-493). Anything that
        /// needs every kind iterates the enum, and anything indexed by ordinal is sized by
        /// <see cref="CapabilityKindCount"/>, never by this list.</summary>
        public static readonly NodeCapabilityKind[] AllCapabilities = new NodeCapabilityKind[]
        {
            NodeCapabilityKind.Refinery,
            NodeCapabilityKind.Manufactory,
            NodeCapabilityKind.Shipyard,
            NodeCapabilityKind.FuelDepot,
            NodeCapabilityKind.ManufactoryII,
            NodeCapabilityKind.RefineryII,
            NodeCapabilityKind.PrototechForge,
            NodeCapabilityKind.PrototechRefinery,
        };

        /// <summary>One past the highest <see cref="NodeCapabilityKind"/> ordinal: the size of any
        /// table indexed by <c>(int)cap</c>.</summary>
        public static readonly int CapabilityKindCount = MaxCapabilityOrdinal() + 1;

        private static int MaxCapabilityOrdinal()
        {
            int max = 0;
            foreach (NodeCapabilityKind k in System.Enum.GetValues(typeof(NodeCapabilityKind)))
                if ((int)k > max) max = (int)k;
            return max;
        }

        // Install costs: [NodeCapabilityKind index] ÔåÆ item quantity required.
        // Refinery costs Ore; Manufactory costs Ingot; Shipyard costs Ore + ProcessedGood;
        // FuelDepot costs Hydrogen; ManufactoryII costs more PG (full assembler);
        // RefineryII costs PG (advanced manufactured parts ÔÇö not Ore; a precious-metal
        // refinery upgrade requires machined components, not raw iron ore).
        // PrototechForge and PrototechRefinery carry a flat cost as well as a real InstallBom, so a
        // host that never resolved their SE blocks charges something rather than installing free.
        // Farm and FoodProcessor the same: their real BOM is the irrigation system and the food
        // processor block, and this is only what a host that never resolved either one charges.
        // Habitat likewise: its real BOM is the medical room, kitchen and beds.
        //                                                    Ref  Man  Shp  Fuel MkII RefII Sens Forge PRef Farm Food Hab
        private static readonly long[] s_installOre   = new long[] { 300,   0, 100,   0,   0,   0,   0,   0,   0,   0,   0,   0 };
        private static readonly long[] s_installIngot = new long[] {   0, 200,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0 };
        private static readonly long[] s_installPG    = new long[] {   0,   0, 300,   0, 400, 300,   0, 600, 500, 150, 150, 150 };
        private static readonly long[] s_installH2    = new long[] {   0,   0,   0,  50,   0,   0,   0,   0,   0,   0,   0,   0 };

        public long InstallCostFor(NodeCapabilityKind cap, ItemKind kind)
        {
            int i = (int)cap;
            switch (kind)
            {
                case ItemKind.Ore:           return i < s_installOre.Length   ? s_installOre[i]   : 0;
                case ItemKind.Ingot:         return i < s_installIngot.Length ? s_installIngot[i] : 0;
                case ItemKind.ProcessedGood: return i < s_installPG.Length    ? s_installPG[i]    : 0;
                case ItemKind.Hydrogen:      return i < s_installH2.Length    ? s_installH2[i]    : 0;
                default: return 0;
            }
        }

        // Optional real component BOM (invKey e.g. "Comp:SteelPlate" -> count) for a capability's
        // install cost, resolved host-side from an SE cube block's actual construction cost
        // (Factions.Core has no SE APIs, so this stays empty unless the host populates it — see
        // FactionsPlugin.ResolveInstallBoms). A capability with an entry here should be charged via
        // its specific components everywhere (ProductionManager reservation/demand, FactionBrain
        // afford, FactionRuntime gate/deduct) instead of InstallCostFor's flat ItemKind costs — this
        // is what stops an unrelated component (e.g. a scarce ship part) from being swept up to pay
        // for a capability whose real-world block never needed it.
        public Dictionary<NodeCapabilityKind, Dictionary<string, long>> InstallBoms { get; set; }
            = new Dictionary<NodeCapabilityKind, Dictionary<string, long>>();

        /// <summary>Per-block emission multipliers, keyed by SE <c>SubtypeId</c>. Better tech is
        /// quieter: a prototech drive radiates less than its vanilla counterpart for the same work.
        ///
        /// <para><b>Keyed on subtype, never on block type</b> — prototech and vanilla drives share
        /// <c>MyJumpDrive</c> and <c>MyThrust</c>, so a type-keyed table would silently give a refit
        /// nothing. An unlisted subtype scores 1.0, so an unknown block is <i>loud by default</i>
        /// rather than silently free; that is the same "unknown, do not gate" direction the rest of the
        /// sensing model takes.</para>
        ///
        /// <para>Data rather than code, so a modded drive can be tuned without a rebuild. Prototech is
        /// rare and expensive, which makes quiet something a player earns instead of something they get
        /// for flying a small hull — a counterweight to the mass floor on a different axis.</para></summary>
        public Dictionary<string, double> SignatureBlockEmission { get; set; }
            = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "LargePrototechJumpDrive",    0.55 },
                { "SmallPrototechJumpDrive",    0.55 },
                { "LargeBlockPrototechThruster", 0.60 },
                { "SmallBlockPrototechThruster", 0.60 },
                { "LargePrototechReactor",      0.65 },
                { "LargeBlockPrototechBattery", 0.65 },
                { "SmallBlockPrototechBattery", 0.65 },
            };

        /// <summary>Emission multiplier for a block subtype; 1.0 for anything unlisted.</summary>
        public double BlockEmissionFor(string subtypeId)
        {
            double m;
            if (string.IsNullOrEmpty(subtypeId) || SignatureBlockEmission == null
                || !SignatureBlockEmission.TryGetValue(subtypeId, out m) || m <= 0)
                return 1.0;
            return m;
        }

        public bool TryGetInstallBom(NodeCapabilityKind cap, out Dictionary<string, long> bom)
        {
            return InstallBoms.TryGetValue(cap, out bom) && bom != null && bom.Count > 0;
        }

        // ---- machine physics ----------------------------------------------------------------------

        /// <summary>Per-capability machine model, resolved host-side from the SE block definition and
        /// world session settings (see <c>FactionsPlugin.ResolveMachineProfiles</c>). Empty in Core-only
        /// hosts (simulator, tests), where <see cref="MachineProfileFor"/> falls back to
        /// <see cref="MachinePhysics.DefaultProfile"/>. Serialized like <see cref="InstallBoms"/>, so a
        /// stamped profile survives a <c>factions reload</c> and can be hand-edited between reloads.</summary>
        public Dictionary<NodeCapabilityKind, MachineProfile> MachineProfiles { get; set; }
            = new Dictionary<NodeCapabilityKind, MachineProfile>();

        /// <summary>Modelled upgrade fitment: the additive Productivity bonus one machine carries.
        /// SE's speed module is +0.5 per upgrade port and a fully connected module occupies two ports,
        /// so four speed modules = 8 ports = 4.0. Not an SE constant — no block exposes it — so it is a
        /// plain tunable, reloadable without a rebuild. Default 4.0 = four fully connected speed
        /// modules, the loadout a player refinery/assembler is normally built with.</summary>
        public double ProductionProductivityBonus = 4.0;

        /// <summary>Planning horizon for ProductionManager's per-recipe slot cap: a recipe holds at
        /// most as many machines as its current input can keep busy for this long. Default 10 s.</summary>
        public double ProductionSlotInputHorizonSeconds = 10.0;

        /// <summary>How many production intervals of real time a machine slot may be credited in one
        /// pass. Default 2.0: a normal pass credits its full elapsed time, while a cold start or a
        /// long server outage is bounded to one extra interval instead of minting hours of output.
        /// Lower than 1.0 silently under-credits every pass — the defect this replaced.</summary>
        public double ProductionCreditCapIntervals = 2.0;

        /// <summary>Real seconds between production passes, resolving the "0 = use the default"
        /// convention the host schedules on. Single-sourced here so Core's credit window and the
        /// host's scheduler cannot disagree about the cadence.</summary>
        public double ProductionIntervalSecondsOrDefault()
        {
            return ProductionIntervalSeconds > 0 ? ProductionIntervalSeconds : DefaultProductionIntervalSeconds;
        }

        /// <summary>Code default for <see cref="ProductionIntervalSeconds"/> when unset.</summary>
        public const double DefaultProductionIntervalSeconds = 30.0;

        /// <summary>The stamped profile for a capability, or the Core default when the host has not
        /// stamped one (or stamped an incomplete one).</summary>
        public MachineProfile MachineProfileFor(NodeCapabilityKind cap)
        {
            MachineProfile profile;
            if (MachineProfiles != null && MachineProfiles.TryGetValue(cap, out profile)
                && profile != null && profile.BaseSpeed > 0 && profile.MachineCount > 0)
                return profile;
            return MachinePhysics.DefaultProfile(cap);
        }

        // Capability upkeep: item units drained per economy quantum from a physical station's inventory.
        // Small steady cost to run installed infrastructure; starvation ÔåÆ abandonment in Slice 3.
        // Item-based capability upkeep is intentionally disabled. Industrial stock should only move
        // through recipes, installation/build costs, and explicit transfers.
        public long CapabilityUpkeepFor(NodeCapabilityKind cap, ItemKind kind)
        {
            return 0;
        }

        // Capability recipe input: item units consumed per production cycle as raw material inputs.
        // Used by PriceOracle to compute per-node ScarcityReferenceStock (what a node needs to keep
        // running at full throughput) without a recipe-registry walk.
        // Index = (int)NodeCapabilityKind: [Refinery=0, Manufactory=1, Shipyard=2, FuelDepot=3,
        // ManufactoryII=4, RefineryII=5, SensorArray=6, PrototechForge=7, PrototechRefinery=8,
        // Farm=9, FoodProcessor=10]. A Farm's input is ice and its crops are ProcessedGood; a
        // FoodProcessor consumes crops and makes meals and seeds, all ProcessedGood.
        //                                                            Ref Man Shp Fuel MkII RefII Sens Forge PRef Farm Food
        private static readonly long[] s_capInputOre     = new long[] { 100,  0,   0, 100,  0,   0,   0,   0,   0,  50,   0 };
        private static readonly long[] s_capInputIngot   = new long[] {   0, 50,   0,  0,  30,   0,   0,  30,   0,   0,   0 };
        private static readonly long[] s_capInputPG      = new long[] {   0,  0,  20,  0,  10,   0,   0,   0,   0,   0,  20 };
        // FuelDepot's own input is ice, above -- it is the one capability that PRODUCES hydrogen, so
        // its row here is 0 and its opinion about holding gas comes from s_capOutputH2/O2 instead.
        private static readonly long[] s_capInputH2      = new long[] {   0,  0,   0,  0,   0,   0,   0,   0,   0 };
        private static readonly long[] s_capInputRareOre = new long[] {   0,  0,   0,  0,   0,  50,   0,   0,  50 };

        // Capability recipe OUTPUT: item units produced per cycle that sit on the node awaiting
        // collection. Also a reference-stock term, for the same reason the input table is one -- a
        // node's shelf has to cover what it makes as well as what it consumes, or its own output
        // reads as surplus the moment any accumulates. Without this a Manufactory holds no opinion
        // about components at all: CapabilityInputFor(Manufactory, ProcessedGood) is 0 because a
        // Manufactory consumes ingots, so every component it produced fell through to the flat
        // ReferenceStockProcessedGood floor, went "oversupplied" at twice that, and stopped being
        // produced at all at scarcity <= 1.0 (ProductionManager.cs:1131).
        // Refinery/RefineryII carry the same shape for Ingot output: they consume Ore/RareOre, so
        // CapabilityInputFor(Refinery, Ingot) is 0 and a refinery's own output had no reference term
        // at all. Live 2026-08-28, every one of the ten ingot subtypes at all 13 refinery nodes sat
        // above the flat ReferenceStockIngot floor of 60 -- Nickel 29,855, Cobalt 24,803, down to
        // Iron 121 -- so scarcity was <= 1.0 network-wide and 13 of 144 installed refinery machines
        // held a recipe. Each row is half its capability's own input row (Ore 100 -> 50,
        // RareOre 50 -> 25), the ratio s_capOutputPG already uses against s_capInputIngot.
        private static readonly long[] s_capOutputPG     = new long[] {   0, 25,   0,  0,  25,   0,   0,  25,   0,  25,  25 };
        private static readonly long[] s_capOutputIngot  = new long[] {  50,  0,   0,  0,   0,  25,   0,   0,  25 };
        // FuelDepot, read off SE rather than chosen: its 100-ice input row above times the generator's
        // own IceToGasRatio, 20 for hydrogen and 10 for oxygen. Gas is the one kind whose two recipes
        // share a single input stream -- a generator makes whichever gas is being drawn -- so these
        // are alternatives, not co-products, and each is sized as if the whole input went to it.
        private static readonly long[] s_capOutputH2     = new long[] {   0,  0,   0, 2000, 0,   0,   0,   0,   0 };
        private static readonly long[] s_capOutputO2     = new long[] {   0,  0,   0, 1000, 0,   0,   0,   0,   0 };

        /// <summary>Item units of <paramref name="kind"/> that one level of <paramref name="cap"/>
        /// produces per cycle. Zero for every pairing the capability does not make.</summary>
        public long CapabilityOutputFor(NodeCapabilityKind cap, ItemKind kind)
        {
            int i = (int)cap;
            switch (kind)
            {
                case ItemKind.ProcessedGood: return i < s_capOutputPG.Length    ? s_capOutputPG[i]    : 0;
                case ItemKind.Ingot:         return i < s_capOutputIngot.Length ? s_capOutputIngot[i] : 0;
                case ItemKind.Hydrogen:      return i < s_capOutputH2.Length    ? s_capOutputH2[i]    : 0;
                case ItemKind.Oxygen:        return i < s_capOutputO2.Length    ? s_capOutputO2[i]    : 0;
                default: return 0;
            }
        }

        public long CapabilityInputFor(NodeCapabilityKind cap, ItemKind kind)
        {
            int i = (int)cap;
            switch (kind)
            {
                case ItemKind.Ore:           return i < s_capInputOre.Length     ? s_capInputOre[i]     : 0;
                case ItemKind.Ingot:         return i < s_capInputIngot.Length   ? s_capInputIngot[i]   : 0;
                case ItemKind.ProcessedGood: return i < s_capInputPG.Length      ? s_capInputPG[i]      : 0;
                case ItemKind.Hydrogen:      return i < s_capInputH2.Length      ? s_capInputH2[i]      : 0;
                case ItemKind.RareOre:       return i < s_capInputRareOre.Length ? s_capInputRareOre[i] : 0;
                default: return 0;
            }
        }

        // ---- Free-mint tables (all tunable via tuning.json) --------------------------------------

        /// <summary>Multiplier on base <see cref="ItemYieldFor"/> for what a node mints for free,
        /// keyed <see cref="ItemKind"/>.ToString(). One global row: a node's magnitude comes from its
        /// own <see cref="SpatialNode.ResourceValue"/> and whether it has a deposit at all, not from a
        /// label. Read through <see cref="NodeContent.FreeMintScaleFor"/>, which is what applies the
        /// mineability gate. See docs/issues/FAC-102.
        /// <para><b>A kind absent from this dictionary mints nothing and is worth nothing.</b>
        /// <see cref="FreeMintScaleFor"/> returns 0 for a missing key, and that zero is what stops
        /// <see cref="Economy.NodeItemIncome"/> crediting the kind — which is why removing a channel
        /// means removing its key here as well as its yield-table row. Ore and RareOre are the only
        /// keys shipped since FAC-233 retired the Ingot channel.</para></summary>
        public Dictionary<string, double> FreeMintScale;

        /// <summary>What any node mints for free, before its deposit replaces the Ore and RareOre
        /// channels. Weights within each <see cref="ItemKind"/> should sum to 1.0.
        /// <para>One table rather than one per kind, and what survives in it is a single row: the
        /// effort-gated <c>Ore:Stone</c> a Mine run can take. Stone is the one thing that is not
        /// geography — it is the base voxel material anywhere there is rock at all — so it is the one
        /// thing a table can still state, and <see cref="NodeYieldEntry.MineOnly"/> is what keeps it
        /// a mining target rather than a faucet.</para>
        ///
        /// <para><b>There is no Ingot row and no Ingot scale.</b> Gravel used to be minted here on
        /// every owned node; it is now mined as <c>Ore:Stone</c> and refined by <c>StoneOreToIngot</c>
        /// like any other material. Removing a persisted row is what
        /// <c>Persistence.RemoveIngotFreeMintChannel</c> exists for — the null-check fallbacks below
        /// it only fill an absent key. See FAC-233.</para>
        ///
        /// <para>The eight per-kind rows this replaced each carried the same curated five-ore fallback
        /// mix. That mix is gone: a node's ores come from its deposit, and a node with no deposit has
        /// no ore, rather than the same invented Iron/Nickel/Cobalt/Silicon/Magnesium blend everywhere.
        /// Deleting it is what finishes the curated-list removal the deposit model began.</para></summary>
        public NodeYieldEntry[] NodeYieldTable;

        /// <summary>How well a site suits a capability, keyed
        /// <see cref="NodeCapabilityKind"/>.ToString() -> condition -> affinity (0..1).
        /// <para>Conditions are world readings, not labels: <c>IceDeposit</c>, <c>Mineable</c>,
        /// <c>OnBody</c>, <c>Default</c>, tried in that order by
        /// <see cref="NodeContent.CapabilityAffinityFor"/>. A row names only the conditions its
        /// capability actually cares about.</para></summary>
        public Dictionary<string, Dictionary<string, double>> CapabilityAffinity;

        /// <summary>Fallback radius for a node's deposit sample, in km, used only when its zone has
        /// no separation to derive from. The live value comes from
        /// <see cref="OreSampleRadiusKmFor"/>; this is the floor under a malformed configuration,
        /// not the number the sampler normally uses.</summary>
        public double OreSampleRadiusKm = 7.5;

        /// <summary>Fraction of a node's separation that its deposit sample covers. At 0.5 a node
        /// owns the ground nearer to it than to any neighbour, so adjacent catchments tile without
        /// sampling the same ground twice.</summary>
        public double OreSampleSeparationFraction = 0.5;

        /// <summary>Metres between ore-map samples. Probe counts are derived from this and the area
        /// actually being covered, so a wider catchment samples more ground
        /// rather than the same ground more thinly. Deliberately coarser than the ~23 m ore-map texel:
        /// a deposit too small to be found on this grid is too small to be worth a mining run.</summary>
        public double OreProbeSpacingM = 92.0;

        /// <summary>Smallest fraction of a node's ground a survey must be able to resolve before it
        /// stops — the one number that says what "surveyed" means. 0.0008 = an ore covering 0.08% of
        /// the ground is known to within a tenth of that, and asks for ~338,000 probes.
        ///
        /// <para><b>Set at the map's own resolution, which is the real ceiling.</b> A 7.5 km
        /// catchment holds roughly 334,000 ore-cubemap texels at ~23 m each, so a survey past that
        /// re-reads ground it has already read and learns nothing further. A target asking for more
        /// would look like a longer survey and be sampling noise.</para>
        ///
        /// <para><b>0.05 left the survey nothing to do at all.</b> It asked for 5,141 probes while a
        /// planet catchment's <i>first</i> sample is 9,279 at 5 km and 20,878 at 7.5 km, so every
        /// planet node was born already surveyed and the paced survey never ran on one — confirmed
        /// live on four nodes, all pegged. A stopping condition satisfied by the opening move is not a
        /// stopping condition.</para>
        ///
        /// <para>This is the survey's total work; <see cref="SurveyDurationHours"/> sets how long
        /// that work is spread over. See FAC-101.</para></summary>
        public double SurveyTargetShare = 0.0008;

        /// <summary>Fraction of a node's survey that survives abandonment. Losing a node loses part
        /// of what was learned about it — surveying is work a faction did, and walking away from the
        /// ground forfeits some of it.
        /// <para>Scales the sample, not the conclusion: composition and density are unchanged, only
        /// the certainty behind them, so a reclaimed node still knows what its ores are and has to
        /// re-earn how sure it is. At a week-long full survey and 0.5 this costs days, which is what
        /// makes it a consequence rather than a formality. <i>Operator direction,
        /// 2026-08-26.</i></para></summary>
        public double SurveyRetainedOnAbandonment = 0.5;

        /// <summary>Survey probes issued per update tick, across the whole estate. The survey's total
        /// work is fixed by <see cref="SurveyTargetShare"/>; this only decides how fast it gets there.
        /// <para><b>The pacing lever, because the target is capped by the map's resolution.</b> A
        /// survey cannot be made to last longer by asking for more probes than the catchment has
        /// distinct texels — past that it re-reads the same ground. So duration is set here instead,
        /// as a duration: the per-visit probe budget is derived from it and the live rotation size by
        /// <see cref="SurveyMath.ProbesPerVisit"/>, so the figure an operator sets stays true as the
        /// estate grows. <i>Operator direction, 2026-08-31: full survey in about two days.</i></para>
        /// </summary>
        public double SurveyDurationHours = 48.0;

        /// <summary>Ceiling on the per-visit probe budget <see cref="SurveyMath.ProbesPerVisit"/> may
        /// derive, so one frame's ore-map reads stay bounded however large the estate or however short
        /// <see cref="SurveyDurationHours"/>. A node's own first sample already reads 9,279 points at
        /// 5 km in a single call, so this is well inside what one frame is known to absorb.</summary>
        public int SurveyMaxProbesPerTick = 4096;

        /// <summary>Least ground a re-survey covers before it replaces a stale deposit
        /// (<see cref="WorldModel.TryResurveyStep"/>), however little the old deposit had looked at.
        /// 20,878 is what a first sample reads over a 7.5 km catchment, so a node that was only ever
        /// sampled is re-read at least that well. New key, no <c>TuneVersion</c> bump needed.</summary>
        public int ResurveyMinimumPoints = 20878;

        /// <summary>Consecutive refusals by the ore seam after which a node leaves the survey
        /// rotation until the next restart.
        /// <para>A refusal is a property of where the node is, not of when it was asked: the seam
        /// declines a position no reader covers, and nothing at runtime moves the node back. Without
        /// this such a node is re-offered on every pass forever, and the survey spends that share of
        /// its budget learning nothing. Above 1 so a transient refusal does not evict a node that a
        /// later pass could still answer for.</para></summary>
        public int SurveyDeclinesBeforeParking = 3;

        /// <summary>Metres between surface-material samples. Two orders of magnitude coarser than
        /// <see cref="OreProbeSpacingM"/> because the features differ in scale by that much — an ore
        /// vein is a texel, a surface layer is a lake or a biome. Live evidence for the value: a
        /// whole-body sweep of Kor at ~2.1 km spacing resolved its ice; Tellus at ~6.6 km did not.</summary>
        public double SurfaceProbeSpacingM = 2000.0;

        /// <summary>Ceiling on derived ore-probe counts. Area grows as the square of the radius, so
        /// without a cap a large catchment is unbounded work.</summary>
        public int OreProbeMaxPoints = 65536;

        /// <summary>Ceiling on derived surface-probe counts. Lower than the ore cap because a surface
        /// probe costs microseconds where an ore-map read costs nanoseconds.</summary>
        public int SurfaceProbeMaxPoints = 4096;

        /// <summary>Deposits sampled per update tick by the backfill pass. One is the deliberate
        /// default: a sample is an area scan over the ore cubemap, and the pass runs on the update
        /// tick. A world's whole estate drains in minutes at this rate and there is nothing to gain
        /// from draining it in seconds. 0 disables the pass. See FAC-083.</summary>
        public int DepositBackfillPerTick = 1;

        /// <summary>Subtype id of ice. A deposit dominated by it labels its node
        /// an ice field. Configurable rather than a literal, but it is one name —
        /// ice is a stable SE concept and <c>AsteroidFieldPlanner</c> already treats it as one.</summary>
        public string IceOreSubTypeId = "Ice";

        /// <summary>How many times the baseline ore's unit price a deposit must be worth, per unit,
        /// before its deposit reads as rare.
        /// <para>This is what replaced the hardcoded three-entry rare list. Rarity is now a
        /// continuous, world-derived property — a body carrying enormous platinum reserves prices
        /// platinum lower, and its deposits stop reading as exceptional, which a global boolean could
        /// never express. The label is descriptive only: it carries no yield consequence.</para>
        /// <para><b>2.0, on the ore scale.</b> The 4.0 it replaced was read against ingot prices, under
        /// which 626 of the 789 sampled live nodes labelled rare. Priced as ore, SE's ores run 1x
        /// (iron, nickel, silicon) to 3.5x (platinum, uranium) and no deposit reaches 4.0; over the
        /// same 789 nodes the share-weighted ratio runs 0.5 / 1.5 / 1.85 / 3.5 (min / p50 / p75 /
        /// max), and 2.0 labels the 70 whose mix is worth at least twice iron. Through the TuneVersion
        /// gate, since every live tuning.json persists 4.0. See docs/issues/FAC-191.</para></summary>
        public double RareDepositValueRatio = 2.0;

        /// <summary>Ceiling on a mineable node's <see cref="SpatialNode.ResourceValue"/>. Applied by
        /// <see cref="NodeContent.ResourceValueCap"/>, which replaced the per-kind table that used to
        /// set it (inline default: missing JSON key must not zero it; purely additive vs the current
        /// TuneVersion, so no bump needed).</summary>
        /// <summary>What a mineable node is worth before anything is known about its ground. Low on
        /// purpose: an unsurveyed deposit is an unproven one, and <see cref="SpatialNode.ResourceValue"/>
        /// rises toward what the survey actually measures. Above zero so an unproven node still mints
        /// something and still interests a brain — a node worth literally nothing would never be
        /// claimed, and so would never be surveyed.</summary>
        public double UnsurveyedResourceValue = 0.2;

        /// <summary>Value-of-ore-per-probe at which a fully surveyed node reaches
        /// <see cref="MineableResourceValueCap"/>. Below it worth scales <b>logarithmically</b>, not
        /// linearly — see <c>NodeContent.ProvenShare</c> for why, and
        /// <c>DepositModel.TryValuePerGround</c> for the quantity.
        ///
        /// <para><b>Renamed from <c>ResourceValueSaturationRatio</c>, deliberately.</b> That knob
        /// measured value-per-unit-<i>ore</i>; this measures value-per-unit-<i>ground</i>. The units
        /// are different, so a value persisted against the old name must not be silently reinterpreted
        /// against the new one — renaming makes a stale entry fall back to this default instead.</para>
        ///
        /// <para><b>Measured on the ore scale, 2026-10-09, over the 789 sampled nodes of the live
        /// estate.</b> The top of the measured value-per-ground range, the same rule the 9000 before it
        /// followed: min 0.72 / p25 6.0 / p50 16.2 / p75 57.5 / max 138.5. That 9000 and the "a real
        /// mix averages ~100x iron" behind it were read off ingot prices, which put the estate in two
        /// clusters three orders apart by ore identity; priced as ore it is one spread over five
        /// octaves, and 140 lands it at p25 0.39 / p50 0.58 / p75 0.82 with nothing at the ceiling.
        /// See docs/issues/FAC-191.</para></summary>
        public double ResourceValueSaturationPerProbe = 140.0;

        /// <summary>Whether <see cref="WorldModel.ExpandFrom"/> prefers a candidate position that
        /// could hold ore over one that provably cannot, ranking that ahead of roominess.
        ///
        /// <para>Off, expansion picks the candidate furthest from every existing node — which in open
        /// space is exactly the ground that has no deposit by design, so the roomiest answer and the
        /// most worthless one are the same answer. On, roominess still decides among candidates that
        /// agree, so a faction with nothing ore-bearing in reach expands exactly as it did before.</para>
        ///
        /// <para>Coverage only, and deliberately: it cannot tell a rich point from a poor one on the
        /// same body — see <see cref="IOreGeography.CouldBearOre"/>. New key, so a persisted
        /// tuning.json falls back to this initialiser and no <c>TuneVersion</c> bump is needed.</para></summary>
        public bool ExpandPrefersGroundThatCouldBearOre = true;

        /// <summary>What <see cref="SpatialNode.Richness"/> a node with a deposit reads before its
        /// ground has been measured. The same "unproven is not worthless" argument
        /// <see cref="UnsurveyedResourceValue"/> makes, for the physical channel: above zero so an
        /// unsurveyed node still mints something and is still worth surveying.
        ///
        /// <para><b>The floor is a starting point, not a guarantee.</b> A node <i>proven</i> thin
        /// settles below it — live, the 23-ore mixed nodes measure ~1 band-metre per probe and land
        /// near 0.05. That is the honest answer and the opposite of
        /// <see cref="UnsurveyedResourceValue"/>'s known problem, where ice's proven worth (0.181)
        /// sits under the floor so ordinary ground gets <i>worse</i> as it is surveyed. Ordinary
        /// ground measures 8-17 band-metres and lands 0.36-0.79 at the shipped saturation, clear of
        /// this floor in the right direction.</para>
        ///
        /// <para>New key, so a persisted tuning.json falls back to this initialiser and no
        /// <c>TuneVersion</c> bump is needed.</para></summary>
        public double RichnessFloor = 0.2;

        /// <summary>Ceiling on <see cref="SpatialNode.Richness"/>. New key, no <c>TuneVersion</c>
        /// bump needed.</summary>
        public double RichnessCap = 1.0;

        /// <summary>Band-metres of ore per probe at which a fully surveyed node reaches
        /// <see cref="RichnessCap"/> — <b>the world's ore-abundance knob</b>, and the one number an
        /// operator turns to make the whole map richer or leaner. Lower is more generous.
        ///
        /// <para><b>Linear, unlike <see cref="ResourceValueSaturationPerProbe"/>, because the
        /// quantity is.</b> Value-per-ground spans four orders of magnitude and needs compressing;
        /// density does not. Measured over the 654 well-surveyed nodes of the live estate
        /// 2026-08-31: min 0.63, p10 8.00, p50 12.00, p90 17.29, max 21.29 — one and a half decades,
        /// with 85% of the mass between 8 and 17. A log map here would flatten exactly the range that
        /// has to discriminate.</para>
        ///
        /// <para><b>22 is mint-neutral, chosen so this lands as a redistribution rather than an
        /// inflation.</b> Summed over all 1,039 live nodes, richness at this saturation totals 377.1
        /// against the 374.4 the same nodes carry as ResourceValue today (1.01x), so the world mints
        /// about what it does now while the share moves from rare ground to thick ground. The
        /// measured maximum is 21.29, so nothing live reaches the cap and the top of the range stays
        /// available for richer ground than currently exists.</para>
        ///
        /// <para>New key, no <c>TuneVersion</c> bump needed.</para></summary>
        public double RichnessSaturationMetresPerProbe = 22.0;

        public double MineableResourceValueCap = 1.0;

        /// <summary>Ceiling on the <see cref="SpatialNode.ResourceValue"/> of a node with no ore that
        /// still sits on or around a body — somewhere industry and habitation could stand. Open space
        /// with no ore caps at zero and is not tunable: there is nothing there to value (inline
        /// default: missing JSON key must not zero it; purely additive vs the current TuneVersion, so
        /// no bump needed).</summary>
        public double InhabitableResourceValueCap = 0.8;

        /// <summary>Physical asteroid-field sizing for a node that has ore under it. One profile, not
        /// one per label: rock goes where there is a deposit, and how much is the node's own
        /// ResourceValue.</summary>
        public NodeKindAsteroidFieldEntry AsteroidField;

        /// <summary>Per-tick cap on new asteroid-field materializations, across all nodes combined --
        /// a safety valve so a large batch of newly-eligible nodes (e.g. right after this feature
        /// first ships to an existing world) can't synchronously build many asteroids in one Torch
        /// tick. See <see cref="AsteroidFieldPlanner"/>.</summary>
        public int MaxAsteroidMaterializationsPerTick = 1;

        // ---- accessors -------------------------------------------------------------------------

        /// <summary>The global free-mint multiplier for <paramref name="ik"/>. Ungated: callers that
        /// mint go through <see cref="NodeContent.FreeMintScaleFor"/>, which applies the deposit
        /// test.</summary>
        /// <summary>Litres of ore per cubic metre of rock for <paramref name="subTypeId"/> (an
        /// inventory key such as <c>Ore:Iron</c>). Unknown ores take
        /// <see cref="DefaultOreDensityRatio"/> -- never 0, which would make the ore unmineable
        /// rather than merely poor.</summary>
        public double OreDensityRatioFor(string subTypeId)
        {
            if (OreDensityRatio == null || string.IsNullOrEmpty(subTypeId)) return DefaultOreDensityRatio;
            double v;
            return OreDensityRatio.TryGetValue(subTypeId, out v) ? v : DefaultOreDensityRatio;
        }

        public double FreeMintScaleFor(ItemKind ik)
        {
            if (FreeMintScale == null) return 0;
            double v;
            return FreeMintScale.TryGetValue(ik.ToString(), out v) ? v : 0;
        }


        private static readonly NodeKindAsteroidFieldEntry s_emptyAsteroidField = new NodeKindAsteroidFieldEntry();


        /// <summary>Physical asteroid-field profile. Never null -- an absent configuration yields an
        /// all-zero entry, which plans no field.</summary>
        public NodeKindAsteroidFieldEntry GetAsteroidFieldProfile()
        {
            return AsteroidField ?? s_emptyAsteroidField;
        }

        // ---- item economy (Phase 1 fields below ÔÇö retained) ------------------------------------
        // Items are produced by owned nodes each economy quantum. Physical stations no longer
        // pay item upkeep; only credit upkeep remains, while unfounded frontier claims still produce nothing.

        public long OreYield;
        public long ProcessedGoodYield;
        public long HydrogenYield;
        public long OxygenYield;
        public long RareOreYield;
        public long IngotYield;  // always 0 in defaults; ingots come from Refinery capability recipes

        public long UpkeepOre;
        public long UpkeepProcessedGood;
        public long UpkeepHydrogen;
        public long UpkeepOxygen;
        public long UpkeepRareOre;
        public long UpkeepIngot;

        // Fallback credit values ÔÇö used only when IItemPriceCatalogue has no price for a SubTypeId
        public long FallbackOreValue;
        public long FallbackProcessedGoodValue;
        public long FallbackHydrogenValue;  // gas: SE returns 0; fallback is essential. Per KILOLITRE
        public long FallbackOxygenValue;    // gas: SE returns 0; fallback is essential. Per KILOLITRE
        public long FallbackRareOreValue;
        public long FallbackIngotValue;

        public double PriceScaleMultiplier;

        public double DemandPressureDecayRate;
        public double DemandPressureFloorOre;
        public double DemandPressureFloorProcessedGood;
        public double DemandPressureFloorHydrogen;
        public double DemandPressureFloorOxygen;
        public double DemandPressureFloorRareOre;
        public double DemandPressureFloorIngot;

        public long TradeCargoOre;
        public long TradeCargoProcessedGood;
        public long TradeCargoHydrogen;
        public long TradeCargoOxygen;
        public long TradeCargoRareOre;
        public long TradeCargoIngot;

        public static readonly ItemKind[] AllItems = { ItemKind.Ore, ItemKind.ProcessedGood, ItemKind.Hydrogen, ItemKind.Oxygen, ItemKind.RareOre, ItemKind.Ingot };

        public long ItemYieldFor(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ore:           return OreYield;
                case ItemKind.ProcessedGood: return ProcessedGoodYield;
                case ItemKind.Hydrogen:      return HydrogenYield;
                case ItemKind.Oxygen:        return OxygenYield;
                case ItemKind.RareOre:       return RareOreYield;
                case ItemKind.Ingot:         return IngotYield;
                default: return 0;
            }
        }

        public long ItemUpkeepFor(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ore:           return UpkeepOre;
                case ItemKind.ProcessedGood: return UpkeepProcessedGood;
                case ItemKind.Hydrogen:      return UpkeepHydrogen;
                case ItemKind.Oxygen:        return UpkeepOxygen;
                case ItemKind.RareOre:       return UpkeepRareOre;
                case ItemKind.Ingot:         return UpkeepIngot;
                default: return 0;
            }
        }

        /// <summary>Fallback per-ItemKind credit value. Used only when the price catalogue has
        /// no entry for a given SubTypeId (modded items, gas, or no SE session).</summary>
        public long FallbackFor(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ore:           return FallbackOreValue;
                case ItemKind.ProcessedGood: return FallbackProcessedGoodValue;
                case ItemKind.Hydrogen:      return FallbackHydrogenValue;
                case ItemKind.Oxygen:        return FallbackOxygenValue;
                case ItemKind.RareOre:       return FallbackRareOreValue;
                case ItemKind.Ingot:         return FallbackIngotValue;
                default: return 1;
            }
        }

        public double DemandPressureFloorFor(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ore:           return DemandPressureFloorOre;
                case ItemKind.ProcessedGood: return DemandPressureFloorProcessedGood;
                case ItemKind.Hydrogen:      return DemandPressureFloorHydrogen;
                case ItemKind.Oxygen:        return DemandPressureFloorOxygen;
                case ItemKind.RareOre:       return DemandPressureFloorRareOre;
                case ItemKind.Ingot:         return DemandPressureFloorIngot;
                default: return 0;
            }
        }

        public long TradeCargoFor(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ore:           return TradeCargoOre;
                case ItemKind.ProcessedGood: return TradeCargoProcessedGood;
                case ItemKind.Hydrogen:      return TradeCargoHydrogen;
                case ItemKind.Oxygen:        return TradeCargoOxygen;
                case ItemKind.RareOre:       return TradeCargoRareOre;
                case ItemKind.Ingot:         return TradeCargoIngot;
                default: return 0;
            }
        }

        public long ReferenceStockFor(ItemKind kind)
        {
            switch (kind)
            {
                case ItemKind.Ore:           return ReferenceStockOre;
                case ItemKind.ProcessedGood: return ReferenceStockProcessedGood;
                case ItemKind.Hydrogen:      return ReferenceStockHydrogen;
                case ItemKind.Oxygen:        return ReferenceStockOxygen;
                case ItemKind.RareOre:       return ReferenceStockRareOre;
                case ItemKind.Ingot:         return ReferenceStockIngot;
                default:                     return 0;
            }
        }

        public int CampaignFocusCapacity(long fleetCombatPower)
        {
            int baseline = Math.Max(1, CampaignFocusBaseCapacity);
            int maximum = Math.Max(baseline, CampaignFocusMaxConcurrent);
            if (CampaignFocusCombatPowerPerAdditional <= 0) return maximum;

            long additional = Math.Max(0, fleetCombatPower) / CampaignFocusCombatPowerPerAdditional;
            long capacity = baseline + additional;
            return capacity >= maximum ? maximum : (int)capacity;
        }

        public TimeSpan CooldownFor(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Expand:    return ExpandCooldown;
                case ActionKind.Claim:     return ClaimCooldown;
                case ActionKind.Develop:   return DevelopCooldown;
                case ActionKind.Withdraw:  return WithdrawCooldown;
                case ActionKind.Trade:
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Guard:
                case ActionKind.Patrol:
                case ActionKind.Gift:          return FleetCooldown;
                case ActionKind.FinishBuild:   return FinishBuildCooldown;
                case ActionKind.Conquer:              return ConquerCooldown;
                case ActionKind.Negotiate:
                case ActionKind.ProposePact: return NegotiateCooldown;
                case ActionKind.Found:                return FoundCooldown;
                case ActionKind.DismantleCapability: return DevelopCooldown;
                case ActionKind.Campaign:             return CampaignCooldown;
                case ActionKind.Muster:                return MusterCooldown;
                default:                             return ActionCooldown;
            }
        }

        // ---- respawn-ship placement and wayfinding ---------------------------------------------
        // All of these are purely additive vs the current TuneVersion, so no bump is needed -- a
        // tuning.json lacking the key keeps the inline default rather than zeroing it, and a zero
        // radius would collapse the spawn onto the anchor itself.

        /// <summary>Distance (metres) from the chosen station a respawn-ship spawn is placed at when
        /// no faction mate qualifies. Far enough that the station is somewhere to travel to rather
        /// than something the pod lands on.</summary>
        public double RespawnStationAnchorRadiusM = 5000.0;

        /// <summary>Distance (metres) from an online faction mate a respawn-ship spawn is placed at.
        /// Deliberately tighter than <see cref="RespawnStationAnchorRadiusM"/>: rejoining your own
        /// faction should land you within sight, not a trip away. Takes priority over the station
        /// anchor whenever a mate qualifies.</summary>
        public double RespawnMateAnchorRadiusM = 2000.0;

        /// <summary>Distance (metres) from the nearest point of a space-respawn region a spawn is
        /// placed at when no mate or station inside one qualifies
        /// (<see cref="WorldFeature.SpaceRespawn"/>). Small, because the point can sit on a ring's
        /// face and the ring is only a few km thick; the host also rejects any spot outside the
        /// region.</summary>
        public double RespawnRegionAnchorRadiusM = 500.0;

        /// <summary>A station whose owning personality has <see cref="Traits.Xenophobia"/> at or above
        /// this is never offered as a spawn anchor or a wayfinding target. Separates raider (0.90) and
        /// factorum (1.00) from mercantile (0.05), settler (0.30) and default (0.30). A war declared
        /// against the spawning player's own faction disqualifies a station independently of this --
        /// that check reads SE's own standings and so cannot live in Core.</summary>
        public double RespawnAnchorMaxXenophobia = 0.5;

        /// <summary>Whether a respawn-ship spawn is given a GPS marker and a datapad naming the
        /// chosen station. Placement is governed separately by the two radii above.</summary>
        public bool RespawnWayfindingEnabled = true;

        // ---- log filter --------------------------------------------------------------------------
        // The eight Log* keys that lived here are now ephemeral per-process toggles on
        // Factions.Core.DebugTraces, switched with `factions debug <kind>`. They were never
        // settings -- they are diagnostic channels -- and every other trace in this codebase already
        // works that way. Operator direction, 2026-09-02. Their live values are preserved as that
        // class's defaults, so nothing went dark in the move.
        //
        // DebugLogBrainDecisions stays a tuning key deliberately: it is a second, far more verbose
        // brain trace rather than one of the eight filters, and nobody asked for it to move.
        public bool DebugLogBrainDecisions;
    }

    /// <summary>Agreement between a Factions node's own position and the position of the SE-side
    /// station founded on it. <c>StationFounder.FoundStation</c> seeds a station's stored position
    /// from <see cref="SpatialNode.Position"/>, but <see cref="WorldModel.ReclassifyNodes"/> — which
    /// runs on every boot — can afterwards move the node itself (Surface nodes onto the terrain,
    /// Orbit nodes into the relative-gravity band via <c>SnapToOrbitBand</c>). Nothing propagated
    /// that move back to the station, so the two drifted apart permanently and re-widened on every
    /// restart that re-snapped the node — the player-visible symptom being a scanner GPS (which
    /// reads <see cref="SpatialNode.Position"/>) that does not match where the grid actually spawns.
    /// Lives in Core, not the host, so the threshold is single-sourced and directly testable.</summary>
    public static class StationPlacement
    {
        /// <summary>A station farther than this from its own node has drifted and should be healed
        /// back onto the node. Comfortably above the largest legitimate spawn-time adjustment (the
        /// planetary embed lift in <c>StationMaterializer.BeginSpawn</c>, bounded by a prefab's own
        /// height) and far below the km-scale minimum node separation, so it can never mistake a
        /// neighbouring node's station for this one's.</summary>
        public const double NodeDriftToleranceM = 500.0;

        /// <summary>Where <paramref name="node"/>'s station belongs, in world-space METRES: the node
        /// itself (x1000 from kilometres). The node is the whole answer — a spawn may displace the
        /// grid to clear an obstruction (<c>StationMaterializer.BeginSpawn</c>'s free-place probe),
        /// but that displacement lives only in the spawn call and is recomputed every time, so it
        /// never becomes part of where the station belongs.</summary>
        public static void StationTargetM(SpatialNode node, out double xm, out double ym, out double zm)
        {
            xm = node.Position.X * 1000.0;
            ym = node.Position.Y * 1000.0;
            zm = node.Position.Z * 1000.0;
        }

        /// <summary>Metres between where <paramref name="node"/>'s station BELONGS (see
        /// <see cref="StationTargetM"/>) and where it actually is, given in WORLD-SPACE METRES.
        /// Returns 0 for a null node (nothing to disagree with).</summary>
        public static double NodeStationDriftM(SpatialNode node, double stationXm, double stationYm, double stationZm)
        {
            if (node == null) return 0.0;
            double tx, ty, tz;
            StationTargetM(node, out tx, out ty, out tz);
            double dx = stationXm - tx;
            double dy = stationYm - ty;
            double dz = stationZm - tz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>True when the station at the given WORLD-SPACE METRE coordinates sits farther from
        /// <paramref name="node"/> than <see cref="NodeDriftToleranceM"/> AND the node is the
        /// authoritative side of the pair, so re-anchoring the station onto it is the correct repair.
        ///
        /// <para>Only engine-invented <c>N###</c> nodes qualify. For a scan-backed node
        /// (<see cref="SourceMapping.IsScanBacked"/>: <c>E###</c> economy stations, <c>G###</c> world
        /// grids) the direction of truth is REVERSED — the station is a real asset that exists in the
        /// save and the node is the copy <c>WorldScanner.Scan</c> ingested FROM it, so any divergence
        /// means the node drifted, not the station. Live-measured 2026-08-19: 42 of 520 founded nodes
        /// had diverged and most were <c>E###</c>, the worst by 128 km — re-anchoring those would have
        /// teleported vanilla's own economy stations across the map. Their node-side drift is a real
        /// but separate bug (<c>ReclassifyNodes</c> snaps every node in the graph, scan-backed
        /// included, and a later re-scan moves them back) and is deliberately NOT repaired here.</para>
        /// </summary>
        public static bool HasDriftedFromNode(SpatialNode node, double stationXm, double stationYm, double stationZm)
        {
            if (node == null || SourceMapping.IsScanBacked(node.Id)) return false;
            return NodeStationDriftM(node, stationXm, stationYm, stationZm) > NodeDriftToleranceM;
        }

        /// <summary>How closely a station grid's Up must already point away from the planet centre
        /// before it counts as gravity-aligned: cos(~11 degrees). Read by BOTH
        /// <c>StationMaterializer.GravityAlignedTransform</c> (which produces such an orientation)
        /// and its <c>NeedsGravityRealignment</c> check (which reclaims a grid that lacks one), so
        /// the producer and the test can never disagree about what "aligned" means.</summary>
        public const double GravityAlignmentDotMin = 0.98;

        /// <summary>Below this the station is effectively AT the planet centre and "away from the
        /// centre" has no direction, so there is nothing to align to.</summary>
        private const double MinRadialLengthM = 1e-6;

        /// <summary>A forward this close to parallel with the new Up carries no usable heading once
        /// projected onto the plane perpendicular to it, so a fallback axis is used instead.</summary>
        private const double MinResidualForward = 1e-3;

        /// <summary>The orientation a station grid should stand at when it sits inside a planet's
        /// natural gravity: <paramref name="up"/> pointing straight away from the planet centre, and
        /// <paramref name="forward"/> the station's existing <paramref name="currentForward"/>
        /// re-squared against that Up (Gram-Schmidt), so it keeps as much of its old heading as the
        /// new Up allows.
        ///
        /// <para>This is the rule the surface branch of <c>StationMaterializer.BeginSpawn</c> already
        /// got for free from <c>MyPlanet.GetSurfacePlacementMatrix</c>, whose Up is likewise the
        /// radial direction. It exists separately because that branch only runs within 2 km of a
        /// surface, and a station can sit deep inside a gravity well without being anywhere near one:
        /// measured 2026-08-29, the Cauldron Ring's three inner bands span 195-271.7 km of radius
        /// while the gas giant's own gravity radius is 304.9 km and its surface is at 9.5 km. Every
        /// station on those bands is in gravity and ~185-262 km from any surface, so nothing ever
        /// oriented it.</para>
        ///
        /// <para>Returns false only when <paramref name="stationM"/> sits on the planet centre, where
        /// "away from the centre" names no direction; the outputs are then left at zero and the
        /// caller should keep whatever orientation it already had.</para>
        ///
        /// <para>All inputs in world-space METRES (only their difference matters, so any consistent
        /// unit works); <paramref name="currentForward"/> need not be normalised. The returned pair
        /// is orthonormal, and <paramref name="up"/> always satisfies
        /// <see cref="GravityAlignmentDotMin"/> against the radial direction by construction.</para>
        /// </summary>
        public static bool GravityAlignedOrientation(
            Vec3 stationM, Vec3 planetCenterM, Vec3 currentForward, out Vec3 up, out Vec3 forward)
        {
            // "Away from the centre" is just "opposite the pull" expressed geometrically.
            Vec3 down = new Vec3(planetCenterM.X - stationM.X,
                                 planetCenterM.Y - stationM.Y,
                                 planetCenterM.Z - stationM.Z);
            return GravityAlignedOrientationFromPull(down, currentForward, out up, out forward);
        }

        /// <summary>
        /// The same rule stated against the GRAVITY VECTOR rather than a planet centre: Up is
        /// directly opposite <paramref name="pull"/>, and Forward is <paramref name="currentForward"/>
        /// re-squared against it.
        ///
        /// <para><b>This overload exists because the centre-based one cannot be reached where it is
        /// needed.</b> Finding the centre means finding the planet, and
        /// <c>MyGamePruningStructure.GetClosestPlanet</c> builds a DEGENERATE box at the query point
        /// and asks the voxel tree what overlaps it — so it answers only for a point inside the
        /// planet's own AABB. The gas giant's surface radius is 9.5 km, giving a voxel storage cube
        /// of ~32.8 km and a half-extent near 16 km, while its GRAVITY reaches 304.9 km and the
        /// Cauldron Ring's inner bands sit at 195–271.7 km. Every ring station is therefore in real
        /// gravity and returns <c>null</c> from that lookup. Gravity itself is a different mechanism
        /// entirely — <c>MyGravityProviderSystem</c> walks its own provider collection and asks each
        /// <c>IsPositionInRange</c> — so the pull is available exactly where the planet is not.</para>
        ///
        /// <para>Live 2026-08-29: v0.79.0 shipped the centre-based branch nested under a
        /// <c>GetClosestPlanet != null</c> gate and produced <b>zero</b> <c>gravity-aligned</c> lines,
        /// alongside zero <c>reclaiming misoriented</c> — the pre-existing check fails at the same
        /// gate. Neither was evidence about orientation; both were the gate.</para>
        /// </summary>
        public static bool GravityAlignedOrientationFromPull(
            Vec3 pull, Vec3 currentForward, out Vec3 up, out Vec3 forward)
        {
            up = new Vec3(0.0, 0.0, 0.0);
            forward = new Vec3(0.0, 0.0, 0.0);

            // Up is straight against the pull.
            double rx = -pull.X, ry = -pull.Y, rz = -pull.Z;
            double rLen = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rLen < MinRadialLengthM) return false;

            double ux = rx / rLen, uy = ry / rLen, uz = rz / rLen;

            // Gram-Schmidt: strip the Up component out of the old forward and keep the remainder.
            double dot = currentForward.X * ux + currentForward.Y * uy + currentForward.Z * uz;
            double fx = currentForward.X - dot * ux;
            double fy = currentForward.Y - dot * uy;
            double fz = currentForward.Z - dot * uz;
            double fLen = Math.Sqrt(fx * fx + fy * fy + fz * fz);

            if (fLen < MinResidualForward)
            {
                // The old forward was (near enough) the new Up, so it says nothing about heading.
                // Cross Up with whichever world axis it leans on LEAST -- that cross product is the
                // furthest from degenerate, so the result stays well-conditioned for any Up.
                double ax = Math.Abs(ux), ay = Math.Abs(uy), az = Math.Abs(uz);
                double px, py, pz;
                if (ax <= ay && ax <= az)      { px = 1.0; py = 0.0; pz = 0.0; }
                else if (ay <= az)             { px = 0.0; py = 1.0; pz = 0.0; }
                else                           { px = 0.0; py = 0.0; pz = 1.0; }

                fx = uy * pz - uz * py;
                fy = uz * px - ux * pz;
                fz = ux * py - uy * px;
                fLen = Math.Sqrt(fx * fx + fy * fy + fz * fz);
                if (fLen < MinResidualForward) return false;
            }

            up = new Vec3(ux, uy, uz);
            forward = new Vec3(fx / fLen, fy / fLen, fz / fLen);
            return true;
        }

        /// <summary>True when <paramref name="gridUp"/> has drifted further than
        /// <see cref="GravityAlignmentDotMin"/> from "away from the planet centre" -- i.e. the grid
        /// is standing wrong for the gravity it is in. Caller decides gravity is present at all;
        /// this only judges the axis. Returns false when the radial direction is undefined.</summary>
        public static bool IsGravityMisaligned(Vec3 stationM, Vec3 planetCenterM, Vec3 gridUp)
        {
            Vec3 down = new Vec3(planetCenterM.X - stationM.X,
                                 planetCenterM.Y - stationM.Y,
                                 planetCenterM.Z - stationM.Z);
            return IsGravityMisalignedForPull(down, gridUp);
        }

        /// <summary><see cref="IsGravityMisaligned"/> stated against the gravity vector — the form
        /// that is reachable for a station too far from a planet for
        /// <c>MyGamePruningStructure.GetClosestPlanet</c> to name one. See
        /// <see cref="GravityAlignedOrientationFromPull"/> for why that matters.</summary>
        public static bool IsGravityMisalignedForPull(Vec3 pull, Vec3 gridUp)
        {
            double rx = -pull.X, ry = -pull.Y, rz = -pull.Z;
            double rLen = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rLen < MinRadialLengthM) return false;

            double uLen = Math.Sqrt(gridUp.X * gridUp.X + gridUp.Y * gridUp.Y + gridUp.Z * gridUp.Z);
            if (uLen < MinRadialLengthM) return false;

            double dot = (gridUp.X * rx + gridUp.Y * ry + gridUp.Z * rz) / (rLen * uLen);
            return dot < GravityAlignmentDotMin;
        }
    }

    /// <summary>The economic rule, single-sourced so the sim and the (future) host adapter agree. Pure:
    /// no SE and no influence field ÔÇö the caller passes the already-measured Layer-2 <paramref name="control"/>
    /// (0..1 = this faction's share at the source's own position). Output is minted into the PRIVATE ledger
    /// (DESIGN decision 6), never into SE credits directly.</summary>
    public static class Economy
    {
        public static bool HasPhysicalStation(SpatialNode n)
        {
            return n != null && n.StableStationId != 0;
        }

        public static bool HasInstalledCapability(SpatialNode n)
        {
            if (n == null) return false;
            foreach (var cap in n.Capabilities)
                if (cap.Value > 0) return true;
            return false;
        }

        public static bool HasDevelopedInfrastructure(SpatialNode n)
        {
            if (!HasPhysicalStation(n)) return false;
            return !SourceMapping.IsScanBacked(n.Id) || HasInstalledCapability(n);
        }

        /// <summary>Treasury above the operational reserve that windowed spend budgets may draw on
        /// (the wealth term in <see cref="SpendBudget"/>). The reserve is
        /// <see cref="FactionTuning.ReserveTreasury"/>, raised to cover
        /// <see cref="FactionTuning.OperationalReserveRunwayTicks"/> passes of net burn while net
        /// flow is negative, so wealth-funded spending converges back to the pure income budget
        /// before it can dig into the faction's runway.</summary>
        public static long SpendableTreasurySurplus(long treasury, long netFlow, FactionTuning t)
        {
            long reserve = t.ReserveTreasury;
            if (netFlow < 0 && t.OperationalReserveRunwayTicks > 0)
            {
                double runwayReserve = -(double)netFlow * t.OperationalReserveRunwayTicks;
                if (runwayReserve > reserve)
                    reserve = runwayReserve >= long.MaxValue ? long.MaxValue : (long)runwayReserve;
            }
            long surplus = treasury - reserve;
            return surplus > 0 ? surplus : 0;
        }

        /// <summary>Can this source produce anything at all — items or scored worth — however long it
        /// is held and however uncontested it is?
        ///
        /// <para><b>Two quantities, and either one at zero silences its own channel.</b>
        /// <see cref="SourceItemYield"/> multiplies by <c>Clamp01(Richness)</c> on both its
        /// miner-model and its table branch, so a node with no deposit mints no items whatever its
        /// site is worth. <see cref="SourceYield"/> multiplies by <c>Clamp01(ResourceValue)</c>, which
        /// is zero only where <c>NodeContent.ResourceValueCap</c> is — open space carrying no ore with
        /// nothing under it to stand on. A node has to fail both to be good for nothing: ore-bearing
        /// ground in open space still mints, and a barren site on a body is still somewhere to stand.
        /// See docs/issues/FAC-219.</para>
        ///
        /// <para>Asked of the node alone, never of the contest. Hold and control are excluded on
        /// purpose — a valuable source pinned at low control by a rival is worth fighting for, and a
        /// predicate that folded control in would read a siege as a worthless node.</para></summary>
        public static bool MintsNothing(SpatialNode n)
        {
            return n != null
                && MathUtil.Clamp01(n.ResourceValue) <= 0
                && MathUtil.Clamp01(n.Richness) <= 0;
        }

        /// <summary>What one source is worth per economy quantum, before upkeep.
        ///
        /// <para><b>Not a mint, despite the name.</b> No treasury is credited from this anywhere: the
        /// only positive <c>AddTreasury</c> calls in the engine are the central bank's three
        /// operations (grant, support loan, asset purchase), and the income pass's own
        /// <c>AddTreasury</c> calls are all debits. This survives as the <i>scoring</i> term behind
        /// <c>NodeUsefulness</c>, <see cref="SourceNet"/>'s shed ranking and
        /// <c>WorldModel.ForceShedWorstDrain</c> — "what is this ground worth to hold" — which is why
        /// it keeps reading <see cref="SpatialNode.ResourceValue"/> while the physical channels moved
        /// to <see cref="SpatialNode.Richness"/>. See docs/issues/FAC-218 and FAC-219.</para></summary>
        public static long SourceYield(SpatialNode n, double control, FactionTuning t)
        {
            double y = t.BaseYield * MathUtil.Clamp01(n.ResourceValue) * MathUtil.Clamp01(n.HoldStrength) * MathUtil.Clamp01(control);
            return (long)y;
        }

        /// <summary>Net contribution of one source per economy quantum. Only physical stations
        /// pay upkeep. Legacy overload: does not account for item production value.</summary>
        public static long SourceNet(SpatialNode n, double control, FactionTuning t)
        {
            return SourceYield(n, control, t)
                 - (HasDevelopedInfrastructure(n) ? t.UpkeepPerSource : 0);
        }

        /// <summary>Credit-equivalent net income from item production at this node per economy quantum.
        /// Computes (gross item yield ÔêÆ base item upkeep ÔêÆ capability item upkeep) ├ù credit value per kind,
        /// summed across all ItemKinds. Only surplus (positive) per-kind flows count toward income.</summary>
        public static long NodeItemIncome(SpatialNode n, double control, FactionTuning t, IItemCatalogue catalogue)
        {
            long total = 0;
            for (int ki = 0; ki < FactionTuning.AllItems.Length; ki++)
            {
                ItemKind kind = FactionTuning.AllItems[ki];
                long gross = SourceItemYield(n, control, t, kind);
                if (gross <= 0) continue;
                long baseUpkeep = HasDevelopedInfrastructure(n) ? t.ItemUpkeepFor(kind) : 0;
                long capUpkeep = 0;
                if (HasPhysicalStation(n))
                    foreach (var cap in n.Capabilities)
                        if (cap.Value > 0) capUpkeep += t.CapabilityUpkeepFor(cap.Key, kind) * cap.Value;
                long net = gross - baseUpkeep - capUpkeep;
                if (net > 0) total += ItemCredits.For(kind, net, catalogue.CreditValuePer(kind));
            }
            return total;
        }

        /// <summary>Net contribution of one source per economy quantum, including item production value.
        /// Use this overload for brain scoring and insolvency guards ÔÇö it correctly marks a productive
        /// RareOreField as profitable even when the legacy credit yield (BaseYield) is zero.</summary>
        public static long SourceNet(SpatialNode n, double control, FactionTuning t, IItemCatalogue catalogue)
        {
            return SourceYield(n, control, t)
                 + NodeItemIncome(n, control, t, catalogue)
                 - (HasDevelopedInfrastructure(n) ? t.UpkeepPerSource : 0);
        }

        /// <summary>Gross item units one source mints per economy quantum for the given <paramref name="kind"/>,
        /// scaled by what its deposit holds and the global free-mint weights.
        ///
        /// <para><b>The ore channels scale by <see cref="SpatialNode.Richness"/>; everything else
        /// still scales by <see cref="SpatialNode.ResourceValue"/>.</b> How much ore comes out of the
        /// ground is a question about the ground, and worth is price-weighted — live 2026-08-31 the
        /// two were close to inversely ordered, so the thickest seams on the map were minting
        /// slowest. See docs/issues/FAC-219.</para>
        ///
        /// <para><b>The non-ore branch has no shipped channel left.</b> It survives because the split
        /// is a property of the kind rather than of the table: a manufactured channel measures the
        /// site, not the seam, so it reads ResourceValue if one is ever configured. Ingot was the last
        /// entry on it and was retired with the gravel mint (FAC-233); ProcessedGood, Hydrogen and
        /// Oxygen carry no scale either, so today every kind that mints is an ore kind.</para></summary>
        public static long SourceItemYield(SpatialNode n, double control, FactionTuning t, ItemKind kind)
        {
            double kindScale = NodeContent.FreeMintScaleFor(n, kind, t);
            if (kindScale <= 0) return 0;

            // All free ore/ice minting uses the physically-grounded miner model: one free miner
            // makes a full-cargo run every FreeMiningRunIntervalTicks. This applies to every node
            // kind ÔÇö a node yielding only trace ore is still modelled as a miner making runs there,
            // at a rate its own measured Richness sets.
            // On the live host FreeMinerCargoVolumeLiters is stamped from the real SE block
            // definition at session load; the tuning value is a sim-only fallback.
            bool isOreKind = kind == ItemKind.Ore || kind == ItemKind.RareOre;
            if (isOreKind && t.FreeMiningRunIntervalTicks > 0 && t.FreeMinerCargoVolumeLiters > 0)
            {
                double scale = kindScale
                             * MathUtil.Clamp01(n.Richness)
                             * MathUtil.Clamp01(n.HoldStrength)
                             * MathUtil.Clamp01(control);
                return (long)((double)t.FreeMinerCargoVolumeLiters / t.FreeMiningRunIntervalTicks * scale);
            }

            // Not an ore channel: the site trickle, scaled by what the site is worth. See the split
            // argument in the summary.
            double baseScale = kindScale
                             * MathUtil.Clamp01(n.ResourceValue)
                             * MathUtil.Clamp01(n.HoldStrength)
                             * MathUtil.Clamp01(control);
            return (long)(t.ItemYieldFor(kind) * baseScale);
        }

        /// <summary>Net item contribution per economy quantum. Only physical stations pay upkeep.</summary>
        public static long SourceItemNet(SpatialNode n, double control, FactionTuning t, ItemKind kind)
        {
            return SourceItemYield(n, control, t, kind)
                 - (HasDevelopedInfrastructure(n) ? t.ItemUpkeepFor(kind) : 0);
        }

    }

    public struct FactionIntent
    {
        public ActionKind Kind;
        public string TargetNodeId;
        public string TargetFactionTag;        // for faction-targeted acts (Negotiate); null otherwise
        public NodeCapabilityKind? TargetCapability; // Develop only: which capability to install
        public long Cost;
        public double TradeMagnitude;          // raw lane weight computed by brain; carried to PendingFleetAction
        /// <summary>Campaign only: a <see cref="NodeRegion"/> key (<see cref="NodeRegion.ToKey"/>)
        /// naming the region to terrorise. Null for an ordinary defensive Campaign, whose
        /// <see cref="TargetNodeId"/> is the centre; for a terror campaign the node is the
        /// staging base and this is the target.</summary>
        public string TargetRegion;
        /// <summary>Campaign only: which objective the focus should carry. Null for the plain
        /// defensive campaign, whose focus carries <see cref="CampaignObjectiveKind.DefendRegion"/>
        /// by construction.</summary>
        public CampaignObjectiveKind? ObjectiveKind;
        /// <summary>Campaign only: why (see <see cref="CampaignCause"/>). Null alongside a null
        /// <see cref="ObjectiveKind"/>.</summary>
        public CampaignCause? Cause;
        /// <summary>Campaign only: the single node a node-scoped conquest objective names.
        /// <see cref="TargetNodeId"/> is the staging base for a campaign intent, so the objective's
        /// own node needs a field of its own. Null for a region-scoped objective.</summary>
        public string ObjectiveNodeId;
        /// <summary>Conquest campaign only: quality the nomination gained for the lanes the target's
        /// owner denies this faction (<c>FactionBrain.LaneConquestBoost</c>), and the lane weight in
        /// credits it was read from. 0 otherwise. Carried to the start line; not persisted.</summary>
        public double LaneBoost;
        public double LaneCredits;
        public override string ToString()
        {
            if (TargetFactionTag != null) return Kind + "(" + TargetFactionTag + ")";
            string cap = TargetCapability.HasValue ? "+" + TargetCapability.Value : "";
            return TargetNodeId == null ? Kind.ToString() : Kind + "(" + TargetNodeId + cap + ")";
        }
    }

    /// <summary>An act of aggression recorded during a tick (a raid). Fed to <c>Diplomacy</c> after the
    /// action pass so the victim's standing toward the attacker sours (war spiral).</summary>
    public struct RaidIncident
    {
        public string Attacker;
        public string Victim;
        public string NodeId;
        public ActionKind Kind;   // Raid or Conquer — set explicitly at every incident-recording site,
                                   // do not rely on the struct default (Idle/0)
        /// <summary>The attack was destroyed before it landed (an intercept terminal) rather than
        /// reaching its target. It carries the same relation hit and fatigue as a landed one — an
        /// attempt is an act of war — and only the line the drain logs differs.</summary>
        public bool Repelled;
    }

    /// <summary>A node handed to <see cref="NeutralFaction"/> by <c>WorldModel.DecayInfluence</c>
    /// (hold decayed to 0 with no explicit Withdraw). Core records these so the host can log the
    /// territory loss and rename the derelict station — the loss itself is otherwise silent.</summary>
    public struct AbandonedNode
    {
        public string NodeId;
        public string PriorOwnerTag;
    }

    /// <summary>One fleet of hulls flown off a node its faction just abandoned — or one group left
    /// behind because nothing the faction still owns was reachable (<see cref="ToNodeId"/> null, and
    /// the hulls stay on the derelict). Written by <see cref="HullRelocation.Evacuate"/>; Core records
    /// these so the host can log them, the engine having no logging sink of its own, the same reason
    /// <see cref="AbandonedNode"/> exists.
    /// <para>Provenance: FAC-278, FAC-432 fix 5.</para></summary>
    public struct ShipEvacuation
    {
        public string FactionTag;
        public string PrefabId;
        public int Count;
        public string FromNodeId;
        /// <summary>Where the hulls are flying; null when they could not be moved at all.</summary>
        public string ToNodeId;
        /// <summary>Whole hulls: <c>nearest_owned</c>. Damaged hulls: <c>yard</c> or
        /// <c>no_reachable_yard</c>. Left behind: <c>no_owned_node</c> or <c>no_faction_state</c>.</summary>
        public string Reason;
        /// <summary>True for hulls taken from <see cref="SpatialNode.DamagedHulls"/>.</summary>
        public bool Damaged;
        /// <summary>The integrity the hulls fly at: 1 for whole ones.</summary>
        public double Integrity;
        /// <summary>The evacuation fleet's <see cref="PendingFleetAction.LogId"/>; null when none flew.</summary>
        public string FleetLogId;
        /// <summary>The evacuation fleet's arrival; default when none flew.</summary>
        public DateTime EtaUtc;
        /// <summary>Repair parts the damaged hulls had absorbed, left in the node's inventory.</summary>
        public long PartsLeft;

        /// <summary>The line the host writes for this record: <c>SHIP_EVACUATED</c> for a fleet that
        /// flew, <c>SHIP_STRANDED_ON_ABANDON</c> for hulls left behind. Damaged hulls add
        /// <c>integrity=</c>.</summary>
        public string LogLine()
        {
            string integrity = Damaged
                ? " integrity=" + Integrity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            if (ToNodeId == null)
                return "SHIP_STRANDED_ON_ABANDON " + FactionTag + " prefab=" + PrefabId + " count=" + Count
                       + " node=" + FromNodeId + " why=" + Reason + integrity;
            return "SHIP_EVACUATED " + FactionTag + " prefab=" + PrefabId + " count=" + Count
                   + " from=" + FromNodeId + " to=" + ToNodeId + " why=" + Reason + integrity
                   + " fleet=" + FleetLogId
                   + " eta=" + EtaUtc.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                   + (PartsLeft > 0 ? " parts_left=" + PartsLeft : "");
        }
    }

    /// <summary>What stands on a node and passes to whoever takes it: whole hulls, damaged hulls,
    /// build-queue entries and inventory units. A claim of a derelict and a conquest both hand all of
    /// it to the new owner; the lines that report the transfer (<c>NODE_CLAIMED</c>,
    /// <c>CONQUER_RESOLVED ... [captured]</c>, <c>CONQUER_ACCEPTED</c>) carry <see cref="Suffix"/>.
    /// <para>Provenance: FAC-432 fix 5.</para></summary>
    public struct NodeHoldings
    {
        public int Hulls;
        public int DamagedHulls;
        public int Builds;
        public long ItemUnits;

        public static NodeHoldings Of(SpatialNode n)
        {
            var h = new NodeHoldings();
            if (n == null) return h;
            foreach (KeyValuePair<string, int> ship in n.ShipStock)
                if (ship.Value > 0) h.Hulls += ship.Value;
            h.DamagedHulls = n.DamagedHulls.Count;
            h.Builds = n.BuildQueue.Count;
            foreach (KeyValuePair<string, long> item in n.Inventory)
                if (item.Value > 0) h.ItemUnits += item.Value;
            return h;
        }

        /// <summary><c> stock=N damaged=N builds=N items=N</c>, with the leading space.</summary>
        public string Suffix()
        {
            return " stock=" + Hulls + " damaged=" + DamagedHulls + " builds=" + Builds + " items=" + ItemUnits;
        }
    }

    /// <summary>What a node removal destroyed — see <c>WorldModel.RemoveNode</c>. The grace sweep
    /// deletes whatever is standing on an expired UNKN node, and until this existed the line said
    /// nothing about how much that was.
    /// <para>Provenance: FAC-278.</para></summary>
    public struct RemovedNodeContents
    {
        /// <summary>Whole hulls: units of <see cref="SpatialNode.ShipStock"/>.</summary>
        public int Hulls;
        /// <summary>Entries of <see cref="SpatialNode.DamagedHulls"/>.</summary>
        public int DamagedHulls;
        public long ItemUnits;
    }

    /// <summary>A node placement or audit-time spacing violation. Core records these so the host can
    /// surface them via operator logs without introducing a logging sink into the engine.</summary>
    public struct NodeSeparationViolation
    {
        public string NewNodeId;
        public NodeZone Zone;
        public string OffendingNeighborId;
        public double ActualDistanceKm;
        public double RequiredMinSepKm;
    }

    /// <summary>Summary returned by <see cref="WorldModel.ReclassifyNodes"/>.</summary>
    public struct NodeReclassifyResult
    {
        public int Total;
        public int Surface;
        public int Orbit;
        public int DeepSpace;
        public int ZoneChanged;    // nodes whose Zone field was (or would be) updated
        public int Repositioned;   // Surface nodes snapped to the planet surface sphere
        public int OrbitRepositioned; // Orbit nodes snapped into the tightened relative gravity band
        public int FeaturePlaced;  // nodes an authored world feature placed, overriding the planet rules
        public int PlanetIdChanged; // nodes whose PlanetId field was (or would be) updated
        public int SiteChanged;    // nodes whose Site field was (or would be) updated
        public int FeatureKeyChanged; // nodes whose FeatureKey field was (or would be) updated
        /// <summary>Nodes whose <see cref="SpatialNode.ResourceValue"/> was (or would be) re-derived
        /// to a different number. Worth is a reading of the evidence rather than a stored judgement,
        /// but it IS persisted, so a rule change reaches an existing estate only through this pass:
        /// the four ordinary refresh sites are commit, deposit resolution, a survey step and
        /// abandonment, and a node the ore seam declined for hits none of them ever again.</summary>
        public int WorthChanged;
        /// <summary>Nodes whose <see cref="SpatialNode.Richness"/> was (or would be) re-derived to a
        /// different number, on the same terms as <see cref="WorthChanged"/>. Expect this to report
        /// the whole estate on the first boot that carries the field, since nothing on disk has one
        /// yet — that is the pass doing its job, not a defect.</summary>
        public int RichnessChanged;
        /// <summary>Repositions declined because the destination was inside the separation in force
        /// there. The node keeps the position it already had; see
        /// <see cref="WorldModel.ReclassifyNodes"/>.</summary>
        public int SeparationBlocked;
        /// <summary>Nodes standing closer to a neighbour than the separation in force where they sit,
        /// counted over the whole graph after the pass. Not caused by the pass -- it is the world's
        /// running total, which nothing else reports.</summary>
        public int StandingViolations;
    }

    /// <summary>Summary returned by <see cref="WorldModel.DensifyTopology"/> -- the one-time retrofit
    /// that links existing nodes within the same reach-cap radius <see cref="WorldModel.ExpandFrom"/>
    /// now uses going forward, closing the gap for nodes created before that fix shipped.</summary>
    public struct TopologyDensifyResult
    {
        public int NodesScanned;
        public int PairsWithinReach;   // within-reach pairs found, including already-linked ones
        public int LinksAdded;         // new undirected edges (actual when applyFix; would-add count on a dry run)
        public double ElapsedMs;       // wall-clock cost of the scan -- log/inspect before ever running `apply` live
    }

    /// <summary>Summary returned by <see cref="WorldModel.ClearOrbitStranding"/>.</summary>
    public struct OrbitStrandingClearResult
    {
        public int NodesRepositioned;   // NodeReclassifyResult.Repositioned + OrbitRepositioned
        public int BuildsCanceled;
        public int BuildItemsRefunded;
        public int ShipsDismantled;
        public int BomUnitsCredited;
    }

    /// <summary>Diagnostic result of <see cref="WorldModel.AuditStrandedShipStock"/> — how much
    /// dispatch-eligible ship stock can never pass <c>ShipPool.IsRouteFeasible</c> for ANY
    /// destination from its current node (its own origin-side dry TWR already falls below
    /// <c>FactionTuning.MinDryTwr</c>, and that check does not depend on the destination).</summary>
    public struct StrandedShipStockResult
    {
        public int TotalAvailableUnits;      // dispatch-eligible units inspected (ListedForSale excluded — still player-purchasable)
        public int StrandedUnits;            // subset of the above that can never clear the route gate anywhere
        public int NodesWithStrandedStock;
        public int DistinctStrandedPrefabs;
        public System.Collections.Generic.List<string> Details; // capped sample of "node=.. prefab=.. count=.. originTwr=.. g=.."
    }

    public sealed class Candidate
    {
        public FactionIntent Intent;
        public Drive SourceDrive;
        public double Support;
        public double SourceStrength;
        public double Need, Afford, Bias, Quality, Cooldown, Repetition, Utility;
    }

    public sealed class Decision
    {
        public FactionIntent Intent;
        public Drive Goal;
        public string Explanation;
        public List<Candidate> Scored;
    }

    // ---- the seam (domain terms; prod = adapters over SE/Crunch/MES, sim = in-memory) ----------

    /// <summary>Layer 2 ÔÇö the influence heatmap, DERIVED from Layer-1 sources. Computed on demand,
    /// never materialized/persisted; the engine senses border friction through it.</summary>
    public interface IInfluenceField
    {
        double Sample(Vec3 pos, string factionTag);
        double Control(Vec3 pos, string factionTag); // 0..1 share at pos: 1 = uncontested, ->0 = besieged
        /// <summary>0..1 how hard byFaction presses on ofFaction's territory: the rival's share of the
        /// influence mass standing over ofFaction's whole estate, normalised by
        /// <see cref="FactionTuning.BorderPressureFullContestShare"/>. Directional — it rises as the
        /// owner loses ground and falls as it wins, and a big estate is not set alight by one
        /// contested node on its rim.</summary>
        double Pressure(string ofFaction, string byFaction);
        /// <summary>Every faction tag that owns at least one node other than <paramref name="factionTag"/>.
        /// The rival set <c>FactionBrain.DampedRivalPressure</c> loops over: it cannot use a
        /// pre-aggregated worst-rival figure, because each rival's <see cref="Pressure"/> has to be
        /// weighed by that rival's fleet power before the maximum is taken.</summary>
        System.Collections.Generic.IEnumerable<string> RivalTags(string factionTag);
        // Snapshot-accelerated: O(1) after BuildSnapshot(), O(nodes) fallback otherwise.
        // Use when the query point is a known graph node (e.g. scoring loops in FactionBrain).
        double SampleAt(string nodeId, string factionTag);
        double ControlAt(string nodeId, string factionTag);
    }

    public interface IWorldView
    {
        DateTime UtcNow { get; }
        SpatialGraph Graph { get; }
        long GetTreasury(string factionTag);
        long GetNetFlow(string factionTag);   // last pass's real income minus upkeep/interest/action costs
        /// <summary>Ledger value: treasury + node inventory at market price. Cached by AccrueIncome; falls back to treasury before first tick.</summary>
        long GetLedger(string factionTag);
        /// <summary>Outstanding borrowed credits for this faction (zero until banking is active).</summary>
        long GetDebt(string factionTag);
        /// <summary>Total combat capacity (summed WeaponBands Dps across all owned nodes' ShipStock)
        /// for this faction. Used to dampen border-tension drives by relative fleet strength — see
        /// FactionBrain.Sense's PowerDampening. Returns 0 for an unknown faction or one with no
        /// combat-capable ShipStock/PrefabCatalogue data.</summary>
        long GetFleetCombatPower(string factionTag);
        /// <summary>True when any node <paramref name="factionTag"/> owns has a ship in
        /// <see cref="SpatialNode.ShipStock"/> (excluding store-reserved stock — see
        /// <see cref="ShipPool.Available"/>) suitable for <paramref name="kind"/>'s purpose. Gates
        /// ship-dispatching brain candidates so the brain never wins the softmax draw on an action
        /// it can't actually enact. Returns false when no prefab catalogue is wired.
        /// <paramref name="targetNodeId"/> null (the default) checks role-suitability only. When
        /// supplied, also requires that suitable stock exist at a node whose route to the target is
        /// dry-TWR feasible (see <see cref="ShipPool.RouteEnvironment"/>) — so a network whose only
        /// stock can't survive the trip (e.g. Ion-only hulls dispatched toward a planet surface)
        /// correctly reads as "no stock" instead of silently spin-failing at Enact every tick.</summary>
        bool HasShipStockFor(string factionTag, ActionKind kind, string targetNodeId = null);
        /// <summary>Owned nodes with dispatchable stock suitable for the action. The returned set is a per-think snapshot used for origin-aware target scoring.</summary>
        System.Collections.Generic.IEnumerable<SpatialNode> SuitableShipOrigins(string factionTag, ActionKind kind);
        IItemCatalogue Catalogue { get; }
        IInfluenceField Influence { get; }
        /// <summary>The world's bodies — what a node's body decides, e.g. which crops a Farm there
        /// can grow (<see cref="FarmCrops"/>).</summary>
        IPlanetCatalogue Planets { get; }
        /// <summary>Item registry: SubTypeId ÔåÆ ItemKind mapping. Used by cargo/loot logic that
        /// needs to aggregate by kind without SE coupling. Returns <see cref="NullItemRegistry"/>
        /// when no SE-backed registry is wired.</summary>
        IItemRegistry ItemRegistry { get; }
        /// <summary>Recipe registry: production conversion recipes indexed by output item.
        /// Returns <see cref="NullRecipeRegistry"/> when no SE-backed registry is wired.</summary>
        IRecipeRegistry RecipeRegistry { get; }
        /// <summary>One NPC faction's aggregate opinion of a player faction, derived from the SE
        /// reputations it holds toward that faction's individual members. Returns
        /// <see cref="NullMemberStandingSource"/> — which answers false for every pair — when no
        /// SE-backed source is wired, so the Sim and the tests see NPC-vs-NPC drift unchanged.</summary>
        IMemberStandingSource MemberStanding { get; }
        /// <summary>Faction-level item inventory (sum over all owned nodes for a given kind).</summary>
        long GetInventory(string factionTag, ItemKind kind);
        /// <summary>Faction-level reserved install stock (sum over all owned nodes for a given kind).</summary>
        long GetReservedInventory(string factionTag, ItemKind kind);
        /// <summary>Faction-level normal inventory for one specific invKey (e.g. "Comp:SteelPlate"),
        /// summed over all owned nodes. Used for BOM-based (per-component) afford checks where a
        /// flat ItemKind aggregate isn't specific enough — see FactionTuning.InstallBoms.</summary>
        long GetFactionInventoryByKey(string factionTag, string invKey);
        /// <summary>Reserved-install counterpart to <see cref="GetFactionInventoryByKey"/>.</summary>
        long GetFactionReservedInventoryByKey(string factionTag, string invKey);
        /// <summary>Per-node item inventory aggregated by kind. Returns 0 if unknown.</summary>
        long GetNodeInventory(string nodeId, ItemKind kind);
        /// <summary>All SubTypeId entries in a node's inventory (for cargo packing / SubTypeId-level access).
        /// Returns an empty enumerable when the node doesn't exist or has no stock.</summary>
        System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, long>> GetNodeInventoryAll(string nodeId);
        /// <summary>Add <paramref name="delta"/> of a specific SubTypeId to a node's inventory (floors at 0).</summary>
        void AddNodeInventory(string nodeId, string subTypeId, long delta);
        /// <summary>Global demand register for inter-manager signalling. Managers post unfulfilled demands;
        /// other managers read and consume them. Not used by the brain.</summary>
        DemandRegister DemandRegister { get; }
        /// <summary>Strategic investment request board. Managers post capability gaps here when blocked
        /// by missing production capacity; the brain reads it during Think to generate high-priority
        /// Develop candidates.</summary>
        InvestmentRegister InvestmentRegister { get; }
    }

    /// <summary>Pure fleet-dispatch rules that depend only on Core types.</summary>
    public static class FleetRules
    {
        /// <summary>
        /// True when both nodes are standing on the terrain of the same body. Patrol and Guard skip
        /// the ship-stock gate when this holds -- ground forces self-deploy across a planet, so the
        /// dispatch consumes no ship and needs none to exist.
        ///
        /// <para><b>Asks the ground, not the label.</b> This is the one gate that removes a physical
        /// requirement rather than expressing a preference, so it cannot afford
        /// <see cref="SpatialNode.Zone"/>: that is relative gravity, and on a body whose atmosphere
        /// reaches far past its terrain it reads "surface" for hundreds of km of open air. Two such
        /// nodes on opposite sides of a gas giant would both be Surface on the same planet, and
        /// troops would "walk" between them across 400 km of sky with no ship at all.
        /// <see cref="StationSiting.Classify"/> asks whether the position is actually within
        /// <see cref="StationSiting.GroundToleranceKm"/> of terrain, which is the question this gate
        /// means. It also refuses a body the world says cannot be landed on, and it reads the live
        /// position rather than a label derived at boot. See docs/issues/FAC-096 stage 2.</para>
        /// </summary>
        public static bool IsSamePlanetSurface(SpatialNode a, SpatialNode b, IPlanetCatalogue planets)
        {
            if (a == null || b == null) return false;
            if (StationSiting.Classify(planets, a.Position) != StationSite.Surface) return false;
            if (StationSiting.Classify(planets, b.Position) != StationSite.Surface) return false;

            PlanetRecord pa = PlanetGeometry.NearestByCentre(planets, a.Position);
            PlanetRecord pb = PlanetGeometry.NearestByCentre(planets, b.Position);
            if (pa == null || pb == null) return false;
            return pa.Center.DistanceTo(pb.Center) < 1.0;
        }
    }

    public interface IFactionActions
    {
        /// <summary>Creates and claims a node on demand. Returns <c>null</c>, having created nothing,
        /// when no position near the origin clears the separation in force there.</summary>
        SpatialNode ExpandFrom(string factionTag, string fromNodeId);
        void Claim(string factionTag, string nodeId);
        void Reinforce(string factionTag, string nodeId);
        void Develop(string factionTag, string nodeId, NodeCapabilityKind capability); // install a capability on a held node
        void Raid(string factionTag, string nodeId);       // attack a RIVAL's source: hold down, threat up
        void Guard(string factionTag, string nodeId);
        void Patrol(string factionTag, string nodeId);
        void Withdraw(string factionTag, string nodeId);
        void DismantleCapability(string factionTag, string nodeId, NodeCapabilityKind capability);
    }
}
