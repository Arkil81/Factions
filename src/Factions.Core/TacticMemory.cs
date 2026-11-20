using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>Where one side stands against the other on one axis of a fight.</summary>
    public enum TacticLevel
    {
        Behind = 0,
        Even = 1,
        Ahead = 2,
    }

    /// <summary>Where the learner's pick came from.</summary>
    public enum TacticBasis
    {
        /// <summary>This faction's own record against this opponent in this context.</summary>
        Opponent = 0,
        /// <summary>No record against this opponent in this context: the context's record against
        /// every other opponent, capped at <see cref="TacticMemory.AggregateEvidence"/>.</summary>
        Aggregate = 1,
        /// <summary>No record in this context at all: the personality prior alone.</summary>
        Prior = 2,
    }

    /// <summary>
    /// The situation a tactic is chosen for, from one side's point of view: whether it out-ranges,
    /// out-paces and out-guns the other side, each at three levels — 27 buckets.
    ///
    /// <para>Read from the same inputs a <see cref="DuelMatchup"/> carries — both sides' bands, the
    /// station floor, both mobilities — so the commit gates and the duel bucket a fight alike.</para>
    /// </summary>
    public struct TacticContext
    {
        /// <summary>Mobility share (<see cref="RangeSchedule.AttackerShare"/> from this side) at or
        /// above which this side is ahead; at or below one minus it, behind.</summary>
        public const double MobilityAheadShare = 0.6;
        /// <summary>Total DPS ratio at or above which this side is ahead; at or below its inverse, behind.</summary>
        public const double ForceAheadRatio = 1.25;

        public TacticLevel Range;
        public TacticLevel Mobility;
        public TacticLevel Force;

        /// <summary>Three characters, range then mobility then force: <c>+</c> ahead, <c>=</c> even,
        /// <c>-</c> behind. What the memory is keyed on and what the log prints.</summary>
        public string Key
        {
            get { return new string(new[] { Sign(Range), Sign(Mobility), Sign(Force) }); }
        }

        public override string ToString() { return Key; }

        /// <summary>The context of the side firing <paramref name="own"/> (plus an always-in-range
        /// <paramref name="ownFlat"/>) at <paramref name="ownMobility"/> against the other.</summary>
        public static TacticContext Of(IReadOnlyList<WeaponBand> own, long ownFlat,
            IReadOnlyList<WeaponBand> enemy, long enemyFlat, double ownMobility, double enemyMobility,
            FactionTuning tuning)
        {
            double[] brackets = RangeSchedule.Brackets(tuning);
            double ownReach = ReachOf(own, ownFlat, brackets);
            double enemyReach = ReachOf(enemy, enemyFlat, brackets);
            double share = RangeSchedule.AttackerShare(ownMobility, enemyMobility);
            double ownDps = Sum(own) + Math.Max(0, ownFlat);
            double enemyDps = Sum(enemy) + Math.Max(0, enemyFlat);
            double ratio = enemyDps > 0 ? ownDps / enemyDps : (ownDps > 0 ? double.PositiveInfinity : 1.0);

            var ctx = new TacticContext();
            ctx.Range = ownReach > enemyReach ? TacticLevel.Ahead
                      : ownReach < enemyReach ? TacticLevel.Behind : TacticLevel.Even;
            ctx.Mobility = share >= MobilityAheadShare - 1e-9 ? TacticLevel.Ahead
                         : share <= 1.0 - MobilityAheadShare + 1e-9 ? TacticLevel.Behind : TacticLevel.Even;
            ctx.Force = ratio >= ForceAheadRatio ? TacticLevel.Ahead
                      : ratio <= 1.0 / ForceAheadRatio ? TacticLevel.Behind : TacticLevel.Even;
            return ctx;
        }

        /// <summary>Parses a <see cref="Key"/>; false for anything else.</summary>
        public static bool TryParse(string key, out TacticContext ctx)
        {
            ctx = new TacticContext();
            if (key == null || key.Length != 3) return false;
            TacticLevel r, m, f;
            if (!Level(key[0], out r) || !Level(key[1], out m) || !Level(key[2], out f)) return false;
            ctx.Range = r; ctx.Mobility = m; ctx.Force = f;
            return true;
        }

        /// <summary>The longest bracket this side lands anything at; -1 when it lands nothing
        /// anywhere, so an unarmed side is out-ranged by any armed one.</summary>
        private static double ReachOf(IReadOnlyList<WeaponBand> bands, long flat, double[] brackets)
        {
            for (int i = brackets.Length - 1; i >= 0; i--)
                if (CombatStrength.InRangeDps(bands, brackets[i]) + Math.Max(0, flat) > 0) return brackets[i];
            return -1;
        }

        private static double Sum(IReadOnlyList<WeaponBand> bands)
        {
            if (bands == null) return 0;
            double s = 0;
            for (int i = 0; i < bands.Count; i++) s += bands[i].Dps;
            return s;
        }

        private static char Sign(TacticLevel l)
        {
            return l == TacticLevel.Ahead ? '+' : l == TacticLevel.Behind ? '-' : '=';
        }

        private static bool Level(char c, out TacticLevel l)
        {
            l = TacticLevel.Even;
            if (c == '+') { l = TacticLevel.Ahead; return true; }
            if (c == '-') { l = TacticLevel.Behind; return true; }
            return c == '=';
        }
    }

    /// <summary>What <see cref="TacticMemory.Pick"/> chose and why.</summary>
    public struct TacticPick
    {
        public TacticArm Arm;
        public BreakOffLevel BreakOff;
        /// <summary>The personality default — the arm and break-off the prior favours.</summary>
        public TacticArm PriorArm;
        public BreakOffLevel PriorBreakOff;
        public TacticBasis Basis;
        /// <summary>The Thompson draw that won.</summary>
        public double Sampled;
        public string Context;
    }

    /// <summary>
    /// One side's result of one fight, as the learner reads it. The abstract duel fills it from its
    /// capacity pools; a physical fight fills it from what the grids measured. Both go through
    /// <see cref="TacticMemory.Record"/>.
    /// </summary>
    public struct TacticOutcome
    {
        /// <summary>The faction fought. An outcome with no opponent teaches nothing.</summary>
        public string OpponentTag;
        /// <summary>The <see cref="TacticContext.Key"/> the tactic was picked in.</summary>
        public string Context;
        public TacticArm Arm;
        public BreakOffLevel BreakOff;
        /// <summary>Damage this side dealt and took, in the same units on both sides.</summary>
        public double DamageDealt;
        public double DamageTaken;
        /// <summary>What the commit arithmetic (<see cref="CombatStrength.ScheduleDamage"/>, the
        /// jitter at its mean over the whole schedule) predicted for the same two figures; negative
        /// when there was no prediction.</summary>
        public double PredictedDealt;
        public double PredictedTaken;
        public int HullsLost;
        public int HullsBrought;
        /// <summary>The tactic was ordered by a commander rather than picked: the outcome is learned
        /// as a demonstration, at <see cref="TacticMemory.DemonstrationWeight"/>.</summary>
        public bool Commanded;
    }

    /// <summary>What one <see cref="TacticMemory.Record"/> did, for <c>TACTIC_REVIEW</c>.</summary>
    public struct TacticReview
    {
        public bool Recorded;
        /// <summary>When the outcome was recorded.</summary>
        public DateTime Utc;
        public string OpponentTag;
        public string Context;
        public TacticArm Arm;
        public BreakOffLevel BreakOff;
        /// <summary>Predicted damage share, dealt ÷ (dealt + taken); NaN without a prediction.</summary>
        public double Predicted;
        /// <summary>Actual damage share.</summary>
        public double Actual;
        public double Reward;
        public double Weight;
        /// <summary>The actual share fell short of the prediction by <see cref="TacticMemory.MistakeGap"/> or more.</summary>
        public bool Mistake;
        public bool Commanded;
        public int HullsLost;
        public int HullsBrought;
        /// <summary>Posterior mean of the cell after the update, prior included.</summary>
        public double PosteriorMean;
        /// <summary>Evidence in the cell after the update, prior excluded — the count that must grow.</summary>
        public double Evidence;
    }

    /// <summary>
    /// A faction's record of which tactic works against whom (docs/fleet-tactics.md, "The learner").
    ///
    /// <para><b>Shape.</b> Keyed by (opponent tag, <see cref="TacticContext"/>); each key holds one
    /// cell per arm × break-off, 12 in all. A cell is a Beta posterior: the personality prior
    /// (<see cref="PriorStrength"/> pseudo-fights, its mean raised for the personality's default arm
    /// and break-off) plus evidence — the weighted sum of rewards <c>A</c> and of their complements
    /// <c>B</c>. Beta rather than mean and count because the reward is a share in [0, 1], so a
    /// fractional Beta update is the natural conjugate, Thompson sampling is one draw per cell, and
    /// decay is a single factor on both terms that leaves the prior untouched.</para>
    ///
    /// <para><b>Choice</b> is Thompson sampling: one Beta draw per cell, the highest wins; a cell is
    /// drawn with <see cref="SiblingShare"/> of its same-arm siblings' evidence added. The draws
    /// come from a <see cref="SeededRandom"/> seeded by (faction, opponent, context,
    /// <see cref="Updates"/>), so the commit gate that asks "what would I pick" and the duel that
    /// follows get the same answer until a fight in between teaches the faction something, and the
    /// world's own random stream is never consumed. <see cref="Exploration"/> widens or narrows every
    /// draw by the personality — a reckless faction tries new things, a cautious one exploits.</para>
    ///
    /// <para><b>Fallback.</b> No evidence against this opponent in this context → the context's
    /// evidence against every other opponent, capped at <see cref="AggregateEvidence"/> so a new
    /// opponent is only weakly prejudged → the prior alone.</para>
    ///
    /// <para><b>Forgetting.</b> Evidence halves every <see cref="HalfLifeDays"/> of world time, applied
    /// against the caller's clock whenever a cell is read or written, and no cell holds more than
    /// <see cref="MaxEvidence"/>, so a refitted opponent is relearned. At most
    /// <see cref="MaxOpponents"/> opponents are kept; the least recently fought is dropped.</para>
    ///
    /// <para>Every parameter is a code constant, not tuning: they shape a learner, not a balance
    /// an operator sets.</para>
    /// </summary>
    public sealed class TacticMemory
    {
        public const int Actions = 12;
        public const int MaxOpponents = 16;
        public const double HalfLifeDays = 7.0;
        public const double MaxEvidence = 30.0;
        public const double AggregateEvidence = 8.0;
        public const double PriorStrength = 6.0;
        public const double PriorMean = 0.45;
        public const double PriorArmBonus = 0.1;
        public const double PriorBreakOffBonus = 0.1;
        /// <summary>Reward lost for losing a whole squadron; a share of it per hull lost.</summary>
        public const double HullLossPenalty = 0.5;
        public const double MistakeGap = 0.2;
        public const double MistakeWeight = 2.0;
        public const double DemonstrationWeight = 0.5;
        /// <summary>The share of its same-arm siblings' evidence a cell borrows when sampled: the arm
        /// decides the range fought, so a record at one break-off says most of what it would at
        /// another.</summary>
        public const double SiblingShare = 0.5;
        /// <summary>Recorded reviews kept for the player's Tactics window, newest last.</summary>
        public const int MaxReviews = 8;

        /// <summary>One (opponent, context) key: evidence per cell as of <see cref="Utc"/>.</summary>
        public sealed class Bucket
        {
            public readonly double[] A = new double[Actions];
            public readonly double[] B = new double[Actions];
            public DateTime Utc;

            public bool IsEmpty
            {
                get
                {
                    for (int i = 0; i < Actions; i++) if (A[i] > 0 || B[i] > 0) return false;
                    return true;
                }
            }
        }

        public sealed class Opponent
        {
            public DateTime LastUtc;
            public readonly Dictionary<string, Bucket> Buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        }

        /// <summary>Outcomes recorded, ever. Part of every draw's seed. Persisted.</summary>
        public long Updates;

        private readonly Dictionary<string, Opponent> _opponents = new Dictionary<string, Opponent>(StringComparer.Ordinal);
        private readonly List<TacticReview> _reviews = new List<TacticReview>();

        /// <summary>The last <see cref="MaxReviews"/> recorded outcomes, oldest first — what
        /// <c>TACTIC_REVIEW</c> states, kept so a player can read it. Persisted.</summary>
        public IReadOnlyList<TacticReview> Reviews { get { return _reviews; } }

        public int OpponentCount { get { return _opponents.Count; } }
        public bool IsEmpty { get { return _opponents.Count == 0 && Updates == 0; } }

        /// <summary>Every opponent held, ordinal by tag.</summary>
        public List<KeyValuePair<string, Opponent>> Opponents()
        {
            var list = new List<KeyValuePair<string, Opponent>>(_opponents);
            list.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            return list;
        }

        public Opponent Find(string opponentTag)
        {
            Opponent o;
            return opponentTag != null && _opponents.TryGetValue(opponentTag, out o) ? o : null;
        }

        /// <summary>The cell index of an arm × break-off.</summary>
        public static int Index(TacticArm arm, BreakOffLevel breakOff) { return (int)arm * 3 + (int)breakOff; }
        public static TacticArm ArmOf(int index) { return (TacticArm)(index / 3); }
        public static BreakOffLevel BreakOffOf(int index) { return (BreakOffLevel)(index % 3); }

        // ---- choice ------------------------------------------------------------------------------

        /// <summary>Thompson-samples a tactic for <paramref name="selfTag"/> against
        /// <paramref name="opponentTag"/> (null: an unowned defence, read through the fallback) in
        /// <paramref name="context"/>. Reads the memory and never writes it.</summary>
        public TacticPick Pick(string selfTag, string personalityId, string opponentTag, TacticContext context, DateTime nowUtc)
        {
            string key = context.Key;
            var a = new double[Actions];
            var b = new double[Actions];
            TacticBasis source = Evidence(opponentTag, key, nowUtc, a, b);
            DuelStance def = TacticChoice.DefaultFor(personalityId);
            double explore = Exploration(personalityId);
            var rng = new SeededRandom(Seed(selfTag, opponentTag, key, Updates));

            int best = Index(def.Arm, def.BreakOff);
            double bestTheta = double.NegativeInfinity;
            for (int i = 0; i < Actions; i++)
            {
                double alpha0, beta0;
                Prior(i, def, out alpha0, out beta0);
                double pooledA = a[i], pooledB = b[i];
                int first = (i / 3) * 3;
                for (int j = first; j < first + 3; j++)
                    if (j != i) { pooledA += SiblingShare * a[j]; pooledB += SiblingShare * b[j]; }
                double theta = BetaSample(rng, (alpha0 + pooledA) / explore, (beta0 + pooledB) / explore);
                if (theta > bestTheta) { bestTheta = theta; best = i; }
            }
            return new TacticPick
            {
                Arm = ArmOf(best),
                BreakOff = BreakOffOf(best),
                PriorArm = def.Arm,
                PriorBreakOff = def.BreakOff,
                Basis = source,
                Sampled = bestTheta,
                Context = key,
            };
        }

        /// <summary>The evidence a pick reads, decayed to <paramref name="nowUtc"/>, and where it
        /// came from.</summary>
        public TacticBasis Evidence(string opponentTag, string contextKey, DateTime nowUtc, double[] a, double[] b)
        {
            Opponent own = Find(opponentTag);
            Bucket bucket;
            if (own != null && own.Buckets.TryGetValue(contextKey, out bucket) && !bucket.IsEmpty)
            {
                double f = DecayFactor(bucket.Utc, nowUtc);
                for (int i = 0; i < Actions; i++) { a[i] = bucket.A[i] * f; b[i] = bucket.B[i] * f; }
                return TacticBasis.Opponent;
            }

            double total = 0;
            foreach (KeyValuePair<string, Opponent> kv in Opponents())
            {
                if (opponentTag != null && kv.Key == opponentTag) continue;
                if (!kv.Value.Buckets.TryGetValue(contextKey, out bucket)) continue;
                double f = DecayFactor(bucket.Utc, nowUtc);
                for (int i = 0; i < Actions; i++)
                {
                    a[i] += bucket.A[i] * f;
                    b[i] += bucket.B[i] * f;
                    total += (bucket.A[i] + bucket.B[i]) * f;
                }
            }
            if (total <= 0) return TacticBasis.Prior;
            if (total > AggregateEvidence)
            {
                double scale = AggregateEvidence / total;
                for (int i = 0; i < Actions; i++) { a[i] *= scale; b[i] *= scale; }
            }
            return TacticBasis.Aggregate;
        }

        // ---- learning ----------------------------------------------------------------------------

        /// <summary>
        /// Learns from one fight — the one entry point every fight feeds, abstract or physical.
        ///
        /// <para><b>Reward</b> is damage dealt ÷ (dealt + taken), less
        /// <see cref="HullLossPenalty"/> × the share of hulls lost, clamped to [0, 1]. <b>A
        /// mistake</b> is an actual damage share at least <see cref="MistakeGap"/> below the
        /// predicted one; it counts <see cref="MistakeWeight"/> times. A commanded outcome counts
        /// <see cref="DemonstrationWeight"/> times.</para>
        /// </summary>
        public TacticReview Record(TacticOutcome o, string personalityId, DateTime nowUtc)
        {
            var review = new TacticReview { OpponentTag = o.OpponentTag, Context = o.Context, Arm = o.Arm, BreakOff = o.BreakOff };
            TacticContext parsed;
            if (string.IsNullOrEmpty(o.OpponentTag) || !TacticContext.TryParse(o.Context, out parsed)) return review;

            double actual = Share(o.DamageDealt, o.DamageTaken);
            double predicted = o.PredictedDealt >= 0 && o.PredictedTaken >= 0
                ? Share(o.PredictedDealt, o.PredictedTaken) : double.NaN;
            double hullShare = o.HullsBrought > 0 ? Math.Min(1.0, Math.Max(0, o.HullsLost) / (double)o.HullsBrought) : 0;
            double reward = Clamp01(actual - HullLossPenalty * hullShare);
            bool mistake = !double.IsNaN(predicted) && actual <= predicted - MistakeGap;
            double weight = (mistake ? MistakeWeight : 1.0) * (o.Commanded ? DemonstrationWeight : 1.0);

            Opponent opp = Touch(o.OpponentTag, nowUtc);
            Bucket bucket;
            if (!opp.Buckets.TryGetValue(o.Context, out bucket))
            {
                bucket = new Bucket { Utc = nowUtc };
                opp.Buckets[o.Context] = bucket;
            }
            Decay(bucket, nowUtc);
            int i = Index(o.Arm, o.BreakOff);
            bucket.A[i] += weight * reward;
            bucket.B[i] += weight * (1.0 - reward);
            double held = bucket.A[i] + bucket.B[i];
            if (held > MaxEvidence)
            {
                double scale = MaxEvidence / held;
                bucket.A[i] *= scale;
                bucket.B[i] *= scale;
            }
            Updates++;

            double alpha0, beta0;
            Prior(i, TacticChoice.DefaultFor(personalityId), out alpha0, out beta0);
            review.Recorded = true;
            review.Utc = nowUtc;
            review.Predicted = predicted;
            review.Actual = actual;
            review.Reward = reward;
            review.Weight = weight;
            review.Mistake = mistake;
            review.Commanded = o.Commanded;
            review.HullsLost = o.HullsLost;
            review.HullsBrought = o.HullsBrought;
            review.Evidence = bucket.A[i] + bucket.B[i];
            review.PosteriorMean = (alpha0 + bucket.A[i]) / (alpha0 + beta0 + review.Evidence);
            AppendReview(review);
            return review;
        }

        /// <summary>Restores one persisted review, appended after those already held.</summary>
        public void RestoreReview(TacticReview review)
        {
            if (string.IsNullOrEmpty(review.OpponentTag)) return;
            review.Recorded = true;
            AppendReview(review);
        }

        private void AppendReview(TacticReview review)
        {
            _reviews.Add(review);
            if (_reviews.Count > MaxReviews) _reviews.RemoveRange(0, _reviews.Count - MaxReviews);
        }

        /// <summary>Restores one persisted key. Tolerant: an unparseable context or a cell index
        /// out of range is skipped.</summary>
        public void Restore(string opponentTag, DateTime lastUtc, string contextKey, DateTime bucketUtc,
            IEnumerable<KeyValuePair<int, double[]>> cells)
        {
            TacticContext parsed;
            if (string.IsNullOrEmpty(opponentTag) || !TacticContext.TryParse(contextKey, out parsed)) return;
            Opponent opp;
            if (!_opponents.TryGetValue(opponentTag, out opp))
            {
                if (_opponents.Count >= MaxOpponents) return;
                opp = new Opponent { LastUtc = lastUtc };
                _opponents[opponentTag] = opp;
            }
            if (lastUtc > opp.LastUtc) opp.LastUtc = lastUtc;
            var bucket = new Bucket { Utc = bucketUtc };
            if (cells != null)
                foreach (KeyValuePair<int, double[]> c in cells)
                {
                    if (c.Key < 0 || c.Key >= Actions || c.Value == null || c.Value.Length < 2) continue;
                    bucket.A[c.Key] = Math.Max(0, c.Value[0]);
                    bucket.B[c.Key] = Math.Max(0, c.Value[1]);
                }
            if (!bucket.IsEmpty) opp.Buckets[contextKey] = bucket;
            if (opp.Buckets.Count == 0) _opponents.Remove(opponentTag);
        }

        /// <summary>The opponent's entry, created (dropping the least recently fought past
        /// <see cref="MaxOpponents"/>) and marked as fought now.</summary>
        private Opponent Touch(string opponentTag, DateTime nowUtc)
        {
            Opponent opp;
            if (!_opponents.TryGetValue(opponentTag, out opp))
            {
                while (_opponents.Count >= MaxOpponents)
                {
                    string oldest = null;
                    DateTime oldestUtc = DateTime.MaxValue;
                    foreach (KeyValuePair<string, Opponent> kv in _opponents)
                        if (kv.Value.LastUtc < oldestUtc
                            || (kv.Value.LastUtc == oldestUtc && string.CompareOrdinal(kv.Key, oldest) < 0))
                        {
                            oldest = kv.Key;
                            oldestUtc = kv.Value.LastUtc;
                        }
                    _opponents.Remove(oldest);
                }
                opp = new Opponent();
                _opponents[opponentTag] = opp;
            }
            if (nowUtc > opp.LastUtc) opp.LastUtc = nowUtc;
            return opp;
        }

        // ---- the prior, exploration, decay -------------------------------------------------------

        /// <summary>The personality prior for cell <paramref name="index"/>: <see cref="PriorStrength"/>
        /// pseudo-fights at <see cref="PriorMean"/>, raised by <see cref="PriorArmBonus"/> when the
        /// arm is the personality's default and by <see cref="PriorBreakOffBonus"/> when the
        /// break-off is.</summary>
        public static void Prior(int index, DuelStance personalityDefault, out double alpha, out double beta)
        {
            double mean = PriorMean
                        + (ArmOf(index) == personalityDefault.Arm ? PriorArmBonus : 0)
                        + (BreakOffOf(index) == personalityDefault.BreakOff ? PriorBreakOffBonus : 0);
            alpha = PriorStrength * mean;
            beta = PriorStrength * (1.0 - mean);
        }

        /// <summary>How widely a personality samples: its softmax temperature relative to the default
        /// profile's, over its Caution weight, in [0.25, 3]. Every draw divides both Beta parameters
        /// by it, which keeps each cell's mean and scales its spread — above 1 explores, below 1
        /// exploits.</summary>
        public static double Exploration(string personalityId)
        {
            Personality p = Personalities.Get(personalityId);
            double caution = p.Weights[Drive.Caution];
            double e = (p.Temperature / 0.5) / (caution > 0 ? caution : 1.0);
            return Math.Max(0.25, Math.Min(3.0, e));
        }

        public static double DecayFactor(DateTime fromUtc, DateTime nowUtc)
        {
            double days = (nowUtc - fromUtc).TotalDays;
            if (days <= 0) return 1.0;
            return Math.Pow(0.5, days / HalfLifeDays);
        }

        private static void Decay(Bucket bucket, DateTime nowUtc)
        {
            double f = DecayFactor(bucket.Utc, nowUtc);
            if (f < 1.0)
                for (int i = 0; i < Actions; i++) { bucket.A[i] *= f; bucket.B[i] *= f; }
            if (nowUtc > bucket.Utc) bucket.Utc = nowUtc;
        }

        // ---- sampling ----------------------------------------------------------------------------

        /// <summary>A draw from Beta(<paramref name="alpha"/>, <paramref name="beta"/>).</summary>
        public static double BetaSample(IRandom rng, double alpha, double beta)
        {
            double x = GammaSample(rng, Math.Max(1e-6, alpha));
            double y = GammaSample(rng, Math.Max(1e-6, beta));
            double s = x + y;
            return s > 0 ? x / s : 0.5;
        }

        /// <summary>Marsaglia–Tsang, with the k &lt; 1 boost.</summary>
        private static double GammaSample(IRandom rng, double k)
        {
            if (k < 1.0)
            {
                double u = rng.NextDouble();
                return GammaSample(rng, k + 1.0) * Math.Pow(u > 0 ? u : double.Epsilon, 1.0 / k);
            }
            double d = k - 1.0 / 3.0, c = 1.0 / Math.Sqrt(9.0 * d);
            while (true)
            {
                double x, v;
                do { x = Normal(rng); v = 1.0 + c * x; } while (v <= 0);
                v = v * v * v;
                double u = rng.NextDouble();
                if (u < 1.0 - 0.0331 * x * x * x * x) return d * v;
                if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
            }
        }

        private static double Normal(IRandom rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>FNV-1a over the key, so the seed is the same on every runtime (string hash codes
        /// are randomised per process on .NET Core).</summary>
        public static int Seed(string selfTag, string opponentTag, string contextKey, long updates)
        {
            unchecked
            {
                uint h = 2166136261;
                string s = (selfTag ?? "") + "|" + (opponentTag ?? "") + "|" + (contextKey ?? "") + "|"
                         + updates.ToString(CultureInfo.InvariantCulture);
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return (int)h;
            }
        }

        private static double Share(double dealt, double taken)
        {
            dealt = Math.Max(0, dealt);
            taken = Math.Max(0, taken);
            double sum = dealt + taken;
            return sum > 0 ? dealt / sum : 0.5;
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        // ---- operator readout ----------------------------------------------------------------------

        /// <summary>The memory as an operator reads it (<c>factions bench tactics</c>): per opponent
        /// and context, every cell with evidence, best posterior mean first, each decayed to
        /// <paramref name="nowUtc"/>. <paramref name="opponentFilter"/> null lists every opponent.</summary>
        public string Describe(string selfTag, string personalityId, string opponentFilter, DateTime nowUtc)
        {
            DuelStance def = TacticChoice.DefaultFor(personalityId);
            var sb = new StringBuilder();
            sb.Append("tactics ").Append(selfTag ?? "?")
              .Append(" personality=").Append(personalityId ?? "default")
              .Append(" prior=").Append(def.Arm).Append('/').Append(def.BreakOff)
              .Append(" explore=").Append(Exploration(personalityId).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" opponents=").Append(_opponents.Count)
              .Append(" updates=").Append(Updates);
            int shown = 0;
            foreach (KeyValuePair<string, Opponent> opp in Opponents())
            {
                if (opponentFilter != null && !string.Equals(opp.Key, opponentFilter, StringComparison.OrdinalIgnoreCase)) continue;
                shown++;
                sb.Append("\n  vs ").Append(opp.Key).Append(" last=")
                  .Append(opp.Value.LastUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                var keys = new List<string>(opp.Value.Buckets.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    Bucket b = opp.Value.Buckets[key];
                    double f = DecayFactor(b.Utc, nowUtc);
                    var cells = new List<KeyValuePair<int, double>>();
                    for (int i = 0; i < Actions; i++)
                        if ((b.A[i] + b.B[i]) * f >= 0.05)
                        {
                            double a0, b0;
                            Prior(i, def, out a0, out b0);
                            cells.Add(new KeyValuePair<int, double>(i, (a0 + b.A[i] * f) / (a0 + b0 + (b.A[i] + b.B[i]) * f)));
                        }
                    cells.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value) : x.Key.CompareTo(y.Key));
                    sb.Append("\n    ctx=").Append(key);
                    foreach (KeyValuePair<int, double> c in cells)
                        sb.Append(' ').Append(ArmOf(c.Key)).Append('/').Append(BreakOffOf(c.Key))
                          .Append(" mean=").Append(c.Value.ToString("0.00", CultureInfo.InvariantCulture))
                          .Append(" n=").Append(((b.A[c.Key] + b.B[c.Key]) * f).ToString("0.0", CultureInfo.InvariantCulture))
                          .Append(';');
                }
            }
            if (opponentFilter != null && shown == 0) sb.Append("\n  no record against ").Append(opponentFilter);
            return sb.ToString();
        }

        // ---- log lines ---------------------------------------------------------------------------

        /// <summary>The decision-log <c>TACTIC_CHOSEN</c> line: one side's pick for one duel.
        /// <paramref name="fleet"/> is its fleet's log id, null for a station.</summary>
        public static string FormatChosen(string selfTag, string fleet, string opponentTag, TacticPick pick)
        {
            var sb = new StringBuilder("TACTIC_CHOSEN fleet=");
            sb.Append(fleet ?? "station")
              .Append(" faction=").Append(selfTag ?? "none")
              .Append(" vs=").Append(opponentTag ?? "none")
              .Append(" ctx=").Append(pick.Context)
              .Append(" arm=").Append(pick.Arm).Append(" breakoff=").Append(pick.BreakOff)
              .Append(" prior=").Append(pick.PriorArm).Append('/').Append(pick.PriorBreakOff)
              .Append(" basis=").Append(BasisName(pick.Basis))
              .Append(" sampled=").Append(pick.Sampled.ToString("0.00", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>The decision-log <c>TACTIC_REVIEW</c> line: what one outcome taught one side.</summary>
        public static string FormatReview(string selfTag, TacticReview r)
        {
            var sb = new StringBuilder("TACTIC_REVIEW faction=");
            sb.Append(selfTag ?? "none")
              .Append(" vs=").Append(r.OpponentTag ?? "none")
              .Append(" ctx=").Append(r.Context)
              .Append(" arm=").Append(r.Arm).Append(" breakoff=").Append(r.BreakOff)
              .Append(" predicted=").Append(double.IsNaN(r.Predicted) ? "none" : r.Predicted.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" actual=").Append(r.Actual.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" hulls_lost=").Append(r.HullsLost).Append('/').Append(r.HullsBrought)
              .Append(" reward=").Append(r.Reward.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" weight=").Append(r.Weight.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" mistake=").Append(r.Mistake ? 1 : 0)
              .Append(" commanded=").Append(r.Commanded ? 1 : 0)
              .Append(" mean=").Append(r.PosteriorMean.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" n=").Append(r.Evidence.ToString("0.0", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string BasisName(TacticBasis s)
        {
            switch (s)
            {
                case TacticBasis.Opponent: return "opponent";
                case TacticBasis.Aggregate: return "aggregate";
                default: return "prior";
            }
        }
    }
}
