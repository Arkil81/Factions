using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>When in a body's day a <see cref="FaunaRule"/> applies.</summary>
    public enum FaunaTime { Any = 0, Day = 1, Night = 2 }

    /// <summary>
    /// Where on a body a <see cref="FaunaRule"/> applies. Every authored bound must hold; an absent
    /// one does not constrain. Resolved at the <b>player's</b> position, because SE lands every bot of
    /// a wave a few hundred metres from the player it spawned around.
    /// </summary>
    public sealed class FaunaArea
    {
        /// <summary>Signed latitude band in degrees, <c>[min, max]</c>, inclusive. Negative is the
        /// southern hemisphere.</summary>
        public double[] LatitudeDeg;

        /// <summary>Surface elevation band in metres, <c>[min, max]</c>, inclusive. Measured from the
        /// body's lowest terrain, so 0 is the deepest basin and every reading is non-negative. Not
        /// height above the ground: SE spawns only around a player within 50 m of it.</summary>
        public double[] AltitudeM;

        /// <summary>The rule applies only this many km or more from the nearest Factions node. Where
        /// the node positions are not known (a deferred Nexus member, a runtime not yet up) the rule
        /// does not apply — a predator rule must not fire beside a station nobody checked for.</summary>
        public double? AwayFromNodesKm;
    }

    /// <summary>
    /// One authored animal spawn rule on a body. Mirrors SE's <c>MyPlanetAnimalSpawnInfo</c>, in
    /// authoring units: seconds rather than milliseconds, and an <b>inclusive</b> wave range where SE's
    /// own maximum is exclusive. An absent number keeps the generator's own value for it.
    /// </summary>
    public sealed class FaunaRule
    {
        /// <summary>Bot definition subtypes (<c>MyObjectBuilder_AnimalBot</c>), e.g. <c>Cow_Bot</c>.
        /// An <b>empty list is an authored statement</b> that nothing spawns where this rule matches,
        /// which is how a body suppresses its generator's native animals: a last rule with no
        /// <see cref="Where"/> and no animals catches everything the rules above it did not.</summary>
        public List<string> Animals;

        public FaunaTime When = FaunaTime.Any;

        /// <summary>Null applies the rule everywhere on the body.</summary>
        public FaunaArea Where;

        /// <summary>Seconds from a player arriving at a spot to the wave, <c>[min, max]</c>.</summary>
        public double[] DelaySeconds;

        /// <summary>Bots per wave, <c>[min, max]</c>, inclusive.</summary>
        public int[] Wave;

        /// <summary>Metres from the player each bot lands, <c>[min, max]</c>.</summary>
        public double[] DistanceM;

        /// <summary>Seconds an area stays quiet after a bot from it is killed.</summary>
        public double? KillDelaySeconds;

        /// <summary>One wave per area per night: once a wave of this rule spawns, later waves of it
        /// within <see cref="FaunaNightGate.RadiusM"/> are refused for half the world's day. See
        /// <see cref="FaunaNightGate"/>. False keeps SE's own cadence, a wave every
        /// <see cref="DelaySeconds"/> for as long as a player stays.</summary>
        public bool OncePerNight;
    }

    /// <summary>The facts about a spawn site a rule is matched against. Gathered by the host at the
    /// player's position, one read per wave.</summary>
    public struct FaunaSite
    {
        public bool IsNight;
        public double LatitudeDeg;
        public double AltitudeM;

        /// <summary>Km to the nearest Factions node; null when the positions are not known.</summary>
        public double? NearestNodeKm;
    }

    /// <summary>
    /// The animal spawn rules each body carries, authored in <c>world_recipe.json</c> and resolved
    /// against a live planet by generator subtype and terrain seed, exactly as
    /// <see cref="PlanetOrePolicy"/> resolves ore.
    ///
    /// <para><b>Why the recipe and not the generator definition.</b> The creature mods on this server
    /// author their animals on the vanilla <c>Alien</c>, <c>EarthLike</c> and <c>Mars</c> generators,
    /// and this world's bodies are not on any of them. And no definition can say <i>where</i> on a
    /// body an animal belongs: SE's spawn info is one list per body per day or night.</para>
    ///
    /// <para><b>First match wins, in authored order.</b> A rule's numbers are not merged with another
    /// rule's, so order is priority: put the narrow area first.</para>
    ///
    /// <para><b>No match leaves the generator's own animals in place.</b> Authored rules add to a body
    /// where they apply and the native lists carry on elsewhere. A body that should have nothing but
    /// its authored animals ends with a catch-all rule whose <see cref="FaunaRule.Animals"/> is empty.
    /// </para>
    /// </summary>
    public sealed class FaunaPolicy
    {
        /// <summary>One authored body.</summary>
        public sealed class Entry
        {
            public string Name;
            public string Generator;
            public int Seed;
            public IList<FaunaRule> Rules;
        }

        private readonly Dictionary<string, List<Entry>> _byGenerator =
            new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

        private readonly List<Entry> _all = new List<Entry>();

        private FaunaPolicy() { }

        /// <summary>Bodies the recipe authors fauna on. Zero leaves every body on its generator's own
        /// lists, which is the shipped default.</summary>
        public int Count { get { return _all.Count; } }

        public IEnumerable<Entry> Entries { get { return _all; } }

        /// <summary>
        /// Build the policy from a recipe. Returns false with a reason rather than throwing. A null
        /// recipe, or one with no fauna authored, is a success with an empty policy.
        /// </summary>
        public static bool TryBuild(WorldRecipe recipe, out FaunaPolicy policy, out string why)
        {
            policy = new FaunaPolicy();
            why = null;
            if (recipe == null || recipe.Planets == null) return true;

            foreach (RecipePlanet p in recipe.Planets)
            {
                if (p == null || p.Fauna == null) continue;
                string label = string.IsNullOrEmpty(p.Name) ? "(unnamed)" : p.Name;

                if (string.IsNullOrEmpty(p.Definition))
                {
                    why = "planet '" + label + "' authors Fauna but has no Definition -- the generator "
                        + "subtype is what a live body is matched on.";
                    policy = null;
                    return false;
                }

                for (int i = 0; i < p.Fauna.Count; i++)
                    if (!ValidateRule(label, i, p.Fauna[i], out why)) { policy = null; return false; }

                var entry = new Entry
                {
                    Name      = label,
                    Generator = p.Definition,
                    Seed      = WorldRecipe.ResolveSeed(recipe.WorldSeed, p),
                    Rules     = p.Fauna,
                };

                List<Entry> siblings;
                if (!policy._byGenerator.TryGetValue(entry.Generator, out siblings))
                    policy._byGenerator[entry.Generator] = siblings = new List<Entry>();

                foreach (Entry sibling in siblings)
                {
                    if (sibling.Seed != entry.Seed) continue;
                    why = "planets '" + sibling.Name + "' and '" + label + "' both author Fauna on "
                        + "generator '" + entry.Generator + "' with the same seed " + entry.Seed
                        + ", so no live body can be matched to one of them.";
                    policy = null;
                    return false;
                }

                siblings.Add(entry);
                policy._all.Add(entry);
            }
            return true;
        }

        private static bool ValidateRule(string label, int index, FaunaRule r, out string why)
        {
            why = null;
            string at = "planet '" + label + "' Fauna[" + index + "]";
            if (r == null) { why = at + " is null."; return false; }
            if (r.Animals == null)
            {
                why = at + " has no Animals list; author [] to say nothing spawns where it matches.";
                return false;
            }
            foreach (string a in r.Animals)
                if (string.IsNullOrEmpty(a)) { why = at + " names an empty animal subtype."; return false; }

            if (!Range(at, "DelaySeconds", r.DelaySeconds, 0, double.MaxValue, out why)) return false;
            if (!Range(at, "DistanceM", r.DistanceM, 0, double.MaxValue, out why)) return false;
            if (r.Wave != null)
            {
                if (r.Wave.Length != 2 || r.Wave[0] < 1 || r.Wave[1] < r.Wave[0])
                {
                    why = at + " Wave must be [min, max] with 1 <= min <= max; an empty Animals list is "
                        + "how a rule spawns nothing.";
                    return false;
                }
            }
            if (r.KillDelaySeconds.HasValue && !(r.KillDelaySeconds.Value >= 0))
            {
                why = at + " KillDelaySeconds cannot be negative.";
                return false;
            }

            FaunaArea w = r.Where;
            if (w != null)
            {
                if (!Range(at, "Where.LatitudeDeg", w.LatitudeDeg, -90, 90, out why)) return false;
                if (!Range(at, "Where.AltitudeM", w.AltitudeM, 0, double.MaxValue, out why)) return false;
                if (w.AwayFromNodesKm.HasValue && !(w.AwayFromNodesKm.Value >= 0))
                {
                    why = at + " Where.AwayFromNodesKm cannot be negative.";
                    return false;
                }
            }
            return true;
        }

        private static bool Range(string at, string field, double[] v, double lo, double hi, out string why)
        {
            why = null;
            if (v == null) return true;
            if (v.Length != 2 || !(v[0] >= lo) || !(v[1] <= hi) || !(v[0] <= v[1]))
            {
                why = at + " " + field + " must be [min, max] with min <= max, inside [" + lo + ", "
                    + (hi == double.MaxValue ? "inf" : hi.ToString()) + "].";
                return false;
            }
            return true;
        }

        /// <summary>The authored body for a live planet, matched as <see cref="PlanetOrePolicy.TryGet"/>
        /// matches: generator first, and the seed only when two entries share a generator.</summary>
        public bool TryGetBody(string generator, int seed, out Entry body)
        {
            body = null;
            if (string.IsNullOrEmpty(generator)) return false;

            List<Entry> candidates;
            if (!_byGenerator.TryGetValue(generator, out candidates) || candidates.Count == 0) return false;
            if (candidates.Count == 1) { body = candidates[0]; return true; }

            foreach (Entry e in candidates)
                if (e.Seed == seed) { body = e; return true; }
            return false;
        }

        /// <summary>Whether a body can spawn anything authored: at least one rule names an animal.
        /// SE registers a body for spawning only if its generator has a day list, so this is what the
        /// host answers for a body whose generator has none.</summary>
        public static bool HasAnimals(Entry body)
        {
            if (body == null || body.Rules == null) return false;
            foreach (FaunaRule r in body.Rules)
                if (r != null && r.Animals != null && r.Animals.Count > 0) return true;
            return false;
        }

        /// <summary>Whether the body's rules ever ask about the time of day or the nodes, so the host
        /// can skip the reads nothing will consult.</summary>
        public static bool AsksNight(Entry body)
        {
            if (body == null || body.Rules == null) return false;
            foreach (FaunaRule r in body.Rules) if (r != null && r.When != FaunaTime.Any) return true;
            return false;
        }

        public static bool AsksNodes(Entry body)
        {
            if (body == null || body.Rules == null) return false;
            foreach (FaunaRule r in body.Rules)
                if (r != null && r.Where != null && r.Where.AwayFromNodesKm.HasValue) return true;
            return false;
        }

        public static bool AsksTerrain(Entry body)
        {
            if (body == null || body.Rules == null) return false;
            foreach (FaunaRule r in body.Rules)
                if (r != null && r.Where != null && (r.Where.LatitudeDeg != null || r.Where.AltitudeM != null))
                    return true;
            return false;
        }

        /// <summary>The first of <paramref name="body"/>'s rules that holds at <paramref name="site"/>,
        /// or null to leave the generator's own animals in place.</summary>
        public static FaunaRule Match(Entry body, FaunaSite site)
        {
            if (body == null || body.Rules == null) return null;
            foreach (FaunaRule r in body.Rules)
                if (r != null && Holds(r, site)) return r;
            return null;
        }

        private static bool Holds(FaunaRule r, FaunaSite site)
        {
            if (r.When == FaunaTime.Day && site.IsNight) return false;
            if (r.When == FaunaTime.Night && !site.IsNight) return false;

            FaunaArea w = r.Where;
            if (w == null) return true;
            if (!Within(w.LatitudeDeg, site.LatitudeDeg)) return false;
            if (!Within(w.AltitudeM, site.AltitudeM)) return false;
            if (w.AwayFromNodesKm.HasValue)
            {
                if (!site.NearestNodeKm.HasValue) return false;
                if (site.NearestNodeKm.Value < w.AwayFromNodesKm.Value) return false;
            }
            return true;
        }

        private static bool Within(double[] band, double v)
        {
            return band == null || (v >= band[0] && v <= band[1]);
        }
    }
}
