using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Wire
{
    /// <summary>
    /// The words the FactionsUI diplomacy surfaces draw from a <see cref="WireDiplomacyRow"/>: the
    /// one-line readout on the terminal's Factions page, its tooltip, and the Diplomacy window's
    /// detail block. Here rather than in the client for the reason <see cref="FleetEngagement"/> is:
    /// the wire tests reach it, and a GUI class cannot be unit-tested at all.
    ///
    /// <para>ASCII only. SE's GUI fonts carry no arrow and no middle dot, and a missing glyph draws
    /// as nothing.</para>
    ///
    /// <para>Every time is measured against the snapshot's server clock, never the client's:
    /// <c>serverNowTicks</c> is the server's <c>UtcNow</c> when the snapshot was written, advanced
    /// by the caller by however long ago that was.</para>
    /// </summary>
    public static class DiplomacyText
    {
        public static string BandName(WireStanceBand band)
        {
            switch (band)
            {
                case WireStanceBand.Hostile:  return "Hostile";
                case WireStanceBand.Friendly: return "Friendly";
                default:                      return "Neutral";
            }
        }

        /// <summary>A relation value with an explicit sign, two decimals, invariant culture.</summary>
        public static string Signed(double v)
        {
            string s = Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture);
            if (s == "0.00") return s;
            return (v < 0 ? "-" : "+") + s;
        }

        /// <summary>"by you", "by IMPR", "both ways", or "none".</summary>
        public static string EmbargoText(WireDiplomacyRow row)
        {
            if (row.EmbargoByYou && row.EmbargoByThem) return "both ways";
            if (row.EmbargoByYou) return "by you";
            if (row.EmbargoByThem) return "by " + row.NpcTag;
            return "none";
        }

        /// <summary>How long until <paramref name="untilTicks"/>: "1 h 05 min", "42 min", "30 s",
        /// or "now" once it has passed. Empty for 0 (no such time).</summary>
        public static string Remaining(long untilTicks, long nowTicks)
        {
            if (untilTicks <= 0) return "";
            long delta = untilTicks - nowTicks;
            if (delta <= 0) return "now";
            TimeSpan span = TimeSpan.FromTicks(delta);
            if (span.TotalHours >= 1)
                return ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h "
                       + span.Minutes.ToString("00", CultureInfo.InvariantCulture) + " min";
            if (span.TotalMinutes >= 1)
                return ((int)Math.Ceiling(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min";
            return ((int)Math.Ceiling(span.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>A proposal's terms as a player reads them: Core's <c>Truce</c> is "peace".</summary>
        public static string OfferName(string terms)
        {
            if (string.IsNullOrEmpty(terms)) return "";
            string[] parts = terms.Split('+');
            var names = new List<string>(parts.Length);
            foreach (string p in parts)
            {
                switch (p)
                {
                    case "Truce":         names.Add("peace"); break;
                    case "NonAggression": names.Add("non-aggression"); break;
                    case "Alliance":      names.Add("alliance"); break;
                    default:              names.Add(p); break;
                }
            }
            return string.Join(" + ", names.ToArray());
        }

        /// <summary>The Factions page's readout under the reputation bar, e.g.
        /// <c>IMPR -&gt; you: Hostile -0.63 | you -&gt; IMPR: Neutral 0.00 | embargo: by IMPR | truce: none</c>.
        /// Trade-barred and allied are appended only when they hold. The page draws it as two lines,
        /// <see cref="StanceHead"/> and <see cref="StanceTail"/>, joined here by the same separator.</summary>
        public static string StanceLine(WireDiplomacyRow row, long nowTicks)
        {
            return StanceHead(row) + " | " + StanceTail(row, nowTicks);
        }

        /// <summary>Both stances: <c>IMPR -&gt; you: Hostile -0.63 | you -&gt; IMPR: Neutral 0.00</c>.</summary>
        public static string StanceHead(WireDiplomacyRow row)
        {
            var sb = new StringBuilder();
            sb.Append(row.NpcTag).Append(" -> you: ").Append(BandName(row.TheirBand)).Append(' ').Append(Signed(row.TheirView));
            sb.Append(" | you -> ").Append(row.NpcTag).Append(": ").Append(BandName(row.YourBand)).Append(' ').Append(Signed(row.YourStance));
            return sb.ToString();
        }

        /// <summary>What stands between them: <c>embargo: by IMPR | truce: none</c>, plus
        /// <c>| allied</c> and <c>| trade barred</c> when they hold.</summary>
        public static string StanceTail(WireDiplomacyRow row, long nowTicks)
        {
            var sb = new StringBuilder();
            sb.Append("embargo: ").Append(EmbargoText(row));
            sb.Append(" | truce: ");
            string truce = TruceLeft(row, nowTicks);
            sb.Append(truce.Length == 0 ? "none" : truce);
            if (row.Allied) sb.Append(" | allied");
            if (row.TradeBarred) sb.Append(" | trade barred");
            return sb.ToString();
        }

        /// <summary>Time left on a truce, or empty when none holds.</summary>
        public static string TruceLeft(WireDiplomacyRow row, long nowTicks)
        {
            if (row.TruceUntilTicks <= 0 || row.TruceUntilTicks <= nowTicks) return "";
            return Remaining(row.TruceUntilTicks, nowTicks);
        }

        /// <summary>The stance line's tooltip: the pending offers both ways, then whatever else
        /// changes what the player can do.</summary>
        public static string Tooltip(WireDiplomacyRow row, long nowTicks)
        {
            var lines = new List<string>();
            lines.Add(OffersLine(row, nowTicks, true));
            lines.Add(OffersLine(row, nowTicks, false));
            if (row.TradeBarred)
                lines.Add("Trade is barred: " + (row.EmbargoByYou || row.EmbargoByThem ? "an embargo" : "one side is hostile")
                          + ". Convoys and " + row.NpcTag + "'s stations will not trade with your faction.");
            if (row.Implacable) lines.Add(row.NpcTag + " takes no gifts and makes no agreements.");
            lines.Add("Diplomacy... to act. Only a faction leader can.");
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>"You offered IMPR: peace (expires in 42 min)" or "You have no offer out to
        /// IMPR." — <paramref name="yours"/> picks the direction.</summary>
        public static string OffersLine(WireDiplomacyRow row, long nowTicks, bool yours)
        {
            string terms = yours ? row.YouOffered : row.TheyOffered;
            long expires = yours ? row.YouOfferedExpiresTicks : row.TheyOfferedExpiresTicks;
            if (string.IsNullOrEmpty(terms))
                return yours ? "You have no offer out to " + row.NpcTag + "."
                             : row.NpcTag + " has no offer out to you.";
            string head = yours ? "You offered " + row.NpcTag + ": " : row.NpcTag + " offered you: ";
            string left = Remaining(expires, nowTicks);
            return head + OfferName(terms) + (left.Length == 0 ? "" : left == "now" ? " (expiring)" : " (expires in " + left + ")");
        }

        /// <summary>The Diplomacy window's detail block for one NPC.</summary>
        public static string Readout(WireDiplomacyRow row, string yourTag, long giftCost, long treasury, long nowTicks)
        {
            string you = string.IsNullOrEmpty(yourTag) ? "your faction" : "[" + yourTag + "]";
            var sb = new StringBuilder();
            sb.Append(row.NpcTag).Append("'s view of ").Append(you).Append(": ")
              .Append(BandName(row.TheirBand)).Append(' ').Append(Signed(row.TheirView)).Append('\n');
            sb.Append(you).Append("'s stance toward ").Append(row.NpcTag).Append(": ")
              .Append(BandName(row.YourBand)).Append(' ').Append(Signed(row.YourStance))
              .Append("  (set by your leader; it does not drift)\n");
            sb.Append("Embargo: ").Append(EmbargoText(row)).Append('\n');
            sb.Append("Trade: ").Append(row.TradeBarred ? "barred both ways" : "open").Append('\n');
            string truce = TruceLeft(row, nowTicks);
            sb.Append("Truce: ").Append(truce.Length == 0 ? "none" : "holds for " + truce).Append('\n');
            if (row.Allied) sb.Append("Allied.\n");
            sb.Append('\n');
            sb.Append(OffersLine(row, nowTicks, true)).Append('\n');
            sb.Append(OffersLine(row, nowTicks, false)).Append('\n');
            sb.Append('\n');
            sb.Append(GiftLine(row, giftCost, treasury, nowTicks));
            return sb.ToString();
        }

        /// <summary>What a gift would cost and whether it can go now.</summary>
        public static string GiftLine(WireDiplomacyRow row, long giftCost, long treasury, long nowTicks)
        {
            if (row.Implacable) return row.NpcTag + " cannot be swayed by gifts.";
            string head = "Gift: " + giftCost.ToString("N0", CultureInfo.InvariantCulture) + " credits from the faction treasury ("
                          + treasury.ToString("N0", CultureInfo.InvariantCulture) + " held)";
            if (row.GiftReadyTicks > nowTicks)
                return head + ". The next one can go in " + Remaining(row.GiftReadyTicks, nowTicks) + ".";
            if (treasury < giftCost) return head + ". The treasury cannot pay it.";
            return head + ".";
        }
    }
}
