using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Sandbox.Game.Entities;    // MyCubeGrid, MyCubeBlock
using VRageMath;                // Color

namespace Factions
{
    /// <summary>
    /// A materialised station's container loot, host half (rules in Core's <see cref="StationLoot"/>,
    /// SE work in <see cref="StationLootLoader"/>). When a station stands, each block its design tags
    /// with a vanilla loot table is cleared, rolled and filled from the node's surplus; when it
    /// despawns, what the blocks still hold goes back to stock and what players took stays gone.
    /// Taking anything starts <see cref="StationLoot.RestockCooldown"/>, during which the station
    /// stands with empty blocks; players near it are told which state it stood in. Vanilla's free
    /// container fill is held off by <see cref="StationTurretRefillPatch"/>.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private readonly IRandom _lootRng = new SeededRandom(Environment.TickCount);
        private readonly List<KeyValuePair<MyCubeBlock, string>> _lootBlocks = new List<KeyValuePair<MyCubeBlock, string>>();

        private void ConfigureStationLoot()
        {
            _stationMaterializer.LootFromStock = () => FeatureSwitch.IsOn(FeatureSwitch.StationLoot);
            _stationMaterializer.LootStood = OnStationLootStood;
            _stationMaterializer.LootClosed = OnStationLootClosed;
        }

        /// <summary>Hands back the loot every station grid held when the server last stopped — none of
        /// them is saved. Call once, after load, before any station stands.</summary>
        public void CreditStrandedStationLoot()
        {
            if (_world == null) return;
            int nodes;
            long credited = StationLoot.CreditAfterRestart(_world, out nodes);
            if (credited > 0)
                FactionsPlugin.Log.Info("Station loot: credited " + credited.ToString(CultureInfo.InvariantCulture)
                    + " unit(s) back to " + nodes.ToString(CultureInfo.InvariantCulture)
                    + " node(s) from station grids that did not survive the restart.");
        }

        private string OnStationLootStood(string nodeId, List<MyCubeGrid> grids, bool gunsFromStock)
        {
            SpatialNode node;
            if (_world == null || nodeId == null || !_world.Graph.Nodes.TryGetValue(nodeId, out node)) return null;

            long stale = StationLoot.CreditStale(_world, node);
            StationLootLoader.LootBlocks(grids, _lootBlocks);
            TimeSpan left;
            bool restocking = StationLoot.Restocking(node, _world.UtcNow, out left);
            long returned, cleared;
            long placed = StationLootLoader.Stand(_world, node, grids, _lootBlocks, gunsFromStock, !restocking, _lootRng,
                                                  out returned, out cleared);
            int blocks = _lootBlocks.Count;
            _lootBlocks.Clear();
            if (blocks == 0 && stale == 0) return null;

            if (blocks > 0)
            {
                string stationName = grids != null && grids.Count > 0 && grids[0] != null ? grids[0].DisplayName : null;
                TellStationObservers(node, StationLoot.SpawnNotice(stationName, node, _world.UtcNow, placed));
            }

            return "loot=" + placed.ToString(CultureInfo.InvariantCulture)
                 + " loot_blocks=" + blocks.ToString(CultureInfo.InvariantCulture)
                 + (restocking ? " loot_restock_in=" + Math.Ceiling(left.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m" : "")
                 + (cleared > 0 ? " loot_cleared=" + cleared.ToString(CultureInfo.InvariantCulture) : "")
                 + (returned > 0 ? " loot_unplaced=" + returned.ToString(CultureInfo.InvariantCulture) : "")
                 + (stale > 0 ? " loot_stale_back=" + stale.ToString(CultureInfo.InvariantCulture) : "");
        }

        /// <summary>Whispers every online player within <see cref="FactionTuning.ObservationRadiusKm"/>
        /// of the node — the players whose approach stood the station.</summary>
        private void TellStationObservers(SpatialNode node, string message)
        {
            if (_chat == null || _playersWithin == null || message == null) return;
            foreach (ulong steamId in _playersWithin(node.Position, _tuning.ObservationRadiusKm))
            {
                try { _chat.SendMessageAsOther(FleetWhisperAuthor, message, Color.CornflowerBlue, steamId); }
                catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Station loot notice to " + steamId + " failed."); }
            }
        }

        private string OnStationLootClosed(string nodeId, Dictionary<string, long> observed)
        {
            SpatialNode node;
            if (_world == null || nodeId == null || !_world.Graph.Nodes.TryGetValue(nodeId, out node)) return null;
            if (node.StationLoot == null) return null;
            long taken;
            long credited = StationLoot.Credit(_world, node, observed, out taken);
            return "loot_back=" + credited.ToString(CultureInfo.InvariantCulture)
                 + " loot_taken=" + taken.ToString(CultureInfo.InvariantCulture)
                 + (taken > 0 ? " loot_restock_utc=" + node.LootRestockUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "");
        }
    }
}
