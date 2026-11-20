using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Xunit;
using static Factions.Core.Tests.ArmedYard;

namespace Factions.Core.Tests
{
    /// <summary>FAC-376: a campaign records WHAT came back, not only that something was attempted.
    /// The tempo stamp is written by a refusal exactly as it is by a dispatch — correctly, since a
    /// refusal decides — so on its own it cannot separate a campaign refusing thirteen times running
    /// from one striking once a minute.
    ///
    /// <para>Live 2026-09-21: EXP's conquest of N873 refused 13 times in 13 minutes and lost 57
    /// intercepts, and every one of them rendered as "Last action: 0s ago".</para></summary>
    public class CampaignOutcomeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

        // ---- the deciding bracket ---------------------------------------------------------------

        /// <summary>The bracket reported is the one the defence most out-reaches, not the loudest
        /// and not the last. A defence firing at 2000 m against hulls that reach 800 decides the
        /// fight on turn 1, which is the whole of FAC-375's finding and the only part a player can
        /// act on.</summary>
        [Fact]
        public void WorstBracket_IsTheTurnTheDefenceMostOutReaches()
        {
            FactionTuning tuning = TestSupport.Tuning();   // turns 3, brackets 2000 / 800 / 0
            var attack = new List<WeaponBand> { new WeaponBand { Dps = 1000, RangeM = 800 } };
            var defence = new List<WeaponBand> { new WeaponBand { Dps = 60000, RangeM = 2000 } };

            CombatStrength.DuelBracket worst = CombatStrength.WorstBracket(attack, defence, null, 0, tuning);

            Assert.Equal(2000, worst.RangeM, 6);
            Assert.Equal(0, worst.AttackerDps, 6);          // nothing the squadron carries reaches
            Assert.Equal(60000, worst.DefenderDps, 6);
            Assert.False(worst.IsNone);
        }

        /// <summary>...and when reach is even it falls back to the bracket where the raw gap is
        /// widest, so the field is never empty for a fight that happened. Without this arm the test
        /// above passes on an implementation that only ever answers the first bracket.</summary>
        [Fact]
        public void WorstBracket_FallsToTheWidestGapWhenReachIsEven()
        {
            FactionTuning tuning = TestSupport.Tuning();
            var attack = new List<WeaponBand> { new WeaponBand { Dps = 500, RangeM = 2000 } };
            var defence = new List<WeaponBand>
            {
                new WeaponBand { Dps = 100, RangeM = 2000 },    // reaches every turn
                new WeaponBand { Dps = 9000, RangeM = 800 },    // only from 800 m in
            };

            CombatStrength.DuelBracket worst = CombatStrength.WorstBracket(attack, defence, null, 0, tuning);

            Assert.Equal(800, worst.RangeM, 6);             // first bracket where the 9000 band acts
            Assert.Equal(500, worst.AttackerDps, 6);
            Assert.Equal(9100, worst.DefenderDps, 6);
        }

        /// <summary>The station's own turrets and its flat floor are counted — the node overload
        /// takes what <see cref="CombatStrength.Survives"/> takes, so a gate reports in the units it
        /// refused in rather than in a second, adjacent reading.</summary>
        [Fact]
        public void WorstBracketAt_CountsTheNodesOwnGuns()
        {
            FactionTuning tuning = TestSupport.Tuning();
            WorldModel world = TestSupport.World();
            SpatialNode node = world.Graph.Add(new Vec3(0, 0, 0), 0.6, "MARK");
            node.StationPrefabId = "Outpost";
            node.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 4000, RangeM = 2000 } };
            var attack = new List<WeaponBand> { new WeaponBand { Dps = 1000, RangeM = 800 } };

            CombatStrength.DuelBracket worst = CombatStrength.WorstBracketAt(attack, null, node, tuning);

            Assert.Equal(2000, worst.RangeM, 6);
            Assert.Equal(4000 + tuning.StationDefensePower, worst.DefenderDps, 6);
        }

        // ---- refusal -----------------------------------------------------------------------------

        /// <summary>A refused dispatch stamps the outcome beside the tempo stamp it already wrote,
        /// with the range that decided it. The two stamps carry the same instant here — a refusal is
        /// decided where it is attempted — and that is exactly why one field could not say which
        /// happened.</summary>
        [Fact]
        public void ARefusedDispatch_RecordsTheRefusalAndTheDecidingBracket()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 60000, defenceRangeM: 2000);
            origin.ShipStock["Fighter"] = 1;                 // 1000 Dps at 800 m, against 60,000 at 2000 m

            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            // The station cannot move, so the raid sets the range: it closes to its own 800 m, and
            // the station's 60,000 still reaches it there. That is the bracket the duel would fight.
            CampaignObjective objective = st.CampaignFoci[0].Objectives[0];
            Assert.Equal(CampaignOutcome.Refused, objective.LastOutcome);
            Assert.Equal(T0, objective.LastOutcomeUtc);
            Assert.Equal(T0, objective.LastDispatchUtc);
            Assert.Equal(800, objective.LastOutcomeRangeM, 6);
            Assert.Equal(1000, objective.LastOutcomeOwnDps, 6);
            Assert.Equal(60000, objective.LastOutcomeOpposedDps, 6);
            Assert.Equal(0, objective.LastOutcomeHullsTotal);   // a refusal spends no hull
            Assert.Equal(0, objective.LastOutcomeHullsLost);
        }

        /// <summary>The log line an operator reads carries the same bracket, so the server-side
        /// reading and the player-side one cannot disagree.</summary>
        [Fact]
        public void ARefusedDispatch_NamesTheDecidingBracketInTheLog()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 60000, defenceRangeM: 2000);
            origin.ShipStock["Fighter"] = 1;

            var log = new List<string>();
            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, log);

            Assert.Contains(log, l => l.Contains("RAID_REFUSED")
                                      && l.Contains("lostAtM=800")
                                      && l.Contains("lostAtOwn=1000")
                                      && l.Contains("lostAtOpposed=60000"));
        }

        /// <summary>A dispatch that goes ahead records no outcome yet — it has not had one — and
        /// stamps the fleet that will bring one back. Without this arm every test here passes on an
        /// implementation that marks every attempt refused.</summary>
        [Fact]
        public void ADispatchThatFlies_RecordsNoOutcomeYet_ButNamesItsFleet()
        {
            SpatialNode origin, target; FactionState st;
            WorldModel world = Campaign(out origin, out target, out st, defenceDps: 1000);
            origin.ShipStock["Fighter"] = 30;

            new FleetManager().Tick(new List<FactionState> { st }, world, world.Tuning, new List<string>());

            CampaignObjective objective = st.CampaignFoci[0].Objectives[0];
            PendingFleetAction fleet = Assert.Single(st.PendingFleets);
            Assert.Equal(CampaignOutcome.None, objective.LastOutcome);
            Assert.Equal(DateTime.MinValue, objective.LastOutcomeUtc);
            Assert.Equal(fleet.FleetId, objective.LastDispatchFleetId);
        }

        // ---- what came back ----------------------------------------------------------------------

        /// <summary>A raid destroyed on arrival reports the interception, the bracket the defence
        /// won it at, and what it cost — the three facts the Campaigns view had none of.</summary>
        [Fact]
        public void AnInterceptedRaid_RecordsTheInterceptionAndItsCost()
        {
            FactionState raider, defender;
            WorldModel world = Intercept(out raider, out defender, guardDps: 90000, guardRangeM: 2000);

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { defender, raider },
                                                 world, world.Tuning, new FixedRandom(0.0), new List<string>(), null);

            CampaignObjective objective = raider.CampaignFoci[0].Objectives[0];
            Assert.Equal(CampaignOutcome.Intercepted, objective.LastOutcome);
            Assert.Equal(world.UtcNow, objective.LastOutcomeUtc);
            Assert.Equal(2000, objective.LastOutcomeRangeM, 6);
            Assert.Equal(0, objective.LastOutcomeOwnDps, 6);        // 800 m hulls against a 2000 m guard
            Assert.Equal(90000, objective.LastOutcomeOpposedDps, 6);
            Assert.Equal(3, objective.LastOutcomeHullsTotal);
        }

        /// <summary>A raid that lands reports the strike, not a loss, and carries no bracket: it was
        /// not decided at one. The row that reads "struck" and the one that reads "intercepted" are
        /// the pair the single timestamp could not tell apart.</summary>
        [Fact]
        public void ARaidThatLands_RecordsTheStrike()
        {
            FactionState raider, defender;
            WorldModel world = Intercept(out raider, out defender, guardDps: 0, guardRangeM: 0);

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { defender, raider },
                                                 world, world.Tuning, new FixedRandom(0.0), new List<string>(), null);

            CampaignObjective objective = raider.CampaignFoci[0].Objectives[0];
            Assert.Equal(CampaignOutcome.Landed, objective.LastOutcome);
            Assert.Equal(0, objective.LastOutcomeRangeM, 6);
            Assert.Equal(0, objective.LastOutcomeOpposedDps, 6);
        }

        /// <summary>A fleet flown under no campaign credits nothing — the stamp is matched on the
        /// dispatch that made it, so a sortie outside a campaign cannot land an outcome on a row
        /// that did not send it.</summary>
        [Fact]
        public void AFleetTheObjectiveDidNotSend_RecordsNothing()
        {
            FactionState raider, defender;
            WorldModel world = Intercept(out raider, out defender, guardDps: 90000, guardRangeM: 2000);
            raider.CampaignFoci[0].Objectives[0].LastDispatchFleetId = "OTHER1";

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { defender, raider },
                                                 world, world.Tuning, new FixedRandom(0.0), new List<string>(), null);

            Assert.Equal(CampaignOutcome.None, raider.CampaignFoci[0].Objectives[0].LastOutcome);
        }

        // ---- persistence ---------------------------------------------------------------------------

        /// <summary>The outcome and the fleet it is waiting on survive a restart. A fleet outlives
        /// the process; an objective that forgot which one it sent would read its loss as never
        /// having happened.</summary>
        [Fact]
        public void Outcome_RoundTripsThroughSaveAndLoad()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;

            var st = new FactionState { Tag = "ACME", FactionId = 7 };
            var focus = new CampaignFocus { Id = "raid:MARK", CenterNodeId = "H1", SetAtUtc = T0, ExpiresAtUtc = T0.AddHours(2) };
            focus.RegionMembers.Add("H1");
            focus.Objectives.Add(new CampaignObjective
            {
                Kind = CampaignObjectiveKind.Terrorise,
                TargetNodeId = "MARK",
                LastDispatchUtc = T0,
                LastOutcome = CampaignOutcome.Intercepted,
                LastOutcomeUtc = T0.AddMinutes(4),
                LastOutcomeRangeM = 2000,
                LastOutcomeOwnDps = 11695,
                LastOutcomeOpposedDps = 136321,
                LastOutcomeHullsLost = 2,
                LastOutcomeHullsTotal = 24,
                LastDispatchFleetId = "DC463D",
            });
            st.CampaignFoci.Add(focus);

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
                FactionState loaded = FactionStore.ApplyBrainDoc(store.LoadBrain("ACME"), new SpatialGraph());

                CampaignObjective objective = loaded.CampaignFoci[0].Objectives[0];
                Assert.Equal(CampaignOutcome.Intercepted, objective.LastOutcome);
                Assert.Equal(T0.AddMinutes(4), objective.LastOutcomeUtc);
                Assert.Equal(2000, objective.LastOutcomeRangeM, 6);
                Assert.Equal(11695, objective.LastOutcomeOwnDps, 6);
                Assert.Equal(136321, objective.LastOutcomeOpposedDps, 6);
                Assert.Equal(2, objective.LastOutcomeHullsLost);
                Assert.Equal(24, objective.LastOutcomeHullsTotal);
                Assert.Equal("DC463D", objective.LastDispatchFleetId);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        /// <summary>An objective that has had no outcome writes none of these keys. The per-faction
        /// brain files are loaded one at a time and eight dead keys per objective compounds.</summary>
        [Fact]
        public void Outcome_IsOmittedFromTheJsonWhenThereIsNone()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            home.OwnerFactionTag = "ACME"; home.HoldStrength = 0.8; home.Radius = 30;

            var st = new FactionState { Tag = "ACME", FactionId = 7 };
            var focus = new CampaignFocus { Id = "raid:MARK", CenterNodeId = "H1", SetAtUtc = T0, ExpiresAtUtc = T0.AddHours(2) };
            focus.RegionMembers.Add("H1");
            focus.Objectives.Add(new CampaignObjective { Kind = CampaignObjectiveKind.Terrorise, TargetNodeId = "MARK" });
            st.CampaignFoci.Add(focus);

            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("ACME"), FactionStore.DefaultLogCap));
                string json = File.ReadAllText(store.BrainPath("ACME"));
                Assert.DoesNotContain("LastOutcome", json, StringComparison.Ordinal);
                Assert.DoesNotContain("LastDispatchFleetId", json, StringComparison.Ordinal);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        // ---- fixtures --------------------------------------------------------------------------

        /// <summary>A faction with a raid campaign against one defended node, staged from its home.
        /// Same shape as <c>RaidCommitGateTests</c>'s, with the defence's REACH parameterised: reach
        /// is what decides which bracket the fight is lost at.</summary>
        private static WorldModel Campaign(out SpatialNode origin, out SpatialNode target,
                                           out FactionState st, double defenceDps, double defenceRangeM = 800)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = T0;
            world.PrefabCatalogue = new ArmedCatalogue().Add("Fighter", 1000, "Small");
            world.SetTreasury("A", 10_000_000);

            origin = world.Graph.Add(new Vec3(0, 0, 0), 1.0, "HOME");
            origin.OwnerFactionTag = "A"; origin.HoldStrength = 1.0; origin.StableStationId = 1; origin.Radius = 40;
            target = world.Graph.Add(new Vec3(4000, 0, 0), 0.6, "MARK");
            target.OwnerFactionTag = "B"; target.HoldStrength = 1.0; target.Radius = 40;
            target.StationWeapons = new List<WeaponBand>
                { new WeaponBand { Dps = (float)defenceDps, RangeM = (float)defenceRangeM } };

            st = new FactionState { Tag = "A", RngSeed = 1 };
            var focus = new CampaignFocus
            {
                Id = "raid:MARK", CenterNodeId = "HOME", SetAtUtc = T0, ExpiresAtUtc = T0.AddHours(2),
            };
            focus.RegionMembers.Add("HOME");
            var objective = new CampaignObjective
            {
                Kind = CampaignObjectiveKind.Terrorise,
                Cause = CampaignCause.Grudge,
                TargetNodeId = "MARK",
            };
            objective.Candidates.Add("MARK");
            focus.Objectives.Add(objective);
            st.CampaignFoci.Add(focus);
            return world;
        }

        /// <summary>A raid already at its target under a campaign that dispatched it, met by a guard
        /// of <paramref name="guardDps"/>. A guard of 0 is the landed arm.</summary>
        private static WorldModel Intercept(out FactionState raider, out FactionState defender,
                                            float guardDps, float guardRangeM)
        {
            WorldModel world = TestSupport.World();
            SpatialNode mark = world.Graph.Add(new Vec3(0, 0, 0), 0.6, "MARK");
            mark.OwnerFactionTag = "MERC"; mark.HoldStrength = 0.8; mark.Radius = 40;
            DateTime now = world.UtcNow;

            defender = new FactionState { Tag = "MERC" };
            if (guardDps > 0)
                defender.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Guard, ActorTag = "MERC", TargetNodeId = "MARK",
                    DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5), Resolved = true,
                    WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = guardDps, RangeM = guardRangeM } },
                });

            raider = new FactionState { Tag = "RVN" };
            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MARK", ConvoySize = 3,
                DepartedUtc = now, EstimatedArrivalUtc = now, FleetId = "RAID01",
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 60, RangeM = 800 } },
            });

            var focus = new CampaignFocus { Id = "raid:MARK", CenterNodeId = "MARK", SetAtUtc = now, ExpiresAtUtc = now.AddHours(2) };
            focus.RegionMembers.Add("MARK");
            focus.Objectives.Add(new CampaignObjective
            {
                Kind = CampaignObjectiveKind.Terrorise,
                TargetNodeId = "MARK",
                LastDispatchUtc = now,
                LastDispatchFleetId = "RAID01",
            });
            raider.CampaignFoci.Add(focus);
            return world;
        }
    }
}
