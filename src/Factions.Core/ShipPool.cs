using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// Query/consume/return helpers over <see cref="SpatialNode.ShipStock"/> — a single
    /// <c>Dictionary&lt;prefabId, count&gt;</c> pool per node, not partitioned by role. A ship's
    /// eligibility for a purpose is decided here, at query time, from its prefab's catalogue tags
    /// (<see cref="IPrefabCatalogue.WeaponBands"/>/<see cref="IPrefabCatalogue.CargoLiters"/>/
    /// <see cref="IPrefabCatalogue.GridSize"/>) rather than which bucket it happened to be built
    /// into — the same ship can be combat-suitable and cargo-suitable at once, and is visible to
    /// every purpose it qualifies for.
    /// </summary>
    public static class ShipPool
    {
        /// <summary>Real gravity/atmosphere samples at a dispatch's origin and destination, plus the
        /// dry-TWR floor to gate against — passed to <see cref="Consume"/>/<see cref="ConsumeForRole"/>
        /// so a candidate whose <c>Down</c>-facing thrust can't survive either end (e.g. an
        /// Atmospheric-only hull dispatched toward deep space) is never selected. <c>MinDryTwr</c> is
        /// bundled here (from <see cref="FactionTuning.MinDryTwr"/>) rather than adding a
        /// <see cref="FactionTuning"/> parameter to every method below.</summary>
        public struct RouteEnvironment
        {
            public double OriginGravityG;
            public bool   OriginInAtmosphere;
            /// <summary>Air density at the origin (<see cref="ThrustPhysics.AirDensity(IPlanetCatalogue, Vec3)"/>):
            /// what sets thrust effectiveness, where gravity sets weight.</summary>
            public double OriginAirDensity;
            public double DestGravityG;
            public bool   DestInAtmosphere;
            public double DestAirDensity;
            public double MinDryTwr;
            /// <summary>Gravity floor for loaded-cargo TWR only (<see cref="MaxCargoMassKg"/>) — see
            /// <see cref="ThrustPhysics.MaxCargoMassKg"/> for why. Defaults to 0 ("no floor"), which
            /// keeps every pre-existing caller (the dry-TWR gate never reads this field) behaviour-
            /// identical; only cargo-mass-trim call sites pass <c>tuning.OrbitGravityMin</c> here.</summary>
            public double GravityFloorG;
            /// <summary>True when the leg cannot be flown inside one atmosphere: an end is in an
            /// atmosphere the other end is not in. Both ends of a surface-to-surface leg between two
            /// planets read as in-atmosphere, and the whole space between them is vacuum.</summary>
            public bool LeavesAtmosphere;
            /// <summary>Gravity where the leg crosses an atmosphere's edge — no air, and the most
            /// gravity anywhere outside an atmosphere on this leg. The higher of the two crossings
            /// when the leg leaves one atmosphere and enters another.</summary>
            public double ExitGravityG;

            /// <summary>Samples real gravity/atmosphere at both ends via <see
            /// cref="IPlanetCatalogue"/> — the one shared way every dispatch call site should build
            /// a <see cref="RouteEnvironment"/>, so the sampling logic lives in exactly one place.
            /// <paramref name="gravityFloorG"/> is optional — only cargo-mass-trim call sites need to
            /// pass <c>tuning.OrbitGravityMin</c>; the dry-TWR route gate ignores it entirely.</summary>
            public static RouteEnvironment From(IPlanetCatalogue planets, Vec3 originPosKm, Vec3 destPosKm,
                double minDryTwr, double gravityFloorG = 0)
            {
                double originG = planets.GravityAt(originPosKm);
                PlanetRecord originAtmo = ThrustPhysics.AtmosphereAt(planets, originPosKm);

                // Several callers ask about a single place rather than a leg -- a station shelving
                // its own stock, a shipyard checking its own pad -- and pass that one position as
                // both ends. Sampling it twice cannot produce a different answer.
                bool samePoint = originPosKm.X == destPosKm.X
                              && originPosKm.Y == destPosKm.Y
                              && originPosKm.Z == destPosKm.Z;
                PlanetRecord destAtmo = samePoint ? originAtmo : ThrustPhysics.AtmosphereAt(planets, destPosKm);

                double originAir = AirDensityIn(originAtmo, originPosKm);
                var env = new RouteEnvironment
                {
                    OriginGravityG     = originG,
                    OriginInAtmosphere = originAtmo != null,
                    OriginAirDensity   = originAir,
                    DestGravityG       = samePoint ? originG : planets.GravityAt(destPosKm),
                    DestInAtmosphere   = destAtmo != null,
                    DestAirDensity     = samePoint ? originAir : AirDensityIn(destAtmo, destPosKm),
                    MinDryTwr          = minDryTwr,
                    GravityFloorG      = gravityFloorG,
                };
                if (samePoint
                    || PlanetRoutePlanner.BodyKey(originAtmo) == PlanetRoutePlanner.BodyKey(destAtmo))
                    return env;

                env.LeavesAtmosphere = true;
                if (originAtmo != null)
                    env.ExitGravityG = planets.GravityAt(AtmosphereEdge(originAtmo, originPosKm));
                if (destAtmo != null)
                    env.ExitGravityG = Math.Max(env.ExitGravityG, planets.GravityAt(AtmosphereEdge(destAtmo, destPosKm)));
                return env;
            }

            private static double AirDensityIn(PlanetRecord atmosphere, Vec3 posKm)
            {
                return atmosphere == null ? 0 : ThrustPhysics.AirDensity(atmosphere, posKm.DistanceTo(atmosphere.Center));
            }

            private static Vec3 AtmosphereEdge(PlanetRecord body, Vec3 posKm)
            {
                Vec3 up = PlanetGeometry.RadialUnit(body, posKm);
                return new Vec3(body.Center.X + up.X * body.AtmosphereRadius,
                                body.Center.Y + up.Y * body.AtmosphereRadius,
                                body.Center.Z + up.Z * body.AtmosphereRadius);
            }

            /// <summary>Maximum additional cargo mass (kg) <paramref name="prefabId"/> can carry on
            /// this route — the binding (lower) limit of both ends — while still clearing <see
            /// cref="MinDryTwr"/>, gravity-floored at <see cref="GravityFloorG"/>. See <see
            /// cref="ThrustPhysics.MaxCargoMassKg"/> for the per-end math and the floor's rationale.
            /// +Infinity ("don't constrain") when <paramref name="cat"/> is null or the prefab is
            /// unscanned.</summary>
            public double MaxCargoMassKg(IPrefabCatalogue cat, string prefabId)
            {
                if (cat == null || prefabId == null) return double.PositiveInfinity;
                double dryMassKg = cat.DryMassKg(prefabId);
                IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = cat.ThrustBands(prefabId);
                ThrustDirection lift = LiftBandFor(cat, prefabId);
                double origin = ThrustPhysics.MaxCargoMassKg(bands, dryMassKg, OriginGravityG, OriginAirDensity,
                                                             OriginInAtmosphere, MinDryTwr, GravityFloorG, lift);
                double dest   = ThrustPhysics.MaxCargoMassKg(bands, dryMassKg, DestGravityG, DestAirDensity,
                                                             DestInAtmosphere, MinDryTwr, GravityFloorG, lift);
                return origin < dest ? origin : dest;
            }
        }


        // ---- pure-value suitability (no IPrefabCatalogue needed) --------------------------------
        // Single source of truth for the actual threshold comparisons, so callers that only have raw
        // scanned stats in hand -- PrefabCatalogueScanner, mid-scan, before an entry is wrapped in a
        // queryable IPrefabCatalogue -- can reuse the exact same suitability rules dispatch-time
        // ShipPool queries use, instead of re-deriving them. The IPrefabCatalogue-based overloads
        // below are thin wrappers over these.

        /// <summary><paramref name="gridSize"/> is an eligibility gate, not a score input — a
        /// station (e.g. a GlobalEncounter prefab that happens to carry weapon blocks) is never
        /// ship-pool-suitable no matter how high its stats, since it can't be dispatched. Null/absent
        /// (the default) skips the gate, for callers that haven't scanned a grid size yet.</summary>
        public static bool IsCombatSuitableValue(double totalDps, double minDps = 0, string gridSize = null)
        {
            if (gridSize == "Station") return false;
            return totalDps > 0 && totalDps >= minDps;
        }

        /// <summary>See <see cref="IsCombatSuitableValue"/> for the <paramref name="gridSize"/> gate.</summary>
        public static bool IsCargoSuitableValue(long cargoLiters, double minCargoLiters = 0, string gridSize = null)
        {
            if (gridSize == "Station") return false;
            return cargoLiters > 0 && cargoLiters >= minCargoLiters;
        }

        /// <summary>Largest <see cref="IPrefabCatalogue.CargoLiters"/> among the universal ("All"
        /// faction-hint) pool for <paramref name="role"/> -- the same rescue pool
        /// <c>ShipyardManager.SelectPrefabBom</c> falls back to when a faction-hint set is
        /// infeasible, so it is a real, buildable ceiling rather than an arbitrary constant.
        /// <para>Callers use it to clamp a Ship demand's <c>MinCargoLiters</c>: an unreachable floor
        /// filters every candidate out, and SelectPrefabBom then drops the floor and ranks the
        /// unfiltered pool cheapest-first -- so the demand that wanted the biggest hull gets the
        /// smallest one. The floor should describe a size a hull could actually satisfy.</para>
        /// <para>Returns 0 when the catalogue is absent or the role has no cargo-carrying prefab,
        /// which callers read as "no ceiling known, leave the floor alone".</para></summary>
        public static long LargestCargoLitersForRole(IPrefabCatalogue catalogue, string role)
        {
            if (catalogue == null) return 0;
            long max = 0;
            foreach (string id in catalogue.GetNamesForRole(role, "All"))
            {
                long liters = catalogue.CargoLiters(id);
                if (liters > max) max = liters;
            }
            return max;
        }

        /// <summary>Largest summed weapon DPS among the universal ("All" faction-hint) pool for
        /// <paramref name="role"/> — the combat counterpart of <see cref="LargestCargoLitersForRole"/>,
        /// and used the same way: to clamp a Ship demand's <c>MinDps</c> to a hull that can actually
        /// be built. Returns 0 when the catalogue is absent or the role has no armed prefab.</summary>
        public static long LargestCombatDpsForRole(IPrefabCatalogue catalogue, string role)
        {
            if (catalogue == null) return 0;
            long max = 0;
            foreach (string id in catalogue.GetNamesForRole(role, "All"))
            {
                long dps = (long)TotalDps(catalogue, id);
                if (dps > max) max = dps;
            }
            return max;
        }

        /// <summary>The strongest raid squadron one node could field right now, in the shape
        /// <see cref="SquadronAssembler.Assemble"/> would draw it.</summary>
        public struct SquadronStrength
        {
            /// <summary>Summed DPS of the chosen hulls.</summary>
            public long Dps;
            /// <summary>Hulls chosen: 0 when nothing anywhere can fight.</summary>
            public int Hulls;
            /// <summary>Squadron slots still empty — the large slot and the unfilled fighter slots.
            /// A budget gap is shared across these; when none is open the weakest hull is what a new
            /// build has to beat.</summary>
            public int OpenSlots;
            /// <summary>DPS of the weakest chosen hull; 0 when none.</summary>
            public long WeakestDps;
            /// <summary>The node that fields it; null when <see cref="Hulls"/> is 0.</summary>
            public string NodeId;
        }

        /// <summary>
        /// The strongest squadron any one node in <paramref name="nodes"/> can assemble: one
        /// large-grid combat hull plus up to <paramref name="maxFighters"/> small-grid ones, best DPS
        /// first, or a single combat hull of any size when neither slot finds one — the composition
        /// <see cref="SquadronAssembler"/> draws at dispatch, read ahead of time. Per node, because a
        /// squadron is drawn from one node's stock; the network total would count hulls that never
        /// fly together. The DPS a requirement is measured against: a budget is met by one big hull
        /// or by a swarm of small ones alike, up to the number a squadron can carry.
        /// </summary>
        public static SquadronStrength BestSquadron(IEnumerable<SpatialNode> nodes, IPrefabCatalogue cat, int maxFighters)
        {
            var best = new SquadronStrength();
            if (cat == null || nodes == null) return best;
            int fighters = Math.Max(0, maxFighters);
            foreach (SpatialNode n in nodes)
            {
                var large = new List<double>();
                var small = new List<double>();
                var any = new List<double>();
                foreach (var kv in n.ShipStock)
                {
                    if (!IsCombatSuitable(cat, kv.Key)) continue;
                    int available = Available(n, kv.Key);
                    if (available <= 0) continue;
                    double dps = TotalDps(cat, kv.Key);
                    string grid = cat.GridSize(kv.Key);
                    List<double> bucket = grid == "Large" ? large : grid == "Small" ? small : any;
                    for (int i = 0; i < available; i++) bucket.Add(dps);
                    if (bucket != any) for (int i = 0; i < available; i++) any.Add(dps);
                }
                var chosen = new List<double>();
                if (large.Count > 0) { large.Sort(); chosen.Add(large[large.Count - 1]); }
                small.Sort();
                for (int i = 0; i < fighters && small.Count - 1 - i >= 0; i++) chosen.Add(small[small.Count - 1 - i]);
                if (chosen.Count == 0 && any.Count > 0) { any.Sort(); chosen.Add(any[any.Count - 1]); }
                if (chosen.Count == 0) continue;

                var here = new SquadronStrength { Hulls = chosen.Count, NodeId = n.Id, WeakestDps = long.MaxValue };
                foreach (double d in chosen)
                {
                    here.Dps += (long)d;
                    if ((long)d < here.WeakestDps) here.WeakestDps = (long)d;
                }
                here.OpenSlots = Math.Max(0, 1 + fighters - chosen.Count);
                if (best.NodeId == null || here.Dps > best.Dps) best = here;
            }
            return best;
        }

        /// <summary>Mine wants a real drill, not just cargo space — a Trade hauler does not qualify.
        /// Requires both a nonzero drill yield (the hull can actually mine) and cargo capacity (it
        /// has somewhere to put what it mines) — every real vanilla mining hull carries both. See
        /// <see cref="IsCombatSuitableValue"/> for the <paramref name="gridSize"/> gate.</summary>
        public static bool IsMineSuitableValue(double drillYield, long cargoLiters, double minDrillYield = 0, string gridSize = null)
        {
            if (gridSize == "Station") return false;
            return drillYield > 0 && drillYield >= minDrillYield && cargoLiters > 0;
        }

        /// <summary><paramref name="minDps"/> optionally raises the bar above "any weapon at all" —
        /// default 0 reproduces the original nonzero-only gate exactly (every existing caller is
        /// unaffected).</summary>
        public static bool IsCombatSuitable(IPrefabCatalogue cat, string prefabId, double minDps = 0)
        {
            return IsCombatSuitableValue(TotalDps(cat, prefabId), minDps, cat != null ? cat.GridSize(prefabId) : null);
        }

        /// <summary><paramref name="minCargoLiters"/> optionally raises the bar above "any cargo
        /// space at all" — default 0 reproduces the original nonzero-only gate exactly (every
        /// existing caller is unaffected).</summary>
        public static bool IsCargoSuitable(IPrefabCatalogue cat, string prefabId, double minCargoLiters = 0)
        {
            return cat != null && IsCargoSuitableValue(cat.CargoLiters(prefabId), minCargoLiters, cat.GridSize(prefabId));
        }

        /// <summary>Mine wants a real drill, not just cargo space — a Trade hauler does not qualify.
        /// Requires both a nonzero <see cref="IPrefabCatalogue.DrillYieldScore"/> (the hull can
        /// actually mine) and cargo capacity (it has somewhere to put what it mines) — every real
        /// vanilla mining hull carries both. <paramref name="minDrillYield"/> optionally raises the
        /// bar above "any drill at all" — default 0 reproduces the original nonzero-only gate
        /// exactly (every existing caller is unaffected).</summary>
        public static bool IsMineSuitable(IPrefabCatalogue cat, string prefabId, double minDrillYield = 0)
        {
            return cat != null && IsMineSuitableValue(cat.DrillYieldScore(prefabId), cat.CargoLiters(prefabId), minDrillYield, cat.GridSize(prefabId));
        }

        /// <summary>Trade/Gift/Haul want cargo capacity; Mine wants a real drill (see
        /// <see cref="IsMineSuitable"/>); Raid/Retaliate/Conquer/Guard/Patrol/Reinforce want combat power.
        /// Everything else (Expand, Develop, ...) never needs a ship and returns false.</summary>
        public static bool IsSuitableFor(ActionKind kind, IPrefabCatalogue cat, string prefabId)
        {
            switch (kind)
            {
                case ActionKind.Trade:
                case ActionKind.Gift:
                case ActionKind.Haul:
                    return IsCargoSuitable(cat, prefabId);
                case ActionKind.Mine:
                    return IsMineSuitable(cat, prefabId);
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer:
                case ActionKind.Guard:
                case ActionKind.Patrol:
                case ActionKind.Reinforce:
                    return IsCombatSuitable(cat, prefabId);
                default:
                    return false;
            }
        }

        /// <summary>True for the purposes whose eligibility is "has cargo space" — the ones that draw
        /// from the same untagged pool a drill- or weapon-equipped hull also sits in. These get the
        /// tiered least-specialised-first selection in <see cref="PreferGeneralist"/>, so cargo work
        /// stops preferentially consuming the network's only drills and warships.</summary>
        private static bool IsCargoPurpose(ActionKind kind)
        {
            return kind == ActionKind.Trade || kind == ActionKind.Gift || kind == ActionKind.Haul;
        }

        /// <summary>Whether a hull carries a capability cargo work does not need: a real drill
        /// (<see cref="IsMineSuitable"/>) or weapons (<see cref="IsCombatSuitable"/>). Cargo capacity
        /// is not a specialism — every candidate here has it, which is why it is not counted.</summary>
        public static int SpecialistCapabilityCount(IPrefabCatalogue cat, string prefabId)
        {
            if (cat == null) return 0;
            int n = 0;
            if (IsMineSuitable(cat, prefabId)) n++;
            if (IsCombatSuitable(cat, prefabId)) n++;
            return n;
        }

        /// <summary>Runs <paramref name="consume"/> for cargo work in three passes, taking the least
        /// specialised hull that can do the job: no specialist capability, then one, then anything.
        ///
        /// <para>Deterministic, nothing to weight, and it can never block a dispatch that would have
        /// succeeded before — the last pass is unrestricted, which is exactly the old behaviour. That
        /// guarantee is the whole design: a faction whose only cargo hulls carry drills must still
        /// haul.</para>
        ///
        /// <para><b>Why it matters that Haul goes first.</b> Mine and Haul share one
        /// <see cref="SpatialNode.ShipStock"/> pool and Haul dispatches first every cycle (Mine's
        /// shortfall is by definition what is left after Haul's pass), so without this a hull that can
        /// both haul and drill is taken for cargo before Mine ever looks, and the resulting Ship:Mine
        /// demand builds a replacement Haul takes too.</para>
        ///
        /// <para><b>The middle pass exists for the dual-role hull.</b> Measured live 2026-09-02, 8.2%
        /// of cargo convoys took a hull that was both drill- AND weapon-equipped — the scarcest kind
        /// there is. Two passes would have spent those exactly as readily as a single-specialism hull.
        /// Within the middle pass the two specialisms are deliberately NOT ranked against each other:
        /// which is scarcer is a per-faction question and no evidence has been taken on it. See
        /// docs/issues/FAC-235.</para></summary>
        private static int PreferGeneralist(IPrefabCatalogue cat,
            System.Func<System.Func<string, bool>, int> consume)
        {
            if (cat != null)
            {
                int consumed = consume(id => SpecialistCapabilityCount(cat, id) == 0);
                if (consumed > 0) return consumed;

                consumed = consume(id => SpecialistCapabilityCount(cat, id) <= 1);
                if (consumed > 0) return consumed;
            }
            return consume(null);
        }

        /// <summary>True for the kinds <see cref="IsSuitableFor"/> ever gates on (Trade, Gift, Haul,
        /// Mine, Raid, Retaliate, Conquer, Guard, Patrol, Reinforce) — distinguishes "this kind needs a ship but
        /// none is suitable" from "this kind never needed a ship in the first place" (Expand, Develop,
        /// ...), which callers must not accidentally zero out via a stock check.</summary>
        public static bool RequiresShip(ActionKind kind)
        {
            switch (kind)
            {
                case ActionKind.Trade:
                case ActionKind.Gift:
                case ActionKind.Haul:
                case ActionKind.Mine:
                case ActionKind.Raid:
                case ActionKind.Retaliate:
                case ActionKind.Conquer:
                case ActionKind.Guard:
                case ActionKind.Patrol:
                case ActionKind.Reinforce:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Role-string variant of <see cref="IsSuitableFor"/> for the sites that only have
        /// a role name (build-demand fulfillment) rather than an <see cref="ActionKind"/> — demand
        /// posting stays role-vocabulary based, so this bridges that vocabulary to the same
        /// underlying capability tags.
        /// <para>Every role here is a real capability. The two store roles are gone (FAC-060): a
        /// shelf hull is demanded by whichever capability role it belongs to, and its purpose rides
        /// on <see cref="DemandEntry.DestinedForSale"/> instead of on its name.</para></summary>
        public static bool IsSuitableForRole(string role, IPrefabCatalogue cat, string prefabId)
        {
            switch (role)
            {
                case "Trade":
                    return IsCargoSuitable(cat, prefabId);
                case "Mine":
                    return IsMineSuitable(cat, prefabId);
                case "Raid":
                case "Patrol":
                    return IsCombatSuitable(cat, prefabId);
                default:
                    return false;
            }
        }

        /// <summary>Whether <paramref name="role"/>'s job is combat — i.e. whether weapon Dps is a
        /// reason to prefer one candidate over another when building for it.
        ///
        /// <para>Beside <see cref="IsSuitableForRole"/> deliberately, and reading the same role
        /// vocabulary, so the two cannot drift: a role that is selected on combat suitability is
        /// exactly a role whose builds should weigh combat power.</para>
        ///
        /// <para><b>This is not the same question as "does the candidate set contain anything
        /// armed".</b> That was the proxy <see cref="ShipyardManager"/> used, and it broke once
        /// dual-role hulls entered cargo pools — measured live 2026-09-03, every hull built for a
        /// `Trade` role in the sample carried Trade AND Raid/Patrol/Guard with 2,279-48,428 Dps. A
        /// set containing armed hulls says nothing about whether the JOB wants them.</para></summary>
        public static bool RoleWantsCombatPower(string role)
        {
            switch (role)
            {
                case "Raid":
                case "Patrol":
                case "Guard":
                case "Retaliate":
                case "Conquer":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Whether a build for <paramref name="role"/> should rank candidates on weapon
        /// power rather than cheapest-first.
        ///
        /// <para>All four conditions must hold. A peacetime posture, a single candidate, or a set
        /// with no Dps to differentiate on all fall back to cost — those three are the original
        /// Muster guard and are unchanged. The fourth, <see cref="RoleWantsCombatPower"/>, is
        /// FAC-239: <paramref name="hasAnyPower"/> alone was standing in for "this job wants
        /// weapons", and it stopped meaning that once dual-role hulls entered cargo pools.</para>
        ///
        /// <para>Extracted so both directions are testable: that a cargo build stops being resolved
        /// on Dps, and — the one that matters more — that a combat build still is.</para>
        ///
        /// <para><paramref name="destinedForSale"/> is a fifth refusal, and it is about whose
        /// statistic <c>militaryPosture</c> is. Posture describes the building faction's own
        /// military situation; a shelf hull is chosen for a buyer, so resolving it on the seller's
        /// war footing answers a question nobody asked. It is the same exclusion
        /// <see cref="FleetFitness.SizingFloor"/> already makes for the same reason.</para>
        ///
        /// <para><b>Unreachable today, deliberately.</b> <c>SelectPrefabBom</c> tests
        /// <c>stockByPrefab != null</c> first, so a shelf build takes the diversification branch and
        /// never arrives here. This states the exclusion as a property of the purpose rather than
        /// leaving it a consequence of branch order — which the next change would otherwise have to
        /// preserve by accident. Its test targets this predicate directly for exactly that reason:
        /// a test routed through <c>SelectPrefabBom</c> would pass without the gate and prove
        /// nothing.</para></summary>
        public static bool ShouldRankOnCombatPower(string role, double militaryPosture,
            int candidateCount, bool hasAnyPower, bool destinedForSale = false)
        {
            if (destinedForSale) return false;
            if (militaryPosture <= 0) return false;
            if (candidateCount <= 1) return false;
            if (!hasAnyPower) return false;
            return RoleWantsCombatPower(role);
        }

        /// <summary>Units of <paramref name="prefabId"/> actually available to fleet dispatch —
        /// stock minus whatever's reserved for store sale. <see cref="SpatialNode.ListedForSale"/>
        /// is a hard floor: fleet consumption (<see cref="Consume"/>/<see cref="HasSuitable"/>)
        /// never dips into it, even if that means a demand goes unfulfilled while stock technically
        /// exists. Only a store purchase (<c>StationStoreManager.OnPlayerBoughtShip</c>) draws from
        /// the reserved slice.</summary>
        public static int Available(SpatialNode node, string prefabId)
        {
            return Available((IReadOnlyDictionary<string, int>)node.ShipStock, node.ListedForSale, prefabId);
        }

        /// <summary><paramref name="route"/> null (the default) preserves today's exact behaviour —
        /// see <see cref="Consume"/>'s contract for what "route-feasible" means when supplied, and
        /// for <paramref name="viaRoute"/>'s second leg.</summary>
        public static bool HasSuitable(SpatialNode node, ActionKind kind, IPrefabCatalogue cat,
            RouteEnvironment? route = null, RouteEnvironment? viaRoute = null)
        {
            return HasSuitableWhere(node, id => IsSuitableFor(kind, cat, id)
                && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute));
        }

        public static bool NetworkHasSuitable(IEnumerable<SpatialNode> nodes, ActionKind kind, IPrefabCatalogue cat,
            RouteEnvironment? route = null)
        {
            foreach (SpatialNode n in nodes)
                if (HasSuitable(n, kind, cat, route)) return true;
            return false;
        }

        /// <summary>The combined weapon loadout a convoy of <paramref name="convoySize"/> hulls of
        /// <paramref name="prefabId"/> brings to a duel: the prefab's cached per-ship bands with each
        /// band's Dps scaled by the hull count, matching
        /// <see cref="PendingFleetAction.WeaponBands"/>'s "already scaled by ConvoySize" contract.
        /// Empty (never null) when the catalogue, prefab or count cannot supply one, which
        /// <c>ResolveShipDuel</c> reads as an unarmed convoy.</summary>
        public static List<WeaponBand> ConvoyWeaponBands(IPrefabCatalogue cat, string prefabId, int convoySize)
        {
            if (cat == null || string.IsNullOrEmpty(prefabId) || convoySize <= 0)
                return new List<WeaponBand>();

            IReadOnlyList<WeaponBand> perShip = cat.WeaponBands(prefabId);
            if (perShip == null) return new List<WeaponBand>();
            var scaled = new List<WeaponBand>(perShip.Count);
            for (int i = 0; i < perShip.Count; i++)
                scaled.Add(new WeaponBand { Dps = perShip[i].Dps * convoySize, RangeM = perShip[i].RangeM });
            return scaled;
        }

        /// <summary>Total available units suitable for <paramref name="kind"/> across
        /// <paramref name="nodes"/>. Unlike the pre-unification per-role counters, a combat-suitable
        /// ship counts once here regardless of how many combat kinds (Raid/Retaliate/Conquer/Guard/
        /// Patrol) it happens to qualify for — there's no separate "Raid stock" vs "Patrol stock" to
        /// sum anymore, since both draw from the same tagged pool.</summary>
        public static int NetworkSuitableCount(IEnumerable<SpatialNode> nodes, ActionKind kind, IPrefabCatalogue cat)
        {
            int total = 0;
            foreach (SpatialNode n in nodes)
                foreach (var kv in n.ShipStock)
                    if (IsSuitableFor(kind, cat, kv.Key)) total += Available(n, kv.Key);
            return total;
        }

        /// <summary>Available units at <paramref name="node"/> suitable for <paramref name="role"/>,
        /// ignoring route feasibility — the role-string counterpart to <see cref="NetworkSuitableCount"/>,
        /// for callers asking "would donating one leave this node with nothing for its own need."</summary>
        public static int SuitableCountForRole(SpatialNode node, string role, IPrefabCatalogue cat)
        {
            int total = 0;
            foreach (var kv in node.ShipStock)
                if (IsSuitableForRole(role, cat, kv.Key)) total += Available(node, kv.Key);
            return total;
        }

        /// <summary>Role-string variant of <see cref="HasSuitable"/> for demand-processing sites
        /// that only have a role name — see <see cref="IsSuitableForRole"/> and <paramref
        /// name="route"/>'s contract on <see cref="Consume"/>.</summary>
        public static bool HasSuitableForRole(SpatialNode node, string role, IPrefabCatalogue cat,
            RouteEnvironment? route = null, RouteEnvironment? viaRoute = null, double minCargoLiters = 0,
            double minDrillYield = 0)
        {
            return HasSuitableWhere(node, id => IsSuitableForRole(role, cat, id)
                && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute)
                && (minCargoLiters <= 0 || (cat != null && cat.CargoLiters(id) >= minCargoLiters))
                && MeetsDrillFloor(cat, id, minDrillYield));
        }

        /// <summary>True when <paramref name="minDrillYield"/> asks for nothing, or the prefab drills
        /// at least that well (<see cref="DemandEntry.MinDrillYield"/>).</summary>
        private static bool MeetsDrillFloor(IPrefabCatalogue cat, string prefabId, double minDrillYield)
        {
            return minDrillYield <= 0 || (cat != null && cat.DrillYieldScore(prefabId) >= minDrillYield);
        }

        /// <summary>Picks the best-scoring eligible prefab in stock (highest Dps for combat
        /// purposes, highest CargoLiters for cargo purposes, plus a small cruise-force tie-break
        /// when <paramref name="route"/> is supplied) and consumes 1..<paramref
        /// name="desiredCount"/> units of that single prefab — one representative prefab × count,
        /// mirroring the pre-unification <c>CheckAndConsumeShipStock</c> contract. Only draws from
        /// <see cref="Available"/> stock — a prefab reserved via <see
        /// cref="SpatialNode.ListedForSale"/> is invisible to this call, even as the sole
        /// candidate. Returns the actual count consumed (capped by available stock; 0 and null
        /// prefabId when nothing eligible is available).
        /// <paramref name="route"/> null (the default) preserves today's exact behaviour. When
        /// supplied, a candidate whose dry (no-cargo) <c>Down</c>-thrust TWR falls below
        /// <see cref="RouteEnvironment.MinDryTwr"/> at either end is excluded — unless the prefab has
        /// never been scanned (<see cref="IPrefabCatalogue.DryMassKg"/> == 0), which is treated as
        /// "unknown, don't gate" rather than "no thrust."
        /// <para><paramref name="viaRoute"/> is a second leg the same hull must also clear, for an
        /// itinerary that is not one hop: a fetched hull flies donor→pickup→delivery, and a pair of
        /// endpoints cannot express three nodes. Both are hard gates and neither is a preference —
        /// the hull is left standing wherever the last leg ends, so a leg that is checked nowhere is
        /// a node it can arrive at and never leave. Mirrors
        /// <see cref="ConsumeForRole"/>'s own <c>viaRoute</c>.</para></summary>
        public static int Consume(SpatialNode node, ActionKind kind, IPrefabCatalogue cat,
            int desiredCount, out string prefabId, RouteEnvironment? route = null,
            RouteEnvironment? viaRoute = null, HaulHullCost cost = null)
        {
            if (cost != null && IsCargoPurpose(kind))
                return Consume(node.ShipStock, node.ListedForSale, kind, cat, desiredCount, out prefabId,
                               route, viaRoute, cost);
            string picked = null;
            int result = !IsCargoPurpose(kind)
                ? ConsumeWhere(node,
                      id => IsSuitableFor(kind, cat, id)
                          && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute),
                      id => ScoreFor(kind, cat, id) + RouteScoreBonus(cat, id, route),
                      desiredCount, out picked)
                : PreferGeneralist(cat, extra => ConsumeWhere(node,
                      id => IsSuitableFor(kind, cat, id)
                          && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute)
                          && (extra == null || extra(id)),
                      id => ScoreFor(kind, cat, id) + RouteScoreBonus(cat, id, route),
                      desiredCount, out picked));
            prefabId = picked;
            return result;
        }

        /// <summary>Role-string variant of <see cref="Consume"/> for demand-processing sites that
        /// only have a role name — see <see cref="IsSuitableForRole"/> and <paramref name="route"/>'s
        /// contract on <see cref="Consume"/>.</summary>
        public static int ConsumeForRole(SpatialNode node, string role, IPrefabCatalogue cat,
            int desiredCount, out string prefabId, RouteEnvironment? route = null,
            RouteEnvironment? viaRoute = null, double minCargoLiters = 0, double minDrillYield = 0)
        {
            string picked = null;
            System.Func<System.Func<string, bool>, int> run = extra => ConsumeWhere(node,
                id => IsSuitableForRole(role, cat, id)
                    && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute)
                    && (minCargoLiters <= 0 || (cat != null && cat.CargoLiters(id) >= minCargoLiters))
                    && MeetsDrillFloor(cat, id, minDrillYield)
                    && (extra == null || extra(id)),
                id => ScoreForRole(role, cat, id) + RouteScoreBonus(cat, id, route),
                desiredCount, out picked);
            // "Trade" is the role-vocabulary cargo purpose -- see IsCargoPurpose/PreferGeneralist.
            int result = role == "Trade" ? PreferGeneralist(cat, run) : run(null);
            prefabId = picked;
            return result;
        }

        /// <summary>True when <paramref name="route"/> is absent, the prefab has never been
        /// thrust-scanned (<c>DryMassKg == 0</c> — "unknown, don't gate"), or its real dry-TWR clears
        /// <see cref="RouteEnvironment.MinDryTwr"/> at both the origin and destination gravity/
        /// atmosphere sample, and — on a leg that <see cref="RouteEnvironment.LeavesAtmosphere"/> —
        /// at the atmosphere's edge with no air, which is where an atmospheric hull stops flying.
        /// A scanned prefab with zero <c>Down</c>-facing thrust (e.g. a
        /// deep-space-only hull) is a genuine, intentional gate failure — distinct from "unknown."
        /// Public so other pool-adjacent selection logic (e.g. <see cref="SquadronAssembler"/>, which
        /// picks straight from <see cref="SpatialNode.ShipStock"/> with its own eligibility
        /// predicates rather than going through <see cref="Consume"/>) can reuse the exact same gate
        /// instead of re-deriving it.</summary>
        public static bool IsRouteFeasible(IPrefabCatalogue cat, string prefabId, RouteEnvironment? route)
        {
            if (route == null || cat == null) return true;
            RouteEnvironment r = route.Value;

            double dryMassKg = cat.DryMassKg(prefabId);
            if (dryMassKg <= 0) return true;

            IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands = cat.ThrustBands(prefabId);
            ThrustDirection lift = LiftBandFor(cat, prefabId);
            double originTwr = ThrustPhysics.DryTwr(bands, dryMassKg, r.OriginGravityG, r.OriginAirDensity, r.OriginInAtmosphere, lift);
            double destTwr   = ThrustPhysics.DryTwr(bands, dryMassKg, r.DestGravityG, r.DestAirDensity, r.DestInAtmosphere, lift);
            if (originTwr < r.MinDryTwr || destTwr < r.MinDryTwr) return false;
            if (!r.LeavesAtmosphere) return true;

            // Every hull that clears the ends clears this point unless its lift needs air.
            double exitTwr = ThrustPhysics.DryTwr(bands, dryMassKg, r.ExitGravityG, 0, false, lift);
            return exitTwr >= r.MinDryTwr;
        }

        /// <summary>The band bucket that holds <paramref name="prefabId"/> up in the attitude it flies:
        /// the opposite of its own up (<see cref="HullFrame.LiftBand"/>). <c>Down</c> when the
        /// catalogue carries no frame for it, which is the belly-down hull every lift gate assumed
        /// before FAC-215.</summary>
        public static ThrustDirection LiftBandFor(IPrefabCatalogue cat, string prefabId)
        {
            var frames = cat as IHullFramePrefabCatalogue;
            if (frames == null || prefabId == null) return ThrustDirection.Down;
            HullFrame frame = frames.HullFrame(prefabId);
            return frame == null ? ThrustDirection.Down : frame.LiftBand;
        }

        /// <summary>Small additive tie-break — worst-leg cruise acceleration (m/s², via the
        /// <c>Backward</c> bucket) — so that among several route-feasible candidates, one with more
        /// forward thrust per kilogram is mildly preferred. Deliberately unnormalized against
        /// CargoLiters/Dps: a real acceleration value (typically single digits) is naturally dwarfed
        /// by those, which is the point — this only breaks near-ties, it never overrides the primary
        /// cargo/combat score. Zero when <paramref name="route"/> is absent, the prefab is unscanned,
        /// or it has no <c>Backward</c>-facing thrust.</summary>
        private static double RouteScoreBonus(IPrefabCatalogue cat, string prefabId, RouteEnvironment? route)
        {
            if (route == null || cat == null) return 0;
            RouteEnvironment r = route.Value;

            double dryMassKg = cat.DryMassKg(prefabId);
            if (dryMassKg <= 0) return 0;

            IReadOnlyList<ThrustBand> backward;
            if (!cat.ThrustBands(prefabId).TryGetValue(ThrustDirection.Backward, out backward)) return 0;

            double originAccel = ThrustPhysics.ForceN(backward, r.OriginAirDensity, r.OriginInAtmosphere) / dryMassKg;
            double destAccel   = ThrustPhysics.ForceN(backward, r.DestAirDensity, r.DestInAtmosphere) / dryMassKg;
            return System.Math.Min(originAccel, destAccel);
        }

        /// <summary>Credits <paramref name="count"/> units of <paramref name="prefabId"/> back to
        /// the node's pool (ship return on mission success). Never touches
        /// <see cref="SpatialNode.ListedForSale"/> — a returning combat ship doesn't suddenly
        /// become a store listing.</summary>
        public static void Return(SpatialNode node, string prefabId, int count)
        {
            if (count <= 0) return;
            if (prefabId == null) prefabId = "";
            int stock;
            node.ShipStock.TryGetValue(prefabId, out stock);
            node.ShipStock[prefabId] = stock + count;
        }

        /// <summary>Removes <paramref name="count"/> units of <paramref name="prefabId"/> from the
        /// node's pool (dismantle/consume by a caller outside the normal <see cref="Consume"/>-for-
        /// dispatch path — e.g. <see cref="WorldModel.ClearOrbitStranding"/>'s stranded-stock cleanup).
        /// Floors at 0, mirroring <see cref="Return"/>'s "never negative" symmetry. Does NOT touch
        /// <see cref="SpatialNode.ListedForSale"/> — callers that need to also un-list store stock must
        /// do that separately (only <see cref="Available"/> units, which already exclude
        /// <see cref="SpatialNode.ListedForSale"/>, should ever be passed to this method).</summary>
        public static void Remove(SpatialNode node, string prefabId, int count)
        {
            if (count <= 0) return;
            if (prefabId == null) prefabId = "";
            int stock;
            node.ShipStock.TryGetValue(prefabId, out stock);
            int next = stock - count;
            node.ShipStock[prefabId] = next > 0 ? next : 0;
        }

        /// <summary>Where a hull actually lands, once ownership is taken into account — the single
        /// answer every terminal that credits a hull asks, so a returning ship, an evacuating one and
        /// a relieved Guard all resolve the same way.
        ///
        /// <para><see cref="NodeId"/> is null when nothing resolved; <see cref="Reason"/> is then
        /// <c>none</c>. <see cref="PriorNodeId"/> is the node an existence-only resolve would have
        /// named (the pre-ownership rule: the preferred node if it is still in the graph, else the
        /// fallback) — it exists so a line can say it was redirected without changing shape for the
        /// case that was already correct.</para></summary>
        public struct Landing
        {
            public SpatialNode Node;
            /// <summary>Node the hull is credited to, or null when nowhere resolved.</summary>
            public string NodeId;
            /// <summary><c>owned</c>, <c>fallback_origin</c>, <c>nearest_owned</c>,
            /// <c>unknown_actor</c> or <c>none</c>.</summary>
            public string Reason;
            /// <summary>What the existence-only rule would have picked, or null when it too found
            /// nothing.</summary>
            public string PriorNodeId;

            /// <summary>True when the hull lands somewhere other than the node the old line named.</summary>
            public bool Redirected
            {
                get
                {
                    return NodeId != null && PriorNodeId != null
                        && !string.Equals(NodeId, PriorNodeId, System.StringComparison.Ordinal);
                }
            }

            /// <summary>Suffix appended to the existing credit line, empty when nothing moved — an
            /// owned landing must read exactly as it did before.</summary>
            public string RedirectSuffix
            {
                get { return Redirected ? " redirected_from=" + PriorNodeId + " why=" + Reason : ""; }
            }
        }

        /// <summary>Resolves where a hull belonging to <paramref name="actorTag"/> can land:
        /// <paramref name="preferredNodeId"/> when it exists and the actor still owns it, else
        /// <paramref name="fallbackNodeId"/> on the same terms, else the nearest node the actor owns
        /// that <paramref name="prefabId"/> can actually reach (<see cref="IsRouteFeasible"/> against
        /// <see cref="WorldModel.PrefabCatalogue"/> — absent catalogue or unscanned prefab means
        /// "unknown, don't gate", so it degenerates to nearest-by-distance), else nothing.
        ///
        /// <para>Distance is measured from the node the hull is standing at (the preferred one, or
        /// the fallback when the preferred is gone, or <paramref name="anchorPosKm"/> when neither is
        /// left in the graph), and route feasibility is only evaluated for a candidate that beats the
        /// best distance so far, so the scan costs one TWR sample per improvement rather than per
        /// node. Callers holding a fleet pass its terminal position as the anchor; the abandonment
        /// path passes the abandoned node's own position.</para>
        /// <para>A null <paramref name="actorTag"/> — a legacy fleet persisted without one — cannot
        /// have its ownership tested, so it falls back to the existence-only rule this replaced
        /// (<c>unknown_actor</c>) rather than resolving to nothing and deleting the hull.</para>
        /// <para>Provenance: FAC-278.</para></summary>
        public static Landing ResolveLanding(WorldModel world, string actorTag, string prefabId,
            string preferredNodeId, string fallbackNodeId, Vec3? anchorPosKm = null)
        {
            var landing = new Landing { Reason = "none" };
            if (world == null) return landing;

            SpatialNode preferred = null, fallback = null;
            if (preferredNodeId != null) world.Graph.Nodes.TryGetValue(preferredNodeId, out preferred);
            if (fallbackNodeId != null) world.Graph.Nodes.TryGetValue(fallbackNodeId, out fallback);

            landing.PriorNodeId = preferred != null ? preferredNodeId : (fallback != null ? fallbackNodeId : null);

            if (actorTag == null)
            {
                landing.Node = preferred ?? fallback;
                landing.NodeId = landing.PriorNodeId;
                if (landing.NodeId != null) landing.Reason = "unknown_actor";
                return landing;
            }

            if (preferred != null && IsOwnedBy(preferred, actorTag))
            {
                landing.Node = preferred; landing.NodeId = preferredNodeId; landing.Reason = "owned";
                return landing;
            }
            if (fallback != null && IsOwnedBy(fallback, actorTag))
            {
                landing.Node = fallback; landing.NodeId = fallbackNodeId; landing.Reason = "fallback_origin";
                return landing;
            }

            SpatialNode anchorNode = preferred ?? fallback;
            if (anchorNode == null && anchorPosKm == null) return landing;
            Vec3 anchor = anchorNode != null ? anchorNode.Position : anchorPosKm.Value;

            SpatialNode best = null;
            double bestSq = double.MaxValue;
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (!IsOwnedBy(n, actorTag)) continue;
                double dx = n.Position.X - anchor.X;
                double dy = n.Position.Y - anchor.Y;
                double dz = n.Position.Z - anchor.Z;
                double distSq = dx * dx + dy * dy + dz * dz;
                if (distSq >= bestSq) continue;

                RouteEnvironment route = RouteEnvironment.From(world.Planets, anchor, n.Position,
                    world.Tuning != null ? world.Tuning.MinDryTwr : 0);
                if (!IsRouteFeasible(world.PrefabCatalogue, prefabId, route)) continue;

                best = n; bestSq = distSq;
            }

            if (best == null) return landing;
            landing.Node = best; landing.NodeId = best.Id; landing.Reason = "nearest_owned";
            return landing;
        }

        private static bool IsOwnedBy(SpatialNode node, string actorTag)
        {
            return node.OwnerFactionTag != null
                && string.Equals(node.OwnerFactionTag, actorTag, System.StringComparison.Ordinal);
        }

        private static bool HasSuitableWhere(SpatialNode node, System.Func<string, bool> eligible)
        {
            return HasSuitableWhere(node.ShipStock, node.ListedForSale, eligible);
        }

        private static int ConsumeWhere(SpatialNode node, System.Func<string, bool> eligible,
            System.Func<string, double> score, int desiredCount, out string prefabId)
        {
            return ConsumeWhere(node.ShipStock, node.ListedForSale, eligible, score, desiredCount, out prefabId);
        }

        // ---- dictionary-backed overloads (staged/resumable Operations cycle, Slice 3) -----------
        // Same selection/consumption semantics as the SpatialNode-based methods above, operating
        // against a private ship-stock overlay instead of SpatialNode.ShipStock directly, so a
        // staged cycle can consume/return ships without touching the live node until a guarded
        // commit. ListedForSale is read-only here (Operations never mutates store listings).

        public static int Available(IReadOnlyDictionary<string, int> shipStock,
            IReadOnlyDictionary<string, int> listedForSale, string prefabId)
        {
            int stock = 0, reserved = 0;
            if (shipStock != null) shipStock.TryGetValue(prefabId, out stock);
            if (listedForSale != null) listedForSale.TryGetValue(prefabId, out reserved);
            int available = stock - reserved;
            return available > 0 ? available : 0;
        }

        public static bool HasSuitable(IReadOnlyDictionary<string, int> shipStock,
            IReadOnlyDictionary<string, int> listedForSale, ActionKind kind, IPrefabCatalogue cat,
            RouteEnvironment? route = null, RouteEnvironment? viaRoute = null)
        {
            return HasSuitableWhere(shipStock, listedForSale, id => IsSuitableFor(kind, cat, id)
                && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute));
        }

        public static int Consume(IDictionary<string, int> shipStock, IReadOnlyDictionary<string, int> listedForSale,
            ActionKind kind, IPrefabCatalogue cat, int desiredCount, out string prefabId,
            RouteEnvironment? route = null, RouteEnvironment? viaRoute = null, HaulHullCost cost = null)
        {
            string picked = null;
            if (cost != null && IsCargoPurpose(kind))
            {
                // Cargo work with a job in hand: the cheapest hull to fly it, inside the same
                // least-specialised-first tiers, so a drill or a gun is still spent last. A gas job
                // (HaulHullCost.GasKey) takes only a hull with tank room for the gas, and measures
                // MaxCargoLiters against that room rather than the hold.
                int taken = PreferGeneralist(cat, extra => ConsumeCheapest(shipStock, listedForSale,
                    id => IsSuitableFor(kind, cat, id)
                        && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute)
                        && cost.Carries(id)
                        && (cost.MaxCargoLiters <= 0 || (cat != null && cost.CapacityLiters(cat, id) <= cost.MaxCargoLiters))
                        && (extra == null || extra(id)),
                    cost, desiredCount, out picked));
                prefabId = picked;
                return taken;
            }
            // Must mirror the SpatialNode overload's drill-preference exactly, or a staged Operations
            // cycle picks a different hull than the synchronous path for the same world.
            System.Func<System.Func<string, bool>, int> run = extra => ConsumeWhere(shipStock, listedForSale,
                id => IsSuitableFor(kind, cat, id)
                    && IsRouteFeasible(cat, id, route) && IsRouteFeasible(cat, id, viaRoute)
                    && (extra == null || extra(id)),
                id => ScoreFor(kind, cat, id) + RouteScoreBonus(cat, id, route),
                desiredCount, out picked);
            int result = IsCargoPurpose(kind) ? PreferGeneralist(cat, run) : run(null);
            prefabId = picked;
            return result;
        }

        /// <summary>Consumes up to <paramref name="desiredCount"/> of exactly <paramref name="prefabId"/>
        /// from <paramref name="shipStock"/>, honouring the <paramref name="listedForSale"/> reservation
        /// the way <see cref="Consume(IDictionary{string, int}, IReadOnlyDictionary{string, int}, ActionKind, IPrefabCatalogue, int, out string, RouteEnvironment?, RouteEnvironment?, HaulHullCost)"/>
        /// does. For a caller that has already chosen the hull by a rule the generic scorer does not
        /// express (Mine's doctrine-then-drill-rate rank) and has already checked its suitability and
        /// route. Returns 0 when none is available.</summary>
        public static int ConsumePrefab(IDictionary<string, int> shipStock, IReadOnlyDictionary<string, int> listedForSale,
            string prefabId, int desiredCount)
        {
            if (shipStock == null || string.IsNullOrEmpty(prefabId)) return 0;
            int available = Available((IReadOnlyDictionary<string, int>)shipStock, listedForSale, prefabId);
            if (available <= 0) return 0;
            int consumed = System.Math.Min(System.Math.Max(1, desiredCount), available);
            shipStock[prefabId] = shipStock[prefabId] - consumed;
            return consumed;
        }

        public static void Return(IDictionary<string, int> shipStock, string prefabId, int count)
        {
            if (count <= 0 || shipStock == null) return;
            if (prefabId == null) prefabId = "";
            int stock;
            shipStock.TryGetValue(prefabId, out stock);
            shipStock[prefabId] = stock + count;
        }

        private static bool HasSuitableWhere(IReadOnlyDictionary<string, int> shipStock,
            IReadOnlyDictionary<string, int> listedForSale, System.Func<string, bool> eligible)
        {
            if (shipStock == null) return false;
            foreach (var kv in shipStock)
                if (Available(shipStock, listedForSale, kv.Key) > 0 && eligible(kv.Key)) return true;
            return false;
        }

        private static int ConsumeWhere(IDictionary<string, int> shipStock, IReadOnlyDictionary<string, int> listedForSale,
            System.Func<string, bool> eligible, System.Func<string, double> score, int desiredCount, out string prefabId)
        {
            string best = null;
            double bestScore = double.NegativeInfinity;
            int bestAvailable = 0;
            if (shipStock != null)
                foreach (var kv in shipStock)
                {
                    int available = Available((IReadOnlyDictionary<string, int>)shipStock, listedForSale, kv.Key);
                    if (available <= 0 || !eligible(kv.Key)) continue;
                    double s = score(kv.Key);
                    if (best == null || s > bestScore) { best = kv.Key; bestScore = s; bestAvailable = available; }
                }
            if (best == null) { prefabId = null; return 0; }

            int consumed = System.Math.Min(System.Math.Max(1, desiredCount), bestAvailable);
            int stock;
            shipStock.TryGetValue(best, out stock);
            shipStock[best] = stock - consumed;

            prefabId = best;
            return consumed;
        }

        /// <summary>Consumes the eligible prefab with the lowest <see cref="HaulHullCost.CostPerLiter"/>,
        /// ties broken by <see cref="HaulHullCost.Fit"/> and then by ordinal name, so the choice does
        /// not depend on dictionary order.</summary>
        private static int ConsumeCheapest(IDictionary<string, int> shipStock, IReadOnlyDictionary<string, int> listedForSale,
            System.Func<string, bool> eligible, HaulHullCost cost, int desiredCount, out string prefabId)
        {
            string best = null;
            double bestCost = 0, bestFit = 0;
            int bestAvailable = 0;
            if (shipStock != null)
                foreach (var kv in shipStock)
                {
                    int available = Available((IReadOnlyDictionary<string, int>)shipStock, listedForSale, kv.Key);
                    if (available <= 0 || !eligible(kv.Key)) continue;
                    double c = cost.CostPerLiter(kv.Key);
                    double f = cost.Fit(kv.Key);
                    bool better = best == null || c < bestCost
                        || (c == bestCost && (f > bestFit || (f == bestFit && string.CompareOrdinal(kv.Key, best) < 0)));
                    if (!better) continue;
                    best = kv.Key; bestCost = c; bestFit = f; bestAvailable = available;
                }
            if (best == null) { prefabId = null; return 0; }

            int consumed = System.Math.Min(System.Math.Max(1, desiredCount), bestAvailable);
            int stock;
            shipStock.TryGetValue(best, out stock);
            shipStock[best] = stock - consumed;
            prefabId = best;
            return consumed;
        }

        private static double ScoreFor(ActionKind kind, IPrefabCatalogue cat, string prefabId)
        {
            switch (kind)
            {
                case ActionKind.Trade:
                case ActionKind.Gift:
                case ActionKind.Haul:
                    return cat != null ? cat.CargoLiters(prefabId) : 0;
                case ActionKind.Mine:
                    // The ship's own cargo hold doesn't change how much a trip yields (that's the
                    // mined node's own formula) -- a better driller is the more valuable pick among
                    // eligible candidates.
                    return cat != null ? cat.DrillYieldScore(prefabId) : 0;
                default:
                    return TotalDps(cat, prefabId);
            }
        }

        private static double ScoreForRole(string role, IPrefabCatalogue cat, string prefabId)
        {
            if (role == "Trade") return cat != null ? cat.CargoLiters(prefabId) : 0;
            if (role == "Mine") return cat != null ? cat.DrillYieldScore(prefabId) : 0;
            return TotalDps(cat, prefabId);
        }

        private static double TotalDps(IPrefabCatalogue cat, string prefabId)
        {
            return cat != null ? TotalDps(cat.WeaponBands(prefabId)) : 0;
        }

        /// <summary>Sums <see cref="WeaponBand.Dps"/> across every band — public so callers holding
        /// raw scanned bands directly (e.g. <c>PrefabCatalogueScanner</c>, mid-scan, before an entry
        /// is wrapped in a queryable <see cref="IPrefabCatalogue"/>) don't need a catalogue lookup
        /// just to total them.</summary>
        public static double TotalDps(IReadOnlyList<WeaponBand> bands)
        {
            if (bands == null) return 0;
            double sum = 0;
            for (int i = 0; i < bands.Count; i++) sum += bands[i].Dps;
            return sum;
        }

        // Managers re-post each tick while still blocked, so this only needs to outlive the gap
        // between two consecutive ticks -- but Operations, Trade, Shipyard and Fleet can all post it,
        // on different cadences, so it takes the widest of them (FactionTuning.SharedPostingTtl).
        private static TimeSpan LocalShipyardInvestmentTtlFor(FactionTuning t)
        {
            return t == null ? TimeSpan.FromSeconds(120.0) : t.SharedPostingTtl(120.0);
        }

        /// <summary>A same-region dispatch leg (both ends share a body's <see cref="SpatialNode.PlanetId"/>,
        /// or both stand in one authored feature — legs between regions are out of scope here,
        /// Ion/Hydrogen already serve those)
        /// that found no route-feasible ship at the source is direct evidence the faction needs local
        /// Shipyard capacity where that leg runs — a Shipyard elsewhere cannot build the hull this
        /// leg needs. Posts a <see cref="NodeCapabilityKind.Shipyard"/> investment request tagged with
        /// the planet AND with the region of the body the source node stands in, so the ask names a
        /// place a node can actually occupy. Shared by every real dispatch call site that already
        /// builds a <see cref="RouteEnvironment"/> and finds it infeasible (Haul, Trade, Ferry,
        /// Patrol).
        ///
        /// <para><b>Region, not ground.</b> The trigger is PlanetId equality, which two nodes in a
        /// body's well satisfy without either standing on it; asking only whether a yard stands on
        /// that body's terrain then makes the request unsatisfiable on any body that forbids surface
        /// stations, and on any body a faction only reaches from orbit. <see cref="ShipyardCoverage"/>
        /// answers both halves so the ask and the install cannot drift apart.</para>
        ///
        /// <para><b>A covered region asks again only when its yards are saturated</b> — see
        /// <see cref="ShipyardCoverage.WantsAnotherYard"/>. Expressed as posting or not posting rather
        /// than as a reduced urgency, because <c>FactionBrain.AddDevelopCandidates</c> floors a
        /// manager request at quality 0.70, above the 0.65 exploratory cap: below that floor the
        /// urgency scale carries no information.</para>
        ///
        /// <para><b>A player faction asks only for what it opted into</b> —
        /// <see cref="FactionLifecycle.CanPostInvestmentFor"/>. The request's install BOM is a whole
        /// prefab yard, so an ungated one reserves and demands most of the component catalogue on
        /// behalf of a player who asked for something else entirely.</para></summary>
        public static void MaybePostLocalShipyardInvestment(WorldModel world, FactionState state,
            SpatialNode sourceNode, SpatialNode destNode, List<string> log, string context = "dispatch")
        {
            if (state == null || sourceNode == null || destNode == null) return;
            NodeRegion region = LocalRegionOf(sourceNode, destNode);
            if (region.IsNone) return;

            int yards, working;
            if (!ShipyardCoverage.WantsAnotherYard(world.Graph.OwnedBy(state.Tag), region, world.Tuning,
                    out yards, out working))
                return;

            string refusal;
            if (!MayAskForYard(state, sourceNode, destNode, region, yards, working, context, out refusal))
            {
                log.Add(refusal);
                return;
            }

            bool isNew = world.Investments.Post(region.Scope(new InvestmentRequest
            {
                FactionTag     = state.Tag,
                Cap            = NodeCapabilityKind.Shipyard,
                Urgency        = 0.8,
                ExpiresUtc     = world.UtcNow.Add(LocalShipyardInvestmentTtlFor(world.Tuning)),
            }));
            if (isNew) log.Add(RequestLine(state.Tag, sourceNode, destNode, region, yards, working, context, null));
        }

        /// <summary>Execution-context-based counterpart to <see cref="MaybePostLocalShipyardInvestment"/>,
        /// for a staged Operations cycle (Slice 3) — same trigger/dedup/opt-in rules, but posts through
        /// <paramref name="ctx"/> instead of directly into <see cref="WorldModel.Investments"/>, and
        /// reads owned-node capabilities via <paramref name="ctx"/> (always live — read-only metadata,
        /// never staged) instead of <c>world.Graph.OwnedBy</c>.</summary>
        public static void MaybePostLocalShipyardInvestment(IOperationsExecutionContext ctx, FactionTuning tuning, FactionState state,
            SpatialNode sourceNode, SpatialNode destNode, string context = "dispatch")
        {
            if (ctx == null || state == null || sourceNode == null || destNode == null) return;
            NodeRegion region = LocalRegionOf(sourceNode, destNode);
            if (region.IsNone) return;

            int yards, working;
            if (!ShipyardCoverage.WantsAnotherYard(ctx.OwnedNodes(state.Tag), region, tuning,
                    out yards, out working))
                return;

            string refusal;
            if (!MayAskForYard(state, sourceNode, destNode, region, yards, working, context, out refusal))
            {
                ctx.AddLog(refusal);
                return;
            }

            bool isNew = ctx.PostInvestment(region.Scope(new InvestmentRequest
            {
                FactionTag     = state.Tag,
                Cap            = NodeCapabilityKind.Shipyard,
                Urgency        = 0.8,
                ExpiresUtc     = ctx.UtcNow.Add(LocalShipyardInvestmentTtlFor(tuning)),
            }));
            if (isNew) ctx.AddLog(RequestLine(state.Tag, sourceNode, destNode, region, yards, working, context, null));
        }

        /// <summary>The <see cref="FactionLifecycle.CanPostInvestmentFor"/> gate for
        /// <see cref="NodeCapabilityKind.Shipyard"/>. A refusal is reported rather than returned
        /// silently, so a gated faction reads differently in the log from one whose legs simply never
        /// failed — <paramref name="refusalLine"/> is non-null exactly when this returns false.
        ///
        /// <para><b>Every refusal, not just the first.</b> The line carries <c>INVEST_REQUEST</c>, the
        /// keyword <c>FactionRuntime</c> filters this whole channel on, so <c>DebugTraces.Investment</c>
        /// is the volume control — off suppresses it, and an operator who turns it on is asking for
        /// the per-leg detail. The call also sits downstream of
        /// <see cref="ShipyardCoverage.WantsAnotherYard"/>, which already requires a founded in-region
        /// station under the level cap, the prerequisite installed somewhere, and every existing yard
        /// working. Deduping here instead would put per-process state in Core that outlives a world
        /// reload: a faction whose refusal had been reported once would then go quiet across it.</para>
        /// </summary>
        private static bool MayAskForYard(FactionState state, SpatialNode sourceNode, SpatialNode destNode,
            NodeRegion region, int yards, int working, string context, out string refusalLine)
        {
            if (FactionLifecycle.CanPostInvestmentFor(state, NodeCapabilityKind.Shipyard))
            {
                refusalLine = null;
                return true;
            }
            refusalLine = RequestLine(state.Tag, sourceNode, destNode, region, yards, working, context, "refused=opt-in");
            return false;
        }

        /// <summary>The region a leg is local to: the source node's own, when the destination shares
        /// it. A leg between two bodies, two features, or out into open space is nobody's local
        /// need — Ion/Hydrogen serve those — and reads as <see cref="NodeRegion.None"/>.
        /// <para>Same-body is judged by <see cref="SpatialNode.PlanetId"/> alone, as it always was:
        /// a leg from a body's ground to its well is still that body's, and it is the source's half
        /// that the yard has to stand in.</para></summary>
        private static NodeRegion LocalRegionOf(SpatialNode sourceNode, SpatialNode destNode)
        {
            NodeRegion region = ShipyardCoverage.RegionOf(sourceNode);
            if (region.IsNone) return region;
            if (region.IsBody)
                return sourceNode.PlanetId == destNode.PlanetId ? region : NodeRegion.None;
            return ShipyardCoverage.InRegion(destNode, region) ? region : NodeRegion.None;
        }

        /// <summary>The line carries the region it asked for and the yard census it decided on, so a
        /// request can be checked against its own reason rather than taken on trust. A null
        /// <paramref name="verdict"/> is the plain posted line; a non-null one names the gate that
        /// stopped the ask instead, so a reader can tell a refusal from never having been
        /// evaluated.</summary>
        private static string RequestLine(string factionTag, SpatialNode sourceNode, SpatialNode destNode,
            NodeRegion region, int yards, int working, string context, string verdict)
        {
            return factionTag + " INVEST_REQUEST Shipyard " + region.Describe()
                + " yards=" + working.ToString(CultureInfo.InvariantCulture)
                + "/" + yards.ToString(CultureInfo.InvariantCulture)
                + (verdict != null ? " " + verdict : "")
                + " (local " + context + " unservable " + sourceNode.Id + "->" + destNode.Id + ")";
        }
    }
}
