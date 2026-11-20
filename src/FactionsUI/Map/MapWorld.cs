using System;
using System.Collections.Generic;
using Factions.Wire;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace FactionsUI.Map;

/// <summary>
/// The client's copy of what the server says is out there. Positions are converted to SE metres on
/// ingest -- the wire and Core both speak km, and this is the one place that boundary is crossed.
/// </summary>
internal sealed class MapWorld
{
    /// <summary>km/min on the wire to m/s here.</summary>
    private const double KmPerMinToMPerSec = 1000.0 / 60.0;

    public readonly List<MapNode> Nodes = new List<MapNode>();
    public readonly List<MapFaction> Factions = new List<MapFaction>();
    public readonly List<MapFleet> Fleets = new List<MapFleet>();

    /// <summary>Nodes under attack or unprotected right now, by node id. Normally empty. Rides the
    /// fleet snapshot, so it moves at the fleet cadence rather than the world's much slower one --
    /// a breach countdown built from the node snapshot would be up to a minute stale.</summary>
    public readonly Dictionary<string, MapAlarm> Alarms =
        new Dictionary<string, MapAlarm>(StringComparer.Ordinal);

    public bool HasWorld { get; private set; }
    public DateTime LastWorldUtc { get; private set; }
    public DateTime LastFleetsUtc { get; private set; }

    /// <summary>
    /// A label shared by more than this many nodes does not identify anything, so it is not drawn.
    /// Applied to the label text AFTER disambiguation, not to the bare name.
    /// </summary>
    private const int MaxSharesForDistinctName = 2;

    public void ApplyWorld(List<WireMapNode> nodes, List<WireMapFaction> factions)
    {
        Nodes.Clear();
        for (int i = 0; i < nodes.Count; i++) Nodes.Add(new MapNode(nodes[i]));
        MarkDistinctNames();

        Factions.Clear();
        for (int i = 0; i < factions.Count; i++) Factions.Add(new MapFaction(factions[i]));

        HasWorld = true;
        LastWorldUtc = DateTime.UtcNow;
    }

    public void ApplyFleets(List<WireMapFleet> fleets, List<WireMapAlarm> alarms)
    {
        Fleets.Clear();
        for (int i = 0; i < fleets.Count; i++) Fleets.Add(new MapFleet(fleets[i]));

        Alarms.Clear();
        int alarmCount = alarms == null ? 0 : alarms.Count;
        for (int i = 0; i < alarmCount; i++)
        {
            WireMapAlarm w = alarms[i];
            if (string.IsNullOrEmpty(w.NodeId)) continue;
            Alarms[w.NodeId] = new MapAlarm(w);
        }
        LastFleetsUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Works out what each node's label should say, and whether it says anything at all.
    ///
    /// A shared name is disambiguated with its owner's tag rather than suppressed: 166 names in the
    /// live world sit on more than one station, 161 of them across factions, and several are real
    /// names vanilla gave its own economy stations. Hiding "Broadmoor Station" because four other
    /// stations share it tells the player less than "Broadmoor Station (IGSL)" does. Only a label
    /// that still collides after the tag is added gets dropped, because at that point it genuinely
    /// cannot identify anything.
    ///
    /// Counted over the whole world once per snapshot rather than per frame -- 2600 nodes is not
    /// something to re-tally every draw.
    /// </summary>
    private void MarkDistinctNames()
    {
        var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Nodes.Count; i++)
        {
            string name = Nodes[i].DisplayName;
            if (string.IsNullOrEmpty(name) || name == Nodes[i].Id) continue;
            int n;
            nameCounts.TryGetValue(name, out n);
            nameCounts[name] = n + 1;
        }

        // Provisional label: bare name when it stands alone, name + owner tag when it does not.
        for (int i = 0; i < Nodes.Count; i++)
        {
            MapNode node = Nodes[i];
            string name = node.DisplayName;
            if (string.IsNullOrEmpty(name) || name == node.Id) { node.LabelText = null; continue; }

            int shared;
            nameCounts.TryGetValue(name, out shared);
            node.LabelText = shared <= 1 || string.IsNullOrEmpty(node.OwnerTag)
                ? name
                : name + " (" + node.OwnerTag + ")";
        }

        // A label that still collides after disambiguation identifies nothing; drop those.
        var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Nodes.Count; i++)
        {
            string label = Nodes[i].LabelText;
            if (label == null) continue;
            int n;
            labelCounts.TryGetValue(label, out n);
            labelCounts[label] = n + 1;
        }
        for (int i = 0; i < Nodes.Count; i++)
        {
            MapNode node = Nodes[i];
            int n;
            node.HasDistinctName =
                node.LabelText != null
                && labelCounts.TryGetValue(node.LabelText, out n)
                && n <= MaxSharesForDistinctName;
        }
    }

    public void Clear()
    {
        Nodes.Clear();
        Factions.Clear();
        Fleets.Clear();
        Alarms.Clear();
        HasWorld = false;
    }

    /// <summary>This node's alarm, or null when nothing is happening to it.</summary>
    public MapAlarm AlarmFor(string nodeId)
    {
        MapAlarm alarm;
        return nodeId != null && Alarms.TryGetValue(nodeId, out alarm) ? alarm : null;
    }

    /// <summary>One node under attack or unprotected, as the map reads it. Written as a class with
    /// a null for "nothing happening" rather than a struct with a flag, so no caller can draw an
    /// alarm out of a default value.</summary>
    internal sealed class MapAlarm
    {
        public readonly WireZoneState State;
        /// <summary>Shield remaining, 0..1. Frozen once breached.</summary>
        public readonly double ZoneIntegrity;
        /// <summary>Block damage as a fraction of what the raid needs, 0..1.</summary>
        public readonly double RaidProgress;
        /// <summary>The earliest the shield can break while besieged, the moment protection returns
        /// while down, <see cref="DateTime.MinValue"/> otherwise.</summary>
        public readonly DateTime UntilUtc;

        public MapAlarm(WireMapAlarm w)
        {
            State = (WireZoneState)w.State;
            ZoneIntegrity = w.ZoneIntegrity;
            RaidProgress = w.RaidProgress;
            UntilUtc = w.UntilUtcTicks > 0
                ? new DateTime(w.UntilUtcTicks, DateTimeKind.Utc)
                : DateTime.MinValue;
        }

        /// <summary>A fight, as opposed to a station merely waiting for its protection back. Only
        /// these pulse.</summary>
        public bool IsAttack
        {
            get
            {
                return State == WireZoneState.Besieged
                    || State == WireZoneState.Breached
                    || State == WireZoneState.Raided;
            }
        }
    }

    internal sealed class MapNode
    {
        public readonly string Id;
        public readonly string DisplayName;
        /// <summary>The bare subtype of the ore dominating this node's ground, or empty when
        /// there is none. Replaces the retired NodeKind ordinal.</summary>
        public readonly string DominantOre;
        /// <summary>Whether the ground has been sampled at all. An empty DominantOre means
        /// "barren" only when this is true; otherwise it means nobody has looked.</summary>
        public readonly bool Surveyed;
        /// <summary>One bit per NodeCapabilityKind ordinal -- see NodeLabels.Capability.</summary>
        public readonly ushort CapabilityMask;
        public readonly byte Zone;
        public readonly string OwnerTag;
        public readonly byte Relation;
        public readonly bool HasStation;
        public readonly bool HasInfrastructure;
        public readonly Vector3D PositionM;
        public readonly double RadiusM;
        public readonly double HoldStrength;

        /// <summary>True when a sensor has this node right now. False means remembered: identity and
        /// position are current -- a node does not move -- while owner and standing are last-known
        /// and may be stale. A node the viewer does not know at all never arrives, so this is only
        /// ever the remembered/live distinction, never a "should I draw it" question.</summary>
        public readonly bool Detected;

        /// <summary>What the map should actually write next to this node -- the bare name, or the
        /// name plus owner tag when the bare name is shared. Set by <see cref="MarkDistinctNames"/>
        /// after ingest; null when the node has no name of its own.</summary>
        public string LabelText { get; internal set; }

        /// <summary>Whether <see cref="LabelText"/> identifies this node rather than colliding with
        /// others even after disambiguation. Set by <see cref="MarkDistinctNames"/>.</summary>
        public bool HasDistinctName { get; internal set; }

        public MapNode(WireMapNode w)
        {
            Id = w.Id;
            DisplayName = string.IsNullOrEmpty(w.DisplayName) ? w.Id : w.DisplayName;
            DominantOre = w.DominantOre ?? "";
            Surveyed = w.Surveyed;
            CapabilityMask = w.CapabilityMask;
            Zone = w.Zone;
            OwnerTag = string.IsNullOrEmpty(w.OwnerTag) ? null : w.OwnerTag;
            Relation = w.Relation;
            HasStation = w.HasStation;
            HasInfrastructure = w.HasInfrastructure;
            PositionM = new Vector3D(w.XKm * 1000.0, w.YKm * 1000.0, w.ZKm * 1000.0);
            RadiusM = w.RadiusKm * 1000.0;
            HoldStrength = w.HoldStrength;
            Detected = w.Knowledge == (byte)WireKnowledge.Detected;
        }

        /// <summary>Scenery: nothing under it, nothing on it, nothing to go to. An UNSURVEYED node
        /// is deliberately not empty — nobody has looked, which is a reason to visit rather than a
        /// reason to hide it.</summary>
        public bool IsEmpty => DominantOre.Length == 0 && CapabilityMask == 0 && !HasStation && Surveyed;
    }

    internal sealed class MapFaction
    {
        public readonly string Tag;
        public readonly byte Relation;
        public readonly int NodeCount;

        public MapFaction(WireMapFaction w)
        {
            Tag = w.Tag;
            Relation = w.Relation;
            NodeCount = w.NodeCount;
        }
    }

    internal sealed class MapFleet
    {
        public readonly string Key;
        public readonly byte Kind;
        public readonly string OwnerTag;
        public readonly byte Relation;
        public readonly Vector3D TargetM;
        public readonly DateTime EtaUtc;
        public readonly int ConvoySize;
        public readonly long EntityId;
        /// <summary>The routed part of the itinerary ahead of <see cref="TargetM"/>, in metres.
        /// Empty on a direct leg. Legs the server has not routed yet are not sent, so the drawn
        /// route never runs straight through a body it would in fact arc around.</summary>
        public readonly Vector3D[] StopsM;

        /// <summary>
        /// This fleet is a raid holding at its target and fighting there right now, rather than one
        /// still flying at it. Drawn distinctly, because the dwell is the window a player can
        /// actually intervene in and a marker parked at a node says nothing about that.
        ///
        /// <para>The rule itself lives in <see cref="FleetEngagement"/>, shared with every other
        /// surface that has to tell a fight from a commute, and tested there. It is derived from
        /// what the wire already carries rather than sent as a field -- see that type for why, and
        /// for the Core guarantee it stands on.</para>
        /// </summary>
        public readonly bool IsEngaging;

        /// <summary>This fleet is a Reinforce holding at the node it came to shore up. The same
        /// dwell read from the other side, and deliberately not folded into
        /// <see cref="IsEngaging"/>: a garrison standing at a station is not an alarm, and on a
        /// live board the reinforces outnumber the raids.</summary>
        public readonly bool IsHoldingDefence;

        private readonly Vector3D _sampledPositionM;
        private readonly Vector3D _velocityMPerSec;
        private readonly double _sampledAtSeconds;

        public MapFleet(WireMapFleet w)
        {
            Key = w.Key;
            Kind = w.Kind;
            OwnerTag = w.OwnerTag;
            Relation = w.Relation;
            TargetM = new Vector3D(w.TargetXKm * 1000.0, w.TargetYKm * 1000.0, w.TargetZKm * 1000.0);
            EtaUtc = new DateTime(w.EtaUtcTicks, DateTimeKind.Utc);
            ConvoySize = w.ConvoySize;
            EntityId = w.EntityId;

            _sampledPositionM = new Vector3D(w.XKm * 1000.0, w.YKm * 1000.0, w.ZKm * 1000.0);
            _velocityMPerSec = new Vector3D(
                w.VelXKmPerMin * KmPerMinToMPerSec,
                w.VelYKmPerMin * KmPerMinToMPerSec,
                w.VelZKmPerMin * KmPerMinToMPerSec);
            _sampledAtSeconds = NowSeconds();

            IsEngaging = FleetEngagement.IsEngaging(w);
            IsHoldingDefence = FleetEngagement.IsHoldingDefence(w);

            int stopCount = w.RoutedStops == null ? 0 : w.RoutedStops.Length;
            StopsM = new Vector3D[stopCount];
            for (int i = 0; i < stopCount; i++)
            {
                WireMapStop stop = w.RoutedStops[i];
                StopsM[i] = new Vector3D(stop.XKm * 1000.0, stop.YKm * 1000.0, stop.ZKm * 1000.0);
            }
        }

        /// <summary>
        /// Where the convoy is right now. A fleet whose grid is streamed to this client is drawn at
        /// the grid's real position; otherwise it is dead-reckoned from the last sample, so it moves
        /// at frame rate rather than stepping once per server pass.
        ///
        /// Extrapolation is <see cref="DeadReckon"/>'s, shared with the scanner HUD so one convoy
        /// cannot be drawn in two places at once by two copies of the same rule.
        /// </summary>
        public Vector3D WorldPosition()
        {
            if (EntityId != 0)
            {
                IMyEntity entity;
                if (MyAPIGateway.Entities.TryGetEntityById(EntityId, out entity)
                    && entity != null && !entity.MarkedForClose)
                {
                    return entity.GetPosition();
                }
            }

            double x, y, z;
            DeadReckon.Extrapolate(
                _sampledPositionM.X, _sampledPositionM.Y, _sampledPositionM.Z,
                _velocityMPerSec.X, _velocityMPerSec.Y, _velocityMPerSec.Z,
                TargetM.X, TargetM.Y, TargetM.Z,
                NowSeconds() - _sampledAtSeconds,
                out x, out y, out z);
            return new Vector3D(x, y, z);
        }

        /// <summary>Wall clock, matching the scanner: presentation timing must not stretch or
        /// freeze with sim speed or a paused session.</summary>
        private static double NowSeconds()
        {
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }
    }
}

