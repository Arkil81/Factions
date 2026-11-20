using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions.Core.Tests
{
    /// <summary>Shared construction helpers for <see cref="StandingOrder"/> across the test suite.
    /// Exists because the pre-FAC-008 idiom (<c>st.MineAutoOrders[hub] = new MineAutoOrder { ... }</c>)
    /// was a dictionary assignment a test could look back up by hub; the list allows several orders
    /// per hub now, so a fixture has to keep the order it created.</summary>
    internal static class StandingOrderFixtures
    {
        internal static StandingOrder AddMineOrder(FactionState st, string hubNodeId, double periodSeconds,
            DateTime nextScanUtc, Dictionary<string, double> oreWeights = null, int priority = 0)
        {
            var order = new StandingOrder
            {
                Id = st.MintStandingOrderId(),
                Kind = StandingOrderKind.Mine,
                Priority = priority,
                HubNodeId = hubNodeId,
                PeriodSeconds = periodSeconds,
                NextScanUtc = nextScanUtc,
                OreWeights = oreWeights,
            };
            st.StandingOrders.Add(order);
            return order;
        }

        /// <summary>One doctrine line on <paramref name="st"/>, minted through the faction's own
        /// counter so the ids a test reads back are the ids the manager would have written.</summary>
        internal static DoctrineLine AddDoctrineLine(FactionState st, string prefabId, int targetCount,
            string scopeNodeId = null, int maxConcurrent = 1, int priority = 0, bool paused = false)
        {
            var line = new DoctrineLine
            {
                Id = st.MintDoctrineLineId(),
                PrefabId = prefabId,
                TargetCount = targetCount,
                MaxConcurrent = maxConcurrent,
                ScopeNodeId = scopeNodeId,
                Priority = priority,
                Paused = paused,
            };
            st.FleetDoctrine.Add(line);
            return line;
        }

    }
}
