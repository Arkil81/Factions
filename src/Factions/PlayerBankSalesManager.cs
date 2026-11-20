using System;
using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Server side of the Bank sales window: every item the central bank buys, what the viewing
    /// player's own faction holds of it, and the rule the faction set for it
    /// (<see cref="FactionState.SetCentralBankRule"/>). A linked client subscribes when the window opens
    /// and unsubscribes when it closes, so a player who never opens it costs nothing.
    ///
    /// <para><b>Re-sent at most every <see cref="ResendSeconds"/></b>, and at once after a command.
    /// The list is the whole bank-buyable catalogue -- hundreds of rows in a modded world -- and what
    /// moves between edits is only the Held column, which a bank pass or a delivery changes slowly.
    /// The scanner cadence the Store window uses would send the same large payload every few
    /// seconds for nothing.</para>
    ///
    /// <para>Only the player's OWN faction is ever described or changed.</para>
    /// </summary>
    internal sealed class PlayerBankSalesManager
    {
        private const double ResendSeconds = 10.0;

        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        /// <summary>SteamIDs with the window open, and when each was last sent a snapshot.</summary>
        private readonly Dictionary<ulong, DateTime> _streaming = new Dictionary<ulong, DateTime>();

        public PlayerBankSalesManager(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.BankSalesRequest, OnRequest);
            _uplink.RegisterHandler(WireMessageKind.BankSalesCommandRequest, OnCommandRequest);
        }

        private void OnRequest(ulong steamId, byte[] data)
        {
            bool stream;
            if (!FactionsWire.TryReadBankSalesRequest(data, out stream))
            {
                FactionsPlugin.Log.Warn("Bank sales request from " + steamId + " was unreadable.");
                return;
            }

            if (stream)
            {
                _streaming[steamId] = DateTime.MinValue;
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("BANK_SALES_SUBSCRIBE steam=" + steamId);
                SendNow(steamId);
            }
            else
            {
                _streaming.Remove(steamId);
                if (UplinkManager.DebugTraffic) _uplink.WriteTraffic("BANK_SALES_UNSUBSCRIBE steam=" + steamId);
            }
        }

        /// <summary>Runs on the scanner cadence; does nothing while no one has the window open.</summary>
        public void Tick()
        {
            if (_streaming.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            foreach (ulong steamId in new List<ulong>(_streaming.Keys))
                if ((now - _streaming[steamId]).TotalSeconds >= ResendSeconds) SendNow(steamId);
        }

        private void SendNow(ulong steamId)
        {
            if (_runtime == null || _runtime.World == null || MySession.Static == null) return;
            _streaming[steamId] = DateTime.UtcNow;

            FactionState owner = ViewerFaction(IdentityFor(steamId));
            var rows = new List<WireBankSalesRow>();
            if (owner != null)
            {
                foreach (BankSalesRow row in BankSalesReport.Build(_runtime.World, owner, _runtime.BankBuyableKeys()))
                {
                    rows.Add(new WireBankSalesRow
                    {
                        Key = row.Key,
                        Name = _runtime.BankItemName(row.Key),
                        Category = (byte)row.Category,
                        Held = row.Held,
                        UnitValue = row.UnitValue,
                        Mode = (byte)row.Mode,
                        Target = row.Target,
                    });
                }
            }
            _uplink.Send(steamId, FactionsWire.WriteBankSalesSnapshot(owner != null ? owner.Tag : "", rows));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("BANK_SALES_SNAPSHOT steam=" + steamId + " rows=" + rows.Count
                                     + " rules=" + (owner != null ? owner.CentralBankRuleCount : 0));
        }

        private void OnCommandRequest(ulong steamId, byte[] data)
        {
            byte mode;
            long target;
            List<string> keys;
            if (!FactionsWire.TryReadBankSalesCommandRequest(data, out mode, out target, out keys))
            {
                FactionsPlugin.Log.Warn("Bank sales command from " + steamId + " was unreadable.");
                return;
            }

            string message = Run(steamId, mode, target, keys);
            _uplink.Send(steamId, FactionsWire.WriteBankSalesCommandResult(message != null, message ?? "No change."));
            if (_streaming.ContainsKey(steamId)) SendNow(steamId);

            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("BANK_SALES_COMMAND steam=" + steamId + " mode=" + mode + " target=" + target
                                     + " keys=" + keys.Count);
        }

        private string Run(ulong steamId, byte mode, long target, List<string> keys)
        {
            if (_runtime == null || _runtime.World == null) return "Factions runtime not ready.";
            long identityId = IdentityFor(steamId);
            if (identityId == 0L) return "Could not identify you on this server.";
            FactionState owner = ViewerFaction(identityId);
            if (owner == null) return "Your faction is not registered with Factions yet.";
            string refusal = PlayerAuthority.SpendRefusal(identityId, owner.Tag);
            if (refusal != null) return refusal;
            if (mode > (byte)BankSaleMode.DontSell) return "Unknown bank-sale rule.";
            return _runtime.SetCentralBankRuleForPlayer(owner, keys, (BankSaleMode)mode, target);
        }

        /// <summary>Mirrors <see cref="PlayerShelfManager"/>'s: a registered player faction only, so an
        /// NPC tag is never reachable from a client.</summary>
        private FactionState ViewerFaction(long identityId)
        {
            if (identityId == 0L || MySession.Static == null || MySession.Static.Factions == null) return null;
            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (seFaction == null) return null;
            FactionState owner = _runtime.FindFaction(seFaction.Tag);
            return owner != null && owner.IsPlayerFaction ? owner : null;
        }

        private static long IdentityFor(ulong steamId)
        {
            if (MySession.Static == null) return 0L;
            return LinkedViewers.IdentityFor(steamId);
        }

        public void PruneOffline(ICollection<ulong> onlineSteamIds)
        {
            if (_streaming.Count == 0) return;
            foreach (ulong steamId in new List<ulong>(_streaming.Keys))
                if (!onlineSteamIds.Contains(steamId)) _streaming.Remove(steamId);
        }

        public void Close()
        {
            _streaming.Clear();
        }
    }
}
