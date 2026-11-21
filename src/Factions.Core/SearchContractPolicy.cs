namespace Factions.Core
{
    /// <summary>
    /// The rule for the search area of SE's vanilla Search contract, kept SE-free so it can be tested.
    ///
    /// <para>SE reads the area from <c>MyContractTypeFindDefinition.MaxGpsOffset</c> each time a
    /// contract is generated: the target spawns at a point, and the GPS marker lands anywhere within
    /// this radius of it. Vanilla content says 3000 m. AWL ran 750 m from a mod's override until
    /// that mod left the mod list, so the value now lives in <see cref="FactionTuning.SearchContractAreaM"/>
    /// and is written onto the loaded definition at every session load.</para>
    /// </summary>
    public static class SearchContractPolicy
    {
        /// <summary>The AWL value: the search area players knew before SE 1.211.</summary>
        public const double DefaultAreaM = 750.0;

        /// <summary>Sentinel meaning "leave whatever SE and the mods loaded alone".</summary>
        public const double NoOverride = 0.0;

        /// <summary>Whether <paramref name="metres"/> asks for the definition to be rewritten. Zero,
        /// a negative, NaN or infinity leaves it alone.</summary>
        public static bool IsOverride(double metres)
        {
            return metres > 0.0 && !double.IsNaN(metres) && !double.IsInfinity(metres);
        }
    }
}
