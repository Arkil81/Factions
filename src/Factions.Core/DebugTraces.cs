using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The manager log-filter switches, as ephemeral per-process toggles rather than persisted
    /// tuning keys.
    ///
    /// <para><b>Why they moved.</b> They were eight <c>Log*</c> fields on <see cref="FactionTuning"/>,
    /// which made them settings: written into every instance's <c>tuning.json</c>, surviving
    /// restarts, and carrying a migration burden whenever one was added. They are not settings —
    /// they are diagnostic channels, switched on to answer a question and off again, and every other
    /// trace in this codebase already works that way (<c>factions debug uplink|flight|store|…</c>).
    /// Operator direction, 2026-09-02.</para>
    ///
    /// <para><b>Defaults are the live values they replace</b>, not all-false: `ItemDemand`,
    /// `ShipEvents`, `Investment` and `BrainDecisions` were `true` in the deployed
    /// <c>tuning_defaults.json</c> and in the running <c>tuning.json</c>, so defaulting them off
    /// would have silently darkened four channels an operator currently has. The failure that
    /// produces is silence, which is indistinguishable from the thing you were watching having
    /// stopped.</para>
    ///
    /// <para>Per-process and deliberately not persisted: a plugin reload clears them, and
    /// <c>FactionsPlugin.LogDebugToggleState</c> reports that it did — for exactly the reason above.
    /// A toggle that survived a restart would be a tuning key with a migration, which is what this
    /// replaced.</para>
    /// </summary>
    public static class DebugTraces
    {
        public static bool Produced;
        public static bool ItemXfer;
        public static bool UpkeepIncome;
        public static bool ItemDemand = true;
        public static bool ShipEvents = true;
        public static bool Investment = true;
        public static bool CentralBankDenials;
        public static bool BrainDecisions = true;

        /// <summary>Per-bank attribution for idle machines — which gate stopped each recipe getting a
        /// slot. See <c>ProductionManager.TickCapabilityBank</c>'s SLOT_GATE line.</summary>
        public static bool SlotGate;

        /// <summary>Per-entry build inflow: units collected, serviced seconds, and the projected
        /// time to finish at that rate. Off by default — it is one line per combat build per sweep,
        /// and it exists to size a horizon that does not exist yet (FAC-351).</summary>
        public static bool BuildRate;

        /// <summary>The SE-relation projection — one line per pair whose projected band moved. See
        /// <c>FactionRuntime.ProjectRelationsToSe</c>'s DIPLOMACY_SE_RELATION line. Off by default:
        /// the per-pass DIPLOMACY_SE_SYNC summary is the unconditional record, and this is the
        /// per-pair detail behind it. Also the vanilla Factions screen's buttons (FAC-394 slice 5):
        /// DIPLOMACY_VANILLA_REQUEST / _REFUSED (<c>FactionStateChangeCheckPatch</c>),
        /// DIPLOMACY_VANILLA_BROADCAST (<c>VanillaDiplomacyBroadcast</c>) and
        /// DIPLOMACY_VANILLA_APPLY_SKIPPED (<c>FactionDiplomacyPatch</c>).</summary>
        public static bool Diplomacy;
        /// <summary>Fuel accounting, three lines. FUEL_OBSERVED (<c>FleetFlightController</c>) sets
        /// the modelled burn against what a real hull's tanks, batteries and reactors lost over one
        /// materialised manoeuvre, and FSD_OBSERVED (<c>FactionsPlugin</c>) is the same comparison
        /// for the drive on a player grid in supercruise. FUEL_DRAWN (<c>FuelMeter</c>) is one line
        /// per leg the abstract fleet flies — <b>very</b> high volume at live dispatch rates, which
        /// is why it is gated and why the aggregate an operator usually wants is
        /// <c>factions fuel</c>.</summary>
        public static bool Fuel;

        /// <summary>Grid marking — one line per grid adopted under a tracked hull: a split piece
        /// renamed to debris, a mechanically attached part found after the spawn, ordnance a hull
        /// released. Off by default: the spawn line's <c>grids=</c> token is the unconditional
        /// record of what a spawn marked.</summary>
        public static bool GridMarks;

        /// <summary>KNOWLEDGE_SWEEP — one line per completed sweep pass: what the pass discovered or
        /// renewed, and how many sightlines a body blocked while it ran. Off by default; the pass is
        /// otherwise silent, so this is the only reader of either count.</summary>
        public static bool Sensing;

        /// <summary>The contract board's generation path — one line per attempt that found no
        /// eligible candidate, one per attempt refused for want of budget, and one per contract
        /// posted. Off by default: the board is regenerated for every station on every economy
        /// tick, so this scales with the station count rather than with events. Measured on
        /// <c>Torch-2026-09-21.log</c> at 5,431 of 20,239 lines. The lines that record a state
        /// change rather than an attempt — <c>Contract completed</c> (credits inventory, spends,
        /// reinforces) and <c>Contract invalidated</c> — stay unconditional.</summary>
        public static bool Contracts;

        /// <summary>EconomyTickProfiler's summary — one line per vanilla economy tick giving where
        /// <c>MySessionComponentEconomy.UpdateStations</c> spent its time, its GC counts and its
        /// heap delta. Off by default: it is measurement instrumentation for a specific stall
        /// investigation, and the Torch log is where the engine's own stall diagnostics land. The
        /// timing is collected either way; this only decides whether it is written.</summary>
        public static bool EconomyProfiler;
        /// <summary>PHYSICS_CLUSTER_REORDER (a cluster reorder over a large box, and what moved) and
        /// PHYSICS_STALL (a physics step over 2 s: every cluster and the fastest active bodies).</summary>
        public static bool Physics;

        /// <summary>GOVERNOR — one line per load-governor step change and the numbers behind it; with
        /// <c>GovernorFactionsActuate</c> on, also FLEET_HELD_OFFSCREEN / FLEET_WAITING /
        /// FLEET_WAIT_EXPIRED as Factions fleets are held (a held traffic fleet is logged once per
        /// telemetry window). Off by default; the <c>governor</c> series in InfluxDB and the
        /// <c>factions governor</c> verb carry every window regardless.</summary>
        public static bool Governor;

        /// <summary>Fleet ammunition — MUNITIONS_DRAWN per armed departure, MUNITIONS_SPENT per duel,
        /// MUNITIONS_RETURNED per landing, MUNITIONS_OBSERVED per materialised read, and a
        /// non-combat fleet's MUNITIONS_SHORT. Off by default: every armed dispatch writes one. A
        /// combat fleet flying short writes MUNITIONS_SHORT whatever this says.</summary>
        public static bool Munitions;

        /// <summary>Trade consent (<c>factions debug consent</c>; "trade" is the fleet trace) —
        /// TRADE_CONSENT_REFUSED when no node of a seller will part with an item a partner's row asks
        /// for, with the seller's stocked nodes counted by the gate that refused each. Logged on change
        /// per (seller, buyer, item). Also TRADE_PARTNER_CLOSED when a whitelisted partner is skipped
        /// because the pair is barred (either side hostile or embargoing) or a player partner's switch
        /// for that direction is off; logged on change per (actor, partner, direction). Off by default.</summary>
        public static bool Trade;

        /// <summary>STORE_REFUSED — one line per transaction refused at a registered faction's store
        /// block or Services Terminal because the customer is barred from trading with the owner
        /// (<c>factions debug storebar</c>). Off by default: a player clicking Buy repeatedly writes
        /// one per click. The chat message the player sees is rate-limited separately and is not
        /// gated by this.</summary>
        public static bool StoreBar;

        /// <summary>EXTRACTION_PLAN — one line per standing extraction order the NPC planner
        /// authors, with the flow terms behind it (<c>ExtractionPlanner.AppendTrace</c>). Off by
        /// default: it is one line per (hub, ore) per faction per plan.</summary>
        public static bool Extraction;

        /// <summary>FLIGHT_TACTIC — in the Torch log, one line when a fighting lead's tactical read
        /// changes its target, arm or bracket (<c>FleetFlightController</c>). TACTIC_CHOSEN — in the
        /// decision log, each side's learned pick for a duel with its context, the prior and the
        /// winning draw. TACTIC_REVIEW — decision log, what the duel taught each side:
        /// predicted and actual damage share, reward, weight and the cell's posterior after. The
        /// decision-log lines are written only while tactic learning is switched on. Off by default.</summary>
        public static bool Tactics;

        /// <summary>RESTORE_PROGRESS — one line per interval of a running restoration at a Services
        /// Terminal: blocks placed and welded so far, credits paid, what is left. Off by default:
        /// RESTORE_STARTED, RESTORE_PAUSED and RESTORE_DONE are the unconditional record.</summary>
        public static bool Restoration;

        /// <summary>TRAFFIC_ADMISSION — one line per spawn start: the fleet's predicted cost, the
        /// traffic budget's headroom, and whether it would have been admitted
        /// (docs/traffic-control.md); and TRAFFIC_REFUSED, once per civilian fleet the presence rule
        /// keeps abstract. Off by default; the <c>traffic_control</c> series is the aggregate.</summary>
        public static bool Admission;

        /// <summary>SPAWN_COST — each Factions spawn landing: the steps of our landing callback and the
        /// whole frame it landed in (which also holds SE adding the grids to the world), and the gap the
        /// spawn pacer then imposes (Torch log). Off by default.</summary>
        public static bool SpawnCost;

        /// <summary>ALLOC_SPLIT — managed allocation every 30 s: process total, game thread, worker threads, and the top
        /// session components, plugins, entity types and Factions sections by bytes allocated (Torch log). Off by
        /// default; reads a per-thread counter beside the timings the cost hooks already take.</summary>
        public static bool Alloc;

        /// <summary>LANDING_PROBE — each landing-gear lock to and release from a voxel, with the lock
        /// pivot's height over the terrain by Havok ray and by the planet's analytic surface, and a
        /// checksum of the voxel content under it; then the grid's position at 0.25 s and 1 s after
        /// the last gear releases (<c>Factions.Shared/LandingProbe</c>). The client writes the same
        /// line under its "Log diagnostics" setting, so the two pair by grid and gear id. Off by
        /// default.</summary>
        public static bool Landing;

        /// <summary>TRESPASS_CROSSING — one line each time a seated pilot's space changes, or whether
        /// its owner sees them does (<see cref="TrespassCrossingBook"/>), in the decision log. Off by
        /// default: it is checked every 5 s per seated pilot, and a ship idling on a border writes a
        /// line per flicker. TRESPASS_HAIL stays the unconditional record of the crossings a faction
        /// answered.</summary>
        public static bool Trespass;

        /// <summary>ASSEMBLER_INPUT — one line each time an assembler's input-tray plan changes mode or
        /// item (<see cref="AssemblerInputPlanner"/>), with what it pushed out and pulled in. Off by
        /// default: it can fire for every assembler on the server.</summary>
        public static bool AssemblerInput;

        /// <summary>What a viewing player is told about a faction's standing. RELATION_RESOLVE: each
        /// (viewer, owner) answer when it changes, with the step that decided it and the SE table
        /// entry behind it (<c>ViewerRelations</c>). SCANNER_RELATION: each scanner send to a linked
        /// client that changed a contact's relation or reset the client, with the reset's reason and
        /// the shifts by owner (<c>PlayerScannerManager</c>). RELATION_ROWS: every 60 s, the SE
        /// relation-table rows between each player faction and every other faction, on change
        /// (<c>RelationTrace</c>). Off by default.</summary>
        public static bool Relation;

        /// <summary>Toggle name → (read, write). One table so the command, the startup state line and
        /// the tests cannot drift from each other.</summary>
        private static readonly Dictionary<string, string> Descriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "produced",       "PRODUCED — every production event (highest-volume trace there is)" },
                { "itemxfer",       "ITEM_XFER — inventory movement between nodes" },
                { "upkeep",         "UPKEEP / INCOME — the per-quantum economy lines" },
                { "itemdemand",     "PROD_INPUT_DEMAND / ITEM_DEMAND — what a bank or a build asked for" },
                { "shipevents",     "SHIP_DEMAND / BUILD_QUEUED / BUILD_SLOTS / SHIP_BUILT — the shipyard pipeline" },
                { "investment",     "INVEST_REQUEST / REFINE_INVEST / THROUGHPUT_INVEST — capability investment" },
                { "bankdenials",    "central-bank grant denials, with the reason" },
                { "braindecisions", "one line per non-Idle brain decision, with its scored terms" },
                { "slots",          "SLOT_GATE — why a capability bank left machines idle, per gate" },
                { "buildrate",      "BUILD_RATE — a build's inflow, and how long it needs at that rate" },
                { "diplomacy",      "DIPLOMACY_SE_RELATION — each pair whose SE reputation band the brain moved" },
                { "grids",          "SimGridMarks — each grid adopted under a tracked hull (split, attached part, released ordnance)" },
                { "sensing",        "KNOWLEDGE_SWEEP — per sweep pass: nodes touched, and sightlines a planet blocked" },
                { "contracts",      "Contract generation / skipped / generated -- every attempt at the contract board (completed and invalidated stay unconditional)" },
                { "econtick",       "EconomyTickProfiler -- per economy tick, where vanilla's UpdateStations spent its time" },
                { "physics",        "PHYSICS_CLUSTER_REORDER — each physics-cluster reorder over a box past 40 km, with the entity that moved; PHYSICS_STALL — each physics step over 2 s, with every cluster and the fastest active bodies; SPEED_CAP — a grid at 95% of the speed cap or more, with its age and the asteroids it overlaps" },
                { "governor",       "GOVERNOR — each load-governor step change, with the numbers behind it; FLEET_HELD_OFFSCREEN / FLEET_WAITING / FLEET_WAIT_EXPIRED while it acts on Factions" },
                { "munitions",      "MUNITIONS_DRAWN / SPENT / RETURNED / OBSERVED — a fleet's ammunition from departure to landing, and a non-combat fleet flying short (a combat fleet's MUNITIONS_SHORT is always on)" },
                { "consent",        "TRADE_CONSENT_REFUSED — when no seller node will part with a partner's wanted item, the seller's stocked nodes by refusing gate (on change per seller, buyer, item); TRADE_PARTNER_CLOSED — a whitelisted partner skipped as barred or opted out (on change per actor, partner, direction)" },
                { "storebar",       "STORE_REFUSED — each transaction refused at a faction's store block or Services Terminal because the customer is barred from trading with the owner" },
                { "extraction",     "EXTRACTION_PLAN — each NPC standing extraction order: hub, ore, consumption, passive, mined, stock, gap, target, state and which limit binds" },
                { "tactics",        "FLIGHT_TACTIC — a fighting lead's target, arm, bracket, held range and both sides' in-range DPS, each time the read changes, and FIRE_MOUNT once per weapon subgrid attributed to its hull (Torch log); with tactic learning on, TACTIC_CHOSEN — each side's learned pick per duel (ctx=, prior, draw) — and TACTIC_REVIEW — what the outcome taught it (decision log)" },
                { "admission",      "TRAFFIC_ADMISSION — each spawn start: predicted cost, the traffic budget's headroom, and whether it would have been admitted; with the traffic-presence feature on, TRAFFIC_REFUSED — each civilian fleet the presence rule keeps abstract, once per refusal; with traffic-queue on, TRAFFIC_QUEUED / CLEARED / DIVERTED / DELAYED — each station-queue change" },
                { "alloc",          "ALLOC_SPLIT — managed allocation every 30 s: process total, game thread, worker threads, top session components, plugins, entity types and Factions sections by MB/s (Torch log)" },
                { "spawncost",      "SPAWN_COST — each Factions spawn landing: our callback's steps, the frame it landed in (SE's grid add included), and the gap the spawn pacer imposes after it (Torch log)" },
                { "restore",        "RESTORE_PROGRESS — each interval of a running restoration: placed, welded, skipped, paid and left so far" },
                { "landing",        "LANDING_PROBE — each landing-gear lock to and release from a voxel: the lock pivot's height over the terrain (Havok ray and analytic surface) and a checksum of the voxel content under it, then the grid's position 0.25 s and 1 s after release (Torch log)" },
                { "trespass",       "TRESPASS_CROSSING — each seated pilot's change of space (from, to, either may be '-' or a player faction's) or of whether the owner sees them, and each logout from inside someone's space (decision log)" },
                { "asminput",       "ASSEMBLER_INPUT — each change of an assembler's input-tray plan: mode, the queue item it stocks, what it pushed out and pulled in (Torch log)" },
                { "relation",       "RELATION_RESOLVE — each viewer's answer for an owner faction when it changes, with the deciding step and the SE table entry; SCANNER_RELATION — each scanner send that changed a contact's relation or reset the client, with the reason and the shifts by owner; RELATION_ROWS — player-faction rows in SE's relation table, checked every 60 s, on change (Torch log)" },
                { "fuel",           "FUEL_OBSERVED / FSD_OBSERVED — modelled vs observed burn on a materialised manoeuvre and on a player grid in supercruise — and FUEL_DRAWN, one line per leg the abstract fleet flies (very high volume; 'factions fuel' is the aggregate)" },
            };

        /// <summary>Flips one trace by name. Returns false for an unknown name so the caller can fall
        /// through to whatever else owns that word.</summary>
        public static bool TryToggle(string name, out bool nowOn, out string description)
        {
            nowOn = false; description = null;
            if (string.IsNullOrEmpty(name)) return false;

            switch (name.ToLowerInvariant())
            {
                case "produced":       Produced = !Produced;                     nowOn = Produced; break;
                case "itemxfer":       ItemXfer = !ItemXfer;                     nowOn = ItemXfer; break;
                case "upkeep":         UpkeepIncome = !UpkeepIncome;             nowOn = UpkeepIncome; break;
                case "itemdemand":     ItemDemand = !ItemDemand;                 nowOn = ItemDemand; break;
                case "shipevents":     ShipEvents = !ShipEvents;                 nowOn = ShipEvents; break;
                case "investment":     Investment = !Investment;                 nowOn = Investment; break;
                case "bankdenials":    CentralBankDenials = !CentralBankDenials; nowOn = CentralBankDenials; break;
                case "braindecisions": BrainDecisions = !BrainDecisions;         nowOn = BrainDecisions; break;
                case "slots":          SlotGate = !SlotGate;                     nowOn = SlotGate; break;
                case "buildrate":      BuildRate = !BuildRate;                   nowOn = BuildRate; break;
                case "diplomacy":      Diplomacy = !Diplomacy;                   nowOn = Diplomacy; break;
                case "grids":          GridMarks = !GridMarks;                   nowOn = GridMarks; break;
                case "sensing":        Sensing = !Sensing;                       nowOn = Sensing; break;
                case "contracts":      Contracts = !Contracts;                   nowOn = Contracts; break;
                case "econtick":       EconomyProfiler = !EconomyProfiler;       nowOn = EconomyProfiler; break;
                case "physics":        Physics = !Physics;                       nowOn = Physics; break;
                case "fuel":           Fuel = !Fuel;                             nowOn = Fuel; break;
                case "governor":       Governor = !Governor;                     nowOn = Governor; break;
                case "munitions":      Munitions = !Munitions;                   nowOn = Munitions; break;
                case "consent":        Trade = !Trade;                           nowOn = Trade; break;
                case "storebar":       StoreBar = !StoreBar;                     nowOn = StoreBar; break;
                case "extraction":     Extraction = !Extraction;                 nowOn = Extraction; break;
                case "tactics":        Tactics = !Tactics;                       nowOn = Tactics; break;
                case "restore":        Restoration = !Restoration;               nowOn = Restoration; break;
                case "admission":      Admission = !Admission;                   nowOn = Admission; break;
                case "spawncost":      SpawnCost = !SpawnCost;                   nowOn = SpawnCost; break;
                case "alloc":          Alloc = !Alloc;                           nowOn = Alloc; break;
                case "landing":        Landing = !Landing;                       nowOn = Landing; break;
                case "trespass":       Trespass = !Trespass;                     nowOn = Trespass; break;
                case "asminput":       AssemblerInput = !AssemblerInput;         nowOn = AssemblerInput; break;
                case "relation":       Relation = !Relation;                     nowOn = Relation; break;
                default: return false;
            }
            Descriptions.TryGetValue(name, out description);
            return true;
        }

        /// <summary>Every trace name and whether it is currently on, for the startup state line.</summary>
        public static IEnumerable<KeyValuePair<string, bool>> All()
        {
            yield return new KeyValuePair<string, bool>("produced",       Produced);
            yield return new KeyValuePair<string, bool>("itemxfer",       ItemXfer);
            yield return new KeyValuePair<string, bool>("upkeep",         UpkeepIncome);
            yield return new KeyValuePair<string, bool>("itemdemand",     ItemDemand);
            yield return new KeyValuePair<string, bool>("shipevents",     ShipEvents);
            yield return new KeyValuePair<string, bool>("investment",     Investment);
            yield return new KeyValuePair<string, bool>("bankdenials",    CentralBankDenials);
            yield return new KeyValuePair<string, bool>("braindecisions", BrainDecisions);
            yield return new KeyValuePair<string, bool>("slots",          SlotGate);
            yield return new KeyValuePair<string, bool>("buildrate",      BuildRate);
            yield return new KeyValuePair<string, bool>("diplomacy",      Diplomacy);
            yield return new KeyValuePair<string, bool>("grids",          GridMarks);
            yield return new KeyValuePair<string, bool>("sensing",        Sensing);
            yield return new KeyValuePair<string, bool>("contracts",      Contracts);
            yield return new KeyValuePair<string, bool>("econtick",       EconomyProfiler);
            yield return new KeyValuePair<string, bool>("physics",        Physics);
            yield return new KeyValuePair<string, bool>("fuel",           Fuel);
            yield return new KeyValuePair<string, bool>("governor",       Governor);
            yield return new KeyValuePair<string, bool>("munitions",      Munitions);
            yield return new KeyValuePair<string, bool>("consent",        Trade);
            yield return new KeyValuePair<string, bool>("storebar",       StoreBar);
            yield return new KeyValuePair<string, bool>("extraction",     Extraction);
            yield return new KeyValuePair<string, bool>("tactics",        Tactics);
            yield return new KeyValuePair<string, bool>("restore",        Restoration);
            yield return new KeyValuePair<string, bool>("admission",      Admission);
            yield return new KeyValuePair<string, bool>("spawncost",      SpawnCost);
            yield return new KeyValuePair<string, bool>("alloc",          Alloc);
            yield return new KeyValuePair<string, bool>("landing",        Landing);
            yield return new KeyValuePair<string, bool>("trespass",       Trespass);
            yield return new KeyValuePair<string, bool>("asminput",       AssemblerInput);
            yield return new KeyValuePair<string, bool>("relation",       Relation);
        }

        /// <summary>Restores the shipped defaults. Tests flip these statics, and a leak into a
        /// sibling test class is the one real cost of a per-process toggle.</summary>
        public static void ResetToDefaults()
        {
            Produced = false; ItemXfer = false; UpkeepIncome = false;
            ItemDemand = true; ShipEvents = true; Investment = true;
            CentralBankDenials = false; BrainDecisions = true; SlotGate = false;
            Diplomacy = false; GridMarks = false; Fuel = false;
            BuildRate = false; Sensing = false;
            Contracts = false; EconomyProfiler = false; Governor = false; Physics = false;
            Munitions = false; Trade = false; StoreBar = false;
            Extraction = false; Tactics = false; Restoration = false;
            Admission = false; SpawnCost = false; Alloc = false;
            Landing = false; Trespass = false; AssemblerInput = false;
            Relation = false;
        }
    }
}
