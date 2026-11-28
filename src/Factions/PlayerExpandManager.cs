using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// Server side of the in-flight placement readout and the Expand it commits (FAC-197;
    /// docs/economy-territory.md → "Expanding — creating a node where there is none").
    ///
    /// <para>Everything is computed here, from the player's own live position: separation is authored
    /// per region and per body, and the client has neither the feature catalogue nor the resolved
    /// planet anchors to derive any of it. The commit therefore carries no position at all — only the
    /// id of the quote the player was looking at — so there is no client-supplied point for the
    /// server to trust or to have to validate.</para>
    ///
    /// <para><b>Occupied says only "occupied".</b> Which node blocks a site, and how far off it is,
    /// are deliberately never sent: a ship scanner reaches 50/100 km against 500 km of deep-space
    /// separation, so a player on a legal-looking deep-space site routinely cannot see what blocks
    /// them. That blind case is structural at these numbers, and flying is what resolves it.</para>
    /// </summary>
    internal sealed class PlayerExpandManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the placement readout open, and the last quote id sent to each. A
        /// commit must echo the current id: a window left open across a flight is showing a verdict
        /// the ground no longer supports, and must not be able to buy it.</summary>
        private readonly Dictionary<ulong, int> _streaming = new Dictionary<ulong, int>();

        /// <summary>Monotonic across the whole manager rather than per client. It only has to be
        /// unguessable-by-accident within one subscriber, and one counter cannot repeat a value a
        /// client still holds from an earlier subscription.</summary>
        private int _quoteSeq;

        public PlayerExpandManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink != null) _uplink.RegisterHandler(WireMessageKind.ExpandRequest, OnExpandRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnExpandRequest(ulong steamId, byte[] data)
        {
            WireExpandAction action;
            int quoteId;
            if (!FactionsWire.TryReadExpandRequest(data, out action, out quoteId))
            {
                FactionsPlugin.Log.Warn("Expand request from " + steamId + " was unreadable.");
                return;
            }

            switch (action)
            {
                case WireExpandAction.Subscribe:
                    _streaming[steamId] = 0;
                    _unresolved.Remove(steamId);
                    if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("EXPAND_SUBSCRIBE steam=" + steamId);
                    SendStatus(steamId);
                    return;

                case WireExpandAction.Unsubscribe:
                    _streaming.Remove(steamId);
                    if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("EXPAND_UNSUBSCRIBE steam=" + steamId);
                    return;

                case WireExpandAction.Commit:
                    Commit(steamId, quoteId);
                    return;
            }
        }

        /// <summary>Runs on the scanner cadence, and does nothing at all while nobody has the readout
        /// open — the normal case.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            // Copied, because SendStatus rewrites the entry it is iterating.
            var subscribers = new List<ulong>(_streaming.Keys);
            foreach (ulong steamId in subscribers) SendStatus(steamId);
        }

        private void SendStatus(ulong steamId)
        {
            long identityId;
            Vec3 posKm;
            MyFaction seFaction;
            string cause;
            if (!TryResolve(steamId, out identityId, out posKm, out seFaction, out cause))
            {
                NoteUnresolved(steamId, identityId, cause);
                return;
            }
            _unresolved.Remove(steamId);

            PlayerExpandQuote quote = _runtime.QuoteExpandForPlayer(
                _runtime.FindFaction(seFaction.Tag), seFaction.Tag,
                identityId, seFaction.FactionId, posKm);
            if (quote == null) return;

            int quoteId = ++_quoteSeq;
            _streaming[steamId] = quoteId;
            // A site with no ground under it is the one refusal a readout can already name.
            WireExpandVerdict readout = quote.Probe != null && quote.Probe.NoGround
                ? WireExpandVerdict.NoGround : WireExpandVerdict.Ok;
            _uplink.Send(steamId, Encode(quoteId, quote, false, readout, null));
        }

        private void Commit(ulong steamId, int quoteId)
        {
            int current;
            if (!_streaming.TryGetValue(steamId, out current) || current == 0 || current != quoteId)
            {
                // Not an error worth a log line: a client that reconnected, or one acting on a status
                // the flight has already invalidated. Answer with a fresh quote so the window
                // re-reads rather than sits on a verdict the server has stopped standing behind.
                SendStatus(steamId);
                return;
            }

            long identityId;
            Vec3 posKm;
            MyFaction seFaction;
            string cause;
            if (!TryResolve(steamId, out identityId, out posKm, out seFaction, out cause))
            {
                NoteUnresolved(steamId, identityId, cause);
                return;
            }

            if (!PlayerAuthority.MayCommand(identityId, seFaction.Tag))
            {
                PlayerExpandQuote refused = _runtime.QuoteExpandForPlayer(
                    _runtime.FindFaction(seFaction.Tag), seFaction.Tag,
                    identityId, seFaction.FactionId, posKm);
                if (refused == null) return;
                _streaming[steamId] = 0;
                _uplink.Send(steamId, Encode(++_quoteSeq, refused, true, WireExpandVerdict.NotLeader, null));
                return;
            }

            PlayerExpandResult result = _runtime.ExpandForPlayer(
                _runtime.FindFaction(seFaction.Tag), seFaction.Tag,
                identityId, seFaction.FactionId, posKm);
            if (result == null) return;

            // Spend the quote whatever the outcome, so a refusal cannot be retried by re-sending the
            // same commit, and a success cannot be sent twice.
            _streaming[steamId] = 0;

            _uplink.Send(steamId, Encode(++_quoteSeq, result.Quote, true, Verdict(result.Refusal),
                                         result.Node != null ? result.Node.Id : null));
            if (UplinkManager.DebugTraffic)
            {
                _uplink.WriteTraffic("EXPAND_COMMIT steam=" + steamId + " tag=" + seFaction.Tag
                                     + " verdict=" + result.Refusal
                                     + " node=" + (result.Node != null ? result.Node.Id : "-"));
            }
            SendStatus(steamId);
        }

        private static byte[] Encode(int quoteId, PlayerExpandQuote quote, bool committed,
                                     WireExpandVerdict verdict, string nodeId)
        {
            NodePlacementProbe p = quote.Probe;
            return FactionsWire.WriteExpandStatus(
                quoteId, p.Clear, (byte)p.Zone, p.SeparationKm,
                p.PlacedKm.X, p.PlacedKm.Y, p.PlacedKm.Z, p.SnapDistanceKm,
                quote.CostCredits, false, committed, verdict, nodeId);
        }

        private static WireExpandVerdict Verdict(PlayerExpandRefusal refusal)
        {
            switch (refusal)
            {
                case PlayerExpandRefusal.Occupied:            return WireExpandVerdict.Occupied;
                case PlayerExpandRefusal.EconomyUnavailable:  return WireExpandVerdict.EconomyUnavailable;
                case PlayerExpandRefusal.ChargeRefused:       return WireExpandVerdict.ChargeRefused;
                case PlayerExpandRefusal.NoFaction:           return WireExpandVerdict.NoFaction;
                case PlayerExpandRefusal.NoGround:            return WireExpandVerdict.NoGround;
                default:                                      return WireExpandVerdict.Ok;
            }
        }

        /// <summary>The subscriber's identity, position and SE faction. A player with no character
        /// (respawn screen) or no faction has nothing to be told, and is left subscribed rather than
        /// dropped — both states are transient and the window is still open.
        ///
        /// <para>A player connected to a Nexus agent is not online here: they resolve to this
        /// server's identity for their Steam id and stand where their agent last reported them, so
        /// the readout quotes and the Expand commits from Main or Day as it does here.</para></summary>
        private bool TryResolve(ulong steamId, out long identityId, out Vec3 posKm, out MyFaction seFaction,
                                out string cause)
        {
            identityId = 0L;
            posKm = new Vec3(0, 0, 0);
            seFaction = null;
            cause = null;

            MyPlayer player = null;
            foreach (MyPlayer candidate in MySession.Static.Players.GetOnlinePlayers())
            {
                if (candidate == null || candidate.IsBot || candidate.Identity == null) continue;
                if (candidate.Id.SteamId != steamId) continue;
                player = candidate;
                break;
            }

            VRageMath.Vector3D world;
            if (player != null)
            {
                IMyCharacter ch = player.Character;
                if (ch == null) { cause = "character"; return false; }
                identityId = player.Identity.IdentityId;
                world = ch.PositionComp.GetPosition();
            }
            else
            {
                byte home;
                identityId = LinkedViewers.IdentityFor(steamId);
                if (identityId == 0L) { cause = "identity"; return false; }
                if (!LinkedViewers.TryRemotePosition(steamId, out world, out home)) { cause = "position"; return false; }
            }
            posKm = new Vec3(world.X / 1000.0, world.Y / 1000.0, world.Z / 1000.0);

            seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (seFaction == null) { cause = "faction"; return false; }

            FactionState owner = _runtime.FindFaction(seFaction.Tag);
            // An NPC tag the brain drives: its territory is the brain's to expand, and the chat verb
            // refuses this same case in the same words.
            if (owner != null && !owner.IsPlayerFaction) { cause = "npc:" + seFaction.Tag; return false; }
            return true;
        }

        /// <summary>Subscribers whose last status could not be resolved, and why; a cause is logged
        /// when it first appears or changes, not on every cadence.</summary>
        private readonly Dictionary<ulong, string> _unresolved = new Dictionary<ulong, string>();

        private void NoteUnresolved(ulong steamId, long identityId, string cause)
        {
            string last;
            if (_unresolved.TryGetValue(steamId, out last) && last == cause) return;
            _unresolved[steamId] = cause;
            // A player in no faction has nothing to quote, and the readout would otherwise wait on
            // "checking..." for an answer that never comes. Sent once, as a commit's answer, because
            // that is the form every client version announces.
            if (cause == "faction")
                _uplink.Send(steamId, FactionsWire.WriteExpandStatus(++_quoteSeq, false, 0, 0, 0, 0, 0, 0, 0, false,
                                                                     true, WireExpandVerdict.NoFaction, null));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("EXPAND_UNRESOLVED steam=" + steamId + " identity=" + identityId
                                     + " remote=" + (_uplink.IsRemote(steamId) ? "yes" : "no") + " cause=" + cause);
        }

        /// <summary>Drops subscribers that are no longer online, on the same cadence and for the same
        /// reason <see cref="UplinkManager.PruneOffline"/> drops links.</summary>
        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_streaming.Count == 0) return;
            List<ulong> gone = null;
            foreach (ulong steamId in _unresolved.Keys)
                if (!onlineSteamIds.Contains(steamId)) { if (gone == null) gone = new List<ulong>(); gone.Add(steamId); }
            if (gone != null) foreach (ulong steamId in gone) _unresolved.Remove(steamId);
            List<ulong> stale = null;
            foreach (ulong steamId in _streaming.Keys)
            {
                if (onlineSteamIds.Contains(steamId)) continue;
                if (stale == null) stale = new List<ulong>();
                stale.Add(steamId);
            }
            if (stale == null) return;
            foreach (ulong steamId in stale) _streaming.Remove(steamId);
        }

        public void Close()
        {
            _streaming.Clear();
        }
    }
}
