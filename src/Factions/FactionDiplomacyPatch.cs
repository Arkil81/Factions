using System;
using System.Collections.Generic;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Turns the vanilla Factions screen's diplomacy buttons into the Magnate's diplomacy surface,
    /// and stops them destroying the record the reputation model derives from (FAC-185).
    ///
    /// <para><b>What vanilla does.</b> <c>MyFactionCollection.ChangeFactionRelation</c> — the path
    /// behind Declare war, Accept peace and Accept friend request — writes the faction-relation table
    /// and then overwrites <b>every member's personal reputation</b> toward the other faction, on
    /// <b>both</b> sides:</para>
    /// <code>
    /// foreach (var member in TryGetFactionById(fromFactionId).Members)
    ///     SetReputationBetweenPlayerAndFaction(member.Key, toFactionId, num, ReputationChangeReason.None);
    /// foreach (var member2 in TryGetFactionById(toFactionId).Members)
    ///     SetReputationBetweenPlayerAndFaction(member2.Key, fromFactionId, num, ReputationChangeReason.None);
    /// </code>
    /// <para>Under FAC-184 those per-player numbers are the authoritative record of individual conduct.
    /// One click of a vanilla button flattens a whole roster's history, and this is the only route that
    /// can destroy it.</para>
    ///
    /// <para><b>The governing rule is war is unilateral, peace is bilateral.</b> A declaration lands
    /// at once. A peace is only ever a proposal, concluded by <see cref="Diplomacy.ConcludeTruces"/>
    /// once the other side is genuinely war-weary — so a player who raids a faction and immediately
    /// clicks peace gets nothing, which is a large improvement on vanilla's instant acceptance and
    /// comes free from machinery that already runs.</para>
    ///
    /// <para><b>Scope: one <c>PlayerMade</c> faction and one Factions-managed NPC.</b> The same
    /// method serves NPC-to-NPC and player-to-player changes we have no business intercepting, and
    /// both fall through to vanilla untouched — the first is the brain's, and suppressing the loop
    /// on the second would leave two players' members carrying records that disagree with their own
    /// declared war.</para>
    ///
    /// <para><b>The in-scope test reads the SE faction TYPE, not the Factions registry.</b> An
    /// earlier cut scoped on a <i>registered</i> player faction, which registration only makes true
    /// once a claim offer is accepted — while the wipe lands at faction <i>creation</i>, when the new
    /// faction establishes its initial relations and there is nothing to look up. So the patch fell
    /// through for precisely the call that does the damage, and the first Magnate on this world lost
    /// every reputation they had earned (FAC-200). An unregistered player faction gets the relation
    /// write and the suppression, but no <c>Diplomacy</c> routing: there is no state to route into,
    /// and creation is not a diplomatic act.</para>
    ///
    /// <para><b>Most peace no longer arrives here.</b> "Send peace" and a player's "Accept peace"
    /// are judged earlier, at the request gate (<see cref="FactionStateChangeCheckPatch"/>): the
    /// first never reaches this method, and the second is refused there so clients are not told
    /// Neutral while the war stands. The peace branch below stays for callers that bypass the
    /// request gate. A change raised by <see cref="VanillaDiplomacyBroadcast"/> is recognised and
    /// skipped (FAC-394 slice 5).</para>
    ///
    /// <para><b>On a Nexus agent</b> this method runs for every in-scope change: one pressed there,
    /// and every change Nexus replays from another server, the authority's own broadcasts included.
    /// Vanilla's member loop would overwrite the members' reputations here, and Nexus relays each of
    /// those writes back to the authority. So the agent skips vanilla for an in-scope pair and writes
    /// the one relation entry itself, straight into the table, which Nexus does not relay. An entry
    /// already in the requested band is kept: the authority's projected value reaches the agent
    /// through Nexus's own faction-reputation sync. Routing into <c>Diplomacy</c> is the authority's,
    /// which runs this method again for the replayed change.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyFactionCollection), "ChangeFactionRelation")]  // private; string required
    internal static class FactionDiplomacyPatch
    {
        private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

        private static bool _outcomeLogged;

        /// <summary>Resolved before Harmony binds anything. A miss disables this class only and must
        /// never throw: a throw fails its whole patch group (<c>HarmonyPatcher</c>).
        /// <para>Provenance: this routing is FAC-185, and the conduct record its failure would
        /// destroy is the one earned standing derives from (FAC-184).</para></summary>
        [HarmonyPrepare]
        private static bool Prepare()
        {
            MethodInfo target = AccessTools.Method(
                typeof(MyFactionCollection), "ChangeFactionRelation",
                new[] { typeof(long), typeof(long), typeof(MyRelationsBetweenFactions) });
            bool resolved = target != null;
            if (!_outcomeLogged)
            {
                _outcomeLogged = true;
                if (resolved)
                    Log.Info("Vanilla diplomacy buttons: Factions routing bound.");
                else
                    Log.Error("Vanilla diplomacy buttons: MyFactionCollection.ChangeFactionRelation"
                            + "(long, long, MyRelationsBetweenFactions) did not resolve. The vanilla "
                            + "buttons still work and still OVERWRITE every member's personal "
                            + "reputation on both sides, destroying the per-member conduct record "
                            + "earned standing is derived from. Nothing else is affected.");
            }
            return resolved;
        }

        /// <summary><c>MyFactionTypes.PlayerMade</c> — the population whose members carry a conduct
        /// record worth protecting. Read from the SE faction rather than from
        /// <see cref="FactionState.IsPlayerFaction"/>, which only exists after registration.</summary>
        private static bool IsPlayerMade(VRage.Game.ModAPI.IMyFaction faction)
        {
            MyFaction se = faction as MyFaction;
            return se != null && se.FactionType == MyFactionTypes.PlayerMade;
        }

        /// <summary>Writes the faction-relation entry both ways, and nothing else.
        /// <c>SetReputationBetweenFactions</c> is the safe door — verified from the decompiled source
        /// to write one entry in <c>m_relationsBetweenFactions</c>, touch no member reputations, and
        /// not re-enter the method this class patches.</summary>
        private static void WriteFactionRelation(long fromFactionId, long toFactionId, MyRelationsBetweenFactions relation)
        {
            int rep = MySession.Static.Factions.TranslateRelationToReputation(relation);
            MySession.Static.Factions.SetReputationBetweenFactions(fromFactionId, toFactionId, rep);
            MySession.Static.Factions.SetReputationBetweenFactions(toFactionId, fromFactionId, rep);
        }

        private static readonly AccessTools.FieldRef<MyFactionCollection,
            Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>>> RelationTable =
            TryRelationTable();

        private static AccessTools.FieldRef<MyFactionCollection,
            Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>>> TryRelationTable()
        {
            try
            {
                return AccessTools.FieldRefAccess<MyFactionCollection,
                    Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>>>("m_relationsBetweenFactions");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MyFactionCollection.m_relationsBetweenFactions did not resolve. On a Nexus agent the "
                            + "vanilla diplomacy buttons overwrite every member's reputation toward the other faction.");
                return null;
            }
        }

        /// <summary>Agent: an in-scope change applied without the member loop. False when out of scope,
        /// or when the snapshot has not arrived, so vanilla runs.</summary>
        private static bool ApplyOnAgent(MyFactionCollection factions, long fromFactionId, long toFactionId,
                                         MyRelationsBetweenFactions relation)
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            if (plugin == null || factions == null || RelationTable == null) return false;
            VRage.Game.ModAPI.IMyFaction seFrom = factions.TryGetFactionById(fromFactionId);
            VRage.Game.ModAPI.IMyFaction seTo = factions.TryGetFactionById(toFactionId);
            if (seFrom == null || seTo == null) return false;

            NexusSnapshotFaction a = plugin.AgentFactionRow(seFrom.Tag);
            NexusSnapshotFaction b = plugin.AgentFactionRow(seTo.Tag);
            bool fromNpc = a != null && !a.Player, toNpc = b != null && !b.Player;
            if (!((IsPlayerMade(seFrom) && toNpc) || (IsPlayerMade(seTo) && fromNpc))) return false;

            var key = new MyFactionCollection.MyRelatablePair(fromFactionId, toFactionId);
            Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>> table = RelationTable(factions);
            Tuple<MyRelationsBetweenFactions, int> current;
            bool kept = table.TryGetValue(key, out current) && current != null && current.Item1 == relation;
            if (!kept) table[key] = new Tuple<MyRelationsBetweenFactions, int>(relation, factions.TranslateRelationToReputation(relation));
            if (DebugTraces.Diplomacy)
                Log.Info("DIPLOMACY_RELATION_SET from=" + seFrom.Tag + " to=" + seTo.Tag + " relation=" + relation
                         + " entry=" + (kept ? "kept" : "written") + " agent=yes");
            return true;
        }

        private static string TagOf(long factionId)
        {
            VRage.Game.ModAPI.IMyFaction f = MySession.Static?.Factions?.TryGetFactionById(factionId);
            return f != null ? f.Tag : factionId.ToString();
        }

        /// <summary>The one DIPLOMACY_PEACE_OFFERED line, shared by this prefix and the request
        /// check (<see cref="FactionStateChangeCheckPatch"/>). <paramref name="requested"/> is the
        /// relation or vanilla action that posted the offer.</summary>
        internal static void LogPeaceOffered(FactionState player, FactionState npc, string requested, FactionTuning tuning)
        {
            Log.Info("DIPLOMACY_PEACE_OFFERED player=" + player.Tag + " target=" + npc.Tag
                   + " requested=" + requested
                   + " fatigue=" + npc.FatigueTo(player.Tag).ToString("0.000")
                   + "/" + tuning.AcceptPeaceThreshold.ToString("0.000")
                   + " (offer posted; it concludes only when they are war-weary)");
        }

        /// <summary>Returns false to skip the vanilla body — including its member-reputation loop —
        /// for an in-scope pair only.</summary>
        [HarmonyPrefix]
        private static bool Prefix(MyFactionCollection __instance, long fromFactionId, long toFactionId,
                                   MyRelationsBetweenFactions relation)
        {
            try
            {
                // Our own broadcast, applied on the server (VanillaDiplomacyBroadcast). The server's
                // table already holds the projected value: skip vanilla's band midpoint and member
                // loop, and route nothing -- it is not a player act.
                if (VanillaDiplomacyBroadcast.Raising)
                {
                    if (DebugTraces.Diplomacy)
                        FactionsPlugin.Instance?.Runtime?.WriteDecisionLine(
                            "DIPLOMACY_VANILLA_APPLY_SKIPPED from=" + TagOf(fromFactionId) + " to=" + TagOf(toFactionId)
                            + " relation=" + relation);
                    return false;
                }
                if (SimulationGate.Deferred) return !ApplyOnAgent(__instance, fromFactionId, toFactionId, relation);

                FactionRuntime runtime = FactionsPlugin.Instance?.Runtime;
                FactionTuning tuning = runtime?.World?.Tuning;
                if (runtime == null || tuning == null) return true;
                if (MySession.Static == null || MySession.Static.Factions == null) return true;

                // IMyFaction, not MyFaction: only the tag is needed, and the interface is what the
                // lookup returns.
                VRage.Game.ModAPI.IMyFaction seFrom = MySession.Static.Factions.TryGetFactionById(fromFactionId);
                VRage.Game.ModAPI.IMyFaction seTo = MySession.Static.Factions.TryGetFactionById(toFactionId);
                if (seFrom == null || seTo == null) return true;

                // Scope is decided on the SE faction TYPE, not on the Factions registry. Registration
                // happens when a claim offer is accepted; the member-reputation wipe lands at faction
                // CREATION, before there is anything to look up. Scoping on the registry meant this
                // patch fell through to vanilla for exactly the call that does the damage, and a
                // founder lost every reputation they had earned (FAC-200).
                bool fromPlayerMade = IsPlayerMade(seFrom);
                bool toPlayerMade = IsPlayerMade(seTo);

                FactionState a = runtime.FindFaction(seFrom.Tag);
                FactionState b = runtime.FindFaction(seTo.Tag);
                bool fromNpc = a != null && !a.IsPlayerFaction;
                bool toNpc = b != null && !b.IsPlayerFaction;

                // Exactly one PlayerMade side and one NPC we manage. Player-to-player stays vanilla's
                // business — we have no standing to arbitrate it and suppressing the loop there would
                // leave two players' members carrying records that disagree with their own war. And
                // NPC-to-NPC is the brain's.
                if (!((fromPlayerMade && toNpc) || (toPlayerMade && fromNpc))) return true;

                FactionState npc = fromPlayerMade ? b : a;
                FactionState player = fromPlayerMade ? a : b;   // null until the faction registers
                string playerTag = fromPlayerMade ? seFrom.Tag : seTo.Tag;

                if (player == null)
                {
                    // Not a Magnate yet, so there is no FactionState to route a verb into — this is
                    // almost always creation establishing the new faction's initial relations. Do
                    // exactly what vanilla does MINUS the member loop: the faction relation is
                    // written, every personal conduct record is left alone.
                    WriteFactionRelation(fromFactionId, toFactionId, relation);
                    Log.Info("DIPLOMACY_RELATION_SET player=" + playerTag + " target=" + npc.Tag
                           + " relation=" + relation
                           + " (unregistered player faction; member reputations left intact)");
                    return false;
                }

                if (relation == MyRelationsBetweenFactions.Enemies)
                {
                    // Both sides: the player's own stance goes to war too (docs/diplomacy-plan.md, rule 3).
                    PlayerDiplomacy.DeclareWar(player, npc, tuning, DateTime.UtcNow);

                    // The relation table IS written, and must be: SE's turret targeting and HUD read
                    // it, and a declared war the game does not know about would make the world
                    // disagree with the sim about who is shooting whom.
                    WriteFactionRelation(fromFactionId, toFactionId, relation);

                    Log.Info("DIPLOMACY_WAR player=" + player.Tag + " target=" + npc.Tag
                           + " standing=" + npc.RelationTo(player.Tag).ToString("0.000")
                           + " (declared; member reputations left intact)");
                    return false;
                }

                // Peace and alliance are both requests. Nothing is written to the relation table:
                // the war stands until the NPC is actually war-weary, and ConcludeTruces is what
                // settles it. Alliance is not modelled yet and is deliberately treated as a peace
                // overture rather than granted -- refusing to invent agreement is the safe default.
                Diplomacy.OfferPeace(player, npc.Tag, runtime.World.UtcNow);
                LogPeaceOffered(player, npc, relation.ToString(), tuning);
                return false;
            }
            catch (Exception ex)
            {
                // Falling through to vanilla costs the member-reputation record for this one click.
                // Throwing out of a prefix would break the Factions screen for everyone, which is
                // worse -- but this is a real loss, so it is an error and not a warning.
                Log.Error(ex, "Diplomacy routing failed; vanilla ran and member reputations toward "
                            + "the other faction were overwritten on both sides.");
                return true;
            }
        }
    }
}
