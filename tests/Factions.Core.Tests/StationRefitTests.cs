using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The Core half of a station refit: the net bill, the gate in front of it, and what a completed
    /// refit leaves behind.
    /// </summary>
    public class StationRefitTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);

        // ---- fixtures ---------------------------------------------------------------------------

        /// <summary>A catalogue that knows both kinds of prefab: station designs (with a site) and
        /// ordinary hulls. Ownership is sparse — an id with no owner is usable by anyone.</summary>
        private sealed class Catalogue : IPrefabCatalogue, IStationPrefabCatalogue, IOwnedPrefabCatalogue,
                                         IPrefabVersionCatalogue
        {
            private readonly Dictionary<string, KeyValuePair<string, int>> _versions
                = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal);

            /// <summary>Registers <paramref name="id"/> as version <paramref name="version"/> of
            /// <paramref name="family"/>.</summary>
            public Catalogue Version(string id, string family, int version)
            {
                _versions[id] = new KeyValuePair<string, int>(family, version);
                return this;
            }

            public string FamilyOf(string prefabId)
            {
                KeyValuePair<string, int> v;
                return prefabId != null && _versions.TryGetValue(prefabId, out v) ? v.Key : prefabId;
            }

            public int VersionOf(string prefabId)
            {
                KeyValuePair<string, int> v;
                return prefabId != null && _versions.TryGetValue(prefabId, out v) ? v.Value : 1;
            }

            public string LatestFor(string prefabId, string factionTag) { return prefabId; }

            private readonly Dictionary<string, string> _stationSite = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _owner = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly HashSet<string> _ships = new HashSet<string>(StringComparer.Ordinal);

            public Catalogue Station(string id, string site, string ownerTag = null)
            {
                _stationSite[id] = site;
                if (ownerTag != null) _owner[id] = ownerTag;
                return this;
            }

            public Catalogue Ship(string id) { _ships.Add(id); return this; }

            public bool IsStationPrefab(string prefabId)
            {
                return prefabId != null && _stationSite.ContainsKey(prefabId);
            }

            public string Site(string prefabId)
            {
                string s;
                return prefabId != null && _stationSite.TryGetValue(prefabId, out s) ? s : null;
            }

            public string OwnerFactionTag(string prefabId)
            {
                string t;
                return prefabId != null && _owner.TryGetValue(prefabId, out t) ? t : null;
            }

            public string GridSize(string prefabId)
            {
                if (prefabId == null) return "Unknown";
                if (_stationSite.ContainsKey(prefabId)) return "Station";
                return _ships.Contains(prefabId) ? "Large" : "Unknown";
            }

            public long CargoLiters(string prefabId) { return _ships.Contains(prefabId ?? "") ? 1000 : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        /// <summary>One <c>Prefab/&lt;id&gt;</c> recipe per registered prefab, with a real multi-key
        /// component bill so a net diff has something to cancel against. Subtypes go in bare; the
        /// inventory keys the BOM comes back under are the registry's own
        /// (<c>SteelPlate</c> -&gt; <c>Comp:SteelPlate</c>).</summary>
        private sealed class Recipes : IRecipeRegistry
        {
            private readonly Dictionary<string, Dictionary<string, long>> _byPrefab
                = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

            public Recipes Add(string prefabId, params object[] pairs)
            {
                var bom = new Dictionary<string, long>(StringComparer.Ordinal);
                for (int i = 0; i + 1 < pairs.Length; i += 2) bom[(string)pairs[i]] = Convert.ToInt64(pairs[i + 1]);
                _byPrefab[prefabId] = bom;
                return this;
            }

            public IEnumerable<ConversionRecipe> GetRecipesFor(string outputTypeId, string outputSubTypeId)
            {
                Dictionary<string, long> bom;
                if (outputTypeId != "Prefab" || outputSubTypeId == null
                    || !_byPrefab.TryGetValue(outputSubTypeId, out bom)) yield break;
                var inputs = new List<RecipeItem>();
                foreach (KeyValuePair<string, long> kv in bom)
                    inputs.Add(new RecipeItem
                    {
                        TypeId = "MyObjectBuilder_Component", SubTypeId = kv.Key, Amount = kv.Value,
                    });
                yield return new ConversionRecipe
                {
                    BlueprintId = outputSubTypeId, Inputs = inputs.ToArray(), Outputs = new RecipeItem[0],
                };
            }

            public IEnumerable<ConversionRecipe> AllRecipes { get { yield break; } }
            public int Count { get { return _byPrefab.Count; } }
        }

        /// <summary>A station rebuilt as a newer version of its own design hands back what the old
        /// version held beyond the new one; a rebuild to an unrelated design, or back to an older
        /// version, credits nothing — those two could be cycled into a component pump.</summary>
        [Fact]
        public void UpgradeCredit_OnlyForANewerVersionOfTheSameDesign()
        {
            var catalogue = new Catalogue()
                .Station("Hab", "Space").Station("Hab@2", "Space").Station("Other", "Space")
                .Version("Hab@2", "Hab", 2);
            var recipes = new Recipes()
                .Add("Hab", "SteelPlate", 100, "Gun", 10)
                .Add("Hab@2", "SteelPlate", 80, "Gun", 20)
                .Add("Other", "SteelPlate", 10);

            Assert.Equal(new Dictionary<string, long> { { "Comp:SteelPlate", 20 } },
                HullRefit.StationUpgradeCredit(catalogue, recipes, "Hab", "Hab@2"));
            Assert.Empty(HullRefit.StationUpgradeCredit(catalogue, recipes, "Hab", "Other"));
            Assert.Empty(HullRefit.StationUpgradeCredit(catalogue, recipes, "Hab@2", "Hab"));
            Assert.Empty(HullRefit.StationUpgradeCredit(catalogue, recipes, null, "Hab@2"));
        }

        /// <summary>A founded, owned, space-sited node standing as <c>Hab_Old</c>, in a world whose
        /// catalogue offers <c>Hab_New</c> (space) and <c>Outpost_New</c> (surface), plus a hull.
        /// <c>Hab_New</c> costs more steel and more girders; it also uses less interior plate, which
        /// the net bill must not turn into a credit.</summary>
        private static WorldModel Founded(out SpatialNode node, out Catalogue cat, string tag = "F")
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            cat = new Catalogue()
                .Station("Hab_Old", StationPrefabSite.Space)
                .Station("Hab_New", StationPrefabSite.Space)
                .Station("Outpost_New", StationPrefabSite.Surface)
                .Ship("Hauler");
            world.PrefabCatalogue = cat;
            world.Recipes = new Recipes()
                .Add("Hab_Old", "SteelPlate", 100, "InteriorPlate", 40)
                .Add("Hab_New", "SteelPlate", 250, "InteriorPlate", 10, "Girder", 30)
                .Add("Outpost_New", "SteelPlate", 120)
                .Add("Hauler", "SteelPlate", 50);

            node = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            node.OwnerFactionTag = tag;
            node.HoldStrength = 1.0;
            node.StableStationId = 7;
            node.StationPrefabId = "Hab_Old";
            node.Site = StationSite.Space;
            return world;
        }

        private static StationRefitResult Refit(WorldModel world, SpatialNode node, string prefabId,
            bool force, List<string> log, string tag = "F")
        {
            BuildQueueEntry queued;
            return ShipyardManager.QueueRefit(world, world.Tuning, tag, node.Id, prefabId, force,
                out queued, log);
        }

        private static StationRefitResult Gate(WorldModel world, string nodeId, string prefabId,
            bool force = false, string tag = "F")
        {
            SpatialNode n;
            Dictionary<string, long> bom;
            BuildQueueEntry blocking;
            return RefitStationValidator.Validate(world, tag, nodeId, prefabId, force, out n, out bom,
                out blocking);
        }

        // ---- the net bill -----------------------------------------------------------------------

        /// <summary>Per component, what the target wants beyond what stands here — floored at zero,
        /// and blind to anything only the current design uses. A key the target does not name is not
        /// a refund and must not appear at all.</summary>
        [Fact]
        public void NetRefitBom_FloorsAtZero_AndDropsKeysTheTargetDoesNotUse()
        {
            var current = new Dictionary<string, long>
            {
                { "Comp:SteelPlate", 100 },
                { "Comp:InteriorPlate", 40 },
                { "Comp:Motor", 12 },         // the new design has no motors at all
            };
            var target = new Dictionary<string, long>
            {
                { "Comp:SteelPlate", 250 },   // 150 more
                { "Comp:InteriorPlate", 10 }, // fewer -- not a credit, and not a line
                { "Comp:Girder", 30 },        // wholly new
            };

            Dictionary<string, long> net = ShipyardManager.NetRefitBom(current, target);

            Assert.Equal(150L, net["Comp:SteelPlate"]);
            Assert.Equal(30L, net["Comp:Girder"]);
            Assert.False(net.ContainsKey("Comp:InteriorPlate"));
            Assert.False(net.ContainsKey("Comp:Motor"));
            Assert.Equal(2, net.Count);
        }

        /// <summary>Nothing standing here yet: the bill is the target's own, key for key.</summary>
        [Fact]
        public void NetRefitBom_WithNoCurrentDesign_IsTheTargetsGrossBill()
        {
            var target = new Dictionary<string, long> { { "Comp:SteelPlate", 250 }, { "Comp:Girder", 30 } };

            Dictionary<string, long> fromNull = ShipyardManager.NetRefitBom(null, target);
            Dictionary<string, long> fromEmpty = ShipyardManager.NetRefitBom(
                new Dictionary<string, long>(), target);

            Assert.Equal(250L, fromNull["Comp:SteelPlate"]);
            Assert.Equal(30L, fromNull["Comp:Girder"]);
            Assert.Equal(fromNull.Count, fromEmpty.Count);
            Assert.Equal(fromNull["Comp:SteelPlate"], fromEmpty["Comp:SteelPlate"]);
        }

        // ---- the gate ---------------------------------------------------------------------------

        [Fact]
        public void Refit_IsRefused_ForEveryGate_AndPassesTheHappyPath()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);

            // The happy path first, so every refusal below is a refusal of something that otherwise works.
            Assert.Equal(StationRefitResult.Ok, Gate(world, "N1", "Hab_New"));

            Assert.Equal(StationRefitResult.NodeNotFound, Gate(world, "N404", "Hab_New"));
            Assert.Equal(StationRefitResult.NotOwned, Gate(world, "N1", "Hab_New", false, "OTHER"));
            Assert.Equal(StationRefitResult.UnknownPrefab, Gate(world, "N1", "Hab_Nonexistent"));
            Assert.Equal(StationRefitResult.NotStationPrefab, Gate(world, "N1", "Hauler"));
            Assert.Equal(StationRefitResult.SiteMismatch, Gate(world, "N1", "Outpost_New"));
            Assert.Equal(StationRefitResult.AlreadyThisPrefab, Gate(world, "N1", "Hab_Old"));

            long founded = node.StableStationId;
            node.StableStationId = 0;
            Assert.Equal(StationRefitResult.NotFounded, Gate(world, "N1", "Hab_New"));
            node.StableStationId = founded;

            node.SiegeStartedUtc = world.UtcNow;
            Assert.Equal(StationRefitResult.SiegeInProgress, Gate(world, "N1", "Hab_New"));
            node.SiegeStartedUtc = default(DateTime);

            // A capture another faction owns is refused however well it fits the node.
            cat.Station("Hab_Theirs", StationPrefabSite.Space, "RIVAL");
            Assert.Equal(StationRefitResult.PrefabNotUsable, Gate(world, "N1", "Hab_Theirs"));

            Assert.Equal(StationRefitResult.Ok, Gate(world, "N1", "Hab_New"));
        }

        /// <summary>A design with no <c>Prefab/&lt;id&gt;</c> recipe has no bill, and a refit to it
        /// would be free. Refused — distinctly from an unknown prefab, because the design is real and
        /// the missing thing is its blueprint.</summary>
        [Fact]
        public void Refit_IsRefused_WhenTheTargetDesignHasNoRecipe()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            cat.Station("Hab_Unpriced", StationPrefabSite.Space);

            Assert.Equal(StationRefitResult.NoRecipe, Gate(world, "N1", "Hab_Unpriced"));
        }

        /// <summary>A ship build mid-collection owns the working slot. The refit is refused until the
        /// caller says so, and the ship build keeps every component it had absorbed.</summary>
        [Fact]
        public void Refit_NeedsForce_WhenAShipBuildIsAlreadyAbsorbing()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);

            var shipBuild = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "Hauler",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
                Priority = ShipyardManager.RolePriority("Trade", world.Tuning),
            };
            shipBuild.Bom["Comp:SteelPlate"] = 50;
            node.BuildQueue.Add(shipBuild);

            // Holding nothing yet: there is nothing to displace, so no force is needed.
            Assert.Equal(StationRefitResult.Ok, Gate(world, "N1", "Hab_New"));

            shipBuild.Collected["Comp:SteelPlate"] = 20;
            Assert.Equal(StationRefitResult.BuildInProgress, Gate(world, "N1", "Hab_New"));
            Assert.Equal(StationRefitResult.Ok, Gate(world, "N1", "Hab_New", true));

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_New", true, log));
            Assert.Contains(node.BuildQueue, e => ShipyardManager.IsStationRefit(e));
            Assert.Contains(node.BuildQueue, e => e.Role == "Trade");
            Assert.Equal(20L, shipBuild.Collected["Comp:SteelPlate"]);
        }

        /// <summary>The refit outranks the fleet roles it shares a yard with, so the slot it was
        /// given force to take is actually the one it gets.</summary>
        [Fact]
        public void Refit_OutranksAFleetBuild_ForTheWorkingSlot()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);

            var shipBuild = new BuildQueueEntry
            {
                Id = "Trade:001", Role = "Trade", PrefabId = "Hauler",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
                Priority = ShipyardManager.RolePriority("Trade", world.Tuning),
            };
            shipBuild.Bom["Comp:SteelPlate"] = 50;
            shipBuild.Collected["Comp:SteelPlate"] = 20;
            node.BuildQueue.Add(shipBuild);

            BuildQueueEntry refit;
            Assert.Equal(StationRefitResult.Ok, ShipyardManager.QueueRefit(world, world.Tuning, "F",
                node.Id, "Hab_New", true, out refit, new List<string>()));

            Assert.True(refit.Priority > shipBuild.Priority,
                "a refit queued behind a fleet build only takes the slot if it outranks it");
            Assert.True(node.IsActiveBuild(refit));
            Assert.False(node.IsActiveBuild(shipBuild));
        }

        /// <summary>The contest that actually decides the rank: <c>Mine</c> is the highest row in
        /// <c>BuildRolePriority</c> and <c>OrderedBuildQueue</c> breaks a tie by creation order, so a
        /// refit merely level with it would sit behind a Mine build queued first — while the
        /// <c>force</c> refusal promises the opposite. Same shape as the Trade case above, against
        /// the one entry that can tie.</summary>
        [Fact]
        public void Refit_OutranksAnAbsorbingMineBuild_ForTheWorkingSlot()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);

            var mineBuild = new BuildQueueEntry
            {
                Id = "Mine:001", Role = "Mine", PrefabId = "Driller",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
                Priority = ShipyardManager.RolePriority("Mine", world.Tuning),
            };
            mineBuild.Bom["Comp:SteelPlate"] = 50;
            mineBuild.Collected["Comp:SteelPlate"] = 20;
            node.BuildQueue.Add(mineBuild);
            Assert.Equal(world.Tuning.HighestBuildRolePriority(), mineBuild.Priority);

            BuildQueueEntry refit;
            Assert.Equal(StationRefitResult.Ok, ShipyardManager.QueueRefit(world, world.Tuning, "F",
                node.Id, "Hab_New", true, out refit, new List<string>()));

            Assert.True(refit.Priority > mineBuild.Priority,
                "a refit queued behind the highest-ranked fleet build only takes the slot if it outranks it");
            Assert.True(node.IsActiveBuild(refit));
            Assert.False(node.IsActiveBuild(mineBuild));
        }

        /// <summary>A second refit is refused rather than silently restarting a half-collected one;
        /// with force it replaces it and everything the first had absorbed comes back.</summary>
        [Fact]
        public void Refit_Override_NeedsForce_AndRefundsThePreviousRefitsCollected()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            cat.Station("Hab_Third", StationPrefabSite.Space);
            ((Recipes)world.Recipes).Add("Hab_Third", "SteelPlate", 300);

            var log = new List<string>();
            BuildQueueEntry first;
            Assert.Equal(StationRefitResult.Ok, ShipyardManager.QueueRefit(world, world.Tuning, "F",
                node.Id, "Hab_New", false, out first, log));
            first.Collected["Comp:SteelPlate"] = 90;
            first.Collected["Comp:Girder"] = 5;

            Assert.Equal(StationRefitResult.RefitInProgress, Gate(world, "N1", "Hab_Third"));

            BuildQueueEntry second;
            Assert.Equal(StationRefitResult.Ok, ShipyardManager.QueueRefit(world, world.Tuning, "F",
                node.Id, "Hab_Third", true, out second, log));

            Assert.Equal(90L, node.Inventory["Comp:SteelPlate"]);
            Assert.Equal(5L, node.Inventory["Comp:Girder"]);
            Assert.Single(node.BuildQueue);
            Assert.Equal("Hab_Third", node.BuildQueue[0].PrefabId);
        }

        // ---- servicing --------------------------------------------------------------------------

        /// <summary>No Shipyard capability anywhere: a founded owned node is enough, and the ordinary
        /// sweep absorbs from its inventory and posts the shortfall as a ShipBuild-class
        /// <c>Item:</c> demand.</summary>
        [Fact]
        public void Refit_AtANodeWithNoShipyard_IsServicedByTick()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            Assert.False(node.Capabilities.ContainsKey(NodeCapabilityKind.Shipyard),
                "the point of this test is a node that could never build a hull");

            node.Inventory["Comp:SteelPlate"] = 60;

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_New", false, log));
            Assert.Contains(log, l => l.Contains("STATION_REFIT_QUEUED") && l.Contains("@N1")
                                      && l.Contains("Hab_Old -> Hab_New") && l.Contains("bom=180"));

            new ShipyardManager().Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                world, world.Tuning, log);

            BuildQueueEntry entry = node.BuildQueue[0];
            Assert.Equal(60L, entry.Collected["Comp:SteelPlate"]);
            long left;
            node.Inventory.TryGetValue("Comp:SteelPlate", out left);
            Assert.Equal(0L, left);

            List<DemandEntry> items = world.Demand.SnapshotItemDemands("F");
            Assert.Contains(items, d => d.Kind == "Item:Comp:SteelPlate" && d.NodeId == "N1"
                                        && d.Quantity == 90 && d.Class == DemandClass.ShipBuild);
            Assert.Contains(items, d => d.Kind == "Item:Comp:Girder" && d.NodeId == "N1"
                                        && d.Quantity == 30);
            Assert.False(entry.Bom.ContainsKey("Comp:InteriorPlate"));
        }

        /// <summary>Completion spends the components on the node itself: a pending design to stand
        /// up, an event, and no hull anywhere.</summary>
        [Fact]
        public void Refit_Completion_SetsPendingPrefab_RaisesTheEvent_AndCreditsNoShip()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            node.Inventory["Comp:SteelPlate"] = 150;
            node.Inventory["Comp:Girder"] = 30;

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_New", false, log));

            new ShipyardManager().Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                world, world.Tuning, log);

            Assert.Empty(node.BuildQueue);
            Assert.Equal("Hab_New", node.PendingStationPrefabId);
            // The node is NOT rebuilt here -- that is the host's, on the game thread.
            Assert.Equal("Hab_Old", node.StationPrefabId);
            Assert.Empty(node.ShipStock);
            Assert.Contains(log, l => l.Contains("STATION_REFIT_READY") && l.Contains("@N1")
                                      && l.Contains("prefab=Hab_New"));
            Assert.DoesNotContain(log, l => l.Contains("SHIP_BUILT"));
            Assert.Contains(world.PendingEvents, e => e.Kind == FactionEventKind.StationRefitReady
                                                     && e.ActorTag == "F" && e.NodeId == "N1");
        }

        /// <summary>Nothing to collect — the target asks for nothing the node does not already stand
        /// as — so the refit is paid for the moment it is asked for, rather than waiting for a sweep
        /// to notice an empty bill.</summary>
        [Fact]
        public void Refit_WithAnEmptyNetBom_CompletesImmediately()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            cat.Station("Hab_Lean", StationPrefabSite.Space);
            // Strictly inside Hab_Old's bill, so max(0, target - current) is empty for every key.
            ((Recipes)world.Recipes).Add("Hab_Lean", "SteelPlate", 40, "InteriorPlate", 40);

            var log = new List<string>();
            BuildQueueEntry queued;
            Assert.Equal(StationRefitResult.Ok, ShipyardManager.QueueRefit(world, world.Tuning, "F",
                node.Id, "Hab_Lean", false, out queued, log));

            Assert.Empty(queued.Bom);
            Assert.Empty(node.BuildQueue);
            Assert.Equal("Hab_Lean", node.PendingStationPrefabId);
            Assert.Contains(log, l => l.Contains("STATION_REFIT_READY") && l.Contains("prefab=Hab_Lean"));
        }

        /// <summary>A yard rebuilding its own station is not a yard turning components into hulls:
        /// counting it would have the faction ask for a second yard beside one building no ships.
        /// The paired fleet build proves the census can still see real work at the same node.</summary>
        [Fact]
        public void Refit_DoesNotCountAsAWorkingYard()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            BuildQueueEntry refit;
            Assert.Equal(StationRefitResult.Ok, ShipyardManager.QueueRefit(world, world.Tuning, "F",
                node.Id, "Hab_New", false, out refit, new List<string>()));
            Assert.True(node.IsActiveBuild(refit), "the refit has to hold the slot for the census to see it at all");

            Assert.False(ShipyardCoverage.IsWorking(node, world.Tuning));

            var hull = new BuildQueueEntry
            {
                Id = "Trade:002", Role = "Trade", PrefabId = "Hauler",
                CreatedUtc = world.UtcNow, ExpiresUtc = world.UtcNow.AddHours(24),
                Priority = refit.Priority + 1,   // outranks the refit, so it holds the working slot
            };
            hull.Bom["Comp:SteelPlate"] = 50;
            node.BuildQueue.Add(hull);

            Assert.True(node.IsActiveBuild(hull));
            Assert.True(ShipyardCoverage.IsWorking(node, world.Tuning));
        }

        /// <summary>The refit role is the player's alone: the hull-building verb refuses it, so
        /// nothing that walks <see cref="ShipyardManager.PlayerQueueRoles"/> can produce one.</summary>
        [Fact]
        public void QueueBuild_RefusesTheRefitRole()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            node.Capabilities[NodeCapabilityKind.Shipyard] = 1;

            Dictionary<string, long> bom;
            PlayerQueueResult result = new ShipyardManager().QueueBuild(world, world.Tuning, "F", node.Id,
                ShipyardManager.StationRefitRole, "Hab_New", out bom, new List<string>());

            Assert.Equal(PlayerQueueResult.UnknownRole, result);
            Assert.DoesNotContain(ShipyardManager.StationRefitRole, ShipyardManager.PlayerQueueRoles);
        }

        // ---- persistence ------------------------------------------------------------------------

        /// <summary>The obligation is written the moment the components are spent, so a restart
        /// between paying for a design and standing it up never strands one. A node owing nothing
        /// writes no field at all — the brain files are loaded one at a time and every node carries
        /// this one.</summary>
        [Fact]
        public void PendingStationPrefabId_RoundTrips_AndIsOmittedWhenNothingIsOwed()
        {
            var graph = new SpatialGraph();
            SpatialNode owing = graph.Add(new Vec3(0, 0, 0), 1.0, "N1");
            owing.OwnerFactionTag = "F";
            owing.StableStationId = 7;
            owing.StationPrefabId = "Hab_Old";
            owing.PendingStationPrefabId = "Hab_New";
            SpatialNode settled = graph.Add(new Vec3(50, 0, 0), 1.0, "N2");
            settled.OwnerFactionTag = "F";
            settled.StableStationId = 8;
            settled.StationPrefabId = "Hab_Old";

            var st = new FactionState { Tag = "F" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("F"), FactionStore.DefaultLogCap));

                var reloaded = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("F"), reloaded);

                Assert.Equal("Hab_New", reloaded.Nodes["N1"].PendingStationPrefabId);
                Assert.Null(reloaded.Nodes["N2"].PendingStationPrefabId);

                string json = File.ReadAllText(Path.Combine(dir, "brains", "F.json"));
                Assert.Contains("PendingStationPrefabId", json);
                Assert.Equal(1, CountOccurrences(json, "PendingStationPrefabId"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ---- a node that changes hands -----------------------------------------------------------

        /// <summary>A rebuild aimed at a design the conqueror may not build does not travel with the
        /// node: the entry goes and its components land in the inventory the new owner inherits. The
        /// unrestricted refit beside it is the control — a capture that drops every refit would pass
        /// the first half of this on its own.</summary>
        [Fact]
        public void Conquest_DropsARefitRestrictedToTheOldOwner_AndRefundsIt()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            cat.Station("Hab_Priv", StationPrefabSite.Space, "F");
            ((Recipes)world.Recipes).Add("Hab_Priv", "SteelPlate", 300);
            // Short of the 200-plate net bill, so the sweep absorbs without completing and there is
            // a half-collected entry for the capture to refund.
            node.Inventory["Comp:SteelPlate"] = 60;

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_Priv", false, log));
            // Let the sweep absorb what the node is holding, so there is a real refund to make.
            new ShipyardManager().Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                world, world.Tuning, log);
            Assert.Single(node.BuildQueue);
            long collected = 0;
            foreach (KeyValuePair<string, long> kv in node.BuildQueue[0].Collected) collected += kv.Value;
            Assert.True(collected > 0, "the refit absorbed nothing, so the refund proves nothing");

            node.HoldStrength = 0.0;
            Assert.Equal("F", world.Conquer("E", node.Id, 1.0));

            Assert.Equal("E", node.OwnerFactionTag);
            Assert.Empty(node.BuildQueue);
            Assert.Equal(60L, node.Inventory["Comp:SteelPlate"]);
        }

        /// <summary>The control for the case above: a design nobody has reserved is buildable by
        /// whoever holds the node, so the rebuild carries on under its new owner.</summary>
        [Fact]
        public void Conquest_KeepsARefitAnyoneMayBuild()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            node.Inventory["Comp:SteelPlate"] = 50;

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_New", false, log));

            node.HoldStrength = 0.0;
            Assert.Equal("F", world.Conquer("E", node.Id, 1.0));

            Assert.Equal("E", node.OwnerFactionTag);
            Assert.Single(node.BuildQueue);
            Assert.Equal("Hab_New", node.BuildQueue[0].PrefabId);
        }

        /// <summary>A refit that is already paid for is the same carry-over in a different field: the
        /// host would otherwise stand a rival's private design up for the conqueror.</summary>
        [Fact]
        public void Conquest_ClearsAPendingRebuildRestrictedToTheOldOwner()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            cat.Station("Hab_Priv", StationPrefabSite.Space, "F");
            node.PendingStationPrefabId = "Hab_Priv";

            node.HoldStrength = 0.0;
            Assert.Equal("F", world.Conquer("E", node.Id, 1.0));

            Assert.Null(node.PendingStationPrefabId);
        }

        /// <summary>...and one anyone may build survives, so the conqueror inherits the rebuild the
        /// node was already owed.</summary>
        [Fact]
        public void Conquest_KeepsAPendingRebuildAnyoneMayBuild()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            node.PendingStationPrefabId = "Hab_New";

            node.HoldStrength = 0.0;
            Assert.Equal("F", world.Conquer("E", node.Id, 1.0));

            Assert.Equal("Hab_New", node.PendingStationPrefabId);
        }

        // ---- a rebuild that is built and waiting -------------------------------------------------

        /// <summary>A finished rebuild is held until the station empties, and the node still stands
        /// as the old design while it waits. Asking for the held design again would price it against
        /// that old design and charge the whole net bill a second time, so it is refused by name.</summary>
        [Fact]
        public void Refit_ToTheDesignAlreadyBuiltAndWaiting_IsRefusedRatherThanBilledTwice()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            node.PendingStationPrefabId = "Hab_New";

            Assert.Equal(StationRefitResult.AlreadyPending, Gate(world, "N1", "Hab_New"));
            // Force is an override of a QUEUED refit, not a licence to buy a built one twice.
            Assert.Equal(StationRefitResult.AlreadyPending, Gate(world, "N1", "Hab_New", force: true));

            var log = new List<string>();
            Assert.Equal(StationRefitResult.AlreadyPending, Refit(world, node, "Hab_New", false, log));
            Assert.Empty(node.BuildQueue);
        }

        /// <summary>A THIRD design over a held rebuild is allowed — and billed from what the node
        /// physically stands as, not from the held design, which was never built in world. Hab_Third
        /// costs 300 plate; standing as Hab_Old (100) that is a 200 bill, where pricing it against
        /// the held Hab_New (250) would make it 50.</summary>
        [Fact]
        public void Refit_OverAHeldRebuild_IsBilledFromTheDesignStandingNow()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            cat.Station("Hab_Third", StationPrefabSite.Space);
            ((Recipes)world.Recipes).Add("Hab_Third", "SteelPlate", 300);
            node.PendingStationPrefabId = "Hab_New";

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_Third", false, log));
            Assert.Single(node.BuildQueue);
            Assert.Equal(200L, node.BuildQueue[0].Bom["Comp:SteelPlate"]);
        }

        /// <summary>The claim path drops an unusable refit exactly as conquest does. Both call sites
        /// matter: a node can change hands without a fight, and a refit left behind would have the
        /// new owner finish, and the host then stand up, a rival's private design.</summary>
        [Fact]
        public void Claim_DropsARefitRestrictedToTheOldOwner_AndRefundsIt()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);
            cat.Station("Hab_Priv", StationPrefabSite.Space, "F");
            ((Recipes)world.Recipes).Add("Hab_Priv", "SteelPlate", 300);
            node.Inventory["Comp:SteelPlate"] = 60;

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_Priv", false, log));
            new ShipyardManager().Tick(new List<FactionState> { new FactionState { Tag = "F" } },
                world, world.Tuning, log);
            long collected = 0;
            foreach (KeyValuePair<string, long> kv in node.BuildQueue[0].Collected) collected += kv.Value;
            Assert.True(collected > 0, "the refit absorbed nothing, so the refund proves nothing");

            // Claim only takes a node nobody holds, which is the state a decayed node lands in.
            node.OwnerFactionTag = NeutralFaction.Tag;
            world.Claim("E", node.Id);

            Assert.Equal("E", node.OwnerFactionTag);
            Assert.Empty(node.BuildQueue);
            Assert.Equal(60L, node.Inventory["Comp:SteelPlate"]);
        }

        /// <summary>The control: a design anyone may build survives a claim, so a change of hands is
        /// not a way to cancel every rebuild a node was owed.</summary>
        [Fact]
        public void Claim_KeepsARefitAnyoneMayBuild()
        {
            SpatialNode node;
            Catalogue cat;
            WorldModel world = Founded(out node, out cat);

            var log = new List<string>();
            Assert.Equal(StationRefitResult.Ok, Refit(world, node, "Hab_New", false, log));

            node.OwnerFactionTag = NeutralFaction.Tag;
            world.Claim("E", node.Id);

            Assert.Equal("E", node.OwnerFactionTag);
            Assert.Single(node.BuildQueue);
            Assert.Equal("Hab_New", node.BuildQueue[0].PrefabId);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }
    }
}
