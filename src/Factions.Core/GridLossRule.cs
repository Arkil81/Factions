using System;

namespace Factions.Core
{
    /// <summary>How the host reads a materialised fleet grid that has left the world: destroyed --
    /// the hull is written off and the fleet force-resolves -- or merely gone, in which case the fleet
    /// flies on abstractly with its stock intact. Absence alone never costs a hull; it takes real
    /// damage inside <see cref="DamageWindow"/>, and a grid our own code closed is never a loss
    /// whatever it took before we closed it.</summary>
    public static class GridLossRule
    {
        /// <summary>How recent the last real hit must be for a grid's disappearance to count as its
        /// destruction. Wide enough to cover a hull shot apart over several passes, far short of the
        /// gap across a server restart.</summary>
        public static readonly TimeSpan DamageWindow = TimeSpan.FromSeconds(120);

        /// <summary>Whether a damage event of <paramref name="amount"/> counts as the grid taking
        /// damage. SE raises zero-amount events (deformation, grazes, blocked hits); they are not
        /// evidence of anything being shot apart.</summary>
        public static bool Stamps(double amount)
        {
            return amount > 0;
        }

        /// <summary>Whether a fleet grid that is no longer in the world was destroyed.
        /// <paramref name="closedByUs"/> is true when this plugin closed it -- a despawn, a
        /// dematerialise, an arrival, a sibling cleanup, or the fleet-mate pass of any of those.
        /// <paramref name="lastDamageUtc"/> is the last real hit on record, null when none.</summary>
        public static bool IsDestroyed(bool closedByUs, DateTime? lastDamageUtc, DateTime nowUtc)
        {
            if (closedByUs || !lastDamageUtc.HasValue) return false;
            return nowUtc - lastDamageUtc.Value <= DamageWindow;
        }
    }
}
