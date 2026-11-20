using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class CrewOrdersTests
    {
        [Fact] public void UnorderedCrewSitOnTheGroundAndWalkInSpace()
        {
            Assert.Equal(CrewStance.Seated, CrewOrders.Stance(CrewOrder.Auto, 1.0));
            Assert.Equal(CrewStance.Seated, CrewOrders.Stance(CrewOrder.Auto, CrewOrders.GroundGravityG));
            Assert.Equal(CrewStance.AtEase, CrewOrders.Stance(CrewOrder.Auto, 0.0));
            Assert.Equal(CrewStance.AtEase, CrewOrders.Stance(CrewOrder.Auto, 0.04));
        }

        [Fact] public void AnExplicitOrderIgnoresGravity()
        {
            foreach (double g in new[] { 0.0, 1.0 })
            {
                Assert.Equal(CrewStance.Seated, CrewOrders.Stance(CrewOrder.Mount, g));
                Assert.Equal(CrewStance.FormUp, CrewOrders.Stance(CrewOrder.Dismount, g));
                Assert.Equal(CrewStance.AtEase, CrewOrders.Stance(CrewOrder.Ease, g));
            }
        }

        [Fact] public void BoardingFromTheGroundGoesThroughTheDoor()
        {
            // Outside, with a door: walk to it, cross, then walk to the seat aboard and sit.
            Assert.Equal(CrewLeg.WalkToExit, CrewOrders.Next(CrewStance.Seated, false, false, false, true, false));
            Assert.Equal(CrewLeg.Cross, CrewOrders.Next(CrewStance.Seated, false, false, false, true, true));
            Assert.Equal(CrewLeg.WalkToSeat, CrewOrders.Next(CrewStance.Seated, false, true, false, true, false));
            Assert.Equal(CrewLeg.Sit, CrewOrders.Next(CrewStance.Seated, false, true, true, true, false));
            Assert.Equal(CrewLeg.Hold, CrewOrders.Next(CrewStance.Seated, true, true, true, true, false));
            // No door: straight at the seat, and the time limit places him.
            Assert.Equal(CrewLeg.WalkToSeat, CrewOrders.Next(CrewStance.Seated, false, false, false, false, false));
        }

        [Fact] public void DismountingGoesOutThroughTheDoor()
        {
            Assert.Equal(CrewLeg.StandUp, CrewOrders.Next(CrewStance.FormUp, true, true, false, true, false));
            Assert.Equal(CrewLeg.WalkToExit, CrewOrders.Next(CrewStance.FormUp, false, true, false, true, false));
            Assert.Equal(CrewLeg.Cross, CrewOrders.Next(CrewStance.FormUp, false, true, false, true, true));
            Assert.Equal(CrewLeg.WalkToSlot, CrewOrders.Next(CrewStance.FormUp, false, false, false, true, false));
            Assert.Equal(CrewLeg.Hold, CrewOrders.Next(CrewStance.FormUp, false, false, true, true, false));
            // Aboard without a door: straight at the slot.
            Assert.Equal(CrewLeg.WalkToSlot, CrewOrders.Next(CrewStance.FormUp, false, true, false, false, false));
        }

        [Fact] public void ChatWordsParseAndRoundTrip()
        {
            foreach (CrewOrder order in new[] { CrewOrder.Auto, CrewOrder.Mount, CrewOrder.Dismount, CrewOrder.Ease })
            {
                CrewOrder parsed;
                Assert.True(CrewOrders.TryParse(CrewOrders.Word(order), out parsed));
                Assert.Equal(order, parsed);
            }
            CrewOrder board;
            Assert.True(CrewOrders.TryParse(" Board ", out board));
            Assert.Equal(CrewOrder.Mount, board);
            CrewOrder none;
            Assert.False(CrewOrders.TryParse("charge", out none));
            Assert.False(CrewOrders.TryParse(null, out none));
        }

        [Fact] public void FormUpRanksAreCentredBehindTheHull()
        {
            var slots = CrewOrders.FormUpSlots(6);
            Assert.Equal(6, slots.Count);
            // First rank of four: -2.25, -0.75, 0.75, 2.25 across, all at the stand-off.
            for (int i = 0; i < 4; i++) Assert.Equal(CrewOrders.FormUpStandOffM, slots[i].Back);
            Assert.Equal(-2.25, slots[0].Side, 6);
            Assert.Equal(2.25, slots[3].Side, 6);
            // Second rank of two, centred, one spacing further back.
            Assert.Equal(CrewOrders.FormUpStandOffM + CrewOrders.FormUpSpacingM, slots[4].Back);
            Assert.Equal(-0.75, slots[4].Side, 6);
            Assert.Equal(0.75, slots[5].Side, 6);
        }

        [Fact] public void FormUpSlotsAreDistinct()
        {
            var slots = CrewOrders.FormUpSlots(8);
            for (int i = 0; i < slots.Count; i++)
                for (int j = i + 1; j < slots.Count; j++)
                    Assert.False(slots[i].Back == slots[j].Back && slots[i].Side == slots[j].Side);
            Assert.Empty(CrewOrders.FormUpSlots(0));
            Assert.Single(CrewOrders.FormUpSlots(1));
            Assert.Equal(0, CrewOrders.FormUpSlots(1)[0].Side);
        }

        [Fact] public void MountAndDismountHoldTheCrewForTheirCommander()
        {
            // A mount given from outside must not lose the crew to the exit grace before they board.
            Assert.True(CrewOrders.HoldsCrew(CrewOrder.Mount));
            Assert.True(CrewOrders.HoldsCrew(CrewOrder.Dismount));
            Assert.False(CrewOrders.HoldsCrew(CrewOrder.Ease));
            Assert.False(CrewOrders.HoldsCrew(CrewOrder.Auto));
        }

        [Fact] public void AHeldCrewOutlastsTheExitGraceButCannotStartAVisit()
        {
            var visit = new CrewVisit();
            Assert.False(visit.Update(false, true, 2));
            Assert.True(visit.Update(true, false, 2));
            for (int i = 0; i < 100; i++) Assert.True(visit.Update(false, true, 2));
            Assert.Equal(0, visit.AbsentSeconds);
            Assert.True(visit.Update(false, false, 18));
            Assert.False(visit.Update(false, false, 2));
            Assert.False(visit.Update(false, true, 2));
        }
    }
}
