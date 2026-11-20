using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What a hull's owner has told its crew to do (docs/ground-operations.md).
    /// <see cref="Auto"/> is the absence of an order.</summary>
    public enum CrewOrder { Auto, Mount, Dismount, Ease }

    /// <summary>What a crewman is doing under an order. <see cref="AtEase"/> is the native
    /// astronaut behaviour; the other two are driven by the host and keep the native tree off.</summary>
    public enum CrewStance { AtEase, Seated, FormUp }

    /// <summary>The next thing a crewman under a seated or form-up stance does
    /// (<see cref="CrewOrders.Next"/>).</summary>
    public enum CrewLeg { Hold, Sit, StandUp, WalkToSeat, WalkToExit, Cross, WalkToSlot }

    /// <summary>One form-up position, in metres from the hull's rear face: <see cref="Back"/> along
    /// the rear direction, <see cref="Side"/> across it.</summary>
    public struct CrewSlot
    {
        public double Back;
        public double Side;
    }

    /// <summary>Crew orders on a dynamic hull: the stance each order resolves to, the chat words
    /// for them, and where a dismounted crew forms up.</summary>
    public static class CrewOrders
    {
        /// <summary>Natural gravity at or above this, in g, is ground: an unordered crew stays
        /// seated there, because the terrain around a grounded hull is walkable and native
        /// navigation does not bring a crewman back from it.</summary>
        public const double GroundGravityG = 0.05;

        /// <summary>A crew under a mount or dismount order outlives the exit grace while its commander
        /// is near: the commander is usually outside with it, or watching it board. At ease and
        /// automatic keep the entry rule.</summary>
        public static bool HoldsCrew(CrewOrder order)
        {
            return order == CrewOrder.Mount || order == CrewOrder.Dismount;
        }

        /// <summary>A crewman still on his way to a seat after this long is put in it. Covers a
        /// walk on the ground, the crossing at the door and the walk aboard.</summary>
        public const double SeatSnapSeconds = 60;

        /// <summary>A crewman still on his way to a form-up slot after this long is put on it.</summary>
        public const double FormUpGiveUpSeconds = 60;

        /// <summary>A crossing through a door that has not arrived after this long is completed by
        /// placing the crewman on the far side.</summary>
        public const double CrossTimeoutSeconds = 12;

        /// <summary>How far behind the rear face the first rank stands, and the spacing between
        /// crewmen and between ranks.</summary>
        public const double FormUpStandOffM = 3;
        public const double FormUpSpacingM = 1.5;
        public const int FormUpRankWidth = 4;

        public static CrewStance Stance(CrewOrder order, double gravityG)
        {
            switch (order)
            {
                case CrewOrder.Mount: return CrewStance.Seated;
                case CrewOrder.Dismount: return CrewStance.FormUp;
                case CrewOrder.Ease: return CrewStance.AtEase;
                default: return gravityG >= GroundGravityG ? CrewStance.Seated : CrewStance.AtEase;
            }
        }

        /// <summary>What a crewman does next. <paramref name="aboard"/>: he stands inside the hull.
        /// <paramref name="atTarget"/>: at his seat (seated stance) or his slot (form-up).
        /// <paramref name="hasExit"/>: the hull has a door with deck inside and ground outside.
        /// <paramref name="atExit"/>: he is at that door on his own side of it. Without an exit a
        /// crewman walks straight at his target, and the stance's time limit places him.</summary>
        public static CrewLeg Next(CrewStance stance, bool seated, bool aboard, bool atTarget, bool hasExit, bool atExit)
        {
            if (stance == CrewStance.Seated)
            {
                if (seated) return CrewLeg.Hold;
                if (aboard) return atTarget ? CrewLeg.Sit : CrewLeg.WalkToSeat;
                if (!hasExit) return CrewLeg.WalkToSeat;
                return atExit ? CrewLeg.Cross : CrewLeg.WalkToExit;
            }
            if (seated) return CrewLeg.StandUp;
            if (atTarget) return CrewLeg.Hold;
            if (aboard && hasExit) return atExit ? CrewLeg.Cross : CrewLeg.WalkToExit;
            return CrewLeg.WalkToSlot;
        }

        public static bool TryParse(string word, out CrewOrder order)
        {
            order = CrewOrder.Auto;
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "mount": case "board": case "seated": case "sit": order = CrewOrder.Mount; return true;
                case "dismount": case "out": order = CrewOrder.Dismount; return true;
                case "ease": case "at-ease": order = CrewOrder.Ease; return true;
                case "auto": order = CrewOrder.Auto; return true;
                default: return false;
            }
        }

        public static string Word(CrewOrder order)
        {
            switch (order)
            {
                case CrewOrder.Mount: return "mount";
                case CrewOrder.Dismount: return "dismount";
                case CrewOrder.Ease: return "ease";
                default: return "auto";
            }
        }

        /// <summary>Ranks of <see cref="FormUpRankWidth"/> abreast behind the hull, the first
        /// <see cref="FormUpStandOffM"/> beyond its rear face, each rank centred on the hull's axis.</summary>
        public static List<CrewSlot> FormUpSlots(int count)
        {
            var slots = new List<CrewSlot>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                int rank = i / FormUpRankWidth;
                int file = i % FormUpRankWidth;
                int inRank = Math.Min(FormUpRankWidth, count - rank * FormUpRankWidth);
                slots.Add(new CrewSlot
                {
                    Back = FormUpStandOffM + rank * FormUpSpacingM,
                    Side = (file - (inRank - 1) / 2.0) * FormUpSpacingM,
                });
            }
            return slots;
        }
    }
}
