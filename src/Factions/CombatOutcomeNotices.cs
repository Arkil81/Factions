using System.Collections.Generic;
using Factions.Core;
using Factions.Wire;

namespace Factions
{
    /// <summary>
    /// Puts a resolved fight on the FactionsUI HUD of every linked player who could see it
    /// (<see cref="WireMessageKind.CombatOutcomeNotice"/>), in the tone their faction reads it in.
    /// <see cref="FactionRuntime.PublishCombatOutcomes"/> decides who could see it and the tone;
    /// this only sends, to clients granted <see cref="WireFeatures.CombatOutcome"/>. Behind
    /// <c>factions debug uplink</c>, one <c>COMBAT_NOTICE steam=&lt;id&gt; tone=&lt;tone&gt;
    /// sent=&lt;bool&gt;</c> line per viewer. A disabled hull's kill confirmation rides the same
    /// notice to the player who disabled it.
    /// </summary>
    internal sealed class CombatOutcomeNotices
    {
        private readonly FactionRuntime _runtime;
        private readonly UplinkManager _uplink;

        public CombatOutcomeNotices(FactionRuntime runtime, UplinkManager uplink)
        {
            _runtime = runtime;
            _uplink = uplink;
            if (_runtime != null && _uplink != null)
            {
                _runtime.CombatNoticeSink = Send;
                _runtime.KillNoticeSink = SendKill;
            }
        }

        public void Close()
        {
            if (_runtime == null) return;
            _runtime.CombatNoticeSink = null;
            _runtime.KillNoticeSink = null;
        }

        /// <summary>A kill confirmation for the one player whose fire disabled a hull
        /// (<see cref="FactionRuntime"/>'s disabled-hull write-off), on the same HUD notice.</summary>
        private void SendKill(ulong steamId, CombatTone tone, string sentence)
        {
            if ((_uplink.GrantedFeatures(steamId) & WireFeatures.CombatOutcome) == 0) return;
            bool sent = _uplink.Send(steamId, FactionsWire.WriteCombatOutcomeNotice((WireCombatTone)(byte)tone, sentence));
            if (UplinkManager.DebugTraffic)
                _uplink.WriteTraffic("COMBAT_NOTICE steam=" + steamId + " tone=" + tone + " sent=" + sent + " kill");
        }

        private void Send(CombatOutcome outcome, string sentence, List<CombatNotice> notices)
        {
            for (int i = 0; i < notices.Count; i++)
            {
                CombatNotice n = notices[i];
                if ((_uplink.GrantedFeatures(n.SteamId) & WireFeatures.CombatOutcome) == 0) continue;
                bool sent = _uplink.Send(n.SteamId, FactionsWire.WriteCombatOutcomeNotice((WireCombatTone)(byte)n.Tone, sentence));
                if (UplinkManager.DebugTraffic)
                    _uplink.WriteTraffic("COMBAT_NOTICE steam=" + n.SteamId + " tone=" + n.Tone + " sent=" + sent
                                         + " " + (outcome.AttackerFleet ?? "?") + " @" + (outcome.NodeId ?? "?"));
            }
        }
    }
}
