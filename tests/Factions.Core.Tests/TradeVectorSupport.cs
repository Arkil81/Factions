using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions.Core.Tests
{
    /// <summary>Shared fixture for the Trade-as-a-vector tests. Trade now reads the same signal Haul
    /// does — a partner's unserved <c>Item:</c> demand rows — so every fixture here is built the same
    /// way: nodes with stock and hulls, a per-(node, kind) scarcity oracle (consent turns entirely on
    /// the two sides disagreeing, which one number per kind cannot express), and rows written
    /// straight onto <see cref="FactionState.UnservedDemands"/>, which is where
    /// <c>OperationsManager</c> publishes them.</summary>
    internal static class TradeTest
    {
        /// <summary>Cargo-suitable by default (CargoLiters > 0), which is what <c>ShipPool</c>
        /// requires before it will hand a hull to a Trade or Haul convoy.</summary>
        internal sealed class Prefabs : IPrefabCatalogue
        {
            public readonly Dictionary<string, long> CargoLitersByPrefab = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, string[]> NamesByRole = new Dictionary<string, string[]>(StringComparer.Ordinal);

            public long CargoLiters(string prefabId)
            {
                long v;
                return prefabId != null && CargoLitersByPrefab.TryGetValue(prefabId, out v) ? v : 1_000_000;
            }
            /// <summary>Per-prefab gas tanks; unset prefabs have none.</summary>
            public readonly Dictionary<string, Dictionary<string, long>> GasTanksByPrefab
                = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId)
            {
                Dictionary<string, long> tanks;
                return prefabId != null && GasTanksByPrefab.TryGetValue(prefabId, out tanks)
                    ? tanks : new Dictionary<string, long>();
            }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint)
            {
                string[] names;
                return role != null && NamesByRole.TryGetValue(role, out names) ? names : new string[0];
            }
            /// <summary>Per-prefab dry mass and thrust; an unset prefab is unscanned, so no lift
            /// ceiling applies to it.</summary>
            public readonly Dictionary<string, double> DryMassKgByPrefab = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>> ThrustBandsByPrefab
                = new Dictionary<string, Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>>(StringComparer.Ordinal);

            /// <summary>Gives <paramref name="prefabId"/> a dry mass and one hydrogen lift band, so a
            /// loaded leg in gravity has a cargo mass ceiling.</summary>
            public void SetHydrogenDown(string prefabId, double dryMassKg, float forceN)
            {
                DryMassKgByPrefab[prefabId] = dryMassKg;
                ThrustBandsByPrefab[prefabId] = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Hydrogen", ForceMagnitudeN = forceN,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
                        EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                        NeedsAtmosphereForInfluence = false,
                    }},
                };
            }

            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>> bands;
                return prefabId != null && ThrustBandsByPrefab.TryGetValue(prefabId, out bands)
                    ? bands : new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId)
            {
                double v;
                return prefabId != null && DryMassKgByPrefab.TryGetValue(prefabId, out v) ? v : 0;
            }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        /// <summary>Scarcity keyed by (node, kind) rather than by kind alone. Consent turns entirely
        /// on the two sides disagreeing — the buyer scarce in something the seller is glutted in — so
        /// an oracle that returns one number per kind cannot express the situation under test.</summary>
        internal sealed class NodeScarcityOracle : IPriceOracle
        {
            public bool TryGetMeasuredTargetStock(string nodeId, string inventoryKey, out long target) { target = 0; return false; }
            private static readonly Dictionary<ItemKind, string[]> KindSubTypes =
                new Dictionary<ItemKind, string[]>
                {
                    { ItemKind.Ore,           new[] { "Iron", "Nickel", "Cobalt", "Silicon", "Magnesium", "Stone", "Ice" } },
                    { ItemKind.ProcessedGood, new[] { "SteelPlate", "Construction", "Computer", "MetalGrid" } },
                };

            private readonly Dictionary<string, double> _byNodeKind = new Dictionary<string, double>(StringComparer.Ordinal);
            private readonly Dictionary<string, double> _byNodeKey  = new Dictionary<string, double>(StringComparer.Ordinal);
            private readonly double _default;

            public NodeScarcityOracle(double defaultScarcity = 1.0) { _default = defaultScarcity; }

            /// <summary>Set scarcity for a whole kind at a node. The value is fanned out to that
            /// kind's subtypes as well: every production consumer reads scarcity per inventory key,
            /// so a fixture that recorded only the kind would configure a number nothing asks for
            /// and leave its test asserting against the fake's default.</summary>
            public NodeScarcityOracle Set(string nodeId, ItemKind kind, double scarcity)
            {
                _byNodeKind[nodeId + "|" + kind] = scarcity;
                string[] subTypes;
                if (KindSubTypes.TryGetValue(kind, out subTypes))
                    foreach (string sub in subTypes) _byNodeKey[nodeId + "|" + sub] = scarcity;
                return this;
            }

            /// <summary>Set scarcity for one concrete inventory key at a node. Consent and
            /// settlement both read the key, never the kind.</summary>
            public NodeScarcityOracle Set(string nodeId, string inventoryKey, double scarcity)
            {
                _byNodeKey[nodeId + "|" + inventoryKey] = scarcity;
                return this;
            }

            private readonly Dictionary<string, long> _referenceByNodeKey = new Dictionary<string, long>(StringComparer.Ordinal);

            /// <summary>A real reference stock for one key at a node, where a test sizes against the
            /// consent floor (<c>reference / ProcurementSellerScarcityCeiling</c>). The scarcity is
            /// still whatever <see cref="Set(string, string, double)"/> says; a fixture keeps the two
            /// consistent by setting <c>reference / stock</c> there.</summary>
            public NodeScarcityOracle SetReference(string nodeId, string inventoryKey, long reference)
            {
                _referenceByNodeKey[nodeId + "|" + inventoryKey] = reference;
                return this;
            }

            public double ScarcityMultiplier(string nodeId, string inventoryKey)
            {
                double v;
                return _byNodeKey.TryGetValue(nodeId + "|" + inventoryKey, out v) ? v : _default;
            }
            public bool TryGetReferenceStock(string nodeId, ItemKind kind, out long reference) { reference = 0; return false; }
            /// <summary>A scarcity other than a flat 1.0 exists only where the node has a reference
            /// stock for the key -- the real oracle derives one from the other -- so the fake reports a
            /// reference wherever it was given an opinion, and none where it answers its neutral 1.0.</summary>
            public bool TryGetReferenceStock(string nodeId, string inventoryKey, out long reference)
            {
                if (_referenceByNodeKey.TryGetValue(nodeId + "|" + inventoryKey, out reference)) return reference > 0;
                bool opinion = _byNodeKey.ContainsKey(nodeId + "|" + inventoryKey) || _default != 1.0;
                reference = opinion ? 1 : 0;
                return opinion;
            }
        }

        internal sealed class FlatPrices : IItemPriceCatalogue
        {
            private readonly long _price;
            public FlatPrices(long price) { _price = price; }
            public long BasePrice(string inventoryKey) { return _price; }
        }

        internal sealed class Volumes : IItemVolumeCatalogue
        {
            private readonly Dictionary<string, double> _litersByKey = new Dictionary<string, double>(StringComparer.Ordinal);
            private readonly double _default;
            public Volumes(double defaultLitersPerUnit = 0) { _default = defaultLitersPerUnit; }
            public Volumes Set(string invKey, double litersPerUnit) { _litersByKey[invKey] = litersPerUnit; return this; }
            public double VolumeLitersPerUnit(string invKey)
            {
                double v;
                return _litersByKey.TryGetValue(invKey, out v) ? v : _default;
            }
        }

        internal sealed class Masses : IItemMassCatalogue
        {
            private readonly Dictionary<string, double> _kgByKey = new Dictionary<string, double>(StringComparer.Ordinal);
            public Masses Set(string invKey, double kgPerUnit) { _kgByKey[invKey] = kgPerUnit; return this; }
            public double MassKgPerUnit(string invKey)
            {
                double v;
                return _kgByKey.TryGetValue(invKey, out v) ? v : 0;
            }
        }

        /// <summary>The same gravity everywhere and no planet bodies: every leg has a lift ceiling and
        /// nothing to route around.</summary>
        internal sealed class ConstantGravity : IPlanetCatalogue
        {
            private readonly double _g;
            public ConstantGravity(double g) { _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return _g; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        internal static WorldModel World()
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new Prefabs();
            return world;
        }

        internal static SpatialNode Node(WorldModel world, string id, string owner, double x = 0, double y = 0, double z = 0)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, y, z), 0.8, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.9;
            return n;
        }

        internal static FactionState State(string tag)
        {
            return new FactionState { Tag = tag, RngSeed = 1 };
        }

        /// <summary>Whitelists <paramref name="partnerTag"/> for <paramref name="st"/> — the one job
        /// <c>FactionBrain.ComputeTradePriority</c> has left.</summary>
        internal static void Whitelist(FactionState st, string partnerTag, double score = 1.0)
        {
            st.TradePriority.Add(new TradePriorityEntry { PartnerTag = partnerTag, Score = score });
        }

        /// <summary>Publishes a row exactly as <c>OperationsManager</c>'s matcher would: a demand its
        /// owner found no internal source for at all.</summary>
        internal static void Unserved(FactionState st, string nodeId, string itemKey, long remaining,
            double urgency = 1.0, DemandClass demandClass = DemandClass.RecipeInput)
        {
            st.UnservedDemands.Add(new UnservedDemand
            {
                NodeId = nodeId,
                ItemKey = itemKey,
                Remaining = remaining,
                Class = demandClass,
                Urgency = urgency,
            });
        }

        internal static TradeManager Manager(IPriceOracle oracle, IItemVolumeCatalogue volumes = null,
            IItemMassCatalogue masses = null)
        {
            return new TradeManager(oracle, volumes, masses);
        }

        /// <summary>Advances a day and resolves whatever has arrived. A day is well past any leg's
        /// ETA at the distances these fixtures use, so each call retires exactly one leg.</summary>
        internal static void ResolveOneLeg(IList<FactionState> states, WorldModel world, FactionTuning tuning,
            List<string> log, NodeScarcityOracle oracle, TradeManager manager, long unitPrice = 10)
        {
            world.UtcNow = world.UtcNow.AddDays(1);
            PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1), log,
                prices: new FlatPrices(unitPrice), oracle: oracle,
                tradeInboundBudget: manager != null ? manager.InboundBudget : null);
        }
    }
}
