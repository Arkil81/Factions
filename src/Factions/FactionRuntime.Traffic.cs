using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// Hands station traffic to the traffic-control voice (FAC-419, docs/atc-voice.md). Core queues
    /// one <see cref="TrafficEvent"/> per departure, arrival and hostile detection
    /// (<see cref="TrafficWatch"/>); after every arrivals pass this takes them and passes each to
    /// <see cref="TrafficSink"/> with the station's name and owner. Who is in radio range, and who
    /// can hear it, is the sink's business.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>Set by the host when a client uplink exists: the event, the station's display
        /// name (null when it has none), and its owner's tag (null when unowned).</summary>
        internal Action<TrafficEvent, string, string> TrafficSink { get; set; }

        /// <summary>Set by the host: true for a player whose client speaks station traffic, so the
        /// grid-lifecycle chat whisper for the same fleet is skipped
        /// (<see cref="SendNearbyFleetMessage"/>).</summary>
        internal Func<ulong, bool> SpeaksTraffic { get; set; }

        private void PublishTrafficEvents()
        {
            List<TrafficEvent> events = _world.TakeTrafficEvents();
            Action<TrafficEvent, string, string> sink = TrafficSink;
            if (events == null || sink == null) return;
            for (int i = 0; i < events.Count; i++)
            {
                TrafficEvent e = events[i];
                SpatialNode node = null;
                if (e.NodeId != null) _world.Graph.Nodes.TryGetValue(e.NodeId, out node);
                sink(e, node != null ? node.DisplayName : null, node != null ? node.OwnerFactionTag : null);
            }
        }
    }
}
