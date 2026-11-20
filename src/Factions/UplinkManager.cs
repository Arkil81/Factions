using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.ModAPI;

namespace Factions
{
    /// <summary>
    /// Server side of the FactionsUI client link. Owns the connection and nothing else: a client
    /// announces itself, this grants or refuses, and remembers who is linked. It knows about no
    /// feature — the scanner and anything after it push their own messages through
    /// <see cref="Send"/> and learn about new clients through <see cref="ClientLinked"/>.
    ///
    /// The link is strictly additive. A player who never uplinks — no client plugin, or a protocol
    /// mismatch — is left exactly as they are today on whatever the server-only path provides.
    ///
    /// <para><b>On a Nexus cluster</b> a client can only reach the server it is connected to, and
    /// only one server simulates. So this has two further shapes, selected by
    /// <see cref="ConfigureRelay"/>. As an <b>agent</b> it answers its own clients' hellos, serves
    /// the few features that are properties of this server's own physics, and forwards every other
    /// feature message to the authority inside a <see cref="NexusRelayWire"/> frame. As the
    /// <b>authority</b> it accepts those frames as if the client had spoken to it directly — a
    /// remote client is a row in the same table with a home server recorded — and
    /// <see cref="Send"/> routes a remote client's payload back through its home server. The
    /// feature managers see a SteamID either way and never learn a relay exists.</para>
    /// </summary>
    internal sealed class UplinkManager
    {
        /// <summary>Logs per-message feature traffic. Off by default and rarely worth turning on:
        /// a linked client's own log already shows what arrived. It earns its place only when a
        /// client shows nothing and the question is which side dropped it — whether the server sent
        /// a delta at all exists nowhere else. Link negotiation is always logged; that is once per
        /// player session. Toggle with 'factions debug uplink'. Relay forwarding logs under the
        /// same switch.</summary>
        public static bool DebugTraffic;

        /// <summary>Features an agent serves from its own process: properties of the world whose
        /// physics this server runs, applied before the authority gate at session load. The
        /// authority withholds these from a remote client, because the client's physics are not
        /// its physics.</summary>
        public const WireFeatures AgentLocalFeatures = WireFeatures.GridSpeed    // munition speed rides the same bit
                                                     | WireFeatures.BlockPause    // the paused blocks are this server's entities
                                                     | WireFeatures.TrafficVoice; // the listener's radio is this server's ship

        /// <summary>Features the authority cannot serve to a remote client: the local ones above,
        /// plus rocks, whose server-side voxels are materialised only in the authority's own
        /// sectors — a client told to derive them in a sector nobody materialises would render
        /// rock that is not there.
        ///
        /// <para>Node inventory joins them because it moves physical goods. The player, their
        /// character position and the grid the goods land in all live in the process the client is
        /// connected to; from the authority a remote caller has no identity to resolve and no entity
        /// to reach, so every request would be refused. Withholding the bit says so once at link
        /// time rather than once per click.</para>
        ///
        /// <para>Ship assets are withheld for the same reason: the terminal block, the hull beside
        /// it and the transient spawn are all entities in the caller's own sector, none of which
        /// exist on this server when the caller is relayed from another.</para>
        ///
        /// <para>Station refit joins them one step further along: it scans a live station grid and
        /// then closes and rebuilds one. The terminal the scan names and the grid the rebuild
        /// replaces are both entities in the caller's sector, and a remote caller has neither an
        /// identity to resolve nor a block to stand at.</para>
        ///
        /// <para>Dock supply for the same reason: refuel and rearm write into a ship docked at a
        /// terminal in the caller's own sector. Restoration too: it welds and places blocks on a ship
        /// beside such a terminal.</para>
        ///
        /// <para>Diplomacy because its acts are leader-only, and the leader check resolves the
        /// caller's SE identity from this server's online players, which a relayed caller is not
        /// among. Every act would be refused as unidentified.</para>
        ///
        /// <para>Wing commands because the antenna pressed and the hulls it commands are entities
        /// in the caller's own sector.</para>
        ///
        /// <para>Combat outcome notices because a fight's viewers are the players within observation
        /// range of it, which a relayed caller, in another server's sectors, never is.</para>
        ///
        /// <para>Node spawn because the respawn request it rides is handled, and the character
        /// placed, by the server the caller is connected to.</para></summary>
        public const WireFeatures RemoteWithheldFeatures =
            AgentLocalFeatures | WireFeatures.FeatureRock | WireFeatures.NodeInventory
            | WireFeatures.ShipAssets | WireFeatures.StationRefit | WireFeatures.DockSupply
            | WireFeatures.Diplomacy | WireFeatures.WingCommand | WireFeatures.CrewOrders | WireFeatures.CombatOutcome
            | WireFeatures.Restoration | WireFeatures.DesignVersions
            | WireFeatures.NodeSpawn | WireFeatures.StatusPanel;

        /// <summary>Client request kinds an agent may replay to the authority after it restarts.
        /// Every one is a subscription — sending it twice yields a fresh snapshot and nothing else.
        /// Commands (node, standing-order, expand) are deliberately absent: replaying one would run
        /// it again.</summary>
        private static readonly WireMessageKind[] ReplayableKinds =
        {
            WireMessageKind.MapRequest,
            WireMessageKind.AssetsRequest,
            WireMessageKind.NodeDetailRequest,
            WireMessageKind.EventsRequest,
            WireMessageKind.StandingOrdersRequest,
            WireMessageKind.MarketRequest,
        };

        private readonly Action<string> _log;
        private readonly Func<WireFeatures> _features;
        private readonly Dictionary<ulong, LinkedClient> _linked = new Dictionary<ulong, LinkedClient>();
        private bool _registered;

        private NexusRelay _relay;
        private NexusRole _role = NexusRole.Standalone;
        private byte _thisServerId;
        private byte _authorityServerId;

        /// <summary>Raised after a client is granted a link, so each feature can push whatever
        /// initial state it owns. Runs on the game thread, inside the message handler.</summary>
        public event Action<ulong> ClientLinked;

        /// <summary>Sinks for client-originated feature messages, keyed by the kind they own — the
        /// mirror of the client's own dispatch table. A feature registers here rather than this
        /// manager knowing anything about it. Instance-scoped, so a restart cannot leave a handler
        /// closed over a dead runtime.</summary>
        private readonly Dictionary<WireMessageKind, Action<ulong, byte[]>> Handlers =
            new Dictionary<WireMessageKind, Action<ulong, byte[]>>();

        public void RegisterHandler(WireMessageKind kind, Action<ulong, byte[]> handler)
        {
            Handlers[kind] = handler;
        }

        public UplinkManager(Func<WireFeatures> features, Action<string> log)
        {
            _features = features;
            _log = log;
            LinkedViewers.Uplink = this;
        }

        /// <summary>Attaches the cluster transport. <paramref name="role"/> is
        /// <see cref="NexusRole.Agent"/> or <see cref="NexusRole.Authority"/>; anything else
        /// leaves this manager standalone.</summary>
        public void ConfigureRelay(NexusRelay relay, byte thisServerId, byte authorityServerId, NexusRole role)
        {
            if (relay == null || (role != NexusRole.Agent && role != NexusRole.Authority))
            {
                _relay = null;
                _role = NexusRole.Standalone;
                return;
            }
            _relay = relay;
            _role = role;
            _thisServerId = thisServerId;
            _authorityServerId = authorityServerId;
        }

        public NexusRole Role { get { return _role; } }

        public int LinkedCount { get { return _linked.Count; } }

        /// <summary>Linked clients whose home is another server. Zero unless this is the authority.</summary>
        public int RemoteCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<ulong, LinkedClient> kv in _linked) if (kv.Value.HomeServerId != 0) n++;
                return n;
            }
        }

        /// <summary>Adds every remote client's SteamID to <paramref name="roster"/>. A feature
        /// manager prunes its subscribers against a roster; on the authority the local online list
        /// does not hold a relayed client, so the roster it is given must carry these too. Remote
        /// rows leave through <see cref="PruneRemote"/> and <see cref="RelayKind.Unlink"/>.</summary>
        /// <summary>Whether this client is linked through an agent rather than connected here.</summary>
        public bool IsRemote(ulong steamId)
        {
            LinkedClient client;
            return _linked.TryGetValue(steamId, out client) && client.HomeServerId != 0;
        }

        public void AddRemoteTo(ICollection<ulong> roster)
        {
            foreach (KeyValuePair<ulong, LinkedClient> kv in _linked)
                if (kv.Value.HomeServerId != 0) roster.Add(kv.Key);
        }

        /// <summary>Authority only: receives each <see cref="RelayKind.PlayerPositions"/> payload
        /// with the Nexus id of the server that sent it.</summary>
        public Action<byte, byte[]> PositionsReceived;

        /// <summary>Authority only: receives each <see cref="RelayKind.AgentReport"/> payload with the
        /// sender's Nexus id.</summary>
        public Action<byte, byte[]> AgentReportReceived;

        /// <summary>Agent only: receives each <see cref="RelayKind.NodeSnapshot"/> payload from the
        /// authority.</summary>
        public Action<byte[]> SnapshotReceived;

        /// <summary>Agent only: receives each <see cref="RelayKind.StationReply"/> payload from the
        /// authority.</summary>
        public Action<byte[]> StationReplyReceived;

        /// <summary>True once the player's client plugin has been granted a link.</summary>
        public bool IsLinked(ulong steamId)
        {
            return _linked.ContainsKey(steamId);
        }

        /// <summary>What this client was granted at link time. <see cref="WireFeatures.None"/> for a
        /// client that is not linked, so a caller never has to pair this with its own check.</summary>
        public WireFeatures GrantedFeatures(ulong steamId)
        {
            LinkedClient client;
            return _linked.TryGetValue(steamId, out client) ? client.Features : WireFeatures.None;
        }

        /// <summary>Sends a feature payload to one linked client. Silently does nothing for a
        /// client that is not linked, so callers never have to pair this with their own check.
        /// A remote client's payload goes to its home server for delivery.</summary>
        public bool Send(ulong steamId, byte[] payload)
        {
            LinkedClient client;
            if (payload == null || !_linked.TryGetValue(steamId, out client)) return false;
            return Deliver(steamId, client.HomeServerId, payload);
        }

        /// <summary>Sends one payload to every linked client that was granted
        /// <paramref name="feature"/>, and returns how many were sent to. The feature check lives
        /// here rather than at each call site so a broadcast can never reach a client the server
        /// deliberately withheld the feature from.</summary>
        public int SendToAll(WireFeatures feature, byte[] payload)
        {
            if (payload == null || _linked.Count == 0) return 0;
            int sent = 0;
            foreach (KeyValuePair<ulong, LinkedClient> kv in _linked)
            {
                if ((kv.Value.Features & feature) == 0) continue;
                if (Deliver(kv.Key, kv.Value.HomeServerId, payload)) sent++;
            }
            return sent;
        }

        /// <summary>Must run on the game thread — SE throws if handlers are (un)registered from
        /// anywhere else.</summary>
        public void Register()
        {
            if (_registered) return;
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(FactionsWire.ChannelId, OnMessage);
            _registered = true;
        }

        public void Close()
        {
            if (_registered)
            {
                try { MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(FactionsWire.ChannelId, OnMessage); }
                catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Uplink handler unregister failed."); }
                _registered = false;
            }
            _linked.Clear();
            Handlers.Clear();
            ClientLinked = null;
            if (LinkedViewers.Uplink == this) LinkedViewers.Uplink = null;
        }

        /// <summary>Drops local clients that are no longer online. Cheap enough to call on the
        /// scanner cadence; there is no disconnect event to hook that is cheaper than this
        /// comparison. Remote clients are not this roster's to judge — see
        /// <see cref="PruneRemote"/>. An agent tells the authority about each drop.</summary>
        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_linked.Count == 0) return;
            List<ulong> stale = null;
            foreach (KeyValuePair<ulong, LinkedClient> kv in _linked)
            {
                if (kv.Value.HomeServerId != 0) continue;
                if (onlineSteamIds.Contains(kv.Key)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(kv.Key);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale)
            {
                _linked.Remove(steamId);
                Write("UPLINK_DROPPED steam=" + steamId + " reason=offline");
                if (_role == NexusRole.Agent)
                    Forward(RelayKind.Unlink, steamId, null, "unlink");
            }
        }

        /// <summary>Authority only: drops remote clients absent from the cluster-wide roster. A
        /// roster that is empty is treated as unavailable rather than as "nobody is online",
        /// because Nexus rebuilding it after a controller hiccup would otherwise unlink every
        /// remote client at once with nothing to re-link them until their agent notices.</summary>
        public void PruneRemote(ICollection<ulong> clusterOnlineSteamIds)
        {
            if (_role != NexusRole.Authority || clusterOnlineSteamIds == null || clusterOnlineSteamIds.Count == 0) return;
            List<ulong> stale = null;
            foreach (KeyValuePair<ulong, LinkedClient> kv in _linked)
            {
                if (kv.Value.HomeServerId == 0) continue;
                if (clusterOnlineSteamIds.Contains(kv.Key)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(kv.Key);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale)
            {
                _linked.Remove(steamId);
                Write("UPLINK_DROPPED steam=" + steamId + " reason=offline-cluster");
            }
        }

        /// <summary>Agent only: re-forwards every linked client's hello and replayable requests.
        /// Called when the authority comes back online, so its remote table is rebuilt and each
        /// client's subscriptions produce a fresh snapshot without the client doing anything.</summary>
        public int ResyncAll()
        {
            if (_role != NexusRole.Agent || _linked.Count == 0) return 0;
            int clients = 0;
            foreach (KeyValuePair<ulong, LinkedClient> kv in _linked)
            {
                if (kv.Value.LastHello == null) continue;
                clients++;
                Forward(RelayKind.ClientMessage, kv.Key, kv.Value.LastHello, "hello(resync)");
                if (kv.Value.LastRequests == null) continue;
                foreach (WireMessageKind kind in ReplayableKinds)
                {
                    byte[] request;
                    if (kv.Value.LastRequests.TryGetValue(kind, out request))
                        Forward(RelayKind.ClientMessage, kv.Key, request, kind + "(resync)");
                }
            }
            Write("RELAY_RESYNC clients=" + clients + " authority=" + _authorityServerId);
            return clients;
        }

        /// <summary>A frame from another cluster member, delivered on the game thread.</summary>
        public void OnRelayInbound(byte fromServerId, byte[] frame)
        {
            RelayKind kind; byte home; ulong steamId; byte[] payload;
            if (!NexusRelayWire.TryRead(frame, out kind, out home, out steamId, out payload))
            {
                Write("RELAY_UNREADABLE from=" + fromServerId + " bytes=" + (frame == null ? 0 : frame.Length));
                return;
            }
            // The outer envelope's sender is the one Nexus vouches for; the inner home byte is what
            // the sender claims. They agree unless something is very wrong, and the envelope wins.
            if (home != fromServerId) home = fromServerId;

            switch (_role)
            {
                case NexusRole.Authority:
                    if (kind == RelayKind.ClientMessage)
                    {
                        if (DebugTraffic) Write("RELAY_IN steam=" + steamId + " home=" + home + " bytes=" + payload.Length);
                        HandleClientMessage(steamId, payload, home);
                    }
                    else if (kind == RelayKind.PlayerPositions)
                    {
                        Action<byte, byte[]> sink = PositionsReceived;
                        if (sink != null) sink(home, payload);
                    }
                    else if (kind == RelayKind.AgentReport)
                    {
                        Action<byte, byte[]> sink = AgentReportReceived;
                        if (sink != null) sink(home, payload);
                    }
                    else if (kind == RelayKind.Unlink)
                    {
                        LinkedClient client;
                        if (_linked.TryGetValue(steamId, out client) && client.HomeServerId == home)
                        {
                            _linked.Remove(steamId);
                            Write("UPLINK_DROPPED steam=" + steamId + " reason=unlinked-by-home home=" + home);
                        }
                    }
                    return;

                case NexusRole.Agent:
                    if (home != _authorityServerId) return;
                    if (kind == RelayKind.NodeSnapshot)
                    {
                        Action<byte[]> sink = SnapshotReceived;
                        if (sink != null) sink(payload);
                        return;
                    }
                    if (kind == RelayKind.StationReply)
                    {
                        Action<byte[]> sink = StationReplyReceived;
                        if (sink != null) sink(payload);
                        return;
                    }
                    if (kind != RelayKind.ServerMessage) return;
                    OnAuthorityMessage(steamId, payload);
                    return;

                default:
                    return;
            }
        }

        /// <summary>Agent: something the authority sent for one of this server's clients.</summary>
        private void OnAuthorityMessage(ulong steamId, byte[] payload)
        {
            LinkedClient client;
            if (!_linked.TryGetValue(steamId, out client)) return;

            WireMessageKind kind; int protocolVersion;
            if (!FactionsWire.TryReadHeader(payload, out kind, out protocolVersion)) return;

            if (kind == WireMessageKind.UplinkGranted)
            {
                string serverVersion; WireFeatures authorityFeatures;
                if (!FactionsWire.TryReadUplinkGranted(payload, out serverVersion, out authorityFeatures)) return;
                // One grant reaches the client, carrying what this server serves and what the
                // authority does. The client never learns which is which.
                WireFeatures merged = (client.Features & AgentLocalFeatures) | authorityFeatures;
                client.Features = merged;
                client.AuthorityLinked = true;
                _linked[steamId] = client;
                SendLocal(steamId, FactionsWire.WriteUplinkGranted(FactionsPlugin.PluginVersion, merged));
                Write("RELAY_LINKED steam=" + steamId + " authority=" + _authorityServerId
                      + " authorityVersion=" + serverVersion + " features=" + merged);
                return;
            }
            if (kind == WireMessageKind.UplinkRefused)
            {
                string reason;
                FactionsWire.TryReadUplinkRefused(payload, out reason);
                _linked.Remove(steamId);
                SendLocal(steamId, payload);
                Write("UPLINK_REFUSED steam=" + steamId + " reason=authority:" + (reason ?? "(unknown)"));
                return;
            }
            if (DebugTraffic) Write("RELAY_OUT steam=" + steamId + " kind=" + kind + " bytes=" + payload.Length);
            SendLocal(steamId, payload);
        }

        private void OnMessage(ushort channelId, byte[] data, ulong senderSteamId, bool fromServer)
        {
            // fromServer would mean our own send echoing back; only clients request a link.
            if (fromServer || senderSteamId == 0) return;
            try
            {
                if (_role == NexusRole.Agent) HandleClientMessageAsAgent(senderSteamId, data);
                else HandleClientMessage(senderSteamId, data, 0);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Uplink message handling failed.");
            }
        }

        /// <summary>The ordinary path: a client speaking to the server that serves it. On the
        /// authority a relayed client takes the same path with its <paramref name="homeServerId"/>
        /// recorded, so the grant and every later payload find their way back.</summary>
        private void HandleClientMessage(ulong senderSteamId, byte[] data, byte homeServerId)
        {
            WireMessageKind kind;
            int protocolVersion;
            if (!FactionsWire.TryReadHeader(data, out kind, out protocolVersion))
            {
                Write("UPLINK_REFUSED steam=" + senderSteamId + " reason=malformed" + HomeSuffix(homeServerId));
                return;
            }
            if (kind != WireMessageKind.UplinkRequest)
            {
                // Everything else belongs to a feature. Nothing is accepted before the link is
                // granted, so an unlinked client cannot reach feature code at all.
                Action<ulong, byte[]> featureHandler;
                LinkedClient client;
                if (protocolVersion == FactionsWire.ProtocolVersion
                    && _linked.TryGetValue(senderSteamId, out client)
                    && client.HomeServerId == homeServerId
                    && Handlers.TryGetValue(kind, out featureHandler))
                {
                    featureHandler(senderSteamId, data);
                }
                return;
            }

            if (protocolVersion != FactionsWire.ProtocolVersion)
            {
                Refuse(senderSteamId, homeServerId, "protocol " + protocolVersion + " != " + FactionsWire.ProtocolVersion);
                if (homeServerId == 0) NotifyProtocolMismatch(senderSteamId, protocolVersion);
                return;
            }

            string clientVersion;
            if (!FactionsWire.TryReadUplinkRequest(data, out clientVersion))
            {
                Refuse(senderSteamId, homeServerId, "unreadable payload");
                return;
            }

            WireFeatures features = _features != null ? _features() : WireFeatures.None;
            if (homeServerId != 0) features &= ~RemoteWithheldFeatures;

            LinkedClient previous;
            if (_linked.TryGetValue(senderSteamId, out previous) && previous.HomeServerId != homeServerId)
            {
                // A player who crossed a sector boundary hellos again from the new server; the new
                // home supersedes the old. The old agent learns nothing and needs to learn nothing:
                // its own roster sweep drops the player.
                Write("UPLINK_MOVED steam=" + senderSteamId + " from=" + previous.HomeServerId + " to=" + homeServerId);
            }

            _linked[senderSteamId] = new LinkedClient
            {
                ClientVersion = clientVersion,
                LinkedUtc = DateTime.UtcNow,
                Features = features,
                HomeServerId = homeServerId,
            };
            Write("UPLINK_REQUEST steam=" + senderSteamId + " client=" + clientVersion
                  + " protocol=" + protocolVersion + HomeSuffix(homeServerId));

            byte[] granted = FactionsWire.WriteUplinkGranted(FactionsPlugin.PluginVersion, features);
            Deliver(senderSteamId, homeServerId, granted);
            Write("UPLINK_GRANTED steam=" + senderSteamId + " features=" + features + HomeSuffix(homeServerId));

            Action<ulong> handler = ClientLinked;
            if (handler != null) handler(senderSteamId);
        }

        /// <summary>Agent: the client's server answers the hello itself, serves what is local, and
        /// forwards the rest. The grant goes out immediately with the full advertised set rather
        /// than waiting on the authority — the client gives up after five silent hellos, and an
        /// authority that is slow or down must not cost it the link to this server. The
        /// authority's own answer later either re-grants with the merged set or refuses.</summary>
        private void HandleClientMessageAsAgent(ulong senderSteamId, byte[] data)
        {
            WireMessageKind kind;
            int protocolVersion;
            if (!FactionsWire.TryReadHeader(data, out kind, out protocolVersion))
            {
                Write("UPLINK_REFUSED steam=" + senderSteamId + " reason=malformed");
                return;
            }
            if (kind != WireMessageKind.UplinkRequest)
            {
                LinkedClient client;
                if (protocolVersion != FactionsWire.ProtocolVersion || !_linked.TryGetValue(senderSteamId, out client)) return;

                // Local features answer here; everything else is the authority's.
                Action<ulong, byte[]> featureHandler;
                if (Handlers.TryGetValue(kind, out featureHandler))
                {
                    featureHandler(senderSteamId, data);
                    return;
                }
                if (Array.IndexOf(ReplayableKinds, kind) >= 0)
                {
                    if (client.LastRequests == null) client.LastRequests = new Dictionary<WireMessageKind, byte[]>();
                    client.LastRequests[kind] = data;
                    _linked[senderSteamId] = client;
                }
                Forward(RelayKind.ClientMessage, senderSteamId, data, kind.ToString());
                return;
            }

            if (protocolVersion != FactionsWire.ProtocolVersion)
            {
                Refuse(senderSteamId, 0, "protocol " + protocolVersion + " != " + FactionsWire.ProtocolVersion);
                NotifyProtocolMismatch(senderSteamId, protocolVersion);
                return;
            }
            string clientVersion;
            if (!FactionsWire.TryReadUplinkRequest(data, out clientVersion))
            {
                Refuse(senderSteamId, 0, "unreadable payload");
                return;
            }

            WireFeatures features = _features != null ? _features() : WireFeatures.None;
            _linked[senderSteamId] = new LinkedClient
            {
                ClientVersion = clientVersion,
                LinkedUtc = DateTime.UtcNow,
                Features = features,
                HomeServerId = 0,
                LastHello = data,
            };
            Write("UPLINK_REQUEST steam=" + senderSteamId + " client=" + clientVersion
                  + " protocol=" + protocolVersion + " via=agent");
            SendLocal(senderSteamId, FactionsWire.WriteUplinkGranted(FactionsPlugin.PluginVersion, features));
            Write("UPLINK_GRANTED steam=" + senderSteamId + " features=" + features + " via=agent");

            Action<ulong> handler = ClientLinked;
            if (handler != null) handler(senderSteamId);

            Forward(RelayKind.ClientMessage, senderSteamId, data, "hello");
        }

        private void Refuse(ulong steamId, byte homeServerId, string reason)
        {
            _linked.Remove(steamId);
            byte[] refused = FactionsWire.WriteUplinkRefused(reason);
            Deliver(steamId, homeServerId, refused);
            Write("UPLINK_REFUSED steam=" + steamId + " reason=" + reason + HomeSuffix(homeServerId));
        }

        /// <summary>Tells a player refused for a protocol mismatch why, on screen, through SE's own
        /// notification. That reaches a client of any build, including one too old to recognise the
        /// refusal itself, which is the client a mismatch is usually about. Local players only: a
        /// relayed client is on another server, where this identity lookup finds nobody.</summary>
        private static void NotifyProtocolMismatch(ulong steamId, int clientVersion)
        {
            try
            {
                Sandbox.Game.World.MySession session = Sandbox.Game.World.MySession.Static;
                long identityId = session != null ? session.Players.TryGetIdentityId(steamId) : 0;
                if (identityId == 0) return;   // 0 broadcasts to every player
                Sandbox.Game.MyVisualScriptLogicProvider.ShowNotification(
                    FactionsWire.ProtocolMismatchNotice(clientVersion, FactionsWire.ProtocolVersion),
                    ProtocolNoticeMs, "Red", identityId);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Protocol mismatch notice to " + steamId + " failed.");
            }
        }

        // Long, because the refusal lands while the player is still settling in after load.
        private const int ProtocolNoticeMs = 20000;

        /// <summary>Routes one payload to a client by its home: this server's own transport for a
        /// local client, the relay for a remote one.</summary>
        private bool Deliver(ulong steamId, byte homeServerId, byte[] payload)
        {
            if (homeServerId == 0) return SendLocal(steamId, payload);
            if (_relay == null || _role != NexusRole.Authority) return false;
            byte[] frame = NexusRelayWire.Write(RelayKind.ServerMessage, _thisServerId, steamId, payload);
            bool ok = _relay.TrySend(homeServerId, frame);
            if (DebugTraffic) Write("RELAY_OUT steam=" + steamId + " home=" + homeServerId + " bytes=" + payload.Length + (ok ? "" : " FAILED"));
            return ok;
        }

        private bool SendLocal(ulong steamId, byte[] payload)
        {
            try
            {
                return MyAPIGateway.Multiplayer.SendMessageTo(FactionsWire.ChannelId, payload, steamId, true);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Uplink send to " + steamId + " failed.");
                return false;
            }
        }

        /// <summary>Agent: wraps and hands one frame to the authority.</summary>
        private bool Forward(RelayKind kind, ulong steamId, byte[] payload, string what)
        {
            if (_relay == null || _role != NexusRole.Agent) return false;
            byte[] frame = NexusRelayWire.Write(kind, _thisServerId, steamId, payload);
            bool ok = _relay.TrySend(_authorityServerId, frame);
            if (DebugTraffic || !ok)
                Write("RELAY_FORWARD steam=" + steamId + " what=" + what + " to=" + _authorityServerId + (ok ? "" : " FAILED (" + (_relay.ApiAvailable ? "nexus refused" : "nexus api not published") + ")"));
            return ok;
        }

        private static string HomeSuffix(byte homeServerId)
        {
            return homeServerId == 0 ? "" : " home=" + homeServerId;
        }

        public string RelayStatus()
        {
            return "role=" + _role + " linked=" + LinkedCount + " remote=" + RemoteCount
                 + (_relay != null ? " " + _relay.Status() : "");
        }

        /// <summary>Link-negotiation events: always logged.</summary>
        private void Write(string line)
        {
            if (_log != null) _log(line);
            FactionsPlugin.Log.Info(line);
        }

        /// <summary>Per-message feature traffic. Callers must test <see cref="DebugTraffic"/>
        /// themselves before building the message — this check is a backstop, not the guard.</summary>
        public void WriteTraffic(string line)
        {
            if (!DebugTraffic) return;
            Write(line);
        }

        private struct LinkedClient
        {
            public string ClientVersion;
            public DateTime LinkedUtc;
            /// <summary>What this client was actually granted. Recorded per client rather than
            /// re-read from the supplier, because a feature that changes what the SERVER withholds
            /// must be answerable for a client that linked before the change.</summary>
            public WireFeatures Features;
            /// <summary>0 for a client connected to this server; otherwise the Nexus ID of the
            /// server relaying for it.</summary>
            public byte HomeServerId;
            /// <summary>Agent only: the client's hello, kept for a resync.</summary>
            public byte[] LastHello;
            /// <summary>Agent only: the last request per replayable kind, kept for a resync.</summary>
            public Dictionary<WireMessageKind, byte[]> LastRequests;
            /// <summary>Agent only: the authority has answered this client's hello.</summary>
            public bool AuthorityLinked;
        }
    }
}
