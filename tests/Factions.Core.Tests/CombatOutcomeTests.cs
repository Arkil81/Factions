using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The <see cref="CombatOutcome"/> every meeting resolution writes: one per resolution,
    /// with both sides' results and hull losses; its sentence, log line and per-viewer tone; who is
    /// told (the fog radius); and the wire notice that carries it.</summary>
    public class CombatOutcomeTests
    {
        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private static List<WeaponBand> Bands(float dps)
        {
            return new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = 2000 } };
        }

        private static WorldModel World()
        {
            WorldModel w = TestSupport.World(1);
            SpatialNode n = w.Graph.Add(new Vec3(5, 0, 0), 0.6, "MERC1");
            n.OwnerFactionTag = "MERC";
            n.HoldStrength = 0.8;
            n.Radius = 40;
            return w;
        }

        private static PendingFleetAction Guard(DateTime now, List<WeaponBand> bands, string tag = "MERC")
        {
            return new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = tag, TargetNodeId = "MERC1", FleetId = "G00001",
                DepartedUtc = now.AddMinutes(-5), EstimatedArrivalUtc = now.AddMinutes(-5),
                Resolved = true, WeaponBands = bands,
            };
        }

        private static PendingFleetAction Raid(DateTime now, List<WeaponBand> bands, ActionKind kind = ActionKind.Raid)
        {
            return new PendingFleetAction
            {
                Kind = kind, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1", FleetId = "R00001",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(5, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, WeaponBands = bands, LastResolvedSegment = -1,
            };
        }

        private static string Line(IEnumerable<string> log, string prefix)
        {
            foreach (string l in log) if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            return null;
        }

        private static CombatOutcome Repelled()
        {
            return new CombatOutcome
            {
                Kind = ActionKind.Raid, Meeting = CombatMeeting.Guard,
                AttackerTag = "EXP", AttackerFleet = "[b.W1.atk]",
                DefenderTag = "IGSL", DefenderFleet = "[b.W1.def]",
                DefenderTags = new List<string> { "IGSL", "ALLY" },
                NodeId = "bench:W1", PositionKm = new Vec3(10, 0, 0),
                Attack = CombatAttackResult.Repelled, Defence = CombatDefenceResult.Stood,
                AttackerHulls = 3, AttackerHullsLost = 0, DefenderHulls = 2, DefenderHullsLost = 1,
                Source = "physical", BenchLabel = "W1",
            };
        }

        // ---- the record, its sentence and its tone --------------------------------------------------

        [Fact]
        public void ARepelledAttackIsALossForTheAttackerAndAWinForEveryDefender()
        {
            CombatOutcome o = Repelled();
            Assert.Equal(CombatTone.Loss, o.ToneFor("EXP"));
            Assert.Equal(CombatTone.Loss, o.ToneFor("exp"));
            Assert.Equal(CombatTone.Win, o.ToneFor("IGSL"));
            Assert.Equal(CombatTone.Win, o.ToneFor("ALLY"));
            Assert.Equal(CombatTone.Neutral, o.ToneFor("SPRT"));
            Assert.Equal(CombatTone.Neutral, o.ToneFor(null));
        }

        [Fact]
        public void ALandingWinsForTheAttackerAndAMutualKillLosesForBoth()
        {
            CombatOutcome landed = Repelled();
            landed.Attack = CombatAttackResult.Landed;
            landed.Defence = CombatDefenceResult.Destroyed;
            Assert.Equal(CombatTone.Win, landed.ToneFor("EXP"));
            Assert.Equal(CombatTone.Loss, landed.ToneFor("IGSL"));

            CombatOutcome mutual = Repelled();
            mutual.Attack = CombatAttackResult.Destroyed;
            mutual.Defence = CombatDefenceResult.Destroyed;
            Assert.Equal(CombatTone.Loss, mutual.ToneFor("EXP"));
            Assert.Equal(CombatTone.Loss, mutual.ToneFor("IGSL"));

            CombatOutcome brokeOff = Repelled();
            brokeOff.Attack = CombatAttackResult.BrokeOff;
            Assert.Equal(CombatTone.Win, brokeOff.ToneFor("IGSL"));
        }

        [Fact]
        public void TheSentenceSaysWhoWhereHowWhatNextAndTheLosses()
        {
            Assert.Equal("[EXP] raid on [IGSL] at bench:W1 was repelled; the raiders are withdrawing."
                         + " Hulls lost: EXP 0/3, IGSL 1/2.", Repelled().Sentence());

            CombatOutcome station = Repelled();
            station.Kind = ActionKind.Conquer;
            station.Meeting = CombatMeeting.Station;
            station.DefenderFleet = null;
            station.DefenderHulls = 0;
            station.DefenderHullsLost = 0;
            station.Attack = CombatAttackResult.Landed;
            station.Defence = CombatDefenceResult.Stood;
            Assert.Equal("[EXP] assault on [IGSL]'s station at bench:W1 broke through the defence, and the raiders"
                         + " press the attack. Hulls lost: EXP 0/3.", station.Sentence());

            CombatOutcome beaten = Repelled();
            beaten.Attack = CombatAttackResult.Destroyed;
            beaten.AttackerHullsLost = 3;
            Assert.Equal("[EXP] raid on [IGSL] at bench:W1 was destroyed by the defence. Hulls lost: EXP 3/3, IGSL 1/2.",
                         beaten.Sentence());
        }

        /// <summary>Live 2026-10-12 00:04:57: UNGD's raid on N096 landed at ratio 0.26 and broke its
        /// siege off out of ammunition, and the defender was told the raiders "press the attack".</summary>
        [Fact]
        public void ALandedAttackThatIsLeaving_SaysSoAndWhy()
        {
            CombatOutcome o = Repelled();
            o.Meeting = CombatMeeting.Station;
            o.DefenderFleet = null;
            o.DefenderHulls = 0;
            o.Attack = CombatAttackResult.Landed;
            o.Withdrawal = "out of ammunition";

            Assert.Equal("[EXP] raid on [IGSL]'s station at bench:W1 broke through the defence, and the raiders"
                         + " are withdrawing (out of ammunition). Hulls lost: EXP 0/3.", o.Sentence());
            Assert.Equal("withdraw", o.NextStep);
        }

        [Fact]
        public void TheWithdrawalMarksOnlyTheNewestLandedOutcomeOfThatFleet()
        {
            WorldModel w = World();
            CombatOutcome older = Repelled(); older.Attack = CombatAttackResult.Landed;
            CombatOutcome other = Repelled(); other.Attack = CombatAttackResult.Landed; other.AttackerFleet = "[other]";
            CombatOutcome newer = Repelled(); newer.Attack = CombatAttackResult.Landed;
            w.AddCombatOutcome(older);
            w.AddCombatOutcome(newer);
            w.AddCombatOutcome(other);

            w.MarkCombatOutcomeWithdrawal("[b.W1.atk]", "worn down");

            Assert.Equal("worn down", newer.Withdrawal);
            Assert.Null(older.Withdrawal);
            Assert.Null(other.Withdrawal);

            CombatOutcome repelled = Repelled();
            w.AddCombatOutcome(repelled);
            w.MarkCombatOutcomeWithdrawal("[b.W1.atk]", "worn down");
            Assert.Null(repelled.Withdrawal);
        }

        [Fact]
        public void TheLogLineCarriesEveryField()
        {
            Assert.Equal("COMBAT_OUTCOME [b.W1.atk] EXP vs [b.W1.def] IGSL @bench:W1 kind=Raid meeting=Guard"
                         + " attack=Repelled defence=Stood next=withdraw hulls_lost atk=0/3 def=1/2 src=physical bench=W1",
                         Repelled().LogLine());
        }

        // ---- who is told ------------------------------------------------------------------------------

        [Fact]
        public void OnlyViewersWithinObservationRangeOfTheFightAreTold()
        {
            CombatOutcome o = Repelled();   // fought at (10, 0, 0) km
            var positions = new Dictionary<ulong, Vec3>
            {
                { 1, new Vec3(10.5, 0, 0) },   // beside it, IGSL
                { 2, new Vec3(24, 0, 0) },     // 14 km off, EXP
                { 3, new Vec3(26, 0, 0) },     // 16 km off
                { 4, new Vec3(10, 3, 0) },     // unaffiliated
            };
            var tags = new Dictionary<ulong, string> { { 1, "IGSL" }, { 2, "EXP" }, { 3, "IGSL" } };
            double asked = -1;
            Func<Vec3, double, IEnumerable<ulong>> within = (pos, r) =>
            {
                asked = r;
                return positions.Where(kv => kv.Value.DistanceTo(pos) <= r).Select(kv => kv.Key).ToList();
            };

            List<CombatNotice> notices = o.Recipients(within, 15, id => { string t; return tags.TryGetValue(id, out t) ? t : null; });

            Assert.Equal(15, asked);
            Assert.Equal(new ulong[] { 1, 2, 4 }, notices.Select(n => n.SteamId).OrderBy(x => x).ToArray());
            Assert.Equal(CombatTone.Win, notices.Single(n => n.SteamId == 1).Tone);
            Assert.Equal(CombatTone.Loss, notices.Single(n => n.SteamId == 2).Tone);
            Assert.Equal(CombatTone.Neutral, notices.Single(n => n.SteamId == 4).Tone);
            Assert.Empty(o.Recipients(null, 15, null));
        }

        [Fact]
        public void TheQueueIsTakenOnceAndBounded()
        {
            WorldModel w = TestSupport.World(1);
            Assert.Null(w.TakeCombatOutcomes());
            for (int i = 0; i < WorldModel.CombatOutcomeCap + 5; i++)
                w.AddCombatOutcome(new CombatOutcome { AttackerFleet = "#" + i });
            List<CombatOutcome> taken = w.TakeCombatOutcomes();
            Assert.Equal(WorldModel.CombatOutcomeCap, taken.Count);
            Assert.Equal("#5", taken[0].AttackerFleet);
            Assert.Null(w.TakeCombatOutcomes());
        }

        // ---- one per resolution ---------------------------------------------------------------------

        private static CombatOutcome Only(WorldModel w, List<string> log)
        {
            Assert.Single(w.PendingCombatOutcomes);
            Assert.Single(log.Where(l => l.StartsWith("COMBAT_OUTCOME ", StringComparison.Ordinal)));
            return w.PendingCombatOutcomes[0];
        }

        private static int HullsLostIn(string line)
        {
            Match m = Regex.Match(line, @"hulls_lost=(\d+)/(\d+)");
            Assert.True(m.Success, line);
            return int.Parse(m.Groups[1].Value);
        }

        [Fact]
        public void AGuardThatBeatsTheRaidWritesDestroyedAndStood()
        {
            WorldModel w = World();
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "MERC" };
            var atk = new FactionState { Tag = "RVN" };
            def.PendingFleets.Add(Guard(now, Bands(1000)));
            atk.PendingFleets.Add(Raid(now, null));
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning, new FixedRandom(0.0), log);

            CombatOutcome o = Only(w, log);
            Assert.Equal(CombatMeeting.Guard, o.Meeting);
            Assert.Equal(CombatAttackResult.Destroyed, o.Attack);
            Assert.Equal(CombatDefenceResult.Stood, o.Defence);
            Assert.Equal("RVN", o.AttackerTag);
            Assert.Equal("MERC", o.DefenderTag);
            Assert.Equal("MERC1", o.NodeId);
            Assert.Equal(5.0, o.PositionKm.X, 6);
            Assert.Equal(1, o.AttackerHulls);
            Assert.Equal(HullsLostIn(Line(log, "GUARD_INTERCEPT")), o.AttackerHullsLost);
            Assert.Equal(1, o.DefenderHulls);
            Assert.Equal(0, o.DefenderHullsLost);
            Assert.Equal("duel", o.Source);
            Assert.Null(o.BenchLabel);
            Assert.Equal(CombatTone.Win, o.ToneFor("MERC"));
        }

        [Fact]
        public void ARaidThatBreaksTheGuardWritesLandedAndTheGuardsLoss()
        {
            WorldModel w = World();
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "MERC" };
            var atk = new FactionState { Tag = "RVN" };
            def.PendingFleets.Add(Guard(now, null));
            atk.PendingFleets.Add(Raid(now, Bands(1000), ActionKind.Retaliate));
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning, new FixedRandom(1.0), log);

            Assert.NotNull(Line(log, "RAID_BREAKS_GUARD"));
            CombatOutcome o = Only(w, log);
            Assert.Equal(ActionKind.Retaliate, o.Kind);
            Assert.Equal(CombatAttackResult.Landed, o.Attack);
            Assert.Equal(CombatDefenceResult.Destroyed, o.Defence);
            Assert.Equal(1, o.DefenderHullsLost);
            // The meeting and the raid's own resolution run in the same pass: a single-dwell raid that
            // breaks the guard lands, loots and leaves, so the attackers are not pressing on.
            Assert.Equal("withdraw", o.NextStep);
            Assert.Equal("the raid is done", o.Withdrawal);
            Assert.StartsWith("[RVN] reprisal on [MERC] at MERC1 broke through; the defence was destroyed", o.Sentence());
        }

        /// <summary>A Guard force the duel destroys writes its hulls off in one line per fleet --
        /// before this, 101 guard hulls died with nothing but <c>COMBAT_OUTCOME def=</c> to show for it.</summary>
        [Fact]
        public void ADestroyedGuardLogsItsHullsLost()
        {
            WorldModel w = World();
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "MERC" };
            var atk = new FactionState { Tag = "RVN" };
            PendingFleetAction guard = Guard(now, null);
            guard.ShipPrefabId = "Tanto";
            guard.ConvoySize = 3;
            def.PendingFleets.Add(guard);
            atk.PendingFleets.Add(Raid(now, Bands(1000), ActionKind.Retaliate));
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning, new FixedRandom(1.0), log);

            string lost = Assert.Single(log, l => l.StartsWith("SHIP_LOST [G00001]", StringComparison.Ordinal));
            Assert.Equal("SHIP_LOST [G00001] MERC prefab=Tanto count=3 node=MERC1 why=guard_destroyed by=RVN", lost);
        }

        /// <summary>Two raids landing on one guard force in the same pass: the force dies once, and
        /// its hulls are logged once.</summary>
        [Fact]
        public void AGuardForceTwoRaidsBreakLogsItsLossOnce()
        {
            WorldModel w = World();
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "MERC" };
            var atk = new FactionState { Tag = "RVN" };
            PendingFleetAction guard = Guard(now, null);
            guard.ShipPrefabId = "Tanto";
            guard.ConvoySize = 2;
            def.PendingFleets.Add(guard);
            atk.PendingFleets.Add(Raid(now, Bands(1000), ActionKind.Retaliate));
            PendingFleetAction second = Raid(now, Bands(1000), ActionKind.Retaliate);
            second.FleetId = "R00002";
            atk.PendingFleets.Add(second);
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning, new FixedRandom(1.0), log);

            Assert.Single(log, l => l.StartsWith("SHIP_LOST [G00001]", StringComparison.Ordinal));
        }

        [Fact]
        public void AStationStandingAloneIsCountedWithoutHulls()
        {
            WorldModel w = World();
            DateTime now = w.UtcNow;
            var atk = new FactionState { Tag = "RVN" };
            atk.PendingFleets.Add(Raid(now, Bands(1000)));
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { new FactionState { Tag = "MERC" }, atk },
                w, w.Tuning, new FixedRandom(0.5), log);

            Assert.NotNull(Line(log, "STATION_DEFENSE"));
            CombatOutcome o = Only(w, log);
            Assert.Equal(CombatMeeting.Station, o.Meeting);
            Assert.Equal(CombatAttackResult.Landed, o.Attack);
            Assert.Null(o.DefenderFleet);
            Assert.Equal("MERC", o.DefenderTag);
            Assert.Equal(0, o.DefenderHulls);
            Assert.Contains("[MERC]'s station at MERC1", o.Sentence());
        }

        [Fact]
        public void ATripleEncounterWritesOneOutcomeForTheDuel()
        {
            WorldModel w = World();
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "MERC" };
            var atk = new FactionState { Tag = "RVN" };
            var trader = new FactionState { Tag = "STL" };
            def.PendingFleets.Add(Guard(now, Bands(1000)));
            atk.PendingFleets.Add(Raid(now, null));
            trader.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "STL", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(5, 0, 0),
                DepartedUtc = now, EstimatedArrivalUtc = now, CargoValue = 3000, LastResolvedSegment = -1,
            });
            var log = new List<string>();

            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk, trader }, w, w.Tuning,
                new FixedRandom(0.0), log);

            Assert.NotNull(Line(log, "TRIPLE GUARD_WINS"));
            CombatOutcome o = Only(w, log);
            Assert.Equal(CombatMeeting.Triple, o.Meeting);
            Assert.Equal(CombatAttackResult.Destroyed, o.Attack);
        }

        // ---- a stopped attack's survivors leave in the world ------------------------------------------

        /// <summary>The raid record as the cleanup hands its grid to the host, read in the callback.</summary>
        private static PendingFleetAction? RetiredWith(List<FactionState> states, long eid)
        {
            foreach (FactionState st in states)
                foreach (PendingFleetAction f in st.PendingFleets)
                    if (f.MaterialisedEntityId == eid) return f;
            return null;
        }

        [Theory]
        [InlineData(0.99)]   // every hull survives the loss rolls
        [InlineData(0.0)]    // every hull dies
        public void ABeatenRaidCarriesItsSurvivorsToTheCleanup(double roll)
        {
            WorldModel w = World();
            DateTime now = w.UtcNow;
            var def = new FactionState { Tag = "MERC" };
            var atk = new FactionState { Tag = "RVN" };
            def.PendingFleets.Add(Guard(now, Bands(1000)));
            PendingFleetAction raid = Raid(now, null);
            raid.ShipPrefabId = "gunboat";
            raid.ConvoySize = 2;
            raid.MaterialisedEntityId = 77;
            raid.CurrentStopKind = FleetStopKind.Engage;   // a dwelling raid resolves with its grid in the world
            atk.PendingFleets.Add(raid);
            var states = new List<FactionState> { def, atk };
            var log = new List<string>();
            PendingFleetAction? retired = null;

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, new FixedRandom(roll), log,
                onGridExpired: eid => { if (eid == 77) retired = RetiredWith(states, 77); });

            string line = Line(log, "GUARD_INTERCEPT");
            Match m = Regex.Match(line, @"hulls_lost=(\d+)/(\d+)");
            int lost = int.Parse(m.Groups[1].Value), total = int.Parse(m.Groups[2].Value);
            Assert.Equal(2, total);
            Assert.True(retired.HasValue);
            Assert.True(retired.Value.Intercepted);
            Assert.Equal(total - lost, retired.Value.HullsLeaving);
            Assert.Equal("beaten", retired.Value.StoppedHow);
            Assert.Equal("duel", retired.Value.StoppedSource);
            Assert.Equal(total - lost, retired.Value.DepartingHulls().Count);
            if (roll > 0.5) Assert.Equal(new List<string> { "gunboat", "gunboat" }, retired.Value.DepartingHulls());
            else Assert.Empty(retired.Value.DepartingHulls());
            // The ledger is the settlement's, as before: nothing about it moved.
            Assert.Empty(atk.PendingFleets);
        }

        [Fact]
        public void ASquadronsSurvivorsAreItsStandingMembers()
        {
            var f = new PendingFleetAction
            {
                Intercepted = true, Squadron = new List<string> { "a", "b", "c" }, HullsLeaving = 2,
            };
            f.MarkHullLost("b");
            List<string> hulls = f.DepartingHulls();
            Assert.Equal(new List<string> { "a", "c" }, hulls);

            Assert.False(PendingFleetAction.TakeDeparting(hulls, "b"));   // the dead hull's grid closes
            Assert.True(PendingFleetAction.TakeDeparting(hulls, "c"));
            Assert.True(PendingFleetAction.TakeDeparting(hulls, null));   // unknown prefab takes any
            Assert.False(PendingFleetAction.TakeDeparting(hulls, "a"));   // none left

            f.Intercepted = false;
            Assert.Empty(f.DepartingHulls());
        }

        [Fact]
        public void AnArrivalThatMeetsNoOneWritesNothing()
        {
            WorldModel w = World();
            var trader = new FactionState { Tag = "STL" };
            trader.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "STL", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(5, 0, 0),
                DepartedUtc = w.UtcNow, EstimatedArrivalUtc = w.UtcNow, CargoValue = 3000, LastResolvedSegment = -1,
            });
            var log = new List<string>();
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { trader }, w, w.Tuning, new FixedRandom(0.5), log);
            Assert.Empty(w.PendingCombatOutcomes);
            Assert.Null(Line(log, "COMBAT_OUTCOME"));
        }

        // ---- the wire ---------------------------------------------------------------------------------

        [Fact]
        public void TheNoticeRoundTrips()
        {
            foreach (WireCombatTone tone in new[] { WireCombatTone.Neutral, WireCombatTone.Win, WireCombatTone.Loss })
            {
                WireCombatTone read;
                string text;
                Assert.True(FactionsWire.TryReadCombatOutcomeNotice(
                    FactionsWire.WriteCombatOutcomeNotice(tone, Repelled().Sentence()), out read, out text));
                Assert.Equal(tone, read);
                Assert.Equal(Repelled().Sentence(), text);
            }
        }

        [Fact]
        public void TheWireToneIsCoresTone()
        {
            Assert.Equal((byte)CombatTone.Neutral, (byte)WireCombatTone.Neutral);
            Assert.Equal((byte)CombatTone.Win, (byte)WireCombatTone.Win);
            Assert.Equal((byte)CombatTone.Loss, (byte)WireCombatTone.Loss);
        }

        [Fact]
        public void AnotherKindAnUnknownToneOrATruncatedNoticeIsRejected()
        {
            WireCombatTone tone;
            string text;
            Assert.False(FactionsWire.TryReadCombatOutcomeNotice(FactionsWire.WriteSignatureStatus(1.5), out tone, out text));
            Assert.Null(text);

            byte[] data = FactionsWire.WriteCombatOutcomeNotice(WireCombatTone.Win, "x");
            data[6] = 9;   // the tone byte, after the two-byte kind and the version
            Assert.False(FactionsWire.TryReadCombatOutcomeNotice(data, out tone, out text));

            byte[] full = FactionsWire.WriteCombatOutcomeNotice(WireCombatTone.Loss, "a longer sentence");
            byte[] cut = new byte[full.Length - 4];
            Array.Copy(full, cut, cut.Length);
            Assert.False(FactionsWire.TryReadCombatOutcomeNotice(cut, out tone, out text));
        }
    }
}
