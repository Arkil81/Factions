using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static Factions.Core.MathUtil;

namespace Factions.Core
{
    /// <summary>
    /// The pure decision engine. No SE/Torch/IO: given a faction's state, its personality, and a
    /// read-only world view, it returns a Decision (intent + explanation). Selection is
    /// non-deterministic — softmax over the top-N candidate utilities, sampled with a seeded RNG —
    /// so factions feel adaptive, not scripted, while remaining reproducible for balancing.
    /// </summary>
    public sealed class FactionBrain
    {
        private const double ActionRepetitionDecay = 0.90;
        private const double ActionRepetitionPenaltyScale = 0.35;
        private const double ActionRepetitionPruneBelow = 0.01;

        /// <summary>Per-stage timings for the most recent <see cref="Think"/> call, in Stopwatch
        /// ticks. Diagnostic only -- the host aggregates these into perf.json so an outlier tick can
        /// be attributed to a stage instead of to Think as a whole.</summary>
        public BrainThinkTelemetry LastThink { get { return _lastThink; } }
        private readonly BrainThinkTelemetry _lastThink = new BrainThinkTelemetry();

        public Decision Think(FactionState self, Personality p, IWorldView world, IRandom rng, FactionTuning t)
        {
            long t0 = Stopwatch.GetTimestamp();
            long netFlow = world.GetNetFlow(self.Tag);
            Sense(self, p, world, t, netFlow);
            long tSense = Stopwatch.GetTimestamp();
            ComputeTradePriority(self, world, t, true);   // TradeManager reads this; brain no longer dispatches Trade
            long tTrade = Stopwatch.GetTimestamp();
            ComputeReinforcePriority(self, world, t); // FleetManager reads this; brain ranks owned nodes once per Think
            ComputeCampaignCandidates(self, world, t); // ...and this: the nodes each offensive objective is worth attacking
            long tReinforce = Stopwatch.GetTimestamp();
            ComputePerceivedThreatBonus(self, p, world); // FleetManager reads this to bias in-region Patrol scoring
            long tThreat = Stopwatch.GetTimestamp();
            Drive dominantDrive = DominantDrive(self, p);

            List<Candidate> candidates = Generate(self, p, world, t);
            long tGenerate = Stopwatch.GetTimestamp();
            Score(candidates, self, p, world, t);
            long tScore = Stopwatch.GetTimestamp();
            Candidate chosen = Select(candidates, p.Temperature, t.TopN, rng);
            RecordActionRepetition(self, chosen.Intent.Kind);
            long tSelect = Stopwatch.GetTimestamp();

            _lastThink.SenseTicks             = tSense - t0;
            _lastThink.TradePriorityTicks     = tTrade - tSense;
            _lastThink.ReinforcePriorityTicks = tReinforce - tTrade;
            _lastThink.ThreatBonusTicks       = tThreat - tReinforce;
            _lastThink.GenerateTicks          = tGenerate - tThreat;
            _lastThink.ScoreTicks             = tScore - tGenerate;
            _lastThink.SelectTicks            = tSelect - tScore;
            _lastThink.Candidates             = candidates.Count;

            return new Decision { Intent = chosen.Intent, Goal = chosen.SourceDrive, Scored = candidates, Explanation = Explain(dominantDrive, self, chosen) };
        }

        // ---- sense: the SAME world is read into drives differently depending on traits ----------
        private static void Sense(FactionState s, Personality p, IWorldView world, FactionTuning t, long netFlow)
        {
            SpatialGraph g = world.Graph;
            long treasury = world.GetTreasury(s.Tag);
            List<SpatialNode> owned = g.OwnedBy(s.Tag).ToList();

            long ledger = world.GetLedger(s.Tag);
            double affluence = Clamp01((double)ledger / Math.Max(1, t.ComfortLedger));
            // cash-flow pressure: bleeding (net flow < 0) raises Wealth even when the stock still looks
            // healthy, scaled by runway = ticks until broke. Stock-poverty OR bleeding both push Wealth.
            double bleed = 0;
            if (netFlow < 0)
            {
                double runway = treasury / (double)(-netFlow);
                bleed = Clamp01(1.0 - runway / Math.Max(1.0, t.RunwayComfortTicks));
            }
            // P8: bank grant signal — if a grant was recently issued (cooldown active), halve bleed
            // so the brain doesn't read itself as perpetually cash-poor immediately after a grant.
            if (bleed > 0 && s.CentralBankGrantCooldownUntilUtc > world.UtcNow)
                bleed *= 0.5;
            double poverty = 1.0 - affluence;
            double wealth = Clamp01(Math.Max(poverty, bleed));

            double security = 0;
            foreach (SpatialNode n in owned)
                security = Math.Max(security, PerceivedThreat(n, s, p));

            // Layer-2 friction: where our influence sphere overlaps another faction's, the border is
            // tense — but, like player presence, it's read through traits (xenophobes/aggressors react
            // hard; merchants barely notice and keep trading through it). Dampened by relative fleet
            // power (see DampedRivalPressure/PowerDampening): a dominant fleet reads a weak rival's
            // border presence as much less threatening than an equal-strength rival's.
            double friction = DampedRivalPressure(s, world, t, out _);
            security = Math.Max(security, friction * (0.3 + 0.7 * p.Traits.Xenophobia));

            // Territorial atrophy: natural hold decay raises Security even without an active enemy —
            // neglected sources lose income and influence just as surely as raided ones.
            if (owned.Count > 0)
            {
                double minHold = 1.0;
                foreach (SpatialNode n in owned) if (n.HoldStrength < minHold) minHold = n.HoldStrength;
                double atrophy = Clamp01((0.55 - minHold) / 0.55);  // linear: 0 at hold≥0.55, 1.0 at hold=0
                security = Math.Max(security, atrophy * 0.7);
            }

            security = Clamp01(security);

            bool hasFrontier = !owned.Any() || g.Frontier(s.Tag).Any();
            double growth = Clamp01((1.0 - security) * affluence * (hasFrontier ? 1.0 : 0.25));
            double opportunity = OpportunitySignal(g, s) * p.Traits.Mercantilism; // neutral presence = upside
            growth = Clamp01(growth + 0.5 * opportunity);

            // Fleet readiness: when the faction owns no hull for a fleet role, steer the brain toward
            // Develop / Expand so it builds up production capacity instead of queueing blocked fleets.
            double fleetReadiness = FactionFleetReadiness(s, world);
            growth = Clamp01(growth + 0.35 * (1.0 - fleetReadiness));

            // a hated rival stokes rivalry even when it isn't currently pressing — relations DRIVE conflict,
            // not just target it. (worst = most-negative standing; merchants rarely have one, so they stay calm.)
            double enmity = 0;
            string worstEnemyTag = null;
            foreach (var kv in s.Relations) if (kv.Value < enmity) { enmity = kv.Value; worstEnemyTag = kv.Key; }
            double rivalry = Clamp01(HostileSignal(g, s) + p.Traits.Aggression * security * 0.5
                                     + friction * (0.2 + 0.6 * p.Traits.Aggression)
                                     + (-enmity) * (0.25 + 0.45 * p.Traits.Aggression));
            double caution = Clamp01(Math.Max(Math.Max(poverty, bleed * 1.15), owned.Count == 0 ? 0.4 : 0.0));

            // War fatigue cools the urge to keep fighting — but it is weariness with a PARTICULAR
            // enemy, so the damping is paired with the rival the Rivalry drive is actually about
            // (the same worst standing the enmity term above reads). Taking the max over every pair
            // instead made exhaustion with one neighbour silence rivalry toward everyone, including
            // an enemy this faction had never fought: the same aggregation error FAC-248 removed from
            // border pressure.
            double enemyFatigue = worstEnemyTag != null ? s.FatigueTo(worstEnemyTag) : 0.0;
            rivalry = Clamp01(rivalry * (1.0 - 0.8 * enemyFatigue));

            // Caution keeps reading the peak: being worn out anywhere is a reason to be careful
            // everywhere, and unlike rivalry it is not aimed at anyone.
            double peakFatigue = 0;
            foreach (var kv in s.Fatigue) if (kv.Value > peakFatigue) peakFatigue = kv.Value;
            caution = Clamp01(Math.Max(caution, 0.5 * peakFatigue));

            // Debt stress: high debt/ledger ratio pushes Caution (stop expanding, focus on repayment).
            long debt = world.GetDebt(s.Tag);
            if (debt > 0 && ledger > 0 && t.DebtRatio > 0)
            {
                double debtStress = Clamp01((double)debt / Math.Max(1L, ledger) / t.DebtRatio);
                caution = Clamp01(Math.Max(caution, debtStress * 0.8));
            }

            // P7: personality-drift war-spiral correction — settler/mercantile factions fighting
            // too many enemies simultaneously get a Caution boost to nudge them toward de-escalation
            // faster than raiders would. Does not affect aggressive/raider personalities (aggr >= 0.5).
            if (p.Traits.Aggression < 0.5)
            {
                int highFatigueEnemies = 0;
                foreach (var kv in s.Fatigue)
                    if (kv.Value > t.PeaceThreshold && s.RelationTo(kv.Key) < 0) highFatigueEnemies++;
                if (highFatigueEnemies > t.PersonalityDriftEnemyCount)
                    caution = Clamp01(caution * t.PersonalityDriftCautionBoost);
            }

            s.Drives[Drive.Wealth] = wealth;
            s.Drives[Drive.Security] = security;
            s.Drives[Drive.Growth] = growth;
            s.Drives[Drive.Rivalry] = rivalry;
            s.Drives[Drive.Caution] = caution;
        }

        /// <summary>The quality multiplier war-weariness puts on answering the rival it is felt
        /// toward: 1.0 when fresh, falling to <see cref="FactionTuning.WarWearyQualityFloor"/> when
        /// wholly spent. A multiplier and not a gate — a faction losing ground to an enemy it is tired
        /// of is exactly the one that must still be able to nominate taking it back.</summary>
        private static double WarWeariness(double fatigue, FactionTuning t)
        {
            double floor = Clamp01(t.WarWearyQualityFloor);
            return floor + (1.0 - floor) * (1.0 - Clamp01(fatigue));
        }

        /// <summary>Hostile activity always counts; neutral foreign presence counts only for xenophobes.</summary>
        private static double PerceivedThreat(SpatialNode n, FactionState s, Personality p)
        {
            return Clamp01(n.Threat + p.Traits.Xenophobia * ForeignNeutralPresence(n, s));
        }

        private static double ForeignNeutralPresence(SpatialNode n, FactionState s)
        {
            if (n.Presences.Count == 0) return 0;
            int foreign = 0;
            foreach (Presence pr in n.Presences)
            {
                if (pr.Hostile) continue;                       // hostility flows via Threat/Rivalry
                if (pr.ActorId == s.Tag) continue;              // ourselves
                if (s.RelationTo(pr.ActorId) > 0.25) continue;  // friends aren't a foreign-threat
                foreign++;
            }
            return Clamp01(foreign / 2.0);
        }

        private static double OpportunitySignal(SpatialGraph g, FactionState s)
        {
            double best = 0;
            foreach (SpatialNode n in g.OwnedBy(s.Tag))
                foreach (Presence pr in n.Presences)
                    if (!pr.Hostile && pr.Kind == ActorKind.Player && s.RelationTo(pr.ActorId) >= 0)
                        best = Math.Max(best, 0.6);
            return best;
        }

        private static double HostileSignal(SpatialGraph g, FactionState s)
        {
            double sig = 0;
            foreach (SpatialNode n in g.OwnedBy(s.Tag))
            {
                sig = Math.Max(sig, n.Threat);
                foreach (Presence pr in n.Presences)
                    if (pr.Hostile || s.RelationTo(pr.ActorId) < -0.25) sig = Math.Max(sig, 0.7);
            }
            return Clamp01(sig);
        }

        /// <summary>Multiplier applied to a rival's territorial Pressure before it feeds Security/
        /// Rivalry, based on relative GetFleetCombatPower. Anchored at three points: powerRatio=1
        /// (we dominate) -> multiplier approaches 0 (an outclassed neighbor barely registers as a
        /// threat); powerRatio=0.5 (parity) -> BorderPressureParityFloor (never 0 — equal fleets
        /// still read as mildly tense, avoiding a stable cold war with no rising-ship-count pressure);
        /// powerRatio=0 (we're outclassed) -> BorderPressureWeaknessCeiling (>1 — amplified threat).
        /// total<=0 (neither side has any known combat power) reads as parity, not zero.</summary>
        private static double PowerDampening(long ourPower, long theirPower, FactionTuning t)
        {
            long total = ourPower + theirPower;
            if (total <= 0) return t.BorderPressureParityFloor;
            double ratio = ourPower / (double)total;
            return ratio >= 0.5
                ? t.BorderPressureParityFloor * (1 - (ratio - 0.5) / 0.5)
                : t.BorderPressureParityFloor + (t.BorderPressureWeaknessCeiling - t.BorderPressureParityFloor) * (0.5 - ratio) / 0.5;
        }

        /// <summary>Worst (max-over-rivals) territorial Pressure this faction feels, each rival's
        /// contribution scaled by PowerDampening. This is why IInfluenceField exposes RivalTags and
        /// Pressure rather than a pre-aggregated worst-rival figure: the maximum has to be taken
        /// AFTER each rival's fleet weighting, so no aggregate computed inside the field can serve.
        /// Also reports which rival produced that worst value,
        /// since the Rivalry-driven Muster candidate needs "who is the actual driver of Rivalry" and
        /// would otherwise have to re-scan the same rival set a second time.</summary>
        private static double DampedRivalPressure(FactionState s, IWorldView world, FactionTuning t, out string worstRivalTag)
        {
            worstRivalTag = null;
            if (world.Influence == null) return 0.0;
            long ourPower = world.GetFleetCombatPower(s.Tag);
            double worst = 0;
            foreach (string rival in world.Influence.RivalTags(s.Tag))
            {
                double raw = world.Influence.Pressure(s.Tag, rival);
                if (raw <= 0) continue;
                long theirPower = world.GetFleetCombatPower(rival);
                double damped = raw * PowerDampening(ourPower, theirPower, t);
                if (damped > worst) { worst = damped; worstRivalTag = rival; }
            }
            return worst;
        }

        private static Drive DominantDrive(FactionState s, Personality p)
        {
            Drive best = Drive.Caution;
            double bestVal = double.NegativeInfinity;
            for (int i = 0; i < DriveVector.Count; i++)
            {
                Drive d = (Drive)i;
                double v = s.Drives[d] * p.Weights[d];
                if (v > bestVal) { bestVal = v; best = d; }
            }
            return best;
        }

        // ---- generate concrete candidate intents from every drive, then let scoring arbitrate ----
        private static List<Candidate> Generate(FactionState s, Personality p, IWorldView world, FactionTuning t)
        {
            // Prune stale grudges once per think cycle before any candidate generation uses them.
            if (s.Grudges.Count > 0) Diplomacy.PruneStaleGrudges(s, world.UtcNow);
            return GenerateInternal(s, p, world, t);
        }

        private static List<Candidate> GenerateInternal(FactionState s, Personality p, IWorldView world, FactionTuning t)
        {
            var merged = new List<Candidate> { new Candidate { Intent = new FactionIntent { Kind = ActionKind.Idle }, SourceDrive = Drive.Caution, Support = 1.0, SourceStrength = 1.0 } };
            double expansionScale = ExpansionHealthScale(s, world, t);

            for (int i = 0; i < DriveVector.Count; i++)
            {
                Drive drive = (Drive)i;
                double driveStrength = DriveStrength(s, p, drive);
                if (driveStrength <= 0) continue;
                List<Candidate> generated = GenerateForDrive(s, p, world, t, drive, expansionScale);
                foreach (Candidate candidate in generated)
                {
                    if (candidate.Intent.Kind == ActionKind.Idle) continue;
                    MergeCandidate(merged, candidate, drive, driveStrength);
                }
            }
            // P1: build-completion urgency — if a ship build is nearly done, inject a high-quality
            // FinishBuild candidate so it competes over Security-backed Gift loops.
            AddFinishBuildCandidates(merged, s, world, t);
            // Standing picket: the drive voices sum to the need, and a calm faction's sum can be too
            // small for the defend offer to reach the draw. Floor it while no defend focus is held.
            if (t.StandingPicketNeed > 0 && CampaignFocusLifecycle.DefensiveFocusCount(s) == 0)
                foreach (Candidate c in merged)
                    if (IsDefensiveCampaign(c.Intent) && c.Support < t.StandingPicketNeed) c.Support = t.StandingPicketNeed;
            return merged;
        }

        /// <summary>A Campaign intent that Enact hands to <see cref="CampaignFocusLifecycle.TryStart"/>:
        /// no objective and no cause, unlike the raid, conquest and terror campaigns.</summary>
        private static bool IsDefensiveCampaign(FactionIntent intent)
        {
            return intent.Kind == ActionKind.Campaign && !intent.ObjectiveKind.HasValue && !intent.Cause.HasValue;
        }

        /// <summary>Quality damper for new-territory candidates (Claim/Expand): the larger the share
        /// of owned nodes below <see cref="FactionTuning.HoldMaintenanceThreshold"/>, the lower the
        /// scale — a faction that cannot maintain what it already holds should consolidate before
        /// growing. 1.0 when healthy, floored at 1 - ClaimHoldHealthWeight when fully degraded.</summary>
        private static double ExpansionHealthScale(FactionState s, IWorldView world, FactionTuning t)
        {
            if (t.ClaimHoldHealthWeight <= 0) return 1.0;
            int owned = 0, underHeld = 0;
            foreach (SpatialNode n in world.Graph.OwnedBy(s.Tag))
            {
                owned++;
                if (n.HoldStrength < t.HoldMaintenanceThreshold) underHeld++;
            }
            if (owned == 0) return 1.0;
            return 1.0 - Clamp01(t.ClaimHoldHealthWeight) * ((double)underHeld / owned);
        }

        // ---- generate concrete candidate intents for one drive voice (+ an Idle floor) ----------
        private static List<Candidate> GenerateForDrive(FactionState s, Personality p, IWorldView world, FactionTuning t, Drive goal, double expansionScale)
        {
            var list = new List<Candidate> { new Candidate { Intent = new FactionIntent { Kind = ActionKind.Idle } } };
            SpatialGraph g = world.Graph;

            switch (goal)
            {
                case Drive.Growth:
                case Drive.Wealth:
                    bool growOk = world.GetTreasury(s.Tag) > t.InsolvencyFloor; // expand only while solvent
                    var ownedList = g.OwnedBy(s.Tag).ToList();

                    // consolidate: prefer dismantling an expensive capability on a draining node.
                    // Full withdrawal is a Caution action, so a merely marginal source does not get
                    // abandoned while the faction is still trying to grow or improve yield.
                    AddDismantleCandidates(list, ownedList, s, world, t);

                    // grow yield in place: install the best (node, capability) pair scored by
                    // NetworkGap × EnvironmentFit. Only considers nodes we actually control.
                    AddDevelopCandidates(list, ownedList, s, world, t);

                    var frontier = ClaimableFrontier(g, s.Tag, world.UtcNow).ToList();
                    var reachableDerelicts = frontier
                        .Where(n => n.OwnerFactionTag == NeutralFaction.Tag)
                        .OrderByDescending(n => n.ResourceValue)
                        .ThenBy(n => n.Id)
                        .ToList();
                    if (growOk && reachableDerelicts.Count > 0)
                        AddClaimCandidates(list, reachableDerelicts, t, expansionScale);
                    else if (growOk && frontier.Count > 0)
                        AddClaimCandidates(list, frontier.OrderByDescending(n => n.ResourceValue).ThenBy(n => n.Id), t, expansionScale);
                    if (ownedList.Count == 0 && reachableDerelicts.Count == 0)
                        list.Add(Make(ActionKind.Expand, null, t.ExpandCost, 0.6));   // found a first outpost (seed capital) — only when no UNKN node to claim first
                    if (ownedList.Count == 0 && reachableDerelicts.Count == 0)
                    {
                        if (growOk && frontier.Count > 0)
                            AddClaimCandidates(list, frontier.OrderByDescending(n => n.ResourceValue).ThenBy(n => n.Id), t, expansionScale);
                    }
                    else if (growOk && reachableDerelicts.Count == 0)
                        foreach (SpatialNode home in PreferredExpandOrigins(g, s.Tag, t).Take(2))
                            list.Add(Make(ActionKind.Expand, home.Id, t.ExpandCost, 0.55 * expansionScale)); // grow into fresh space
                    AddFoundingCandidates(list, s, world, t);
                    break;

                case Drive.Security:
                case Drive.Rivalry:
                    {
                        var threatened = g.OwnedBy(s.Tag)
                            .Select(n => new KeyValuePair<SpatialNode, double>(n, PerceivedThreat(n, s, p)))
                            .OrderByDescending(kv => kv.Value).Take(3);
                        foreach (var kv in threatened)
                        {
                            if (kv.Value > 0.7 && kv.Key.HoldStrength < 0.3)
                                list.Add(Make(ActionKind.Withdraw, kv.Key.Id, 0, kv.Value));
                        }
                    }
                    // Guard emission removed (BUG-003): Guard is an operational FleetManager
                    // action now; the brain signals defense posture via Campaign only.
                    AddRaids(list, s, p, world, t);            // strike a hostile rival's reachable source
                    AddConquers(list, s, p, world, t);         // finish off an already-weakened hostile node
                    AddTerrorCampaign(list, s, p, world, t);   // the antagonist picks a region to raid
                    AddPeaceOvertures(list, s, p, world, t);  // ...or sue for peace if the war has worn us down
                    AddGiftOffers(list, s, world, t);          // olive branch: buy goodwill from a cold-war neighbour
                    AddPactProposals(list, s, world, t);       // formalise high-relation partnerships into alliances
                    // Secure-expansion: when all territory is contested, expanding into fresh
                    // uncontested nodes is itself a security action — it gives the faction a
                    // buffer zone and grows the influence sphere away from the contested core.
                    // Quality 0.35 sits above the maintenance-Reinforce floor (0.20) but below
                    // a directly-threatened Reinforce, so acute defence still wins; once
                    // repetition suppresses the looped Reinforce cycle this competes naturally.
                    if (s.Drives[Drive.Security] > 0.35 && world.GetTreasury(s.Tag) > t.InsolvencyFloor)
                    {
                        var securityDerelicts = ClaimableFrontier(g, s.Tag, world.UtcNow)
                            .Where(n => n.OwnerFactionTag == NeutralFaction.Tag)
                            .OrderByDescending(n => n.ResourceValue)
                            .ThenBy(n => n.Id)
                            .ToList();
                        if (securityDerelicts.Count > 0)
                            AddClaimCandidates(list, securityDerelicts, t, expansionScale);
                        else
                        {
                            var ownedForExpand = PreferredExpandOrigins(g, s.Tag, t).ToList();
                            if (ownedForExpand.Count > 0)
                                foreach (SpatialNode home in ownedForExpand.Take(2))
                                    list.Add(Make(ActionKind.Expand, home.Id, t.ExpandCost, 0.35 * expansionScale));
                        }
                    }
                    break;

                case Drive.Caution:
                    // Guard emission removed (BUG-003): operational defense belongs to FleetManager.
                    {
                        var cautionOwned = g.OwnedBy(s.Tag).ToList();
                        AddDismantleCandidates(list, cautionOwned, s, world, t);  // trim capabilities before abandoning nodes
                        long treasury = world.GetTreasury(s.Tag);
                        long ledger = world.GetLedger(s.Tag);
                        if (treasury <= t.ComfortTreasury && ledger <= t.ComfortLedger)
                            AddDrainWithdrawals(list, cautionOwned, s, world, t); // cut losses only under real cash pressure
                    }
                    AddPeaceOvertures(list, s, p, world, t);  // a war-weary faction de-escalating reaches for a truce
                    AddGiftOffers(list, s, world, t);          // olive branch: buy goodwill from a cold-war neighbour
                    AddPactProposals(list, s, world, t);       // formalise high-relation partnerships into alliances
                    break;
            }
            // Campaign focus: replace strategic Reinforce/Patrol competition with a single
            // campaign intent under Security/Caution/Growth drives. FleetManager will
            // interpret this intent and dispatch Reinforce/Patrol operationally.
            // Quality scales with the dominant security drive strength so that urgent
            // security pressure produces higher-utility campaign candidates.
            // Defensive capacity only: an offensive focus does not take a defensive slot.
            // Standing picket: a faction holding no defensive focus is offered one whatever its
            // drives, because Patrol flies only under a defend focus; GenerateInternal floors its
            // need. Pressure decides only whether further foci are offered.
            int defensiveFoci = CampaignFocusLifecycle.DefensiveFocusCount(s);
            bool standingPicket = defensiveFoci == 0 && t.StandingPicketNeed > 0;
            if (defensiveFoci < t.CampaignFocusCapacity(world.GetFleetCombatPower(s.Tag))
                && (standingPicket || s.Drives[Drive.Security] > 0.15 || s.Drives[Drive.Caution] > 0.3
                || s.Drives[Drive.Growth] > 0.5))
            {
                var dominantDrive = s.Drives[Drive.Security] > 0 ? Drive.Security
                    : s.Drives[Drive.Caution] > 0 ? Drive.Caution : Drive.Growth;
                double qualityMultiplier = Clamp01(s.Drives[dominantDrive] * 0.8);
                string topNode = g.OwnedBy(s.Tag)
                    // A current focus retains its centre for its full persisted lifetime; nominate
                    // the next distinct centre rather than reissuing the same Campaign action.
                    .Where(n => !s.CampaignFoci.Any(f => f != null
                        && (f.CenterNodeId == n.Id || f.RegionMembers.Contains(n.Id))))
                    .OrderByDescending(n => PerceivedThreat(n, s, p)).Select(n => n.Id).FirstOrDefault();
                if (topNode != null)
                {
                    var campaignCandidate = new Candidate
                    {
                        Intent = new FactionIntent
                        {
                            Kind = ActionKind.Campaign,
                            TargetNodeId = topNode,
                            Cost = t.CampaignCost,
                        },
                        Quality = Clamp01(qualityMultiplier + 0.3),
                        SourceDrive = dominantDrive,
                    };
                    MergeCandidate(list, campaignCandidate, dominantDrive, s.Drives[dominantDrive] * 0.4);
                }
            }
            // Muster: deterrence build-up, the military counterpart to Gift — where Gift buys
            // goodwill from a cold-war neighbour, Muster raises the fleet strength that makes
            // territorial pressure feel less threatening in the first place (see PowerDampening).
            // Two firing modes, both state-only (no dispatch — Enact mirrors Campaign's handler):
            //  - Security-driven (untargeted "para bellum"): raises general MilitaryPosture.
            //  - Rivalry-driven (targeted): focuses the same worst-dampened-pressure rival that
            //    Sense() already identified as the actual driver of Rivalry, so a build-up responds
            //    to a genuine threat instead of diffusing across every neighbor equally.
            // Both modes are scaled by remaining headroom (1 - MilitaryPosture) — otherwise a
            // fully-postured faction keeps winning the softmax draw against Idle/other actions and
            // spending MusterCost for zero effect, since Enact clamps posture at 1.0 regardless.
            {
                string worstRivalTag;
                double dampedPressure = DampedRivalPressure(s, world, t, out worstRivalTag);
                double headroom = 1.0 - s.MilitaryPosture;

                if (s.Drives[Drive.Security] > 0.4)
                {
                    double musterQuality = Clamp01((0.25 + 0.55 * s.Drives[Drive.Security]) * headroom);
                    var musterCandidate = new Candidate
                    {
                        Intent = new FactionIntent { Kind = ActionKind.Muster, Cost = t.MusterCost },
                        Quality = musterQuality,
                    };
                    MergeCandidate(list, musterCandidate, Drive.Security, s.Drives[Drive.Security] * 0.4);
                }

                if (worstRivalTag != null && s.Drives[Drive.Rivalry] > 0.4 && dampedPressure > 0.2)
                {
                    double musterQuality = Clamp01((0.3 + 0.6 * s.Drives[Drive.Rivalry]) * headroom);
                    var musterCandidate = new Candidate
                    {
                        Intent = new FactionIntent { Kind = ActionKind.Muster, TargetFactionTag = worstRivalTag, Cost = t.MusterCost },
                        Quality = musterQuality,
                    };
                    MergeCandidate(list, musterCandidate, Drive.Rivalry, s.Drives[Drive.Rivalry] * 0.4);
                }
            }
            return list;
        }

        /// <summary>The claimable frontier minus ground that mints nothing — the only frontier any
        /// Claim candidate is drawn from.
        ///
        /// <para>Filtered here rather than inside <see cref="AddClaimCandidates"/> because the
        /// derelict list this feeds does two jobs: it supplies the candidates, and its emptiness is
        /// what lets Expand run at all. A sterile derelict left in it would suppress expansion in
        /// favour of a Claim that is then never made — and, once <see cref="FleetManager"/> stopped
        /// reinforcing worthless ground, would be re-claimed the moment decay released it, at
        /// ClaimCost a lap, forever. Ground worth nothing to hold is worth nothing to retake.</para>
        ///
        /// <para><see cref="SpatialGraph.Frontier"/> itself stays unfiltered: it answers a question
        /// about topology, and one caller (the Growth-drive frontier signal) genuinely wants "is
        /// there open ground adjacent" regardless of what is under it.</para>
        ///
        /// <para>Ground another faction's live siege is holding
        /// (<see cref="NeutralFaction.IsReservedAgainst"/>) is dropped for the same reason: the
        /// Claim would be refused, and a candidate that cannot be enacted still costs the pass a
        /// decision slot and suppresses Expand behind it.</para></summary>
        private static IEnumerable<SpatialNode> ClaimableFrontier(SpatialGraph g, string factionTag, DateTime nowUtc)
        {
            foreach (SpatialNode n in g.Frontier(factionTag))
                if (!Economy.MintsNothing(n) && !NeutralFaction.IsReservedAgainst(n, factionTag, nowUtc))
                    yield return n;
        }

        private static IEnumerable<SpatialNode> PreferredExpandOrigins(SpatialGraph g, string factionTag, FactionTuning t)
        {
            List<SpatialNode> owned = g.OwnedBy(factionTag).ToList();
            IOrderedEnumerable<SpatialNode> frontier = owned
                .Where(n => g.IsFrontier(factionTag, n, t.NodeFrontierMaxOwnedNeighbors))
                .OrderBy(n => g.OwnedNeighborCount(factionTag, n))
                .ThenByDescending(n => n.ResourceValue)
                .ThenBy(n => n.Id);

            if (frontier.Any())
                return frontier;

            return owned
                .OrderBy(n => g.OwnedNeighborCount(factionTag, n))
                .ThenByDescending(n => n.ResourceValue)
                .ThenBy(n => n.Id);
        }

        private static void AddClaimCandidates(List<Candidate> list, IEnumerable<SpatialNode> frontier, FactionTuning t,
            double qualityScale)
        {
            // UNKN nodes are preferred over expanding into new space: Claim quality is boosted
            // so it beats the Expand seed (q=0.6) and fresh-space Expand (q=0.55/0.35) in the
            // softmax, while still scaling with resource attractiveness for the same prioritisation.
            // qualityScale (ExpansionHealthScale) damps the whole tier when the estate is under-held.
            foreach (SpatialNode node in frontier.Take(3))
                list.Add(Make(ActionKind.Claim, node.Id, t.ClaimCost, (0.55 + 0.45 * node.ResourceValue) * qualityScale));
        }

        private static Candidate Make(ActionKind kind, string target, long cost, double quality)
        {
            return new Candidate { Intent = new FactionIntent { Kind = kind, TargetNodeId = target, Cost = cost }, Quality = quality };
        }

        private static Candidate MakeDevelop(string nodeId, NodeCapabilityKind cap, long cost, double quality)
        {
            return new Candidate { Intent = new FactionIntent { Kind = ActionKind.Develop, TargetNodeId = nodeId, TargetCapability = cap, Cost = cost }, Quality = quality };
        }

        private static void MergeCandidate(List<Candidate> list, Candidate candidate, Drive drive, double driveStrength)
        {
            Candidate existing = list.FirstOrDefault(c => SameIntent(c.Intent, candidate.Intent));
            if (existing == null)
            {
                candidate.SourceDrive = drive;
                candidate.Support = driveStrength;
                candidate.SourceStrength = driveStrength;
                list.Add(candidate);
                return;
            }

            existing.Support += driveStrength;
            if (candidate.Quality > existing.Quality) existing.Quality = candidate.Quality;
            if (driveStrength > existing.SourceStrength)
            {
                existing.SourceDrive = drive;
                existing.SourceStrength = driveStrength;
            }
        }

        private static bool SameIntent(FactionIntent a, FactionIntent b)
        {
            return a.Kind == b.Kind
                && a.TargetNodeId == b.TargetNodeId
                && a.TargetFactionTag == b.TargetFactionTag
                && a.TargetCapability == b.TargetCapability
                && a.TargetRegion == b.TargetRegion
                // Two campaigns staging from the same node against the same region are still two
                // different decisions when they mean to do different things there, or for different
                // reasons — a raid campaign and a conquest campaign are not one candidate.
                && a.ObjectiveKind == b.ObjectiveKind
                && a.Cause == b.Cause
                && a.ObjectiveNodeId == b.ObjectiveNodeId
                && a.Cost == b.Cost;
        }

        private static double DriveStrength(FactionState s, Personality p, Drive drive)
        {
            return s.Drives[drive] * p.Weights[drive];
        }

        /// <summary>Enumerate (owned node × uninstalled capability) pairs, score each as
        /// NetworkGap × EnvironmentFit, and emit the best few as Develop candidates.</summary>
        private static void AddDevelopCandidates(List<Candidate> list, List<SpatialNode> owned, FactionState s, IWorldView world, FactionTuning t)
        {
            // Don't invest when we can't afford it — prevents the invest→bleed→invest cycle where
            // the brain keeps picking Develop and Enact keeps silently no-opping due to insufficient funds.
            if (world.GetTreasury(s.Tag) <= t.InsolvencyFloor + t.DevelopCost) return;

            // Capability network coverage: how many nodes already have each capability installed.
            int[] installed = InvestmentTargeting.InstalledCounts(owned);

            // ---- Phase 1: manager investment requests (high quality: 0.70–1.00) -----------------
            // Managers post requests when a structural gap is blocking production. These always
            // outbid the exploratory phase (capped at 0.65) so unblocking takes priority.
            List<InvestmentRequest> requests = world.InvestmentRegister.SnapshotFor(s.Tag);
            foreach (InvestmentRequest req in requests)
            {
                InvestmentTarget target = InvestmentTargeting.Resolve(world, s.Tag, owned, installed, req, t);
                if (target.Resolved)
                    list.Add(MakeDevelop(target.NodeId, req.Cap, t.DevelopCost,
                        Clamp01(0.7 + 0.3 * req.Urgency)));
            }

            // ---- Phase 2: exploratory gap×fit (quality capped at 0.65) ------------------------
            // Structural heuristic: install the best unmet capability when no manager is asking.
            // Capped below the manager-request floor so it never outbids an investment request.
            SpatialNode bestNode = null;
            NodeCapabilityKind bestCap = NodeCapabilityKind.Refinery;
            double bestScore = 0;

            foreach (SpatialNode n in owned)
            {
                if (n.StableStationId == 0) continue;                  // capabilities are station sub-modules; need a Founded node
                double control = world.Influence != null ? world.Influence.ControlAt(n.Id, s.Tag) : 1.0;
                if (control <= 0.5) continue;

                for (int ci = 0; ci < FactionTuning.AllCapabilities.Length; ci++)
                {
                    NodeCapabilityKind cap = FactionTuning.AllCapabilities[ci];

                    NodeCapabilityKind? capPrereq = CapabilityPrerequisites.For(cap);
                    if (capPrereq.HasValue && installed[(int)capPrereq.Value] < 1) continue;

                    int level;
                    n.Capabilities.TryGetValue(cap, out level);
                    if (level >= 1) continue;

                    // By ordinal: InstalledCounts is indexed by kind, and AllCapabilities is not
                    // in ordinal order once it omits a kind.
                    double gap   = Clamp01(1.0 - installed[(int)cap] * 0.5);
                    double fit   = NodeContent.CapabilityAffinityFor(n, cap, t);
                    double score = gap * fit;
                    // P12: chain bonus — installing this cap at a node adjacent to an owned upstream node
                    // completes a spatial supply chain (e.g. Refinery neighbor + this Manufactory = chain).
                    if (t.DevelopChainBonus > 0)
                    {
                        NodeCapabilityKind? prereq = CapabilityPrerequisites.For(cap);
                        if (prereq.HasValue)
                        {
                            foreach (string nbId in n.Neighbors)
                            {
                                SpatialNode nb;
                                if (!world.Graph.Nodes.TryGetValue(nbId, out nb)) continue;
                                if (nb.OwnerFactionTag != s.Tag) continue;
                                int nbLvl;
                                if (nb.Capabilities.TryGetValue(prereq.Value, out nbLvl) && nbLvl >= 1)
                                { score += t.DevelopChainBonus; break; }
                            }
                        }
                    }
                    if (score > bestScore) { bestScore = score; bestNode = n; bestCap = cap; }
                }
            }

            if (bestNode != null && bestScore > 0)
                list.Add(MakeDevelop(bestNode.Id, bestCap, t.DevelopCost, Clamp01(0.3 + 0.35 * bestScore)));
        }

        /// <summary>Fraction of the faction's network that lacks a given capability: 1.0 = none installed,
        /// 0.0 = two or more installed ("covered"). Used by <see cref="AddDevelopCandidates"/>.</summary>
        private static double NetworkGap(FactionState s, NodeCapabilityKind cap, IWorldView world)
        {
            int installed = 0;
            foreach (SpatialNode n in world.Graph.OwnedBy(s.Tag))
            {
                int level;
                if (n.Capabilities.TryGetValue(cap, out level) && level >= 1) installed++;
            }
            return Clamp01(1.0 - installed * 0.5);
        }

        private static double NodeUsefulness(SpatialNode n, double control, FactionTuning t, IItemCatalogue catalogue)
        {
            double usefulness = Economy.SourceYield(n, control, t);
            usefulness += Economy.NodeItemIncome(n, control, t, catalogue);

            int installedCapabilities = 0;
            foreach (var cap in n.Capabilities)
                if (cap.Value > 0) installedCapabilities += cap.Value;

            usefulness += installedCapabilities * (0.25 * t.UpkeepPerSource);
            return usefulness;
        }

        /// <summary>Consolidation: propose shedding the worst net-negative ("drain") owned source. Quality
        /// favours the ones we can't rescue by spending — a besieged source (low control) ranks above a
        /// merely marginal one, so cautious/peaceful factions cut sieges while aggressors dig in. Free (cost 0).</summary>
        private static void AddDrainWithdrawals(List<Candidate> list, List<SpatialNode> owned, FactionState s, IWorldView world, FactionTuning t)
        {
            long treasury = world.GetTreasury(s.Tag);
            long ledger = world.GetLedger(s.Tag);
            if (treasury > t.ComfortTreasury || ledger > t.ComfortLedger) return;

            SpatialNode worst = null; double worstControl = 1; long worstNet = 0;
            double worstUsefulness = double.PositiveInfinity;
            foreach (SpatialNode n in owned)
            {
                double control = world.Influence != null ? world.Influence.ControlAt(n.Id, s.Tag) : 1.0;
                long net = Economy.SourceNet(n, control, t, world.Catalogue);
                if (net >= 0) continue;

                double usefulness = NodeUsefulness(n, control, t, world.Catalogue);
                if (worst == null || usefulness < worstUsefulness ||
                    (Math.Abs(usefulness - worstUsefulness) < 0.0001 && net < worstNet))
                {
                    worst = n;
                    worstNet = net;
                    worstControl = control;
                    worstUsefulness = usefulness;
                }
            }
            if (worst != null)
            {
                double severity = Clamp01((double)(-worstNet) / Math.Max(1L, t.UpkeepPerSource));
                double usefulnessBias = Clamp01(1.0 - worstUsefulness / Math.Max(1.0, t.ComfortLedger));
                list.Add(Make(ActionKind.Withdraw, worst.Id, 0,
                    Clamp01(0.55 + 0.20 * severity + 0.15 * usefulnessBias + 0.10 * (1 - worstControl))));
            }
        }

        /// <summary>Propose removing the most expensive capability from any net-negative node.
        /// Preferred over full withdrawal (quality 0.55 vs. Withdraw's max 0.4 at full control)
        /// because trimming a capability preserves the node's production while cutting upkeep costs.</summary>
        private static void AddDismantleCandidates(List<Candidate> list, List<SpatialNode> owned, FactionState s, IWorldView world, FactionTuning t)
        {
            SpatialNode worstNode = null;
            NodeCapabilityKind worstCap = NodeCapabilityKind.Refinery;
            long worstCapUpkeep = 0;

            foreach (SpatialNode n in owned)
            {
                double control = world.Influence != null ? world.Influence.ControlAt(n.Id, s.Tag) : 1.0;
                long nodeNet = Economy.SourceNet(n, control, t, world.Catalogue);
                if (nodeNet >= 0) continue;  // only target nodes that are net drains

                foreach (var cap in n.Capabilities)
                {
                    if (cap.Value <= 0) continue;
                    long capUpkeep = 0;
                    for (int ki = 0; ki < FactionTuning.AllItems.Length; ki++)
                    {
                        ItemKind uk = FactionTuning.AllItems[ki];
                        long u = t.CapabilityUpkeepFor(cap.Key, uk) * cap.Value;
                        if (u > 0) capUpkeep += ItemCredits.For(uk, u, world.Catalogue.CreditValuePer(uk));
                    }
                    if (capUpkeep > worstCapUpkeep) { worstCapUpkeep = capUpkeep; worstNode = n; worstCap = cap.Key; }
                }
            }

            if (worstNode != null)
                list.Add(new Candidate
                {
                    Intent = new FactionIntent { Kind = ActionKind.DismantleCapability, TargetNodeId = worstNode.Id, TargetCapability = worstCap, Cost = 0 },
                    Quality = Clamp01(0.55 + 0.2 * MathUtil.Clamp01((double)worstCapUpkeep / (t.UpkeepPerSource + 1)))
                });
        }

        /// <summary>The best target one region holds, and why it is worth attacking.</summary>
        private struct RegionPick
        {
            public SpatialNode Node;
            public double Score;
            public CampaignCause Cause;
            public NodeRegion Region;
            /// <summary>The target stands in no region — open deep space belonging to no body and no
            /// authored feature — so the objective names the node itself instead.</summary>
            public bool NodeScoped;
            /// <summary>Quality the pick gained for the lanes its owner's space costs this faction
            /// (<see cref="LaneConquestBoost"/>); 0 for a raid pick.</summary>
            public double LaneBoost;
            /// <summary>The lane weight that boost was read from, in credits.</summary>
            public double LaneCredits;
        }

        /// <summary>The key one campaign target collapses under: its region, or the node itself when
        /// it stands in none.</summary>
        public static string CampaignKeyFor(NodeRegion region, SpatialNode node)
        {
            return region.IsNone ? "node:" + node.Id : region.ToKey();
        }

        /// <summary>Keep <paramref name="node"/> as <paramref name="region"/>'s pick when it beats what
        /// is there. <b>The collapse is a max, not a sum</b>: a region is worth what its best target is
        /// worth. Summing every node's score saturates the <c>Clamp01</c> the candidate's quality is
        /// built from, so any multi-node region would score 1.0 and the warm-target damper, the truce
        /// penalty and the grudge boost — every weighting the diplomatic rules exist to produce — would
        /// stop changing the answer. Ties break on node id so two equal regions cannot alternate by
        /// dictionary order.</summary>
        private static void ConsiderRegion(Dictionary<string, RegionPick> picks, NodeRegion region,
            SpatialNode node, double score, CampaignCause cause, double laneBoost = 0, double laneCredits = 0)
        {
            string key = CampaignKeyFor(region, node);
            RegionPick current;
            if (picks.TryGetValue(key, out current)
                && (current.Score > score
                    || (current.Score == score && string.CompareOrdinal(current.Node.Id, node.Id) <= 0)))
                return;
            picks[key] = new RegionPick
            {
                Node = node,
                Score = score,
                Cause = cause,
                Region = region,
                NodeScoped = region.IsNone,
                LaneBoost = laneBoost,
                LaneCredits = laneCredits,
            };
        }

        /// <summary>Quality a conquest of <paramref name="node"/> gains for what its owner's space has
        /// cost this faction's lanes (<see cref="FactionState.TransitDenial"/>):
        /// <see cref="FactionTuning.LaneConquestQualityBoost"/> × w / (w + <see cref="FactionTuning.ConquerCost"/>),
        /// so the lanes are worth half the boost once they have cost what a conquest does. Taking the
        /// ground is what opens the lane; raids follow a conquest's region through the seize boost.</summary>
        public static double LaneConquestBoost(FactionState s, SpatialNode node, NodeRegion region, FactionTuning t,
                                                 DateTime nowUtc, out double laneCredits)
        {
            laneCredits = 0;
            if (s == null || node == null || t == null || t.LaneConquestQualityBoost <= 0) return 0;
            if (s.TransitDenial.Count == 0) return 0;
            laneCredits = s.TransitDenial.Weight(node.OwnerFactionTag, CampaignKeyFor(region, node), nowUtc);
            if (laneCredits <= 0) return 0;
            double half = Math.Max(1.0, t.ConquerCost);
            return t.LaneConquestQualityBoost * laneCredits / (laneCredits + half);
        }

        /// <summary>The faction's own node nearest <paramref name="target"/> — where a campaign against
        /// it stages from. Ties break on node id, so the same world always names the same base.</summary>
        private static string StagingNodeFor(List<SpatialNode> owned, SpatialNode target)
        {
            string staging = null;
            double bestDist = double.MaxValue;
            foreach (SpatialNode mine in owned)
            {
                double d = mine.Position.DistanceTo(target.Position);
                if (d < bestDist || (d == bestDist && string.CompareOrdinal(mine.Id, staging) < 0))
                {
                    bestDist = d;
                    staging = mine.Id;
                }
            }
            return staging;
        }

        /// <summary>Turn one region's pick into a <see cref="ActionKind.Campaign"/> candidate carrying the
        /// objective it would start. Skips a region an active objective of the same kind already covers —
        /// a standing campaign is reinforced by the brain's ranking, not re-nominated every think.</summary>
        private static void AddCampaignCandidate(List<Candidate> list, FactionState s, FactionTuning t,
            List<SpatialNode> owned, RegionPick pick, CampaignObjectiveKind kind, double quality)
        {
            string objectiveNodeId = pick.NodeScoped ? pick.Node.Id : null;
            if (OffensiveCampaigns.Covers(s, kind, pick.Region, objectiveNodeId)) return;
            string staging = StagingNodeFor(owned, pick.Node);
            if (staging == null) return;
            list.Add(new Candidate
            {
                Intent = new FactionIntent
                {
                    Kind = ActionKind.Campaign,
                    TargetNodeId = staging,
                    TargetRegion = pick.NodeScoped ? null : pick.Region.ToKey(),
                    ObjectiveKind = kind,
                    Cause = pick.Cause,
                    ObjectiveNodeId = objectiveNodeId,
                    Cost = t.CampaignCost,
                    LaneBoost = pick.LaneBoost,
                    LaneCredits = pick.LaneCredits,
                },
                Quality = quality,
            });
        }

        /// <summary>Conflict, nomination: which <b>regions</b> are worth a raid campaign, and why.
        ///
        /// <para><b>This names a place, not a target.</b> The brain decides whether to fight, where, and
        /// under which <see cref="CampaignCause"/>; <c>FleetManager</c>'s offensive block decides when,
        /// which node of the region, and with what, reading the ranked
        /// <see cref="CampaignObjective.Candidates"/> this brain rewrites every think
        /// (<see cref="ComputeCampaignCandidates"/>). See <c>docs/campaigns.md</c>. Every diplomatic gate
        /// stays here — a pact partner, a faction we are suing for peace, a truce partner's penalty, war
        /// fatigue — because they are strategy, not tactics.</para>
        ///
        /// <para><b>Predation creates the candidate, not hatred</b> (FAC-273). The rule is "you are weaker
        /// than me and have what I want, therefore I raid you, because I am a raider" — so the score is
        /// <c>want × weakness × predation</c>, where <i>want</i> is the target's resource value, <i>weakness</i>
        /// is how loosely it is held, and <i>predation</i> is derived from <see cref="Traits.Aggression"/>
        /// through <see cref="FactionTuning.RaidPredationAggressionFloor"/>. At or below that floor predation
        /// is zero and the faction still raids only owners it already hates. Above it, a raider takes a weak,
        /// valuable neighbour it has no quarrel with, and the hostility that follows is the <i>consequence</i>
        /// rather than the precondition.</para>
        ///
        /// <para><b>Standing modulates; it does not gate.</b> A hostile owner scores full, plus the grudge
        /// and seize boosts. A warm owner is multiplied down by
        /// <see cref="FactionTuning.RaidWarmTargetQualityPenalty"/>, ramped by how warm it is. Pact partners
        /// and factions we are suing for peace are excluded outright, and a truce partner keeps the
        /// truce-breach penalty.</para>
        ///
        /// <para><b>Retaliation is a cause, not a kind</b> (operator, 2026-09-09: <i>"it's just a raid with a
        /// casus belli"</i>). The second pass below nominates a raid campaign with
        /// <see cref="CampaignCause.Retaliation"/> against the attacker's region, on the same two ways in it
        /// always had: an owned node's threat above <see cref="FactionTuning.RetaliateThreatThreshold"/> with a
        /// hostile owner, or an attacker in <see cref="FightLedger.RecentAttackers"/> whatever we think of
        /// them. The fleets it dispatches keep <see cref="ActionKind.Retaliate"/>.</para></summary>
        private static void AddRaids(List<Candidate> list, FactionState s, Personality p, IWorldView world, FactionTuning t)
        {
            if (world.Influence == null) return;
            List<SpatialNode> owned = world.Graph.OwnedBy(s.Tag).ToList();
            if (owned.Count == 0) return;   // nowhere to stage a campaign from

            // Predation: 0 at/below the floor, 1 at maximum aggression. The floor is what keeps a
            // mercantile faction's behaviour unchanged — with predation 0 the non-hostile branch below
            // is skipped entirely, so its candidate set is still "the ones we hate" and nothing else.
            double predation = MathUtil.Clamp01(
                (p.Traits.Aggression - t.RaidPredationAggressionFloor)
                / Math.Max(0.0001, 1.0 - t.RaidPredationAggressionFloor));
            List<SpatialNode> origins = world.SuitableShipOrigins(s.Tag, ActionKind.Raid).ToList();

            var picks = new Dictionary<string, RegionPick>(StringComparer.Ordinal);
            foreach (NodeBelief belief in KnowledgeGate.Beliefs(world.Graph, s.Tag, s.Knowledge, world.UtcNow, t))
            {
                SpatialNode n = belief.Node;
                string owner = belief.OwnerTag;
                if (owner == null || owner == s.Tag || owner == NeutralFaction.Tag) continue;
                double rel = s.RelationTo(owner);
                bool hostileOwner = rel < t.HostileRelationThreshold;
                if (!hostileOwner && predation <= 0.0) continue;                        // no appetite: only hatred makes a target
                if (Diplomacy.InPactWith(s, owner)) continue;                           // never raid an ally
                if (s.Proposals.ContainsKey(owner)) continue;                           // we're suing them for peace, not raiding
                NodeRegion region = NodeRegion.Of(n);
                double reach = world.Influence.SampleAt(n.Id, s.Tag);                   // can we project force there?
                if (reach < t.RaidReachThreshold) continue;
                double score = (0.4 + 0.6 * belief.ResourceValue) * (1.0 - 0.5 * belief.HoldStrength); // value, easier if weakly held
                if (!hostileOwner)
                {
                    // A predatory raid, not a grudge: appetite scales it, and warmth damps it. Applied
                    // only off the hostile path so a hostile target's score is bit-for-bit the old one.
                    score *= predation;
                    if (rel > 0.0 && t.AllyRelationThreshold > 0.0)
                    {
                        double warmth = MathUtil.Clamp01(rel / t.AllyRelationThreshold);
                        score *= 1.0 - warmth * (1.0 - t.RaidWarmTargetQualityPenalty);
                    }
                }
                // P9: grudge — known treaty-breakers rank higher as raid targets
                bool grudge = s.Grudges.ContainsKey(owner);
                if (grudge) score += t.GrudgeRaidQualityBoost;
                // A seize objective's region ranks higher: a conquest campaign softens with raids before
                // it finishes, so the objective has to bias the raid nomination too.
                score += SeizeObjectives.QualityBoost(s, n, t.SeizeObjectiveQualityBoost);
                // Truce-breaking: attacking a truce partner is possible but carries a heavy quality penalty.
                // Aggressive factions with a very valuable target will occasionally choose it; merchants almost never do.
                if (Diplomacy.UnderTruce(s, owner, world.UtcNow)) score *= t.TruceBreachQualityPenalty;
                if (origins.Count > 0 && t.RaidDistanceHalfValueKm > 0)
                {
                    double nearestKm = double.MaxValue;
                    foreach (SpatialNode origin in origins)
                        nearestKm = Math.Min(nearestKm, origin.Position.DistanceTo(n.Position));
                    score /= 1.0 + nearestKm / t.RaidDistanceHalfValueKm;
                }
                if (score <= 0) continue;
                ConsiderRegion(picks, region, n, score, grudge ? CampaignCause.Grudge : CampaignCause.Predation);
            }

            foreach (KeyValuePair<string, RegionPick> kv in picks)
                AddCampaignCandidate(list, s, t, owned, kv.Value,
                    CampaignObjectiveKind.Terrorise, Clamp01(0.4 + 0.6 * kv.Value.Score));

            // Retaliation. Two ways in, and either one alone is enough. The original: an owned node's
            // threat is above RetaliateThreatThreshold and we are hostile to the target's owner. The
            // second: that owner attacked us inside the memory window, whatever we think of them — an
            // attack we defended off leaves no threat on the node and moves no standing before the
            // incident is drained, so a faction with no standing quarrel would otherwise have to be
            // raided into hatred before it could answer the first raid at all.
            SpatialNode mostThreatened = null;
            double maxThreat = t.RetaliateThreatThreshold;
            foreach (SpatialNode tn in owned)
                if (tn.Threat > maxThreat) { maxThreat = tn.Threat; mostThreatened = tn; }

            // The window fatigue already uses to decide a war is still running: the same act, read
            // for the same span, so a faction stops answering an attacker exactly when it stops
            // counting the war against it. Clamped to the clock's own origin, which a world younger
            // than the window (or an unclocked fixture) would otherwise walk back past.
            double windowSeconds = Math.Max(0.0, t.WarFatigueActWindowSeconds);
            double clockSeconds = world.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
            HashSet<string> recentAttackers = FightLedger.RecentAttackers(
                s, world.UtcNow.AddSeconds(-Math.Min(windowSeconds, clockSeconds)));

            if (mostThreatened == null && recentAttackers.Count == 0) return;

            var retaliation = new Dictionary<string, RegionPick>(StringComparer.Ordinal);
            foreach (NodeBelief belief in KnowledgeGate.Beliefs(world.Graph, s.Tag, s.Knowledge, world.UtcNow, t))
            {
                SpatialNode n = belief.Node;
                string owner = belief.OwnerTag;
                if (owner == null || owner == s.Tag || owner == NeutralFaction.Tag) continue;
                bool hostileOwner = s.RelationTo(owner) < t.HostileRelationThreshold;
                bool attackedUs = recentAttackers.Contains(owner);
                // The hostile path is unchanged: it still needs a threatened node of our own.
                if (!(hostileOwner && mostThreatened != null) && !attackedUs) continue;
                if (Diplomacy.UnderTruce(s, owner, world.UtcNow)) continue;
                if (Diplomacy.InPactWith(s, owner)) continue;
                if (s.Proposals.ContainsKey(owner)) continue;
                NodeRegion region = NodeRegion.Of(n);
                double reach = world.Influence.SampleAt(n.Id, s.Tag);
                if (reach < t.RaidReachThreshold) continue;
                double score = (0.4 + 0.6 * belief.ResourceValue) * (1.0 - 0.5 * belief.HoldStrength);
                if (hostileOwner) score *= t.RetaliateHostileTargetQualityBoost;
                if (score <= 0) continue;
                ConsiderRegion(retaliation, region, n, score, CampaignCause.Retaliation);
            }

            // Severity, from the threatened node when there is one. An attack destroyed on the way in
            // leaves none, so it is worth what it would have been worth had it landed — RaidThreat, the
            // threat a landed raid adds.
            double severity = mostThreatened != null ? mostThreatened.Threat : t.RaidThreat;
            double retaliationQuality = Clamp01(0.5 + 0.5 * severity);
            // One answer, not one per place. Retaliation quality is severity alone — how badly we
            // were hit, which is a single number for the whole faction — so every retaliation
            // candidate would carry the identical quality and the hostile-target boost, whose entire
            // job is to rank one attacker above another, would stop deciding anything. Ranking the
            // regions here and nominating the winner is what keeps that boost load-bearing.
            RegionPick answer = default(RegionPick);
            bool haveAnswer = false;
            foreach (KeyValuePair<string, RegionPick> kv in retaliation)
                if (!haveAnswer || kv.Value.Score > answer.Score
                    || (kv.Value.Score == answer.Score
                        && string.CompareOrdinal(kv.Value.Node.Id, answer.Node.Id) < 0))
                { answer = kv.Value; haveAnswer = true; }
            if (haveAnswer)
            {
                // Weariness weighs on the answer itself, not on which place is answered. The score
                // above ranks one attacker's ground above another's and is about them; how spent we
                // are is about us, and it is the quality that competes against everything else this
                // faction could do instead. It is a multiplier with a floor
                // (FactionTuning.WarWearyQualityFloor): a faction ground down by an enemy is exactly
                // the one that must still be able to nominate hitting back.
                double answerFat;
                s.Fatigue.TryGetValue(answer.Node.OwnerFactionTag ?? "", out answerFat);
                AddCampaignCandidate(list, s, t, owned, answer,
                    CampaignObjectiveKind.Terrorise,
                    Clamp01(retaliationQuality * WarWeariness(answerFat, t)));
            }
        }

        /// <summary>Conflict, nomination: which regions are worth a <b>conquest campaign</b> — a
        /// standing decision to take ground, which <c>FleetManager</c> pursues by raiding the region
        /// until a node falls to or under <see cref="FactionTuning.ConquerHoldCeiling"/> and sending a
        /// Conquer at it then. Soften, then finish: the sequence the brain used to arrive at only by
        /// luck, because Raid and Conquer were two independent candidate generators.
        ///
        /// <para>The hold ceiling is therefore <b>not</b> a nomination gate — a region held at full
        /// strength is exactly what a conquest campaign is for — but the weakness term still ranks a
        /// broken region above a healthy one. Every other gate <see cref="AddRaids"/> applies stays,
        /// plus the hostility gate this kind keeps outright: seizing territory is a war act, and a raid
        /// that turns a neighbour hostile is how a predatory faction earns the right to conquer it.
        /// <see cref="Traits.Implacable"/> never conquers — the antagonist looms, it does not settle
        /// (FAC-268).</para></summary>
        private static void AddConquers(List<Candidate> list, FactionState s, Personality p, IWorldView world, FactionTuning t)
        {
            if (world.Influence == null) return;
            if (p.Traits.Implacable) return;   // raids only, never conquest
            List<SpatialNode> owned = world.Graph.OwnedBy(s.Tag).ToList();
            if (owned.Count == 0) return;

            var picks = new Dictionary<string, RegionPick>(StringComparer.Ordinal);
            foreach (NodeBelief belief in KnowledgeGate.Beliefs(world.Graph, s.Tag, s.Knowledge, world.UtcNow, t))
            {
                SpatialNode n = belief.Node;
                string owner = belief.OwnerTag;
                if (owner == null || owner == s.Tag || owner == NeutralFaction.Tag) continue;
                if (s.RelationTo(owner) >= t.HostileRelationThreshold) continue;
                if (Diplomacy.InPactWith(s, owner)) continue;                 // never conquer an ally
                if (s.Proposals.ContainsKey(owner)) continue;                 // suing for peace, not conquering
                double fat; s.Fatigue.TryGetValue(owner, out fat);
                NodeRegion region = NodeRegion.Of(n);
                double reach = world.Influence.SampleAt(n.Id, s.Tag);
                if (reach < t.RaidReachThreshold) continue;
                double weakness = 1.0 - Clamp01(belief.HoldStrength / Math.Max(0.0001, t.ConquerHoldCeiling));
                double score = (0.4 + 0.6 * belief.ResourceValue) * (0.5 + 0.5 * weakness);
                score *= WarWeariness(fat, t);                                // weary discourages; it never forbids
                bool grudge = s.Grudges.ContainsKey(owner);
                if (grudge) score += t.GrudgeRaidQualityBoost;
                // The beachhead's whole purpose: a node in the region it was diverted from outranks an
                // equally weak one elsewhere, so the outpost becomes a way in rather than the goal.
                score += SeizeObjectives.QualityBoost(s, n, t.SeizeObjectiveQualityBoost);
                // A region whose space costs our lanes is worth taking to open them.
                double laneCredits;
                double laneBoost = LaneConquestBoost(s, n, region, t, world.UtcNow, out laneCredits);
                score += laneBoost;
                if (Diplomacy.UnderTruce(s, owner, world.UtcNow)) score *= t.ConquerTruceBreachQualityPenalty;
                if (score <= 0) continue;
                ConsiderRegion(picks, region, n, score, grudge ? CampaignCause.Grudge : CampaignCause.Predation,
                               laneBoost, laneCredits);
            }

            foreach (KeyValuePair<string, RegionPick> kv in picks)
                AddCampaignCandidate(list, s, t, owned, kv.Value,
                    CampaignObjectiveKind.SeizeRegion, Clamp01(0.4 + 0.6 * kv.Value.Score));
        }

        /// <summary>The antagonist's campaign: an implacable faction, on a cadence, nominates a region
        /// of space to terrorise (<see cref="TerrorCampaigns"/>). A <see cref="ActionKind.Campaign"/>
        /// intent whose <see cref="FactionIntent.TargetNodeId"/> is the staging base and whose
        /// <see cref="FactionIntent.TargetRegion"/> is the target; Enact starts the focus. Quality
        /// rides Rivalry, which an implacable faction holds high by construction.</summary>
        private static void AddTerrorCampaign(List<Candidate> list, FactionState s, Personality p, IWorldView world, FactionTuning t)
        {
            if (!p.Traits.Implacable) return;
            if (!TerrorCampaigns.CanStart(s, t, world.UtcNow)) return;
            TerrorCampaigns.Target target = TerrorCampaigns.ChooseTarget(s, world, t);
            if (!target.Found) return;
            var candidate = new Candidate
            {
                Intent = new FactionIntent
                {
                    Kind = ActionKind.Campaign,
                    TargetNodeId = target.StagingNodeId,
                    TargetRegion = target.Region.ToKey(),
                    ObjectiveKind = CampaignObjectiveKind.Terrorise,
                    Cause = CampaignCause.Terror,
                    Cost = t.CampaignCost,
                },
                Quality = Clamp01(0.5 + 0.5 * s.Drives[Drive.Rivalry]),
            };
            MergeCandidate(list, candidate, Drive.Rivalry, Math.Max(0.2, s.Drives[Drive.Rivalry]));
        }

        /// <summary>Diplomacy: when war-weary past the peace threshold, sue for peace with the enemy we're most
        /// exhausted by (a Negotiate overture targeting that faction). Trait-biased toward the un-aggressive (in
        /// scoring) — merchants sue early, raiders only when truly spent. A truce concludes once the other side
        /// is willing too (see <see cref="Diplomacy.ConcludeTruces"/>); free, so a broke faction can still seek it.</summary>
        private static void AddPeaceOvertures(List<Candidate> list, FactionState s, Personality p, IWorldView world, FactionTuning t)
        {
            if (p.Traits.Implacable) return;    // does not sue for peace, and no truce would conclude if it did

            string worst = null; double worstFat = 0;
            foreach (var kv in s.Fatigue)
            {
                if (kv.Value < t.PeaceThreshold) continue;
                if (s.RelationTo(kv.Key) >= 0) continue;                                // any negative standing: cold-war band included
                if (Diplomacy.UnderTruce(s, kv.Key, world.UtcNow)) continue;            // already at peace
                if (kv.Value > worstFat) { worstFat = kv.Value; worst = kv.Key; }
            }
            if (worst == null) return;
            list.Add(new Candidate
            {
                Intent = new FactionIntent { Kind = ActionKind.Negotiate, TargetFactionTag = worst, Cost = t.NegotiateCost },
                Quality = Clamp01(0.45 + 0.55 * worstFat)
            });
        }

        /// <summary>Alliance proposal: when standing with a partner has risen above
        /// <c>AllyRelationThreshold − 0.1</c> and we have no active pact or pending proposal to them,
        /// emit a <see cref="ActionKind.ProposePact"/> candidate. Quality scales with current relation.
        /// A pact concludes once both sides have proposed (bilateral, same pattern as Negotiate/Truce).</summary>
        private static void AddPactProposals(List<Candidate> list, FactionState s, IWorldView world, FactionTuning t)
        {
            if (world.Influence == null) return;
            string bestPartner = null; double bestRel = t.AllyRelationThreshold - 0.1;
            foreach (NodeBelief belief in KnowledgeGate.Beliefs(world.Graph, s.Tag, s.Knowledge, world.UtcNow, t))
            {
                string owner = belief.OwnerTag;
                if (owner == null || owner == s.Tag || owner == NeutralFaction.Tag) continue;
                if (Diplomacy.InPactWith(s, owner)) continue;       // already allied
                if (s.Proposals.ContainsKey(owner)) continue;       // proposal already pending
                double rel = s.RelationTo(owner);
                if (rel > bestRel) { bestRel = rel; bestPartner = owner; }
            }
            if (bestPartner == null) return;
            list.Add(new Candidate
            {
                Intent = new FactionIntent { Kind = ActionKind.ProposePact, TargetFactionTag = bestPartner, Cost = t.NegotiateCost },
                Quality = Clamp01(0.5 + 0.5 * (bestRel - (t.AllyRelationThreshold - 0.1)))
            });
        }

        /// <summary>Olive-branch action: send a valuable gift convoy to the neighbour we most need to
        /// appease — the one we view most negatively while still in the cold-war band (not yet open war).
        /// On arrival, the RECIPIENT's standing toward the sender lifts by <see cref="FactionTuning.GiftRelationBoost"/>.
        /// Only generated under Security/Caution drives; trait-biased heavily against aggressors.</summary>
        private static void AddGiftOffers(List<Candidate> list, FactionState s, IWorldView world, FactionTuning t)
        {
            if (world.Influence == null) return;
            string bestTarget = null;
            double bestBadBlood = 0;
            foreach (NodeBelief belief in KnowledgeGate.Beliefs(world.Graph, s.Tag, s.Knowledge, world.UtcNow, t))
            {
                string owner = belief.OwnerTag;
                if (owner == null || owner == s.Tag || owner == NeutralFaction.Tag) continue;
                if (Diplomacy.UnderTruce(s, owner, world.UtcNow)) continue;         // already at peace
                if (s.Grudges.ContainsKey(owner)) continue;                          // P9: no olive branch for known treaty-breakers
                double rel = s.RelationTo(owner);
                if (rel >= -0.05) continue;                                          // relations fine — no gesture needed
                if (rel < t.HostileRelationThreshold) continue;                      // open war — Negotiate/Raid instead
                double badBlood = -rel;                                              // 0.05..0.3
                if (badBlood > bestBadBlood) { bestBadBlood = badBlood; bestTarget = owner; }
            }
            if (bestTarget == null) return;
            // Quality scales with how strained relations are — the closer to war, the more urgent the gesture.
            double quality = Clamp01(0.3 + 0.7 * bestBadBlood / 0.25);
            list.Add(new Candidate
            {
                Intent = new FactionIntent { Kind = ActionKind.Gift, TargetFactionTag = bestTarget, Cost = t.GiftCost },
                Quality = quality
            });
        }

        /// <summary>Compute and store the trade-partner whitelist for TradeManager to consume: which
        /// factions are non-hostile, un-embargoed and within reach. Accumulates
        /// (reach × resourceValue × relFactor) per partner across their reachable nodes and sorts
        /// descending, so the score only orders partners when several could serve one demand row --
        /// it no longer multiplies anything, because there is no route to score. Called once per
        /// Think() cycle; result lives on <see cref="FactionState.TradePriority"/>.
        /// See docs/trade-haul-shaped.md.</summary>
        /// <summary>Ranks the partners this faction could trade with, into
        /// <see cref="FactionState.TradePriority"/>. Reach x resource value x relation, summed per
        /// owner over the nodes this faction believes in.
        ///
        /// <para><b>Reads no brain state, which is why a player faction can use it.</b> There is no
        /// personality parameter: the inputs are world influence, the faction's own
        /// <see cref="FactionState.Knowledge"/>, its relations, its embargo set and tuning, and a
        /// player faction has all five. <see cref="FactionLifecycle.ProjectPlayerTradePriority"/>
        /// calls it for exactly that reason, the way
        /// <see cref="FactionLifecycle.ProjectPlayerCampaignCandidates"/> calls
        /// <see cref="RankCampaignCandidates"/>.</para>
        ///
        /// <para><paramref name="manageEmbargoes"/> is the one thing that differs between the two
        /// callers. For an NPC this function OWNS the embargo set, auto-adding a partner whose
        /// relation falls into the cold-war band and lifting it once recovered. A player's set is
        /// theirs, so the player path passes false and only reads it -- otherwise an embargo the
        /// player placed by hand would be lifted again on the next pass.</para></summary>
        internal static void ComputeTradePriority(FactionState s, IWorldView world, FactionTuning t,
            bool manageEmbargoes)
        {
            s.TradePriority.Clear();
            if (world.Influence == null) return;

            var magnitudes = new Dictionary<string, double>();
            foreach (NodeBelief belief in KnowledgeGate.Beliefs(world.Graph, s.Tag, s.Knowledge, world.UtcNow, t))
            {
                SpatialNode n = belief.Node;
                string owner = belief.OwnerTag;
                if (owner == null || owner == s.Tag || owner == NeutralFaction.Tag) continue;
                double rel = s.RelationTo(owner);
                // P10: auto-manage embargo — add when relation falls below the embargo line, lift when
                // recovered. Ahead of the hostile check, and with no lower bound on the add: the
                // embargo is read by the PARTNER too (Diplomacy.TradeBarred), so a relation that fell
                // straight through the cold-war band into open war must still place one, and one
                // placed earlier must still be managed rather than frozen behind the skip below.
                if (manageEmbargoes && t.EmbargoRelationThreshold < 0)
                {
                    if (rel < t.EmbargoRelationThreshold)
                        s.Embargoes.Add(owner);
                    else if (rel > t.EmbargoRelationThreshold + t.EmbargoHysteresis)
                        s.Embargoes.Remove(owner);
                }
                if (rel < t.HostileRelationThreshold) continue;
                if (s.Embargoes.Contains(owner)) continue;                           // P10: skip embargoed factions
                double reach = world.Influence.SampleAt(n.Id, s.Tag);
                if (reach < t.TradeReachThreshold) continue;
                double relFactor = Clamp01(0.5 + 0.5 * rel);
                double contrib = reach * belief.ResourceValue * relFactor;
                double cur;
                magnitudes[owner] = magnitudes.TryGetValue(owner, out cur) ? cur + contrib : contrib;
            }

            foreach (var kv in magnitudes)
                if (kv.Value > 0)
                    s.TradePriority.Add(new TradePriorityEntry { PartnerTag = kv.Key, Score = kv.Value });

            s.TradePriority.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        /// <summary>Compute and store the ranked owned-node reinforcement list for FleetManager to
        /// consume. Scores node value plus urgency so the manager can do a cheap ordered walk each tick.
        /// Called once per Think() cycle; result lives on <see cref="FactionState.ReinforcePriority"/>.</summary>
        private static void ComputeReinforcePriority(FactionState s, IWorldView world, FactionTuning t)
        {
            s.ReinforcePriority.Clear();

            const double belowThresholdUrgencyWeight = 10.0;
            const double resourceWeight = 5.0;
            const double capabilityWeight = 0.75;
            const double campaignFocusBonus = 1.5;

            foreach (SpatialNode node in world.Graph.OwnedBy(s.Tag))
            {
                double capabilityLevels = 0;
                foreach (int level in node.Capabilities.Values)
                    capabilityLevels += level;

                double urgency = node.HoldStrength < t.HoldMaintenanceThreshold
                    ? (t.HoldMaintenanceThreshold - node.HoldStrength) * belowThresholdUrgencyWeight
                    : 0;
                double campaignBonus = s.CampaignFocusRegionMembers.Contains(node.Id) ? campaignFocusBonus : 0;
                double score = urgency + (node.ResourceValue * resourceWeight) + (capabilityLevels * capabilityWeight) + campaignBonus;

                s.ReinforcePriority.Add(new ReinforcePriorityEntry
                {
                    NodeId = node.Id,
                    Score = score
                });
            }

            s.ReinforcePriority.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        /// <summary>
        /// Rank, for every active offensive campaign objective, the nodes worth attacking under it —
        /// best first, into <see cref="CampaignObjective.Candidates"/>, which <c>FleetManager</c>'s
        /// offensive block reads and never writes. The same brain-writes / manager-reads contract as
        /// <see cref="ComputeReinforcePriority"/>, recomputed fresh every <c>Think()</c> and never
        /// persisted.
        ///
        /// <para><b>This list is the reason the manager does not scan the graph.</b> Deciding which
        /// node of a region is worth hitting means <see cref="KnowledgeGate.Beliefs"/> over the graph
        /// plus every diplomatic rule — far too much for a per-tick manager pass (rule 5 in
        /// CLAUDE.md). The brain already walks that ground; this caches the ordering it produces.</para>
        ///
        /// <para><b>No influence-reach gate here.</b> A campaign's region is reachable <i>by decision</i>
        /// — the nomination applied the reach gate, and a deep-space faction projects nothing onto a
        /// planet it has already committed to attacking (the rule <c>TerrorCampaigns</c> shipped with).
        /// A conquest keeps the hostility and fatigue gates, because taking ground is a war act.</para>
        /// </summary>
        private static void ComputeCampaignCandidates(FactionState s, IWorldView world, FactionTuning t)
        {
            List<OffensiveCampaigns.Entry> active = OffensiveCampaigns.Active(s);
            if (active.Count == 0) return;
            foreach (OffensiveCampaigns.Entry entry in active)
                RankCampaignCandidates(s, entry.Objective, entry.Focus != null && entry.Focus.PlayerIssued, world, t);
        }

        /// <summary>Rank one offensive objective's targets into its
        /// <see cref="CampaignObjective.Candidates"/>, clearing whatever was there. The one ranking
        /// formula: value, weakness, the grudge boost and the two objective boosts, ordered
        /// deterministically.
        ///
        /// <para>Public because a <b>player</b> faction never thinks, and its directives still need a
        /// list — <see cref="FactionLifecycle.ProjectPlayerCampaignCandidates"/> calls this from the
        /// manager tick. A second copy of the formula on that side is exactly the drift this shares
        /// one body to avoid.</para>
        ///
        /// <para><paramref name="playerIssued"/> selects which admission gate a target passes; see
        /// <see cref="OffensiveCampaigns.IsCampaignTarget"/> for why a directive's is the narrower
        /// one. The war-fatigue gate a conquest applies stays on both: a faction too exhausted to
        /// fight is exhausted whoever gave the order.</para></summary>
        public static void RankCampaignCandidates(FactionState s, CampaignObjective objective,
            bool playerIssued, IWorldView world, FactionTuning t)
        {
            if (s == null || objective == null || world == null || t == null) return;
            objective.Candidates.Clear();
            bool conquest = objective.Kind == CampaignObjectiveKind.SeizeRegion;
            var ranked = new List<KeyValuePair<string, double>>();
            foreach (NodeBelief belief in KnowledgeGate.Beliefs(world.Graph, s.Tag, s.Knowledge, world.UtcNow, t))
            {
                SpatialNode n = belief.Node;
                if (!OffensiveCampaigns.Advances(objective, n, s.Tag)) continue;
                if (!OffensiveCampaigns.IsCampaignTarget(s, n, t, world.UtcNow, playerIssued)) continue;
                string owner = belief.OwnerTag;
                if (owner == null) continue;
                if (conquest)
                {
                    double fat; s.Fatigue.TryGetValue(owner, out fat);
                    if (fat >= 1.0) continue;
                }
                double score = (0.4 + 0.6 * belief.ResourceValue) * (1.0 - 0.5 * belief.HoldStrength);
                if (s.Grudges.ContainsKey(owner)) score += t.GrudgeRaidQualityBoost;
                score += SeizeObjectives.QualityBoost(s, n, t.SeizeObjectiveQualityBoost);
                score += TerrorCampaigns.QualityBoost(s, n, t.SeizeObjectiveQualityBoost);
                ranked.Add(new KeyValuePair<string, double>(n.Id, score));
            }
            // Deterministic: score descending, node id ascending, so the same world always hands
            // the manager the same order and a test can name the node it expects.
            ranked.Sort((a, b) =>
            {
                int byScore = b.Value.CompareTo(a.Value);
                return byScore != 0 ? byScore : string.CompareOrdinal(a.Key, b.Key);
            });
            foreach (KeyValuePair<string, double> kv in ranked) objective.Candidates.Add(kv.Key);
        }

        /// <summary>Caches, per owned node, just the personality-driven delta term from
        /// <see cref="PerceivedThreat"/> — <c>Xenophobia × ForeignNeutralPresence</c> — not the full
        /// formula, since <see cref="PerceivedThreat"/> already folds in raw <see
        /// cref="SpatialNode.Threat"/> and FleetManager has its own copy of that. FleetManager applies
        /// this bonus only to nodes within the active Campaign focus region, letting the same
        /// perception that picked the region also bias which node the operational Patrol dispatch
        /// actually targets. Recomputed fresh every Think() cycle, same as <see
        /// cref="ComputeReinforcePriority"/> — brain writes, FleetManager reads, never persisted.</summary>
        private static void ComputePerceivedThreatBonus(FactionState s, Personality p, IWorldView world)
        {
            s.PerceivedThreatBonusByNode.Clear();
            foreach (SpatialNode node in world.Graph.OwnedBy(s.Tag))
                s.PerceivedThreatBonusByNode[node.Id] = p.Traits.Xenophobia * ForeignNeutralPresence(node, s);
        }

        /// <summary>Propose founding a new SE station on the best owned brain-only node (N###) whose hold
        /// has consolidated. Any held claim qualifies, whatever its ground is worth: a station is what
        /// makes a node take a siege rather than a presence raid to lose. Worth, plus
        /// <see cref="FactionTuning.FoundRecruitingBodyBonus"/> on a body that recruits, decides which
        /// claim goes first and how strongly. One candidate per think cycle.</summary>
        private static void AddFoundingCandidates(List<Candidate> list, FactionState s, IWorldView world, FactionTuning t)
        {
            SpatialNode best = null;
            double bestWorth = 0;
            foreach (SpatialNode n in world.Graph.OwnedBy(s.Tag))
            {
                if (n.StableStationId != 0) continue;             // already has an SE station
                if (SourceMapping.IsScanBacked(n.Id)) continue;   // E### / G### — not brain-owned
                if (n.HoldStrength < t.FoundHoldThreshold) continue; // must consolidate before committing station capital
                double worth = FoundingWorth(n, world, t);
                if (best == null || worth > bestWorth) { best = n; bestWorth = worth; }
            }
            if (best != null)
                list.Add(Make(ActionKind.Found, best.Id, t.FoundCost, Clamp01(0.5 + 0.5 * bestWorth)));
        }

        /// <summary>A claim's case for a station: its Worth, plus
        /// <see cref="FactionTuning.FoundRecruitingBodyBonus"/> when its body recruits.</summary>
        public static double FoundingWorth(SpatialNode n, IWorldView world, FactionTuning t)
        {
            double worth = n.ResourceValue;
            if (WorkerRecruitValidator.HasCivilianPool(n, world.Planets)) worth += t.FoundRecruitingBodyBonus;
            return worth;
        }

        /// <summary>P1: Build-completion urgency. When a ship build at a Shipyard node has collected
        /// ≥ BuildCompletionUrgencyFraction of its BOM, inject a high-quality FinishBuild candidate
        /// that competes even under Security/Gift pressure, preventing a nearly-done ship from being
        /// abandoned because a war broke out.</summary>
        private static void AddFinishBuildCandidates(List<Candidate> list, FactionState s, IWorldView world, FactionTuning t)
        {
            foreach (SpatialNode n in world.Graph.OwnedBy(s.Tag))
            {
                foreach (BuildQueueEntry entry in n.BuildQueue)
                {
                    if (entry.Bom.Count == 0) continue;
                    long totalBom = 0, totalCollected = 0;
                    foreach (var kv in entry.Bom) totalBom += kv.Value;
                    foreach (var kv in entry.Collected) totalCollected += kv.Value;
                    if (totalBom == 0) continue;
                    if ((double)totalCollected / totalBom < t.BuildCompletionUrgencyFraction) continue;
                    // Costed so force-consolidation stays an emergency override, not a routine
                    // shortcut around Haul-based logistics -- scales with build size so a large
                    // stalled warship costs more to force through than a small drone.
                    long cost = (long)(totalBom * t.FinishBuildCostPerBomUnit);
                    Candidate c = Make(ActionKind.FinishBuild, n.Id, cost, 0.85);
                    c.SourceDrive    = Drive.Security;
                    c.Support        = 1.0;
                    c.SourceStrength = 1.0;
                    list.Add(c);
                    return; // one candidate per think cycle — the most urgent build
                }
            }
        }

        // ---- score: utility = need × afford × trait-bias × quality × cooldown-gate --------------
        private static void Score(List<Candidate> cands, FactionState s, Personality p, IWorldView world, FactionTuning t)
        {
            long treasury = world.GetTreasury(s.Tag);
            foreach (Candidate c in cands)
            {
                if (c.Intent.Kind == ActionKind.Idle)
                {
                    c.Afford = c.Bias = c.Quality = c.Cooldown = c.Repetition = 1;
                    // P11: boost Idle when all drives are genuinely quiet — prevents micro-action
                    // spam during peacetime from stable, well-funded factions.
                    bool allStable = t.StabilityThreshold > 0
                        && s.Drives[Drive.Security] < t.StabilityThreshold
                        && s.Drives[Drive.Growth]   < t.StabilityThreshold
                        && s.Drives[Drive.Wealth]   < t.StabilityThreshold
                        && s.Drives[Drive.Rivalry]  < t.StabilityThreshold
                        && s.Drives[Drive.Caution]  < t.StabilityThreshold;
                    c.Utility = allStable ? t.IdleUtility * t.StabilityIdleMultiplier : t.IdleUtility;
                    continue;
                }
                c.Need = c.Support;
                c.Afford = Afford(treasury, c.Intent.Cost, t);
                // Item-cost gate: multiply Afford by 0 when faction can't cover the item cost.
                if (c.Afford > 0) c.Afford *= ItemAfford(c.Intent, s.Tag, world, t);
                c.Bias = TraitBias(c.Intent, p);

                if (c.Intent.Kind == ActionKind.Gift)
                {
                    string dest = c.Intent.TargetFactionTag;
                    // Gift shares TradeDestCooldowns with Trade — sending a gift blocks the trade lane
                    // to that partner until the dest cooldown expires, preventing a flood of convoys.
                    bool blocked = OnCooldown(s, ActionKind.Gift, world.UtcNow)
                                || OnDestCooldown(s, dest, world.UtcNow);
                    c.Cooldown = blocked ? 0 : 1;
                }
                else
                {
                    // Raid/Guard/Patrol/Retaliate/Conquer concurrency used to be additionally capped
                    // at one in-flight fleet per kind (HasInFlightFleet) — removed: ship-stock honestly
                    // depletes per dispatch now (see OffensiveDispatch.CheckAndConsumeShipStock), so the
                    // HasShipStockFor gate below is sufficient on its own to limit how many fleets
                    // a faction can actually sustain concurrently, the same way Trade is limited by
                    // MaxTradeFleets/stock rather than a hardcoded uniqueness rule.
                    c.Cooldown = OnCooldown(s, c.Intent.Kind, world.UtcNow) ? 0 : 1;
                }

                // Ship-stock gate: zero utility for actions that require a physical ship when
                // no ship of the required role is available anywhere in the faction's network —
                // and, when the candidate names a target node, no such stock can actually survive
                // the trip there (route-aware since the thrust-aware ship-selection work). This
                // prevents the brain from winning the draw on Gift/Raid/Patrol/Guard and then
                // silently aborting in Enact every tick, for either reason.
                // Negotiate is deliberately excluded — it is a radio call, not a dispatch.
                if (c.Afford > 0 && ShipPool.RequiresShip(c.Intent.Kind)
                    && !world.HasShipStockFor(s.Tag, c.Intent.Kind, c.Intent.TargetNodeId))
                    c.Afford = 0;

                if (c.Quality <= 0) c.Quality = 0.5;
                c.Repetition = RepetitionGate(s, c.Intent.Kind);
                c.Utility = c.Need * c.Afford * c.Bias * c.Quality * c.Cooldown * c.Repetition;
            }
        }

        private static double RepetitionGate(FactionState s, ActionKind kind)
        {
            double count;
            if (!s.ActionRepetition.TryGetValue(kind, out count) || count <= 0) return 1.0;
            return 1.0 / (1.0 + ActionRepetitionPenaltyScale * count);
        }

        private static void RecordActionRepetition(FactionState s, ActionKind chosen)
        {
            var keys = s.ActionRepetition.Keys.ToList();
            foreach (ActionKind kind in keys)
            {
                if (kind == chosen && chosen != ActionKind.Idle) continue;
                double decayed = s.ActionRepetition[kind] * ActionRepetitionDecay;
                if (decayed < ActionRepetitionPruneBelow) s.ActionRepetition.Remove(kind);
                else s.ActionRepetition[kind] = decayed;
            }

            if (chosen == ActionKind.Idle) return;

            double current;
            s.ActionRepetition.TryGetValue(chosen, out current);
            s.ActionRepetition[chosen] = current + 1.0;
        }

        /// <summary>Returns 1 if the faction can cover the item costs for the given intent, 0 otherwise.
        /// Only Expand, Develop, and Found have item costs; all other actions return 1.</summary>
        private static double ItemAfford(FactionIntent intent, string tag, IWorldView world, FactionTuning t)
        {
            switch (intent.Kind)
            {
                case ActionKind.Expand:
                    return world.GetInventory(tag, ItemKind.ProcessedGood) >= t.ExpandItemCostProcessedGood ? 1 : 0;
                case ActionKind.Develop:
                    if (!intent.TargetCapability.HasValue) return 0;
                    NodeCapabilityKind cap = intent.TargetCapability.Value;
                    Dictionary<string, long> bom;
                    if (t.TryGetInstallBom(cap, out bom))
                    {
                        // BOM-backed capability: every specific component the real block needs must
                        // be on hand (normal + reserved), not just an aggregate ProcessedGood total.
                        foreach (var kv in bom)
                        {
                            long have = world.GetFactionInventoryByKey(tag, kv.Key)
                                      + world.GetFactionReservedInventoryByKey(tag, kv.Key);
                            if (have < kv.Value) return 0;
                        }
                        return 1;
                    }
                    long oreNeed   = t.InstallCostFor(cap, ItemKind.Ore);
                    long ingotNeed = t.InstallCostFor(cap, ItemKind.Ingot);
                    long pgNeed    = t.InstallCostFor(cap, ItemKind.ProcessedGood);
                    long h2Need    = t.InstallCostFor(cap, ItemKind.Hydrogen);
                    return (oreNeed   == 0 || world.GetInventory(tag, ItemKind.Ore)          + world.GetReservedInventory(tag, ItemKind.Ore)          >= oreNeed)
                        && (ingotNeed == 0 || world.GetInventory(tag, ItemKind.Ingot)        + world.GetReservedInventory(tag, ItemKind.Ingot)        >= ingotNeed)
                        && (pgNeed    == 0 || world.GetInventory(tag, ItemKind.ProcessedGood) + world.GetReservedInventory(tag, ItemKind.ProcessedGood) >= pgNeed)
                        && (h2Need    == 0 || world.GetInventory(tag, ItemKind.Hydrogen)      + world.GetReservedInventory(tag, ItemKind.Hydrogen)      >= h2Need) ? 1 : 0;
                case ActionKind.Found:
                    // H2 requirement removed until the FuelDepot/IceField gas pipeline is live.
                    return world.GetInventory(tag, ItemKind.ProcessedGood) >= t.FoundItemCostProcessedGood ? 1 : 0;
                default: return 1;
            }
        }

        private static double Afford(long treasury, long cost, FactionTuning t)
        {
            if (cost <= 0) return 1;
            if (cost > treasury) return 0;                                   // can't afford -> dropped
            return Clamp01((double)(treasury - cost) / Math.Max(1, t.ReserveTreasury)); // discourage draining the reserve
        }

        private static bool OnCooldown(FactionState s, ActionKind k, DateTime now)
        {
            DateTime until;
            return s.Cooldowns.TryGetValue(k, out until) && now < until;
        }

        private static bool OnDestCooldown(FactionState s, string destTag, DateTime now)
        {
            DateTime until;
            return destTag != null && s.TradeDestCooldowns.TryGetValue(destTag, out until) && now < until;
        }

        /// <summary>Personality weight for one candidate. A <see cref="ActionKind.Campaign"/> is
        /// biased by <b>what it means to do</b>, not by the verb: an offensive campaign carries the
        /// Raid or Conquer aggression curve, because the decision to mount one is the decision those
        /// two used to be. A defensive campaign keeps the mixed aggression/mercantilism weight it has
        /// always had.</summary>
        private static double TraitBias(FactionIntent intent, Personality p)
        {
            if (intent.Kind == ActionKind.Campaign && intent.ObjectiveKind.HasValue)
            {
                if (intent.ObjectiveKind.Value == CampaignObjectiveKind.SeizeRegion)
                    return TraitBias(ActionKind.Conquer, p);
                if (intent.Cause.HasValue && intent.Cause.Value == CampaignCause.Retaliation)
                    return TraitBias(ActionKind.Retaliate, p);
                return TraitBias(ActionKind.Raid, p);
            }
            return TraitBias(intent.Kind, p);
        }

        private static double TraitBias(ActionKind k, Personality p)
        {
            switch (k)
            {
                case ActionKind.Expand: return 0.4 + 0.6 * p.Traits.Expansionism;
                case ActionKind.Claim: return 0.5 + 0.5 * p.Traits.Expansionism;
                case ActionKind.Guard:  return 0.7;
                // Patrol bias removed: Patrol is now operational (FleetManager), not strategic
                case ActionKind.Withdraw: return 0.5 + 0.5 * (1 - p.Traits.Aggression);
                case ActionKind.Develop: return 0.45 + 0.55 * p.Traits.Mercantilism;
                case ActionKind.Raid: return 0.3 + 0.7 * p.Traits.Aggression;
                case ActionKind.Retaliate: return 0.4 + 0.6 * p.Traits.Aggression;
                case ActionKind.Conquer: return 0.2 + 0.8 * p.Traits.Aggression;   // steeper than Raid: a bigger
                                                                                     // commitment low-aggression factions
                                                                                     // almost never choose it (floor 0.2 vs
                                                                                     // Raid's 0.3) but a maxed-aggression
                                                                                     // faction pursues it as readily (ceiling 1.0)
                case ActionKind.Negotiate:    return 0.3 + 0.7 * (1 - p.Traits.Aggression); // peaceful factions sue for peace readily
                case ActionKind.ProposePact: return 0.2 + 0.5 * p.Traits.Mercantilism + 0.3 * (1 - p.Traits.Aggression); // merchants & settlers formalise; raiders resist
                case ActionKind.Trade: return 0.3 + 0.7 * p.Traits.Mercantilism;          // merchants trade readily; raiders rarely bother
                case ActionKind.Found: return 0.3 + 0.7 * p.Traits.Expansionism;         // expansionist factions plant stations readily
                case ActionKind.Gift: return 0.2 + 0.8 * (1.0 - p.Traits.Aggression);    // peaceable factions send reparations; raiders never bother
                case ActionKind.FinishBuild: return 0.6 + 0.4 * p.Traits.Aggression;    // aggressive factions are more motivated to finish combat ships
                case ActionKind.Campaign: return 0.4 + 0.3 * p.Traits.Aggression + 0.3 * p.Traits.Mercantilism; // campaign focus weighted by aggression (security) and mercantilism (resource allocation)
                default: return 0.5;
            }
        }

        // ---- select: softmax over the top-N utilities, sampled with the seeded RNG --------------
        private static Candidate Select(List<Candidate> candidates, double temperature, int topN, IRandom rng)
        {
            List<Candidate> viable = candidates.Where(c => c.Utility > 0)
                                               .OrderByDescending(c => c.Utility)
                                               .Take(Math.Max(1, topN)).ToList();
            if (viable.Count == 1) return viable[0];

            double temp = temperature <= 0 ? 0.0001 : temperature;
            double max = viable.Max(c => c.Utility);
            var weights = new double[viable.Count];
            double sum = 0;
            for (int i = 0; i < viable.Count; i++)
            {
                weights[i] = Math.Exp((viable[i].Utility - max) / temp);
                sum += weights[i];
            }
            double roll = rng.NextDouble() * sum;
            double acc = 0;
            for (int i = 0; i < viable.Count; i++)
            {
                acc += weights[i];
                if (roll <= acc) return viable[i];
            }
            return viable[viable.Count - 1];
        }

        /// <summary>Readiness [0..1]: the fraction of Trade, Raid and Patrol the faction owns a suitable
        /// hull for anywhere in its estate. 0 = no hull for any of them; 1 = all three.
        ///
        /// <para>Asked through <see cref="IWorldView.HasShipStockFor"/>, the question the fleet gate in
        /// <c>Score</c> asks, because <see cref="SpatialNode.ShipStock"/> is keyed by prefab id: a hull
        /// answers a role by what it carries (<see cref="ShipPool.IsSuitableFor"/>), not by a name.</para></summary>
        private static double FactionFleetReadiness(FactionState s, IWorldView world)
        {
            int ready = 0;
            if (world.HasShipStockFor(s.Tag, ActionKind.Trade)) ready++;
            if (world.HasShipStockFor(s.Tag, ActionKind.Raid)) ready++;
            if (world.HasShipStockFor(s.Tag, ActionKind.Patrol)) ready++;
            return ready / 3.0;
        }


        private static string Explain(Drive dominantDrive, FactionState s, Candidate c)
        {
            if (c.Intent.Kind == ActionKind.Idle)
                return c.SourceDrive + " -> Idle (u=" + c.Utility.ToString("0.00") + ", dominant " + dominantDrive + ")  [" + s.Drives + "]";
            return c.SourceDrive + " -> " + c.Intent + " u=" + c.Utility.ToString("0.00") +
                   " (dominant " + dominantDrive + ")" +
                   " [need " + c.Need.ToString("0.00") + " × afford " + c.Afford.ToString("0.00") +
                   " × bias " + c.Bias.ToString("0.00") + " × q " + c.Quality.ToString("0.00") +
                   " × rep " + c.Repetition.ToString("0.00") + "]";
        }

        /// <summary>Verbose, single-line decision dump for live log diagnostics. Includes the selected
        /// intent, current drives, and scored candidates with every factor in the utility product.</summary>
        public static string FormatDecisionDebug(FactionState s, Decision d, int maxCandidates = 25)
        {
            var sb = new StringBuilder();
            string tag = s != null ? s.Tag : "?";
            FactionIntent chosen = d != null ? d.Intent : new FactionIntent { Kind = ActionKind.Idle };
            string chosenText = chosen.ToString();
            List<Candidate> scored = d != null && d.Scored != null
                ? d.Scored.OrderByDescending(c => c.Utility).ToList()
                : new List<Candidate>();
            int shown = Math.Max(0, Math.Min(maxCandidates, scored.Count));

            sb.Append("brain-debug ").Append(tag)
              .Append(" chosen=").Append(d != null ? d.Goal.ToString() : Drive.Caution.ToString()).Append(" -> ").Append(chosenText)
              .Append(" explanation=\"").Append(d != null ? d.Explanation : "").Append("\"")
              .Append(" drives=").Append(s != null ? s.Drives.ToString() : "?")
              .Append(" candidates=").Append(scored.Count)
              .Append(" showing=").Append(shown);

            for (int i = 0; i < shown; i++)
            {
                Candidate c = scored[i];
                bool isChosen = c.Intent.ToString() == chosenText && c.SourceDrive == (d != null ? d.Goal : c.SourceDrive);
                sb.Append(" | ")
                  .Append(isChosen ? "* " : "  ")
                  .Append("#").Append(i + 1).Append(" ")
                  .Append(c.SourceDrive).Append(" ")
                  .Append(c.Intent)
                  .Append(" cost=").Append(c.Intent.Cost)
                  .Append(" support=").Append(F3(c.Support))
                  .Append(" source=").Append(F3(c.SourceStrength))
                  .Append(" need=").Append(F3(c.Need))
                  .Append(" afford=").Append(F3(c.Afford))
                  .Append(" bias=").Append(F3(c.Bias))
                  .Append(" quality=").Append(F3(c.Quality))
                  .Append(" cooldown=").Append(F3(c.Cooldown))
                  .Append(" repetition=").Append(F3(c.Repetition))
                  .Append(" utility=").Append(F3(c.Utility));
            }
            return sb.ToString();
        }

        private static string F3(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
