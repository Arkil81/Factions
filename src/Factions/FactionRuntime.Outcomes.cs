using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Tells the players who could see a fight how it came out. Core queues one
    /// <see cref="CombatOutcome"/> per meeting resolution (and logs <c>COMBAT_OUTCOME</c>); after
    /// every arrivals pass this takes them, whispers the sentence in chat to the players near the
    /// fight, and hands each viewer within <see cref="FactionTuning.ObservationRadiusKm"/> of it --
    /// with the tone their faction reads it in -- to <see cref="CombatNoticeSink"/>, which puts it
    /// on the FactionsUI HUD. See docs/fleet-events.md, "Combat outcome notices".
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>Set by the host when a client uplink exists: the outcome, its sentence, and every
        /// viewer who could see it with their tone.</summary>
        internal Action<CombatOutcome, string, List<CombatNotice>> CombatNoticeSink { get; set; }

        /// <summary>Set by a Nexus authority: every resolved fight and its sentence, for the agents
        /// whose players may be near it.</summary>
        internal Action<CombatOutcome, string> CombatRelaySink { get; set; }

        private void PublishCombatOutcomes()
        {
            List<CombatOutcome> outcomes = _world.TakeCombatOutcomes();
            if (outcomes == null) return;
            for (int i = 0; i < outcomes.Count; i++)
            {
                CombatOutcome o = outcomes[i];
                string sentence = o.Sentence();
                SendNearbyFleetMessage(o.PositionKm, sentence);
                Action<CombatOutcome, string> relay = CombatRelaySink;
                if (relay != null) relay(o, sentence);
                Action<CombatOutcome, string, List<CombatNotice>> sink = CombatNoticeSink;
                if (sink == null || _playersWithin == null) continue;
                List<CombatNotice> notices = o.Recipients(_playersWithin, _tuning.ObservationRadiusKm, PlayerFactionTag);
                if (notices.Count > 0) sink(o, sentence, notices);
            }
        }

        /// <summary>The SE faction tag of an online player, or null when they have none.</summary>
        private static string PlayerFactionTag(ulong steamId)
        {
            MySession session = MySession.Static;
            if (session == null || session.Players == null || session.Factions == null) return null;
            long identity = session.Players.TryGetIdentityId(steamId);
            if (identity == 0) return null;
            MyFaction faction = session.Factions.GetPlayerFaction(identity);
            return faction != null ? faction.Tag : null;
        }
    }
}
