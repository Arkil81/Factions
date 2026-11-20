using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The wing commander's rules (docs/fleet-tactics.md, Package 2): when an antenna carries an
    /// order, which hulls it reaches, which target a command names and whether it may, the per-hull
    /// link and its grace, the order as the resolver's top rung, a commanded duel learned as a
    /// demonstration, and the wire. Every clock is explicit; nothing touches a static switch.
    /// </summary>
    public class WingCommandTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        private const long Commander = 1001;

        private static WingAntennaReading Working()
        {
            return new WingAntennaReading
            {
                Exists = true, Functional = true, Enabled = true, Powered = true, Broadcasting = true,
                RadiusM = 5000, OwnerIdentityId = Commander, OwnerFactionTag = "PLYR", PosM = new Vec3(0, 0, 0),
            };
        }

        // ---- the antenna ---------------------------------------------------------------------------

        [Fact]
        public void Antenna_CarriesTheOrder_OnlyWhenFunctionalOnPoweredBroadcastingAndOurs()
        {
            Assert.Equal(WingRefusal.None, WingRules.CheckAntenna(Working(), Commander, "PLYR"));

            WingAntennaReading a = Working(); a.Exists = false;
            Assert.Equal(WingRefusal.NoAntenna, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a = Working(); a.Functional = false;
            Assert.Equal(WingRefusal.AntennaDamaged, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a = Working(); a.Enabled = false;
            Assert.Equal(WingRefusal.AntennaOff, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a = Working(); a.Powered = false;
            Assert.Equal(WingRefusal.AntennaUnpowered, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a = Working(); a.Broadcasting = false;
            Assert.Equal(WingRefusal.NotBroadcasting, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a = Working(); a.RadiusM = 0;
            Assert.Equal(WingRefusal.NotBroadcasting, WingRules.CheckAntenna(a, Commander, "PLYR"));
        }

        [Fact]
        public void Antenna_IsTheCommandersOrTheirFactions_AndNobodyElses()
        {
            WingAntennaReading a = Working();
            a.OwnerIdentityId = 2002;                       // a faction mate's antenna
            Assert.Equal(WingRefusal.None, WingRules.CheckAntenna(a, Commander, "plyr"));
            a.OwnerFactionTag = "RIVL";                     // someone else's
            Assert.Equal(WingRefusal.NotYourAntenna, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a.OwnerFactionTag = null;
            Assert.Equal(WingRefusal.NotYourAntenna, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a.OwnerIdentityId = Commander;                  // the commander's own, factionless
            Assert.Equal(WingRefusal.None, WingRules.CheckAntenna(a, Commander, "PLYR"));
            a.OwnerIdentityId = 0;                          // nobody's
            Assert.Equal(WingRefusal.NotYourAntenna, WingRules.CheckAntenna(a, 0, null));
        }

        [Fact]
        public void Range_IsTheBroadcastRadius_PerHull()
        {
            WingAntennaReading a = Working();
            Assert.True(WingRules.InRange(a, new Vec3(4999, 0, 0)));
            Assert.True(WingRules.InRange(a, new Vec3(3000, 4000, 0)));   // exactly on the radius
            Assert.False(WingRules.InRange(a, new Vec3(3000, 4000, 1)));
            a.RadiusM = 2000;                                             // the slider pulled in
            Assert.False(WingRules.InRange(a, new Vec3(2500, 0, 0)));
            a.RadiusM = 0;
            Assert.False(WingRules.InRange(a, new Vec3(0, 0, 0)));
        }

        // ---- the target ----------------------------------------------------------------------------

        [Fact]
        public void Target_MustExistBeHostileInTheBubbleAndSeen()
        {
            var t = new WingTargetReading { EntityId = 77, Exists = true, Hostile = true, PosM = new Vec3(4000, 0, 0) };
            Vec3 cmdr = new Vec3(0, 0, 0);
            Assert.Equal(WingRefusal.None, WingRules.CheckTarget(t, cmdr, null));

            var none = t; none.EntityId = 0;
            Assert.Equal(WingRefusal.NoTarget, WingRules.CheckTarget(none, cmdr, null));
            var gone = t; gone.Exists = false;
            Assert.Equal(WingRefusal.TargetGone, WingRules.CheckTarget(gone, cmdr, null));
            var friend = t; friend.Hostile = false;
            Assert.Equal(WingRefusal.TargetNotHostile, WingRules.CheckTarget(friend, cmdr, null));
            var far = t; far.PosM = new Vec3(WingRules.TargetBubbleM + 1, 0, 0);
            Assert.Equal(WingRefusal.TargetOutOfBubble, WingRules.CheckTarget(far, cmdr, new List<Vec3> { far.PosM }));

            // Past the commander's own sight, inside the bubble: seen only through a wing hull.
            var unseen = t; unseen.PosM = new Vec3(WingRules.TargetSightM + 500, 0, 0);
            Assert.Equal(WingRefusal.TargetNotVisible, WingRules.CheckTarget(unseen, cmdr, new List<Vec3>()));
            Assert.Equal(WingRefusal.TargetNotVisible,
                         WingRules.CheckTarget(unseen, cmdr, new List<Vec3> { new Vec3(-5000, 0, 0) }));
            Assert.Equal(WingRefusal.None,
                         WingRules.CheckTarget(unseen, cmdr, new List<Vec3> { new Vec3(-5000, 0, 0), new Vec3(4000, 0, 0) }));
        }

        [Fact]
        public void MyTarget_FromAPress_IsTheLockThenTheCrosshair()
        {
            WingTargetSource src;
            Assert.Equal(11, WingRules.PickTarget(WingVia.Client, 11, 22, 33, 44, 55, out src));
            Assert.Equal(WingTargetSource.PresserLock, src);
            Assert.Equal(22, WingRules.PickTarget(WingVia.Client, 0, 22, 33, 44, 55, out src));
            Assert.Equal(WingTargetSource.Crosshair, src);
            // A press never reads the grid's own locks: those are a block trigger's.
            Assert.Equal(0, WingRules.PickTarget(WingVia.Client, 0, 0, 33, 44, 55, out src));
            Assert.Equal(WingTargetSource.None, src);
            Assert.Equal(11, WingRules.PickTarget(WingVia.Bench, 11, 0, 33, 0, 0, out src));
            Assert.Equal(WingTargetSource.PresserLock, src);
        }

        [Fact]
        public void MyTarget_FromABlock_IsThePilotThenACombatBlockThenTheCockpitsCopy_ElseRefused()
        {
            WingTargetSource src;
            Assert.Equal(33, WingRules.PickTarget(WingVia.Block, 11, 22, 33, 44, 55, out src));
            Assert.Equal(WingTargetSource.PilotLock, src);
            Assert.Equal(44, WingRules.PickTarget(WingVia.Block, 11, 22, 0, 44, 55, out src));
            Assert.Equal(WingTargetSource.CombatBlockLock, src);
            Assert.Equal(55, WingRules.PickTarget(WingVia.Block, 11, 22, 0, 0, 55, out src));
            Assert.Equal(WingTargetSource.CockpitRetained, src);
            // No presser behind a block: its lock and crosshair are not read.
            Assert.Equal(0, WingRules.PickTarget(WingVia.Block, 11, 22, 0, 0, 0, out src));
            Assert.Equal(WingTargetSource.None, src);
        }

        // ---- the link ------------------------------------------------------------------------------

        [Fact]
        public void Link_HoldsTheLastOrderForTheGrace_ThenLapsesOnce()
        {
            var o = new WingOrder();
            Assert.Equal(WingLinkChange.None, o.UpdateLink(7, false, WingRefusal.OutOfRange, T0));  // never held
            Assert.False(o.Obeys(7));

            Assert.Equal(WingLinkChange.Joined, o.UpdateLink(7, true, WingRefusal.None, T0));
            Assert.True(o.Obeys(7));
            Assert.True(o.Holds(7));

            DateTime lost = T0.AddSeconds(30);
            Assert.Equal(WingLinkChange.Dropped, o.UpdateLink(7, false, WingRefusal.AntennaOff, lost));
            Assert.True(o.Obeys(7));
            Assert.False(o.Holds(7));
            Assert.Equal(WingLinkChange.None, o.UpdateLink(7, false, WingRefusal.AntennaOff, lost + WingRules.LinkGrace - TimeSpan.FromMilliseconds(1)));
            Assert.True(o.Obeys(7));

            Assert.Equal(WingLinkChange.Lapsed, o.UpdateLink(7, false, WingRefusal.AntennaOff, lost + WingRules.LinkGrace));
            Assert.False(o.Obeys(7));
            Assert.False(o.AnyObeys());
            // Reported once: later reads while still out say nothing.
            Assert.Equal(WingLinkChange.None, o.UpdateLink(7, false, WingRefusal.AntennaOff, lost + WingRules.LinkGrace + TimeSpan.FromSeconds(5)));

            Assert.Equal(WingLinkChange.Joined, o.UpdateLink(7, true, WingRefusal.None, lost.AddMinutes(1)));
            Assert.True(o.Holds(7));
        }

        [Fact]
        public void Link_ThatComesBackInsideTheGrace_IsRestored_AndTheClockStartsAgain()
        {
            var o = new WingOrder();
            o.UpdateLink(7, true, WingRefusal.None, T0);
            o.UpdateLink(7, false, WingRefusal.AntennaUnpowered, T0.AddSeconds(1));
            Assert.Equal(WingLinkChange.Restored, o.UpdateLink(7, true, WingRefusal.None, T0.AddSeconds(8)));
            Assert.True(o.Holds(7));
            // A second flicker gets a whole grace of its own, not what was left of the first.
            o.UpdateLink(7, false, WingRefusal.AntennaUnpowered, T0.AddSeconds(9));
            Assert.Equal(WingLinkChange.None, o.UpdateLink(7, false, WingRefusal.AntennaUnpowered, T0.AddSeconds(18)));
            Assert.True(o.Obeys(7));
            Assert.Equal(WingLinkChange.Lapsed, o.UpdateLink(7, false, WingRefusal.AntennaUnpowered, T0.AddSeconds(19)));
        }

        [Fact]
        public void Link_IsPerHull_OneHullOutOfRangeLapsesAlone()
        {
            var o = new WingOrder();
            o.UpdateLink(1, true, WingRefusal.None, T0);
            o.UpdateLink(2, true, WingRefusal.None, T0);
            o.UpdateLink(1, true, WingRefusal.None, T0.AddSeconds(1));
            o.UpdateLink(2, false, WingRefusal.OutOfRange, T0.AddSeconds(1));
            o.UpdateLink(1, true, WingRefusal.None, T0.AddSeconds(11));
            Assert.Equal(WingLinkChange.Lapsed, o.UpdateLink(2, false, WingRefusal.OutOfRange, T0.AddSeconds(11)));
            Assert.True(o.Obeys(1));
            Assert.False(o.Obeys(2));
            Assert.True(o.AnyObeys());
            o.Forget(1);
            Assert.False(o.AnyObeys());
        }

        // ---- the order and the resolver ------------------------------------------------------------

        [Fact]
        public void Commands_Compose_FormThenFocusAttacksThenFallsIn_HoldAndBreakOffDropTheFormation()
        {
            var o = new WingOrder();
            o.Apply(WingCommand.FormOnMe, 500, 0, T0);
            Assert.Equal(500, o.AnchorEntityId);
            o.Apply(WingCommand.FocusTarget, 500, 900, T0);
            Assert.Equal(500, o.AnchorEntityId);
            Assert.Equal(900, o.TargetEntityId);

            o.Apply(WingCommand.Hold, 500, 0, T0);
            Assert.Equal(0, o.AnchorEntityId);
            Assert.Equal(900, o.TargetEntityId);     // holds at range on the target
            Assert.True(o.Holding);

            o.Apply(WingCommand.BreakOff, 500, 0, T0);
            Assert.False(o.Holding);
            Assert.True(o.BreakingOff);
            Assert.Equal(900, o.TargetEntityId);     // what it breaks off from

            o.Apply(WingCommand.FormOnMe, 501, 0, T0);
            Assert.False(o.BreakingOff);
            Assert.Equal(501, o.AnchorEntityId);
            Assert.True(WingRules.EndsOrder(WingCommand.Return));
            Assert.True(WingRules.EndsOrder(WingCommand.Release));
            Assert.False(WingRules.EndsOrder(WingCommand.Hold));
        }

        [Fact]
        public void Resolver_TheCommandersOrderOutranksTheOverrideAndTheLearnedPick()
        {
            var o = new WingOrder();
            o.Apply(WingCommand.FormOnMe, 500, 0, T0);
            o.Apply(WingCommand.FocusTarget, 500, 900, T0);
            TacticOrder commander = o.ToTacticOrder(TacticArm.Kite, BreakOffLevel.Low);
            var request = new TacticRequest
            {
                TargetEntityId = 111,
                AnchorEntityId = 222,
                Commander = commander,
                PlayerOverride = new TacticOrder { Arm = TacticArm.Brawl, BreakOff = BreakOffLevel.High },
            };
            TacticOrder resolved = TacticResolver.Resolve(request);
            Assert.Equal(TacticSource.Commander, resolved.Source);
            Assert.Equal(900, resolved.TargetEntityId);     // the commander's target, not the acquired one
            Assert.Equal(500, resolved.AnchorEntityId);     // the commander's grid, not the lead
            Assert.Equal(TacticArm.Kite, resolved.Arm);     // focus keeps the arm the learned rung gave
            Assert.Equal(500, TacticResolver.ResolveAnchor(request));

            request.Commander = null;
            Assert.Equal(TacticSource.PlayerOverride, TacticResolver.Resolve(request).Source);
            request.PlayerOverride = null;
            TacticOrder learned = TacticResolver.Resolve(request);
            Assert.Equal(TacticSource.Learned, learned.Source);
            Assert.Equal(111, learned.TargetEntityId);
        }

        [Fact]
        public void Resolver_HoldFightsStandoff_BreakOffLeavesHigh_AnUnaimedOrderTakesTheAcquiredTarget()
        {
            var o = new WingOrder();
            o.Apply(WingCommand.Hold, 500, 0, T0);
            TacticOrder hold = TacticResolver.Resolve(new TacticRequest
            {
                TargetEntityId = 111, AnchorEntityId = 222,
                Commander = o.ToTacticOrder(TacticArm.Brawl, BreakOffLevel.Low),
            });
            Assert.Equal(TacticArm.Standoff, hold.Arm);
            Assert.Equal(BreakOffLevel.Low, hold.BreakOff);
            Assert.Equal(111, hold.TargetEntityId);
            Assert.Equal(222, hold.AnchorEntityId);

            o.Apply(WingCommand.BreakOff, 500, 0, T0);
            CommandedTactic t = o.Tactic(TacticArm.Brawl, BreakOffLevel.Low);
            Assert.Equal(TacticArm.Brawl, t.Arm);
            Assert.Equal(BreakOffLevel.High, t.BreakOff);
        }

        // ---- learning ------------------------------------------------------------------------------

        [Fact]
        public void ACommandedDuel_FightsTheCommandedArm_AndIsLearnedAtHalfWeight()
        {
            // Once uncommanded, to read what the learned rung picks and what that fight weighs; then
            // the same fight commanded to fight that very tactic: the same duel, half the evidence.
            var cat = new Catalogue().Add("gunboat", 5000, 100, 800);
            FightRecord plain;
            double plainEvidence = Evidence(cat, null, out plain);
            var commanded = new CommandedTactic { Arm = plain.Tactic.AttackerArm, BreakOff = plain.Tactic.AttackerBreakOff };
            FightRecord shown;
            double shownEvidence = Evidence(cat, commanded, out shown);

            Assert.True(plainEvidence > 0);
            Assert.Equal(plainEvidence * TacticMemory.DemonstrationWeight, shownEvidence, 9);
            Assert.Equal(plain.AttackerRatio, shown.AttackerRatio);

            // A commanded arm the faction would not have picked is the one fought and recorded.
            TacticArm other = plain.Tactic.AttackerArm == TacticArm.Kite ? TacticArm.Brawl : TacticArm.Kite;
            FightRecord forced;
            Evidence(cat, new CommandedTactic { Arm = other, BreakOff = BreakOffLevel.High }, out forced);
            Assert.Equal(other, forced.Tactic.AttackerArm);
            Assert.Equal(BreakOffLevel.High, forced.Tactic.AttackerBreakOff);
        }

        /// <summary>One raid on a Guard; the evidence the attacker's memory gained in the cell it fought.</summary>
        private static double Evidence(Catalogue cat, CommandedTactic? commanded, out FightRecord record)
        {
            WorldModel w = ResolverWorld(cat);
            var def = new FactionState { Tag = "MERC", PersonalityId = "factorum" };
            var atk = new FactionState { Tag = "RVN", PersonalityId = "raider" };
            def.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Guard, ActorTag = def.Tag, TargetNodeId = "MERC1", FleetId = "G1",
                ShipPrefabId = "gunboat", ConvoySize = 1,
                DepartedUtc = T0.AddMinutes(-5), EstimatedArrivalUtc = T0.AddMinutes(-5),
                Resolved = true, WeaponBands = Bands(100, 800),
            });
            atk.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid, ActorTag = atk.Tag, TargetTag = "MERC", TargetNodeId = "MERC1", FleetId = "R1",
                OriginNodeId = "RVN1", ShipPrefabId = "gunboat", ConvoySize = 1,
                OriginPosKm = new Vec3(0, 0, 0), TargetPosKm = new Vec3(0, 0, 0),
                DepartedUtc = T0, EstimatedArrivalUtc = T0, WeaponBands = Bands(100, 800),
                Commanded = commanded,
            });
            PendingFleetResolver.ResolveArrivals(new List<FactionState> { def, atk }, w, w.Tuning,
                                                 new FixedRandom(0.5), new List<string>(), null);
            record = Assert.Single(atk.Fights);
            Assert.Equal(1, atk.TacticMemory.Updates);
            TacticMemory.Opponent opp = atk.TacticMemory.Find("MERC");
            int idx = TacticMemory.Index(record.Tactic.AttackerArm, record.Tactic.AttackerBreakOff);
            double sum = 0;
            foreach (TacticMemory.Bucket b in opp.Buckets.Values)
                for (int i = 0; i < TacticMemory.Actions; i++)
                {
                    if (i != idx) Assert.Equal(0.0, b.A[i] + b.B[i], 9);
                    else sum += b.A[i] + b.B[i];
                }
            return sum;
        }

        private sealed class FixedRandom : IRandom
        {
            private readonly double _v;
            public FixedRandom(double v) { _v = v; }
            public double NextDouble() { return _v; }
        }

        private sealed class Catalogue : IPrefabCatalogue
        {
            private readonly Dictionary<string, List<WeaponBand>> _weapons = new Dictionary<string, List<WeaponBand>>();
            private readonly Dictionary<string, float> _thrust = new Dictionary<string, float>();
            public Catalogue Add(string id, float thrustN, float dps, float rangeM)
            {
                _weapons[id] = new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };
                _thrust[id] = thrustN;
                return this;
            }
            public long CargoLiters(string prefabId) { return 0; }
            public long Cost(string prefabId) { return 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                List<WeaponBand> w;
                return prefabId != null && _weapons.TryGetValue(prefabId, out w) ? w : new List<WeaponBand>();
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                var d = new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
                float n;
                if (prefabId != null && _thrust.TryGetValue(prefabId, out n))
                    d[ThrustDirection.Backward] = new List<ThrustBand>
                    {
                        new ThrustBand { ThrusterType = "Hydrogen", ForceMagnitudeN = n,
                                         EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f },
                    };
                return d;
            }
            public double DryMassKg(string prefabId) { return _thrust.ContainsKey(prefabId ?? "") ? 1000 : 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        private static WorldModel ResolverWorld(Catalogue cat)
        {
            WorldModel w = TestSupport.World();
            w.TacticLearning = true;
            w.PrefabCatalogue = cat;
            w.UtcNow = T0;
            foreach (var spec in new[] { new { Id = "MERC1", Owner = "MERC", X = 0.0 }, new { Id = "RVN1", Owner = "RVN", X = 500.0 } })
            {
                SpatialNode n = w.Graph.Add(new Vec3(spec.X, 0, 0), 0.6, spec.Id);
                n.OwnerFactionTag = spec.Owner;
                n.HoldStrength = 0.8;
                n.Radius = 40;
            }
            return w;
        }

        private static List<WeaponBand> Bands(float dps, float rangeM)
        {
            return new List<WeaponBand> { new WeaponBand { Dps = dps, RangeM = rangeM } };
        }

        // ---- words and the wire --------------------------------------------------------------------

        [Fact]
        public void BenchWords_ParseToTheCommands_AndTokensAreStable()
        {
            WingCommand c;
            Assert.True(WingRules.TryParse("form", out c)); Assert.Equal(WingCommand.FormOnMe, c);
            Assert.True(WingRules.TryParse("FOCUS", out c)); Assert.Equal(WingCommand.FocusTarget, c);
            Assert.True(WingRules.TryParse("hold", out c)); Assert.Equal(WingCommand.Hold, c);
            Assert.True(WingRules.TryParse("breakoff", out c)); Assert.Equal(WingCommand.BreakOff, c);
            Assert.True(WingRules.TryParse("break_off", out c)); Assert.Equal(WingCommand.BreakOff, c);
            Assert.True(WingRules.TryParse("return", out c)); Assert.Equal(WingCommand.Return, c);
            Assert.True(WingRules.TryParse("release", out c)); Assert.Equal(WingCommand.Release, c);
            Assert.False(WingRules.TryParse("cut", out c));
            Assert.False(WingRules.TryParse(null, out c));
            Assert.Equal("antenna_off", WingRules.Token(WingRefusal.AntennaOff));
            Assert.Equal("no_hulls_in_range", WingRules.Token(WingRefusal.NoHulls));
            Assert.Equal("presser_lock", WingRules.Token(WingTargetSource.PresserLock));
            Assert.Equal("break_off", WingRules.Token(WingCommand.BreakOff));
        }

        [Fact]
        public void WireCommands_MapOntoCoresByName_OneForOne()
        {
            foreach (WireWingCommand w in WingActions.All)
                Assert.Equal(w.ToString(), ((WingCommand)(int)w).ToString());
            Assert.Equal(Enum.GetValues(typeof(WingCommand)).Length, WingActions.All.Length);
        }

        [Fact]
        public void ActionIds_AreStable_Distinct_AndNamed()
        {
            // A toolbar slot is saved with the id: these strings are a save-format contract.
            Assert.Equal("FactionsWingFormOnMe", WingActions.Id(WireWingCommand.FormOnMe));
            Assert.Equal("FactionsWingRelease", WingActions.Id(WireWingCommand.Release));
            Assert.Equal(WingActions.All.Length, WingActions.All.Select(WingActions.Id).Distinct().Count());
            foreach (WireWingCommand c in WingActions.All)
                Assert.StartsWith("Wing: ", WingActions.Name(c), StringComparison.Ordinal);
        }

        [Fact]
        public void WingCommandRequest_RoundTrips_AndRefusesAnUnknownCommand()
        {
            foreach (WireWingCommand cmd in WingActions.All)
            {
                long antenna, crosshair;
                WireWingCommand back;
                Assert.True(FactionsWire.TryReadWingCommandRequest(
                    FactionsWire.WriteWingCommandRequest(123456789012L, cmd, -42L), out antenna, out back, out crosshair));
                Assert.Equal(123456789012L, antenna);
                Assert.Equal(cmd, back);
                Assert.Equal(-42L, crosshair);
            }

            byte[] data = FactionsWire.WriteWingCommandRequest(1, WireWingCommand.Hold, 0);
            data[6 + 8] = 99;                                   // header, antenna, then the command byte
            long a, x;
            WireWingCommand c;
            Assert.False(FactionsWire.TryReadWingCommandRequest(data, out a, out c, out x));
            Assert.False(FactionsWire.TryReadWingCommandRequest(new byte[] { 1, 2 }, out a, out c, out x));
            Assert.False(FactionsWire.TryReadWingCommandRequest(
                FactionsWire.WriteWingCommandResult(true, WireWingCommand.Hold, "x"), out a, out c, out x));
        }

        [Fact]
        public void WingCommandResult_RoundTrips()
        {
            bool ok;
            WireWingCommand cmd;
            string message;
            Assert.True(FactionsWire.TryReadWingCommandResult(
                FactionsWire.WriteWingCommandResult(true, WireWingCommand.FocusTarget, "Wing: 3 hull(s) attacking your target."),
                out ok, out cmd, out message));
            Assert.True(ok);
            Assert.Equal(WireWingCommand.FocusTarget, cmd);
            Assert.Equal("Wing: 3 hull(s) attacking your target.", message);

            Assert.True(FactionsWire.TryReadWingCommandResult(
                FactionsWire.WriteWingCommandResult(false, WireWingCommand.Release, null), out ok, out cmd, out message));
            Assert.False(ok);
            Assert.Equal("", message);

            WireMessageKind kind;
            int version;
            Assert.True(FactionsWire.TryReadHeader(FactionsWire.WriteWingCommandResult(true, WireWingCommand.Hold, ""), out kind, out version));
            Assert.Equal(WireMessageKind.WingCommandResult, kind);
            Assert.Equal(FactionsWire.ProtocolVersion, version);
        }
    }
}
