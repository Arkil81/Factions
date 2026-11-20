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
            MyPlayer player;
            Vec3 posKm;
            MyFaction seFaction;
            if (!TryResolve(steamId, out player, out posKm, out seFaction)) return;

            PlayerExpandQuote quote = _runtime.QuoteExpandForPlayer(
                _runtime.FindFaction(seFaction.Tag), seFaction.Tag,
                player.Identity.IdentityId, seFaction.FactionId, posKm);
            if (quote == null) return;

            int quoteId = ++_quoteSeq;
            _streaming[steamId] = quoteId;
            _uplink.Send(steamId, Encode(quoteId, quote, false, WireExpandVerdict.Ok, null));
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

            MyPlayer player;
            Vec3 posKm;
            MyFaction seFaction;
            if (!TryResolve(steamId, out player, out posKm, out seFaction)) return;

            if (!PlayerAuthority.MayCommand(player.Identity.IdentityId, seFaction.Tag))
            {
                PlayerExpandQuote refused = _runtime.QuoteExpandForPlayer(
                    _runtime.FindFaction(seFaction.Tag), seFaction.Tag,
                    player.Identity.IdentityId, seFaction.FactionId, posKm);
                if (refused == null) return;
                _streaming[steamId] = 0;
                _uplink.Send(steamId, Encode(++_quoteSeq, refused, true, WireExpandVerdict.NotLeader, null));
                return;
            }

            PlayerExpandResult result = _runtime.ExpandForPlayer(
                _runtime.FindFaction(seFaction.Tag), seFaction.Tag,
                player.Identity.IdentityId, seFaction.FactionId, posKm);
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
                default:                                      return WireExpandVerdict.Ok;
            }
        }

        /// <summary>The subscriber's live player, position and SE faction. A player with no character
        /// (respawn screen) or no faction has nothing to be told, and is left subscribed rather than
        /// dropped — both states are transient and the window is still open.</summary>
        private bool TryResolve(ulong steamId, out MyPlayer player, out Vec3 posKm, out MyFaction seFaction)
        {
            player = null;
            posKm = new Vec3(0, 0, 0);
            seFaction = null;

            foreach (MyPlayer candidate in MySession.Static.Players.GetOnlinePlayers())
            {
                if (candidate == null || candidate.IsBot || candidate.Identity == null) continue;
                if (candidate.Id.SteamId != steamId) continue;
                player = candidate;
                break;
            }
            if (player == null) return false;

            IMyCharacter ch = player.Character;
            if (ch == null) return false;
            VRageMath.Vector3D world = ch.PositionComp.GetPosition();
            posKm = new Vec3(world.X / 1000.0, world.Y / 1000.0, world.Z / 1000.0);

            seFaction = MySession.Static.Factions.GetPlayerFaction(player.Identity.IdentityId);
            if (seFaction == null) return false;

            FactionState owner = _runtime.FindFaction(seFaction.Tag);
            // An NPC tag the brain drives: its territory is the brain's to expand, and the chat verb
            // refuses this same case in the same words.
            return owner == null || owner.IsPlayerFaction;
        }

        /// <summary>Drops subscribers that are no longer online, on the same cadence and for the same
        /// reason <see cref="UplinkManager.PruneOffline"/> drops links.</summary>
        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_streaming.Count == 0) return;
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
