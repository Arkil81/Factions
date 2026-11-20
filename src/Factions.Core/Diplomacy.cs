using System;
using System.Collections.Generic;
using System.Linq;

namespace Factions.Core
{
    /// <summary>
    /// Inter-faction relations dynamics (SE-free). Each economy quantum a faction's standing toward every other
    /// faction eases toward a target set by BORDER PRESSURE, read through traits: a rival pressing on our
    /// territory breeds rivalry (xenophobes/aggressors fastest; merchants barely notice and keep trading
    /// through it), while calm neighbours warm slightly for the mercantile. Raids (recorded as incidents)
    /// sour the victim's standing sharply, tipping a tense border into open war.
    ///
    /// War, however, no longer only spirals down. Sustained hostility accrues per-enemy <b>war fatigue</b>;
    /// a war-weary faction sues for peace (the <see cref="ActionKind.Negotiate"/> overture), and when the
    /// other side is also weary a <b>truce</b> concludes — relations reset to a wary peace, fatigue clears,
    /// and a non-aggression window blocks the spiral from instantly restarting. So a stalemate resolves into
    /// diplomacy, while a side that is losing economically still collapses first (conquest, the other outcome).
    ///
    /// Standing lives in <see cref="FactionState.Relations"/> (-1..1), fatigue in <see cref="FactionState.Fatigue"/>
    /// (0..1) — the brain reads both; this is the half that WRITES them.
    ///
    /// One personality opts out of the whole model: an <see cref="Traits.Implacable"/> faction holds every
    /// other faction at <see cref="ImplacableRelation"/> permanently — the standing is pinned rather than
    /// drifted, no truce concludes in either direction, a gift buys nothing, and no war fatigue accrues.
    /// </summary>
    public static class Diplomacy
    {
        /// <summary>The standing an <see cref="Traits.Implacable"/> faction holds toward everyone. The floor
        /// of the -1..1 scale rather than a tuning knob: the trait means "maximally hostile", and on this
        /// scale that is -1.</summary>
        public const double ImplacableRelation = -1.0;

        /// <summary>True if this faction's personality never softens toward anyone. Resolved from
        /// <see cref="FactionState.PersonalityId"/> for the call sites that hold a state but no profile.</summary>
        public static bool IsImplacable(FactionState s)
        {
            return s != null && Personalities.IsImplacable(s.PersonalityId);
        }

        /// <summary>True when this faction's own <see cref="FactionState.Relations"/> are set by a
        /// person and not by the sim: a player faction, whose stance toward every NPC is its
        /// leader's. Such a stance does not drift, is not moved by what the other side does to it
        /// (a raid, a trade, a gift received, an ally's defence), and war fatigue does not accept a
        /// truce on its behalf. A concluded truce still sets it: both sides agreed to that.
        /// See docs/diplomacy-plan.md, rule 3.</summary>
        public static bool HoldsLeaderStance(FactionState s)
        {
            return s != null && s.IsPlayerFaction;
        }

        /// <summary>How long an offer from <paramref name="from"/> to <paramref name="to"/> stays on
        /// the table. Between two NPCs: <see cref="FactionTuning.PeaceOfferTtl"/> for a truce or
        /// non-aggression, <see cref="FactionTuning.PactOfferTtl"/> for an alliance, as before. When
        /// either side is a player faction: <see cref="FactionTuning.PlayerOfferTtl"/> for every kind,
        /// because a person has to be online, read it and answer.
        ///
        /// <para><b>Real time on the host.</b> The TTL is compared against
        /// <see cref="IWorldView.UtcNow"/>, which the host sets from <c>DateTime.UtcNow</c> every frame
        /// and every <c>Step</c>; only the offline sim runs it as simulated time. Offers are persisted
        /// in the brain file, so a restart inside the window does not end it.</para>
        ///
        /// <para>A <paramref name="to"/> of null (a partner with no state) is judged by the sender
        /// alone. <see cref="FactionTuning.PlayerOfferTtl"/> at or below zero switches the player
        /// window off and restores the NPC TTLs for every pair.</para></summary>
        public static TimeSpan OfferTtl(FactionState from, FactionState to, bool alliance, FactionTuning t)
        {
            if (t == null) return TimeSpan.Zero;
            bool player = (from != null && from.IsPlayerFaction) || (to != null && to.IsPlayerFaction);
            if (player && t.PlayerOfferTtl > TimeSpan.Zero) return t.PlayerOfferTtl;
            return alliance ? t.PactOfferTtl : t.PeaceOfferTtl;
        }

        /// <summary>True while <paramref name="msg"/>, sent by <paramref name="from"/> to
        /// <paramref name="to"/>, is not older than <see cref="OfferTtl"/>. The one expiry rule:
        /// ProcessProposals' prune and freshness checks, the vanilla peace-flag reconcile and the
        /// player readout all read it. An offer exactly at the TTL is still open.</summary>
        public static bool OfferOpen(FactionState from, FactionState to, DiplomacyMessage msg, DateTime nowUtc, FactionTuning t)
        {
            if (msg == null) return false;
            return nowUtc - msg.IssuedUtc <= OfferTtl(from, to, HasVerb(msg, DiplomacyVerb.Alliance), t);
        }

        /// <summary>True when goods or credits may not cross between <paramref name="a"/> and
        /// <paramref name="b"/>, in either direction. Symmetric: either side's standing toward the other
        /// below <see cref="FactionTuning.HostileRelationThreshold"/>, or either side holding the other
        /// in its <see cref="FactionState.Embargoes"/>, bars the pair.
        ///
        /// <para>This is the second key. The initiator's own <see cref="FactionState.TradePriority"/>
        /// is the first, and it reads only the initiator's view, so a partner that held the initiator
        /// hostile or embargoed was traded with anyway. Checked where a trade vector forms and at both
        /// settlements, so a convoy already in flight when the bar starts settles nothing.
        /// See docs/diplomacy-plan.md, rule 1.</para>
        ///
        /// <para>Null-safe: a missing state is not barred (a node owned by a tag with no state, or a
        /// fixture that omits one). A null tuning skips the relation half.</para></summary>
        public static bool TradeBarred(FactionState a, FactionState b, FactionTuning t)
        {
            if (a == null || b == null) return false;
            if (a.Tag != null && b.Embargoes.Contains(a.Tag)) return true;
            if (b.Tag != null && a.Embargoes.Contains(b.Tag)) return true;
            if (t == null || a.Tag == null || b.Tag == null) return false;
            return a.RelationTo(b.Tag) < t.HostileRelationThreshold
                || b.RelationTo(a.Tag) < t.HostileRelationThreshold;
        }

        /// <summary>Drift one faction's standings toward all other present factions by current border pressure.
        /// Pairs under an active truce are held at their settled peace (skipped) so the non-aggression sticks.</summary>
        public static void Drift(FactionState self, Personality p, IWorldView world, FactionTuning t)
        {
            if (world == null || world.Influence == null) return;

            // A player faction's own stance is its leader's (docs/diplomacy-plan.md, rule 3). It
            // moves only by a leader act (PlayerDiplomacy) or a concluded truce. Existing values are
            // kept as they are, not reset. What an NPC thinks OF the player is that NPC's own drift,
            // which runs with the NPC as `self` and is untouched.
            if (HoldsLeaderStance(self)) return;

            // The one link between the two levels of the reputation model: what this faction's
            // members have personally done is SENSED here, beside border pressure, and never copied
            // into the stored relation. Absent for every NPC-vs-NPC pair, which is what makes that
            // half of the drift provably unchanged.
            IMemberStandingSource standing = world.MemberStanding ?? NullMemberStandingSource.Instance;

            var others = new HashSet<string>();
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n.OwnerFactionTag != null && n.OwnerFactionTag != self.Tag && n.OwnerFactionTag != NeutralFaction.Tag)
                    others.Add(n.OwnerFactionTag);

            // A faction with no territory has nothing to have border relations ABOUT. Pressure() loops
            // over nodes owned by self, so with none it returns 0 for every rival, and the target
            // below collapses to pure Mercantilism warmth -- a landless faction warming toward
            // everyone purely for owning nothing (up to +0.195 for a mercantile personality). Holding
            // the existing standings is the honest answer. FAC-188 item 6; not player-specific, a
            // just-force-shed NPC reaches it by the same route.
            bool landless = true;
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n.OwnerFactionTag == self.Tag) { landless = false; break; }

            bool implacable = p.Traits.Implacable;

            foreach (string r in others)
            {
                // Pinned, not drifted: this faction's standing is not a function of anything the other
                // side does, so neither border pressure nor the earned ledger nor a truce gets a vote.
                // The ledger underneath is still written by the acts themselves and left honest — if the
                // trait is ever taken off a faction, its record is there to fall back to.
                if (implacable) { self.Relations[r] = ImplacableRelation; continue; }

                if (UnderTruce(self, r, world.UtcNow)) continue;                            // a truce holds the standing

                // Where the other party has no traits to predict it by, its record IS the personality
                // slot on that side: read traits where traits exist, history where they do not. The
                // standing source answers only for an actor of that kind, so this needs no species
                // check (docs/player-progression.md, seam invariant 2).
                double aggregate;
                if (standing.TryGetAggregate(self.Tag, r, out aggregate))
                {
                    self.Relations[r] = EarnedStanding(self, r, aggregate, p, t);
                    continue;
                }

                if (landless) continue;

                // What they have DONE to each other, plus a bounded read of where they currently stand.
                // Conduct is the substance; border tension is a bias on it. Before FAC-249 the target
                // was the bias ALONE, which made the standing a pure function of present geometry:
                // recomputing it every pass erased every act within about a minute, so a raider who
                // moved away was forgiven and a thousand convoys counted for nothing.
                double pressure = world.Influence.Pressure(self.Tag, r);                  // 0..1: r presses on us
                double hostile = pressure * (0.2 + 0.8 * p.Traits.Xenophobia + 0.5 * p.Traits.Aggression);
                double friendly = (1.0 - pressure) * p.Traits.Mercantilism * 0.3;          // calm neighbours warm merchants
                double cap = t.MaxBorderPressureStandingBias;
                if (cap < 0) cap = 0;
                double bias = MathUtil.Clamp(friendly - hostile, -cap, cap);
                // EarnedLedgerTo, never EarnedRelationTo: the latter falls back to the current standing
                // when no entry exists, and a target built on that reads back what it just wrote and
                // ratchets by `bias` every pass. See its own doc comment.
                double target = MathUtil.Clamp(self.EarnedLedgerTo(r) + bias, -1.0, 1.0);
                double cur = self.RelationTo(r);
                self.Relations[r] = MathUtil.Clamp(cur + (target - cur) * t.RelationDriftRate, -1.0, 1.0);
            }
            others.Clear();
        }

        /// <summary>
        /// What an actor with no traits of its own has <b>earned</b> from this faction: the ledger of
        /// acts between them, plus what its members have personally earned, weighed by this faction's
        /// own personality.
        ///
        /// <para><b>Earned, not chased — this does not ease toward a target.</b> An earlier cut made
        /// the member aggregate a drift target blended with border pressure, which made the whole
        /// relationship a function of <i>current</i> state: a raider who withdrew had the target
        /// recomputed from present pressure and climbed back to a warm number on their own, and five
        /// raids ended in the same place as one about ten quanta later (FAC-189). Border pressure is
        /// instantaneous; conduct is a record. They cannot share a target.</para>
        ///
        /// <para><b>Traits keep a real job, on the side that has them.</b> A xenophobe weighs a
        /// trespass harder and a merchant weighs goodwill harder, so the same conduct reads
        /// differently to different factions without the conduct itself changing.</para>
        /// </summary>
        private static double EarnedStanding(FactionState self, string otherTag, double aggregate,
                                             Personality p, FactionTuning t)
        {
            double w = MathUtil.Clamp01(t.MemberStandingDriftWeight);
            double earned = self.EarnedRelationTo(otherTag)
                          + MathUtil.Clamp(aggregate, -1.0, 1.0) * w;

            // 0.5..1.5 either side of neutral. Reading the sign of the earned value rather than of
            // the aggregate alone is deliberate: it is the standing as a whole this faction is
            // reacting to, not just the part its members contributed.
            double lean = earned < 0.0
                ? 0.5 + MathUtil.Clamp01(p.Traits.Xenophobia)
                : 0.5 + MathUtil.Clamp01(p.Traits.Mercantilism);
            return MathUtil.Clamp(earned * lean, -1.0, 1.0);
        }

        /// <summary>War fatigue toward each rival, <b>derived</b> from what the war has actually cost
        /// this faction rather than accrued per quantum: the combat power it has lost to that rival
        /// inside <see cref="FactionTuning.WarFatigueActWindowSeconds"/>, against that loss plus the
        /// fleet it can still field. It cools the Rivalry drive and, past
        /// <see cref="FactionTuning.PeaceThreshold"/>, makes a faction sue for peace.
        ///
        /// <para><b>A ratio of two real quantities, so it cannot ratchet.</b> A per-quantum accrual
        /// climbs while the pair keeps fighting and decays only while it does not, which makes the
        /// number a measure of how long a war has run. Under a player who attacks continuously that
        /// only ever goes up, and it went up fastest on the side that was losing: at the clamp,
        /// exhaustion switched off retaliation and conquest entirely, so the faction being conquered
        /// was the one forbidden to answer. Here the denominator is the fleet — a faction that can
        /// replace what it lost reads low however long it has been at war, one ground down to nothing
        /// approaches 1.0, and rebuilding is what releases it. That is the arc fatigue was introduced
        /// for, keyed to a quantity the world already carries.</para>
        ///
        /// <para>Losses come from the attacking side of <see cref="FactionState.Fights"/>, which is
        /// where hulls are recorded lost (<see cref="FightRecord.AttackerHullsLost"/>). A faction that
        /// only ever defends therefore tires from nothing, which is correct: it has spent nothing.</para>
        ///
        /// <para>Truce, an absent rival and an implacable faction all read zero, as before — the
        /// reasons are unchanged, only the thing being suppressed is now a derivation rather than a
        /// running total.</para></summary>
        public static void AccrueFatigue(FactionState self, IWorldView world, FactionTuning t)
        {
            if (world == null) return;

            var present = new HashSet<string>();
            foreach (SpatialNode n in world.Graph.Nodes.Values)
                if (n.OwnerFactionTag != null && n.OwnerFactionTag != self.Tag && n.OwnerFactionTag != NeutralFaction.Tag)
                    present.Add(n.OwnerFactionTag);

            bool implacable = IsImplacable(self);

            // What is still ours to lose. Zero is a real answer -- a faction with no fleet left is as
            // spent as it can be -- so the ratio is guarded by the loss term, not by this.
            double fieldable = world.GetFleetCombatPower(self.Tag);

            // The same knob FightingRecently reads, and it has to mean the same thing on both
            // halves: at or below zero the recency gate is off, so every recorded loss counts rather
            // than a window of nothing. Otherwise disabling the gate would say "always at war" and
            // "nothing was ever lost" in the same breath.
            DateTime since = DateTime.MinValue;
            if (t.WarFatigueActWindowSeconds > 0)
            {
                double clockSeconds = world.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
                since = world.UtcNow.AddSeconds(-Math.Min(t.WarFatigueActWindowSeconds, clockSeconds));
            }

            // union of "fighting them now" and "already tired of them" so a vanished enemy's fatigue still fades
            var tags = new HashSet<string>(self.Fatigue.Keys);
            foreach (string r in present) tags.Add(r);

            foreach (string r in tags)
            {
                bool isPresent = present.Contains(r);
                bool truce = UnderTruce(self, r, world.UtcNow);
                double rel = self.RelationTo(r);

                double next = 0;
                if (isPresent && !truce && !implacable && rel < t.HostileRelationThreshold
                    && FightingRecently(self, r, world.UtcNow, t))
                {
                    double lost = LostCombatPowerTo(self, r, since);
                    if (lost > 0) next = MathUtil.Clamp01(lost / (lost + Math.Max(0.0, fieldable)));
                }

                if (next <= 0) self.Fatigue.Remove(r); else self.Fatigue[r] = next;
            }
            present.Clear();
        }

        /// <summary>Combat power this faction spent attacking <paramref name="otherTag"/> and did not
        /// get back, since <paramref name="sinceUtc"/>. A fleet's own summed Dps is its effective HP
        /// in the duel (<c>PendingFleetResolver.ResolveShipDuel</c>), so the Dps of a convoy that came
        /// home in a body bag is exactly what the war took — one currency for the loss and for the
        /// fleet it is measured against.</summary>
        private static double LostCombatPowerTo(FactionState self, string otherTag, DateTime sinceUtc)
        {
            if (self == null) return 0;
            double lost = 0;
            foreach (FightRecord f in self.Fights)
                if (f.Attacking && f.AttackerHullsLost > 0 && f.Utc >= sinceUtc
                    && string.Equals(f.OpponentTag, otherTag, StringComparison.Ordinal))
                    lost += f.AttackerDps;
            return lost;
        }

        /// <summary>Has this pair actually exchanged blows recently enough to still be at war?
        ///
        /// <para>A sour standing is not a war. Before FAC-253 it was the whole test, and the result
        /// was measurable: at 2026-09-04T11:10Z <b>every one of the five saturated fatigue entries
        /// sat on a pair that had never exchanged a raid</b> in the preceding 79 hours, while SPRT —
        /// which committed 74 of the 88 resolutions in that window — peaked at 0.64 and had none.
        /// War-weariness was anti-correlated with fighting a war.</para>
        ///
        /// <para>A window of 0 or less disables the gate, restoring the standing-only behaviour.</para></summary>
        private static bool FightingRecently(FactionState self, string otherTag, DateTime nowUtc, FactionTuning t)
        {
            if (t.WarFatigueActWindowSeconds <= 0) return true;
            DateTime last;
            if (!self.LastHostileActUtc.TryGetValue(otherTag ?? "", out last)) return false;
            return (nowUtc - last).TotalSeconds <= t.WarFatigueActWindowSeconds;
        }

        /// <summary>A raid sharply sours the victim's standing toward the attacker (toward war).</summary>
        public static void HitRelation(FactionState victim, string attackerTag, FactionTuning t)
        {
            HitRelation(victim, attackerTag, t.RaidRelationHit);
        }

        /// <summary>Sour a faction's standing toward an attacker by an explicit amount (shared by
        /// Raid and the harsher Conquer penalty).</summary>
        public static void HitRelation(FactionState victim, string attackerTag, double amount)
        {
            ApplyAct(victim, attackerTag, -amount);
        }

        /// <summary>A trade lifts the initiator's standing toward the partner by a small positive step.
        /// The effect is <b>unilateral</b>: only the initiator warms (the partner warms independently if
        /// they also choose Trade). This is ~1/10 of <see cref="FactionTuning.RaidRelationHit"/>, so
        /// negative actions are remembered ~10× longer than positive ones — scars last.</summary>
        public static void LiftRelation(FactionState initiator, string partnerTag, FactionTuning t)
        {
            LiftRelation(initiator, partnerTag, t.TradeRelationBoost);
        }

        /// <summary>Lift a faction's standing toward a partner by an explicit amount (shared by Trade
        /// recipient warmth and Gift resolution).</summary>
        public static void LiftRelation(FactionState initiator, string partnerTag, double amount)
        {
            if (partnerTag == null || amount <= 0) return;
            // The one place goodwill is refused rather than merely outweighed: no trade and no gift moves
            // an implacable faction, and the ledger stays unmoved too so nothing accrues to cash in later.
            if (IsImplacable(initiator)) return;
            ApplyAct(initiator, partnerTag, amount);
        }

        /// <summary>Moves the standing and records the act on the ledger behind it, in one place.
        ///
        /// <para><b>Both reads are taken before either write.</b> <see cref="FactionState.EarnedRelationTo"/>
        /// falls back to the current standing when no ledger entry exists yet, so updating
        /// <see cref="FactionState.Relations"/> first would make the ledger read the value this same
        /// act had already applied and count it twice.</para>
        ///
        /// <para>Written for <b>every</b> pair, not only the ones that read it. The writers are the
        /// acts themselves — Raid, Conquer, Trade, Gift — and none of them has any business asking
        /// what kind of actor it is dealing with; that is the seam invariant the whole model is built
        /// to keep. <see cref="Drift"/> decides where the ledger is consulted.</para>
        ///
        /// <para><b>A leader-held stance records the act and does not move.</b> For a player faction
        /// (<see cref="HoldsLeaderStance"/>) the act still goes on the ledger, so the record stays
        /// honest, but <see cref="FactionState.Relations"/> is the leader's: being raided, trading,
        /// receiving a gift or being defended by an ally does not change what the leader has
        /// declared. Whether to answer a raid with war is the leader's decision.</para></summary>
        private static void ApplyAct(FactionState self, string otherTag, double delta)
        {
            if (self == null || string.IsNullOrEmpty(otherTag)) return;
            double standing = self.RelationTo(otherTag);
            double earned = self.EarnedRelationTo(otherTag);
            if (!HoldsLeaderStance(self))
                self.Relations[otherTag] = MathUtil.Clamp(standing + delta, -1.0, 1.0);
            self.EarnedRelations[otherTag] = MathUtil.Clamp(earned + delta, -1.0, 1.0);
        }

        /// <summary>A gift/reparation convoy warms the RECIPIENT's view of the sender — the inverse of
        /// the normal Trade lift. The sender pays <see cref="FactionTuning.GiftCost"/> with no direct
        /// relation benefit to themselves; the payoff is purely diplomatic (reducing the recipient's
        /// hostility). Only Security/Caution personalities generate Gift actions; high-aggression
        /// factions never spend on it.</summary>
        public static void GiftRelation(FactionState recipient, string senderTag, FactionTuning t)
        {
            LiftRelation(recipient, senderTag, t.GiftRelationBoost);
        }

        /// <summary>
        /// A declaration of war, from an actor who does not need anyone's agreement to make one.
        ///
        /// <para><b>War is unilateral; peace is bilateral.</b> You cannot force anyone to like you,
        /// but you can always be their enemy — so this lands immediately and completely, while the
        /// other direction is only ever a proposal (<see cref="OfferPeace"/>) that concludes through
        /// <see cref="ConcludeTruces"/> once the other side is genuinely war-weary.</para>
        ///
        /// <para><b>It never raises a standing.</b> Declaring war on a faction that already hates you
        /// more than this cannot improve their opinion — that would make the button a laundering
        /// path, which is the whole failure the earned ledger exists to prevent.</para>
        /// </summary>
        public static void DeclareWar(FactionState victim, string aggressorTag, FactionTuning t, DateTime nowUtc)
        {
            if (victim == null || string.IsNullOrEmpty(aggressorTag)) return;

            // Below the threshold, not merely at it: at exactly the threshold the hostile predicates
            // read `< HostileRelationThreshold` and would not fire, leaving a declared war that no
            // brain treats as one.
            double target = MathUtil.Clamp(t.HostileRelationThreshold - Math.Abs(t.RaidRelationHit), -1.0, 1.0);
            double earned = victim.EarnedRelationTo(aggressorTag);
            if (earned > target) ApplyAct(victim, aggressorTag, target - earned);

            victim.TruceUntil.Remove(aggressorTag);      // a truce cannot survive its own breach
            victim.Proposals.Remove(aggressorTag);       // and neither can an outstanding peace offer
            HitFatigue(victim, aggressorTag, t.RaidFatigue, nowUtc);
        }

        /// <summary>A raid is exhausting for BOTH parties — bump fatigue toward the other.</summary>
        public static void HitFatigue(FactionState self, string otherTag, FactionTuning t, DateTime nowUtc)
        {
            HitFatigue(self, otherTag, t.RaidFatigue, nowUtc);
        }

        /// <summary>Bump fatigue toward a partner by an explicit amount (shared by Raid and the
        /// harsher Conquer penalty), and <b>stamp the blow</b>.
        ///
        /// <para>This is the single funnel every raid and conquer resolution already passes through,
        /// for both sides, which is why the stamp lives here rather than at the call sites: a new
        /// conflict path gets it by construction instead of by remembering. The clock is a parameter
        /// and not <c>DateTime.UtcNow</c> so the sim and the tests stay deterministic.</para></summary>
        public static void HitFatigue(FactionState self, string otherTag, double amount, DateTime nowUtc)
        {
            self.Fatigue[otherTag] = MathUtil.Clamp01(self.FatigueTo(otherTag) + amount);
            self.LastHostileActUtc[otherTag] = nowUtc;
        }

        /// <summary>Record a peace overture toward an enemy (the enacted <see cref="ActionKind.Negotiate"/>).
        /// Convenience wrapper: posts a <see cref="DiplomacyVerb.Truce"/> proposal via <see cref="Propose"/>.</summary>
        public static void OfferPeace(FactionState self, string enemyTag, DateTime nowUtc)
        {
            Propose(self, enemyTag, new[] { new DiplomacyTerm { Verb = DiplomacyVerb.Truce } }, nowUtc);
        }

        /// <summary>Post a structured diplomatic message to a partner faction. The message is stored in
        /// <see cref="FactionState.Proposals"/> and processed by <see cref="ProcessProposals"/> on the next
        /// global pass. Overwrites any prior proposal to the same partner.</summary>
        public static void Propose(FactionState self, string partnerTag, DiplomacyTerm[] terms, DateTime nowUtc)
        {
            if (partnerTag == null || terms == null || terms.Length == 0) return;
            self.Proposals[partnerTag] = new DiplomacyMessage { Terms = terms, IssuedUtc = nowUtc };
        }

        /// <summary>Record a truce-breach grudge on the victim against the attacker.
        /// Called when a raid fires while the actor's TruceUntil window with the target is still open.</summary>
        public static void RecordTruceBreachGrudge(FactionState victim, string attackerTag, DateTime now, FactionTuning t)
        {
            if (attackerTag == null || t.GrudgeTtl <= TimeSpan.Zero) return;
            victim.Grudges[attackerTag] = new GrudgeRecord { Kind = GrudgeKind.TruceBreached, ExpiresUtc = now + t.GrudgeTtl };
        }

        /// <summary>Remove grudge entries whose TTL has expired. Intended to be called once per Think cycle.</summary>
        public static void PruneStaleGrudges(FactionState self, DateTime now)
        {
            if (self.Grudges.Count == 0) return;
            var expired = new List<string>();
            foreach (var kv in self.Grudges)
                if (now >= kv.Value.ExpiresUtc) expired.Add(kv.Key);
            foreach (string tag in expired) self.Grudges.Remove(tag);
        }

        /// <summary>True while a concluded truce's non-aggression window is still open.</summary>
        public static bool UnderTruce(FactionState self, string otherTag, DateTime nowUtc)
        {
            DateTime until;
            return self.TruceUntil.TryGetValue(otherTag, out until) && nowUtc < until;
        }

        /// <summary>True if this faction has an active pact with the given partner.</summary>
        public static bool InPactWith(FactionState self, string otherTag)
        {
            return self.PactWith.Contains(otherTag);
        }

        /// <summary>
        /// Drains <see cref="WorldModel.Incidents"/> into standings and war fatigue: the victim's
        /// standing toward the attacker sours, and BOTH sides tire of the war. The incident list is
        /// cleared afterwards, so a caller runs this exactly once per pass.
        ///
        /// <para>The one implementation of the drain. The host and the offline sim each held their
        /// own copy of this loop, which is how a rule could hold on a live server and not in the
        /// sim; they now differ only in where their log lines go.</para>
        ///
        /// <para><paramref name="log"/> receives one line per incident, already formatted, and may
        /// be null for a caller that keeps no decision log. A repelled attack is logged as
        /// <c>ATTACK_REPELLED</c> and never as <c>RAID</c>/<c>CONQUER</c>: those two count the
        /// attacks that actually landed, and an attempt must not be readable as one.</para>
        ///
        /// <para>Returns the number of incidents applied. Provenance: FAC-275 slice 1.</para>
        /// </summary>
        public static int ApplyIncidents(IEnumerable<FactionState> roster, WorldModel world, FactionTuning t,
                                         Action<string> log = null)
        {
            if (world == null || world.Incidents.Count == 0) return 0;

            var byTag = new Dictionary<string, FactionState>();
            foreach (FactionState s in roster) if (s != null && s.Tag != null) byTag[s.Tag] = s;

            int applied = 0;
            foreach (RaidIncident inc in world.Incidents)
            {
                bool isConquer = inc.Kind == ActionKind.Conquer;
                double relHit = isConquer ? t.ConquerRelationHit : t.RaidRelationHit;
                double fatHit = isConquer ? t.ConquerFatigue     : t.RaidFatigue;

                FactionState victim;
                if (inc.Victim != null && byTag.TryGetValue(inc.Victim, out victim))
                {
                    HitRelation(victim, inc.Attacker, relHit);
                    HitFatigue(victim, inc.Attacker, fatHit, world.UtcNow);   // war is tiring for the victim
                }
                FactionState attacker;
                if (inc.Attacker != null && byTag.TryGetValue(inc.Attacker, out attacker))
                    HitFatigue(attacker, inc.Victim, fatHit, world.UtcNow);   // ...and for the attacker

                if (log != null)
                    log(Verb(inc) + " " + inc.Attacker + " -> " + inc.Victim + " @ " + inc.NodeId);
                applied++;
            }
            world.ClearIncidents();
            byTag.Clear();
            return applied;
        }

        /// <summary>The leading verb of an incident's log line. Kept beside
        /// <see cref="ApplyIncidents"/> because the three are read as one set: anything counting
        /// landed attacks greps <c>RAID</c>/<c>CONQUER</c> and must not match an attempt.</summary>
        private static string Verb(RaidIncident inc)
        {
            if (inc.Repelled) return "ATTACK_REPELLED";
            return inc.Kind == ActionKind.Conquer ? "CONQUER" : "RAID";
        }

        /// <summary>Resolve all pending proposals for the roster. Runs once per global pass, after actions.
        /// <list type="bullet">
        /// <item><see cref="DiplomacyVerb.Truce"/> / <see cref="DiplomacyVerb.NonAggression"/>: bilateral
        /// offer OR fatigued-enough partner → <see cref="FormTruce"/>.</item>
        /// <item><see cref="DiplomacyVerb.Alliance"/>: strictly bilateral → <see cref="FormPact"/>.</item>
        /// <item><see cref="DiplomacyVerb.SuggestWar"/>: unilateral; stored for future brain read; no
        /// automatic resolution here.</item>
        /// </list>
        /// Stale proposals (older than their respective TTL) are pruned.</summary>
        public static void ProcessProposals(IEnumerable<FactionState> roster, IWorldView world, FactionTuning t,
                                            Action<string, string> onTruce = null,
                                            Action<string, string> onPact  = null)
        {
            DateTime now = world.UtcNow;
            var byTag = new Dictionary<string, FactionState>();
            foreach (FactionState s in roster) byTag[s.Tag] = s;

            foreach (FactionState a in byTag.Values)
            {
                foreach (KeyValuePair<string, DiplomacyMessage> entry in a.Proposals.ToList())
                {
                    string bTag = entry.Key;
                    DiplomacyMessage msg = entry.Value;

                    // Prune by TTL (OfferTtl): the NPC TTLs between two NPCs, the player window when
                    // either side is a player faction. The partner is looked up first for that reason;
                    // a partner with no state is judged by the sender alone.
                    bool isAlliance = HasVerb(msg, DiplomacyVerb.Alliance);
                    FactionState b;
                    byTag.TryGetValue(bTag, out b);
                    if (!OfferOpen(a, b, msg, now, t)) { a.Proposals.Remove(bTag); continue; }

                    // Skip if already at peace / already allied.
                    if (!isAlliance && UnderTruce(a, bTag, now)) { a.Proposals.Remove(bTag); continue; }
                    if (isAlliance && InPactWith(a, bTag))        { a.Proposals.Remove(bTag); continue; }

                    if (b == null) continue;

                    // Neither side of an implacable pair can conclude anything — truce or alliance. Drop the
                    // proposal instead of letting it age out, so the other party is not left waiting on an
                    // answer that cannot come.
                    if (IsImplacable(a) || IsImplacable(b)) { a.Proposals.Remove(bTag); continue; }

                    if (isAlliance)
                    {
                        // Alliance requires the other side to also have sent an Alliance proposal.
                        DiplomacyMessage counter;
                        bool bProposed = b.Proposals.TryGetValue(a.Tag, out counter) &&
                                         HasVerb(counter, DiplomacyVerb.Alliance) &&
                                         OfferOpen(b, a, counter, now, t);
                        if (bProposed) FormPact(a, b, onPact);
                    }
                    else
                    {
                        // Truce / NonAggression: accept if B has its own fresh peace proposal OR is fatigued.
                        // Fatigue never accepts for a player faction: its peace is its leader's, given
                        // by the leader's own offer (docs/diplomacy-plan.md, rule 3).
                        bool bWilling = (ProposalFresh(b, a, now, t) &&
                                         !HasVerb(b.Proposals[a.Tag], DiplomacyVerb.Alliance))
                                        || (!HoldsLeaderStance(b) && b.FatigueTo(a.Tag) >= t.AcceptPeaceThreshold);
                        if (bWilling) FormTruce(a, b, now, t, onTruce);
                    }
                }
            }
        }

        /// <summary>Legacy alias kept so call sites that haven't been updated yet still compile.</summary>
        public static void ConcludeTruces(IEnumerable<FactionState> roster, IWorldView world, FactionTuning t,
                                          Action<string, string> onTruce = null)
        {
            ProcessProposals(roster, world, t, onTruce);
        }

        private static bool HasVerb(DiplomacyMessage msg, DiplomacyVerb verb)
        {
            if (msg == null || msg.Terms == null) return false;
            foreach (DiplomacyTerm term in msg.Terms)
                if (term.Verb == verb) return true;
            return false;
        }

        /// <summary>True while <paramref name="s"/> has an open offer to <paramref name="to"/>, on
        /// <see cref="OfferTtl"/>. Before the player window this read <see cref="FactionTuning.PeaceOfferTtl"/>
        /// for every message kind; an alliance counter is excluded by its caller either way.</summary>
        private static bool ProposalFresh(FactionState s, FactionState to, DateTime now, FactionTuning t)
        {
            DiplomacyMessage msg;
            return s.Proposals.TryGetValue(to.Tag, out msg) && OfferOpen(s, to, msg, now, t);
        }

        private static void FormTruce(FactionState a, FactionState b, DateTime now, FactionTuning t,
                                      Action<string, string> onTruce)
        {
            DateTime until = now + t.TruceDuration;
            // The ledger too: concluding a truce IS an act, and a settled peace that only reached
            // Relations would be rebuilt away from the pre-truce ledger on the next drift.
            a.Relations[b.Tag] = t.TruceRelation; b.Relations[a.Tag] = t.TruceRelation;
            a.EarnedRelations[b.Tag] = t.TruceRelation; b.EarnedRelations[a.Tag] = t.TruceRelation;
            a.Fatigue.Remove(b.Tag); b.Fatigue.Remove(a.Tag);
            a.TruceUntil[b.Tag] = until; b.TruceUntil[a.Tag] = until;
            a.Proposals.Remove(b.Tag); b.Proposals.Remove(a.Tag);
            CancelAttackFleets(a, b.Tag);
            CancelAttackFleets(b, a.Tag);
            if (onTruce != null) onTruce(a.Tag, b.Tag);
        }

        /// <summary>Remove in-flight Raid, Retaliate, or Conquer fleets directed at the new truce partner.
        /// Called when a truce forms so that pre-ceasefire attack orders don't arrive after the peace.</summary>
        private static void CancelAttackFleets(FactionState faction, string partnerTag)
        {
            for (int i = faction.PendingFleets.Count - 1; i >= 0; i--)
            {
                PendingFleetAction f = faction.PendingFleets[i];
                if ((f.Kind == ActionKind.Raid || f.Kind == ActionKind.Retaliate || f.Kind == ActionKind.Conquer) && f.TargetTag == partnerTag)
                    faction.PendingFleets.RemoveAt(i);
            }
        }

        private static void FormPact(FactionState a, FactionState b, Action<string, string> onPact)
        {
            a.PactWith.Add(b.Tag); b.PactWith.Add(a.Tag);
            a.Proposals.Remove(b.Tag); b.Proposals.Remove(a.Tag);
            if (onPact != null) onPact(a.Tag, b.Tag);
        }
    }
}
