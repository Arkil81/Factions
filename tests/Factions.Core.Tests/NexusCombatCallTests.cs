using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A fight resolved on the Nexus authority, told on an agent: the agent must give each
    /// viewer the tone the authority would, and only once.</summary>
    public class NexusCombatCallTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        private static CombatOutcome Fight(CombatAttackResult attack, CombatDefenceResult defence)
        {
            var o = new CombatOutcome
            {
                AttackerTag = "IMPR", DefenderTag = "EXP", PositionKm = new Vec3(10, 20, 30),
                Attack = attack, Defence = defence,
            };
            o.DefenderTags.Add("ALY");
            return o;
        }

        [Fact]
        public void AnAgentGivesEveryViewerTheAuthoritysTone()
        {
            foreach (CombatAttackResult a in Enum.GetValues(typeof(CombatAttackResult)))
                foreach (CombatDefenceResult d in Enum.GetValues(typeof(CombatDefenceResult)))
                {
                    CombatOutcome here = Fight(a, d);
                    var reply = new NexusStationReply { Combat = new List<NexusCombatCall> { NexusCombatCall.From(here, "s") } };
                    NexusStationReply back;
                    Assert.True(NexusStationReply.TryDecode(reply.Encode(), out back));
                    CombatOutcome there = Assert.Single(back.Combat).ToOutcome();
                    foreach (string viewer in new[] { "IMPR", "EXP", "ALY", "OTHR", null })
                        Assert.Equal(here.ToneFor(viewer), there.ToneFor(viewer));
                    Assert.Equal(here.PositionKm.X, there.PositionKm.X);
                }
        }

        [Fact]
        public void AFightIsHandedToEachAgentOnce()
        {
            var relay = new NexusCombatRelay();
            relay.Add(NexusCombatCall.From(Fight(CombatAttackResult.Landed, CombatDefenceResult.Destroyed), "s"), T0);
            Assert.Single(relay.Take(2, T0));
            Assert.Null(relay.Take(2, T0.AddSeconds(1)));
            Assert.Single(relay.Take(4, T0.AddSeconds(1)));
            Assert.Null(relay.Take(3, T0.AddSeconds(6)));
        }

        [Fact]
        public void AReplyWithOnlyAFightIsNotEmpty()
        {
            var c = NexusCombatCall.From(Fight(CombatAttackResult.Repelled, CombatDefenceResult.Destroyed), "s");
            Assert.False(new NexusStationReply { Combat = new List<NexusCombatCall> { c } }.IsEmpty);
            Assert.Null(NexusCombatCall.From(Fight(CombatAttackResult.Landed, CombatDefenceResult.Destroyed), null));
        }
    }
}
