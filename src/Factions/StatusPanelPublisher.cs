using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Sends each linked player the contents of their HUD status panel
    /// (<see cref="WireFeatures.StatusPanel"/>). The panel replaced a centre-screen notification
    /// re-sent every five seconds, which stayed on screen for good and drew at 1.2x text scale.
    ///
    /// <para><b>Owners refresh, nobody clears.</b> Whatever owns a situation calls <see cref="Set"/>
    /// for as long as it runs; an item not refreshed for <see cref="StaleSeconds"/> is dropped. A
    /// siege ends by lapse, conquest, a declined offer, abandonment or a change of hands, and making
    /// every one of those paths clear the panel is how one of them would forget to.</para>
    ///
    /// <para><b>Full state, sent on change.</b> A player is sent their whole list when its digest
    /// moves -- deadlines are absolute, so a ticking countdown is not a change -- and again every
    /// <see cref="KeepAliveSeconds"/> while it is not empty, which is what lets the client drop a
    /// panel this end stopped refreshing. An emptied list is sent once.</para>
    /// </summary>
    internal sealed class StatusPanelPublisher
    {
        private const double StaleSeconds = 3.0;
        private const double KeepAliveSeconds = 8.0;

        private sealed class Entry
        {
            public StatusPanelItem Item;
            public DateTime SeenUtc;
        }

        private sealed class Viewer
        {
            public readonly Dictionary<string, Entry> Items = new Dictionary<string, Entry>(StringComparer.Ordinal);
            public long SentDigest;
            public DateTime SentUtc;
            public bool SentAny;
        }

        private readonly UplinkManager _uplink;
        private readonly Dictionary<long, Viewer> _viewers = new Dictionary<long, Viewer>();
        private readonly List<long> _idScratch = new List<long>();
        private readonly List<string> _keyScratch = new List<string>();
        private readonly List<WireStatusPanelItem> _wireScratch = new List<WireStatusPanelItem>();

        internal StatusPanelPublisher(UplinkManager uplink)
        {
            _uplink = uplink;
        }

        /// <summary>Whether this player's client draws the panel. A player it does not is told the
        /// old way, by notification.</summary>
        public bool Draws(long identityId)
        {
            ulong steamId = SteamIdOf(identityId);
            return steamId != 0 && _uplink != null
                && (_uplink.GrantedFeatures(steamId) & WireFeatures.StatusPanel) != 0;
        }

        /// <summary>Shows or refreshes one item on a player's panel.</summary>
        public void Set(long identityId, StatusPanelItem item, DateTime now)
        {
            if (identityId == 0 || item == null || string.IsNullOrEmpty(item.Key)) return;
            Viewer viewer;
            if (!_viewers.TryGetValue(identityId, out viewer))
            {
                viewer = new Viewer();
                _viewers[identityId] = viewer;
            }
            Entry entry;
            if (!viewer.Items.TryGetValue(item.Key, out entry))
            {
                entry = new Entry();
                viewer.Items[item.Key] = entry;
            }
            entry.Item = item;
            entry.SeenUtc = now;
        }

        /// <summary>Drops stale items and sends every panel that changed or is due a keep-alive.</summary>
        public void Flush(DateTime now)
        {
            if (_viewers.Count == 0) return;
            _idScratch.Clear();
            _idScratch.AddRange(_viewers.Keys);

            for (int v = 0; v < _idScratch.Count; v++)
            {
                long identityId = _idScratch[v];
                Viewer viewer = _viewers[identityId];

                _keyScratch.Clear();
                foreach (KeyValuePair<string, Entry> kv in viewer.Items)
                    if ((now - kv.Value.SeenUtc).TotalSeconds > StaleSeconds) _keyScratch.Add(kv.Key);
                for (int i = 0; i < _keyScratch.Count; i++) viewer.Items.Remove(_keyScratch[i]);

                if (viewer.Items.Count == 0 && !viewer.SentAny)
                {
                    _viewers.Remove(identityId);
                    continue;
                }

                _keyScratch.Clear();
                _keyScratch.AddRange(viewer.Items.Keys);
                _keyScratch.Sort(StringComparer.Ordinal);
                long digest = 17;
                unchecked
                {
                    for (int i = 0; i < _keyScratch.Count; i++)
                        digest = digest * 31 + viewer.Items[_keyScratch[i]].Item.Digest();
                }

                bool changed = !viewer.SentAny || digest != viewer.SentDigest;
                bool keepAlive = viewer.Items.Count > 0 && (now - viewer.SentUtc).TotalSeconds >= KeepAliveSeconds;
                if (!changed && !keepAlive) continue;

                _wireScratch.Clear();
                for (int i = 0; i < _keyScratch.Count; i++)
                    _wireScratch.Add(ToWire(viewer.Items[_keyScratch[i]].Item, now));

                ulong steamId = SteamIdOf(identityId);
                bool sent = steamId != 0 && _uplink != null
                    && (_uplink.GrantedFeatures(steamId) & WireFeatures.StatusPanel) != 0
                    && _uplink.Send(steamId, FactionsWire.WriteStatusPanel(_wireScratch));

                if (viewer.Items.Count == 0 || !sent)
                {
                    // Emptied and told, or nobody to tell: a player who comes back links afresh and
                    // is sent whatever is current by then.
                    _viewers.Remove(identityId);
                    continue;
                }
                viewer.SentAny = true;
                viewer.SentDigest = digest;
                viewer.SentUtc = now;
            }
        }

        private static WireStatusPanelItem ToWire(StatusPanelItem item, DateTime now)
        {
            var lines = new WireStatusPanelLine[item.Lines.Count];
            for (int i = 0; i < lines.Length; i++)
            {
                StatusPanelLine line = item.Lines[i];
                lines[i] = new WireStatusPanelLine
                {
                    Text = line.Text,
                    CountdownSeconds = line.CountdownTo == default(DateTime)
                        ? -1f
                        : (float)Math.Max(0.0, (line.CountdownTo - now).TotalSeconds),
                };
            }
            return new WireStatusPanelItem { Key = item.Key, Tone = (byte)item.Tone, Lines = lines };
        }

        /// <summary>The Nexus authority's players on its agents, whose identities this server's
        /// player table may not map to a Steam id; null elsewhere.</summary>
        public Func<IList<RemotePlayer>> RemotePlayers { get; set; }

        private ulong SteamIdOf(long identityId)
        {
            if (identityId == 0) return 0UL;
            ulong steam = MySession.Static != null && MySession.Static.Players != null
                ? MySession.Static.Players.TryGetSteamId(identityId) : 0UL;
            if (steam != 0UL) return steam;
            Func<IList<RemotePlayer>> remote = RemotePlayers;
            IList<RemotePlayer> players = remote != null ? remote() : null;
            if (players != null)
                for (int i = 0; i < players.Count; i++)
                    if (players[i].IdentityId == identityId) return players[i].SteamId;
            return 0UL;
        }
    }
}
