using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The owner's answer to a player occupying its ground (FAC-483): a hail per hold strip, a
    /// reputation charge per strip at SE's own damage rate, and the occupation remembered as a raid
    /// against the occupier's faction once it ends.
    /// </summary>
    public class OccupationTests
    {
        private static SpatialNode AddOwned(WorldModel w, string id, string owner)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.5, id);
            n.OwnerFactionTag = owner;
            n.HoldStrength = 1.0;
            return n;
        }

        // ── the hail ────────────────────────────────────────────────────────────

        [Fact]
        public void TheFirstStripWarns_AndEveryLaterOneSaysItWillBeRemembered()
        {
            Assert.Equal("You are occupying Mercury Trade ground. You will be treated as an aggressor.",
                         Occupation.HailText(1, "Mercury Trade"));
            Assert.Equal("You are still on Mercury Trade ground. This will not be forgotten.",
                         Occupation.HailText(2, "Mercury Trade"));
            Assert.Equal(Occupation.HailText(2, "X"), Occupation.HailText(3, "X"));
            Assert.Null(Occupation.HailText(0, "X"));
            Assert.True(Occupation.IsFirstStrip(1));
            Assert.False(Occupation.IsFirstStrip(2));
        }

        // ── the charge ──────────────────────────────────────────────────────────

        /// <summary>SE's shipped settings: 1 reputation per 200 damage. A station-path strip is
        /// 20,000 block damage, so SE charges 100 for it, and a presence strip costs the same.</summary>
        [Fact]
        public void AStripCosts_WhatSeChargesForTheSameStripShotOffAStation()
        {
            Assert.Equal(100, Occupation.ReputationPerStrip(20000.0, 200.0, 1));
            Assert.Equal(200, Occupation.ReputationPerStrip(20000.0, 200.0, 2));
            Assert.Equal(99, Occupation.ReputationPerStrip(19999.0, 200.0, 1));   // whole steps only
        }

        [Fact]
        public void AnUnsetInput_ChargesNothing()
        {
            Assert.Equal(0, Occupation.ReputationPerStrip(0.0, 200.0, 1));
            Assert.Equal(0, Occupation.ReputationPerStrip(20000.0, 0.0, 1));
            Assert.Equal(0, Occupation.ReputationPerStrip(20000.0, 200.0, 0));
        }

        // ── the strip clock ─────────────────────────────────────────────────────

        [Fact]
        public void TheStripsAreCounted_OpeningOneIncluded_AndClearedWithTheRaid()
        {
            WorldModel w = TestSupport.World();
            w.Tuning.RaidZoneSiegeDwellSeconds = 300.0;
            SpatialNode n = AddOwned(w, "N001", "DEF");
            DateTime t0 = w.UtcNow;

            Assert.True(w.BeginPresenceRaid("N001", "PLYR"));
            Assert.Equal(1, n.PresenceStrips);

            var seen = new List<int>();
            for (int sec = 0; sec < 900; sec += 60)
            {
                w.UtcNow = t0.AddSeconds(sec);
                w.MarkPlayerPresenceAttack("N001", "PLYR");
                if (seen.Count == 0 || seen[seen.Count - 1] != n.PresenceStrips) seen.Add(n.PresenceStrips);
            }
            Assert.Equal(new[] { 1, 2, 3 }, seen);                 // opening, +300 s, +600 s

            PlayerSiege.Reset(n);
            Assert.Equal(0, n.PresenceStrips);
        }

        // ── the record ──────────────────────────────────────────────────────────

        /// <summary><c>RecordEvent</c> files nothing without an actor, so a presence raid recorded
        /// nothing at all until the occupier's faction was carried as one.</summary>
        [Fact]
        public void EachStripIsARaidResolved_ByTheOccupiersFaction()
        {
            WorldModel w = TestSupport.World();
            w.Tuning.RaidZoneSiegeDwellSeconds = 300.0;
            AddOwned(w, "N001", "DEF");
            DateTime t0 = w.UtcNow;
            w.ClearPendingEvents();

            w.BeginPresenceRaid("N001", "PLYR");
            w.UtcNow = t0.AddSeconds(300);
            w.MarkPlayerPresenceAttack("N001", "PLYR");

            List<FactionEvent> raids = w.PendingEvents.Where(e => e.Kind == FactionEventKind.RaidResolved).ToList();
            Assert.Equal(2, raids.Count);
            Assert.All(raids, e => { Assert.Equal("PLYR", e.ActorTag); Assert.Equal("DEF", e.OtherTag); });
        }

        [Fact]
        public void AnEndedOccupation_NamesTheOccupierAsARecentAttacker_OfTheOwner()
        {
            var owner = new FactionState { Tag = "DEF" };
            var player = new FactionState { Tag = "PLYR", IsPlayerFaction = true };
            var node = new SpatialNode { Id = "N001", OwnerFactionTag = "DEF" };
            DateTime now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(FightLedger.RecordOccupation(owner, player, node, false, now, 50));

            Assert.Contains("PLYR", FightLedger.RecentAttackers(owner, now.AddMinutes(-1)));
            FightRecord theirs = Assert.Single(FightLedger.DefendedSince(owner, now.AddMinutes(-1)));
            Assert.Equal("N001", theirs.NodeId);
            Assert.True(theirs.Won);                                // the owner kept it
            FightRecord ours = Assert.Single(player.Fights);
            Assert.True(ours.Attacking);
            Assert.Equal("DEF", ours.OpponentTag);
        }

        [Fact]
        public void ATakenNode_IsTheOccupiersWin()
        {
            var owner = new FactionState { Tag = "DEF" };
            var player = new FactionState { Tag = "PLYR", IsPlayerFaction = true };
            var node = new SpatialNode { Id = "N001", OwnerFactionTag = "DEF" };

            FightLedger.RecordOccupation(owner, player, node, true, DateTime.UtcNow, 50);

            Assert.True(player.Fights[0].Won);
            Assert.False(owner.Fights[0].Won);
        }

        /// <summary>Retaliation targets ground, and only a registered faction holds any: an occupier
        /// with no registered faction leaves nothing to answer.</summary>
        [Fact]
        public void AnUnregisteredOccupier_IsNotRecorded()
        {
            var owner = new FactionState { Tag = "DEF" };
            var node = new SpatialNode { Id = "N001", OwnerFactionTag = "DEF" };

            Assert.False(FightLedger.RecordOccupation(owner, null, node, false, DateTime.UtcNow, 50));
            Assert.Empty(owner.Fights);
        }
    }
}
