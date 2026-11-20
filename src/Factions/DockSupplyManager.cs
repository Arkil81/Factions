using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using Factions.Wire;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;

namespace Factions
{
    /// <summary>
    /// Server side of the Services Terminal's Refuel and Rearm: a ship docked to the station by a
    /// connector buys hydrogen, oxygen and ammunition out of the node's own stock.
    ///
    /// <para><b>The store's shelf, not a second one.</b> What may be sold and at what price is the
    /// station's own offer rows (<see cref="StationStoreManager.OffersFor"/>), with SE's
    /// friendly-reputation discount applied the way the store block applies it. So a refuel can
    /// never draw below the floor the store shelf keeps, and a player reads the same price at the
    /// counter as at the terminal.</para>
    ///
    /// <para><b>Plan, charge, deliver, refund, settle</b> — in that order and in one frame on the
    /// game thread. The plan (<see cref="DockSupply"/>) never exceeds the buyer's balance or the total
    /// they confirmed, so the whole plan is charged up front with a wallet debit SE refuses rather
    /// than clamps. Anything that then fails to land is refunded, and only what landed is debited
    /// from the node and credited to the owning faction's ledger.</para>
    ///
    /// <para><b>Docked means connector-locked.</b> The target is the mechanical group of the grid
    /// selected on the Active Grids tab, and one of its connectors must be locked to the terminal's
    /// own construct. A ship parked on landing gear, or docked to another ship, is refused: fuel goes
    /// through a connector, and a connector is what says the ship is berthed here rather than
    /// passing.</para>
    /// </summary>
    internal sealed class DockSupplyManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        private static readonly string HydrogenKey =
            InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_GasProperties", "Hydrogen");
        private static readonly string OxygenKey =
            InventoryKeyMapper.FromTypeIdAndSubtype("MyObjectBuilder_GasProperties", "Oxygen");

        /// <summary>Upper bound on one cargo-reserve line. A client asking for more is not asking
        /// for a reserve; the container volume would stop it anyway, this stops the arithmetic.</summary>
        private const long MaxReserveCount = 1000000;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly StationStoreManager _store;
        private readonly FactionTuning _tuning;

        public DockSupplyManager(FactionRuntime runtime, UplinkManager uplink, StationStoreManager store,
            FactionTuning tuning)
        {
            _runtime = runtime;
            _uplink = uplink;
            _store = store;
            _tuning = tuning;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.DockRefuelRequest, OnRefuelRequest);
            _uplink.RegisterHandler(WireMessageKind.RearmSurveyRequest, OnRearmSurveyRequest);
            _uplink.RegisterHandler(WireMessageKind.RearmRequest, OnRearmRequest);
        }

        private bool Granted(ulong steamId)
        {
            return _uplink != null && (_uplink.GrantedFeatures(steamId) & WireFeatures.DockSupply) != 0;
        }

        // ---- the docked ship ----------------------------------------------------------------------

        /// <summary>Everything one request resolves before it plans anything.</summary>
        private sealed class DockContext
        {
            public TerminalContext Terminal;
            public MyCubeGrid Grid;
            public List<MyCubeGrid> Ship;
            public Dictionary<string, DockSupplyOffer> Offers;
        }

        private bool TryResolve(ulong steamId, long terminalEntityId, long gridEntityId,
            out DockContext dock, out string refusal)
        {
            dock = null;
            if (_tuning != null && !_tuning.ServicesTerminalDockSupply)
            {
                refusal = "Refuel and rearm are switched off on this server.";
                return false;
            }
            if (_store == null)
            {
                refusal = "Factions is not ready on this server.";
                return false;
            }

            TerminalContext ctx;
            if (!TerminalAccess.TryAuthoriseCustomer(_runtime, steamId, terminalEntityId, out ctx, out refusal))
                return false;

            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById<MyCubeGrid>(gridEntityId, out grid, false)
                || grid == null || grid.MarkedForClose || grid.Closed)
            {
                refusal = "That ship is no longer here.";
                return false;
            }

            List<MyCubeGrid> ship = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid);
            List<MyCubeGrid> station = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ctx.Block.CubeGrid);
            var stationSet = new HashSet<MyCubeGrid>(station);
            foreach (MyCubeGrid g in ship)
            {
                if (stationSet.Contains(g))
                {
                    refusal = "That is the station itself. Select a ship docked to it.";
                    return false;
                }
            }

            if (!grid.BigOwners.Contains(ctx.IdentityId))
            {
                refusal = "You do not own " + NameOf(grid) + ".";
                return false;
            }

            if (!IsDockedTo(ship, stationSet))
            {
                refusal = NameOf(grid) + " is not docked here. Lock one of its connectors to this "
                        + "station first.";
                return false;
            }

            dock = new DockContext
            {
                Terminal = ctx,
                Grid = grid,
                Ship = ship,
                Offers = OffersFor(ctx),
            };
            refusal = null;
            return true;
        }

        private static bool IsDockedTo(List<MyCubeGrid> ship, HashSet<MyCubeGrid> station)
        {
            foreach (MyCubeGrid g in ship)
            {
                foreach (MyShipConnector connector in g.GetFatBlocks<MyShipConnector>())
                {
                    if (connector == null || connector.Closed || !connector.Connected) continue;
                    MyShipConnector other = connector.Other;
                    if (other != null && other.CubeGrid != null && station.Contains(other.CubeGrid)) return true;
                }
            }
            return false;
        }

        /// <summary>The station's offer rows as planner offers: quantities in inventory units, prices
        /// with the caller's friendly discount.</summary>
        private Dictionary<string, DockSupplyOffer> OffersFor(TerminalContext ctx)
        {
            double multiplier = OffersMultiplier(ctx);
            var offers = new Dictionary<string, DockSupplyOffer>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, StationListing> kv in _store.OffersFor(ctx.Node))
            {
                offers[kv.Key] = new DockSupplyOffer
                {
                    Available = DockSupply.OfferUnits(kv.Key, kv.Value.Quantity),
                    UnitPrice = DockSupply.ApplyMultiplier(kv.Value.UnitPrice, multiplier),
                };
            }
            return offers;
        }

        /// <summary>SE's discount for a friend of the station's faction, from
        /// <c>MyStoreBlock.BuyFromStation</c>: <c>1 − OffersFriendlyBonus × (rep − min) / (max − min)</c>.
        /// 1.0 for anyone who is not a friend, and for anything that cannot be read.</summary>
        internal static double OffersMultiplier(TerminalContext ctx)
        {
            try
            {
                var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
                if (economy == null || economy.EconomyDefinition == null) return 1.0;
                IMyFactionStation station = MySession.Static.Factions.GetStationByGridId(ctx.Block.CubeGrid.EntityId);
                if (station == null) return 1.0;
                Tuple<MyRelationsBetweenFactions, int> relation =
                    MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(ctx.IdentityId, station.FactionId);
                if (relation == null || relation.Item1 != MyRelationsBetweenFactions.Friends) return 1.0;
                var def = economy.EconomyDefinition;
                double span = def.ReputationFriendlyMax - def.ReputationFriendlyMin;
                if (span <= 0) return 1.0;
                return 1.0 - def.OffersFriendlyBonus * ((relation.Item2 - def.ReputationFriendlyMin) / span);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Friendly store discount could not be read; charging the list price.");
                return 1.0;
            }
        }

        private static bool Usable(MyTerminalBlock block, long identityId)
        {
            return block != null && !block.Closed && !block.MarkedForClose && block.IsFunctional
                   && block.HasPlayerAccess(identityId);
        }

        // ---- refuel -------------------------------------------------------------------------------

        private void OnRefuelRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId, maxCredits;
            WireDockGas gas;
            bool commit;
            if (!FactionsWire.TryReadDockRefuelRequest(data, out terminalEntityId, out gridEntityId,
                    out gas, out commit, out maxCredits))
            {
                Log.Warn("Dock refuel request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            bool ok = false;
            long litres = 0, credits = 0, balanceAfter = 0;
            WireReceiptLine[] receipt = null;
            string message;
            try
            {
                ok = TryRefuel(steamId, terminalEntityId, gridEntityId, gas, commit, maxCredits,
                    out litres, out credits, out message, out balanceAfter, out receipt);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Dock refuel failed for " + steamId + ".");
                ok = false;
                message = "The refuel failed on the server. If you were charged, the charge stands only "
                        + "for gas that reached your tanks.";
            }

            if (!ok)
                Log.Info("DOCK_SUPPLY_REFUSED service=refuel gas=" + gas + " player=" + steamId
                       + " grid=" + gridEntityId + " commit=" + commit + " reason=" + message);
            _uplink.Send(steamId, FactionsWire.WriteDockRefuelResult(gas, ok, ok && !commit, litres, credits, message,
                balanceAfter, receipt));
        }

        private bool TryRefuel(ulong steamId, long terminalEntityId, long gridEntityId, WireDockGas gas,
            bool commit, long maxCredits, out long litres, out long credits, out string message,
            out long balanceAfter, out WireReceiptLine[] receipt)
        {
            litres = 0;
            credits = 0;
            balanceAfter = 0;
            receipt = null;
            DockContext dock;
            if (!TryResolve(steamId, terminalEntityId, gridEntityId, out dock, out message)) return false;

            string key = gas == WireDockGas.Oxygen ? OxygenKey : HydrogenKey;
            string gasName = gas == WireDockGas.Oxygen ? "oxygen" : "hydrogen";
            long identityId = dock.Terminal.IdentityId;

            var tanks = new List<MyGasTank>();
            var sinks = new List<DockSupplySink>();
            foreach (MyCubeGrid g in dock.Ship)
            {
                foreach (MyGasTank tank in g.GetFatBlocks<MyGasTank>())
                {
                    if (!Usable(tank, identityId)) continue;
                    MyGasTankDefinition def = tank.BlockDefinition;
                    if (def == null || def.Capacity <= 0f) continue;
                    string tankKey = InventoryKeyMapper.FromTypeIdAndSubtype(
                        def.StoredGasId.TypeId.ToString(), def.StoredGasId.SubtypeName);
                    if (!string.Equals(tankKey, key, StringComparison.Ordinal)) continue;
                    long free = (long)Math.Floor((1.0 - tank.FilledRatio) * def.Capacity);
                    sinks.Add(new DockSupplySink { Slot = tanks.Count, InvKey = key, Capacity = Math.Max(0, free) });
                    tanks.Add(tank);
                }
            }
            if (tanks.Count == 0)
            {
                message = NameOf(dock.Grid) + " has no " + gasName + " tanks you can fill.";
                return false;
            }

            DockSupplyOffer offer;
            if (!dock.Offers.TryGetValue(key, out offer) || offer.Available <= 0)
            {
                message = "This station has no " + gasName + " to spare right now.";
                return false;
            }

            long balance = ServicesTerminalHelpers.BalanceOf(identityId);
            long budget = commit ? Math.Min(balance, Math.Max(0, maxCredits)) : balance;
            DockSupplyPlan plan = DockSupply.Plan(sinks, dock.Offers, budget);

            if (plan.TotalUnits <= 0)
            {
                message = EmptyPlanReason(plan.Limit, gasName + " tanks", gasName,
                    PriceText(key, offer.UnitPrice), commit);
                return false;
            }

            int filled = 0;
            for (int i = 0; i < plan.Units.Length; i++) if (plan.Units[i] > 0) filled++;
            litres = plan.TotalUnits;
            credits = plan.Credits;

            if (!commit)
            {
                var quote = new StringBuilder();
                quote.Append("Fill ").Append(filled).Append(filled == 1 ? " tank" : " tanks")
                     .Append(" on ").Append(NameOf(dock.Grid)).Append(" with ")
                     .Append(Num(litres)).Append(" L of ").Append(gasName)
                     .Append(" for ").Append(Num(credits)).Append(" credits?");
                quote.AppendLine().AppendLine()
                     .Append("Price: ").Append(PriceText(key, offer.UnitPrice)).Append('.');
                string limit = LimitNote(plan.Limit, gasName);
                if (limit != null) quote.AppendLine().Append(limit);
                message = quote.ToString();
                return true;
            }

            if (!MyBankingSystem.ChangeBalance(identityId, -credits))
            {
                message = "Your account refused the charge of " + Num(credits) + " credits. Nothing was delivered.";
                litres = 0;
                credits = 0;
                return false;
            }

            for (int i = 0; i < plan.Units.Length; i++)
            {
                long add = plan.Units[i];
                if (add <= 0) continue;
                MyGasTank tank = tanks[sinks[i].Slot];
                double capacity = tank.BlockDefinition.Capacity;
                double ratio = Math.Min(1.0, tank.FilledRatio + add / capacity);
                // The ModAPI entry point is Transfer's own path on a server: sets the ratio locally
                // and raises the broadcast event that replicates it. Its false return on the sync
                // branch is not a failure.
                ((Sandbox.ModAPI.IMyGasTank)tank).ChangeFilledRatio(ratio, true);
            }

            _store.HandleDockSupplySettled(dock.Terminal.Node, identityId, "refuel", plan.UnitsByKey, credits);

            message = "Filled " + filled + (filled == 1 ? " tank" : " tanks") + " on " + NameOf(dock.Grid) + ".";
            string note = LimitNote(plan.Limit, gasName);
            if (note != null) message += Environment.NewLine + note;
            balanceAfter = ServicesTerminalHelpers.BalanceOf(identityId);
            receipt = new[]
            {
                new WireReceiptLine
                {
                    Key = key,
                    Name = gas == WireDockGas.Oxygen ? "Oxygen" : "Hydrogen",
                    Loaded = litres,
                    Cargo = 0,
                    UnitPrice = offer.UnitPrice,
                    Credits = credits,
                },
            };
            return true;
        }

        // ---- rearm: the ship's guns and cargo ------------------------------------------------------

        /// <summary>One gun's ammo inventory.</summary>
        private sealed class GunSlot
        {
            public MyUserControllableGun Block;
            public MyInventory Inventory;
            public MyGunBase GunBase;
        }

        /// <summary>The ship's guns of one block definition.</summary>
        private sealed class GunGroup
        {
            public string Id;
            public string Name;
            public readonly List<GunSlot> Guns = new List<GunSlot>();
            /// <summary>Every magazine the group's weapon takes, by inventory key.</summary>
            public readonly Dictionary<string, MyDefinitionId> Magazines =
                new Dictionary<string, MyDefinitionId>(StringComparer.Ordinal);
        }

        private sealed class Armament
        {
            public readonly List<GunGroup> Groups = new List<GunGroup>();
            public readonly List<MyInventory> Cargo = new List<MyInventory>();
            public readonly Dictionary<string, MyDefinitionId> Magazines =
                new Dictionary<string, MyDefinitionId>(StringComparer.Ordinal);

            public GunGroup Find(string id)
            {
                foreach (GunGroup g in Groups)
                    if (string.Equals(g.Id, id, StringComparison.Ordinal)) return g;
                return null;
            }
        }

        private static Armament SurveyShip(List<MyCubeGrid> ship, long identityId)
        {
            var arms = new Armament();
            var byId = new Dictionary<string, GunGroup>(StringComparer.Ordinal);
            foreach (MyCubeGrid g in ship)
            {
                foreach (MyUserControllableGun gun in g.GetFatBlocks<MyUserControllableGun>())
                {
                    if (!Usable(gun, identityId)) continue;
                    var gunObject = gun as IMyGunObject<MyGunBase>;
                    MyGunBase gunBase = gunObject != null ? gunObject.GunBase : null;
                    MyWeaponDefinition weapon = gunBase != null ? gunBase.WeaponDefinition : null;
                    if (weapon == null || !weapon.HasAmmoMagazines()) continue;
                    MyInventory inventory = gun.GetInventory(0);
                    if (inventory == null) continue;

                    string id = gun.BlockDefinition.Id.ToString();
                    GunGroup group;
                    if (!byId.TryGetValue(id, out group))
                    {
                        group = new GunGroup { Id = id, Name = gun.BlockDefinition.DisplayNameText ?? id };
                        foreach (MyDefinitionId mag in weapon.AmmoMagazinesId)
                        {
                            string key = KeyOf(mag);
                            if (string.IsNullOrEmpty(key)) continue;
                            group.Magazines[key] = mag;
                            arms.Magazines[key] = mag;
                        }
                        byId[id] = group;
                        arms.Groups.Add(group);
                    }
                    group.Guns.Add(new GunSlot { Block = gun, Inventory = inventory, GunBase = gunBase });
                }

                foreach (MyCargoContainer container in g.GetFatBlocks<MyCargoContainer>())
                {
                    if (!Usable(container, identityId)) continue;
                    MyInventory inventory = container.GetInventory(0);
                    if (inventory != null) arms.Cargo.Add(inventory);
                }
            }
            return arms;
        }

        private static long Fits(MyInventory inventory, MyDefinitionId magazine)
        {
            if (inventory == null || !inventory.CheckConstraint(magazine)) return 0;
            long fits = (long)inventory.ComputeAmountThatFits(magazine);
            return fits > 0 ? fits : 0;
        }

        private static double FreeLitres(MyInventory inventory)
        {
            double free = (double)(inventory.MaxVolume - inventory.CurrentVolume) * 1000.0;
            return free > 0 ? free : 0;
        }

        private static double UnitLitres(MyDefinitionId magazine)
        {
            MyPhysicalItemDefinition def = MyDefinitionManager.Static.GetPhysicalItemDefinition(magazine);
            return def != null ? def.Volume * 1000.0 : 0;
        }

        private static string DisplayName(MyDefinitionId magazine)
        {
            MyPhysicalItemDefinition def = MyDefinitionManager.Static.GetPhysicalItemDefinition(magazine);
            return def != null && !string.IsNullOrEmpty(def.DisplayNameText) ? def.DisplayNameText : magazine.SubtypeName;
        }

        private static string KeyOf(MyDefinitionId id)
        {
            return InventoryKeyMapper.FromTypeIdAndSubtype(id.TypeId.ToString(), id.SubtypeName);
        }

        // ---- rearm: survey -----------------------------------------------------------------------

        private void OnRearmSurveyRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId;
            if (!FactionsWire.TryReadRearmSurveyRequest(data, out terminalEntityId, out gridEntityId))
            {
                Log.Warn("Rearm survey request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            byte[] reply;
            try
            {
                reply = BuildSurvey(steamId, terminalEntityId, gridEntityId);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Rearm survey failed for " + steamId + ".");
                reply = FactionsWire.WriteRearmSurvey(terminalEntityId, gridEntityId, false,
                    "The survey failed on the server.", 0, 0, null, null);
            }
            _uplink.Send(steamId, reply);
        }

        private byte[] BuildSurvey(ulong steamId, long terminalEntityId, long gridEntityId)
        {
            DockContext dock;
            string refusal;
            if (!TryResolve(steamId, terminalEntityId, gridEntityId, out dock, out refusal))
                return FactionsWire.WriteRearmSurvey(terminalEntityId, gridEntityId, false, refusal, 0, 0, null, null);

            long identityId = dock.Terminal.IdentityId;
            Armament arms = SurveyShip(dock.Ship, identityId);
            if (arms.Groups.Count == 0)
                return FactionsWire.WriteRearmSurvey(terminalEntityId, gridEntityId, false,
                    NameOf(dock.Grid) + " has no weapons that take ammunition.", 0, 0, null, null);

            var magazines = new List<WireRearmMagazine>();
            foreach (KeyValuePair<string, MyDefinitionId> kv in arms.Magazines)
            {
                DockSupplyOffer offer;
                if (!dock.Offers.TryGetValue(kv.Key, out offer) || offer.Available <= 0) continue;
                magazines.Add(new WireRearmMagazine
                {
                    Key = kv.Key,
                    Name = DisplayName(kv.Value),
                    UnitPrice = offer.UnitPrice,
                    Available = offer.Available,
                    UnitLitres = UnitLitres(kv.Value),
                });
            }

            var groups = new List<WireRearmGroup>();
            foreach (GunGroup group in arms.Groups)
            {
                var options = new List<WireRearmOption>();
                foreach (KeyValuePair<string, MyDefinitionId> mag in group.Magazines)
                {
                    if (!dock.Offers.ContainsKey(mag.Key)) continue;
                    long fits = 0;
                    foreach (GunSlot gun in group.Guns) fits += Fits(gun.Inventory, mag.Value);
                    options.Add(new WireRearmOption { Key = mag.Key, Fits = fits });
                }
                groups.Add(new WireRearmGroup
                {
                    GroupId = group.Id,
                    Name = group.Name,
                    Blocks = group.Guns.Count,
                    CurrentKey = CurrentKey(group),
                    Options = options.ToArray(),
                });
            }

            double cargoFree = 0;
            foreach (MyInventory inventory in arms.Cargo) cargoFree += FreeLitres(inventory);

            return FactionsWire.WriteRearmSurvey(terminalEntityId, gridEntityId, true, "",
                ServicesTerminalHelpers.BalanceOf(identityId), cargoFree,
                magazines.ToArray(), groups.ToArray());
        }

        /// <summary>The magazine most of the group has selected.</summary>
        private static string CurrentKey(GunGroup group)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            string best = "";
            int bestCount = 0;
            foreach (GunSlot gun in group.Guns)
            {
                string key = KeyOf(gun.GunBase.CurrentAmmoMagazineId);
                if (string.IsNullOrEmpty(key)) continue;
                int c;
                counts.TryGetValue(key, out c);
                counts[key] = ++c;
                if (c > bestCount) { best = key; bestCount = c; }
            }
            return best;
        }

        // ---- rearm: commit -----------------------------------------------------------------------

        private void OnRearmRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId, maxCredits;
            WireRearmPick[] picks;
            WireRearmReserve[] reserves;
            if (!FactionsWire.TryReadRearmRequest(data, out terminalEntityId, out gridEntityId, out maxCredits,
                    out picks, out reserves))
            {
                Log.Warn("Rearm request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            bool ok;
            string message;
            long balanceAfter = 0;
            WireReceiptLine[] receipt = null;
            try
            {
                ok = TryRearm(steamId, terminalEntityId, gridEntityId, maxCredits, picks, reserves, out message,
                    out balanceAfter, out receipt);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Rearm failed for " + steamId + ".");
                ok = false;
                message = "The rearm failed on the server. If you were charged, the charge stands only "
                        + "for ammunition that reached your ship.";
            }

            if (!ok)
                Log.Info("DOCK_SUPPLY_REFUSED service=rearm player=" + steamId + " grid=" + gridEntityId
                       + " reason=" + message);
            _uplink.Send(steamId, FactionsWire.WriteRearmResult(ok, message, balanceAfter, receipt));
        }

        private bool TryRearm(ulong steamId, long terminalEntityId, long gridEntityId, long maxCredits,
            WireRearmPick[] picks, WireRearmReserve[] reserves, out string message,
            out long balanceAfter, out WireReceiptLine[] receipt)
        {
            balanceAfter = 0;
            receipt = null;
            DockContext dock;
            if (!TryResolve(steamId, terminalEntityId, gridEntityId, out dock, out message)) return false;

            long identityId = dock.Terminal.IdentityId;
            Armament arms = SurveyShip(dock.Ship, identityId);

            // Sinks in delivery order: every picked gun first, then the cargo reserve, so a thin
            // station loads the weapons before it stocks the hold.
            var sinks = new List<DockSupplySink>();
            var targets = new List<MyInventory>();
            var targetMags = new List<MyDefinitionId>();
            int guns = 0;

            if (picks != null)
            {
                foreach (WireRearmPick pick in picks)
                {
                    GunGroup group = arms.Find(pick.GroupId);
                    MyDefinitionId magazine;
                    if (group == null || string.IsNullOrEmpty(pick.Key)
                        || !group.Magazines.TryGetValue(pick.Key, out magazine)) continue;
                    foreach (GunSlot gun in group.Guns)
                    {
                        long fits = Fits(gun.Inventory, magazine);
                        if (fits <= 0) continue;
                        sinks.Add(new DockSupplySink { Slot = targets.Count, InvKey = pick.Key, Capacity = fits });
                        targets.Add(gun.Inventory);
                        targetMags.Add(magazine);
                        guns++;
                    }
                }
            }
            int gunSinks = sinks.Count;

            if (reserves != null && reserves.Length > 0 && arms.Cargo.Count > 0)
            {
                var requests = new List<DockReserveRequest>();
                foreach (WireRearmReserve reserve in reserves)
                {
                    MyDefinitionId magazine;
                    if (reserve.Count <= 0 || string.IsNullOrEmpty(reserve.Key)
                        || !arms.Magazines.TryGetValue(reserve.Key, out magazine)) continue;
                    requests.Add(new DockReserveRequest
                    {
                        InvKey = reserve.Key,
                        Count = Math.Min(reserve.Count, MaxReserveCount),
                        UnitLitres = UnitLitres(magazine),
                    });
                }
                var free = new List<double>(arms.Cargo.Count);
                foreach (MyInventory inventory in arms.Cargo) free.Add(FreeLitres(inventory));
                foreach (DockSupplySink cargoSink in DockSupply.PlanCargoReserve(free, requests))
                {
                    MyInventory inventory = arms.Cargo[cargoSink.Slot];
                    MyDefinitionId magazine = arms.Magazines[cargoSink.InvKey];
                    if (!inventory.CheckConstraint(magazine)) continue;
                    sinks.Add(new DockSupplySink { Slot = targets.Count, InvKey = cargoSink.InvKey, Capacity = cargoSink.Capacity });
                    targets.Add(inventory);
                    targetMags.Add(magazine);
                }
            }

            if (sinks.Count == 0)
            {
                message = "Nothing to load: the selected guns are full and no cargo reserve fits.";
                return false;
            }

            long balance = ServicesTerminalHelpers.BalanceOf(identityId);
            long budget = Math.Min(balance, Math.Max(0, maxCredits));
            DockSupplyPlan plan = DockSupply.Plan(sinks, dock.Offers, budget);
            if (plan.TotalUnits <= 0)
            {
                message = EmptyPlanReason(plan.Limit, "selected guns", "ammunition", null, true);
                return false;
            }

            if (!MyBankingSystem.ChangeBalance(identityId, -plan.Credits))
            {
                message = "Your account refused the charge of " + Num(plan.Credits) + " credits. Nothing was delivered.";
                return false;
            }

            var delivered = new Dictionary<string, long>(StringComparer.Ordinal);
            var loadedByKey = new Dictionary<string, long>(StringComparer.Ordinal);
            var cargoByKey = new Dictionary<string, long>(StringComparer.Ordinal);
            long intoGuns = 0, intoCargo = 0;
            for (int i = 0; i < sinks.Count; i++)
            {
                long want = plan.Units[i];
                if (want <= 0) continue;
                long landed = AddMagazines(targets[sinks[i].Slot], targetMags[sinks[i].Slot], want);
                if (landed <= 0) continue;
                long d;
                delivered.TryGetValue(sinks[i].InvKey, out d);
                delivered[sinks[i].InvKey] = d + landed;
                Dictionary<string, long> split = i < gunSinks ? loadedByKey : cargoByKey;
                long sd;
                split.TryGetValue(sinks[i].InvKey, out sd);
                split[sinks[i].InvKey] = sd + landed;
                if (i < gunSinks) intoGuns += landed;
                else intoCargo += landed;
            }

            // Price what landed, per key, exactly as the plan priced what was asked for; refund the
            // difference. Equal unless an inventory refused items it said a moment ago it would take.
            long charged = 0;
            var lines = new List<WireReceiptLine>();
            foreach (KeyValuePair<string, long> kv in delivered)
            {
                long price = dock.Offers[kv.Key].UnitPrice;
                long lineCredits = DockSupply.CreditsFor(kv.Key, kv.Value, price);
                charged += lineCredits;
                long loaded, cargo;
                loadedByKey.TryGetValue(kv.Key, out loaded);
                cargoByKey.TryGetValue(kv.Key, out cargo);
                MyDefinitionId magazine;
                lines.Add(new WireReceiptLine
                {
                    Key = kv.Key,
                    Name = arms.Magazines.TryGetValue(kv.Key, out magazine) ? DisplayName(magazine) : kv.Key,
                    Loaded = loaded,
                    Cargo = cargo,
                    UnitPrice = price,
                    Credits = lineCredits,
                });
            }
            long refund = plan.Credits - charged;
            if (refund > 0 && !MyBankingSystem.ChangeBalance(identityId, refund))
                Log.Warn("DOCK_SUPPLY refund of " + refund + " to player " + identityId + " was refused.");

            if (intoGuns + intoCargo <= 0)
            {
                message = "None of the magazines would fit after all. Your credits were refunded.";
                return false;
            }

            _store.HandleDockSupplySettled(dock.Terminal.Node, identityId, "rearm", delivered, charged);

            // The receipt carries the per-item split; the message is only what the table cannot say.
            var text = new StringBuilder();
            text.Append("Rearmed ").Append(NameOf(dock.Grid)).Append('.');
            // Name each magazine the station ran out of: a total that came up short says nothing
            // about which line of the order it cut.
            var requested = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (DockSupplySink sink in sinks)
            {
                long r;
                requested.TryGetValue(sink.InvKey, out r);
                requested[sink.InvKey] = r + sink.Capacity;
            }
            foreach (KeyValuePair<string, long> kv in requested)
            {
                long planned;
                plan.UnitsByKey.TryGetValue(kv.Key, out planned);
                DockSupplyOffer offer;
                if (planned >= kv.Value || !dock.Offers.TryGetValue(kv.Key, out offer) || planned < offer.Available) continue;
                MyDefinitionId magazine;
                string name = arms.Magazines.TryGetValue(kv.Key, out magazine) ? DisplayName(magazine) : kv.Key;
                text.AppendLine().Append(name).Append(": the station had only ").Append(Num(offer.Available))
                    .Append(" of the ").Append(Num(kv.Value)).Append(" asked for, guns first.");
            }
            if (plan.Limit == DockSupplyLimit.Funds)
                text.AppendLine().Append("That is as much as your credits cover.");
            if (refund > 0) text.AppendLine().Append(Num(refund)).Append(" credits refunded for magazines that did not fit.");
            message = text.ToString();
            balanceAfter = ServicesTerminalHelpers.BalanceOf(identityId);
            receipt = lines.ToArray();
            return true;
        }

        /// <summary>Adds whole magazines, falling back to what still fits when the first attempt is
        /// refused. Returns what landed.</summary>
        private static long AddMagazines(MyInventory inventory, MyDefinitionId magazine, long count)
        {
            if (count <= 0) return 0;
            var builder = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine);
            if (inventory.AddItems((MyFixedPoint)(double)count, builder)) return count;
            long fits = Math.Min(count, Fits(inventory, magazine));
            if (fits <= 0) return 0;
            builder = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine);
            return inventory.AddItems((MyFixedPoint)(double)fits, builder) ? fits : 0;
        }

        // ---- text ---------------------------------------------------------------------------------

        /// <summary>Why a plan came out empty, in the player's terms.</summary>
        /// <param name="sinkName">What was full: "hydrogen tanks", "selected guns".</param>
        /// <param name="noun">What was being bought: "hydrogen", "ammunition".</param>
        /// <param name="price">The unit price, for the can't-afford line; null to omit it.</param>
        private static string EmptyPlanReason(DockSupplyLimit limit, string sinkName, string noun,
            string price, bool commit)
        {
            switch (limit)
            {
                case DockSupplyLimit.NoSpace:
                    return "Your " + sinkName + " are already full.";
                case DockSupplyLimit.Funds:
                    return commit
                        ? "Neither your balance nor the total you confirmed covers any " + noun
                          + " now. Ask again for a new price."
                        : "You cannot afford any " + noun + " here" + (price != null ? " at " + price : "") + ".";
                default:
                    return "This station has no " + noun + " to spare right now.";
            }
        }

        private static string LimitNote(DockSupplyLimit limit, string goods)
        {
            switch (limit)
            {
                case DockSupplyLimit.Stock:
                    return "That is all the " + goods + " this station can spare.";
                case DockSupplyLimit.Funds:
                    return "That is as much as your credits cover.";
                default:
                    return null;
            }
        }

        private static string PriceText(string invKey, long unitPrice)
        {
            return InventoryKeyMapper.IsGasKey(invKey)
                ? Num(unitPrice) + " credits per 1,000 L"
                : Num(unitPrice) + " credits each";
        }

        private static string NameOf(MyCubeGrid grid)
        {
            return grid == null || string.IsNullOrEmpty(grid.DisplayName) ? "that ship" : grid.DisplayName;
        }

        private static string Num(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
