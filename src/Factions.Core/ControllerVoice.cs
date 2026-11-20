using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Which recorded voice speaks for a faction's stations: its traffic callouts and its
    /// trespass hails (FAC-483). One per faction, so a faction is recognisable by ear, recorded on the
    /// faction (<see cref="FactionState.ControllerSpeaker"/>) so it survives restarts. Speaker 0 is the
    /// original controller; the library carries <see cref="Speakers"/> (scripts/atc-audio,
    /// <c>HAIL_SPEAKERS</c>).
    ///
    /// <para><b>Assigned to spread, not hashed.</b> A faction without a voice takes the one fewest
    /// factions already speak in, lowest index on a tie, and factions are assigned in tag order. A tag
    /// hash left one of the five voices unused across the 14 live factions (2026-10-17), and the point
    /// of five voices is to hear all of them.</para></summary>
    public static class ControllerVoice
    {
        public const int Speakers = 5;

        /// <summary>Gives every faction in <paramref name="states"/> without a voice the least-used one.
        /// Returns how many were assigned.</summary>
        public static int Assign(IList<FactionState> states)
        {
            if (states == null) return 0;
            var used = new int[Speakers];
            var pending = new List<FactionState>();
            for (int i = 0; i < states.Count; i++)
            {
                FactionState s = states[i];
                if (s == null || s.Tag == null) continue;
                if (s.ControllerSpeaker >= 0 && s.ControllerSpeaker < Speakers) used[s.ControllerSpeaker]++;
                else pending.Add(s);
            }
            pending.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));
            for (int i = 0; i < pending.Count; i++)
            {
                int pick = 0;
                for (int k = 1; k < Speakers; k++) if (used[k] < used[pick]) pick = k;
                pending[i].ControllerSpeaker = pick;
                used[pick]++;
            }
            return pending.Count;
        }

        /// <summary>The speaker for <paramref name="factionTag"/>, assigning any missing voices first.
        /// 0 for a tag not in the roster.</summary>
        public static byte SpeakerOf(IList<FactionState> states, string factionTag)
        {
            if (states == null || string.IsNullOrEmpty(factionTag)) return 0;
            for (int i = 0; i < states.Count; i++)
            {
                FactionState s = states[i];
                if (s == null || !string.Equals(s.Tag, factionTag, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.ControllerSpeaker < 0 || s.ControllerSpeaker >= Speakers) Assign(states);
                return (byte)Math.Max(0, s.ControllerSpeaker);
            }
            return 0;
        }
    }
}
