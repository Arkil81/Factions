using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// The seven reputation marks SE's economy definition carries, and the two thresholds that turn
    /// a number into a band.
    ///
    /// <para><b>Passed in rather than hard-coded, for the same reason
    /// <see cref="EstateSeeder.Seed"/> takes its geometry as a delegate.</b> These are the running
    /// world's numbers, read off <c>MySessionComponentEconomyDefinition</c> by the host. Core must
    /// not carry a second copy that can silently disagree with the world it is planning for — the
    /// defaults here are vanilla's shipped values and exist so the Sim and the tests have a scale at
    /// all, not so anything reads them at runtime.</para>
    ///
    /// <para>The band thresholds mirror <c>TranslateReputationToRelationship</c> exactly: below
    /// <see cref="NeutralMin"/> is Enemies, at or above <see cref="FriendlyMin"/> is Friends, and
    /// everything between is Neutral. That function is what decides whether a turret fires, so an
    /// approximation of it here would report a stance the game does not hold.</para>
    /// </summary>
    public sealed class ReputationScale
    {
        public int HostileMin = -1500;
        public int HostileMid = -1000;
        public int NeutralMin = -500;
        public int NeutralMid = 0;
        public int FriendlyMin = 500;
        public int FriendlyMid = 1000;
        public int FriendlyMax = 1500;

        /// <summary>Vanilla's shipped numbers — a scale for the Sim and the tests, never a
        /// substitute for the running world's.</summary>
        public static ReputationScale Vanilla { get { return new ReputationScale(); } }

        /// <summary>A reputation onto <see cref="FactionState.Relations"/>' −1..1, split about
        /// <see cref="NeutralMid"/> so an asymmetric scale maps with 0 still meaning neutral on both
        /// sides. Linear on each side — the member aggregate's reading of a personal record, not the
        /// inverse of <see cref="ReputationFor"/>.</summary>
        public double Normalise(int reputation)
        {
            if (reputation >= NeutralMid)
            {
                int span = FriendlyMax - NeutralMid;
                if (span <= 0) return 0.0;
                return MathUtil.Clamp((double)(reputation - NeutralMid) / span, 0.0, 1.0);
            }
            int below = NeutralMid - HostileMin;
            if (below <= 0) return 0.0;
            return MathUtil.Clamp((double)(reputation - NeutralMid) / below, -1.0, 0.0);
        }

        /// <summary>Which band a reputation falls in, by SE's own thresholds.</summary>
        public string BandOf(int reputation)
        {
            if (reputation < NeutralMin) return "Enemies";
            if (reputation < FriendlyMin) return "Neutral";
            return "Friends";
        }

        /// <summary>
        /// Project one of the brain's own standings (−1..1) onto this world's reputation scale, so
        /// SE's faction table can follow <see cref="FactionState.Relations"/> rather than the
        /// founding recipe's static pin (FAC-274). Factions' model is authoritative; this is the
        /// projection of it.
        ///
        /// <para><b>Piecewise-linear through five knots</b>, not a straight line, because the two
        /// models put their boundaries in different places and a straight line loses the ones that
        /// matter. Mapping −1..1 evenly onto <see cref="HostileMin"/>..<see cref="FriendlyMax"/> sends
        /// a standing at <paramref name="hostileRelationThreshold"/> (−0.3 live) to −450, which
        /// <see cref="BandOf"/> reads as <c>Neutral</c> — so a faction the brain considers a valid war
        /// target would have SE's turrets holding fire. The knots pin the two thresholds that decide
        /// behaviour into the bands that mean the same thing:</para>
        ///
        /// <list type="bullet">
        /// <item><description>−1 → <see cref="HostileMin"/> (nothing below it)</description></item>
        /// <item><description><paramref name="hostileRelationThreshold"/> → <see cref="HostileMid"/> — inside Enemies</description></item>
        /// <item><description>0 → <see cref="NeutralMid"/></description></item>
        /// <item><description><paramref name="allyRelationThreshold"/> → <see cref="FriendlyMid"/> — inside Friends</description></item>
        /// <item><description>+1 → <see cref="FriendlyMax"/></description></item>
        /// </list>
        ///
        /// <para>Monotonic across the whole range, so a standing that falls can never raise the
        /// projected reputation. A threshold outside its own half (0 or worse for the hostile one,
        /// 0 or better for the ally one) is not usable as a knot and the corresponding half falls
        /// back to a straight line — a degenerate configuration must not produce a non-monotonic
        /// curve.</para>
        /// </summary>
        public int ReputationFor(double relation, double hostileRelationThreshold, double allyRelationThreshold)
        {
            double r = relation;
            if (r < -1.0) r = -1.0;
            if (r >  1.0) r =  1.0;

            if (r < 0.0)
            {
                double h = hostileRelationThreshold;
                if (!(h > -1.0 && h < 0.0)) return Lerp(NeutralMid, HostileMin, -r);   // degenerate: straight line
                return r <= h
                    ? Lerp(HostileMid, HostileMin, (h - r) / (h + 1.0))                // −1 .. threshold
                    : Lerp(NeutralMid, HostileMid, r / h);                             // threshold .. 0
            }
            if (r > 0.0)
            {
                double a = allyRelationThreshold;
                if (!(a > 0.0 && a < 1.0)) return Lerp(NeutralMid, FriendlyMax, r);     // degenerate: straight line
                return r >= a
                    ? Lerp(FriendlyMid, FriendlyMax, (r - a) / (1.0 - a))               // threshold .. +1
                    : Lerp(NeutralMid, FriendlyMid, r / a);                             // 0 .. threshold
            }
            return NeutralMid;
        }

        /// <summary>Rounded linear interpolation between two marks. Away-from-zero at the half-step,
        /// so a rounding tie never depends on the sign of the band being interpolated.</summary>
        private static int Lerp(int from, int to, double f)
        {
            if (f < 0.0) f = 0.0;
            if (f > 1.0) f = 1.0;
            return (int)Math.Round(from + (to - from) * f, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Turn an authored stance into a number. Accepts a mark by name, one of the three band
        /// aliases, or a raw integer.
        ///
        /// <para><c>Enemies</c> and <c>Friends</c> resolve to the <b>Mid</b> marks, not the extremes.
        /// An authored stance is a starting position that ordinary play then moves — Raid and Conquer
        /// both push relations down — and starting at <see cref="HostileMin"/> is a floor with no
        /// room left below it. Author the extreme by name when that is what is meant.</para>
        /// </summary>
        public bool TryResolve(string stance, out int reputation, out string why)
        {
            reputation = 0; why = null;
            if (string.IsNullOrEmpty(stance)) { why = "empty stance."; return false; }

            string s = stance.Trim();
            if (string.Equals(s, "HostileMin",   StringComparison.OrdinalIgnoreCase)) { reputation = HostileMin;   return true; }
            if (string.Equals(s, "HostileMid",   StringComparison.OrdinalIgnoreCase)) { reputation = HostileMid;   return true; }
            if (string.Equals(s, "NeutralMin",   StringComparison.OrdinalIgnoreCase)) { reputation = NeutralMin;   return true; }
            if (string.Equals(s, "NeutralMid",   StringComparison.OrdinalIgnoreCase)) { reputation = NeutralMid;   return true; }
            if (string.Equals(s, "FriendlyMin",  StringComparison.OrdinalIgnoreCase)) { reputation = FriendlyMin;  return true; }
            if (string.Equals(s, "FriendlyMid",  StringComparison.OrdinalIgnoreCase)) { reputation = FriendlyMid;  return true; }
            if (string.Equals(s, "FriendlyMax",  StringComparison.OrdinalIgnoreCase)) { reputation = FriendlyMax;  return true; }
            if (string.Equals(s, "Enemies",      StringComparison.OrdinalIgnoreCase)) { reputation = HostileMid;   return true; }
            if (string.Equals(s, "Neutral",      StringComparison.OrdinalIgnoreCase)) { reputation = NeutralMid;   return true; }
            if (string.Equals(s, "Friends",      StringComparison.OrdinalIgnoreCase)) { reputation = FriendlyMid;  return true; }

            int raw;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
            {
                if (raw < HostileMin || raw > FriendlyMax)
                {
                    why = "reputation " + raw + " is outside the scale (" + HostileMin + ".." + FriendlyMax + ").";
                    return false;
                }
                reputation = raw;
                return true;
            }

            why = "'" + stance + "' is not a stance. Use Enemies/Neutral/Friends, a named mark "
                + "(HostileMin, HostileMid, NeutralMin, NeutralMid, FriendlyMin, FriendlyMid, "
                + "FriendlyMax), or a raw reputation.";
            return false;
        }

        /// <summary>Shape check for <see cref="FactionRecipe.Validate"/> — is this a stance at all?
        /// Names are fixed, so this needs no world; a raw number is range-checked against
        /// vanilla's scale here and against the running world's again at plan time. A world that
        /// widened the scale would reject at load and pass at plan, which is the harmless direction.</summary>
        public static bool IsKnownStance(string stance)
        {
            int ignored; string alsoIgnored;
            return Vanilla.TryResolve(stance, out ignored, out alsoIgnored);
        }
    }

    /// <summary>The authored diplomacy: who stands where with whom, at genesis.</summary>
    public sealed class RelationRecipe
    {
        /// <summary>The wildcard key. A stance filed under this reaches every other registered
        /// faction the pair is not named for explicitly.</summary>
        public const string Wildcard = "*";

        /// <summary>Stance for any pair no entry reaches. Absent means <c>Neutral</c>.</summary>
        public string Default;

        /// <summary>Tag -> (tag or <c>*</c>) -> stance. A relation is one cell, not two: SE stores it
        /// on an unordered pair, so <c>SPRT.IGSL</c> and <c>IGSL.SPRT</c> name the same thing and
        /// authoring both with different stances is refused rather than resolved by order.</summary>
        public Dictionary<string, Dictionary<string, string>> Stances;
    }

    /// <summary>One resolved pair, and where its stance came from.</summary>
    public sealed class RelationPlanEntry
    {
        public string TagA;
        public string TagB;
        public int Reputation;
        /// <summary>What decided it — <c>explicit</c>, <c>* on SPRT</c>, <c>default</c>. Printed in the
        /// dry run, because "SPRT-UVCO is Enemies" and "SPRT-UVCO is Enemies because SPRT is hostile
        /// to everything" are different claims and only the second can be checked against intent.</summary>
        public string Source;
    }

    /// <summary>What applying the authored diplomacy would do.</summary>
    public sealed class RelationPlan
    {
        public readonly List<RelationPlanEntry> Entries = new List<RelationPlanEntry>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Refused as a whole. Half an authored diplomacy is worse than none — the pairs
        /// that resolved would sit beside the ones that did not, indistinguishable afterwards.</summary>
        public bool IsRefused;
    }

    /// <summary>
    /// Resolves <see cref="RelationRecipe"/> into a reputation for <b>every</b> pair of registered
    /// factions.
    ///
    /// <para><b>Every pair, not only the authored ones.</b> The point of authoring diplomacy is that
    /// the file is the whole statement; a plan that touched only named pairs would leave the rest
    /// holding whatever <c>MyFactionRelationGenerator</c> last computed, and the world would be half
    /// designed and half inherited with nothing saying which half is which. That is the state
    /// FAC-108 found and it is exactly what this replaces.</para>
    ///
    /// <para><b>Why authored at all, rather than re-running SE's generator.</b> The generator derives
    /// reputation from the minimum station-to-station distance per faction pair, normalised across
    /// every pair sharing the same faction-type combination. Against an authored estate those bands
    /// collapse: measured on this world, <c>Miner</c> and <c>Military</c> hold exactly one pair each,
    /// so min equals max and the pair is forced to the extreme whatever the distance, and the three
    /// <c>Builder</c> factions span 41 km of a 5,000 km world stretched across the full hostile
    /// range. It does not produce a worse answer than the stale one; it produces a differently
    /// arbitrary one.</para>
    /// </summary>
    public static class RelationPlanner
    {
        public static RelationPlan Build(FactionRecipe recipe, ICollection<string> registeredTags,
                                         ReputationScale scale)
        {
            var plan = new RelationPlan();
            if (scale == null) scale = ReputationScale.Vanilla;

            // Same refusal as EstateSeeder: "caller supplied nothing" and "caller supplied a matching
            // roster" must not produce the same silence.
            if (registeredTags == null)
            {
                plan.Warnings.Add("no faction roster supplied -- refusing rather than authoring "
                                + "standings between factions that may not exist.");
                plan.IsRefused = true;
                return plan;
            }

            RelationRecipe rel = recipe != null ? recipe.Relations : null;

            int defaultRep = scale.NeutralMid;
            if (rel != null && !string.IsNullOrEmpty(rel.Default))
            {
                string why;
                if (!scale.TryResolve(rel.Default, out defaultRep, out why))
                {
                    plan.Warnings.Add("Default: " + why);
                    plan.IsRefused = true;
                    return plan;
                }
            }

            var roster = new List<string>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string tag in registeredTags)
                if (!string.IsNullOrEmpty(tag) && known.Add(tag)) roster.Add(tag);
            roster.Sort(StringComparer.OrdinalIgnoreCase);

            // Index the authored stances case-insensitively in both directions, refusing a tag the
            // world does not have rather than dropping it. A stance filed under a tag that is not
            // here is authored intent that will never be honoured, and a silent skip is how the
            // estate came to place 45 nodes for owners that did not exist (FAC-105).
            var explicitStance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wildcardStance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (rel != null && rel.Stances != null)
            {
                foreach (KeyValuePair<string, Dictionary<string, string>> from in rel.Stances)
                {
                    if (string.IsNullOrEmpty(from.Key) || from.Value == null) continue;
                    if (!known.Contains(from.Key))
                    {
                        plan.Warnings.Add("Stances names '" + from.Key + "', which this world has no faction for.");
                        plan.IsRefused = true;
                        continue;
                    }
                    foreach (KeyValuePair<string, string> to in from.Value)
                    {
                        if (string.IsNullOrEmpty(to.Key)) continue;

                        if (to.Key == RelationRecipe.Wildcard)
                        {
                            string clash;
                            if (wildcardStance.TryGetValue(from.Key, out clash) && !StanceEquals(clash, to.Value))
                            {
                                plan.Warnings.Add(from.Key + ": two different '*' stances.");
                                plan.IsRefused = true;
                            }
                            wildcardStance[from.Key] = to.Value;
                            continue;
                        }

                        if (!known.Contains(to.Key))
                        {
                            plan.Warnings.Add(from.Key + " names '" + to.Key
                                            + "', which this world has no faction for.");
                            plan.IsRefused = true;
                            continue;
                        }
                        if (string.Equals(from.Key, to.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            plan.Warnings.Add(from.Key + " names itself; a faction has no standing with itself.");
                            plan.IsRefused = true;
                            continue;
                        }

                        string key = PairKey(from.Key, to.Key);
                        string existing;
                        if (explicitStance.TryGetValue(key, out existing) && !StanceEquals(existing, to.Value))
                        {
                            plan.Warnings.Add(key + ": authored twice, as '" + existing + "' and '"
                                            + to.Value + "'. A relation is one cell -- write it once.");
                            plan.IsRefused = true;
                            continue;
                        }
                        explicitStance[key] = to.Value;
                    }
                }
            }

            for (int i = 0; i < roster.Count; i++)
            {
                for (int j = i + 1; j < roster.Count; j++)
                {
                    string a = roster[i], b = roster[j];
                    string stance = null, source = null;

                    string pair = PairKey(a, b);
                    if (explicitStance.TryGetValue(pair, out stance))
                    {
                        source = "explicit";
                    }
                    else
                    {
                        string wa, wb;
                        bool hasA = wildcardStance.TryGetValue(a, out wa);
                        bool hasB = wildcardStance.TryGetValue(b, out wb);
                        if (hasA && hasB && !StanceEquals(wa, wb))
                        {
                            // Not resolved by precedence on purpose. Both authors said "everyone",
                            // they disagree about this pair, and picking one silently would be a
                            // guess presented as a decision.
                            plan.Warnings.Add(pair + ": '*' on " + a + " says '" + wa + "' and '*' on "
                                            + b + " says '" + wb + "'. Author the pair explicitly.");
                            plan.IsRefused = true;
                            continue;
                        }
                        if (hasA) { stance = wa; source = "* on " + a; }
                        else if (hasB) { stance = wb; source = "* on " + b; }
                    }

                    int rep;
                    if (stance == null) { rep = defaultRep; source = "default"; }
                    else
                    {
                        string why;
                        if (!scale.TryResolve(stance, out rep, out why))
                        {
                            plan.Warnings.Add(pair + ": " + why);
                            plan.IsRefused = true;
                            continue;
                        }
                    }

                    plan.Entries.Add(new RelationPlanEntry
                    {
                        TagA = a, TagB = b, Reputation = rep, Source = source,
                    });
                }
            }

            return plan;
        }

        private static bool StanceEquals(string x, string y)
        {
            return string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Ordinal-sorted "A-B", so the two directions of one relation share a key.</summary>
        private static string PairKey(string a, string b)
        {
            // Case-insensitive, because the dictionaries this keys into are. Sorting the raw
            // casing put "IGSL-sprt" and "SPRT-igsl" in different cells of one OrdinalIgnoreCase
            // dictionary, so a pair authored in mixed case would miss its own duplicate check.
            return StringComparer.OrdinalIgnoreCase.Compare(a, b) <= 0 ? a + "-" + b : b + "-" + a;
        }
    }
}
