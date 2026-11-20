using System;

namespace Factions.Core
{
    /// <summary>FAC-440: nearest non-self ray hit decides visibility. A shooter-only ray is clear;
    /// a wall or other character before the intended target blocks fire.</summary>
    public sealed class CrewSightline
    {
        private double _nearest = double.PositiveInfinity;
        private bool _clear = true;
        public bool Clear => _clear;
        public bool Observe(double distanceSquared, bool shooter, bool target)
        {
            if (shooter) return false;
            if (double.IsNaN(distanceSquared) || double.IsInfinity(distanceSquared) || distanceSquared < 0)
            {
                _nearest = -1;
                _clear = false;
                return true;
            }
            if (distanceSquared >= _nearest) return false;
            _nearest = distanceSquared;
            _clear = target;
            return true;
        }
    }
}
