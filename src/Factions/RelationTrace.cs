using System;
using System.Collections.Generic;
using System.Text;
using Factions.Core;
using Factions.Wire;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// The <c>factions debug relation</c> trace: what a viewing player is told about a faction's
    /// standing, and the SE table it is read from.
    ///
    /// <para><b>Why it exists.</b> On 2026-11-22 a Srr member's scanner turned every contact red
    /// after hours of white, and in every production save that day Srr had no relation row with any
    /// NPC faction, which <c>GetRelationBetweenFactions</c> reads as Enemies. That state predicts
    /// red all along, so the white hours, and the moment they ended, are what this has to catch
    /// (FAC-518). Three lines, each written on change only:</para>
    /// <list type="bullet">
    /// <item><c>RELATION_RESOLVE</c> — a (viewer, owner) answer from <see cref="ViewerRelations"/>,
    /// with the step that decided it and the table entry behind it.</item>
    /// <item><c>SCANNER_RELATION</c> — a scanner send that changed a contact's relation or reset
    /// the client (<see cref="PlayerScannerManager"/>).</item>
    /// <item><c>RELATION_ROWS</c> — every 60 s, the table rows between each faction with a real
    /// player member and every other faction. Runs on every shard, because Nexus replays faction
    /// relation changes between shards and logs nothing when its own debug mode is off.</item>
    /// </list>
    /// <para>Arming starts a new baseline: the first line per key after <c>factions debug relation</c>
    /// has <c>prev=-</c>. Game thread only.</para>
    /// </summary>
    internal static class RelationTrace
    {
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(60);
        private static readonly ChangeBook Resolves = new ChangeBook();
        private static readonly ChangeBook Rows = new ChangeBook();
        private static bool _armed;
        private static bool _tableUnreadableLogged;
        private static DateTime _nextProbeUtc;

        /// <summary>Bumped each time the trace is armed. A scanner state remembering what it sent
        /// under an older generation discards that memory, so a re-armed trace does not compare
        /// against sends from before it was off.</summary>
        internal static int Generation { get; private set; }

        /// <summary>Per frame. Cheap when the trace is off: one static read.</summary>
        internal static void Tick(DateTime nowUtc)
        {
            if (!DebugTraces.Relation)
            {
                if (_armed) { _armed = false; Resolves.Clear(); Rows.Clear(); }
                return;
            }
            EnsureArmed();
            if (nowUtc < _nextProbeUtc) return;
            _nextProbeUtc = nowUtc + ProbeInterval;
            try { ProbeRows(); }
            catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Relation row probe failed."); }
        }

        internal static void EnsureArmed()
        {
            if (_armed) return;
            _armed = true;
            Generation++;
            _nextProbeUtc = DateTime.MinValue;
        }

        /// <summary>One answer from <see cref="ViewerRelations"/>. Called only with the trace on.</summary>
        internal static void Resolved(string surface, long viewerIdentityId, MyFaction viewer, string ownerTag,
                                      MyFaction owner, string step, Tuple<MyRelationsBetweenFactions, int> table,
                                      WireRelation result)
        {
            try
            {
                EnsureArmed();
                string row = "-";
                if (viewer != null && owner != null && viewer.FactionId != owner.FactionId)
                {
                    bool? present = FactionDiplomacyPatch.HasRelationRow(MySession.Static.Factions, viewer.FactionId, owner.FactionId);
                    row = present == null ? "unreadable" : present.Value ? "present" : "missing";
                }
                string tableText = table == null ? "-" : table.Item1 + "/" + table.Item2;
                string value = step + "|" + row + "|" + tableText + "|" + result;
                string prev;
                if (!Resolves.Changed(viewerIdentityId + "|" + ownerTag, value, out prev)) return;
                FactionsPlugin.Log.Info("RELATION_RESOLVE surface=" + surface
                    + " viewer=" + viewerIdentityId
                    + " viewerFaction=" + (viewer != null ? viewer.Tag : "-")
                    + " owner=" + ownerTag
                    + " step=" + step + " row=" + row + " table=" + tableText + " result=" + result
                    + " prev=" + (prev ?? "-"));
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Relation resolve trace failed for '" + ownerTag + "'.");
            }
        }

        private static Dictionary<MyFactionCollection.MyRelatablePair, Tuple<MyRelationsBetweenFactions, int>> Table(
            MyFactionCollection factions)
        {
            if (factions == null || FactionDiplomacyPatch.RelationTable == null) return null;
            return FactionDiplomacyPatch.RelationTable(factions);
        }

        private static void ProbeRows()
        {
            MyFactionCollection factions = MySession.Static != null ? MySession.Static.Factions : null;
            if (factions == null) return;
            var table = Table(factions);
            if (table == null)
            {
                if (!_tableUnreadableLogged)
                    FactionsPlugin.Log.Warn("RELATION_ROWS table=unreadable");
                _tableUnreadableLogged = true;
                return;
            }

            MyFaction[] all = factions.GetAllFactions();
            var entries = new List<string>();
            foreach (MyFaction p in all)
            {
                if (p == null || !HasPlayerMember(p)) continue;
                entries.Clear();
                int others = 0;
                foreach (MyFaction o in all)
                {
                    if (o == null || o.FactionId == p.FactionId) continue;
                    others++;
                    Tuple<MyRelationsBetweenFactions, int> rel;
                    if (table.TryGetValue(new MyFactionCollection.MyRelatablePair(p.FactionId, o.FactionId), out rel)
                        && rel != null)
                        entries.Add(o.Tag + "=" + rel.Item1 + "/" + rel.Item2);
                }
                entries.Sort(StringComparer.Ordinal);
                string value = entries.Count == 0 ? "-" : string.Join(",", entries.ToArray());
                string prev;
                if (!Rows.Changed(p.Tag, value, out prev)) continue;
                FactionsPlugin.Log.Info("RELATION_ROWS faction=" + p.Tag + " others=" + others
                    + " rows=" + entries.Count + " entries=" + value + " prev=" + (prev ?? "-"));
            }
        }

        /// <summary>A faction with at least one member who is a real player (has a Steam id). SE crew
        /// bots are faction members never marked NPC, so <c>IsEveryoneNpc</c> is not this test.</summary>
        private static bool HasPlayerMember(MyFaction faction)
        {
            foreach (KeyValuePair<long, MyFactionMember> m in faction.Members)
                if (MySession.Static.Players.TryGetSteamId(m.Key) != 0) return true;
            return false;
        }
    }
}
