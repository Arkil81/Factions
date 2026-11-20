using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Which faction the brain actor should tick next. Lives in Core so the policy is
    /// testable; the host only dispatches.</summary>
    public static class FactionTickSelection
    {
        /// <summary>Picks the due faction that has been waiting longest. First-found in list order
        /// would systematically favour whichever faction sits earlier in <paramref name="states"/>
        /// whenever two come due together, which is a fairness cliff as the roster grows.</summary>
        public static bool TryPickMostOverdue(IList<FactionState> states, DateTime nowUtc, out int index)
        {
            index = -1;
            if (states == null) return false;

            DateTime earliest = DateTime.MaxValue;
            for (int i = 0; i < states.Count; i++)
            {
                DateTime due = states[i].NextTickRealUtc;
                if (due > nowUtc) continue;
                // Ties resolve to the lower index so selection stays deterministic.
                if (index < 0 || due < earliest)
                {
                    index = i;
                    earliest = due;
                }
            }
            return index >= 0;
        }
    }
}
