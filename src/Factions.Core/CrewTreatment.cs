using System;

namespace Factions.Core
{
    public enum CrewTreatmentIntent { Combat, Retreat, UseKit, Healing }

    /// <summary>FAC-440: finite treatment policy. The host measures native effects and cover;
    /// an active medpack always suppresses another retreat, even if health remains low.</summary>
    public sealed class CrewTreatment
    {
        private double _retreatSeconds, _retrySeconds;
        public CrewTreatmentIntent Intent { get; private set; }
        public CrewTreatmentIntent Update(double seconds, double healthRatio, bool hasKit,
            bool hasThreat, bool medpackActive, bool inCover)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            _retrySeconds = Math.Max(0, _retrySeconds - seconds);
            if (medpackActive)
            {
                _retreatSeconds = 0;
                return Intent = CrewTreatmentIntent.Healing;
            }
            bool needs = hasThreat && hasKit && healthRatio > 0 && healthRatio < 0.5;
            if (!needs)
            {
                _retreatSeconds = 0;
                return Intent = CrewTreatmentIntent.Combat;
            }
            if (Intent == CrewTreatmentIntent.Healing) Intent = CrewTreatmentIntent.Combat;
            if (_retrySeconds > 0) return Intent = CrewTreatmentIntent.Combat;
            _retreatSeconds += seconds;
            if (_retreatSeconds >= 20)
            {
                Defer();
                return Intent;
            }
            return Intent = inCover ? CrewTreatmentIntent.UseKit : CrewTreatmentIntent.Retreat;
        }
        public void Defer()
        {
            _retreatSeconds = 0;
            _retrySeconds = 10;
            Intent = CrewTreatmentIntent.Combat;
        }
    }
}
