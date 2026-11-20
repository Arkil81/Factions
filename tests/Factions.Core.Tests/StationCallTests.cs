using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A station calls for protection through its antenna (docs/fleet-tactics.md, "A station calls
    /// for protection"): the antenna check, per-hull radius membership, the target order, the NPC
    /// silence behind the <c>station-call</c> switch, the call's end conditions, the Defend focus
    /// placed once and refreshed, and the reactive Guard dispatch a calling node draws.
    /// </summary>
    public class StationCallTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static WingAntennaReading Working(string ownerTag = "DEF", double radiusM = 5000)
        {
            return new WingAntennaReading
            {
                Exists = true, Functional = true, Enabled = true, Powered = true, Broadcasting = true,
                RadiusM = radiusM, OwnerIdentityId = 42, OwnerFactionTag = ownerTag, PosM = new Vec3(0, 0, 0),
            };
        }

        // ---- the antenna ----------------------------------------------------------------------

        [Fact]
        public void Antenna_CarriesTheCall_OnlyWorkingAndOwnedByTheStationsFaction()
        {
            Assert.Equal(WingRefusal.None, StationCallRules.CheckAntenna(Working(), "DEF"));
            Assert.Equal(WingRefusal.None, StationCallRules.CheckAntenna(Working("def"), "DEF"));

            WingAntennaReading a = Working(); a.Exists = false;
            Assert.Equal(WingRefusal.NoAntenna, StationCallRules.CheckAntenna(a, "DEF"));
            a = Working(); a.Functional = false;
            Assert.Equal(WingRefusal.AntennaDamaged, StationCallRules.CheckAntenna(a, "DEF"));
            a = Working(); a.Enabled = false;
            Assert.Equal(WingRefusal.AntennaOff, StationCallRules.CheckAntenna(a, "DEF"));
            a = Working(); a.Powered = false;
            Assert.Equal(WingRefusal.AntennaUnpowered, StationCallRules.CheckAntenna(a, "DEF"));
            a = Working(); a.Broadcasting = false;
            Assert.Equal(WingRefusal.NotBroadcasting, StationCallRules.CheckAntenna(a, "DEF"));
            a = Working(); a.RadiusM = 0;
            Assert.Equal(WingRefusal.NotBroadcasting, StationCallRules.CheckAntenna(a, "DEF"));

            Assert.Equal(WingRefusal.NotYourAntenna, StationCallRules.CheckAntenna(Working("OTH"), "DEF"));
            a = Working(); a.OwnerIdentityId = 0;
            Assert.Equal(WingRefusal.NotYourAntenna, StationCallRules.CheckAntenna(a, "DEF"));
            a = Working(); a.OwnerFactionTag = null;
            Assert.Equal(WingRefusal.NotYourAntenna, StationCallRules.CheckAntenna(a, "DEF"));
        }

        // ---- who answers ----------------------------------------------------------------------

        // Membership is read per hull against the antenna's current radius: of two hulls either side
        // of it, only the one inside joins, and pulling the radius in past a member drops it.
        [Fact]
        public void Hulls_AnswerPerHull_InsideTheBroadcastRadius()
        {
            WingAntennaReading a = Working(radiusM: 2000);
            var inside = new Vec3(1999, 0, 0);
            var outside = new Vec3(2001, 0, 0);
            Assert.True(WingRules.InRange(a, inside));
            Assert.False(WingRules.InRange(a, outside));

            var order = new WingOrder { CommanderIdentityId = 42, FactionTag = "DEF", StationGridId = 900 };
            Assert.Equal(-900, order.Key);
            order.Apply(WingCommand.FormOnMe, 900, 0, T0);
            Assert.Equal(900, order.AnchorEntityId);
            Assert.Equal(WingLinkChange.Joined, order.UpdateLink(1, WingRules.InRange(a, inside), WingRefusal.None, T0));
            Assert.Equal(WingLinkChange.None, order.UpdateLink(2, WingRules.InRange(a, outside), WingRefusal.OutOfRange, T0));
            Assert.True(order.Obeys(1));
            Assert.False(order.Obeys(2));

            a.RadiusM = 1000;
            Assert.Equal(WingLinkChange.Dropped, order.UpdateLink(1, WingRules.InRange(a, inside), WingRefusal.OutOfRange, T0));
            Assert.Equal(WingLinkChange.Lapsed,
                order.UpdateLink(1, false, WingRefusal.OutOfRange, T0 + WingRules.LinkGrace));
            Assert.False(order.AnyObeys());
        }

        [Fact]
        public void StationOrderKey_DoesNotCollideWithItsOwnersOrder()
        {
            var own = new WingOrder { CommanderIdentityId = 42 };
            var station = new WingOrder { CommanderIdentityId = 42, StationGridId = 42 };
            Assert.Equal(42, own.Key);
            Assert.NotEqual(own.Key, station.Key);
        }

        // ---- the target -----------------------------------------------------------------------

        [Fact]
        public void Target_IsTheDamager_ThenACombatBlockLock_ThenTheNearestHostile()
        {
            StationCallTargetSource s;
            Assert.Equal(11, StationCallRules.PickTarget(11, 22, 33, out s));
            Assert.Equal(StationCallTargetSource.Damage, s);
            Assert.Equal(22, StationCallRules.PickTarget(0, 22, 33, out s));
            Assert.Equal(StationCallTargetSource.CombatBlockLock, s);
            Assert.Equal(33, StationCallRules.PickTarget(0, 0, 33, out s));
            Assert.Equal(StationCallTargetSource.HostileNear, s);
            Assert.Equal(0, StationCallRules.PickTarget(0, 0, 0, out s));
            Assert.Equal(StationCallTargetSource.None, s);
            Assert.Equal("damaged_by", StationCallRules.Token(StationCallTargetSource.Damage));
        }

        // ---- the NPC side ---------------------------------------------------------------------

        // Only one row of the table is silenced: materialised, NPC, no working antenna.
        [Theory]
        [InlineData(true,  false, false, true)]
        [InlineData(false, false, false, false)]   // abstract: its call is assumed
        [InlineData(true,  true,  false, false)]   // a player's station is never silenced here
        [InlineData(true,  false, true,  false)]   // a working antenna calls
        public void Silenced_OnlyAMaterialisedNpcStationWithNoAntenna(
            bool materialised, bool player, bool antennaWorks, bool expected)
        {
            Assert.Equal(expected, StationCallRules.Silenced(materialised, player, antennaWorks));
        }

        [Fact]
        public void HostCalls_OnlyForAMaterialisedNpcStation()
        {
            Assert.True(StationCallRules.HostCalls(true, false));
            Assert.False(StationCallRules.HostCalls(false, false));
            Assert.False(StationCallRules.HostCalls(true, true));
        }

        // ---- the call's end -------------------------------------------------------------------

        [Fact]
        public void AntennaLoss_EndsTheCall_OnlyAfterTheGrace()
        {
            var c = new StationCall(T0);
            Assert.Equal(StationCallEnd.None, c.Read(T0, true, WingRefusal.AntennaOff, true, false));
            Assert.Equal(WingRefusal.AntennaOff, c.AntennaLostReason);
            Assert.Equal(StationCallEnd.None, c.Read(T0 + TimeSpan.FromSeconds(9), true, WingRefusal.AntennaOff, true, false));
            Assert.Equal(StationCallEnd.AntennaLost, c.Read(T0 + WingRules.LinkGrace, true, WingRefusal.AntennaOff, true, false));
        }

        [Fact]
        public void AntennaBackInsideTheGrace_KeepsTheCall()
        {
            var c = new StationCall(T0);
            c.Read(T0, true, WingRefusal.AntennaUnpowered, true, false);
            Assert.Equal(StationCallEnd.None, c.Read(T0 + TimeSpan.FromSeconds(5), true, WingRefusal.None, true, false));
            Assert.Null(c.AntennaLostUtc);
            // The grace starts over from the next loss, not the first.
            Assert.Equal(StationCallEnd.None, c.Read(T0 + TimeSpan.FromSeconds(8), true, WingRefusal.AntennaDamaged, true, false));
            Assert.Equal(StationCallEnd.None, c.Read(T0 + TimeSpan.FromSeconds(17), true, WingRefusal.AntennaDamaged, true, false));
            Assert.Equal(StationCallEnd.AntennaLost, c.Read(T0 + TimeSpan.FromSeconds(18), true, WingRefusal.AntennaDamaged, true, false));
        }

        [Fact]
        public void NoThreat_EndsTheCall_AfterTheClearWindow_AndARepeatCountsAsThreat()
        {
            var c = new StationCall(T0);
            Assert.Equal(StationCallEnd.None, c.Read(T0 + TimeSpan.FromSeconds(29), true, WingRefusal.None, false, false));
            c.Repeat(T0 + TimeSpan.FromSeconds(29));
            Assert.Equal(StationCallEnd.None, c.Read(T0 + TimeSpan.FromSeconds(58), true, WingRefusal.None, false, false));
            Assert.Equal(StationCallEnd.ThreatCleared,
                c.Read(T0 + TimeSpan.FromSeconds(29) + StationCallRules.ThreatClearAfter, true, WingRefusal.None, false, false));
        }

        [Fact]
        public void FocusExpiry_EndsACallThatTracksIt_AndNoOtherCall()
        {
            var tracked = new StationCall(T0) { TracksFocus = true };
            Assert.Equal(StationCallEnd.None, tracked.Read(T0, true, WingRefusal.None, true, true));
            Assert.Equal(StationCallEnd.FocusExpired, tracked.Read(T0 + TimeSpan.FromSeconds(1), true, WingRefusal.None, true, false));

            var npc = new StationCall(T0);
            Assert.Equal(StationCallEnd.None, npc.Read(T0 + TimeSpan.FromSeconds(1), true, WingRefusal.None, true, false));
        }

        [Fact]
        public void AGoneStation_EndsTheCall()
        {
            Assert.Equal(StationCallEnd.StationGone, new StationCall(T0).Read(T0, false, WingRefusal.None, true, true));
        }

        [Fact]
        public void RepeatLines_AreCounted_AndWrittenOncePerInterval()
        {
            var c = new StationCall(T0);
            Assert.False(c.Repeat(T0 + TimeSpan.FromSeconds(1)));
            Assert.False(c.Repeat(T0 + TimeSpan.FromSeconds(2)));
            Assert.True(c.Repeat(T0 + StationCallRules.RepeatLogInterval));
            Assert.Equal(3, c.TakeRepeats(T0 + StationCallRules.RepeatLogInterval));
            Assert.False(c.Repeat(T0 + StationCallRules.RepeatLogInterval + TimeSpan.FromSeconds(1)));
        }

        // ---- the Defend focus -----------------------------------------------------------------

        private static FactionTuning FocusTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.PlayerCampaignCap = 2;
            t.PlayerCampaignDuration = TimeSpan.FromHours(1);
            return t;
        }

        private static WorldModel FocusWorld(out FactionState st, bool player = true)
        {
            WorldModel w = TestSupport.World();
            AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            AddNode(w, "N003", "DEF", new Vec3(20, 0, 0));
            w.Graph.Connect("HOME", "N002");
            w.Graph.Connect("N002", "N003");
            st = new FactionState { Tag = "DEF", RngSeed = 1, IsPlayerFaction = player };
            return w;
        }

        private static StationCallFocus Call(FactionState st, WorldModel w, FactionTuning t, DateTime now, string node)
        {
            CampaignFocus f;
            OffensiveCampaigns.PlayerResult r;
            return StationCallRules.PlaceOrRefreshFocus(st, t, now, w, node,
                w.ComputeCampaignRegionMembers(st.Tag, node, t.CampaignRegionTraverseDepth), out f, out r);
        }

        [Fact]
        public void RepeatCalls_PlaceTheFocusOnce_AndRefreshIt()
        {
            FactionState st; WorldModel w = FocusWorld(out st); FactionTuning t = FocusTuning();
            DateTime now = w.UtcNow;

            Assert.Equal(StationCallFocus.Placed, Call(st, w, t, now, "N002"));
            CampaignFocus f = Assert.Single(st.CampaignFoci);
            Assert.True(f.PlayerIssued);
            Assert.Equal(now + t.PlayerCampaignDuration, f.ExpiresAtUtc);

            DateTime later = now + TimeSpan.FromMinutes(20);
            Assert.Equal(StationCallFocus.Refreshed, Call(st, w, t, later, "N002"));
            Assert.Equal(StationCallFocus.Refreshed, Call(st, w, t, later, "N002"));
            Assert.Same(f, Assert.Single(st.CampaignFoci));
            Assert.Equal(later + t.PlayerCampaignDuration, f.ExpiresAtUtc);

            Assert.True(StationCallRules.FocusLive(st, "N002", later));
            Assert.False(StationCallRules.FocusLive(st, "N002", later + t.PlayerCampaignDuration));
        }

        [Fact]
        public void ANodeAnotherFocusCovers_IsLeftToIt()
        {
            FactionState st; WorldModel w = FocusWorld(out st); FactionTuning t = FocusTuning();
            Assert.Equal(StationCallFocus.Placed, Call(st, w, t, w.UtcNow, "N002"));
            CampaignFocus first = Assert.Single(st.CampaignFoci);
            DateTime expiry = first.ExpiresAtUtc;
            // N003 is inside N002's region: the coverage dedup refuses a second circuit, and the
            // focus centred elsewhere is not the call's to refresh.
            Assert.Contains("N003", first.RegionMembers);
            Assert.Equal(StationCallFocus.Covered, Call(st, w, t, w.UtcNow + TimeSpan.FromMinutes(5), "N003"));
            Assert.Single(st.CampaignFoci);
            Assert.Equal(expiry, first.ExpiresAtUtc);
        }

        [Fact]
        public void AnNpcStationsCall_PlacesNoPlayerFocus()
        {
            FactionState st; WorldModel w = FocusWorld(out st, player: false); FactionTuning t = FocusTuning();
            Assert.Equal(StationCallFocus.NotPlayer, Call(st, w, t, w.UtcNow, "N002"));
            Assert.Empty(st.CampaignFoci);
        }

        [Fact]
        public void RefreshPlayer_NeverShortensAFocus()
        {
            FactionState st; WorldModel w = FocusWorld(out st); FactionTuning t = FocusTuning();
            Call(st, w, t, w.UtcNow + TimeSpan.FromMinutes(30), "N002");
            DateTime expiry = st.CampaignFoci[0].ExpiresAtUtc;
            Assert.NotNull(CampaignFocusLifecycle.RefreshPlayer(st, t, w.UtcNow, "N002"));
            Assert.Equal(expiry, st.CampaignFoci[0].ExpiresAtUtc);
            Assert.Null(CampaignFocusLifecycle.RefreshPlayer(st, t, w.UtcNow, "HOME"));
        }

        // ---- the reactive Guard ---------------------------------------------------------------

        private sealed class Cat : IPrefabCatalogue
        {
            public readonly HashSet<string> Armed = new HashSet<string>();
            public long CargoLiters(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return Armed.Contains(prefabId)
                    ? new[] { new WeaponBand { Dps = 500f, RangeM = 800 } }
                    : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static SpatialNode AddNode(WorldModel w, string id, string owner, Vec3 pos)
        {
            SpatialNode n = w.Graph.Add(pos, 0.6, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 0.9;
            n.Radius = 40;
            return n;
        }

        internal static FactionTuning GuardTuning()
        {
            FactionTuning t = TestSupport.Tuning();
            t.GuardCost = 1000;
            t.FleetSpeedKmPerMin = 1200.0;
            t.GuardMaxIncomeFractionPerWindow = 0; t.GuardBudgetFloor = 0;
            t.PatrolMaxIncomeFractionPerWindow = 0; t.PatrolBudgetFloor = 0;
            t.ReinforceMaxIncomeFractionPerWindow = 0; t.ReinforceBudgetFloor = 0;
            return t;
        }

        internal static WorldModel DefenderWorld()
        {
            WorldModel w = TestSupport.World();
            w.SetTreasury("DEF", 500000);
            SpatialNode home = AddNode(w, "HOME", "DEF", new Vec3(0, 0, 0));
            AddNode(w, "N002", "DEF", new Vec3(10, 0, 0));
            w.Graph.Connect("HOME", "N002");
            home.ShipStock["Gunship"] = 2;
            var cat = new Cat();
            cat.Armed.Add("Gunship");
            w.PrefabCatalogue = cat;
            return w;
        }

        internal static FactionState Raider(WorldModel w, string targetNodeId)
        {
            var st = new FactionState { Tag = "RAD", RngSeed = 1 };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                ActorTag = "RAD",
                TargetNodeId = targetNodeId,
                OriginPosKm = new Vec3(500, 0, 0),
                TargetPosKm = new Vec3(10, 0, 0),
                DepartedUtc = w.UtcNow,
                EstimatedArrivalUtc = w.UtcNow.AddMinutes(3),
                FleetId = "RAID1",
                ShipPrefabId = "Gunship",
                WeaponBands = new List<WeaponBand> { new WeaponBand { Dps = 100f, RangeM = 800 } },
                LastResolvedSegment = -1,
            });
            return st;
        }

        internal static List<string> GuardLines(List<string> log)
        {
            return log.Where(m => m.Contains("GUARD_DISPATCH")).ToList();
        }

        // With nothing else happening in the world, a calling node draws a Guard, and the dispatch
        // line names the call.
        [Fact]
        public void ACallingNode_DrawsAReactiveGuard()
        {
            WorldModel w = DefenderWorld();
            FactionTuning t = GuardTuning();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };

            var log = new List<string>();
            new FleetManager().Tick(new[] { def }, w, t, log);
            Assert.Empty(GuardLines(log));   // control: no call, nothing to answer

            w.SetStationCall("N002", null);
            new FleetManager().Tick(new[] { def }, w, t, log);
            string line = Assert.Single(GuardLines(log));
            Assert.Contains("-> N002", line);
            Assert.Contains("vs=station_call", line);
            Assert.Single(def.PendingFleets.Where(f => f.Kind == ActionKind.Guard && f.TargetNodeId == "N002"));
        }

        // A repeat call on a node already answered does not buy a second Guard.
        [Fact]
        public void ARepeatCall_DoesNotDuplicateTheGuard()
        {
            WorldModel w = DefenderWorld();
            FactionTuning t = GuardTuning();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            var fm = new FleetManager();

            w.SetStationCall("N002", null);
            var log = new List<string>();
            fm.Tick(new[] { def }, w, t, log);
            w.SetStationCall("N002", null);
            w.UtcNow = w.UtcNow.AddSeconds(30);
            fm.Tick(new[] { def }, w, t, log);

            Assert.Single(GuardLines(log));
            Assert.Single(def.PendingFleets.Where(f => f.Kind == ActionKind.Guard));
        }

        // A call that names the attacking fleet sizes the defence against it: the line carries that
        // fleet, not the stand-in.
        [Fact]
        public void ACallNamingItsAttacker_IsAnsweredAgainstThatFleet()
        {
            WorldModel w = DefenderWorld();
            FactionTuning t = GuardTuning();
            var def = new FactionState { Tag = "DEF", RngSeed = 1 };
            FactionState rad = Raider(w, "N002");
            // A bench raid without react=1: not an inbound threat on its own.
            PendingFleetAction raid = rad.PendingFleets[0];
            raid.BenchLabel = "b1";
            rad.PendingFleets[0] = raid;

            var log = new List<string>();
            new FleetManager().Tick(new[] { def, rad }, w, t, log);
            Assert.Empty(GuardLines(log));   // control: the bench raid draws nothing on its own

            w.SetStationCall("N002", "RAID1");
            new FleetManager().Tick(new[] { def, rad }, w, t, log);
            string line = Assert.Single(GuardLines(log));
            Assert.Contains("vs=RAD", line);
            Assert.Contains("threatFleet=RAID1", line);
        }

        // The raider's counterplay: an inbound raid on a silenced node draws no Guard; the same raid
        // on the same node, not silenced, does.
        [Fact]
        public void ASilencedNode_GetsNoReactiveGuard_EvenAgainstAnInboundRaid()
        {
            FactionTuning t = GuardTuning();

            WorldModel open = DefenderWorld();
            var log = new List<string>();
            new FleetManager().Tick(new[] { new FactionState { Tag = "DEF", RngSeed = 1 }, Raider(open, "N002") }, open, t, log);
            Assert.Single(GuardLines(log));

            WorldModel cut = DefenderWorld();
            cut.SetSilencedStations(new[] { "N002" });
            log.Clear();
            new FleetManager().Tick(new[] { new FactionState { Tag = "DEF", RngSeed = 1 }, Raider(cut, "N002") }, cut, t, log);
            Assert.Empty(GuardLines(log));

            // Silence outranks a call too: a station that cannot call is not answered.
            cut.SetStationCall("N002", null);
            new FleetManager().Tick(new[] { new FactionState { Tag = "DEF", RngSeed = 1 } }, cut, t, log);
            Assert.Empty(GuardLines(log));

            cut.SetSilencedStations(null);
            Assert.False(cut.IsStationSilenced("N002"));
        }

        [Fact]
        public void AClearedCall_DrawsNothing()
        {
            WorldModel w = DefenderWorld();
            FactionTuning t = GuardTuning();
            w.SetStationCall("N002", null);
            Assert.True(w.AnyStationCall);
            w.ClearStationCall("N002");
            Assert.False(w.AnyStationCall);
            string id;
            Assert.False(w.IsStationCalling("N002", out id));

            var log = new List<string>();
            new FleetManager().Tick(new[] { new FactionState { Tag = "DEF", RngSeed = 1 } }, w, t, log);
            Assert.Empty(GuardLines(log));
        }

        // ---- the action ---------------------------------------------------------------------

        // A toolbar slot saves the action id: adding protect me must not move any other command.
        [Fact]
        public void ProtectMe_IsAppended_WithoutRenumberingTheOthers()
        {
            Assert.Equal(1, (int)WireWingCommand.FormOnMe);
            Assert.Equal(2, (int)WireWingCommand.FocusTarget);
            Assert.Equal(3, (int)WireWingCommand.Hold);
            Assert.Equal(4, (int)WireWingCommand.BreakOff);
            Assert.Equal(5, (int)WireWingCommand.Return);
            Assert.Equal(6, (int)WireWingCommand.Release);
            Assert.Equal(7, (int)WireWingCommand.ProtectMe);
            Assert.Equal("FactionsWingProtectMe", WingActions.Id(WireWingCommand.ProtectMe));
            Assert.Equal("Wing: protect me", WingActions.Name(WireWingCommand.ProtectMe));
            Assert.Equal(WireWingCommand.ProtectMe, WingActions.All[WingActions.All.Length - 1]);
            Assert.True(WingActions.IsKnown(7));
            Assert.False(WingActions.IsKnown(8));

            WingCommand c;
            Assert.True(WingRules.TryParse("protect", out c));
            Assert.Equal(WingCommand.ProtectMe, c);
            Assert.Equal("protect", WingRules.Token(WingCommand.ProtectMe));
            Assert.False(WingRules.NeedsTarget(WingCommand.ProtectMe));
            Assert.False(WingRules.EndsOrder(WingCommand.ProtectMe));
        }
    }
}
