using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Definitions;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// SE-backed <see cref="IPlanetTypeCatalogue"/>: resolves a generator definition's physical
    /// constants so <see cref="WorldPlanner"/> can size gravity wells and storage cubes without
    /// touching SE itself.
    ///
    /// <para>Planet generators live in their own definition set
    /// (<c>GetPlanetsGeneratorsDefinitions</c>) rather than the general one, so the usual
    /// <c>GetAllDefinitions</c> pass does not see them.</para>
    ///
    /// <para>Must be constructed on the SE game thread.</para>
    /// </summary>
    internal sealed class SePlanetTypeCatalogue : IPlanetTypeCatalogue
    {
        private readonly Dictionary<string, PlanetTypeSpec> _byName =
            new Dictionary<string, PlanetTypeSpec>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _byName.Count; } }

        public SePlanetTypeCatalogue()
        {
            if (MyDefinitionManager.Static == null) return;
            IEnumerable<MyPlanetGeneratorDefinition> defs = MyDefinitionManager.Static.GetPlanetsGeneratorsDefinitions();
            if (defs == null) return;

            foreach (MyPlanetGeneratorDefinition def in defs)
            {
                if (def == null) continue;
                string subtype = def.Id.SubtypeName;
                if (string.IsNullOrEmpty(subtype) || _byName.ContainsKey(subtype)) continue;

                _byName[subtype] = new PlanetTypeSpec
                {
                    DefinitionName = subtype,
                    HillParamsMax = def.HillParams.Max,
                    SurfaceGravity = def.SurfaceGravity,
                    GravityFalloffPower = def.GravityFalloffPower,
                    AllowSurfaceEconomyStations = def.AllowSurfaceEconomyStations,
                    AllowOrbitalEconomyStations = def.AllowOrbitalEconomyStations
                };
            }
        }

        public bool TryGet(string definitionName, out PlanetTypeSpec spec)
        {
            spec = null;
            return !string.IsNullOrEmpty(definitionName) && _byName.TryGetValue(definitionName, out spec);
        }

        /// <summary>Every generator subtype this world resolved, sorted — what a recipe may name.</summary>
        public List<string> Names()
        {
            var names = new List<string>(_byName.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }
}
