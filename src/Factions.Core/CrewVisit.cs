using System;

namespace Factions.Core
{
    /// <summary>Entry creates a crew visit; proximity cannot start or extend one. A short exit
    /// grace tolerates jumping, doorways and brief excursions while bounding unseen crews.
    /// A crew dismounted on its owner's order is <i>held</i>: it keeps an active visit while its
    /// commander is near, but cannot start one.</summary>
    public sealed class CrewVisit
    {
        public bool Active { get; private set; }
        public double AbsentSeconds { get; private set; }
        public bool Update(bool aboard, double elapsedSeconds)
        {
            return Update(aboard, false, elapsedSeconds);
        }
        public bool Update(bool aboard, bool held, double elapsedSeconds)
        {
            if (aboard || (held && Active)) { Active = true; AbsentSeconds = 0; }
            else if (Active)
            {
                if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
                    throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
                AbsentSeconds += elapsedSeconds;
                if (AbsentSeconds >= 20) Active = false;
            }
            return Active;
        }
    }
}
