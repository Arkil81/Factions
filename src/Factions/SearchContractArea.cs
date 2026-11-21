using System.Globalization;
using Factions.Core;
using Sandbox.Definitions;

namespace Factions
{
    /// <summary>
    /// Writes <see cref="FactionTuning.SearchContractAreaM"/> onto the loaded <c>Find</c> contract
    /// definition's <c>MaxGpsOffset</c>.
    ///
    /// <para>The definition object is the one <c>MyContractTypeSearchStrategy</c> holds and reads at
    /// generation time, so contracts generated after the write use the new area. A contract already
    /// on a board or accepted carries its own <c>MaxGpsOffset</c> in its object builder and keeps
    /// it. SE does not save definitions with the world, so this runs at every session load, on every
    /// server: each one generates contracts for its own stations. The client reads the radius from
    /// the contract it is sent, so no client change is needed.</para>
    /// </summary>
    internal static class SearchContractArea
    {
        /// <summary>The vanilla Search contract type's subtype in <c>ContractTypes_Economy.sbc</c>.</summary>
        private const string FindSubtype = "Find";

        /// <summary>Applies the tuning value. Returns the line to log, or null when nothing was asked
        /// for or no Search definition is loaded.</summary>
        public static string Apply(FactionTuning tuning)
        {
            if (tuning == null || !SearchContractPolicy.IsOverride(tuning.SearchContractAreaM)) return null;
            if (MyDefinitionManager.Static == null) return null;

            var find = MyDefinitionManager.Static.GetContractType(FindSubtype) as MyContractTypeFindDefinition;
            if (find == null) return null;
            string was = Fmt(find.MaxGpsOffset);
            find.MaxGpsOffset = tuning.SearchContractAreaM;
            return "Search contract area set to " + Fmt(find.MaxGpsOffset) + " m (was " + was + " m).";
        }

        private static string Fmt(double metres)
        {
            return metres.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
