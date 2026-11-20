using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Which planet generators a faction node may be sited on the surface of, authored per body in
    /// <c>world_recipe.json</c> and keyed by <b>generator subtype</b>.
    ///
    /// <para><b>Why authored rather than read from SE.</b> <c>MyPlanetGeneratorDefinition
    /// .AllowSurfaceEconomyStations</c> looks like the answer and is not: it defaults to
    /// <c>true</c>, vanilla declares it <c>true</c> on every body that mentions it, and no mod on
    /// this server declares it at all. Uniformly true discriminates nothing, and the body it would
    /// need to catch — a gas giant whose 9.5 km core sits under a 191 km atmosphere — is exactly
    /// the one it does not.</para>
    ///
    /// <para><b>Why keyed by generator, not by name or entity id.</b> A live <see
    /// cref="PlanetRecord.Name"/> is SE's display name (<c>"Planet"</c> for every body on the
    /// generated world), so it cannot match a recipe entry. An entity id changes every time the
    /// world is regenerated — the same rot that left a ring anchored to a planet that no longer
    /// existed. The generator subtype survives both, and landability is a property of the body type
    /// rather than of where it was placed.</para>
    /// </summary>
    public sealed class SurfaceNodePolicy
    {
        private readonly Dictionary<string, bool> _byGenerator =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, double> _separationByGenerator =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, double> _occlusionByGenerator =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string[]> _cropsByGenerator =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, bool> _recruitsByGenerator =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Generators the recipe has an opinion about. Zero means every body defers to SE.</summary>
        public int Count { get { return _byGenerator.Count; } }

        /// <summary>The authored generators and their values, for a boot line — an authored rule
        /// that silently fails to load is indistinguishable from one that was never written.</summary>
        public IEnumerable<KeyValuePair<string, bool>> Entries { get { return _byGenerator; } }

        private SurfaceNodePolicy() { }

        /// <summary>
        /// Build the policy from a recipe. Returns false with a reason rather than throwing, matching
        /// how the rest of the recipe reports a refusal.
        ///
        /// <para>Two entries sharing a generator must agree. Landability belongs to the body type, so
        /// a contradiction is an authoring mistake, and resolving it by last-wins would silently pick
        /// one of two stated intentions.</para>
        /// </summary>
        public static bool TryBuild(WorldRecipe recipe, out SurfaceNodePolicy policy, out string why)
        {
            policy = new SurfaceNodePolicy();
            why = null;
            if (recipe == null || recipe.Planets == null) return true;

            foreach (RecipePlanet p in recipe.Planets)
            {
                if (p == null || !p.AllowSurfaceNodes.HasValue) continue;
                if (string.IsNullOrEmpty(p.Definition))
                {
                    why = "planet '" + (p.Name ?? "(unnamed)") + "' sets AllowSurfaceNodes but has no Definition.";
                    policy = null;
                    return false;
                }

                bool value = p.AllowSurfaceNodes.Value;
                bool existing;
                if (policy._byGenerator.TryGetValue(p.Definition, out existing) && existing != value)
                {
                    why = "generator '" + p.Definition + "' is authored both landable and unlandable; "
                        + "surface access belongs to the body type, so the two entries must agree.";
                    policy = null;
                    return false;
                }
                policy._byGenerator[p.Definition] = value;
            }

            if (!TryReadSeparations(recipe, policy, out why)) { policy = null; return false; }
            if (!TryReadOcclusionRadii(recipe, policy, out why)) { policy = null; return false; }
            if (!TryReadNativeCrops(recipe, policy, out why)) { policy = null; return false; }
            if (!TryReadRecruits(recipe, policy, out why)) { policy = null; return false; }
            return true;
        }

        /// <summary>Reads <see cref="RecipePlanet.NativeCrops"/> under the same rules as the others:
        /// keyed by generator, and two entries sharing a generator must agree, because what grows
        /// wild comes from the generator's own surface rules.</summary>
        private static bool TryReadNativeCrops(WorldRecipe recipe, SurfaceNodePolicy policy, out string why)
        {
            why = null;
            foreach (RecipePlanet p in recipe.Planets)
            {
                if (p == null || p.NativeCrops == null) continue;
                if (string.IsNullOrEmpty(p.Definition))
                {
                    why = "planet '" + (p.Name ?? "(unnamed)") + "' sets NativeCrops but has no Definition.";
                    return false;
                }

                var crops = new List<string>();
                foreach (string c in p.NativeCrops)
                    if (!string.IsNullOrEmpty(c) && !crops.Contains(c)) crops.Add(c);
                crops.Sort(StringComparer.Ordinal);
                string[] value = crops.ToArray();

                string[] existing;
                if (policy._cropsByGenerator.TryGetValue(p.Definition, out existing)
                    && string.Join(",", existing) != string.Join(",", value))
                {
                    why = "generator '" + p.Definition + "' is authored with two different NativeCrops "
                        + "lists; what grows wild belongs to the body type, so the two entries must agree.";
                    return false;
                }
                policy._cropsByGenerator[p.Definition] = value;
            }
            return true;
        }

        /// <summary>Reads <see cref="RecipePlanet.Recruits"/> under the same rules as the others: keyed
        /// by generator, and two entries sharing a generator must agree.</summary>
        private static bool TryReadRecruits(WorldRecipe recipe, SurfaceNodePolicy policy, out string why)
        {
            why = null;
            foreach (RecipePlanet p in recipe.Planets)
            {
                if (p == null || !p.Recruits.HasValue) continue;
                if (string.IsNullOrEmpty(p.Definition))
                {
                    why = "planet '" + (p.Name ?? "(unnamed)") + "' sets Recruits but has no Definition.";
                    return false;
                }
                bool existing;
                if (policy._recruitsByGenerator.TryGetValue(p.Definition, out existing)
                    && existing != p.Recruits.Value)
                {
                    why = "generator '" + p.Definition + "' is authored both with and without a civilian "
                        + "pool; the two entries must agree.";
                    return false;
                }
                policy._recruitsByGenerator[p.Definition] = p.Recruits.Value;
            }
            return true;
        }

        /// <summary>Whether a body of this generator has a civilian pool to recruit from.</summary>
        public bool RecruitsFor(string generator)
        {
            bool recruits;
            return !string.IsNullOrEmpty(generator) && _recruitsByGenerator.TryGetValue(generator, out recruits)
                && recruits;
        }

        /// <summary>The crops that grow wild on a body of this generator, or null when none is
        /// authored or the authored list is empty.</summary>
        public string[] NativeCropsFor(string generator)
        {
            string[] crops;
            if (!string.IsNullOrEmpty(generator) && _cropsByGenerator.TryGetValue(generator, out crops)
                && crops.Length > 0)
                return crops;
            return null;
        }

        /// <summary>Generators carrying an authored crop list, for a boot line.</summary>
        public IEnumerable<KeyValuePair<string, string[]>> NativeCropEntries { get { return _cropsByGenerator; } }

        /// <summary>How many generators carry an authored crop list, empty ones included.</summary>
        public int NativeCropCount { get { return _cropsByGenerator.Count; } }

        /// <summary>Reads <see cref="RecipePlanet.NodeSeparationKm"/> under the same rules as the
        /// landability flag above: keyed by generator, and two entries sharing a generator must agree.
        /// Kept in this class rather than a parallel one because it is the same authored-per-body
        /// lookup over the same recipe entries, and two of them would drift.</summary>
        private static bool TryReadSeparations(WorldRecipe recipe, SurfaceNodePolicy policy, out string why)
        {
            why = null;
            foreach (RecipePlanet p in recipe.Planets)
            {
                if (p == null || !p.NodeSeparationKm.HasValue) continue;
                if (string.IsNullOrEmpty(p.Definition))
                {
                    why = "planet '" + (p.Name ?? "(unnamed)") + "' sets NodeSeparationKm but has no Definition.";
                    return false;
                }

                double value = p.NodeSeparationKm.Value;
                if (!(value > 0))
                {
                    why = "planet '" + (p.Name ?? "(unnamed)") + "' sets NodeSeparationKm to " + value
                        + "; it must be greater than 0. Zero or negative would disable separation entirely "
                        + "rather than authoring a dense body, which is never what an author means.";
                    return false;
                }

                double existing;
                if (policy._separationByGenerator.TryGetValue(p.Definition, out existing) && existing != value)
                {
                    why = "generator '" + p.Definition + "' is authored with two different "
                        + "NodeSeparationKm values (" + existing + " and " + value + "); how densely a "
                        + "body may be settled belongs to the body type, so the two entries must agree.";
                    return false;
                }
                policy._separationByGenerator[p.Definition] = value;
            }
            return true;
        }

        /// <summary>Reads <see cref="RecipePlanet.SensorOcclusionRadiusKm"/> under the same rules as
        /// the two above: keyed by generator, positive, and two entries sharing a generator must
        /// agree. How big a body is to a sensor is a property of the body type, the same as how
        /// densely it may be settled.</summary>
        private static bool TryReadOcclusionRadii(WorldRecipe recipe, SurfaceNodePolicy policy, out string why)
        {
            why = null;
            foreach (RecipePlanet p in recipe.Planets)
            {
                if (p == null || !p.SensorOcclusionRadiusKm.HasValue) continue;
                if (string.IsNullOrEmpty(p.Definition))
                {
                    why = "planet '" + (p.Name ?? "(unnamed)") + "' sets SensorOcclusionRadiusKm but has no Definition.";
                    return false;
                }

                double value = p.SensorOcclusionRadiusKm.Value;
                if (!(value > 0))
                {
                    why = "planet '" + (p.Name ?? "(unnamed)") + "' sets SensorOcclusionRadiusKm to " + value
                        + "; it must be greater than 0. A body that occludes nothing is authored by "
                        + "omitting the field, which defers to its terrain radius.";
                    return false;
                }

                double existing;
                if (policy._occlusionByGenerator.TryGetValue(p.Definition, out existing) && existing != value)
                {
                    why = "generator '" + p.Definition + "' is authored with two different "
                        + "SensorOcclusionRadiusKm values (" + existing + " and " + value + "); how large a "
                        + "body is to a sensor belongs to the body type, so the two entries must agree.";
                    return false;
                }
                policy._occlusionByGenerator[p.Definition] = value;
            }
            return true;
        }

        /// <summary>The authored occluding radius for a body of this generator, or null to defer to
        /// <see cref="PlanetRecord.SurfaceRadius"/>.</summary>
        public double? OcclusionRadiusKmFor(string generator)
        {
            double authored;
            if (!string.IsNullOrEmpty(generator) && _occlusionByGenerator.TryGetValue(generator, out authored))
                return authored;
            return null;
        }

        /// <summary>Generators carrying an authored occluding radius, for a boot line — same reasoning
        /// as <see cref="Entries"/>.</summary>
        public IEnumerable<KeyValuePair<string, double>> OcclusionEntries { get { return _occlusionByGenerator; } }

        /// <summary>How many generators carry an authored occluding radius.</summary>
        public int OcclusionCount { get { return _occlusionByGenerator.Count; } }

        /// <summary>The authored surface separation for a body of this generator, or null to defer to
        /// <see cref="FactionTuning.NodeSeparationSurfaceKm"/>.</summary>
        public double? SeparationKmFor(string generator)
        {
            double authored;
            if (!string.IsNullOrEmpty(generator) && _separationByGenerator.TryGetValue(generator, out authored))
                return authored;
            return null;
        }

        /// <summary>Generators carrying an authored separation, for a boot line — same reasoning as
        /// <see cref="Entries"/>.</summary>
        public IEnumerable<KeyValuePair<string, double>> SeparationEntries { get { return _separationByGenerator; } }

        /// <summary>How many generators carry an authored separation.</summary>
        public int SeparationCount { get { return _separationByGenerator.Count; } }

        /// <summary>
        /// Whether a body of this generator may carry a Surface node. The authored value wins;
        /// otherwise <paramref name="seFlag"/> — SE's own <c>AllowSurfaceEconomyStations</c> — decides,
        /// so a mod that does set it meaningfully is still honoured with no authoring required.
        /// </summary>
        /// <summary>
        /// Stamps every authored per-body rule onto <paramref name="record"/> — the one place a
        /// policy becomes a <see cref="PlanetRecord"/>.
        ///
        /// <para><b>Why this is a method and not two assignments at the call site.</b> The call site
        /// is <c>SePlanetCatalogue</c>, in the net48 host project, which no Core test can reach — so
        /// a test covering "the authored value arrives at <c>WorldModel.SeparationAtKm</c>" would have
        /// had to re-implement the stamping and would then pass while the real one was wrong. That is
        /// exactly how an authored separation stayed inert on <c>testing</c>: every half was tested
        /// against its own copy of the field. Put here, the hop is single-sourced and the host is left
        /// with only "pass the right generator string", which is one visible line.</para>
        /// </summary>
        /// <param name="seFlag">SE's own <c>AllowSurfaceEconomyStations</c> for this body, the
        /// fallback where the recipe has no opinion. There is no SE flag for separation, so an
        /// unauthored body simply keeps <see cref="PlanetRecord.NodeSeparationKm"/> null and defers to
        /// the global per-zone floor.</param>
        public void ApplyTo(PlanetRecord record, string generator, bool seFlag)
        {
            if (record == null) return;
            record.AllowsSurfaceStations = AllowsSurface(generator, seFlag);
            record.NodeSeparationKm = SeparationKmFor(generator);
            record.SensorOcclusionRadiusKm = OcclusionRadiusKmFor(generator);
            record.NativeCrops = NativeCropsFor(generator);
            record.Recruits = RecruitsFor(generator);
        }

        public bool AllowsSurface(string generator, bool seFlag)
        {
            bool authored;
            if (!string.IsNullOrEmpty(generator) && _byGenerator.TryGetValue(generator, out authored))
                return authored;
            return seFlag;
        }
    }
}
