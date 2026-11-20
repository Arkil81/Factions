using System;
using System.Collections.Generic;
using System.IO;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Rule 4 of docs/diplomacy-plan.md: while a player is a member of a registered player faction,
    /// SE's personal-reputation field shows the faction standing, and the member's own record is kept
    /// by Factions, keeps moving with their own conduct, feeds the aggregate, and goes back into the
    /// field when they leave.
    /// </summary>
    public class MemberReputationSyncTests
    {
        private const long Arkil = 144115188075855873L;
        private const long Joiner = 144115188075855999L;
        private const double Hostile = -0.3;
        private const double Ally = 0.6;

        /// <summary>SE's field, as a dictionary. <see cref="OnWrite"/> mimics SE raising its change
        /// event from inside our own write, before the value is stored.</summary>
        private sealed class FakeField : IPersonalReputationField
        {
            public readonly Dictionary<string, int> Values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int Min = -1500, Max = 1500;
            public int Writes;
            public Action<long, string, int, int> OnWrite;

            private static string Key(long id, string tag) { return id + "|" + tag; }
            public void Put(long id, string tag, int v) { Values[Key(id, tag)] = v; }
            public int Get(long id, string tag) { return Values[Key(id, tag)]; }

            /// <summary>Reads of this key that fail before it becomes readable — a faction SE did not
            /// have when the seed looked, and does by the time the projection does.</summary>
            public readonly Dictionary<string, int> FailReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public bool TryRead(long identityId, string npcTag, out int reputation)
            {
                int fails;
                if (FailReads.TryGetValue(Key(identityId, npcTag), out fails) && fails > 0)
                {
                    FailReads[Key(identityId, npcTag)] = fails - 1;
                    reputation = 0;
                    return false;
                }
                return Values.TryGetValue(Key(identityId, npcTag), out reputation);
            }

            public void FailNextReads(long id, string tag, int count) { FailReads[Key(id, tag)] = count; }

            public bool Write(long identityId, string npcTag, int reputation)
            {
                int old;
                if (!TryRead(identityId, npcTag, out old)) return false;
                int v = Clamp(reputation);
                if (OnWrite != null) OnWrite(identityId, npcTag, old, v);
                Values[Key(identityId, npcTag)] = v;
                Writes++;
                return true;
            }

            public int Clamp(int reputation) { return reputation < Min ? Min : (reputation > Max ? Max : reputation); }

            /// <summary>Every re-send, as "identity|TAG", in order.</summary>
            public readonly List<string> Resends = new List<string>();
            public Action<long, string, int> OnResend;

            public bool Resend(long identityId, string npcTag)
            {
                int v;
                if (!TryRead(identityId, npcTag, out v)) return false;
                if (OnResend != null) OnResend(identityId, npcTag, v);
                Resends.Add(Key(identityId, npcTag));
                return true;
            }
        }

        private static readonly string[] Npcs = { "IMPR", "ISBT" };

        private static List<PlayerFactionMember> Roster(params long[] ids)
        {
            var list = new List<PlayerFactionMember>();
            foreach (long id in ids) list.Add(new PlayerFactionMember(id, "EXP"));
            return list;
        }

        private static MemberReputationPassResult Pass(MemberReputationSync sync, IList<PlayerFactionMember> members,
                                                       Dictionary<string, double> standing, int minDelta = 1, int maxWrites = 0)
        {
            return sync.RunPass(members, Npcs, (npc, player) => standing[npc], ReputationScale.Vanilla,
                                Hostile, Ally, minDelta, maxWrites);
        }

        private static Dictionary<string, double> Standing(double impr, double isbt)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { { "IMPR", impr }, { "ISBT", isbt } };
        }

        private static MemberReputationSync NewSync(FakeField field)
        {
            return new MemberReputationSync(new MemberReputationBook(null), field);
        }

        // ---- seeding ----------------------------------------------------------------------------

        [Fact]
        public void Seeding_HappensExactlyOncePerMembership_AndBeforeTheFieldIsOverwritten()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 143); field.Put(Arkil, "ISBT", -6);
            var sync = NewSync(field);

            MemberReputationPassResult first = Pass(sync, Roster(Arkil), Standing(-0.667, 0.2));
            Assert.Equal(2, first.Seeded);
            Assert.NotEqual(143, field.Get(Arkil, "IMPR"));        // the field now shows the standing

            MemberReputationPassResult second = Pass(sync, Roster(Arkil), Standing(-0.667, 0.2));
            Assert.Equal(0, second.Seeded);

            int record;
            Assert.True(sync.Book.TryGet(Arkil, "IMPR", out record));
            Assert.Equal(143, record);                               // not re-read from the projected field
            Assert.True(sync.Book.TryGet(Arkil, "ISBT", out record));
            Assert.Equal(-6, record);
        }

        [Fact]
        public void ANewNpcIsSeededForAnExistingMember_WithoutReseedingTheOthers()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 143);
            var sync = NewSync(field);
            sync.Seed(Arkil, new[] { "IMPR" });
            field.Put(Arkil, "IMPR", -900);                          // projected since
            field.Put(Arkil, "ISBT", 40);                            // a pair SE never had projected

            Assert.Equal(1, sync.Seed(Arkil, Npcs));
            int impr, isbt;
            sync.Book.TryGet(Arkil, "IMPR", out impr);
            sync.Book.TryGet(Arkil, "ISBT", out isbt);
            Assert.Equal(143, impr);
            Assert.Equal(40, isbt);
        }

        // ---- projection -------------------------------------------------------------------------

        [Theory]
        [InlineData(-1.0, -1500)]
        [InlineData(Hostile, -1000)]   // the hostile threshold lands inside Enemies, as the faction table does
        [InlineData(0.0, 0)]
        [InlineData(Ally, 1000)]
        [InlineData(1.0, 1500)]
        public void TheFieldShowsTheStanding_ThroughTheSameKnotsAsTheFactionTable(double standing, int expected)
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 250); field.Put(Arkil, "ISBT", 250);
            var sync = NewSync(field);

            Pass(sync, Roster(Arkil), Standing(standing, 0.0));

            Assert.Equal(expected, field.Get(Arkil, "IMPR"));
            Assert.Equal(ReputationScale.Vanilla.ReputationFor(standing, Hostile, Ally), field.Get(Arkil, "IMPR"));
        }

        [Fact]
        public void TheProjectedValueIsClampedToTheWorldsBounds_NotVanillas()
        {
            var field = new FakeField { Min = -1000, Max = 1000 };
            field.Put(Arkil, "IMPR", 0); field.Put(Arkil, "ISBT", 0);
            var sync = NewSync(field);

            Assert.Equal(-1000, sync.ProjectedReputation(-1.0, ReputationScale.Vanilla, Hostile, Ally));
            Assert.Equal(1000, sync.ProjectedReputation(1.0, ReputationScale.Vanilla, Hostile, Ally));
        }

        [Theory]
        [InlineData(0, 0, 1, false)]
        [InlineData(0, 1, 1, true)]     // one point is enough
        [InlineData(0, -1, 1, true)]
        [InlineData(0, 0, 0, false)]    // a threshold below one reads as one: an equal field is never rewritten
        [InlineData(0, 4, 5, false)]
        [InlineData(0, 5, 5, true)]
        public void TheFieldIsWrittenOnlyWhenItMovedByTheMinimum(int current, int target, int minDelta, bool expected)
        {
            Assert.Equal(expected, MemberReputationSync.ShouldWrite(current, target, minDelta));
        }

        [Fact]
        public void AFieldAlreadyAtTheStanding_IsNotWrittenAgain()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 0); field.Put(Arkil, "ISBT", 0);
            var sync = NewSync(field);

            Pass(sync, Roster(Arkil), Standing(-0.5, 0.25));
            int afterFirst = field.Writes;
            Assert.Equal(2, afterFirst);
            Pass(sync, Roster(Arkil), Standing(-0.5, 0.25));
            Assert.Equal(afterFirst, field.Writes);
        }

        [Fact]
        public void TheWriteBudgetDefersTheRest_ToTheNextPass()
        {
            var field = new FakeField();
            foreach (long id in new[] { Arkil, Joiner }) { field.Put(id, "IMPR", 0); field.Put(id, "ISBT", 0); }
            var sync = NewSync(field);

            MemberReputationPassResult first = Pass(sync, Roster(Arkil, Joiner), Standing(-0.5, 0.5), 1, 3);
            Assert.Equal(3, first.Written);
            Assert.Equal(1, first.Deferred);

            MemberReputationPassResult second = Pass(sync, Roster(Arkil, Joiner), Standing(-0.5, 0.5), 1, 3);
            Assert.Equal(1, second.Written);
            Assert.Equal(0, second.Deferred);
        }

        [Fact]
        public void AnUnseededPairIsNeverProjectedOver()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 143);
            field.Put(Arkil, "ISBT", 40);
            field.FailNextReads(Arkil, "ISBT", 1);                   // unreadable when the seed looks, readable after
            var sync = NewSync(field);

            Pass(sync, Roster(Arkil), Standing(-1.0, -1.0));

            int ignored;
            Assert.False(sync.Book.TryGet(Arkil, "ISBT", out ignored));
            Assert.Equal(40, field.Get(Arkil, "ISBT"));              // still the player's own record
            Assert.Equal(1, field.Writes);

            Pass(sync, Roster(Arkil), Standing(-1.0, -1.0));         // seeded now, then projected
            Assert.True(sync.Book.TryGet(Arkil, "ISBT", out ignored));
            Assert.Equal(40, ignored);
        }

        // ---- capture ----------------------------------------------------------------------------

        [Fact]
        public void OurOwnProjectionWrite_IsNeverCapturedAsConduct_WhateverReasonItCarries()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 143); field.Put(Arkil, "ISBT", -6);
            var sync = NewSync(field);
            int captured = 0;
            // SE raises its change event from inside our own write. Report it with a conduct reason on
            // purpose: the guard, not the reason, is what must stop it.
            field.OnWrite = (id, tag, oldValue, newValue) =>
            {
                if (sync.CaptureAdd(id, tag, newValue - oldValue, ReputationChangeKind.Hostility)) captured++;
                if (sync.CaptureObserved(id, tag, oldValue, newValue, ReputationChangeKind.Contract)) captured++;
            };

            Pass(sync, Roster(Arkil), Standing(-1.0, 1.0));

            Assert.Equal(0, captured);
            int record;
            sync.Book.TryGet(Arkil, "IMPR", out record);
            Assert.Equal(143, record);
        }

        [Fact]
        public void DecayIsNotConduct_OnEitherPath()
        {
            Assert.False(MemberReputationSync.IsConductOnAddPath(ReputationChangeKind.Decay));
            Assert.False(MemberReputationSync.IsConductOnSetPath(ReputationChangeKind.Decay));

            var field = new FakeField();
            field.Put(Arkil, "IMPR", 100);
            var sync = NewSync(field);
            sync.Seed(Arkil, new[] { "IMPR" });

            Assert.False(sync.CaptureAdd(Arkil, "IMPR", 4, ReputationChangeKind.Decay));
            Assert.False(sync.CaptureObserved(Arkil, "IMPR", 100, 96, ReputationChangeKind.Decay));
            int record;
            sync.Book.TryGet(Arkil, "IMPR", out record);
            Assert.Equal(100, record);
        }

        /// <summary>The operator's clarification: the record does not freeze on joining. Every conduct
        /// delta moves it by exactly what SE would have applied to the field had the field still held
        /// it — earning and losing, and at a bound of the projected field too.</summary>
        [Theory]
        [InlineData(100, -25, ReputationChangeKind.Hostility)]     // grinding a station
        [InlineData(100, -300, ReputationChangeKind.Hostility)]    // killing
        [InlineData(100, 40, ReputationChangeKind.Contract)]       // contract reward
        [InlineData(100, -60, ReputationChangeKind.Contract)]      // contract failed
        [InlineData(100, 25, ReputationChangeKind.None)]           // our own trade grant
        [InlineData(-1490, -25, ReputationChangeKind.Hostility)]   // clamped at the floor, as SE clamps
        [InlineData(1480, 40, ReputationChangeKind.Contract)]      // clamped at the ceiling
        public void ConductAfterJoining_MovesTheRecordByWhatSeWouldHaveApplied(int record, int delta, ReputationChangeKind kind)
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", record);
            var sync = NewSync(field);
            sync.Seed(Arkil, new[] { "IMPR" });
            // Now a member at war: the field shows the hostile floor, so SE's own clamp would swallow
            // every further loss there. The record must not care.
            field.Put(Arkil, "IMPR", -1500);

            Assert.True(sync.CaptureAdd(Arkil, "IMPR", delta, kind));

            // What SE's GenerateChanges would have stored had the field still been the record.
            int seWouldHold = field.Clamp(record + delta);
            int stored;
            sync.Book.TryGet(Arkil, "IMPR", out stored);
            Assert.Equal(seWouldHold, stored);
            Assert.Equal(-1500, field.Get(Arkil, "IMPR"));           // the active value is not touched
        }

        [Fact]
        public void ConductKeepsAccruingAcrossManyActs_WhileTheFieldKeepsShowingTheStanding()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 0); field.Put(Arkil, "ISBT", 0);
            var sync = NewSync(field);
            Pass(sync, Roster(Arkil), Standing(0.2, 0.2));
            int projected = field.Get(Arkil, "IMPR");

            for (int i = 0; i < 8; i++) sync.CaptureAdd(Arkil, "IMPR", -25, ReputationChangeKind.Hostility);
            for (int i = 0; i < 3; i++) sync.CaptureAdd(Arkil, "IMPR", 25, ReputationChangeKind.None);
            Pass(sync, Roster(Arkil), Standing(0.2, 0.2));

            int record;
            sync.Book.TryGet(Arkil, "IMPR", out record);
            Assert.Equal(-125, record);
            Assert.Equal(projected, field.Get(Arkil, "IMPR"));
        }

        [Fact]
        public void CaptureOnlyTouchesTrackedPairs()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 100);
            var sync = NewSync(field);
            Assert.False(sync.CaptureAdd(Arkil, "IMPR", -25, ReputationChangeKind.Hostility));   // not a member
            Assert.Equal(0, sync.Book.IdentityCount);
        }

        [Fact]
        public void AnAbsoluteSet_IsConductOnlyFromAModOrAnAdmin()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 100);
            var sync = NewSync(field);
            sync.Seed(Arkil, new[] { "IMPR" });

            // ChangeFactionRelation's member loop and ForceRelationToPlayers both set with None.
            Assert.False(sync.CaptureSet(Arkil, "IMPR", -1000, ReputationChangeKind.None));
            Assert.True(sync.CaptureSet(Arkil, "IMPR", 700, ReputationChangeKind.Mod));
            int record;
            sync.Book.TryGet(Arkil, "IMPR", out record);
            Assert.Equal(700, record);
            Assert.True(sync.CaptureSet(Arkil, "IMPR", 9000, ReputationChangeKind.Admin));
            sync.Book.TryGet(Arkil, "IMPR", out record);
            Assert.Equal(1500, record);
        }

        [Fact]
        public void TheObservedFallback_TakesConductButNotEngineResets()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 100);
            var sync = NewSync(field);
            sync.Seed(Arkil, new[] { "IMPR" });

            Assert.False(sync.CaptureObserved(Arkil, "IMPR", -500, -1000, ReputationChangeKind.None));
            Assert.True(sync.CaptureObserved(Arkil, "IMPR", -500, -525, ReputationChangeKind.Hostility));
            int record;
            sync.Book.TryGet(Arkil, "IMPR", out record);
            Assert.Equal(75, record);
        }

        [Theory]
        [InlineData(1f, 25, 25)]
        [InlineData(0.25f, 25, 6)]      // truncates toward zero, as GenerateChanges does
        [InlineData(0.5f, -25, -12)]
        [InlineData(-0.25f, 40, -10)]
        public void ScaledDeltas_UseSesOwnArithmetic(float multiplier, int delta, int expected)
        {
            Assert.Equal(expected, MemberReputationSync.ScaleDelta(multiplier, delta));
        }

        // ---- forced re-send after a vanilla relation broadcast ------------------------------------

        [Theory]
        [InlineData(VanillaFactionAct.DeclareWar, true)]
        [InlineData(VanillaFactionAct.AcceptPeace, true)]
        [InlineData(VanillaFactionAct.AcceptFriendRequest, true)]
        [InlineData(VanillaFactionAct.SendPeaceRequest, false)]
        [InlineData(VanillaFactionAct.CancelPeaceRequest, false)]
        [InlineData(VanillaFactionAct.None, false)]
        [InlineData(VanillaFactionAct.Other, false)]
        public void OnlyTheThreeRelationActsRunTheMemberLoop(VanillaFactionAct act, bool expected)
        {
            Assert.Equal(expected, VanillaDiplomacy.RunsMemberLoop(act));
        }

        private const long Outsider = 144115188075855777L;

        /// <summary>Arkil and Joiner in EXP, Outsider in OTH, all projected and settled.</summary>
        private static MemberReputationSync Settled(FakeField field, out List<PlayerFactionMember> roster)
        {
            foreach (long id in new[] { Arkil, Joiner, Outsider }) { field.Put(id, "IMPR", 0); field.Put(id, "ISBT", 0); }
            var sync = NewSync(field);
            roster = Roster(Arkil, Joiner);
            roster.Add(new PlayerFactionMember(Outsider, "OTH"));
            Pass(sync, roster, Standing(-0.5, 0.5));
            Pass(sync, roster, Standing(-0.5, 0.5));
            return sync;
        }

        [Fact]
        public void AMarkedPair_IsResentOnceForEachOfItsMembers_EvenWithTheServerValueUnchanged()
        {
            var field = new FakeField();
            List<PlayerFactionMember> roster;
            MemberReputationSync sync = Settled(field, out roster);
            int writes = field.Writes;

            sync.MarkResend("EXP", "IMPR");
            MemberReputationPassResult pass = Pass(sync, roster, Standing(-0.5, 0.5));

            Assert.Equal(2, pass.Resent);
            Assert.Equal(0, pass.Written);
            Assert.Equal(writes, field.Writes);                       // the server value is not rewritten
            Assert.Equal(new[] { Arkil + "|IMPR", Joiner + "|IMPR" }, field.Resends.ToArray());
            Assert.Equal(0, sync.PendingResends);

            Pass(sync, roster, Standing(-0.5, 0.5));
            Assert.Equal(2, field.Resends.Count);                     // once, not every pass
        }

        [Fact]
        public void WithoutAMark_ASettledFieldIsNeverResent()
        {
            var field = new FakeField();
            List<PlayerFactionMember> roster;
            MemberReputationSync sync = Settled(field, out roster);

            MemberReputationPassResult pass = Pass(sync, roster, Standing(-0.5, 0.5));

            Assert.Equal(0, pass.Resent);
            Assert.Empty(field.Resends);
        }

        [Fact]
        public void AMarkedPairWithinTheMinimumDelta_IsWrittenOntoTheStanding_NotResentAtTheOldValue()
        {
            var field = new FakeField();
            List<PlayerFactionMember> roster;
            MemberReputationSync sync = Settled(field, out roster);
            int target = field.Get(Arkil, "IMPR");
            field.Put(Arkil, "IMPR", target + 3);                     // off by less than the minimum

            sync.MarkResend("EXP", "IMPR");
            MemberReputationPassResult pass = Pass(sync, roster, Standing(-0.5, 0.5), 5);

            Assert.Equal(target, field.Get(Arkil, "IMPR"));
            Assert.Equal(1, pass.Written);
            Assert.Equal(1, pass.Resent);                             // Joiner was exactly on it
            Assert.Equal(new[] { Joiner + "|IMPR" }, field.Resends.ToArray());
        }

        [Fact]
        public void ResendsCountAgainstTheBudget_AndTheMarkSurvivesUntilEveryMemberIsDone()
        {
            var field = new FakeField();
            List<PlayerFactionMember> roster;
            MemberReputationSync sync = Settled(field, out roster);

            sync.MarkResend("EXP", "IMPR");
            MemberReputationPassResult first = Pass(sync, roster, Standing(-0.5, 0.5), 1, 1);
            Assert.Equal(1, first.Resent);
            Assert.Equal(1, first.Deferred);
            Assert.False(sync.IsOwedResend(Arkil, "IMPR"));
            Assert.True(sync.IsOwedResend(Joiner, "IMPR"));

            // The next pass resumes with the deferred member, not from the top of the roster.
            MemberReputationPassResult second = Pass(sync, roster, Standing(-0.5, 0.5), 1, 1);
            Assert.Equal(1, second.Resent);
            Assert.Equal(0, second.Deferred);
            Assert.Equal(0, sync.PendingResends);
            Assert.Equal(new[] { Arkil + "|IMPR", Joiner + "|IMPR" }, field.Resends.ToArray());
        }

        [Fact]
        public void AResend_IsNeverCapturedAsConduct()
        {
            var field = new FakeField();
            List<PlayerFactionMember> roster;
            MemberReputationSync sync = Settled(field, out roster);
            int captured = 0;
            field.OnResend = (id, tag, v) => { if (sync.CaptureAdd(id, tag, 25, ReputationChangeKind.Hostility)) captured++; };
            int before;
            sync.Book.TryGet(Arkil, "IMPR", out before);

            sync.MarkResend("EXP", "IMPR");
            Pass(sync, roster, Standing(-0.5, 0.5));

            Assert.Equal(0, captured);
            int after;
            sync.Book.TryGet(Arkil, "IMPR", out after);
            Assert.Equal(before, after);
        }

        [Fact]
        public void AMarkForAFactionWithNoMembers_IsDropped()
        {
            var field = new FakeField();
            List<PlayerFactionMember> roster;
            MemberReputationSync sync = Settled(field, out roster);

            sync.MarkResend("GONE", "IMPR");
            Pass(sync, roster, Standing(-0.5, 0.5));

            Assert.Equal(0, sync.PendingResends);
            Assert.Empty(field.Resends);
        }

        // ---- leaving ----------------------------------------------------------------------------

        [Fact]
        public void LeavingWritesTheRecordBack_AndStopsProjecting()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 143); field.Put(Arkil, "ISBT", -6);
            var sync = NewSync(field);
            int captured = 0;
            field.OnWrite = (id, tag, o, n) => { if (sync.CaptureAdd(id, tag, n - o, ReputationChangeKind.Mod)) captured++; };

            Pass(sync, Roster(Arkil), Standing(-1.0, 1.0));
            sync.CaptureAdd(Arkil, "IMPR", -25, ReputationChangeKind.Hostility);   // conduct while a member

            MemberReputationPassResult left = Pass(sync, Roster(), Standing(-1.0, 1.0));

            Assert.Equal(1, left.Released);
            Assert.Equal(118, field.Get(Arkil, "IMPR"));
            Assert.Equal(-6, field.Get(Arkil, "ISBT"));
            Assert.False(sync.Book.IsTracked(Arkil));
            Assert.Equal(0, captured);                                // the write-back is ours, too

            int writes = field.Writes;
            Pass(sync, Roster(), Standing(-1.0, 1.0));
            Assert.Equal(writes, field.Writes);                       // no longer projected
        }

        [Fact]
        public void RejoiningSeedsAfresh_FromTheRecordThatWasWrittenBack()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 143); field.Put(Arkil, "ISBT", -6);
            var sync = NewSync(field);
            Pass(sync, Roster(Arkil), Standing(-1.0, 1.0));
            sync.CaptureAdd(Arkil, "IMPR", -25, ReputationChangeKind.Hostility);
            sync.WriteBack(Arkil);
            // Conduct outside any faction lands on the field, which is theirs again.
            field.Put(Arkil, "IMPR", 90);

            MemberReputationPassResult rejoin = Pass(sync, Roster(Arkil), Standing(-1.0, 1.0));
            Assert.Equal(2, rejoin.Seeded);
            int record;
            sync.Book.TryGet(Arkil, "IMPR", out record);
            Assert.Equal(90, record);
        }

        [Fact]
        public void TheKillSwitchReleasesEveryone()
        {
            var field = new FakeField();
            foreach (long id in new[] { Arkil, Joiner }) { field.Put(id, "IMPR", 10); field.Put(id, "ISBT", 20); }
            var sync = NewSync(field);
            Pass(sync, Roster(Arkil, Joiner), Standing(-1.0, 1.0));

            Assert.Equal(2, sync.ReleaseAll());
            Assert.Equal(0, sync.Book.IdentityCount);
            Assert.Equal(10, field.Get(Joiner, "IMPR"));
            Assert.Equal(20, field.Get(Arkil, "ISBT"));
        }

        // ---- the aggregate ----------------------------------------------------------------------

        [Fact]
        public void TheAggregateReadsStoredRecords_NotTheProjectedField()
        {
            var field = new FakeField();
            field.Put(Arkil, "IMPR", -1200); field.Put(Arkil, "ISBT", 0);
            var sync = NewSync(field);
            Pass(sync, Roster(Arkil), Standing(1.0, 0.0));
            Assert.Equal(1500, field.Get(Arkil, "IMPR"));             // the field shows the standing

            List<MemberReputation> roster = MemberStandingRoster.Build(
                new[] { Arkil }, "IMPR", sync.Book, field, ReputationScale.Vanilla, id => DateTime.UtcNow);

            Assert.Single(roster);
            Assert.Equal(ReputationScale.Vanilla.Normalise(-1200), roster[0].Standing, 9);
            Assert.Equal(-0.8, roster[0].Standing, 9);
        }

        [Fact]
        public void AnUntrackedMember_ContributesTheField_WhichIsStillTheirRecord()
        {
            var field = new FakeField();
            field.Put(Joiner, "IMPR", 750);
            var sync = NewSync(field);

            List<MemberReputation> roster = MemberStandingRoster.Build(
                new[] { Joiner }, "IMPR", sync.Book, field, ReputationScale.Vanilla, id => null);

            Assert.Equal(0.5, roster[0].Standing, 9);
        }

        private sealed class RosterSource : IMemberStandingSource
        {
            private readonly MemberReputationSync _sync;
            private readonly FakeField _field;
            private readonly long[] _ids;
            private readonly DateTime _now;
            public RosterSource(MemberReputationSync sync, FakeField field, DateTime now, params long[] ids)
            { _sync = sync; _field = field; _ids = ids; _now = now; }
            public bool TryGetAggregate(string npc, string player, out double aggregate)
            {
                var roster = MemberStandingRoster.Build(_ids, npc, _sync.Book, _field, ReputationScale.Vanilla, id => _now);
                return MemberStandingAggregate.TryCompute(roster, _now, 0.5, 14.0, out aggregate) && player == "EXP";
            }
        }

        private static Personality Persona()
        {
            var p = new Personality { Temperature = 0.5, Traits = new Traits { Xenophobia = 0.5, Aggression = 0.5, Mercantilism = 0.5 } };
            p.Weights[Drive.Security] = 1; p.Weights[Drive.Growth] = 1; p.Weights[Drive.Wealth] = 1;
            p.Weights[Drive.Rivalry] = 1; p.Weights[Drive.Caution] = 1;
            return p;
        }

        /// <summary>A joiner with a bad record drags the faction down even though, once projected,
        /// their own SE field shows the faction's (good) standing.</summary>
        [Fact]
        public void ABadJoiner_LowersTheStanding()
        {
            var now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
            var field = new FakeField();
            field.Put(Arkil, "IMPR", 1000); field.Put(Arkil, "ISBT", 0);
            field.Put(Joiner, "IMPR", -1400); field.Put(Joiner, "ISBT", 0);
            var sync = NewSync(field);

            Pass(sync, Roster(Arkil), Standing(0.7, 0.0));
            var alone = new RosterSource(sync, field, now, Arkil);
            double before;
            Assert.True(alone.TryGetAggregate("IMPR", "EXP", out before));

            Pass(sync, Roster(Arkil, Joiner), Standing(0.7, 0.0));
            Assert.Equal(field.Get(Arkil, "IMPR"), field.Get(Joiner, "IMPR"));   // both show the standing now
            var both = new RosterSource(sync, field, now, Arkil, Joiner);
            double after;
            Assert.True(both.TryGetAggregate("IMPR", "EXP", out after));
            Assert.True(after < before - 0.5, "aggregate " + before + " -> " + after);

            // And through Drift, the standing IMPR holds toward EXP.
            var t = TestSupport.Tuning();
            var withArkil = new FactionState { Tag = "IMPR" };
            var withJoiner = new FactionState { Tag = "IMPR" };
            for (int i = 0; i < 40; i++)
            {
                WorldModel wa = TwoFactionWorld(); wa.MemberStanding = alone;
                Diplomacy.Drift(withArkil, Persona(), wa, t);
                WorldModel wb = TwoFactionWorld(); wb.MemberStanding = both;
                Diplomacy.Drift(withJoiner, Persona(), wb, t);
            }
            Assert.True(withJoiner.RelationTo("EXP") < withArkil.RelationTo("EXP"),
                "IMPR toward EXP: " + withArkil.RelationTo("EXP") + " alone, " + withJoiner.RelationTo("EXP") + " with the joiner");
        }

        private static WorldModel TwoFactionWorld()
        {
            var w = TestSupport.World();
            var a = w.Graph.Add(new Vec3(0, 0, 0), 0.5, "A1"); a.OwnerFactionTag = "IMPR"; a.HoldStrength = 0.8; a.Radius = 30;
            var b = w.Graph.Add(new Vec3(12, 0, 0), 0.5, "B1"); b.OwnerFactionTag = "EXP"; b.HoldStrength = 0.8; b.Radius = 30;
            return w;
        }

        [Theory]
        [InlineData(1500, 1.0)]
        [InlineData(750, 0.5)]
        [InlineData(0, 0.0)]
        [InlineData(-750, -0.5)]
        [InlineData(-1500, -1.0)]
        [InlineData(-3000, -1.0)]
        public void Normalise_IsLinearEachSideOfNeutral(int reputation, double expected)
        {
            Assert.Equal(expected, ReputationScale.Vanilla.Normalise(reputation), 9);
        }

        // ---- persistence ------------------------------------------------------------------------

        [Fact]
        public void TheBookRoundTrips_AndIsLean()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fac-memberrep-" + Guid.NewGuid().ToString("N"));
            try
            {
                var book = new MemberReputationBook(dir);
                book.Seed(Arkil, "IMPR", -118);
                book.Seed(Arkil, "ISBT", 143);
                book.Track(Joiner);
                book.SaveIfDirty();

                string json = File.ReadAllText(Path.Combine(dir, MemberReputationBook.FileName));
                Assert.Contains("\"IMPR\": -118", json);
                Assert.DoesNotContain("null", json);

                var reloaded = new MemberReputationBook(dir);
                int v;
                Assert.True(reloaded.TryGet(Arkil, "impr", out v));    // tags are case-insensitive
                Assert.Equal(-118, v);
                Assert.True(reloaded.IsTracked(Joiner));
                Assert.Equal(2, reloaded.RecordCount);

                reloaded.Release(Arkil);
                reloaded.SaveIfDirty();
                Assert.False(new MemberReputationBook(dir).IsTracked(Arkil));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void AnUnreadableBook_Throws_RatherThanStartingEmpty()
        {
            string dir = Path.Combine(Path.GetTempPath(), "fac-memberrep-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, MemberReputationBook.FileName), "{ not json");
                Assert.ThrowsAny<Exception>(() => new MemberReputationBook(dir));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void ASeedNeverOverwritesALiveRecord()
        {
            var book = new MemberReputationBook(null);
            Assert.True(book.Seed(Arkil, "IMPR", 143));
            Assert.False(book.Seed(Arkil, "IMPR", -1000));
            int v;
            book.TryGet(Arkil, "IMPR", out v);
            Assert.Equal(143, v);
        }
    }
}
