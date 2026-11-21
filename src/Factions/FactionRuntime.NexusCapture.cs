using System;
using Factions.Core;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// The authority half of a capture made on a Nexus agent: the agent read the grid and ran the
    /// checks that need it; the name, the catalogue, the recipe and the store are registered here,
    /// by the same halves a local capture ends in.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>Rises with every capture registered and livery moved, so a Nexus agent knows
        /// to reload the designs it spawns from. Not persisted: an agent reloads on any change.</summary>
        public int CaptureGeneration { get; private set; }

        /// <summary>What the terminal's ship scan and the chat verb's answer start with when a ship
        /// was captured.</summary>
        internal const string CapturedReplyPrefix = "Captured '";
        /// <summary>The same for a station scan.</summary>
        internal const string ScannedStationReplyPrefix = "Scanned station '";

        /// <summary>A design registered on a Nexus agent: written to the store, its fee paid to the
        /// owner of the node it was registered at (<see cref="NexusCaptureUpload.AdoptNode"/>). The
        /// agent charged the payer and holds its own copy until its next reload.</summary>
        public NexusCaptureResult RegisterRelayedDesign(NexusCaptureUpload up, MyObjectBuilder_CubeGrid[] builders, byte home)
        {
            var result = new NexusCaptureResult { Id = up != null ? up.Id : 0 };
            if (up == null || builders == null || builders.Length == 0 || up.EntityId == 0 || _customPrefabStore == null)
            {
                result.Message = "The design did not arrive.";
                return result;
            }
            SpatialNode node = null;
            if (!string.IsNullOrEmpty(up.AdoptNode) && _world != null && _world.Graph != null)
                _world.Graph.Nodes.TryGetValue(up.AdoptNode, out node);
            string payee = node != null && !string.IsNullOrEmpty(node.OwnerFactionTag) ? node.OwnerFactionTag : null;
            if (payee != null && up.Fee > 0) _world.Earn(payee, up.Fee);
            bool persisted = _customPrefabStore.RegisterDesign(up.EntityId, up.Fingerprint, builders, up.DisplayName,
                                                               up.Identity, DateTime.UtcNow);
            CaptureGeneration++;
            int blocks = 0;
            foreach (MyObjectBuilder_CubeGrid g in builders) if (g != null && g.CubeBlocks != null) blocks += g.CubeBlocks.Count;
            FactionsPlugin.Log.Info("DESIGN_REGISTERED grid=" + up.EntityId + " blocks=" + blocks + " grids=" + builders.Length
                                  + " fingerprint=" + up.Fingerprint + " by=" + up.Identity + " fee=" + up.Fee
                                  + " payee=" + (payee ?? "none") + " server=" + home
                                  + (persisted ? "" : " persisted=false"));
            result.Ok = persisted;
            return result;
        }

        public NexusCaptureResult RegisterRelayedCapture(NexusCaptureUpload up, MyObjectBuilder_CubeGrid[] builders)
        {
            var result = new NexusCaptureResult { Id = up != null ? up.Id : 0 };
            if (up == null) { result.Message = "The capture could not be read."; return result; }
            NexusCaptureKind kind = (NexusCaptureKind)up.Kind;

            FactionState owner = FindFaction(up.Owner);
            if (owner == null || (kind != NexusCaptureKind.Livery && !owner.IsPlayerFaction))
            {
                result.Message = kind == NexusCaptureKind.Livery
                    ? "No registered faction [" + (up.Owner ?? "") + "]."
                    : "Your faction is not registered with Factions yet.";
                result.Reason = result.Message;
                return result;
            }

            if (kind == NexusCaptureKind.PreviewShip || kind == NexusCaptureKind.PreviewStation)
                return Preview(up, owner, result);

            if (builders == null || builders.Length == 0)
            {
                result.Message = "The scanned grid did not arrive.";
                return result;
            }

            CaptureTarget target;
            SePrefabCatalogue catalogue;
            string refusal;
            switch (kind)
            {
                case NexusCaptureKind.Ship:
                case NexusCaptureKind.Livery:
                {
                    bool livery = kind == NexusCaptureKind.Livery;
                    if (!TryValidateCaptureRequest(owner, livery ? "own" : up.Scope, up.Name,
                            livery ? LiveryCaptureUsage : ShipCaptureUsage, out target, out catalogue, out refusal))
                    { result.Message = refusal; return result; }
                    foreach (MyObjectBuilder_CubeGrid g in builders)
                        if (g != null && g.IsStatic) { result.Message = "That is a station — use !factions station scan."; return result; }
                    if (up.Blocks > _tuning.ShipCaptureMaxBlocks)
                    {
                        result.Message = "That ship has " + up.Blocks + " blocks — the cap is " + _tuning.ShipCaptureMaxBlocks + ".";
                        return result;
                    }
                    string mismatch = DescribeVersionMismatch(catalogue, target, false, null);
                    if (mismatch != null) { result.Message = mismatch; return result; }
                    result.Message = RegisterShipCapture(owner, up.Identity, builders, up.Blocks, target, catalogue,
                        livery ? PrefabHintFor(_factionTypesCatalogue, owner.Tag) : null);
                    result.Ok = IsCaptured(result.Message, CapturedReplyPrefix);
                    return result;
                }
                case NexusCaptureKind.Station:
                {
                    if (!TryValidateCaptureRequest(owner, up.Scope, up.Name, StationScanUsage,
                            out target, out catalogue, out refusal))
                    { result.Message = refusal; return result; }
                    foreach (MyObjectBuilder_CubeGrid g in builders)
                        if (g != null && !g.IsStatic)
                        {
                            result.Message = "That is not a station: " + (g.DisplayName ?? "a grid") + " is a ship grid.";
                            return result;
                        }
                    if (up.Blocks > _tuning.StationCaptureMaxBlocks)
                    {
                        result.Message = "That station has " + up.Blocks + " blocks — the cap is " + _tuning.StationCaptureMaxBlocks + ".";
                        return result;
                    }
                    result.Node = up.AdoptNode;
                    result.Message = RegisterStationCapture(owner, up.Identity, builders, up.Blocks,
                        new Vec3(up.Px, up.Py, up.Pz), target, catalogue, up.AdoptNode);
                    result.Ok = IsCaptured(result.Message, ScannedStationReplyPrefix);
                    return result;
                }
                default:
                    result.Message = "Unknown capture.";
                    return result;
            }
        }

        private static bool IsCaptured(string reply, string prefix)
        {
            return !string.IsNullOrEmpty(reply)
                   && (reply.StartsWith(prefix, StringComparison.Ordinal)
                       || reply.StartsWith(PublishedReplyPrefix, StringComparison.Ordinal));
        }

        /// <summary>The scan dialogs' "Publish as" answer for a grid or station on an agent.</summary>
        private NexusCaptureResult Preview(NexusCaptureUpload up, FactionState owner, NexusCaptureResult result)
        {
            bool station = (NexusCaptureKind)up.Kind == NexusCaptureKind.PreviewStation;
            string currentId = null;
            if (station)
            {
                SpatialNode node;
                if (up.AdoptNode != null && _world != null && _world.Graph.Nodes.TryGetValue(up.AdoptNode, out node))
                    currentId = node.StationPrefabId;
            }
            else
            {
                currentId = up.KnownId;
                var catalogue = _prefabCatalogue as SePrefabCatalogue;
                string canonical;
                if (string.IsNullOrEmpty(currentId) && catalogue != null && !string.IsNullOrEmpty(up.DisplayName)
                    && catalogue.TryGetCanonicalId(up.DisplayName.Trim(), out canonical))
                    currentId = canonical;
            }
            DesignVersionPreview preview = PreviewVersionForPlayer(owner, currentId, station);
            result.Ok = true;
            result.Offered = preview.Offered;
            result.Family = preview.Family;
            result.CurrentId = preview.CurrentId;
            result.NextId = preview.NextId;
            result.ScopeAll = preview.ScopeAll;
            result.ScopeLabel = preview.ScopeLabel;
            result.Reason = preview.Reason;
            return result;
        }
    }
}
