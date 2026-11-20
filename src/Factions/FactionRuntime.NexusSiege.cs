using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions
{
    /// <summary>
    /// The authority half of a siege on a station that stands on a Nexus agent (FAC-506 step 2b).
    /// The agent's fire enters the same <see cref="PlayerSiegeManager"/> a local hit does, once per
    /// event however often it is re-sent; the zone states it implies go back on every reply.
    /// </summary>
    public sealed partial class FactionRuntime
    {
        private readonly NexusSiegeInbox _agentSiege = new NexusSiegeInbox();
        private Action<long, string, string, int, bool> _remoteSiegeNotice;
        private Func<IList<RemotePlayer>> _remoteSiegePlayers;
        // home -> when it last reported, and the nodes a player was at there.
        private readonly Dictionary<byte, KeyValuePair<DateTime, HashSet<string>>> _agentObserved =
            new Dictionary<byte, KeyValuePair<DateTime, HashSet<string>>>();
        /// <summary>An agent silent this long no longer holds a refit.</summary>
        private static readonly TimeSpan AgentObservedMaxAge = TimeSpan.FromSeconds(5);

        /// <summary>Takes an agent's observed stations from its report.</summary>
        internal void NoteAgentObserved(byte home, IList<string> observed, DateTime nowUtc)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (observed != null) foreach (string id in observed) if (id != null) set.Add(id);
            _agentObserved[home] = new KeyValuePair<DateTime, HashSet<string>>(nowUtc, set);
        }

        /// <summary>Whether a player is at this node's station on a reporting agent.</summary>
        internal bool IsObservedOnAgent(string nodeId)
        {
            if (nodeId == null || _agentObserved.Count == 0) return false;
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<byte, KeyValuePair<DateTime, HashSet<string>>> kv in _agentObserved)
                if (now - kv.Value.Key <= AgentObservedMaxAge && kv.Value.Value.Contains(nodeId)) return true;
            return false;
        }

        /// <summary>Hands the siege manager the way to reach players on agents. Arrives from the
        /// plugin; a siege manager built later still picks it up.</summary>
        internal void AttachNexusSiege(Action<long, string, string, int, bool> notice, Func<IList<RemotePlayer>> players)
        {
            _remoteSiegeNotice = notice;
            _remoteSiegePlayers = players;
            if (_playerSiege != null)
            {
                _playerSiege.RemoteNotice = notice;
                _playerSiege.RemotePlayers = players;
            }
        }

        /// <summary>Applies the agent's new siege events, then fills the reply's acknowledgement and
        /// zone states, and takes its observed stations for the refit hold. Returns the events that
        /// were new, answers to offers among them.</summary>
        internal List<NexusSiegeEvent> ApplyAgentSiege(byte home, NexusAgentReport report, NexusStationReply reply)
        {
            if (report == null || reply == null) return new List<NexusSiegeEvent>();
            long through;
            List<NexusSiegeEvent> fresh = _agentSiege.Accept(home, report.Session, report.Siege, out through);
            reply.SiegeThrough = through;
            NoteAgentObserved(home, report.Observed, DateTime.UtcNow);
            if (_playerSiege != null)
                for (int i = 0; i < fresh.Count; i++) _playerSiege.AddRemote(fresh[i]);
            reply.Zones = NexusZoneStates();
            return fresh;
        }

        /// <summary>Every founded node whose zone is off its default, as an agent standing it applies it.</summary>
        private List<NexusZoneState> NexusZoneStates()
        {
            if (_world == null) return null;
            List<NexusZoneState> list = null;
            foreach (SpatialNode n in _world.Graph.Nodes.Values)
            {
                if (!Economy.HasPhysicalStation(n)) continue;
                NexusZoneState z = NexusZoneState.For(n, _tuning, _world.UtcNow);
                if (z == null) continue;
                if (list == null) list = new List<NexusZoneState>();
                list.Add(z);
            }
            return list;
        }
    }
}
