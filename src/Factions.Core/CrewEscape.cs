using System;

namespace Factions.Core
{
    public enum CrewEscapeAction { None, Fly, Teleport }

    /// <summary>FAC-440: bounded escape escalation. Geometry and destination checks belong to the host.</summary>
    public sealed class CrewEscape
    {
        public bool Flying { get; private set; }
        public double CooldownSeconds { get; private set; }
        private double _flightSeconds;
        public CrewEscapeAction Begin(bool embedded, bool clearFlight, bool destinationKnown)
        {
            if (Flying || CooldownSeconds > 0 || !destinationKnown) return CrewEscapeAction.None;
            if (embedded || !clearFlight) return CrewEscapeAction.Teleport;
            Flying = true;
            _flightSeconds = 0;
            return CrewEscapeAction.Fly;
        }
        public CrewEscapeAction Advance(double seconds, bool reached)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) return CrewEscapeAction.None;
            CooldownSeconds = Math.Max(0, CooldownSeconds - seconds);
            if (!Flying) return CrewEscapeAction.None;
            if (reached) { Finish(); return CrewEscapeAction.None; }
            _flightSeconds += seconds;
            if (_flightSeconds < 4) return CrewEscapeAction.None;
            Flying = false;
            return CrewEscapeAction.Teleport;
        }
        public void Finish()
        {
            Flying = false;
            _flightSeconds = 0;
            CooldownSeconds = 10;
        }
    }
}
