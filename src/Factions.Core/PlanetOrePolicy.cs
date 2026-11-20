using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The ore map each body carries, authored in <c>world_recipe.json</c> and resolved against a
    /// live planet by its <b>generator subtype</b> and terrain seed.
    ///
    /// <para><b>Why the recipe and not the generator definition.</b> A planet's ore is whatever
    /// generator subtype the world happens to name, so it is authored by whoever shipped that
    /// definition — vanilla, or a planet mod. The recipe can already say what a ring is made of
    /// (<see cref="FeatureBand.Materials"/>); this is the same statement for a body.</para>
    ///
    /// <para><b>Why keyed by generator first, seed second.</b> A live <see cref="PlanetRecord.Name"/>
    /// is SE's display name — <c>"Planet"</c> for every body on the generated world — so it cannot
    /// match a recipe entry, and an entity id does not survive a regenerate. The generator subtype
    /// survives both. Unlike landability (<see cref="SurfaceNodePolicy"/>) ore is a property of the
    /// body rather than of the body type, so two bodies may share a generator and differ: the seed
    /// separates them. A generator named by exactly one entry matches on the generator alone, so a
    /// body whose seed has drifted from the recipe still gets its ore instead of silently getting
    /// none.</para>
    /// </summary>
    public sealed class PlanetOrePolicy
    {
        /// <summary>One authored body: the map, and the seed that distinguishes it from another
        /// body on the same generator.</summary>
        public sealed class Entry
        {
            public string Name;
            public string Generator;
            public int Seed;
            public IList<RecipeOreMapping> Mappings;
        }

        private readonly Dictionary<string, List<Entry>> _byGenerator =
            new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

        private readonly List<Entry> _all = new List<Entry>();

        private PlanetOrePolicy() { }

        /// <summary>Bodies the recipe has an ore opinion about. Zero means every body keeps its
        /// generator's own map, which is the shipped default.</summary>
        public int Count { get { return _all.Count; } }

        /// <summary>The authored bodies, for a boot line — an authored map that silently fails to
        /// load is indistinguishable from one that was never written.</summary>
        public IEnumerable<Entry> Entries { get { return _all; } }

        /// <summary>
        /// Build the policy from a recipe. Returns false with a reason rather than throwing, matching
        /// how the rest of the recipe reports a refusal.
        ///
        /// <para>A null recipe, or one with no authored map, is a success with an empty policy: no
        /// opinion is the default, not an error.</para>
        /// </summary>
        public static bool TryBuild(WorldRecipe recipe, out PlanetOrePolicy policy, out string why)
        {
            policy = new PlanetOrePolicy();
            why = null;
            if (recipe == null || recipe.Planets == null) return true;

            foreach (RecipePlanet p in recipe.Planets)
            {
                if (p == null || p.OreMappings == null) continue;
                string label = string.IsNullOrEmpty(p.Name) ? "(unnamed)" : p.Name;

                if (string.IsNullOrEmpty(p.Definition))
                {
                    why = "planet '" + label + "' authors OreMappings but has no Definition -- the "
                        + "generator subtype is what a live body is matched on.";
                    policy = null;
                    return false;
                }

                if (!ValidateMappings(label, p.OreMappings, out why)) { policy = null; return false; }

                var entry = new Entry
                {
                    Name      = label,
                    Generator = p.Definition,
                    Seed      = WorldRecipe.ResolveSeed(recipe.WorldSeed, p),
                    Mappings  = p.OreMappings,
                };

                List<Entry> siblings;
                if (!policy._byGenerator.TryGetValue(entry.Generator, out siblings))
                    policy._byGenerator[entry.Generator] = siblings = new List<Entry>();

                foreach (Entry sibling in siblings)
                {
                    if (sibling.Seed != entry.Seed) continue;
                    why = "planets '" + sibling.Name + "' and '" + label + "' both author an ore map on "
                        + "generator '" + entry.Generator + "' with the same seed " + entry.Seed
                        + ", so no live body can be matched to one of them. Give them different seeds "
                        + "or one map.";
                    policy = null;
                    return false;
                }

                siblings.Add(entry);
                policy._all.Add(entry);
            }

            return true;
        }

        /// <summary>Every fault an authored map can carry that is visible without SE. Material
        /// existence is not one of them -- only the host knows which voxel materials this world
        /// resolved -- so it is checked where the map is applied, not here.</summary>
        private static bool ValidateMappings(string label, IList<RecipeOreMapping> mappings, out string why)
        {
            why = null;
            var seen = new Dictionary<byte, string>();

            for (int i = 0; i < mappings.Count; i++)
            {
                RecipeOreMapping m = mappings[i];
                if (m == null)
                {
                    why = "planet '" + label + "' has a null OreMappings entry.";
                    return false;
                }
                if (string.IsNullOrEmpty(m.Material))
                {
                    why = "planet '" + label + "' has an OreMappings entry on Value " + m.Value
                        + " with no Material.";
                    return false;
                }
                if (!(m.Depth > 0))
                {
                    why = "planet '" + label + "' authors Value " + m.Value + " with Depth " + m.Depth
                        + "; a band of no thickness carries no ore, which is what an empty OreMappings "
                        + "list says deliberately.";
                    return false;
                }
                if (m.Start < 0)
                {
                    why = "planet '" + label + "' authors Value " + m.Value + " with Start " + m.Start
                        + "; Start is metres below the surface and cannot be negative.";
                    return false;
                }

                // SE keys its own decode table by this byte and keeps a list per value; the reader
                // this feeds (SeOreGeography.BandsFor) keeps one. Two entries on a value would be
                // visible in-game and invisible to the economy, so the schema forbids it.
                string other;
                if (seen.TryGetValue(m.Value, out other))
                {
                    why = "planet '" + label + "' authors Value " + m.Value + " twice ('" + other
                        + "' and '" + m.Material + "'); one byte of the ore map means one material.";
                    return false;
                }
                seen[m.Value] = m.Material;
            }
            return true;
        }

        /// <summary>
        /// The authored map for a live body, or false to leave the generator's own in place.
        ///
        /// <para>Matches on the generator, then the seed. A generator named by one entry ignores the
        /// seed: the alternative is that a body created before the recipe was authored, or whose
        /// seed was edited, silently keeps vanilla's ore while the boot line says a map was loaded.
        /// </para>
        /// </summary>
        public bool TryGet(string generator, int seed, out IList<RecipeOreMapping> mappings)
        {
            mappings = null;
            if (string.IsNullOrEmpty(generator)) return false;

            List<Entry> candidates;
            if (!_byGenerator.TryGetValue(generator, out candidates) || candidates.Count == 0) return false;

            if (candidates.Count == 1)
            {
                mappings = candidates[0].Mappings;
                return true;
            }

            foreach (Entry e in candidates)
            {
                if (e.Seed != seed) continue;
                mappings = e.Mappings;
                return true;
            }
            return false;
        }
    }
}
