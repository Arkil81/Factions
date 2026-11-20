using System;
using Factions.Core;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Definitions.SessionComponents;
using VRage.Game.ModAPI;

namespace Factions
{
    /// <summary>
    /// SE's personal-reputation field for <see cref="MemberReputationSync"/>. Game thread only.
    ///
    /// <para><b>Reading.</b> <c>GetRelationBetweenPlayerAndFaction</c> answers for a pair with no row
    /// from the faction's own default (or <c>MyPerGameSettings</c>'s), and that default is what SE
    /// itself then uses for the pair everywhere — store prices, decay, damage, the Factions screen.
    /// So a seed taken from a missing row records exactly the value SE was treating as the player's,
    /// and writing it back later makes the row explicit without changing what SE does.</para>
    ///
    /// <para><b>Writing</b> goes through <c>AddFactionPlayerReputation</c>, not
    /// <c>SetReputationBetweenPlayerAndFaction</c>: only the additive path replicates to clients (it
    /// raises the broadcast <c>AddFactionPlayerReputationSuccess</c>), and a set that never reaches
    /// the client would leave the vanilla Factions screen showing the old number — the one thing this
    /// feature exists to change. Reason <c>Decay</c>, the one value that suppresses SE's on-screen
    /// notification; <c>propagate: false</c>, so no other faction moves; <c>adminChange: true</c>, so
    /// SE's pirate gain limit neither refuses the write nor counts it against the player.</para>
    /// </summary>
    internal sealed class SePersonalReputationField : IPersonalReputationField
    {
        private static MyFactionCollection Factions
        {
            get { return MySession.Static != null ? MySession.Static.Factions : null; }
        }

        private static MySessionComponentEconomyDefinition Economy
        {
            get
            {
                MySessionComponentEconomy economy = MySession.Static != null
                    ? MySession.Static.GetComponent<MySessionComponentEconomy>() : null;
                return economy != null ? economy.EconomyDefinition : null;
            }
        }

        public bool TryRead(long identityId, string npcTag, out int reputation)
        {
            reputation = 0;
            MyFactionCollection factions = Factions;
            if (factions == null || string.IsNullOrEmpty(npcTag)) return false;
            MyFaction npc = factions.TryGetFactionByTag(npcTag);
            if (npc == null) return false;
            Tuple<MyRelationsBetweenFactions, int> rel = factions.GetRelationBetweenPlayerAndFaction(identityId, npc.FactionId);
            if (rel == null) return false;
            reputation = rel.Item2;
            return true;
        }

        public bool Write(long identityId, string npcTag, int reputation)
        {
            MyFactionCollection factions = Factions;
            if (factions == null || string.IsNullOrEmpty(npcTag)) return false;
            MyFaction npc = factions.TryGetFactionByTag(npcTag);
            if (npc == null || npc.StaticReputation || npc.FactionType == MyFactionTypes.PlayerMade) return false;

            int target = factions.ClampReputation(reputation);
            int current;
            if (!TryRead(identityId, npcTag, out current)) return false;
            if (current == target) return true;

            // GenerateChanges applies (int)(RepMult_Pos_Owner * delta). Vanilla ships 1; a world that
            // changed it would land off target, so pre-divide.
            int delta = target - current;
            float owner = OwnerMultiplier();
            if (owner > 0f && Math.Abs(owner - 1f) > 1e-6f)
                delta = (int)Math.Round(delta / owner, MidpointRounding.AwayFromZero);
            factions.AddFactionPlayerReputation(identityId, npc.FactionId, delta, ReputationChangeReason.Decay,
                                                propagate: false, adminChange: true);

            int after;
            if (TryRead(identityId, npcTag, out after) && after != target)
            {
                // Rounding residue on a non-unity multiplier: exact on the server; a client catches up
                // on the next replicated write.
                factions.SetReputationBetweenPlayerAndFaction(identityId, npc.FactionId, target, ReputationChangeReason.Decay);
                TryRead(identityId, npcTag, out after);
            }
            return after == target;
        }

        /// <summary>
        /// Re-sends the field's current value to every client, unchanged, in one broadcast.
        ///
        /// <para>An add of <b>0</b> does it. <c>GenerateChanges</c> has no zero-delta early-out: it
        /// emits one change with <c>RepTotal</c> = the current value and <c>Change</c> = 0, so
        /// <c>AddFactionPlayerReputation</c> raises <c>AddFactionPlayerReputationSuccess</c>, and each
        /// client applies <c>RepTotal</c> as an absolute value through
        /// <c>ChangeReputationWithPlayer</c>. One broadcast per (member, NPC), against two for
        /// +d then −d, and nothing to show — a zero change displays no notification, and the reason is
        /// <c>Decay</c> besides. The server re-stores the same value.</para>
        /// </summary>
        public bool Resend(long identityId, string npcTag)
        {
            MyFactionCollection factions = Factions;
            if (factions == null || string.IsNullOrEmpty(npcTag)) return false;
            MyFaction npc = factions.TryGetFactionByTag(npcTag);
            if (npc == null || npc.StaticReputation || npc.FactionType == MyFactionTypes.PlayerMade) return false;
            return factions.AddFactionPlayerReputation(identityId, npc.FactionId, 0, ReputationChangeReason.Decay,
                                                       propagate: false, adminChange: true);
        }

        public int Clamp(int reputation)
        {
            MyFactionCollection factions = Factions;
            return factions != null ? factions.ClampReputation(reputation) : reputation;
        }

        /// <summary><c>RepMult_Pos_Owner</c>: what <c>GenerateChanges</c> multiplies the target
        /// faction's delta by, for either sign (it always asks for the positive modifiers).</summary>
        public float OwnerMultiplier()
        {
            MySessionComponentEconomyDefinition d = Economy;
            return d != null ? d.RepMult_Pos_Owner : 1f;
        }

        /// <summary>The multiplier <c>GenerateChanges</c> propagates a positive delta with, from
        /// <paramref name="targetFactionId"/> to <paramref name="otherFactionId"/>, by their relation.</summary>
        public bool TryPropagationMultiplier(long targetFactionId, long otherFactionId, out float multiplier)
        {
            multiplier = 0f;
            MyFactionCollection factions = Factions;
            MySessionComponentEconomyDefinition d = Economy;
            if (factions == null || d == null) return false;
            Tuple<MyRelationsBetweenFactions, int> rel = factions.GetRelationBetweenFactions(targetFactionId, otherFactionId);
            if (rel == null) return false;
            switch (rel.Item1)
            {
                case MyRelationsBetweenFactions.Neutral: multiplier = d.RepMult_Pos_Neutral; return true;
                case MyRelationsBetweenFactions.Enemies: multiplier = d.RepMult_Pos_Enemy;   return true;
                case MyRelationsBetweenFactions.Friends: multiplier = d.RepMult_Pos_Friend;  return true;
                default: return false;
            }
        }
    }
}
