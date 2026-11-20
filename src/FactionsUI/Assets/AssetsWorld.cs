using System;
using System.Collections.Generic;
using Factions.Wire;
using VRageMath;

namespace FactionsUI.Assets;

/// <summary>
/// The client's copy of what the server says this player owns. Positions are converted to SE
/// metres on ingest — the wire and Core both speak km, and this is the one place that boundary is
/// crossed, matching <see cref="FactionsUI.Map.MapWorld"/>.
/// </summary>
internal sealed class AssetsWorld
{
    public readonly List<AssetNode> Nodes = new List<AssetNode>();
    public readonly List<AssetFleet> Fleets = new List<AssetFleet>();
    public readonly List<AssetGrid> Grids = new List<AssetGrid>();

    /// <summary>Empty for a player in no faction — a real state, not an error: they can still own
    /// grids, and the tab says so rather than showing nothing.</summary>
    public string FactionTag = "";
    public string FactionName = "";

    public bool HasSnapshot { get; private set; }
    public DateTime LastSnapshotUtc { get; private set; }

    /// <summary>Bumped on every change. The page polls this rather than subscribing to an event,
    /// so a screen that has already closed cannot hold a live callback into a rebuilt table.</summary>
    public int Revision { get; private set; }

    public int Count => Nodes.Count + Fleets.Count + Grids.Count;

    public void Apply(
        string factionTag, string factionName,
        List<WireAssetNode> nodes, List<WireAssetFleet> fleets, List<WireAssetGrid> grids)
    {
        FactionTag = factionTag ?? "";
        FactionName = factionName ?? "";

        Nodes.Clear();
        for (int i = 0; i < nodes.Count; i++) Nodes.Add(new AssetNode(nodes[i]));

        Fleets.Clear();
        for (int i = 0; i < fleets.Count; i++) Fleets.Add(new AssetFleet(fleets[i]));

        Grids.Clear();
        for (int i = 0; i < grids.Count; i++) Grids.Add(new AssetGrid(grids[i]));

        HasSnapshot = true;
        LastSnapshotUtc = DateTime.UtcNow;
        Revision++;
    }

    public void Clear()
    {
        Nodes.Clear();
        Fleets.Clear();
        Grids.Clear();
        FactionTag = "";
        FactionName = "";
        HasSnapshot = false;
        Revision++;
    }

    internal sealed class AssetNode
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly byte Zone;
        public readonly bool HasStation;
        public readonly Vector3D PositionM;
        public readonly double HoldStrength;
        public readonly double ResourceValue;
        public readonly double Richness;
        public readonly long FoundCost;
        public readonly int ShipsReady;
        public readonly int BuildsQueued;
        /// <summary>Damaged hulls waiting here for repair.</summary>
        public readonly int ShipsInRepair;
        public readonly bool ShipyardPaused;
        public readonly WireAssetCapability[] Capabilities;
        /// <summary>The ore under this node, or empty. Replaces the retired NodeKind ordinal.</summary>
        public readonly string DominantOre;
        /// <summary>Whether the ground has been sampled at all -- an empty DominantOre means
        /// "barren" only when this is true.</summary>
        public readonly bool Surveyed;

        /// <summary>Capability names as the server sent them -- NOT in significance order; the
        /// label helper ranks them. Built once here rather than at every draw: the Assets table
        /// rebuilds a row per node per refresh, and this is the only reason it would allocate.</summary>
        public readonly string[] CapabilityNames;

        public AssetNode(WireAssetNode w)
        {
            Id = w.Id;
            DisplayName = string.IsNullOrEmpty(w.DisplayName) ? w.Id : w.DisplayName;
            DominantOre = w.DominantOre ?? "";
            Surveyed = w.Surveyed;
            Zone = w.Zone;
            HasStation = w.HasStation;
            PositionM = new Vector3D(w.XKm * 1000.0, w.YKm * 1000.0, w.ZKm * 1000.0);
            HoldStrength = w.HoldStrength;
            ResourceValue = w.ResourceValue;
            Richness = w.Richness;
            FoundCost = w.FoundCost;
            ShipsReady = w.ShipsReady;
            BuildsQueued = w.BuildsQueued;
            ShipsInRepair = w.ShipsInRepair;
            ShipyardPaused = w.ShipyardPaused;
            Capabilities = w.Capabilities ?? new WireAssetCapability[0];
            CapabilityNames = new string[Capabilities.Length];
            for (int i = 0; i < Capabilities.Length; i++) CapabilityNames[i] = Capabilities[i].Cap;
        }
    }

    internal sealed class AssetFleet
    {
        public readonly string Key;
        public readonly byte Kind;
        public readonly Vector3D PositionM;
        public readonly Vector3D TargetM;
        public readonly string OriginName;
        public readonly string DestinationName;
        public readonly DateTime EtaUtc;
        public readonly int ConvoySize;
        public readonly long EntityId;

        public AssetFleet(WireAssetFleet w)
        {
            Key = w.Key;
            Kind = w.Kind;
            PositionM = new Vector3D(w.XKm * 1000.0, w.YKm * 1000.0, w.ZKm * 1000.0);
            TargetM = new Vector3D(w.TargetXKm * 1000.0, w.TargetYKm * 1000.0, w.TargetZKm * 1000.0);
            OriginName = w.OriginName ?? "";
            DestinationName = w.DestinationName ?? "";
            EtaUtc = new DateTime(w.EtaUtcTicks, DateTimeKind.Utc);
            ConvoySize = w.ConvoySize;
            EntityId = w.EntityId;
        }
    }

    internal sealed class AssetGrid
    {
        public readonly long EntityId;
        public readonly string Name;
        public readonly bool IsStatic;
        public readonly byte GridSize;
        public readonly int BlockCount;
        public readonly Vector3D PositionM;
        public readonly bool OwnedPersonally;
        public readonly string OwnerName;

        public AssetGrid(WireAssetGrid w)
        {
            EntityId = w.EntityId;
            Name = string.IsNullOrEmpty(w.Name) ? "(unnamed grid)" : w.Name;
            IsStatic = w.IsStatic;
            GridSize = w.GridSize;
            BlockCount = w.BlockCount;
            PositionM = new Vector3D(w.XKm * 1000.0, w.YKm * 1000.0, w.ZKm * 1000.0);
            OwnedPersonally = w.OwnedPersonally;
            OwnerName = w.OwnerName ?? "";
        }
    }
}
