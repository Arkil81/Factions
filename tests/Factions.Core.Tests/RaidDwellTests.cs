using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The engagement dwell: a Raid/Retaliate/Conquer holds at its target for
    /// <see cref="FactionTuning.RaidDwellSeconds"/> before its effect lands, so the raid is
    /// contestable for the whole of that window instead of resolving the instant it arrives.
    /// Covers where the dwell sits in the itinerary, that contact applies nothing, that the defence
    /// duel and the hold damage both fire at the end of it, the tuning kill switch, and the ratio a
    /// fleet resolves at when it is cut short partway through.
    /// </summary>
    public class RaidDwellTests
    {
        private static WorldModel MakeWorld(int seed = 1) => TestSupport.World(seed);

        private static FactionState MakeState(string tag) => new FactionState { Tag = tag, RngSeed = 1 };

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos,
                                           double hold = 0.8, double value = 0.6)
        {
            SpatialNode n = w.Graph.Add(pos, value, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = hold;
            n.Radius = 40;
            return n;
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        /// <summary>Zero jitter, so a duel's damage is the untouched base fraction.</summary>
        private static IRandom NoJitter() => new FixedRandom(0.5);

        /// <summary>An attacker/victim pair 100 km apart, with the raid already dispatched and in
        /// transit. Returns the raider's state; the world's nodes are "RVN1" and "MERC1".</summary>
        private static FactionState DispatchRaid(WorldModel w, out List<FactionState> states,
                                                 out SpatialNode target, ActionKind kind = ActionKind.Raid)
        {
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            target = AddNode(w, "MERC1", "MERC", new Vec3(100, 0, 0));
            var raider = MakeState("RVN");
            states = new List<FactionState> { raider, MakeState("MERC") };
            raider.PendingFleets.Add(PendingFleetResolver.CreatePending(
                kind, "RVN", new FactionIntent { Kind = kind, TargetNodeId = "MERC1", Cost = 0 },
                w, w.Tuning, immediate: false));
            return raider;
        }

        [Fact]
        public void Dispatch_QueuesTheDwellBetweenTheTargetAndTheWayHome()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);

            int cycles = w.Tuning.RaidSiegeCycles;
            PendingFleetAction f = raider.PendingFleets[0];
            Assert.Equal(FleetStopKind.Service, f.CurrentStopKind);   // the travel leg is unchanged
            Assert.Equal(cycles + 1, f.RemainingStops.Count);
            for (int c = 0; c < cycles; c++)
            {
                Assert.Equal(FleetStopKind.Engage, f.RemainingStops[c].Kind);
                Assert.Equal("MERC1", f.RemainingStops[c].NodeId);    // dwells at what it came to attack
                Assert.Equal(w.Tuning.RaidDwellSeconds, f.RemainingStops[c].DurationSeconds);
            }
            Assert.Equal(FleetStopKind.Return, f.RemainingStops[cycles].Kind);
        }

        [Theory]
        [InlineData(ActionKind.Raid)]
        [InlineData(ActionKind.Retaliate)]
        [InlineData(ActionKind.Conquer)]
        public void EveryCombatKindDwells_NotJustRaid(ActionKind kind)
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target, kind);

            Assert.Equal(FleetStopKind.Engage, raider.PendingFleets[0].RemainingStops[0].Kind);
        }

        [Fact]
        public void Contact_StartsTheDwellAndChangesNothingAtTheNode()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            PendingFleetAction f = raider.PendingFleets[0];
            Assert.Equal(FleetStopKind.Engage, f.CurrentStopKind);
            Assert.Equal(w.Tuning.RaidDwellSeconds, (f.EstimatedArrivalUtc - f.DepartedUtc).TotalSeconds, 3);
            Assert.False(f.Resolved);
            Assert.Equal(0.8, target.HoldStrength);           // nothing has been done to it yet
            Assert.Equal(0.0, target.Threat);

            string all = string.Join("\n", log);
            Assert.Contains("RAID_ENGAGED", all);
            Assert.DoesNotContain("RAID_RESOLVED", all);
            // The defence duel is the dwell's outcome, so it has not run either.
            Assert.DoesNotContain("STATION_DEFENSE", all);
            Assert.DoesNotContain("STATION_INTERCEPT", all);
        }

        [Fact]
        public void Contact_RecordsTheEngagementAsAnEventCarryingTheWindowLength()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), new List<string>(), null);

            FactionEvent engaged = default(FactionEvent);
            bool found = false;
            foreach (FactionEvent e in w.PendingEvents)
                if (e.Kind == FactionEventKind.RaidEngaged) { engaged = e; found = true; break; }

            Assert.True(found, "the start of the engagement must be reportable, not only loggable");
            Assert.Equal("RVN", engaged.ActorTag);
            Assert.Equal("MERC", engaged.OtherTag);          // the side that has to react
            Assert.Equal("MERC1", engaged.NodeId);
            Assert.Equal((long)w.Tuning.RaidDwellSeconds, engaged.Amount);
            Assert.True(FactionEventLog.IsNotable(FactionEventKind.RaidEngaged));
        }

        [Fact]
        public void DwellEnd_LandsTheEffectAndSendsTheFleetHome()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.True(target.HoldStrength < 0.8, "the raid lands when its dwell runs out");
            Assert.Contains("RAID_RESOLVED", string.Join("\n", log));

            // The first dwell lands the effect; the fleet is still on station for the rest of the
            // siege, and only goes home once it is spent -- which here is the ceiling being reached,
            // because an unopposed siege strips past it.
            int guard = 0;
            while (raider.PendingFleets[0].CurrentStopKind == FleetStopKind.Engage && guard++ < 16)
            {
                w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            }
            Assert.Equal(FleetStopKind.Return, raider.PendingFleets[0].CurrentStopKind);
        }

        [Fact]
        public void DwellEnd_IsWhenTheStationFightsBack()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            target.StationPrefabId = "SomeStation";
            target.StationWeapons = new List<WeaponBand> { new WeaponBand { Dps = 400, RangeM = 3000 } };
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            Assert.DoesNotContain("STATION_", string.Join("\n", log));   // contact alone provokes nothing

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            // An unarmed raider against a 400 Dps station: the station wins outright, and it wins at
            // the end of a window the defender had the whole of to act in.
            Assert.Contains("STATION_INTERCEPT", string.Join("\n", log));
            Assert.Equal(0.8, target.HoldStrength);
        }

        [Fact]
        public void DwellTunedToZero_ResolvesOnContact_AsItDidBeforeTheDwellExisted()
        {
            WorldModel w = MakeWorld();
            w.Tuning.RaidDwellSeconds = 0;
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            Assert.Equal(FleetStopKind.Return, raider.PendingFleets[0].RemainingStops[0].Kind);

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.True(target.HoldStrength < 0.8);
            Assert.Contains("RAID_RESOLVED", string.Join("\n", log));
            Assert.DoesNotContain("RAID_ENGAGED", string.Join("\n", log));
        }

        [Fact]
        public void AFleetWithNoDwellQueued_StillResolvesOnContact()
        {
            // The shape every fleet persisted before this existed has, and the shape an immediate
            // dispatch keeps: no Engage stop, so contact is still resolution.
            WorldModel w = MakeWorld();
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            SpatialNode target = AddNode(w, "MERC1", "MERC", new Vec3(100, 0, 0));
            var raider = MakeState("RVN");
            var states = new List<FactionState> { raider, MakeState("MERC") };
            var log = new List<string>();

            raider.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = "RVN", TargetTag = "MERC", TargetNodeId = "MERC1",
                OriginNodeId = "RVN1", OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(100, 0, 0),
                DepartedUtc = w.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = w.UtcNow,
            });

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.True(target.HoldStrength < 0.8);
            Assert.Contains("RAID_RESOLVED", string.Join("\n", log));
        }

        [Fact]
        public void ImmediateDispatch_SkipsTheDwell()
        {
            WorldModel w = MakeWorld();
            AddNode(w, "RVN1", "RVN", new Vec3(0, 0, 0));
            AddNode(w, "MERC1", "MERC", new Vec3(100, 0, 0));

            PendingFleetAction f = PendingFleetResolver.CreatePending(
                ActionKind.Raid, "RVN", new FactionIntent { Kind = ActionKind.Raid, TargetNodeId = "MERC1", Cost = 0 },
                w, w.Tuning, immediate: true);

            Assert.Equal(FleetStopKind.Return, f.RemainingStops[0].Kind);
        }

        [Fact]
        public void ADwellingFleet_ResolvesEvenWhileItsGridIsMaterialised()
        {
            // The frozen-window failure. ResolveArrivals defers a fleet with a live grid so an
            // abstract arrival cannot race one still flying -- but a dwelling raider is not flying
            // anywhere, and deferring it would stop the clock for exactly as long as a player stands
            // nearby. Watching a raid must not make it last forever.
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            // A player is close enough to see it, so the grid is live for the whole dwell.
            PendingFleetAction dwelling = raider.PendingFleets[0];
            Assert.Equal(FleetStopKind.Engage, dwelling.CurrentStopKind);
            dwelling.MaterialisedEntityId = 12345L;
            raider.PendingFleets[0] = dwelling;

            w.UtcNow = dwelling.EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.True(target.HoldStrength < 0.8, "the dwell must end on its own clock, watched or not");
            Assert.Contains("RAID_RESOLVED", string.Join("\n", log));
        }

        [Fact]
        public void AMaterialisedFleetStillInTransit_IsStillDeferred()
        {
            // The guard the exemption above must not widen: a fleet whose grid is still flying is
            // deferred exactly as before, or the abstract arrival races the physical one.
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            PendingFleetAction inTransit = raider.PendingFleets[0];
            Assert.Equal(FleetStopKind.Service, inTransit.CurrentStopKind);
            inTransit.MaterialisedEntityId = 12345L;
            raider.PendingFleets[0] = inTransit;

            w.UtcNow = inTransit.EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Equal(FleetStopKind.Service, raider.PendingFleets[0].CurrentStopKind);
            Assert.DoesNotContain("RAID_ENGAGED", string.Join("\n", log));
        }

        // ---- what a fleet cut short mid-dwell is worth ---------------------------------------

        [Fact]
        public void LossRatio_IsZeroOffTheDwell_AndTheElapsedFractionOnIt()
        {
            WorldModel w = MakeWorld();
            DateTime departed = w.UtcNow;
            var inTransit = new PendingFleetAction
            {
                Kind = ActionKind.Raid, CurrentStopKind = FleetStopKind.Service,
                DepartedUtc = departed, EstimatedArrivalUtc = departed.AddMinutes(10),
            };
            Assert.Equal(0.0, PendingFleetResolver.LossRatioFor(inTransit, departed.AddMinutes(9)));

            var dwelling = new PendingFleetAction
            {
                Kind = ActionKind.Raid, CurrentStopKind = FleetStopKind.Engage,
                DepartedUtc = departed, EstimatedArrivalUtc = departed.AddSeconds(300),
            };
            Assert.Equal(0.0, PendingFleetResolver.LossRatioFor(dwelling, departed), 6);
            Assert.Equal(0.4, PendingFleetResolver.LossRatioFor(dwelling, departed.AddSeconds(120)), 6);
            Assert.Equal(1.0, PendingFleetResolver.LossRatioFor(dwelling, departed.AddSeconds(600)), 6);
        }

        /// <summary>A grid lost with no grid deciding it -- a stall, a dismissed duplicate -- keeps
        /// the ratio its elapsed dwell was worth instead of going through the defence duel, which
        /// would overwrite it with its own; against a node carrying neither station weapons nor a
        /// StationPrefabId that duel is unopposed and lands the raid at full effect. A lead lost in
        /// combat is not one of these: that attack has lost (LeadLossTests).</summary>
        [Theory]
        [InlineData("grid_stalled")]
        [InlineData("spawn_dedup")]
        public void AFleetThatLostItsGrid_KeepsThatRatio_InsteadOfDuellingForANewOne(string cause)
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            // Reach the dwell, then take the grid out partway through it, exactly as the host does:
            // read the ratio off the live window first, then force the fleet to arrive.
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            PendingFleetAction dwelling = raider.PendingFleets[0];
            w.UtcNow = dwelling.DepartedUtc.AddSeconds(w.Tuning.RaidDwellSeconds * 0.25);
            double lossRatio = PendingFleetResolver.LossRatioFor(dwelling, w.UtcNow);
            Assert.Equal(0.25, lossRatio, 6);

            dwelling.MaterialisedEntityId = -1;
            dwelling.ForcedRatio = lossRatio;
            dwelling.ForcedRatioCause = cause;
            dwelling.EstimatedArrivalUtc = w.UtcNow;
            raider.PendingFleets[0] = dwelling;

            log.Clear();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            string all = string.Join("\n", log);
            Assert.DoesNotContain("STATION_DEFENSE", all);
            Assert.DoesNotContain("STATION_INTERCEPT", all);
            Assert.Contains("ratio=0.25", all);
        }

        /// <summary>A copy the host dismissed is not a lost fleet: it goes home with its hulls and
        /// never duels or lands its effect.</summary>
        [Fact]
        public void ADismissedDuplicate_IsRecalled_InsteadOfDuellingOrLandingItsEffect()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            PendingFleetAction dwelling = raider.PendingFleets[0];
            w.UtcNow = dwelling.DepartedUtc.AddSeconds(w.Tuning.RaidDwellSeconds * 0.25);
            dwelling.MaterialisedEntityId = -1;
            dwelling.ForcedRatio = 0.0;
            dwelling.ForcedRatioCause = PendingFleetResolver.SiblingDismissCause;
            dwelling.EstimatedArrivalUtc = w.UtcNow;
            raider.PendingFleets[0] = dwelling;
            double holdBefore = target.HoldStrength;

            log.Clear();
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            string all = string.Join("\n", log);
            Assert.DoesNotContain("STATION_DEFENSE", all);
            Assert.DoesNotContain("SHIP_LOST", all);
            Assert.Contains("FLEET_RECALLED " + dwelling.LogId, all);
            Assert.Equal(holdBefore, target.HoldStrength);
        }

        [Fact]
        public void AQuarterOfADwell_DoesLessDamageThanAWholeOne()
        {
            // The point of a progressive ratio, for a raid whose grid stalled on its dwell with no
            // grid deciding it: ending it early is worth more than ending it late, and both more than
            // not ending it at all. A lead shot down lands nothing at all (LeadLossTests).
            double holdAfterQuarter = HoldAfterInterruptedDwell(0.25);
            double holdAfterThreeQuarters = HoldAfterInterruptedDwell(0.75);
            double holdAfterFullDwell = HoldAfterInterruptedDwell(1.0);

            Assert.True(holdAfterQuarter > holdAfterThreeQuarters,
                "a raid stopped a quarter in must leave more hold than one stopped three quarters in");
            Assert.True(holdAfterThreeQuarters > holdAfterFullDwell,
                "a raid stopped three quarters in must still leave more hold than one that ran its course");
        }


        // ---- the siege: a raid holds for several dwells, not one -------------------------------

        [Fact]
        public void ASiege_StripsHoldOncePerDwell_NotOncePerRaid()
        {
            WorldModel w = MakeWorld();
            w.Tuning.ContestedHoldGainSuppressedSeconds = 0;   // isolate the strip from FAC-362
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            target.HoldStrength = 1.0;
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            var holdAfterEachCycle = new List<double>();
            int guard = 0;
            while (raider.PendingFleets[0].CurrentStopKind == FleetStopKind.Engage && guard++ < 16)
            {
                w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
                holdAfterEachCycle.Add(target.HoldStrength);
            }

            // More than one strip is the whole point: before the siege a raid produced exactly one.
            Assert.True(holdAfterEachCycle.Count > 1,
                "a siege must strip more than once, got " + holdAfterEachCycle.Count + " cycle(s)");
            for (int i = 1; i < holdAfterEachCycle.Count; i++)
                Assert.True(holdAfterEachCycle[i] < holdAfterEachCycle[i - 1],
                    "every dwell must take hold down further, cycle " + i + " read "
                    + holdAfterEachCycle[i] + " against " + holdAfterEachCycle[i - 1]);
            Assert.True(target.HoldStrength <= w.Tuning.ConquerHoldCeiling,
                "an unopposed siege must take a full-hold node to the conquer ceiling, left it at "
                + target.HoldStrength);
        }

        [Fact]
        public void ASiegeCycleCount_OfOne_ReproducesTheSingleBlowExactly()
        {
            WorldModel w = MakeWorld();
            w.Tuning.RaidSiegeCycles = 1;
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            Assert.Equal(2, raider.PendingFleets[0].RemainingStops.Count);

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            Assert.Equal(FleetStopKind.Return, raider.PendingFleets[0].CurrentStopKind);
            Assert.DoesNotContain("siege_holds", string.Join("\n", log));
        }

        [Fact]
        public void ASiege_BreaksOffWhenTheNodeIsAlreadyAtTheCeiling()
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            target.HoldStrength = w.Tuning.ConquerHoldCeiling;   // nothing left to strip
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            string joined = string.Join("\n", log);
            Assert.Contains("siege_broken_off", joined);
            Assert.Equal(FleetStopKind.Return, raider.PendingFleets[0].CurrentStopKind);
        }

        [Fact]
        public void ASiege_LootsOnceOnTheWayOut_NotEveryCycle()
        {
            WorldModel w = MakeWorld();
            w.Tuning.ContestedHoldGainSuppressedSeconds = 0;
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            target.HoldStrength = 1.0;
            w.AddNodeInventory("MERC1", "Ingot:Iron", 100000);
            PendingFleetAction inTransit = raider.PendingFleets[0];
            inTransit.CargoCapacityLiters = 1000000;
            raider.PendingFleets[0] = inTransit;
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            int guard = 0;
            while (raider.PendingFleets[0].CurrentStopKind == FleetStopKind.Engage && guard++ < 16)
            {
                w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
                PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            }

            int looted = 0;
            foreach (string line in log)
                if (line.Contains("RAID_RESOLVED") && line.Contains(" loot=")) looted++;
            Assert.Equal(1, looted);
        }

        private static double HoldAfterInterruptedDwell(double fraction)
        {
            WorldModel w = MakeWorld();
            List<FactionState> states; SpatialNode target;
            FactionState raider = DispatchRaid(w, out states, out target);
            var log = new List<string>();

            w.UtcNow = raider.PendingFleets[0].EstimatedArrivalUtc;
            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);

            PendingFleetAction dwelling = raider.PendingFleets[0];
            w.UtcNow = dwelling.DepartedUtc.AddSeconds(w.Tuning.RaidDwellSeconds * fraction);
            dwelling.MaterialisedEntityId = -1;
            dwelling.ForcedRatio = PendingFleetResolver.LossRatioFor(dwelling, w.UtcNow);
            dwelling.ForcedRatioCause = "grid_stalled";
            dwelling.EstimatedArrivalUtc = w.UtcNow;
            raider.PendingFleets[0] = dwelling;

            PendingFleetResolver.ResolveArrivals(states, w, w.Tuning, NoJitter(), log, null);
            return target.HoldStrength;
        }
    }
}
