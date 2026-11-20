using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using Factions.Wire;
using HarmonyLib;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.EntityComponents.Blocks;
using SpaceEngineers.Game.SessionComponents;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Server side of the Services Terminal's ship-asset surface: the faction's hulls listed beside
    /// vanilla's stored grids, a withdrawal that converts one of them into a vanilla stored grid,
    /// and the two buttons on the Active Grids tab that send a live hull to a node or scan it into a
    /// new prefab.
    ///
    /// <para><b>Why the conversion and not a second queue.</b> A withdrawal does not spawn the ship.
    /// It spawns a transient clone far from anybody, hands it to <see cref="MyGridStorageHelper"/>
    /// exactly as vanilla's own deposit does, closes the clone, and files the resulting entry under
    /// the owning node's position. Everything the player then sees — the countdown, its distance
    /// term, Expedite, Cancel, Deploy — is vanilla's, unmodified. Re-implementing that formula and
    /// its Enroute panel was the alternative and is the thing this design exists to avoid.</para>
    ///
    /// <para><b>Every request is re-authorised.</b> The client sends an entity id and nothing else
    /// that matters. This resolves the sender's SteamID to an identity, that identity to an SE
    /// faction, that faction to a <see cref="FactionState"/> that must be a player faction, the
    /// entity id to a Services Terminal block, its grid to an SE station, and that station to a node
    /// this faction owns — and refuses with a logged reason at any step. A client-side hide is not a
    /// gate; this is.</para>
    ///
    /// <para>Provenance: FAC-283. The terminal helpers it reuses are FAC-280's.</para>
    /// </summary>
    internal sealed class PlayerShipAssetsManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Vanilla's own designated area for the Active Grids tab
        /// (<c>MyGridsStorageEntityComponent.GetGridsInDesignatedArea</c>, a 200 m sphere around the
        /// block). Send and Scan use it so the buttons can never reach a hull the tab did not
        /// list.</summary>
        private const double DesignatedAreaM = 200.0;

        /// <summary>How far a spawn probe walks out from the terminal looking for empty space, and
        /// the radius that has to be empty. The clone exists for one call and is closed in the same
        /// frame, but it is a real entity while it exists, so it is put somewhere nothing can be
        /// standing.</summary>
        private const double SpawnProbeStartM = 8000.0;
        private const double SpawnProbeStepM = 4000.0;
        private const double SpawnProbeMaxM = 60000.0;
        private const double SpawnClearRadiusM = 1000.0;

        /// <summary>Vanilla's <c>ValidateGrid</c>, resolved once. Private static on the entity
        /// component, and reused rather than re-derived: moving, in combat, static, linked, over
        /// limits, not owned, hostile blocks are its rules, and a second implementation of them
        /// would drift from the one the player meets on the Deposit button one tab over.</summary>
        private static MethodInfo _validateGrid;
        private static bool _validateGridResolved;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;
        private readonly Action<string> _recordActivity;

        /// <summary>Block/PCU per prefab, resolved from its object builders on first use. A prefab's
        /// blocks do not change within a session, and the snapshot is sent per terminal open.</summary>
        private readonly Dictionary<string, PrefabStats> _stats =
            new Dictionary<string, PrefabStats>(StringComparer.OrdinalIgnoreCase);

        private struct PrefabStats
        {
            public int Blocks;
            public int Pcu;
            public string GridSize;
        }

        public PlayerShipAssetsManager(FactionRuntime runtime, UplinkManager uplink, Action<string> recordActivity)
        {
            _runtime = runtime;
            _uplink = uplink;
            _recordActivity = recordActivity;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.TerminalAssetsRequest, OnAssetsRequest);
            _uplink.RegisterHandler(WireMessageKind.ShipWithdrawRequest, OnWithdrawRequest);
            _uplink.RegisterHandler(WireMessageKind.ShipSendRequest, OnSendRequest);
            _uplink.RegisterHandler(WireMessageKind.ShipScanRequest, OnScanRequest);
        }

        public void Close()
        {
            _stats.Clear();
            // A session ending mid-wait must not leave the clones behind, and must not keep the
            // hull it never delivered: the runtime flush that persists the node runs after this,
            // so the return reaches disk.
            for (int i = 0; i < _pending.Count; i++)
            {
                CloseClone(_pending[i]);
                ReturnHull(_pending[i]);
            }
            _pending.Clear();
        }

        // ---- authorisation ----------------------------------------------------------------------

        /// <summary>Resolves and checks everything a request needs, from the sender's SteamID down.
        /// Lives in <see cref="TerminalAccess"/> because the station-refit surface asks the same
        /// question of the same block — a second implementation of "is this caller standing at a
        /// Services Terminal on a node their faction owns" is a second place for the two to
        /// disagree about who may act at a station.</summary>
        private bool TryAuthorise(ulong steamId, long terminalEntityId, out TerminalContext ctx, out string refusal)
        {
            return TerminalAccess.TryAuthorise(_runtime, steamId, terminalEntityId, out ctx, out refusal);
        }

        /// <summary>Whether this client was granted the ship surfaces at link time. Checked on every
        /// request rather than trusted from the client's own dispatch table: the feature has its own
        /// bit precisely so a server can withhold it, and a withheld feature that still answers is
        /// not withheld.</summary>
        private bool Granted(ulong steamId)
        {
            return _uplink != null && (_uplink.GrantedFeatures(steamId) & WireFeatures.ShipAssets) != 0;
        }

        // ---- snapshot ---------------------------------------------------------------------------

        private void OnAssetsRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId;
            if (!FactionsWire.TryReadTerminalAssetsRequest(data, out terminalEntityId))
            {
                FactionsPlugin.Log.Warn("Terminal assets request from " + steamId + " was unreadable.");
                return;
            }

            try
            {
                TerminalContext ctx;
                string refusal;
                if (!TryAuthorise(steamId, terminalEntityId, out ctx, out refusal))
                {
                    // No log line: every player who opens any terminal reaches this, and the client
                    // needs the reason only to decide whether to render anything at all.
                    _uplink.Send(steamId, FactionsWire.WriteTerminalAssetsSnapshot(
                        terminalEntityId, "", "", false, refusal, null));
                    return;
                }

                // mayUse is the question both Services Terminal surfaces share -- is this caller at
                // a terminal on a node their faction owns -- so it is answered for a client granted
                // either bit. The rows are this feature's own: a client without ShipAssets is told
                // it may use the terminal and sent none.
                WireShipAssetRow[] rows = Granted(steamId) ? DescribeRows(ctx) : null;
                _uplink.Send(steamId, FactionsWire.WriteTerminalAssetsSnapshot(
                    terminalEntityId, ctx.Node.Id, ctx.Node.DisplayName ?? "", true, "", rows));
                if (UplinkManager.DebugTraffic)
                {
                    _uplink.WriteTraffic("TERMINAL_ASSETS steam=" + steamId + " node=" + ctx.Node.Id
                                         + " rows=" + (rows == null ? 0 : rows.Length));
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Terminal assets snapshot failed for " + steamId + ".");
                _uplink.Send(steamId, FactionsWire.WriteTerminalAssetsSnapshot(
                    terminalEntityId, "", "", false, "The server could not read your faction's ships.", null));
            }
        }

        /// <summary>One row per (node, prefab) with a hull to spare, across every node the faction
        /// owns rather than only the terminal's — an asset at another station is exactly the case
        /// vanilla's distance term exists for, and hiding it would leave the player no way to move
        /// it.</summary>
        private WireShipAssetRow[] DescribeRows(TerminalContext ctx)
        {
            Vector3D terminal = ctx.Block.PositionComp.GetPosition();
            var rows = new List<WireShipAssetRow>();
            foreach (SpatialNode node in _runtime.World.Graph.OwnedBy(ctx.Faction.Tag))
            {
                if (node == null || node.ShipStock.Count == 0) continue;
                List<ShipAssetOffer> offers = ShipAssetRules.WithdrawableAt(node.ShipStock, node.ListedForSale);
                if (offers.Count == 0) continue;

                double distanceKm = Vector3D.Distance(terminal, WorldPositionOf(node)) / 1000.0;
                foreach (ShipAssetOffer offer in offers)
                {
                    PrefabStats stats = StatsFor(offer.PrefabId);
                    rows.Add(new WireShipAssetRow
                    {
                        NodeId = node.Id,
                        NodeName = node.DisplayName ?? "",
                        PrefabId = offer.PrefabId,
                        Count = offer.Count,
                        Blocks = stats.Blocks,
                        Pcu = stats.Pcu,
                        GridSize = stats.GridSize ?? "",
                        DistanceKm = distanceKm,
                    });
                }
            }
            return rows.Count == 0 ? null : rows.ToArray();
        }

        private static Vector3D WorldPositionOf(SpatialNode node)
        {
            return new Vector3D(node.Position.X * 1000.0, node.Position.Y * 1000.0, node.Position.Z * 1000.0);
        }

        /// <summary>Block and PCU counts for one prefab, through the same
        /// <see cref="MyBlockLimits.GetBlockLimits"/> vanilla's own stored-grid details use — so the
        /// synthetic row's two columns are computed the way the vanilla rows beside it were. Zeroes
        /// when the prefab cannot be resolved, which the wire documents as "the catalogue does not
        /// know them" rather than as a real zero.</summary>
        private PrefabStats StatsFor(string prefabId)
        {
            PrefabStats cached;
            if (_stats.TryGetValue(prefabId, out cached)) return cached;

            var stats = new PrefabStats { GridSize = "" };
            SePrefabCatalogue catalogue = _runtime.Catalogue;
            if (catalogue != null) stats.GridSize = catalogue.GridSize(prefabId);

            MyObjectBuilder_CubeGrid[] grids;
            if (TryResolvePrefabGrids(prefabId, out grids))
            {
                try
                {
                    var limits = new Dictionary<string, int>();
                    int pcu, totalBlocks;
                    bool overLimits;
                    MyBlockLimits.GetBlockLimits(grids, limits, out pcu, out totalBlocks, out overLimits);
                    stats.Blocks = totalBlocks;
                    stats.Pcu = pcu;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Could not measure prefab '" + prefabId + "' for the terminal listing.");
                }
            }

            _stats[prefabId] = stats;
            return stats;
        }

        /// <summary>A prefab's object builders from whichever of the two sources holds it —
        /// captures first, then SE's own definitions. The same order
        /// <c>VanillaFleetSpawner.DoSpawn</c> uses, because a capture is never an SE prefab
        /// definition and would otherwise be unresolvable.</summary>
        private bool TryResolvePrefabGrids(string prefabId, out MyObjectBuilder_CubeGrid[] grids)
        {
            grids = null;
            if (string.IsNullOrEmpty(prefabId)) return false;

            CustomPrefabStore captured = _runtime.CapturedPrefabs;
            if (captured != null && captured.TryGetGrids(prefabId, out grids) && grids != null && grids.Length > 0)
                return true;

            if (MyDefinitionManager.Static == null) { grids = null; return false; }
            MyPrefabDefinition def = null;
            try { def = MyDefinitionManager.Static.GetPrefabDefinition(prefabId); }
            catch { def = null; }
            if (def == null || def.CubeGrids == null || def.CubeGrids.Length == 0) { grids = null; return false; }
            grids = def.CubeGrids;
            return true;
        }

        // ---- withdraw ---------------------------------------------------------------------------

        private void OnWithdrawRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId;
            string nodeId, prefabId;
            if (!FactionsWire.TryReadShipWithdrawRequest(data, out terminalEntityId, out nodeId, out prefabId))
            {
                FactionsPlugin.Log.Warn("Ship withdraw request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            string message;
            try
            {
                // True means the clone is in the world: Tick owns the reply from here, and this
                // request is answered only once the blueprint has been copied off it.
                if (TryWithdraw(steamId, terminalEntityId, nodeId, prefabId, out message)) return;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Ship withdrawal failed for " + steamId + ".");
                message = "The withdrawal failed on the server; nothing was taken from the node.";
            }

            // Acceptance check 2 turns on this line: a hand-crafted request against a terminal
            // the caller has no claim to must leave the reason behind, not merely fail quietly.
            Log.Info("SHIP_WITHDRAW_REFUSED node=" + (nodeId ?? "") + " player=" + steamId
                   + " prefab=" + (prefabId ?? "") + " reason=" + message);
            _uplink.Send(steamId, FactionsWire.WriteShipWithdrawResult(false, Guid.Empty, message));
        }

        /// <summary>
        /// Stages the withdrawal: everything that can refuse it, then the clone, and nothing else.
        /// The blueprint is copied off that clone a few frames later by <see cref="Tick"/>.
        ///
        /// <para><b>Why the copy cannot happen here.</b> SE wires a mechanical joint in
        /// <c>MyMechanicalConnectionBlockBase.UpdateOnceBeforeFrame</c>, which runs the frame
        /// <i>after</i> a grid reaches the world. <c>StoreGrid</c> copies the root's physical group,
        /// so a copy taken in this call sees a group of one and stores a hull with every turret,
        /// arm and docked craft missing — measured on a 17-grid destroyer as 1 grid and 3806 of
        /// 4566 blocks.</para>
        ///
        /// <para>The hull leaves <see cref="SpatialNode.ShipStock"/> here rather than on completion,
        /// so a second pull arriving while this one is staged cannot pass the stock check on a hull
        /// already spoken for. Every path that then fails puts it back.</para>
        /// </summary>
        /// <returns>True when the clone is staged and <see cref="Tick"/> now owns the reply; false
        /// with <paramref name="message"/> set when the withdrawal was refused outright.</returns>
        private bool TryWithdraw(ulong steamId, long terminalEntityId, string nodeId, string prefabId,
                                 out string message)
        {
            TerminalContext ctx;
            if (!TryAuthorise(steamId, terminalEntityId, out ctx, out message)) return false;

            if (string.IsNullOrEmpty(prefabId)) { message = "Pick a ship to withdraw."; return false; }

            SpatialNode holder;
            if (string.IsNullOrEmpty(nodeId)
                || !_runtime.World.Graph.Nodes.TryGetValue(nodeId, out holder) || holder == null)
            {
                message = "That station no longer exists.";
                return false;
            }
            if (!string.Equals(holder.OwnerFactionTag, ctx.Faction.Tag, StringComparison.OrdinalIgnoreCase))
            {
                message = "Your faction does not own " + nodeId + ".";
                return false;
            }

            int stock, listed = 0;
            holder.ShipStock.TryGetValue(prefabId, out stock);
            holder.ListedForSale.TryGetValue(prefabId, out listed);
            if (ShipAssetRules.WithdrawableCount(stock, listed) <= 0)
            {
                message = "No " + prefabId + " is free to withdraw at " + NodeLabel(holder) + ".";
                return false;
            }

            MyGridsStorageSessionComponent session = GridStorage();
            if (session == null)
            {
                message = "Grid storage is switched off on this server, so there is nowhere to put it.";
                return false;
            }

            MyObjectBuilder_CubeGrid[] template;
            if (!TryResolvePrefabGrids(prefabId, out template))
            {
                message = "'" + prefabId + "' is no longer a prefab this server can build.";
                return false;
            }

            // The clone is never where anybody is: the probe wants a clear kilometre before it will
            // use a point.
            Vector3D spawnAt;
            if (!TryFindClearSpawn(ctx.Block.PositionComp.GetPosition(), out spawnAt))
            {
                message = "Could not find clear space to prepare the ship. Try again shortly.";
                return false;
            }

            List<MyCubeGrid> spawned;
            if (!CustomPrefabSpawner.TrySpawn(template, spawnAt, ctx.IdentityId, out spawned)
                || spawned == null || spawned.Count == 0)
            {
                message = "The server could not prepare that ship.";
                return false;
            }

            // A clone now outlives the frame that made it, so it has to be kept out of the world
            // save: an autosave landing mid-wait would otherwise persist a grid this manager closes
            // moments later.
            foreach (MyCubeGrid grid in spawned)
                if (grid != null) grid.Save = false;

            int remaining = stock - 1;
            if (remaining > 0) holder.ShipStock[prefabId] = remaining;
            else holder.ShipStock.Remove(prefabId);

            _pending.Add(new PendingWithdrawal
            {
                SteamId = steamId,
                IdentityId = ctx.IdentityId,
                NodeId = holder.Id,
                FactionTag = holder.OwnerFactionTag,
                HolderLabel = NodeLabel(holder),
                HolderPosition = WorldPositionOf(holder),
                PrefabId = prefabId,
                TemplateGrids = template.Length,
                Spawned = spawned,
                Remaining = remaining,
            });

            return true;
        }

        // ---- staged withdrawals -----------------------------------------------------------------

        /// <summary>A withdrawal whose clone is in the world, waiting for SE to attach its subgrids
        /// before the blueprint is copied off it.</summary>
        private sealed class PendingWithdrawal
        {
            public ulong SteamId;
            public long IdentityId;
            public string NodeId;
            public string FactionTag;
            public string HolderLabel;
            public Vector3D HolderPosition;
            public string PrefabId;
            public int TemplateGrids;
            public List<MyCubeGrid> Spawned;
            public int Remaining;
            public int FramesWaited;
            public int LastGroupSize;
            public int StableFrames;
        }

        /// <summary>How long a staged clone is given to finish attaching, and how long the group
        /// has to stop changing before it counts as settled.
        ///
        /// <para>Reaching the template's grid count ends the wait immediately, and that is the usual
        /// exit. The settle window is for the two cases where it never will: a merge block fuses two
        /// of the prefab's grids into one, so the whole ship is a group <i>smaller</i> than the
        /// template; and a prefab carrying a craft nothing joins to the hull, which is stored short
        /// — the same way vanilla's own Deposit button stores a ship short of an undocked craft
        /// parked beside it.</para></summary>
        private const int AttachWaitMaxFrames = 120;
        private const int AttachSettleFrames = 10;

        private readonly List<PendingWithdrawal> _pending = new List<PendingWithdrawal>();

        /// <summary>Advances every staged withdrawal by one frame. Called from the plugin's update.
        /// Returns immediately when nothing is staged, which is almost always.</summary>
        public void Tick()
        {
            if (_pending.Count == 0) return;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingWithdrawal pending = _pending[i];
                bool finished;
                try
                {
                    finished = AdvanceWithdrawal(pending);
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Staged withdrawal failed for " + pending.SteamId + ".");
                    CloseClone(pending);
                    ReturnHull(pending);
                    Refuse(pending, "The withdrawal failed on the server; nothing was taken from the node.");
                    finished = true;
                }
                if (finished) _pending.RemoveAt(i);
            }
        }

        /// <summary>One frame of one staged withdrawal. True when it is done with, either way.</summary>
        private bool AdvanceWithdrawal(PendingWithdrawal pending)
        {
            MyCubeGrid root = pending.Spawned.Count > 0 ? pending.Spawned[0] : null;
            if (root == null || root.Closed || root.MarkedForClose)
            {
                CloseClone(pending);
                ReturnHull(pending);
                Refuse(pending, "The server lost the ship while preparing it; nothing was taken from the node.");
                return true;
            }

            pending.FramesWaited++;
            int attached = PhysicalGroupSize(root);
            if (attached == pending.LastGroupSize) pending.StableFrames++;
            else { pending.LastGroupSize = attached; pending.StableFrames = 0; }

            if (attached < pending.TemplateGrids
                && pending.StableFrames < AttachSettleFrames
                && pending.FramesWaited < AttachWaitMaxFrames) return false;

            return CompleteWithdrawal(pending, attached);
        }

        /// <summary>Grids in the root's physical group — what <c>StoreGrid</c> will copy. One when
        /// nothing has attached yet, which is the state this whole wait exists to leave.</summary>
        private static int PhysicalGroupSize(MyCubeGrid root)
        {
            List<MyCubeGrid> nodes = MyCubeGridGroups.Static.Physical.GetGroupNodes(root);
            return nodes == null || nodes.Count == 0 ? 1 : nodes.Count;
        }

        /// <summary>Copies the blueprint off the clone, files it under the holding node and answers
        /// the client. The clone is closed whatever happens.</summary>
        private bool CompleteWithdrawal(PendingWithdrawal pending, int attached)
        {
            MyGridsStorageSessionComponent session = GridStorage();
            if (session == null)
            {
                CloseClone(pending);
                ReturnHull(pending);
                Refuse(pending, "Grid storage is switched off on this server, so there is nowhere to put it.");
                return true;
            }

            Guid? storedId = null;
            MyObjectBuilder_SavedGridDetails details = null;
            try
            {
                var result = MyGridStorageHelper.Instance.StoreGrid(
                    pending.Spawned[0], pending.PrefabId, pending.SteamId);
                storedId = result.Item1;
                details = result.Item2;
            }
            finally
            {
                // Always: a clone left in the world would be a free ship in deep space.
                CloseClone(pending);
            }

            if (!storedId.HasValue)
            {
                ReturnHull(pending);
                Refuse(pending, "Grid storage refused the ship; nothing was taken from the node.");
                return true;
            }

            // StorageEntityPosition is the HOLDING node, not this terminal -- it is the whole reason
            // an off-site hull costs vanilla's distance term. RelativePosition/Orientation stay
            // neutral: vanilla applies them to whichever terminal finally deploys the grid, with a
            // 200 m placement tolerance around the result, and a stored offset from a terminal that
            // never held the ship would mean nothing there.
            session.GetStoredGridsData()[storedId.Value] = new MyStoredGridData
            {
                RelativePosition = Vector3.Zero,
                Orientation = Quaternion.Identity,
                StorageEntityPosition = pending.HolderPosition,
                OwnerId = pending.IdentityId,
                // Fully qualified: Sandbox.ModAPI declares an enum of the same name, and this is the
                // one MyStoredGridData's field is typed as. Vanilla's own StoreRequest qualifies it
                // for the same reason.
                OwnershipKind = Sandbox.Common.ObjectBuilders.MyStoredGridOwnershipKind.FactionShare,
                SavedGridDetails = details,
                InTransition = false,
            };

            if (_recordActivity != null) _recordActivity(pending.NodeId);

            Log.Info("SHIP_WITHDRAWN node=" + pending.NodeId + " faction=" + (pending.FactionTag ?? "")
                   + " player=" + pending.IdentityId + " prefab=" + pending.PrefabId
                   + " guid=" + storedId.Value + " remaining=" + pending.Remaining
                   + " grids=" + attached + "/" + pending.TemplateGrids
                   + " frames=" + pending.FramesWaited);

            _uplink.Send(pending.SteamId, FactionsWire.WriteShipWithdrawResult(
                true, storedId.Value,
                pending.PrefabId + " is in storage at " + pending.HolderLabel + ". Pull it to bring it here."));
            return true;
        }

        /// <summary>Closes every grid the clone became. The spawn list is not enough on its own now
        /// that the clone lives long enough to be wired together: anything SE attached to it during
        /// the wait is folded in first, because a grid left behind is a free ship in deep space.
        /// Idempotent, so the failure paths may call it twice.</summary>
        private static void CloseClone(PendingWithdrawal pending)
        {
            MyCubeGrid root = pending.Spawned.Count > 0 ? pending.Spawned[0] : null;
            if (root != null && !root.Closed && !root.MarkedForClose)
            {
                List<MyCubeGrid> group = MyCubeGridGroups.Static.Physical.GetGroupNodes(root);
                if (group != null)
                    foreach (MyCubeGrid grid in group)
                        if (grid != null && !pending.Spawned.Contains(grid)) pending.Spawned.Add(grid);
            }

            foreach (MyCubeGrid grid in pending.Spawned)
            {
                if (grid == null || grid.MarkedForClose || grid.Closed) continue;
                grid.Close();
            }
        }

        /// <summary>Puts a staged hull back into the node that lent it. Silent when the node is gone
        /// — so is everything else it held.</summary>
        private void ReturnHull(PendingWithdrawal pending)
        {
            SpatialNode holder;
            if (!_runtime.World.Graph.Nodes.TryGetValue(pending.NodeId, out holder) || holder == null) return;
            int stock;
            holder.ShipStock.TryGetValue(pending.PrefabId, out stock);
            holder.ShipStock[pending.PrefabId] = stock + 1;
        }

        private void Refuse(PendingWithdrawal pending, string message)
        {
            Log.Info("SHIP_WITHDRAW_REFUSED node=" + pending.NodeId + " player=" + pending.SteamId
                   + " prefab=" + pending.PrefabId + " reason=" + message);
            _uplink.Send(pending.SteamId, FactionsWire.WriteShipWithdrawResult(false, Guid.Empty, message));
        }

        private static string NodeLabel(SpatialNode node)
        {
            return string.IsNullOrEmpty(node.DisplayName) ? node.Id : node.DisplayName;
        }

        private static MyGridsStorageSessionComponent GridStorage()
        {
            return MySession.Static != null
                ? MySession.Static.GetComponent<MyGridsStorageSessionComponent>() : null;
        }

        /// <summary>A point with nothing inside a kilometre of it, walked outward from the terminal
        /// along the six axes. Deliberately near the terminal in sector terms — the clone lives for
        /// one call, and putting it across the map would be a longer-lived entity, not a safer
        /// one.</summary>
        private static bool TryFindClearSpawn(Vector3D origin, out Vector3D found)
        {
            var directions = new Vector3D[]
            {
                Vector3D.UnitX, -Vector3D.UnitX,
                Vector3D.UnitY, -Vector3D.UnitY,
                Vector3D.UnitZ, -Vector3D.UnitZ,
            };
            for (double range = SpawnProbeStartM; range <= SpawnProbeMaxM; range += SpawnProbeStepM)
            {
                for (int i = 0; i < directions.Length; i++)
                {
                    Vector3D candidate = origin + directions[i] * range;
                    if (IsClear(candidate)) { found = candidate; return true; }
                }
            }
            found = Vector3D.Zero;
            return false;
        }

        private static bool IsClear(Vector3D at)
        {
            var sphere = new BoundingSphereD(at, SpawnClearRadiusM);
            List<MyEntity> hits = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
            try { return hits == null || hits.Count == 0; }
            finally { if (hits != null) hits.Clear(); }
        }

        // ---- send to node -----------------------------------------------------------------------

        private void OnSendRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId;
            if (!FactionsWire.TryReadShipSendRequest(data, out terminalEntityId, out gridEntityId))
            {
                FactionsPlugin.Log.Warn("Ship send request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            string prefabId = "";
            string message;
            bool ok = false;
            try
            {
                ok = TrySend(steamId, terminalEntityId, gridEntityId, out prefabId, out message);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Ship send failed for " + steamId + ".");
                prefabId = "";
                message = "The send failed on the server; your ship and its cargo are untouched.";
            }

            if (!ok)
            {
                Log.Info("SHIP_SEND_REFUSED node=" + NodeIdFor(steamId, terminalEntityId)
                       + " player=" + steamId + " grid=" + gridEntityId + " reason=" + message);
            }
            _uplink.Send(steamId, FactionsWire.WriteShipSendResult(ok, prefabId ?? "", message));
        }

        /// <summary>The node for a refusal line, best-effort: a refusal that failed authorisation has
        /// no node, and the line still has to say which terminal it was about.</summary>
        private string NodeIdFor(ulong steamId, long terminalEntityId)
        {
            try
            {
                TerminalContext ctx;
                string ignored;
                return TryAuthorise(steamId, terminalEntityId, out ctx, out ignored) ? ctx.Node.Id : "-";
            }
            catch { return "-"; }
        }

        private bool TrySend(ulong steamId, long terminalEntityId, long gridEntityId,
                             out string prefabId, out string message)
        {
            prefabId = "";

            TerminalContext ctx;
            if (!TryAuthorise(steamId, terminalEntityId, out ctx, out message)) return false;

            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById<MyCubeGrid>(gridEntityId, out grid, false)
                || grid == null || grid.MarkedForClose || grid.Closed)
            {
                message = "That ship is no longer there.";
                return false;
            }

            List<MyCubeGrid> group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid);
            if (group == null || group.Count == 0) { message = "That ship is no longer there."; return false; }

            if (!WithinDesignatedArea(ctx.Block, group))
            {
                message = "Bring the ship within " + DesignatedAreaM.ToString("0") + " m of the terminal.";
                return false;
            }

            string denied;
            if (!ShipCaptureScanner.PlayerHasAccessToAll(ctx.Player, group, out denied))
            {
                message = denied;
                return false;
            }

            MyGridsStorageSessionComponent session = GridStorage();
            if (session == null)
            {
                message = "Grid storage is switched off on this server, so eligibility cannot be checked.";
                return false;
            }
            MyGridStorageRequestResult verdict;
            if (!TryValidateWithVanilla(grid, ctx, session, out verdict))
            {
                message = "The server could not check that ship against vanilla's storage rules.";
                return false;
            }
            // InventoryIssue is vanilla saying the hold is not empty. That is the one rule this
            // feature replaces: the cargo goes to the node instead of onto the floor as loot bags.
            if (verdict != MyGridStorageRequestResult.Success && verdict != MyGridStorageRequestResult.InventoryIssue)
            {
                message = DescribeVerdict(verdict);
                return false;
            }

            string damaged;
            if (!IsFullyRepaired(group, out damaged))
            {
                message = damaged;
                return false;
            }

            SePrefabCatalogue catalogue = _runtime.Catalogue;
            if (catalogue == null) { message = "No prefab catalogue is loaded on this server."; return false; }

            string fingerprint = PrefabCatalogueScanner.ComputeLiveFingerprint(group);
            List<string> matches = catalogue.IdsWithFingerprint(fingerprint, ctx.Faction.Tag);
            if (matches.Count == 0)
            {
                message = "No prefab match — Scan it first.";
                return false;
            }
            matches.Sort(StringComparer.OrdinalIgnoreCase);   // stable pick when a hull matches twice
            prefabId = matches[0];

            var census = new Dictionary<string, long>();
            foreach (MyCubeGrid g in group)
            {
                if (g == null) continue;
                foreach (MySlimBlock block in g.CubeBlocks)
                {
                    if (block == null || block.FatBlock == null || !block.FatBlock.HasInventory) continue;
                    ServicesTerminalHelpers.AddCargo(block.FatBlock, census);
                }
            }

            var accepted = new Dictionary<string, long>();
            var refusedKeys = new List<string>();
            if (!ShipAssetRules.FilterCargo(census, accepted, refusedKeys))
            {
                message = "Unload these first — the station cannot take them: " + string.Join(", ", refusedKeys.ToArray());
                prefabId = "";
                return false;
            }

            // From here nothing may fail: the node is credited, the hull is closed, the stock is up.
            long cargoUnits = 0;
            foreach (KeyValuePair<string, long> entry in accepted)
            {
                long held;
                ctx.Node.Inventory.TryGetValue(entry.Key, out held);
                ctx.Node.Inventory[entry.Key] = held + entry.Value;
                cargoUnits += entry.Value;
            }

            int blocks = 0;
            foreach (MyCubeGrid g in group)
            {
                if (g == null) continue;
                blocks += g.BlocksCount;
                // As AcceptOffer does before it closes a hull: a player still seated would otherwise
                // be closed along with the grid.
                g.DismountAllCockpits();
                if (!g.MarkedForClose && !g.Closed) g.Close();
            }

            int held2;
            ctx.Node.ShipStock.TryGetValue(prefabId, out held2);
            ctx.Node.ShipStock[prefabId] = held2 + 1;
            if (_recordActivity != null) _recordActivity(ctx.Node.Id);

            Log.Info("SHIP_SENT node=" + ctx.Node.Id + " faction=" + ctx.Node.OwnerFactionTag
                   + " player=" + ctx.IdentityId + " prefab=" + prefabId
                   + " blocks=" + blocks + " cargokeys=" + accepted.Count + " cargounits=" + cargoUnits);
            if (StationStoreManager.DebugEnabled)
            {
                foreach (KeyValuePair<string, long> entry in accepted)
                    Log.Info("SHIP_SENT_CARGO node=" + ctx.Node.Id + " key=" + entry.Key + " units=" + entry.Value);
            }

            message = prefabId + " is now stock at " + NodeLabel(ctx.Node)
                    + (cargoUnits > 0 ? ", with " + cargoUnits + " unit(s) of cargo." : ".");
            return true;
        }

        /// <summary>Whether any grid in the group reaches into the terminal's designated area. Tested
        /// against each grid's own box rather than its centre, which is how vanilla's sphere query
        /// finds them in the first place — a long hull nosed up to the terminal is in the tab's list
        /// and must not be refused by the button beside it.</summary>
        private static bool WithinDesignatedArea(MyCubeBlock terminal, List<MyCubeGrid> group)
        {
            var sphere = new BoundingSphereD(terminal.PositionComp.GetPosition(), DesignatedAreaM);
            foreach (MyCubeGrid g in group)
            {
                if (g == null || g.PositionComp == null) continue;
                if (sphere.Intersects(g.PositionComp.WorldAABB)) return true;
            }
            return false;
        }

        private static bool TryValidateWithVanilla(MyCubeGrid grid, TerminalContext ctx,
            MyGridsStorageSessionComponent session, out MyGridStorageRequestResult verdict)
        {
            verdict = MyGridStorageRequestResult.Success;
            if (!_validateGridResolved)
            {
                _validateGridResolved = true;
                _validateGrid = AccessTools.Method(typeof(MyGridsStorageEntityComponent), "ValidateGrid");
                if (_validateGrid == null)
                    Log.Warn("MyGridsStorageEntityComponent.ValidateGrid not found; Send to Node is unavailable.");
            }
            if (_validateGrid == null || ctx.Identity == null) return false;

            MyCubeGrid terminalGrid = ctx.Block.CubeGrid;
            object result = _validateGrid.Invoke(null, new object[] { grid, terminalGrid, ctx.Identity, session });
            if (!(result is MyGridStorageRequestResult)) return false;
            verdict = (MyGridStorageRequestResult)result;
            return true;
        }

        /// <summary>Vanilla's own refusal, in words a player can act on. Flags, so the first one that
        /// applies is named rather than the whole set — a list of six reasons is not more
        /// actionable than the first.</summary>
        private static string DescribeVerdict(MyGridStorageRequestResult verdict)
        {
            if ((verdict & MyGridStorageRequestResult.GridNotFound) != 0) return "That ship is no longer there.";
            if ((verdict & MyGridStorageRequestResult.GridIsMoving) != 0) return "Bring the ship (and the station) to a stop first.";
            if ((verdict & MyGridStorageRequestResult.GridInCombat) != 0) return "That ship was in combat too recently.";
            if ((verdict & MyGridStorageRequestResult.GridIsStatic) != 0) return "That is a station, not a ship.";
            if ((verdict & MyGridStorageRequestResult.HasLinkedGrids) != 0) return "Detach the connected grids first — only one construct can be sent.";
            if ((verdict & MyGridStorageRequestResult.OwnershipIssue) != 0) return "You do not own that ship.";
            if ((verdict & MyGridStorageRequestResult.GridContainsHostileBlocks) != 0) return "That ship carries blocks owned outside your faction.";
            if ((verdict & MyGridStorageRequestResult.BlockLimitsViolation) != 0) return "That ship is over this server's block limits.";
            if ((verdict & MyGridStorageRequestResult.GridsPerPlayerLimitReached) != 0) return "You are at this server's stored-grid limit.";
            return "Vanilla's storage rules refuse that ship (" + verdict + ").";
        }

        /// <summary>A hull with anything missing or under-built is refused, because the prefab it
        /// would become respawns whole: sending a wreck and withdrawing it back is a free repair,
        /// and the Repair tab that charges for exactly that is one tab over.
        ///
        /// <para>Deformation is deliberately not part of this. The rule is "nothing is missing" —
        /// integrity and components — and a dented plate costs the faction nothing when the prefab
        /// is rebuilt.</para></summary>
        private static bool IsFullyRepaired(List<MyCubeGrid> group, out string reason)
        {
            var missing = new Dictionary<string, int>();
            foreach (MyCubeGrid g in group)
            {
                if (g == null) continue;
                foreach (MySlimBlock block in g.CubeBlocks)
                {
                    if (block == null || block.BlockDefinition == null) continue;
                    if (!block.IsFullIntegrity)
                    {
                        reason = "Repair the ship first — " + block.BlockDefinition.DisplayNameText
                               + " is damaged, and a sent hull comes back whole.";
                        return false;
                    }
                    missing.Clear();
                    block.GetMissingComponents(missing);
                    if (missing.Count > 0)
                    {
                        reason = "Finish building the ship first — " + block.BlockDefinition.DisplayNameText
                               + " is incomplete.";
                        return false;
                    }
                }
            }
            reason = null;
            return true;
        }

        // ---- scan -------------------------------------------------------------------------------

        private void OnScanRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId;
            string scope, name;
            if (!FactionsWire.TryReadShipScanRequest(data, out terminalEntityId, out gridEntityId, out scope, out name))
            {
                FactionsPlugin.Log.Warn("Ship scan request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            string message;
            bool ok = false;
            try
            {
                ok = TryScan(steamId, terminalEntityId, gridEntityId, scope, name, out message);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Ship scan failed for " + steamId + ".");
                message = "The scan failed on the server.";
            }
            _uplink.Send(steamId, FactionsWire.WriteShipScanResult(ok, message));
        }

        /// <summary>The chat verb's capture with an entity id in front of it instead of a raycast.
        /// The body — name sanitising, the block cap, the scan, the registration, the recipe, the
        /// sidecar refresh, and the <c>SHIP_SCANNED</c> line — is
        /// <see cref="FactionRuntime.CaptureGridsForPlayer"/>, shared verbatim with
        /// <c>!factions ship capture</c>.</summary>
        private bool TryScan(ulong steamId, long terminalEntityId, long gridEntityId,
                             string scope, string name, out string message)
        {
            TerminalContext ctx;
            if (!TryAuthorise(steamId, terminalEntityId, out ctx, out message)) return false;

            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById<MyCubeGrid>(gridEntityId, out grid, false)
                || grid == null || grid.MarkedForClose || grid.Closed)
            {
                message = "That ship is no longer there.";
                return false;
            }

            List<MyCubeGrid> group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid);
            if (group == null || group.Count == 0) { message = "That ship is no longer there."; return false; }
            if (!WithinDesignatedArea(ctx.Block, group))
            {
                message = "Bring the ship within " + DesignatedAreaM.ToString("0") + " m of the terminal.";
                return false;
            }

            string reply = _runtime.CaptureGridsForPlayer(ctx.Faction, ctx.Player, group, scope, name);
            message = reply;
            // The runtime answers in prose either way; a capture that happened says so on its first
            // line. Matching on that prefix keeps one source of truth for the wording.
            return !string.IsNullOrEmpty(reply)
                   && (reply.StartsWith("Captured '", StringComparison.Ordinal)
                       || reply.StartsWith(FactionRuntime.PublishedReplyPrefix, StringComparison.Ordinal));
        }
    }
}
