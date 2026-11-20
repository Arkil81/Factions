// Explicit, because this file is also source-included by Factions.Core.Tests, which
// disables nullable reference types project-wide.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Factions.Core;
using Newtonsoft.Json.Linq;

namespace Factions.Api
{
    public sealed class SnapshotService : IDisposable
    {
        private readonly FactionStore _store;
        private readonly string _brainsDir;
        private WorldSnapshotDto? _cached;
        private DateTime _cacheTime;
        private readonly object _lock = new();
        private FileSystemWatcher? _watcher;

        // Per-SubTypeId market prices from item_prices.json
        private Dictionary<string, long>? _itemPrices;
        // Per-kind fallback prices from tuning.json
        private long _fallbackOre = 1, _fallbackIngot = 2, _fallbackComp = 10,
                     _fallbackH2 = 20, _fallbackO2 = 15, _fallbackRareOre = 10;
        private bool _pricesLoaded;

        public string DataDir { get; }

        /// <summary>
        /// The per-Torch-root <c>Instance\Factions</c> directory, when it differs from
        /// <see cref="DataDir"/>. On a shared-data deployment the state (brains, index, caches,
        /// events) lives in one folder that several roots share, while <c>tuning.json</c> and the
        /// bridge stay per-root — <c>tuning.json</c> because it is the file that names the data
        /// directory and so cannot live inside it.
        ///
        /// <para>Equal to <see cref="DataDir"/> when unset, which is the single-root behaviour.</para>
        /// </summary>
        public string InstanceDir { get; }

        public SnapshotService(string dataDir, string? instanceDir = null)
        {
            DataDir = dataDir;
            InstanceDir = string.IsNullOrWhiteSpace(instanceDir) ? dataDir : instanceDir!;
            _store = new FactionStore(dataDir);
            _brainsDir = Path.Combine(dataDir, "brains");

            if (Directory.Exists(_brainsDir))
                StartWatcher();
        }

        /// <summary>Where to read a per-root input from: the data directory if it is there,
        /// otherwise the instance directory. Core's own resolver, so the dashboard and the plugin
        /// answer "which tuning.json" the same way rather than twice.
        ///
        /// <para><b>Inputs fall back; state never does.</b> Brains, the index and the caches read
        /// from <see cref="DataDir"/> alone — a fallback there would resurrect a stale local world
        /// beside the shared one, which is the failure the split exists to prevent.</para></summary>
        private string Input(string fileName)
        {
            bool usedFallback;
            return DataDirectory.ResolveInput(fileName, DataDir, InstanceDir, File.Exists, out usedFallback);
        }

        private void StartWatcher()
        {
            _watcher = new FileSystemWatcher(_brainsDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, _) => Invalidate();
            _watcher.Created += (_, _) => Invalidate();
            _watcher.Deleted += (_, _) => Invalidate();
            // The plugin writes a brain to a temp file and File.Replaces it in, which arrives as a
            // rename onto <TAG>.json, not as a change.
            _watcher.Renamed += (_, _) => Invalidate();
        }

        private void Invalidate()
        {
            lock (_lock) _cached = null;
        }

        public WorldSnapshotDto GetSnapshot()
        {
            lock (_lock)
            {
                if (_cached != null && (DateTime.UtcNow - _cacheTime).TotalSeconds < 5)
                    return _cached;

                // Lazily start watcher once the directory appears
                if (_watcher == null && Directory.Exists(_brainsDir))
                    StartWatcher();

                _cached = Build();
                _cacheTime = DateTime.UtcNow;
                return _cached;
            }
        }

        private void EnsurePrices()
        {
            if (_pricesLoaded) return;
            _pricesLoaded = true;

            string pricesFile = Path.Combine(DataDir, "item_prices.json");
            if (File.Exists(pricesFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pricesFile));
                _itemPrices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Value.TryGetInt64(out long v))
                        _itemPrices[prop.Name] = v;
            }

            string tuningFile = Input("tuning.json");
            if (File.Exists(tuningFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(tuningFile));
                JsonElement p;
                if (doc.RootElement.TryGetProperty("FallbackOreValue",           out p)) _fallbackOre     = p.GetInt64();
                if (doc.RootElement.TryGetProperty("FallbackIngotValue",          out p)) _fallbackIngot   = p.GetInt64();
                if (doc.RootElement.TryGetProperty("FallbackProcessedGoodValue",  out p)) _fallbackComp    = p.GetInt64();
                if (doc.RootElement.TryGetProperty("FallbackHydrogenValue",       out p)) _fallbackH2      = p.GetInt64();
                if (doc.RootElement.TryGetProperty("FallbackOxygenValue",         out p)) _fallbackO2      = p.GetInt64();
                if (doc.RootElement.TryGetProperty("FallbackRareOreValue",        out p)) _fallbackRareOre = p.GetInt64();
            }
        }

        private long PriceForInvKey(string key)
        {
            int colon = key.IndexOf(':');
            if (colon < 0) return 1;
            string prefix    = key.Substring(0, colon);
            string subTypeId = key.Substring(colon + 1);

            if (_itemPrices != null && _itemPrices.TryGetValue(subTypeId, out long specific))
                return specific;

            switch (prefix)
            {
                case "Ore":   return _fallbackOre;
                case "Ingot": return _fallbackIngot;
                // Per kilolitre, like every gas price -- ComputeAssetValue multiplies it through
                // ItemCredits, which is where the litre stock meets it.
                case "Gas":   return subTypeId == "Oxygen" ? _fallbackO2 : _fallbackH2;
                // A bottle is an assembler product, not gas: it prices as the component it is.
                case "Comp":  return _fallbackComp;
                default: return 1;
            }
        }

        /// <summary>A node's repair queue as the yard serves it. The rows come from Core's own view
        /// (<see cref="HullDamage.QueueView"/>) over a scratch node carrying just what it reads, so
        /// the dashboard and the in-game Builds window order and fold the queue by one rule.</summary>
        private static List<RepairRowDto>? BuildRepairQueue(SourceDoc src)
        {
            if (src.DamagedHulls == null || src.DamagedHulls.Count == 0) return null;
            var node = new SpatialNode { Id = src.Id ?? "", ShipyardPaused = src.ShipyardPaused };
            int yard;
            if (src.Capabilities != null && src.Capabilities.TryGetValue(nameof(NodeCapabilityKind.Shipyard), out yard) && yard > 0)
                node.Capabilities[NodeCapabilityKind.Shipyard] = yard;
            FactionStore.LoadDamagedHulls(node, src.DamagedHulls);

            var rows = new List<RepairRowDto>();
            foreach (RepairQueueRow r in HullDamage.QueueView(node))
            {
                Dictionary<string, long>? missing = null;
                foreach (KeyValuePair<string, long> kv in r.Hull.Bill)
                {
                    long have;
                    r.Hull.Collected.TryGetValue(kv.Key, out have);
                    if (have >= kv.Value) continue;
                    missing ??= new Dictionary<string, long>();
                    missing[kv.Key] = kv.Value - have;
                }
                rows.Add(new RepairRowDto
                {
                    PrefabId       = r.PrefabId ?? "",
                    Count          = r.Count,
                    Position       = r.Position,
                    Integrity      = r.Integrity,
                    State          = r.State.ToString(),
                    BillUnits      = r.BillUnits,
                    CollectedUnits = r.CollectedUnits,
                    QueuedUtc      = r.QueuedUtc,
                    DoneUtc        = r.DoneUtc == default(DateTime) ? (DateTime?)null : r.DoneUtc,
                    Missing        = missing,
                });
            }
            return rows;
        }

        private static int CountDamagedHulls(BrainDoc brain, bool waitingOnly)
        {
            if (brain.Sources == null) return 0;
            int n = 0;
            foreach (var src in brain.Sources)
            {
                if (src.DamagedHulls == null) continue;
                foreach (var h in src.DamagedHulls)
                    if (!waitingOnly || h.PartsCompleteUtc == default(DateTime)) n++;
            }
            return n;
        }

        private long ComputeAssetValue(BrainDoc brain)
        {
            if (brain.Sources == null) return 0;
            long total = 0;
            foreach (var src in brain.Sources)
            {
                if (src.Inventory != null)
                    foreach (var kv in src.Inventory)
                        total += ItemCredits.For(kv.Key, kv.Value, PriceForInvKey(kv.Key));
                if (src.ReservedInventory != null)
                    foreach (var kv in src.ReservedInventory)
                        total += ItemCredits.For(kv.Key, kv.Value, PriceForInvKey(kv.Key));
            }
            return total;
        }

        private WorldSnapshotDto Build()
        {
            EnsurePrices();
            var now = DateTime.UtcNow;
            var result = new WorldSnapshotDto { UtcNow = now, DataAsOf = NewestBrainFileTime() };
            FactionTuning transitTuning = LoadTransitTuning(result.InputLoadFailures);
            FactionTuning siegeTuning = LoadSiegeTuning();
            TryLoadPlanets(result);
            var brains = new List<BrainDoc>();

            List<IndexEntry> index;
            try { index = _store.LoadIndex(); }
            catch { return result; }

            foreach (var entry in index)
            {
                BrainDoc? brain;
                // A brain that will not parse is still skipped -- one bad file must not blank the
                // whole snapshot -- but it is RECORDED. Silently skipping made a version skew between
                // this service and the plugin look like a world with two factions in it.
                try { brain = _store.LoadBrain(entry.Tag); }
                catch (Exception ex)
                {
                    result.BrainLoadFailures.Add(new BrainLoadFailureDto
                    {
                        Tag = entry.Tag ?? "(no tag)",
                        Error = ex.GetType().Name + ": " + ex.Message,
                    });
                    continue;
                }
                if (brain == null)
                {
                    result.BrainLoadFailures.Add(new BrainLoadFailureDto
                    {
                        Tag = entry.Tag ?? "(no tag)",
                        Error = "brain file is absent or parsed to nothing.",
                    });
                    continue;
                }
                brains.Add(brain);

                var campaignFoci = new List<CampaignFocusDto>();
                if (brain.CampaignFoci != null)
                    foreach (CampaignFocusDoc focus in brain.CampaignFoci)
                    {
                        if (focus == null || string.IsNullOrEmpty(focus.CenterNodeId)) continue;
                        var dto = new CampaignFocusDto
                        {
                            Id = focus.Id ?? focus.CenterNodeId,
                            CenterNodeId = focus.CenterNodeId,
                            Priority = focus.Priority,
                            SetAtUtc = focus.SetAtUtc,
                            ExpiresAtUtc = focus.ExpiresAtUtc,
                            RegionMembers = focus.RegionMembers ?? new List<string>()
                        };
                        if (focus.Objectives != null)
                            foreach (CampaignObjectiveDoc objective in focus.Objectives)
                                if (objective != null)
                                    dto.Objectives.Add(new CampaignObjectiveDto
                                    {
                                        Kind = objective.Kind ?? "",
                                        TargetNodeId = objective.TargetNodeId ?? ""
                                    });
                        campaignFoci.Add(dto);
                    }
                if (campaignFoci.Count == 0 && !string.IsNullOrEmpty(brain.CampaignFocusRegion))
                {
                    var legacyFocus = new CampaignFocusDto
                    {
                        Id = brain.CampaignFocusRegion,
                        CenterNodeId = brain.CampaignFocusRegion,
                        Priority = brain.CampaignFocusPriority ?? 0,
                        SetAtUtc = brain.CampaignSetAtUtc ?? DateTime.MinValue,
                        ExpiresAtUtc = DateTime.MinValue,
                        RegionMembers = brain.CampaignFocusRegionMembers ?? new List<string>()
                    };
                    if (legacyFocus.RegionMembers.Count == 0) legacyFocus.RegionMembers.Add(legacyFocus.CenterNodeId);
                    legacyFocus.Objectives.Add(new CampaignObjectiveDto { Kind = "DefendRegion", TargetNodeId = legacyFocus.CenterNodeId });
                    campaignFoci.Add(legacyFocus);
                }

                // Count owned nodes for the faction summary
                int nodeCount = brain.Sources?.Count ?? 0;
                long assetValue = ComputeAssetValue(brain);
                long debt       = brain.Debt ?? 0;

                result.Factions.Add(new FactionDto
                {
                    Tag             = brain.Tag ?? entry.Tag,
                    PersonalityId   = brain.PersonalityId ?? "default",
                    Treasury        = brain.Treasury,
                    Debt            = debt,
                    SupportDebt     = brain.CentralBankSupportDebt ?? 0,
                    AssetValue      = assetValue,
                    NetWorth        = brain.Treasury + assetValue - debt,
                    Drives          = brain.Drives ?? new double[5],
                    Relations       = brain.Relations ?? new Dictionary<string, double>(),
                    Fatigue         = brain.Fatigue,
                    TruceUntil      = brain.TruceUntil,
                    CampaignFoci    = campaignFoci,
                    Log             = brain.Log ?? new List<string>(),
                    DemandPressure  = brain.DemandPressure,
                    IsDormant       = brain.DormantUntilUtc.HasValue && brain.DormantUntilUtc.Value > now,
                    NodeCount       = nodeCount,
                    HullsInRepair   = CountDamagedHulls(brain, false),
                    HullsWaitingForParts = CountDamagedHulls(brain, true),
                });

                if (brain.Sources != null)
                {
                    foreach (var src in brain.Sources)
                    {
                        List<BuildQueueDto>? bq = null;
                        if (src.BuildQueue != null && src.BuildQueue.Count > 0)
                        {
                            bq = new List<BuildQueueDto>(src.BuildQueue.Count);
                            foreach (var b in src.BuildQueue)
                                bq.Add(new BuildQueueDto
                                {
                                    Id         = b.Id ?? "",
                                    Role       = b.Role ?? "",
                                    PrefabId   = b.PrefabId,
                                    Bom            = b.Bom ?? new Dictionary<string, long>(),
                                    Collected      = b.Collected ?? new Dictionary<string, long>(),
                                    ReservedInputs = b.ReservedInputs ?? new Dictionary<string, long>(),
                                    CreatedUtc = b.CreatedUtc,
                                    ExpiresUtc = b.ExpiresUtc,
                                });
                        }

                        result.Nodes.Add(new NodeDto
                        {
                            Id            = src.Id ?? "",
                            X             = src.X,
                            Y             = src.Y,
                            Z             = src.Z,
                            OwnerTag      = brain.Tag,
                            HoldStrength  = src.Strength,
                            Radius        = src.Radius,
                            ResourceValue = src.ResourceValue,
                            Richness      = src.Richness,
                            // What the ground holds, and whether anyone has looked. A brain written
                            // before FAC-102 carries neither, and reads as unsurveyed -- which is the
                            // honest answer: its retired kind ordinal was a die roll, not a sample,
                            // and stage 1 already ruled that it grants nothing on load.
                            DominantOre   = src.DominantOre ?? "",
                            // A barren sample is persisted as points with no entries -- ToDepositDoc
                            // returns null for it -- so the deposit list alone would read a node that
                            // was surveyed and found empty as one nobody has looked at.
                            Surveyed      = src.Deposit != null || (src.DepositPoints ?? 0) > 0,
                            // Never mined is written as DateTime.MinValue and sent as nothing at
                            // all: a field on every node would print a cooldown row for all 1,459
                            // of them and stop answering the question it was added for. An expired
                            // deadline is still sent -- the browser decides what is active against
                            // the snapshot's own clock.
                            MineCooldownUntilUtc = src.MineCooldownUntilUtc == default(DateTime)
                                ? (DateTime?)null
                                : src.MineCooldownUntilUtc,
                            DisplayName   = string.IsNullOrEmpty(src.DisplayName) ? null : src.DisplayName,
                            HasStation    = src.StableStationId != 0,
                            HasInfrastructure = src.StableStationId != 0
                                && (!SourceMapping.IsScanBacked(src.Id) || HasCapabilities(src.Capabilities)),
                            Capabilities  = src.Capabilities,
                            ReservedInventory = src.ReservedInventory,
                            Inventory     = src.Inventory,
                            ShipStock     = src.ShipStock,
                            BuildQueue    = bq,
                            RepairQueue   = BuildRepairQueue(src),
                            Siege         = BuildSiege(src.Siege, siegeTuning, now),
                        });
                    }
                }

                if (brain.PendingFleets != null)
                {
                    foreach (var pf in brain.PendingFleets)
                    {
                        if (pf.Resolved && pf.Kind != "Guard") continue;

                        double dx = pf.TargetX - pf.OriginX;
                        double dy = pf.TargetY - pf.OriginY;
                        double dz = pf.TargetZ - pf.OriginZ;
                        double transitDistanceKm = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        // The tier table the server scheduled this leg against, when it carries one.
                        // The dashboard has no planet catalogue, so a leg that crosses gravity or an
                        // authored region can only be placed from what was persisted with it.
                        LegZoneProfile legZones = FactionStore.ZonesFromDoc(pf.ZoneRunKm, pf.ZoneRunTiers);
                        double totalSec = (pf.EstimatedArrivalUtc - pf.DepartedUtc).TotalSeconds;
                        double elapsedSec = (now - pf.DepartedUtc).TotalSeconds;
                        double ratio = totalSec > 0
                            ? Math.Max(0.0, Math.Min(1.0, elapsedSec / totalSec))
                            : 0.0;

                        result.Fleets.Add(new FleetDto
                        {
                            FleetId              = pf.FleetId ?? (pf.ActorTag + "_" + pf.DepartedUtc.Ticks),
                            Kind                 = pf.Kind ?? "Trade",
                            ActorTag             = pf.ActorTag ?? "",
                            TargetTag            = pf.TargetTag,
                            OriginNodeId         = pf.OriginNodeId,
                            TargetNodeId         = pf.TargetNodeId,
                            OriginX              = pf.OriginX,
                            OriginY              = pf.OriginY,
                            OriginZ              = pf.OriginZ,
                            TargetX              = pf.TargetX,
                            TargetY              = pf.TargetY,
                            TargetZ              = pf.TargetZ,
                            CurrentX             = pf.OriginX + (pf.TargetX - pf.OriginX) * ratio,
                            CurrentY             = pf.OriginY + (pf.TargetY - pf.OriginY) * ratio,
                            CurrentZ             = pf.OriginZ + (pf.TargetZ - pf.OriginZ) * ratio,
                            DepartedUtc          = pf.DepartedUtc,
                            EstimatedArrivalUtc  = pf.EstimatedArrivalUtc,
                            CompletionRatio      = ratio,
                            TransitProgressRatio = legZones != null
                                ? transitTuning.TransitProgressRatio(legZones, elapsedSec / 60.0)
                                : transitTuning.TransitProgressRatio(transitDistanceKm, elapsedSec / 60.0),
                            TransitDistanceKm    = transitDistanceKm,
                            FleetSpeedMinKmPerMin = transitTuning.FleetSpeedMinKmPerMin,
                            FleetSpeedKmPerMin   = transitTuning.FleetSpeedKmPerMin,
                            FleetAccelKmPerMin2  = transitTuning.FleetAccelKmPerMin2,
                            CargoValue           = pf.CargoValue,
                            Cargo                = pf.Cargo,
                            ConvoySize           = pf.ConvoySize,
                            Resolved             = pf.Resolved,
                            RouteDistanceKm      = pf.RouteDistanceKm,
                            RouteCoveredKm       = pf.RouteCoveredKm,
                            RouteDepartedUtc     = pf.RouteDepartedUtc,
                            CurrentStopKind      = pf.CurrentStopKind ?? "Service",
                            PickupItemKey        = pf.PickupItemKey,
                            PickupQuantity       = pf.PickupQuantity,
                            RemainingStops       = pf.RemainingStops == null ? null : pf.RemainingStops
                                .Select(stop => new FleetStopDto
                                {
                                    NodeId = stop.NodeId ?? "",
                                    Kind = stop.Kind ?? "Service",
                                    HasPosition = stop.HasPosition,
                                    X = stop.X,
                                    Y = stop.Y,
                                    Z = stop.Z,
                                }).ToList(),
                            PathNodeIds          = pf.PathNodeIds,
                            MaterialisedEntityId = Math.Max(0, pf.MaterialisedEntityId),
                            FerryRole            = pf.FerryRole,
                            ShipPrefabId         = pf.ShipPrefabId,
                        });
                    }
                }
            }

            result.CentralBank = BuildCentralBankSnapshot(brains, now, result.Factions);
            return result;
        }

        /// <summary>Rebuilds the world from the brains and reads the Central Bank off it. Also stamps
        /// each faction's ship counts onto <paramref name="factions"/>: they need the same rebuilt
        /// roster, and taking them from it keeps the per-faction figures summing to the bank's.</summary>
        private CentralBankDto BuildCentralBankSnapshot(List<BrainDoc> brains, DateTime now, List<FactionDto> factions)
        {
            var world = new WorldModel(new SeededRandom(1));
            world.UtcNow = now;
            ApplyCentralBankTuning(world.Tuning, Input("tuning_defaults.json"));
            ApplyCentralBankTuning(world.Tuning, Input("tuning.json"));
            // Hulls priced as the host prices them, and the target set against the host's anchor.
            world.PrefabCatalogue = PrefabCostCatalogue.Load(Path.Combine(DataDir, "prefab_catalogue.json"));
            new FactionStore(DataDir).LoadCentralBankState(world);

            var states = new List<FactionState>(brains.Count);
            foreach (BrainDoc brain in brains)
            {
                FactionState state = FactionStore.ApplyBrainDoc(brain, world.Graph);
                world.SetTreasury(state.Tag, brain.Treasury);
                world.SetDebt(state.Tag, brain.Debt ?? 0);
                state.CentralBankSupportDebt = brain.CentralBankSupportDebt ?? 0;
                states.Add(state);
            }

            CentralBankSnapshot snap = world.SnapshotCentralBank(states);
            Dictionary<string, ShipTally> ships = world.TallyShipsByFaction(states);
            foreach (FactionDto f in factions)
            {
                ShipTally t;
                if (!ships.TryGetValue(f.Tag, out t)) continue;
                f.ShipCount = t.Total;
                f.IdleShipCount = t.Idle;
                f.InFlightShipCount = t.InFlight;
            }
            CentralBankEventLogDto eventLog = LoadCentralBankEventLog();
            return new CentralBankDto
            {
                CurrentMoneySupply = snap.CurrentMoneySupply,
                TargetMoneySupply = snap.TargetMoneySupply,
                MoneySupplyGap = snap.MoneySupplyGap,
                WorldCollateralValue = snap.WorldCollateralValue,
                AnchorCollateralValue = snap.AnchorCollateralValue,
                ReserveRatio = snap.ReserveRatio,
                TerritoryNodeCount = snap.TerritoryNodeCount,
                FoundedNodeCount = snap.FoundedNodeCount,
                CapabilityLevelCount = snap.CapabilityLevelCount,
                ShipCount = snap.ShipCount,
                IdleShipCount = snap.IdleShipCount,
                InFlightShipCount = snap.InFlightShipCount,
                BankEvents = eventLog.BankEvents,
                DebtEvents = eventLog.DebtEvents,
            };
        }

        private void TryLoadPlanets(WorldSnapshotDto result)
        {
            string path = Path.Combine(DataDir, "planets.json");
            if (!File.Exists(path)) return;
            try
            {
                var snapshot = JsonSerializer.Deserialize<PlanetFileDto>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (snapshot != null && snapshot.Planets != null) result.Planets = snapshot.Planets;
            }
            catch { /* Snapshot may be replaced while it is read; retain an empty catalogue. */ }
        }

        private sealed class PlanetFileDto
        {
            public List<PlanetDto> Planets { get; set; } = new();
        }

        private CentralBankEventLogDto LoadCentralBankEventLog()
        {
            string path = Path.Combine(DataDir, "central_bank.json");
            if (!File.Exists(path)) return new CentralBankEventLogDto();
            try
            {
                string json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<CentralBankEventLogDto>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return dto ?? new CentralBankEventLogDto();
            }
            catch
            {
                return new CentralBankEventLogDto();
            }
        }

        /// <summary>The fleet transit contract, read through <see cref="Input"/> like every other
        /// per-root input: both tuning files are per-instance under the shared-data split, so a read
        /// straight off <see cref="DataDir"/> finds neither of them there.</summary>
        private FactionTuning LoadTransitTuning(List<InputLoadFailureDto> failures)
        {
            var tuning = new FactionTuning();
            ApplyTransitTuning(tuning, Input("tuning_defaults.json"));
            ApplyTransitTuning(tuning, Input("tuning.json"));

            // A zero contract is not a world anyone configured -- it is what an unread tuning file
            // leaves behind, since the three fields carry no initialiser. FactionTuning's own guard
            // then reports full progress for every fleet, which reads as a world that has all
            // arrived rather than as a failure, so the absence is only visible if it is said here.
            if (tuning.FleetSpeedKmPerMin <= 0 || tuning.FleetAccelKmPerMin2 <= 0)
                failures.Add(new InputLoadFailureDto
                {
                    Input = "tuning_defaults.json + tuning.json (fleet transit contract)",
                    Error = string.Format(CultureInfo.InvariantCulture,
                        "read as FleetSpeedKmPerMin={0}, FleetSpeedMinKmPerMin={1}, FleetAccelKmPerMin2={2}; "
                        + "a zero contract draws every fleet at zero speed and 100% progress. Searched '{3}'"
                        + " then '{4}'.",
                        tuning.FleetSpeedKmPerMin, tuning.FleetSpeedMinKmPerMin, tuning.FleetAccelKmPerMin2,
                        DataDir, InstanceDir),
                });
            return tuning;
        }

        /// <summary>The siege contract, read the same way and from the same two files as the transit
        /// one. No load-failure row is reported for it: unlike the transit fields, every field here
        /// carries an initialiser on <see cref="FactionTuning"/>, so an unread tuning file leaves
        /// the shipped numbers rather than a zero contract that renders as a lie.</summary>
        private FactionTuning LoadSiegeTuning()
        {
            var tuning = new FactionTuning();
            ApplySiegeTuning(tuning, Input("tuning_defaults.json"));
            ApplySiegeTuning(tuning, Input("tuning.json"));
            return tuning;
        }

        private static void ApplySiegeTuning(FactionTuning tuning, string path)
        {
            if (!File.Exists(path)) return;
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(path)); }
            catch { return; }
            ApplyDouble(root, "RaidZoneIntegrityPool", v => tuning.RaidZoneIntegrityPool = v);
            ApplyDouble(root, "RaidZoneIntegrityRegenSeconds", v => tuning.RaidZoneIntegrityRegenSeconds = v);
            ApplyDouble(root, "RaidZoneSiegeDwellSeconds", v => tuning.RaidZoneSiegeDwellSeconds = v);
            ApplyDouble(root, "PlayerRaidBlockDamageForSuccess", v => tuning.PlayerRaidBlockDamageForSuccess = v);
        }

        /// <summary>One node's siege, or null when the brain carries none — which is the common case
        /// and the reason the whole object is omitted rather than sent as zeros.
        ///
        /// <para>The integrity is not recomputed here. The doc is written back onto a scratch
        /// <see cref="SpatialNode"/> and <see cref="PlayerSiege.IntegrityFraction"/> answers, which
        /// is the same call the host colours the in-game safe zone with: the regeneration ramp, the
        /// freeze while the zone is breached and the never-besieged sentinel are then one
        /// derivation for both surfaces instead of two that can drift apart.</para></summary>
        private static SiegeDto? BuildSiege(SiegeDoc? doc, FactionTuning tuning, DateTime now)
        {
            if (doc == null) return null;

            var scratch = new SpatialNode();
            doc.ApplyTo(scratch);

            double forSuccess = tuning.PlayerRaidBlockDamageForSuccess;
            return new SiegeDto
            {
                Integrity = PlayerSiege.IntegrityFraction(scratch, tuning, now),
                StartedUtc = doc.SiegeStartedUtc,
                DwellSeconds = tuning.RaidZoneSiegeDwellSeconds,
                BreachedUtc = doc.ZoneBreachedUtc == default(DateTime)
                    ? (DateTime?)null
                    : doc.ZoneBreachedUtc,
                // Clamped, because the last flush of damage overshoots the threshold and a bar past
                // 100% reads as a broken gauge rather than as a raid that is already won.
                RaidProgress = forSuccess > 0
                    ? Math.Max(0.0, Math.Min(1.0, doc.PlayerRaidBlockDamage / forSuccess))
                    : 0.0,
                SucceededUtc = doc.PlayerRaidSucceededUtc == default(DateTime)
                    ? (DateTime?)null
                    : doc.PlayerRaidSucceededUtc,
            };
        }

        private static void ApplyTransitTuning(FactionTuning tuning, string path)
        {
            if (!File.Exists(path)) return;
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(path)); }
            catch { return; }
            ApplyDouble(root, "FleetSpeedMinKmPerMin", v => tuning.FleetSpeedMinKmPerMin = v);
            ApplyDouble(root, "FleetSpeedKmPerMin", v => tuning.FleetSpeedKmPerMin = v);
            ApplyDouble(root, "FleetAccelKmPerMin2", v => tuning.FleetAccelKmPerMin2 = v);
        }

        private static void ApplyCentralBankTuning(FactionTuning tuning, string path)
        {
            if (!File.Exists(path)) return;

            JObject root;
            try { root = JObject.Parse(File.ReadAllText(path)); }
            catch { return; }

            ApplyLong(root, "CentralBankTerritoryNodeValue", v => tuning.CentralBankTerritoryNodeValue = v);
            ApplyLong(root, "CentralBankFoundedNodeValue", v => tuning.CentralBankFoundedNodeValue = v);
            ApplyLong(root, "CentralBankCapabilityLevelValue", v => tuning.CentralBankCapabilityLevelValue = v);
            ApplyLong(root, "CentralBankShipValue", v => tuning.CentralBankShipValue = v);
            ApplyDouble(root, "CentralBankReserveRatio", v => tuning.CentralBankReserveRatio = v);
            ApplyDouble(root, "CentralBankAnchorHalfLife", v => tuning.CentralBankAnchorHalfLife = TimeSpan.FromSeconds(v));
        }

        private static void ApplyLong(JObject root, string name, Action<long> apply)
        {
            JToken? token;
            if (root.TryGetValue(name, StringComparison.Ordinal, out token) && token.Type != JTokenType.Null)
            {
                long value;
                if (long.TryParse(token.ToString(), out value))
                    apply(value);
            }
        }

        private static void ApplyDouble(JObject root, string name, Action<double> apply)
        {
            JToken? token;
            if (root.TryGetValue(name, StringComparison.Ordinal, out token) && token.Type != JTokenType.Null)
            {
                double value;
                if (double.TryParse(token.ToString(), out value))
                    apply(value);
            }
        }

        private static bool HasCapabilities(Dictionary<string, int>? capabilities)
        {
            if (capabilities == null) return false;
            foreach (var kv in capabilities)
                if (kv.Value > 0) return true;
            return false;
        }

        private sealed class CentralBankEventLogDto
        {
            public List<MonetaryEventDto> BankEvents { get; set; } = new List<MonetaryEventDto>();
            public List<MonetaryEventDto> DebtEvents { get; set; } = new List<MonetaryEventDto>();
        }

        private DateTime? NewestBrainFileTime()
        {
            if (!Directory.Exists(_brainsDir)) return null;
            DateTime? newest = null;
            foreach (string f in Directory.GetFiles(_brainsDir, "*.json"))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (newest == null || t > newest) newest = t;
            }
            // Also check index.json in the base dir
            string idx = Path.Combine(DataDir, "index.json");
            if (File.Exists(idx))
            {
                var t = File.GetLastWriteTimeUtc(idx);
                if (newest == null || t > newest) newest = t;
            }
            return newest;
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }
    }
}
