using System;

namespace Factions.Core
{
    /// <summary>FAC-440: progress towards a stable waypoint, independent of distance walked.
    /// The caller supplies waypoint coordinates in a frame that follows the navigation surface.</summary>
    public sealed class WaypointProgress
    {
        private bool _known;
        private double _x, _y, _z, _bestDistance;
        public double NoProgressSeconds { get; private set; }
        public bool Stuck => _known && NoProgressSeconds >= 8;

        public void Reset()
        {
            _known = false;
            NoProgressSeconds = 0;
        }

        public bool Observe(bool active, double x, double y, double z, double distance, double elapsedSeconds)
        {
            if (!active || !Finite(x) || !Finite(y) || !Finite(z) || !Finite(distance)
                || distance < 0 || !Finite(elapsedSeconds) || elapsedSeconds <= 0)
            {
                Reset();
                return false;
            }
            double dx = x - _x, dy = y - _y, dz = z - _z;
            if (!_known || dx * dx + dy * dy + dz * dz > 0.01)
            {
                _known = true;
                _x = x; _y = y; _z = z;
                _bestDistance = distance;
                NoProgressSeconds = 0;
            }
            else if (distance <= _bestDistance - 0.25)
            {
                _bestDistance = distance;
                NoProgressSeconds = 0;
            }
            else NoProgressSeconds += elapsedSeconds;
            return Stuck;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
