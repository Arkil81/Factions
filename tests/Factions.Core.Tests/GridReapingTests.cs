using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The reap rule, which decides what the simulation closes in a world it shares with players.
    /// Every case here is a grid class measured live on 2026-09-15: a rotor turret head off a
    /// despawned hull, a turret still on a flying one, a printed missile in a fight, a station, and
    /// a player docked to a convoy.
    /// </summary>
    public sealed class GridReapingTests
    {
        private const double Grace = 600.0;

        /// <summary>A group with nothing but the record set, old enough and unwatched — the default
        /// litter case every other test varies one field away from.</summary>
        private static ReapCandidate Litter()
        {
            return new ReapCandidate
            {
                EntityId   = 1234,
                Marked     = true,
                Bit        = true,
                AgeSeconds = 3600.0,
                Blocks     = 35,
            };
        }

        [Fact]
        public void MarkedAndUnheldIsReaped()
        {
            Assert.Equal(ReapVerdict.Reap, GridReaping.Judge(Litter(), Grace));
        }

        [Fact]
        public void AnUnmarkedGridIsNeverReaped()
        {
            ReapCandidate c = Litter();
            c.Marked = false;
            c.Bit    = false;
            Assert.Equal(ReapVerdict.NotSimBorn, GridReaping.Judge(c, Grace));
        }

        /// <summary>The bit alone is not ours. A vanilla Search contract spawns its target with
        /// <c>SetNpcSpawnedGrid</c>, owned by the station faction's founder, held by no fleet and
        /// carrying neither record nor name: reaping it left the player searching an empty GPS
        /// sphere. Container drops, escort waves and encounters have the same shape.</summary>
        [Fact]
        public void TheBitAloneIsNotSimBorn()
        {
            ReapCandidate c = Litter();
            c.Marked     = false;
            c.AgeSeconds = double.MaxValue;   // no record to date it
            Assert.True(c.Bit);
            Assert.Equal(ReapVerdict.NotSimBorn, GridReaping.Judge(c, Grace));
        }

        /// <summary>The bit beside a real carrier changes nothing: a marked hull still reaps.</summary>
        [Fact]
        public void TheBitBesideTheNameStillReaps()
        {
            ReapCandidate c = Litter();
            c.Marked       = false;
            c.NamedSimBorn = true;
            c.AgeSeconds   = double.MaxValue;
            Assert.Equal(ReapVerdict.Reap, GridReaping.Judge(c, Grace));
        }

        /// <summary>The entity name alone is enough, and this is the case the other two cannot
        /// cover: measured 13 s after the v0.232.0 boot, five <c>[NPC]</c>-named grids stood with no
        /// record (the process is new) and no bit (SE's claim timer cleared it, because a player
        /// faction's hull spawns owned by its founder). Record-or-bit skipped them for as long as
        /// they stood.</summary>
        [Fact]
        public void TheNameAloneIsSimBorn()
        {
            ReapCandidate c = Litter();
            c.Marked       = false;
            c.Bit          = false;
            c.NamedSimBorn = true;
            c.AgeSeconds   = double.MaxValue;   // no record to date it
            Assert.Equal(ReapVerdict.Reap, GridReaping.Judge(c, Grace));
        }

        /// <summary>A grid carrying none of the three is still nobody's business but its owner's —
        /// the name is a third carrier, not a looser rule.</summary>
        [Fact]
        public void NoCarrierIsStillNotSimBorn()
        {
            ReapCandidate c = Litter();
            c.Marked       = false;
            c.Bit          = false;
            c.NamedSimBorn = false;
            Assert.Equal(ReapVerdict.NotSimBorn, GridReaping.Judge(c, Grace));
        }

        /// <summary>Every refusal outranks the name, as it does the bit: a name-identified grid a
        /// fleet still flies is held, not litter.</summary>
        [Fact]
        public void ANameIdentifiedGridIsStillHeldAndStillAStation()
        {
            ReapCandidate c = Litter();
            c.Marked       = false;
            c.Bit          = false;
            c.NamedSimBorn = true;
            c.AgeSeconds   = double.MaxValue;

            ReapCandidate held = c;
            held.Held = true;
            Assert.Equal(ReapVerdict.Held, GridReaping.Judge(held, Grace));

            ReapCandidate station = c;
            station.Station = true;
            Assert.Equal(ReapVerdict.Station, GridReaping.Judge(station, Grace));

            ReapCandidate owned = c;
            owned.UnmarkedPlayerOwned = true;
            Assert.Equal(ReapVerdict.PlayerOwned, GridReaping.Judge(owned, Grace));
        }

        /// <summary>The whole reason ownership is not the predicate. A hull spawns as
        /// <c>faction.FounderId</c>, so a player faction's convoy is owned by a player identity by
        /// construction: "no player owner in the group" would refuse to close every hull the marshal
        /// flies for one. The host reads ownership off the <b>unmarked</b> members only, and these
        /// two candidates are the same group with the same player identity on it — once on the
        /// marked grids, once on an unmarked peer docked to them.</summary>
        [Fact]
        public void TheMarkTrumpsTheOwnerAndAnUnmarkedPeerBlocksTheGroup()
        {
            ReapCandidate playerOwnedButMarked = Litter();
            playerOwnedButMarked.UnmarkedPlayerOwned = false;

            ReapCandidate playerOwnedPeer = Litter();
            playerOwnedPeer.UnmarkedPlayerOwned = true;

            Assert.Equal(ReapVerdict.Reap,        GridReaping.Judge(playerOwnedButMarked, Grace));
            Assert.Equal(ReapVerdict.PlayerOwned, GridReaping.Judge(playerOwnedPeer, Grace));
        }

        /// <summary>A turret on a hull a fleet is still flying: the turret is its own grid and its own
        /// id is in no fleet record, so the group is what carries the hold.</summary>
        [Fact]
        public void HeldBeatsEverythingBelowIt()
        {
            ReapCandidate c = Litter();
            c.Held = true;
            c.UnmarkedPlayerOwned = true;
            Assert.Equal(ReapVerdict.Held, GridReaping.Judge(c, Grace));
        }

        /// <summary>A station is spawned from a prefab like a hull is, so it carries the bit and would
        /// otherwise read as exactly the litter the reaper exists to close.</summary>
        [Fact]
        public void AStationIsNeverReaped()
        {
            ReapCandidate c = Litter();
            c.Station = true;
            Assert.Equal(ReapVerdict.Station, GridReaping.Judge(c, Grace));

            c.AgeSeconds = 0.0;
            c.PlayerNear = true;
            Assert.Equal(ReapVerdict.Station, GridReaping.Judge(c, Grace));
        }

        [Fact]
        public void ARespawnGridIsNeverReaped()
        {
            ReapCandidate c = Litter();
            c.Respawn = true;
            Assert.Equal(ReapVerdict.Respawn, GridReaping.Judge(c, Grace));
        }

        /// <summary>A missile in a fight someone is watching is content, not litter.</summary>
        [Fact]
        public void YoungAndWatchedIsKept()
        {
            ReapCandidate c = Litter();
            c.AgeSeconds = 10.0;
            c.PlayerNear = true;
            Assert.Equal(ReapVerdict.TooYoung, GridReaping.Judge(c, Grace));
        }

        /// <summary>With nobody in range there is nothing for the grace to protect: the disjunction is
        /// what stops an empty server accumulating a fresh population between passes.</summary>
        [Fact]
        public void YoungWithNobodyNearIsReaped()
        {
            ReapCandidate c = Litter();
            c.AgeSeconds = 10.0;
            c.PlayerNear = false;
            Assert.Equal(ReapVerdict.Reap, GridReaping.Judge(c, Grace));
        }

        [Fact]
        public void OldAndWatchedIsReaped()
        {
            ReapCandidate c = Litter();
            c.AgeSeconds = Grace;
            c.PlayerNear = true;
            Assert.Equal(ReapVerdict.Reap, GridReaping.Judge(c, Grace));
        }

        /// <summary>SE names anything nobody renamed <c>Small Grid 1234</c>, and the live world held
        /// 208 of them under 2 names. A census that did not fold the suffix would print 208 rows.</summary>
        [Theory]
        [InlineData("Small Grid 1234",      "Small Grid")]
        [InlineData("[NPC] PDC Type 1a GAT","[NPC] PDC Type 1a GAT")]
        [InlineData("Large Grid 7",         "Large Grid")]
        [InlineData("  Static Grid 33  ",   "Static Grid")]
        [InlineData("[NPC] Debris",         "[NPC] Debris")]
        [InlineData("MiniMerchant",         "MiniMerchant")]
        [InlineData("PDC Type 1a GAT",      "PDC Type 1a GAT")]   // digits welded to a word stay
        [InlineData("1234",                 "1234")]              // a name someone chose
        [InlineData("",                     "(unnamed)")]
        [InlineData(null,                   "(unnamed)")]
        public void NameBucketFoldsSeAutoNumbering(string name, string expected)
        {
            Assert.Equal(expected, GridReaping.NameBucket(name));
        }

        /// <summary>Idempotent: bucketing a bucket name changes nothing, so a census of a census is
        /// the same table.</summary>
        [Fact]
        public void NameBucketIsIdempotent()
        {
            Assert.Equal("Small Grid", GridReaping.NameBucket(GridReaping.NameBucket("Small Grid 1234")));
        }
    }
}
