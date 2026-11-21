using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// A resolved fight, carried from the Nexus authority, which resolves every fight, to an agent,
    /// whose players are the ones who could have seen a fight in its sectors. Carries what the
    /// agent needs to choose those viewers and their tone (<see cref="CombatOutcome.Recipients"/>)
    /// and the sentence to tell them.
    /// </summary>
    public sealed class NexusCombatCall : NexusRelayedCall
    {
        public string Sentence;
        public double Px, Py, Pz;
        public string Attacker;
        public string Defender;
        /// <summary>Every defending faction besides <see cref="Defender"/>; null when none.</summary>
        public List<string> Defenders;
        public int Attack;
        public int Defence;

        public static NexusCombatCall From(CombatOutcome o, string sentence)
        {
            if (o == null || string.IsNullOrEmpty(sentence)) return null;
            return new NexusCombatCall
            {
                Sentence = sentence,
                Px = o.PositionKm.X, Py = o.PositionKm.Y, Pz = o.PositionKm.Z,
                Attacker = o.AttackerTag, Defender = o.DefenderTag,
                Defenders = o.DefenderTags != null && o.DefenderTags.Count > 0 ? new List<string>(o.DefenderTags) : null,
                Attack = (int)o.Attack, Defence = (int)o.Defence,
            };
        }

        /// <summary>The outcome as far as choosing its viewers and their tone needs it.</summary>
        public CombatOutcome ToOutcome()
        {
            var o = new CombatOutcome
            {
                PositionKm = new Vec3(Px, Py, Pz),
                AttackerTag = Attacker, DefenderTag = Defender,
                Attack = (CombatAttackResult)Attack, Defence = (CombatDefenceResult)Defence,
            };
            if (Defenders != null) o.DefenderTags.AddRange(Defenders);
            return o;
        }
    }

    /// <summary>Authority: the fights of the last few seconds, handed to each agent once.</summary>
    public sealed class NexusCombatRelay : NexusFreshRelay<NexusCombatCall> { }
}
