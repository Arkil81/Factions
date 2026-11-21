using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the FactionsUI diplomacy surfaces: the stance line on the terminal's Factions
    /// page and the Diplomacy window (docs/diplomacy-plan.md, rule 5; docs/client-plugin.md,
    /// Part 17). A linked client subscribes while either is up, and is sent its own faction's
    /// readout on subscribe, after each command, and whenever a row changes.
    ///
    /// <para><b>Change-driven on the scanner cadence.</b> Every tick the snapshot is rebuilt for each
    /// subscriber and compared, byte for byte, with the last one sent to them, written with the
    /// server clock zeroed so the clock alone never counts as a change. A stance moves only when a
    /// leader acts, but the NPC's view drifts, offers expire and truces end without anyone clicking,
    /// so a revision counter would miss most of what changes.</para>
    ///
    /// <para><b>No rule lives here.</b> The rows are <see cref="PlayerDiplomacy.Readout"/>, the same
    /// rows the chat <c>status</c> verb formats, and a command goes through
    /// <see cref="PlayerDiplomacyAccess.Act"/>, the door the chat verb uses: the caller's own faction
    /// from SE, the leader check, then <see cref="FactionRuntime.LeaderDiplomacy(FactionState, LeaderAct, string, out bool)"/>.
    /// Nothing the client sends is trusted beyond the act and the NPC tag.</para>
    ///
    /// <para>Granted on a standalone server and on the authority, relayed clients included: the
    /// caller's identity comes from <see cref="LinkedViewers"/>, which resolves a player connected to
    /// a Nexus agent, and the leader check reads SE's faction table by identity alone.</para>
    /// </summary>
    internal sealed class PlayerDiplomacyManager
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs subscribed, and the zero-clock payload last sent to each.</summary>
        private readonly Dictionary<ulong, byte[]> _streaming = new Dictionary<ulong, byte[]>();

        public PlayerDiplomacyManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.DiplomacyRequest, OnRequest);
            _uplink.RegisterHandler(WireMessageKind.DiplomacyCommandRequest, OnCommandRequest);
        }

        public int SubscriberCount { get { return _streaming.Count; } }

        private void OnRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadDiplomacyRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Diplomacy request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                // A fresh subscribe always gets a snapshot, even one identical to the last: the
                // client dropped its copy when it unsubscribed.
                _streaming[steamId] = null;
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("DIPLOMACY_SUBSCRIBE steam=" + steamId);
                Tick();
            }
            else
            {
                _streaming.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("DIPLOMACY_UNSUBSCRIBE steam=" + steamId);
            }
        }

        /// <summary>Runs on the scanner cadence, and does nothing while no one is subscribed.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;

            foreach (LinkedViewer player in LinkedViewers.Collect())
            {
                ulong steamId = player.SteamId;
                byte[] last;
                if (!_streaming.TryGetValue(steamId, out last)) continue;

                long identityId = player.IdentityId;
                FactionState owner = PlayerDiplomacyAccess.CallerFaction(_runtime, identityId);
                bool leader = owner != null && PlayerDiplomacyAccess.IsLeader(identityId, owner.Tag);
                List<WireDiplomacyRow> rows = BuildRows(owner);
                long giftCost = _runtime.Tuning != null ? Math.Max(0L, _runtime.Tuning.GiftCost) : 0L;
                long treasury = owner != null ? _runtime.World.GetTreasury(owner.Tag) : 0L;
                string tag = owner != null ? owner.Tag : "";

                byte[] key = FactionsWire.WriteDiplomacySnapshot(tag, leader, giftCost, treasury, 0L, rows);
                if (last != null && SameBytes(last, key)) continue;

                _streaming[steamId] = key;
                _uplink.Send(steamId, FactionsWire.WriteDiplomacySnapshot(
                    tag, leader, giftCost, treasury, _runtime.World.UtcNow.Ticks, rows));
                if (UplinkManager.DebugTraffic)
                    _uplink.WriteTraffic("DIPLOMACY_SNAPSHOT steam=" + steamId + " tag=" + tag
                                         + " rows=" + rows.Count + " leader=" + leader);
            }
        }

        /// <summary><see cref="PlayerDiplomacy.Readout"/> mapped onto the wire, one row per NPC the
        /// faction can deal with. Empty for a viewer in no registered player faction.</summary>
        private List<WireDiplomacyRow> BuildRows(FactionState owner)
        {
            var rows = new List<WireDiplomacyRow>();
            if (owner == null) return rows;
            foreach (DiplomacyReadoutRow r in PlayerDiplomacy.Readout(owner, _runtime.States, _runtime.Tuning, _runtime.World.UtcNow))
                rows.Add(ToWire(r));
            return rows;
        }

        internal static WireDiplomacyRow ToWire(DiplomacyReadoutRow r)
        {
            return new WireDiplomacyRow
            {
                NpcTag = r.NpcTag ?? "",
                TheirView = r.TheirView,
                TheirBand = ToWire(r.TheirBand),
                YourStance = r.YourStance,
                YourBand = ToWire(r.YourBand),
                EmbargoByYou = r.EmbargoByYou,
                EmbargoByThem = r.EmbargoByThem,
                TradeBarred = r.TradeBarred,
                Allied = r.Allied,
                Implacable = r.Implacable,
                TruceUntilTicks = r.TruceUntilUtc.HasValue ? r.TruceUntilUtc.Value.Ticks : 0L,
                YouOffered = r.YouOffered ?? "",
                YouOfferedExpiresTicks = r.YouOfferedExpiresUtc.HasValue ? r.YouOfferedExpiresUtc.Value.Ticks : 0L,
                TheyOffered = r.TheyOffered ?? "",
                TheyOfferedExpiresTicks = r.TheyOfferedExpiresUtc.HasValue ? r.TheyOfferedExpiresUtc.Value.Ticks : 0L,
                GiftReadyTicks = r.GiftReadyUtc.HasValue ? r.GiftReadyUtc.Value.Ticks : 0L,
            };
        }

        private static WireStanceBand ToWire(StanceBand band)
        {
            switch (band)
            {
                case StanceBand.Hostile:  return WireStanceBand.Hostile;
                case StanceBand.Friendly: return WireStanceBand.Friendly;
                default:                  return WireStanceBand.Neutral;
            }
        }

        /// <summary>The wire act as Core's. Mapped by name, not cast, so a renumbering on either
        /// side cannot silently turn one act into another.</summary>
        internal static bool TryMap(WireDiplomacyAct act, out LeaderAct mapped)
        {
            switch (act)
            {
                case WireDiplomacyAct.Status:        mapped = LeaderAct.Status; return true;
                case WireDiplomacyAct.War:           mapped = LeaderAct.War; return true;
                case WireDiplomacyAct.Embargo:       mapped = LeaderAct.Embargo; return true;
                case WireDiplomacyAct.Lift:          mapped = LeaderAct.Lift; return true;
                case WireDiplomacyAct.Gift:          mapped = LeaderAct.Gift; return true;
                case WireDiplomacyAct.Peace:         mapped = LeaderAct.Peace; return true;
                case WireDiplomacyAct.NonAggression: mapped = LeaderAct.NonAggression; return true;
                case WireDiplomacyAct.Alliance:      mapped = LeaderAct.Alliance; return true;
                case WireDiplomacyAct.Withdraw:      mapped = LeaderAct.Withdraw; return true;
                default:                             mapped = LeaderAct.Status; return false;
            }
        }

        // ---- Commands -------------------------------------------------------------------------

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            WireDiplomacyAct act;
            string npcTag;
            if (!FactionsWire.TryReadDiplomacyCommandRequest(data, out act, out npcTag))
            {
                FactionsPlugin.Log.Warn("Diplomacy command from " + steamId + " was unreadable.");
                return;
            }

            bool done;
            string message = Run(steamId, act, npcTag, out done);
            _uplink.Send(steamId, FactionsWire.WriteDiplomacyCommandResult(done, npcTag, message));

            // Without waiting out the cadence: the player clicked and is looking at the row. Every
            // other subscriber in the same faction is picked up by the same pass.
            Tick();

            // Behind the uplink debug gate: the act itself is already in the Torch log as its
            // DIPLOMACY_* line, written by FactionRuntime for this path and the chat verb alike.
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("DIPLOMACY_COMMAND steam=" + steamId + " act=" + act
                                     + " target=" + npcTag + " done=" + done);
        }

        private string Run(ulong steamId, WireDiplomacyAct act, string npcTag, out bool done)
        {
            done = false;
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";

            LeaderAct mapped;
            if (!TryMap(act, out mapped)) return "Unknown diplomacy act.";

            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";

            FactionState owner = PlayerDiplomacyAccess.CallerFaction(_runtime, identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";

            return PlayerDiplomacyAccess.Act(_runtime, owner, identityId, mapped, npcTag, out done);
        }

        private static long IdentityFor(ulong steamId)
        {
            if (MySession.Static == null) return 0L;
            return LinkedViewers.IdentityFor(steamId);
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

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
