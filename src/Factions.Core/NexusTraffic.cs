using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// One station traffic event as an agent needs it to play the traffic voice to its own players:
    /// the authority's <see cref="TrafficEvent"/> plus what the authority resolves for it (the node's
    /// name, its owner, the controller's speaker). Flat and short-named: it rides every reply.
    /// </summary>
    public sealed class NexusTrafficCall : NexusRelayedCall
    {
        public int Kind;
        public string Node;
        public double Nx, Ny, Nz;
        public string Actor;
        public int FleetKind;
        public int StopKind;
        public string FleetKey;
        public string FleetId;
        public int Queue;
        public bool Departure;
        public string Divert;
        public string DivertName;
        public DateTime Utc;
        public double Px, Py, Pz;
        public double Vx, Vy, Vz;
        public double Sig;
        public long Pilot;
        public string NodeName;
        public string Owner;
        public byte Speaker;
        /// <summary>A hail (a <c>WireTrafficEvent</c>) to the one player <see cref="To"/>, from the
        /// faction <see cref="Owner"/> at <see cref="Node"/>; 0 for a station traffic event.</summary>
        public int Hail;
        public ulong To;

        public static NexusTrafficCall HailTo(ulong steamId, int hail, string ownerTag, byte speaker,
                                              string nodeId, Vec3 nodePosKm, string nodeName, DateTime utc)
        {
            return new NexusTrafficCall
            {
                Hail = hail, To = steamId, Owner = ownerTag, Speaker = speaker, Node = nodeId,
                Nx = nodePosKm.X, Ny = nodePosKm.Y, Nz = nodePosKm.Z, NodeName = nodeName, Utc = utc,
            };
        }
        public static NexusTrafficCall From(TrafficEvent e, string nodeName, string owner, byte speaker)
        {
            return new NexusTrafficCall
            {
                Kind = (int)e.Kind, Node = e.NodeId,
                Nx = e.NodePosKm.X, Ny = e.NodePosKm.Y, Nz = e.NodePosKm.Z,
                Actor = e.ActorTag, FleetKind = (int)e.FleetKind, StopKind = (int)e.StopKind,
                FleetKey = e.FleetKey, FleetId = e.FleetId, Queue = e.QueuePosition, Departure = e.Departure,
                Divert = e.DivertNodeId, DivertName = e.DivertNodeName, Utc = e.Utc,
                Px = e.PilotPosKm.X, Py = e.PilotPosKm.Y, Pz = e.PilotPosKm.Z,
                Vx = e.PilotVelMs.X, Vy = e.PilotVelMs.Y, Vz = e.PilotVelMs.Z,
                Sig = e.PilotSignature, Pilot = e.PilotEntityId,
                NodeName = nodeName, Owner = owner, Speaker = speaker,
            };
        }

        public TrafficEvent ToEvent()
        {
            return new TrafficEvent
            {
                Kind = (TrafficEventKind)Kind, NodeId = Node, NodePosKm = new Vec3(Nx, Ny, Nz),
                ActorTag = Actor, FleetKind = (ActionKind)FleetKind, StopKind = (FleetStopKind)StopKind,
                FleetKey = FleetKey, FleetId = FleetId, QueuePosition = Queue, Departure = Departure,
                DivertNodeId = Divert, DivertNodeName = DivertName, Utc = Utc,
                PilotPosKm = new Vec3(Px, Py, Pz), PilotVelMs = new Vec3(Vx, Vy, Vz),
                PilotSignature = Sig, PilotEntityId = Pilot,
            };
        }
    }

    /// <summary>
    /// Authority: the traffic events of the last few seconds, handed to each agent once. Not
    /// acknowledged and not re-sent: a controller's call heard late is wrong rather than late, so a
    /// lost reply loses the calls it carried. An agent that has not taken any for a while gets only
    /// what is still fresh.
    /// </summary>
    public sealed class NexusTrafficRelay : NexusFreshRelay<NexusTrafficCall> { }

    /// <summary>A call an authority hands its agents once, numbered by <see cref="NexusFreshRelay{T}"/>.</summary>
    public abstract class NexusRelayedCall
    {
        public long Seq;
    }

    /// <summary>
    /// Authority: the calls of the last few seconds, handed to each agent once. Not acknowledged and
    /// not re-sent: a call heard late is wrong rather than late, so a lost reply loses the calls it
    /// carried. An agent that has not taken any for a while gets only what is still fresh.
    /// </summary>
    public class NexusFreshRelay<T> where T : NexusRelayedCall
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);
        public const int Capacity = 500;

        private struct Entry
        {
            public DateTime At;
            public byte Home;
            public T Call;
        }

        private readonly List<Entry> _recent = new List<Entry>();
        private readonly Dictionary<byte, long> _sentThrough = new Dictionary<byte, long>();
        private long _seq;

        /// <param name="home">The one agent the call is for (a call to a player there); 0 for every agent.</param>
        public void Add(T call, DateTime nowUtc, byte home = 0)
        {
            if (call == null) return;
            Prune(nowUtc);
            if (_recent.Count >= Capacity) _recent.RemoveAt(0);
            call.Seq = ++_seq;
            _recent.Add(new Entry { At = nowUtc, Home = home, Call = call });
        }

        /// <summary>The fresh calls <paramref name="home"/> has not been handed yet, oldest first;
        /// null when there are none.</summary>
        public List<T> Take(byte home, DateTime nowUtc)
        {
            Prune(nowUtc);
            long through;
            _sentThrough.TryGetValue(home, out through);
            List<T> list = null;
            for (int i = 0; i < _recent.Count; i++)
            {
                T c = _recent[i].Call;
                if (c.Seq <= through) continue;
                if (_recent[i].Home != 0 && _recent[i].Home != home) continue;
                if (list == null) list = new List<T>();
                list.Add(c);
            }
            _sentThrough[home] = _seq;
            return list;
        }

        /// <summary>Marks everything so far as handed to <paramref name="home"/> without sending it:
        /// an agent with nobody to hear it.</summary>
        public void Skip(byte home) { _sentThrough[home] = _seq; }

        public int Count { get { return _recent.Count; } }

        private void Prune(DateTime nowUtc)
        {
            int drop = 0;
            while (drop < _recent.Count && nowUtc - _recent[drop].At > MaxAge) drop++;
            if (drop > 0) _recent.RemoveRange(0, drop);
        }
    }
}
