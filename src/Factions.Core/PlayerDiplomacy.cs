using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Factions.Core
{
    /// <summary>What a player faction's leader can do toward one NPC faction.</summary>
    public enum LeaderAct
    {
        Status = 0,
        War = 1,
        Embargo = 2,
        Lift = 3,
        Gift = 4,
        Peace = 5,
        NonAggression = 6,
        Alliance = 7,
        Withdraw = 8,
    }

    /// <summary>Which side of the two relation thresholds a stance sits on: below
    /// <see cref="FactionTuning.HostileRelationThreshold"/>, at or above
    /// <see cref="FactionTuning.AllyRelationThreshold"/>, or between them.</summary>
    public enum StanceBand
    {
        Hostile = 0,
        Neutral = 1,
        Friendly = 2,
    }

    /// <summary>One NPC faction as a player faction's diplomacy readout sees it. Built by
    /// <see cref="PlayerDiplomacy.Readout"/>; the chat <c>status</c> line and the FactionsUI
    /// Diplomacy window are both drawn from it, so the two cannot disagree.</summary>
    public sealed class DiplomacyReadoutRow
    {
        public string NpcTag;
        /// <summary>The NPC's view of the player faction.</summary>
        public double TheirView;
        public StanceBand TheirBand;
        /// <summary>The player faction's own stance, set by its leader.</summary>
        public double YourStance;
        public StanceBand YourBand;
        public bool EmbargoByYou;
        public bool EmbargoByThem;
        /// <summary><see cref="Diplomacy.TradeBarred"/> between the two, either way.</summary>
        public bool TradeBarred;
        public bool Allied;
        /// <summary>A gift or a proposal to this NPC is refused: it makes no agreements.</summary>
        public bool Implacable;
        /// <summary>End of a truce holding on either side, or null.</summary>
        public DateTime? TruceUntilUtc;
        /// <summary>The player faction's outstanding offer to the NPC, as its terms joined by '+'
        /// (<c>Truce</c>, <c>NonAggression</c>, <c>Alliance</c>), or null.</summary>
        public string YouOffered;
        public DateTime? YouOfferedExpiresUtc;
        /// <summary>The NPC's outstanding offer to the player faction, or null.</summary>
        public string TheyOffered;
        public DateTime? TheyOfferedExpiresUtc;
        /// <summary>When the next gift to this NPC may go, or null when one may go now.</summary>
        public DateTime? GiftReadyUtc;
    }

    /// <summary>The outcome of one leader act. <see cref="LogLine"/> is null when nothing changed
    /// (a refusal, or a no-op such as embargoing a faction already embargoed), so the caller logs
    /// exactly one line per act that happened.</summary>
    public sealed class LeaderActResult
    {
        public bool Done;
        public string Response;
        public string LogLine;

        internal static LeaderActResult Refused(string why)
        {
            return new LeaderActResult { Done = false, Response = why };
        }
    }

    /// <summary>
    /// A player faction's diplomacy toward the NPC factions: the leader's acts, and the readout.
    /// SE-free, so the chat command, the bridge verb and the vanilla war button all run the same
    /// rule. See docs/diplomacy-plan.md, rule 3.
    ///
    /// <para><b>The stance is the leader's.</b> A player faction's own
    /// <see cref="FactionState.Relations"/> toward an NPC does not drift and is not moved by what the
    /// NPC does (<see cref="Diplomacy.HoldsLeaderStance"/>). It moves by <see cref="DeclareWar"/> and
    /// by a concluded truce, and by nothing else. A player faction that never acted reads 0.</para>
    ///
    /// <para><b>War is unilateral, peace is bilateral.</b> War lands at once on both sides. Peace,
    /// non-aggression and alliance are proposals, concluded by <see cref="Diplomacy.ProcessProposals"/>
    /// on the NPC's own terms.</para>
    ///
    /// <para>Who may act is the host's question (the SE faction's leader list). This class only says
    /// which acts need a leader: <see cref="LeaderOnly"/>.</para>
    /// </summary>
    public static class PlayerDiplomacy
    {
        /// <summary>The stance a declaration of war sets on the player's side: the floor of the scale.</summary>
        public const double WarStance = -1.0;

        /// <summary>The verbs, as typed. Kept beside <see cref="TryParse"/> so the usage line and the
        /// parser cannot disagree.</summary>
        public const string Usage =
            "status [npc] | war <npc> | embargo <npc> | lift <npc> | gift <npc> | peace <npc> | "
          + "propose <npc> nonaggression|alliance | withdraw <npc>";

        /// <summary>Parses a verb (and, for <c>propose</c>, its kind) into an act. Case-insensitive.</summary>
        public static bool TryParse(string verb, string proposeKind, out LeaderAct act, out string error)
        {
            act = LeaderAct.Status;
            error = null;
            string v = (verb ?? "").Trim().ToLowerInvariant();
            switch (v)
            {
                case "":
                case "status": act = LeaderAct.Status; return true;
                case "war":
                case "declare": act = LeaderAct.War; return true;
                case "embargo": act = LeaderAct.Embargo; return true;
                case "lift":
                case "unembargo": act = LeaderAct.Lift; return true;
                case "gift": act = LeaderAct.Gift; return true;
                case "peace":
                case "truce": act = LeaderAct.Peace; return true;
                case "withdraw": act = LeaderAct.Withdraw; return true;
                case "propose":
                {
                    string k = (proposeKind ?? "").Trim().ToLowerInvariant();
                    if (k == "nonaggression" || k == "non-aggression" || k == "na") { act = LeaderAct.NonAggression; return true; }
                    if (k == "alliance" || k == "ally" || k == "pact") { act = LeaderAct.Alliance; return true; }
                    error = "Propose what? propose <npc> nonaggression|alliance";
                    return false;
                }
                default:
                    error = "Unknown diplomacy verb '" + verb + "'. One of: " + Usage;
                    return false;
            }
        }

        /// <summary>True for every act but <see cref="LeaderAct.Status"/>: only the faction's leader
        /// changes its diplomacy; any member may read it. Vanilla has no such check on its own
        /// buttons, so this one is ours, on the server.</summary>
        public static bool LeaderOnly(LeaderAct act)
        {
            return act != LeaderAct.Status;
        }

        /// <summary>Null when the caller may perform <paramref name="act"/>; otherwise the refusal.</summary>
        public static string Authorise(LeaderAct act, bool callerIsLeader, string factionTag)
        {
            if (!LeaderOnly(act) || callerIsLeader) return null;
            return "Only a leader of [" + factionTag + "] can change its diplomacy. "
                 + "Any member can read it: !factions diplomacy status";
        }

        /// <summary>Why <paramref name="npc"/> cannot be the target of a leader act, or null. The
        /// target must be an NPC faction Factions manages, and not the neutral bucket.</summary>
        public static string TargetRefusal(FactionState player, FactionState npc, string typedTag)
        {
            if (player == null) return "Your faction is not registered with Factions yet.";
            if (string.IsNullOrEmpty(typedTag)) return "Which faction? Give its tag, e.g. IMPR.";
            if (npc == null) return "No faction '" + typedTag + "' is known to Factions.";
            if (string.Equals(npc.Tag, player.Tag, StringComparison.OrdinalIgnoreCase))
                return "That is your own faction.";
            if (string.Equals(npc.Tag, NeutralFaction.Tag, StringComparison.OrdinalIgnoreCase))
                return npc.Tag + " is the unclaimed-territory placeholder, not a faction you can deal with.";
            if (npc.IsPlayerFaction)
                return "[" + npc.Tag + "] is a player faction. Diplomacy between player factions is SE's own.";
            return null;
        }

        /// <summary>Runs one act. <paramref name="world"/> is needed only for a gift (the treasury).</summary>
        public static LeaderActResult Act(LeaderAct act, FactionState player, FactionState npc,
                                          WorldModel world, FactionTuning t, DateTime nowUtc)
        {
            switch (act)
            {
                case LeaderAct.War:           return DeclareWar(player, npc, t, nowUtc);
                case LeaderAct.Embargo:       return SetEmbargo(player, npc, true);
                case LeaderAct.Lift:          return SetEmbargo(player, npc, false);
                case LeaderAct.Gift:          return Gift(player, npc, world, t, nowUtc);
                case LeaderAct.Peace:         return OfferPeace(player, npc, t, nowUtc);
                case LeaderAct.NonAggression: return Propose(player, npc, DiplomacyVerb.NonAggression, t, nowUtc);
                case LeaderAct.Alliance:      return Propose(player, npc, DiplomacyVerb.Alliance, t, nowUtc);
                case LeaderAct.Withdraw:      return Withdraw(player, npc);
                default:
                    return new LeaderActResult { Done = false, Response = Describe(player, new[] { npc }, npc != null ? npc.Tag : null, t, nowUtc) };
            }
        }

        /// <summary>Declares war on <paramref name="npc"/>: the player's own stance goes to
        /// <see cref="WarStance"/>, and the NPC's view of the player takes
        /// <see cref="Diplomacy.DeclareWar"/> as before. A truce between them ends on both sides,
        /// and the player's own outstanding offer to them is withdrawn.
        ///
        /// <para>Shared by the chat command, the bridge verb and the vanilla Declare-war button.</para></summary>
        public static LeaderActResult DeclareWar(FactionState player, FactionState npc, FactionTuning t, DateTime nowUtc)
        {
            if (player == null || npc == null || t == null) return LeaderActResult.Refused("Nothing to declare war on.");
            double before = player.RelationTo(npc.Tag);
            player.Relations[npc.Tag] = WarStance;
            player.TruceUntil.Remove(npc.Tag);
            player.Proposals.Remove(npc.Tag);
            Diplomacy.DeclareWar(npc, player.Tag, t, nowUtc);

            return new LeaderActResult
            {
                Done = true,
                Response = "[" + player.Tag + "] is at war with " + npc.Tag + ". Your stance "
                         + F2(before) + " -> " + F2(WarStance) + "; their view of you "
                         + F2(npc.RelationTo(player.Tag)) + ". Trade between you stops both ways. "
                         + "Peace needs their agreement: !factions diplomacy peace " + npc.Tag,
                LogLine = "DIPLOMACY_WAR player=" + player.Tag + " target=" + npc.Tag
                        + " standing=" + F3(npc.RelationTo(player.Tag))
                        + " stance=" + F3(before) + "->" + F3(WarStance),
            };
        }

        /// <summary>Adds or removes <paramref name="npc"/> in the player's
        /// <see cref="FactionState.Embargoes"/>. The stance is unchanged and the NPC's view is
        /// untouched: an embargo is not an attack. <see cref="Diplomacy.TradeBarred"/> reads the set
        /// from both sides, so it stops trade in both directions.</summary>
        public static LeaderActResult SetEmbargo(FactionState player, FactionState npc, bool on)
        {
            if (player == null || npc == null) return LeaderActResult.Refused("Nothing to embargo.");
            bool changed = on ? player.Embargoes.Add(npc.Tag) : player.Embargoes.Remove(npc.Tag);
            if (!changed)
                return new LeaderActResult
                {
                    Done = false,
                    Response = on ? "[" + player.Tag + "] already embargoes " + npc.Tag + "."
                                  : "[" + player.Tag + "] has no embargo on " + npc.Tag + " to lift.",
                };
            return new LeaderActResult
            {
                Done = true,
                Response = on
                    ? "[" + player.Tag + "] embargoes " + npc.Tag + ": no convoy trade either way, and "
                      + "a convoy already flying settles nothing. Your stance is unchanged."
                    : "[" + player.Tag + "] lifted its embargo on " + npc.Tag + "."
                      + (npc.Embargoes.Contains(player.Tag) ? " " + npc.Tag + " still embargoes you, so trade stays barred." : ""),
                LogLine = "DIPLOMACY_EMBARGO player=" + player.Tag + " target=" + npc.Tag + " " + (on ? "on" : "off"),
            };
        }

        /// <summary>Pays <see cref="FactionTuning.GiftCost"/> from the player faction's treasury and
        /// warms the NPC's view of the player by <see cref="Diplomacy.GiftRelation"/>. The player's
        /// own stance is unchanged.
        ///
        /// <para>Refused, before any money moves, when the treasury cannot pay, when the NPC is
        /// implacable (a gift buys nothing there), or while the same NPC was gifted within
        /// <see cref="FactionTuning.GiftDestCooldown"/>. The cooldown is the NPC gift path's own rule
        /// and lives in the same place, <see cref="FactionState.TradeDestCooldowns"/>, so an NPC's
        /// standing cannot be bought outright in one sitting.</para></summary>
        public static LeaderActResult Gift(FactionState player, FactionState npc, WorldModel world,
                                           FactionTuning t, DateTime nowUtc)
        {
            if (player == null || npc == null || world == null || t == null)
                return LeaderActResult.Refused("Nothing to send a gift to.");

            if (Diplomacy.IsImplacable(npc))
                return LeaderActResult.Refused(npc.Tag + " cannot be swayed by gifts. Nothing was paid.");

            DateTime next;
            if (player.TradeDestCooldowns.TryGetValue(npc.Tag, out next) && nowUtc < next)
                return LeaderActResult.Refused("A gift to " + npc.Tag + " was sent recently. The next one can go in "
                    + Math.Ceiling((next - nowUtc).TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min. Nothing was paid.");

            long cost = Math.Max(0L, t.GiftCost);
            long treasury = world.GetTreasury(player.Tag);
            if (treasury < cost)
                return LeaderActResult.Refused("A gift costs " + cost + " and [" + player.Tag + "]'s treasury holds "
                    + treasury + ". Nothing was paid.");

            double before = npc.RelationTo(player.Tag);
            if (cost > 0) world.Spend(player.Tag, cost);
            Diplomacy.GiftRelation(npc, player.Tag, t);
            double after = npc.RelationTo(player.Tag);
            if (t.GiftDestCooldown > TimeSpan.Zero)
                player.TradeDestCooldowns[npc.Tag] = nowUtc + t.GiftDestCooldown;
            world.RecordEvent(FactionEventKind.GiftResolved, player.Tag, npc.Tag, null, cost);

            return new LeaderActResult
            {
                Done = true,
                Response = "[" + player.Tag + "] sent " + npc.Tag + " a gift of " + cost + ". Their view of you "
                         + F2(before) + " -> " + F2(after) + ". Treasury now " + world.GetTreasury(player.Tag) + ".",
                LogLine = "DIPLOMACY_GIFT player=" + player.Tag + " target=" + npc.Tag + " cost=" + cost
                        + " standing=" + F3(before) + "->" + F3(after)
                        + " treasury=" + world.GetTreasury(player.Tag),
            };
        }

        /// <summary>Posts a truce offer through <see cref="Diplomacy.OfferPeace"/>. It concludes on a
        /// later pass when the NPC has offered peace too or is war-weary, and the truce then sets
        /// both standings to <see cref="FactionTuning.TruceRelation"/>. Refused when neither side
        /// holds the other hostile, or a truce already holds: there is no war to end.</summary>
        public static LeaderActResult OfferPeace(FactionState player, FactionState npc, FactionTuning t, DateTime nowUtc)
        {
            if (player == null || npc == null || t == null) return LeaderActResult.Refused("Nothing to make peace with.");
            if (Diplomacy.UnderTruce(player, npc.Tag, nowUtc) || Diplomacy.UnderTruce(npc, player.Tag, nowUtc))
                return LeaderActResult.Refused("A truce with " + npc.Tag + " already holds.");
            bool atWar = player.RelationTo(npc.Tag) < t.HostileRelationThreshold
                      || npc.RelationTo(player.Tag) < t.HostileRelationThreshold;
            if (!atWar)
                return LeaderActResult.Refused("[" + player.Tag + "] is not at war with " + npc.Tag
                    + ", so there is no peace to make. For a standing agreement: !factions diplomacy propose "
                    + npc.Tag + " nonaggression");

            Diplomacy.OfferPeace(player, npc.Tag, nowUtc);
            double fatigue = npc.FatigueTo(player.Tag);
            bool theyOffered = HasFreshOffer(npc, player, t, nowUtc);
            return new LeaderActResult
            {
                Done = true,
                Response = "[" + player.Tag + "] offered " + npc.Tag + " peace. "
                         + (theyOffered ? "They have offered peace too, so it concludes on the next pass."
                            : "They accept when war-weary (" + F2(fatigue) + " of " + F2(t.AcceptPeaceThreshold)
                              + ") or when they offer peace themselves. The offer stands for "
                              + Span(Diplomacy.OfferTtl(player, npc, false, t)) + "."),
                LogLine = "DIPLOMACY_PEACE_OFFERED player=" + player.Tag + " target=" + npc.Tag
                        + " requested=Truce fatigue=" + F3(fatigue) + "/" + F3(t.AcceptPeaceThreshold),
            };
        }

        /// <summary>Posts a non-aggression or alliance proposal through <see cref="Diplomacy.Propose"/>.
        /// <see cref="Diplomacy.ProcessProposals"/> concludes it on the NPC's own terms: non-aggression
        /// when the NPC offers peace too or is war-weary, alliance only when the NPC proposes one back.</summary>
        public static LeaderActResult Propose(FactionState player, FactionState npc, DiplomacyVerb verb,
                                              FactionTuning t, DateTime nowUtc)
        {
            if (player == null || npc == null || t == null) return LeaderActResult.Refused("Nothing to propose to.");
            if (verb != DiplomacyVerb.NonAggression && verb != DiplomacyVerb.Alliance)
                return LeaderActResult.Refused("Only non-aggression and alliance can be proposed.");
            if (Diplomacy.IsImplacable(npc))
                return LeaderActResult.Refused(npc.Tag + " makes no agreements with anyone.");
            if (verb == DiplomacyVerb.Alliance && Diplomacy.InPactWith(player, npc.Tag))
                return LeaderActResult.Refused("[" + player.Tag + "] is already allied with " + npc.Tag + ".");
            if (verb == DiplomacyVerb.NonAggression
                && (Diplomacy.UnderTruce(player, npc.Tag, nowUtc) || Diplomacy.UnderTruce(npc, player.Tag, nowUtc)))
                return LeaderActResult.Refused("A non-aggression window with " + npc.Tag + " already holds.");

            Diplomacy.Propose(player, npc.Tag, new[] { new DiplomacyTerm { Verb = verb } }, nowUtc);
            TimeSpan ttl = Diplomacy.OfferTtl(player, npc, verb == DiplomacyVerb.Alliance, t);
            return new LeaderActResult
            {
                Done = true,
                Response = "[" + player.Tag + "] proposed " + Describe(verb) + " to " + npc.Tag + ". "
                         + (verb == DiplomacyVerb.Alliance
                            ? "It concludes only if " + npc.Tag + " proposes an alliance to you too."
                            : "It concludes if " + npc.Tag + " offers peace too or is war-weary.")
                         + " The proposal stands for " + Span(ttl) + ".",
                LogLine = "DIPLOMACY_PROPOSED player=" + player.Tag + " target=" + npc.Tag + " terms=" + verb,
            };
        }

        /// <summary>Withdraws the player's outstanding proposal to <paramref name="npc"/>, whatever it was.</summary>
        public static LeaderActResult Withdraw(FactionState player, FactionState npc)
        {
            if (player == null || npc == null) return LeaderActResult.Refused("Nothing to withdraw.");
            DiplomacyMessage msg;
            if (!player.Proposals.TryGetValue(npc.Tag, out msg))
                return new LeaderActResult { Done = false, Response = "[" + player.Tag + "] has no proposal outstanding to " + npc.Tag + "." };
            player.Proposals.Remove(npc.Tag);
            string terms = TermsOf(msg);
            return new LeaderActResult
            {
                Done = true,
                Response = "[" + player.Tag + "] withdrew its " + terms + " proposal to " + npc.Tag + ".",
                LogLine = "DIPLOMACY_WITHDRAWN player=" + player.Tag + " target=" + npc.Tag + " terms=" + terms,
            };
        }

        /// <summary>The readout: per NPC, the player's stance, the NPC's view, embargoes either way,
        /// whether trade is barred, the truce window and pending proposals both ways. With
        /// <paramref name="onlyTag"/> set, just that NPC.</summary>
        public static string Describe(FactionState player, IEnumerable<FactionState> roster, string onlyTag,
                                      FactionTuning t, DateTime nowUtc)
        {
            if (player == null) return "Your faction is not registered with Factions yet.";
            var sb = new StringBuilder();
            sb.AppendLine("[" + player.Tag + "] diplomacy (stance = yours, set by your leader; view = theirs of you):");
            int rows = 0;
            if (roster != null)
            {
                foreach (FactionState npc in roster)
                {
                    if (npc == null || TargetRefusal(player, npc, npc.Tag) != null) continue;
                    if (onlyTag != null && !string.Equals(npc.Tag, onlyTag, StringComparison.OrdinalIgnoreCase)) continue;
                    rows++;
                    sb.AppendLine(DescribeRow(player, npc, t, nowUtc));
                }
            }
            if (rows == 0)
                sb.AppendLine(onlyTag != null ? "  no NPC faction '" + onlyTag + "' known to Factions." : "  no NPC factions known.");
            return sb.ToString().TrimEnd();
        }

        private static string DescribeRow(FactionState player, FactionState npc, FactionTuning t, DateTime nowUtc)
        {
            DiplomacyReadoutRow row = ReadoutRow(player, npc, t, nowUtc);
            var sb = new StringBuilder();
            sb.Append("  ").Append(npc.Tag)
              .Append("  stance ").Append(F2(row.YourStance)).Append(" (").Append(Band(row.YourStance, t)).Append(")")
              .Append("  view ").Append(F2(row.TheirView)).Append(" (").Append(Band(row.TheirView, t)).Append(")");

            if (row.EmbargoByYou || row.EmbargoByThem)
                sb.Append("  embargo ").Append(row.EmbargoByYou && row.EmbargoByThem ? "both ways" : row.EmbargoByYou ? "by you" : "by them");
            if (row.TradeBarred) sb.Append("  TRADE BARRED");
            if (row.Allied) sb.Append("  allied");
            if (row.TruceUntilUtc.HasValue)
                sb.Append("  truce until ").Append(row.TruceUntilUtc.Value.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("Z");
            if (row.YouOffered != null) sb.Append("  you offered ").Append(row.YouOffered).Append(OpenUntil(row.YouOfferedExpiresUtc));
            if (row.TheyOffered != null) sb.Append("  they offered ").Append(row.TheyOffered).Append(OpenUntil(row.TheyOfferedExpiresUtc));
            return sb.ToString();
        }

        /// <summary>The readout as data: one row per NPC faction the player faction can deal with
        /// (<see cref="TargetRefusal"/> is null), ordered by tag. Empty for a null player.
        ///
        /// <para>Read-only. The FactionsUI Diplomacy snapshot is this list mapped onto the wire, and
        /// <see cref="Describe"/> formats the same rows, so the window and the chat verb show one
        /// answer.</para></summary>
        public static List<DiplomacyReadoutRow> Readout(FactionState player, IEnumerable<FactionState> roster,
                                                        FactionTuning t, DateTime nowUtc)
        {
            var rows = new List<DiplomacyReadoutRow>();
            if (player == null || roster == null) return rows;
            foreach (FactionState npc in roster)
            {
                if (npc == null || TargetRefusal(player, npc, npc.Tag) != null) continue;
                rows.Add(ReadoutRow(player, npc, t, nowUtc));
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.NpcTag, b.NpcTag));
            return rows;
        }

        /// <summary>One row of <see cref="Readout"/>. An offer is reported only while
        /// <see cref="Diplomacy.OfferOpen"/> holds, with the moment it stops; one past its TTL but not
        /// yet pruned by <see cref="Diplomacy.ProcessProposals"/> is already gone here.</summary>
        public static DiplomacyReadoutRow ReadoutRow(FactionState player, FactionState npc, FactionTuning t, DateTime nowUtc)
        {
            var row = new DiplomacyReadoutRow
            {
                NpcTag = npc.Tag,
                TheirView = npc.RelationTo(player.Tag),
                YourStance = player.RelationTo(npc.Tag),
                EmbargoByYou = player.Embargoes.Contains(npc.Tag),
                EmbargoByThem = npc.Embargoes.Contains(player.Tag),
                TradeBarred = Diplomacy.TradeBarred(player, npc, t),
                Allied = Diplomacy.InPactWith(player, npc.Tag) || Diplomacy.InPactWith(npc, player.Tag),
                Implacable = Diplomacy.IsImplacable(npc),
            };
            row.TheirBand = BandOf(row.TheirView, t);
            row.YourBand = BandOf(row.YourStance, t);

            DateTime until;
            if ((player.TruceUntil.TryGetValue(npc.Tag, out until) && nowUtc < until)
                || (npc.TruceUntil.TryGetValue(player.Tag, out until) && nowUtc < until))
                row.TruceUntilUtc = until;

            DiplomacyMessage msg;
            if (player.Proposals.TryGetValue(npc.Tag, out msg) && Diplomacy.OfferOpen(player, npc, msg, nowUtc, t))
            {
                row.YouOffered = TermsOf(msg);
                row.YouOfferedExpiresUtc = OfferExpiry(player, npc, msg, t);
            }
            if (npc.Proposals.TryGetValue(player.Tag, out msg) && Diplomacy.OfferOpen(npc, player, msg, nowUtc, t))
            {
                row.TheyOffered = TermsOf(msg);
                row.TheyOfferedExpiresUtc = OfferExpiry(npc, player, msg, t);
            }

            DateTime next;
            if (player.TradeDestCooldowns.TryGetValue(npc.Tag, out next) && nowUtc < next)
                row.GiftReadyUtc = next;
            return row;
        }

        /// <summary>The band a relation value falls in. Friendly is checked last, so a tuning where
        /// the two thresholds cross still reads a value below the hostile line as hostile.</summary>
        public static StanceBand BandOf(double v, FactionTuning t)
        {
            if (t == null) return StanceBand.Neutral;
            if (v < t.HostileRelationThreshold) return StanceBand.Hostile;
            if (v >= t.AllyRelationThreshold) return StanceBand.Friendly;
            return StanceBand.Neutral;
        }

        /// <summary>When the offer stops being open, on <see cref="Diplomacy.OfferTtl"/>: the moment
        /// <see cref="Diplomacy.OfferOpen"/> turns false and <see cref="Diplomacy.ProcessProposals"/>
        /// prunes it.</summary>
        private static DateTime OfferExpiry(FactionState from, FactionState to, DiplomacyMessage msg, FactionTuning t)
        {
            bool alliance = msg.Terms != null && msg.Terms.Any(x => x.Verb == DiplomacyVerb.Alliance);
            return msg.IssuedUtc + Diplomacy.OfferTtl(from, to, alliance, t);
        }

        private static bool HasFreshOffer(FactionState from, FactionState to, FactionTuning t, DateTime nowUtc)
        {
            DiplomacyMessage msg;
            if (from == null || to == null || !from.Proposals.TryGetValue(to.Tag, out msg) || msg == null) return false;
            return Diplomacy.OfferOpen(from, to, msg, nowUtc, t);
        }

        private static string Band(double v, FactionTuning t)
        {
            if (t == null) return "?";
            if (v < t.HostileRelationThreshold) return "hostile";
            if (v >= t.AllyRelationThreshold) return "friendly";
            return "neutral";
        }

        private static string TermsOf(DiplomacyMessage msg)
        {
            if (msg == null || msg.Terms == null || msg.Terms.Length == 0) return "empty";
            var parts = new List<string>();
            foreach (DiplomacyTerm term in msg.Terms) parts.Add(term.Verb.ToString());
            return string.Join("+", parts.ToArray());
        }

        private static string Describe(DiplomacyVerb verb)
        {
            return verb == DiplomacyVerb.Alliance ? "an alliance" : "non-aggression";
        }

        /// <summary>" (open until MM-dd HH:mmZ)" for an offer, from its <see cref="Diplomacy.OfferTtl"/> expiry.</summary>
        private static string OpenUntil(DateTime? until)
        {
            if (!until.HasValue) return "";
            return " (open until " + until.Value.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z)";
        }

        private static string Span(TimeSpan span)
        {
            if (span.TotalHours >= 2) return Math.Round(span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h";
            if (span.TotalMinutes >= 1) return Math.Round(span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
            return Math.Round(span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        }

        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
    }
}
