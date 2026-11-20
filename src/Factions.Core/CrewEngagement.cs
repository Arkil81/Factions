using System;

namespace Factions.Core
{
    public enum CrewCombatIntent { Idle, Pursue, Aim, Fire }

    /// <summary>FAC-440: pursuit and firing-range policy, shared by trial and future defensive crew.
    /// Visibility and alignment are fresh host measurements; losing either immediately ends fire.</summary>
    public sealed class CrewEngagement
    {
        private bool _holding;
        public CrewCombatIntent Update(bool hasTarget, double distance, bool visible, bool aligned)
        {
            if (!hasTarget || double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0)
            {
                _holding = false;
                return CrewCombatIntent.Idle;
            }
            if (!visible || distance > 30) _holding = false;
            else if (distance <= 25) _holding = true;
            if (!_holding) return CrewCombatIntent.Pursue;
            return aligned ? CrewCombatIntent.Fire : CrewCombatIntent.Aim;
        }
    }
}
