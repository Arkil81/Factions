using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>A bot sitting in a seat, cryo chamber or bed stands up for a player who reaches for
    /// it (<see cref="SeatYield"/>). Any bot, whoever spawned it: SE refuses the player on their own
    /// client while any bot holds the seat.
    ///
    /// <para>The yielded seat is reserved with SE's AI target manager, which the native astronaut's
    /// seat search skips; <see cref="Held"/> keeps <see cref="CrewOrderControl"/> from choosing it, and
    /// <see cref="CrewSeatHoldPatch"/> refuses any bot that still tries to sit in it.</para></summary>
    internal static class CrewSeatYield
    {
        private static readonly SeatYieldWatch Watch = new SeatYieldWatch();
        private static readonly List<MyCockpit> BotSeats = new List<MyCockpit>();
        private static readonly List<MyPlayer> Humans = new List<MyPlayer>();
        private static int _yields;
        private static string _last = "none";

        internal static bool Held(MyCockpit seat)
        {
            return seat != null && Watch.IsHeld(seat.EntityId, DateTime.UtcNow);
        }

        internal static string Status()
        {
            return "seat yields: " + _yields + " since load, held now " + Watch.HeldCount(DateTime.UtcNow)
                   + ", last " + _last;
        }

        /// <summary>One sampling pass; the caller runs it every quarter second.</summary>
        internal static void Tick()
        {
            if (MySession.Static == null || MySession.Static.Players == null) return;
            BotSeats.Clear();
            Humans.Clear();
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                MyCharacter character = player.Character;
                if (character == null || character.IsDead || character.MarkedForClose) continue;
                var seat = character.Parent as MyCockpit;
                if (player.IsBot || player.Id.SerialId != 0)
                {
                    if (seat != null && !seat.Closed && !seat.MarkedForClose) BotSeats.Add(seat);
                }
                else if (seat == null) Humans.Add(player);
            }
            if (BotSeats.Count > 0 && Humans.Count > 0)
            {
                foreach (MyCockpit seat in BotSeats)
                {
                    Vector3D entry = seat.GetDetectorPosition();
                    foreach (MyPlayer human in Humans)
                    {
                        MatrixD head = human.Character.GetHeadMatrix(true);
                        bool reaching = SeatYield.Aims(ToVec3(head.Translation), ToVec3(head.Forward), ToVec3(entry))
                            && seat.HasPlayerAccess(human.Identity.IdentityId)
                            && seat.HasLineOfSightToDetector(head.Translation);
                        if (!Watch.Observe(seat.EntityId, human.Identity.IdentityId, reaching)) continue;
                        Yield(seat, human);
                        break;
                    }
                }
            }
            Watch.EndPass();
        }

        private static void Yield(MyCockpit seat, MyPlayer human)
        {
            MyCharacter bot = seat.Pilot;
            if (bot == null) return;
            MyCubeGrid hull = seat.CubeGrid;
            string why;
            Vector3D? deck = CrewExits.DeckBeside(hull, seat, bot, out why);
            seat.RemovePilot();
            if (deck.HasValue && !CrewNavigationArea.Inside(hull, bot.PositionComp.GetPosition()))
                CrewOrderControl.Teleport(bot, deck.Value);

            DateTime now = DateTime.UtcNow;
            Watch.Hold(seat.EntityId, now);
            if (MyAiTargetManager.Static != null)
            {
                MyAiTargetManager.Static.UnreserveEntity(seat.EntityId);
                MyAiTargetManager.Static.ReserveEntity(seat.EntityId, (long)(SeatYield.HoldSeconds * 1000), human.Id);
            }
            _yields++;
            _last = seat.BlockDefinition.Id.SubtypeName + " on " + hull.DisplayName + " at "
                    + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            if (DebugTraces.CrewSeats)
                FactionsPlugin.Log.Info("CREW_SEAT_YIELD grid=" + hull.EntityId + " seat=" + seat.EntityId
                    + " block=" + seat.BlockDefinition.Id.SubtypeName + " bot=" + bot.EntityId
                    + " player=" + human.Id.SteamId + " deck=" + (deck.HasValue ? "yes" : "no:" + why));
        }

        private static Vec3 ToVec3(Vector3D v) { return new Vec3(v.X, v.Y, v.Z); }
    }
}
