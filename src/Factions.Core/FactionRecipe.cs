using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// One region a faction holds territory in, and how much of it.
    ///
    /// <para>Exactly one of <see cref="Body"/> or <see cref="Feature"/> is set. They are separate
    /// fields rather than one <c>Region</c> string because the two name spaces overlap — this
    /// world has a planet whose generator is <c>Kor</c> and could as easily have a feature called
    /// <c>Kor</c> — and a single key would have to guess. A guess that picks the wrong one places
    /// an estate somewhere plausible and says nothing.</para>
    /// </summary>
    public sealed class EstateRegion
    {
        /// <summary>A planet, keyed by its <b>generator subtype</b> — <c>Tellus</c>, <c>Kor</c>,
        /// <c>RealGasGiant</c> — matching <see cref="RecipePlanet.Definition"/>.
        ///
        /// <para><b>Not the display name.</b> <c>PlanetRecord.Name</c> reads <c>"Planet"</c> for
        /// every body at runtime, so a name key can never match anything. This is the same key
        /// <see cref="SurfaceNodePolicy"/> uses, for the same reason.</para></summary>
        public string Body;

        /// <summary>A world feature, keyed by its <see cref="WorldFeature.Name"/> — "Cauldron Ring".
        /// Feature names do survive to runtime.</summary>
        public string Feature;

        /// <summary>How many nodes this faction starts with here. **Mandatory, and deliberately not
        /// inferred from the region's capacity.**
        ///
        /// <para>Capacity used to do the budgeting by accident: at a 500 km separation a 200 km
        /// feature held 0.31 nodes, so "fill the region" was self-limiting. At 50 km the same feature
        /// holds ~307, and a seeder pointed at it with no cap produces a 300-node faction and no
        /// error anywhere. Geometry stopped being the budget; the author is (FAC-105).</para></summary>
        public int Nodes;

        /// <summary>Which region this entry names, for messages. Never null.</summary>
        public string Describe()
        {
            if (!string.IsNullOrEmpty(Feature)) return "feature '" + Feature + "'";
            if (!string.IsNullOrEmpty(Body)) return "body '" + Body + "'";
            return "(no region)";
        }
    }

    /// <summary>One faction's starting estate: where it holds territory, and how much in each place.
    ///
    /// <para><b>There is no role field, and that is deliberate.</b> "Power base" and "forward
    /// presence" describe the *shape* of an estate, and the shape is already fully expressed by the
    /// node counts — twenty nodes on Tellus and two on Kor is a Tellus power base by construction.
    /// An enum beside the counts would be a second, weaker statement of the same thing, free to
    /// disagree with it. Operator, 2026-08-26: the terms were prompt language, not a taxonomy, and
    /// role "is derived from their node weighting".</para></summary>
    public sealed class FactionEstate
    {
        /// <summary>SE faction tag — <c>SPRT</c>, <c>FCTM</c>, <c>UNKN</c>.</summary>
        public string Tag;

        /// <summary>The regions this faction holds. **An empty list is meaningful**: it authors a
        /// faction that starts with no territory at all, which is exactly what <c>UNKN</c> is — the
        /// destination for abandoned nodes, which fills over time on its own. Omitting the tag
        /// entirely says the same thing less clearly, so authoring it empty is preferred.</summary>
        public List<EstateRegion> Regions = new List<EstateRegion>();

        /// <summary>
        /// How many of this faction's seeded nodes start with a station already standing, rather than
        /// waiting for the brain to found one.
        ///
        /// <para><b>Why this exists.</b> `Found` is a real brain action, but it waits on a claim's
        /// hold consolidating and on its cooldown, so a genesis world would start with nothing
        /// physically in it. This is the bootstrap: enough
        /// standing stations that the world is playable on day one, after which the ordinary Found
        /// path takes over.</para>
        ///
        /// <para>Nodes are chosen in <see cref="Regions"/> order, so a faction's power base is
        /// founded before its forward presence — authoring order is the priority. 0 or absent means
        /// the faction starts with nothing built, which is a legitimate choice and what UNKN wants.
        /// A count larger than the faction's node total founds every node and warns.</para>
        /// </summary>
        public int Founded;

        /// <summary>Hulls this faction starts with, parked at its founded nodes. Empty or absent
        /// means it starts with none, which is what <c>UNKN</c> wants.</summary>
        public List<EstateShips> Ships = new List<EstateShips>();

        /// <summary>Capabilities this faction starts with installed. Empty or absent means it starts
        /// with nothing built and develops from scratch.</summary>
        public List<EstateCapability> Capabilities = new List<EstateCapability>();
    }

    /// <summary>
    /// A capability the faction starts with, and how much of it.
    ///
    /// <para><b>Not to be confused with <c>capability_recipes.json</c>'s <c>Capabilities</c></b>,
    /// which maps a capability to the blueprint classes each of its levels can process. That file
    /// says what a Refinery <i>can do</i>; this says who <i>has</i> one.</para>
    ///
    /// <para><b>Where it lands is not authored, and deliberately so.</b> Naming a node would mean
    /// authoring against ground the recipe has not sampled yet — a node's deposit is resolved when it
    /// is committed. <see cref="EstateSeeder"/> puts each capability on the founded nodes with the
    /// highest <c>NodeContent.CapabilityAffinityFor</c>, which is the same ranking
    /// <c>InvestmentTargeting</c> uses, so a fuel depot lands on ice and a refinery lands on ore
    /// without anyone writing that down twice.</para>
    /// </summary>
    public sealed class EstateCapability
    {
        /// <summary>A <c>NodeCapabilityKind</c> name — <c>Refinery</c>, <c>Manufactory</c>,
        /// <c>Shipyard</c>, <c>FuelDepot</c>, <c>ManufactoryII</c>, <c>RefineryII</c>,
        /// <c>SensorArray</c>.</summary>
        public string Kind;

        /// <summary>How many of this faction's founded nodes get it. Must be greater than zero — a
        /// zero entry and an absent one say the same thing, and only one of them can be read as a
        /// typo.</summary>
        public int Count;

        /// <summary>What level to install it at. Absent or zero means 1.
        ///
        /// <para>Clamped at seed time to <c>FactionTuning.MaxLevelFor</c>, which is not the same for
        /// every capability — Refinery and RefineryII climb to <c>MaxRefineryUpgrades</c>,
        /// Manufactory and ManufactoryII to <c>MaxManufactoryUpgrades</c>, and everything else stops
        /// at 1. The ceiling is a tuning value, so the recipe cannot check it and the seeder warns
        /// rather than refusing.</para></summary>
        public int Level;

        /// <summary>The level this entry asks for, with an absent value read as 1.</summary>
        public int EffectiveLevel { get { return Level > 0 ? Level : 1; } }
    }

    /// <summary>
    /// A count of starting hulls, authored by <b>role</b> rather than by prefab.
    ///
    /// <para><b>Why a role and not a prefab id.</b> A prefab id is a fact about the installed SE
    /// content, and this file is authored offline against a world that may not have it — a name
    /// that no longer resolves seeds nothing and says nothing. A role is a fact about the estate:
    /// "this faction starts able to haul" survives a content update, a mod, and a different world.
    /// It also resolves through <see cref="IPrefabCatalogue.GetNamesForRole"/> — the same picker
    /// <see cref="ShipyardManager"/> uses at build time — so what the recipe asks for and what the
    /// faction would have built for itself cannot drift apart.</para>
    ///
    /// <para>This is the fleet half of the bootstrap <see cref="FactionEstate.Founded"/> is the
    /// station half of: a genesis world's factions have no hulls for weeks, because a hull is built
    /// from a BOM out of node inventory that a fresh estate has not accumulated yet. Seeding cash
    /// does not substitute — hulls are not bought.</para>
    /// </summary>
    public sealed class EstateShips
    {
        /// <summary>A fleet role from <see cref="KnownRoles"/>.</summary>
        public string Role;

        /// <summary>How many hulls of this role. Must be greater than zero — a zero entry and an
        /// absent one say the same thing, and only one of them can be read as a typo.</summary>
        public int Count;

        /// <summary>The roles a recipe may author, matching what
        /// <c>PrefabCatalogueScanner.AssignCapabilityRoles</c> assigns and what
        /// <see cref="IPrefabCatalogue.GetNamesForRole"/> answers to.
        ///
        /// <para>Encounter roles (<c>GlobalEncounter</c>, <c>Encounter</c>,
        /// <c>PlanetaryEncounter</c>) are deliberately absent: they are spawn-table membership, not
        /// something a faction owns.</para></summary>
        public static readonly string[] KnownRoles =
        {
            "Trade", "Ferry", "Gift", "Haul", "Raid", "Patrol", "Guard", "Mine",
        };

        public static bool IsKnownRole(string role)
        {
            if (string.IsNullOrEmpty(role)) return false;
            foreach (string r in KnownRoles)
                if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// The authored starting estates, one entry per faction.
    ///
    /// <para><b>A separate file from the world recipe, on operator direction (2026-08-26).</b> The
    /// world recipe's own <c>WorldEstate</c> block argued the opposite — that one file should
    /// describe the world — and it is superseded. The reason it lost: factions are not the world.
    /// The same bodies and features should be able to take a different faction set, and the estate
    /// brief is thirteen tags against several regions each, which would dwarf the recipe it sat in.
    ///
    /// <para>The cost of the split is that a region here can name a body or feature the world does
    /// not have. That is a resolve-time error, exactly as
    /// <see cref="RecipeFeature.AnchorPlanet"/> naming an absent planet is a plan-time one — the
    /// same check, not a new class of problem.</para></para>
    ///
    /// <para>Parsed in Core, like the world recipe, so a misparsed file is catchable by a unit test
    /// rather than by a half-seeded world.</para>
    /// </summary>
    public sealed class FactionRecipe
    {
        /// <summary>The file this is read from, beside <c>tuning.json</c>.</summary>
        public const string FileName = "faction_recipe.json";

        public List<FactionEstate> Factions = new List<FactionEstate>();

        /// <summary>
        /// The authored diplomacy — who starts hostile to whom. Absent means every pair sits at the
        /// scale's neutral mark.
        ///
        /// <para>Separate from <see cref="Factions"/> and not a field on <see cref="FactionEstate"/>,
        /// because a relation belongs to the <b>pair</b>. Filed under one side it would be authored
        /// twice, free to disagree with itself, and the file would not say which copy is the
        /// world.</para>
        ///
        /// <para>Resolved by <see cref="RelationPlanner"/> against the live roster, not against
        /// <see cref="Factions"/>: the point of the check is the same one
        /// <see cref="EstateSeeder"/> makes — catching a recipe written for a different world's
        /// factions.</para>
        /// </summary>
        public RelationRecipe Relations;

        /// <summary>Structural validation — shape only. Whether the regions exist is a question for
        /// the world, answered by <see cref="ResolveAgainst"/>. Returns false with a reason rather
        /// than throwing, matching how the rest of the recipe reports a refusal.</summary>
        public bool Validate(out string why)
        {
            why = null;
            if (Factions == null) { why = "Factions is missing."; return false; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FactionEstate f in Factions)
            {
                if (f == null) { why = "null faction entry."; return false; }
                if (string.IsNullOrEmpty(f.Tag)) { why = "a faction entry has no Tag."; return false; }
                if (!seen.Add(f.Tag))
                {
                    why = "faction '" + f.Tag + "' appears twice; merge the entries rather than "
                        + "leaving two statements of one estate.";
                    return false;
                }
                if (f.Regions == null) continue;

                var regionsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (EstateRegion r in f.Regions)
                {
                    if (r == null) { why = f.Tag + " has a null region entry."; return false; }

                    bool hasBody = !string.IsNullOrEmpty(r.Body);
                    bool hasFeature = !string.IsNullOrEmpty(r.Feature);
                    if (hasBody == hasFeature)
                    {
                        why = f.Tag + ": a region needs exactly one of Body or Feature"
                            + (hasBody ? ", not both." : "; it has neither.");
                        return false;
                    }
                    if (r.Nodes <= 0)
                    {
                        why = f.Tag + " " + r.Describe() + ": Nodes must be greater than zero. "
                            + "A region's capacity is not a budget -- author the count or drop the region.";
                        return false;
                    }
                    string key = (hasFeature ? "f:" : "b:") + (hasFeature ? r.Feature : r.Body);
                    if (!regionsSeen.Add(key))
                    {
                        why = f.Tag + " names " + r.Describe() + " twice; sum the counts instead.";
                        return false;
                    }
                }

                if (!ValidateShips(f, out why)) return false;
                if (!ValidateCapabilities(f, out why)) return false;
            }

            return ValidateRelations(out why);
        }

        /// <summary>Shape of one faction's <see cref="FactionEstate.Ships"/> block: roles that
        /// resolve, positive counts, and no role authored twice.
        ///
        /// <para>Whether a role has any hull the catalogue can actually offer is <b>not</b> checked
        /// here, for the same reason region existence is not: it is a property of the world, and
        /// this method sees only the file. <see cref="EstateSeeder.Seed"/> answers it against the
        /// live catalogue and refuses there.</para></summary>
        private static bool ValidateShips(FactionEstate f, out string why)
        {
            why = null;
            if (f.Ships == null) return true;

            var rolesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EstateShips s in f.Ships)
            {
                if (s == null) { why = f.Tag + " has a null Ships entry."; return false; }
                if (!EstateShips.IsKnownRole(s.Role))
                {
                    why = f.Tag + ": '" + (s.Role ?? "") + "' is not a fleet role. Author one of: "
                        + string.Join(", ", EstateShips.KnownRoles) + ".";
                    return false;
                }
                if (s.Count <= 0)
                {
                    why = f.Tag + " Ships[" + s.Role + "]: Count must be greater than zero. "
                        + "Drop the entry rather than authoring none of it.";
                    return false;
                }
                if (!rolesSeen.Add(s.Role))
                {
                    why = f.Tag + " names ship role '" + s.Role + "' twice; sum the counts instead.";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Shape of one faction's <see cref="FactionEstate.Capabilities"/> block: kinds that
        /// resolve, positive counts, no kind twice, and no capability authored without the one it
        /// depends on.
        ///
        /// <para>The prerequisite check is here rather than at seed time because it is answerable
        /// from the file alone: <c>CapabilityPrerequisites</c> gates on the faction holding the
        /// prerequisite <b>anywhere</b> in its estate, not on the same node, so "does this faction
        /// author a Manufactory at all" is the whole question. What the ceiling on a level is, by
        /// contrast, is a tuning value the file cannot see — that one is clamped at seed time.</para>
        ///
        /// <para>Level is NOT range-checked against <c>MaxLevelFor</c> here, for that reason.</para>
        /// </summary>
        private static bool ValidateCapabilities(FactionEstate f, out string why)
        {
            why = null;
            if (f.Capabilities == null) return true;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EstateCapability c in f.Capabilities)
            {
                if (c == null) { why = f.Tag + " has a null Capabilities entry."; return false; }

                NodeCapabilityKind kind;
                if (string.IsNullOrEmpty(c.Kind) || !TryParseCapability(c.Kind, out kind))
                {
                    why = f.Tag + ": '" + (c.Kind ?? "") + "' is not a capability. Author one of: "
                        + string.Join(", ", CapabilityNames()) + ".";
                    return false;
                }
                if (c.Count <= 0)
                {
                    why = f.Tag + " Capabilities[" + c.Kind + "]: Count must be greater than zero. "
                        + "Drop the entry rather than authoring none of it.";
                    return false;
                }
                if (c.Level < 0)
                {
                    why = f.Tag + " Capabilities[" + c.Kind + "]: Level cannot be negative.";
                    return false;
                }
                if (!seen.Add(c.Kind))
                {
                    why = f.Tag + " names capability '" + c.Kind + "' twice; one entry carries both "
                        + "how many nodes get it and at what level.";
                    return false;
                }
            }

            foreach (EstateCapability c in f.Capabilities)
            {
                NodeCapabilityKind kind;
                if (!TryParseCapability(c.Kind, out kind)) continue;
                NodeCapabilityKind? prereq = CapabilityPrerequisites.For(kind);
                if (!prereq.HasValue) continue;
                if (seen.Contains(prereq.Value.ToString())) continue;

                why = f.Tag + " authors " + c.Kind + " but not " + prereq.Value
                    + ", which it depends on. The gate is estate-wide rather than per-node, so one "
                    + prereq.Value + " anywhere in the faction is enough -- but with none at all the "
                    + "capability could never have been developed either.";
                return false;
            }
            return true;
        }

        /// <summary>Parse against the <see cref="NodeCapabilityKind"/> enum, deliberately NOT against
        /// <c>FactionTuning.AllCapabilities</c> — that array lists six of the enum's seven and omits
        /// <c>SensorArray</c>, so authoring one through it would be impossible. A numeric string
        /// parses too, hence the <c>IsDefined</c> check.</summary>
        private static bool TryParseCapability(string name, out NodeCapabilityKind kind)
        {
            kind = default(NodeCapabilityKind);
            if (string.IsNullOrEmpty(name)) return false;
            NodeCapabilityKind parsed;
            if (!Enum.TryParse(name, true, out parsed)) return false;
            if (!Enum.IsDefined(typeof(NodeCapabilityKind), parsed)) return false;
            kind = parsed;
            return true;
        }

        private static List<string> CapabilityNames()
        {
            var names = new List<string>();
            foreach (object k in Enum.GetValues(typeof(NodeCapabilityKind))) names.Add(k.ToString());
            return names;
        }

        /// <summary>Shape of the <see cref="Relations"/> block: stance names that resolve, no faction
        /// standing with itself, and no pair written twice under two different stances.
        ///
        /// <para>Tags are <b>not</b> checked here. Which factions exist is a property of the world,
        /// not of this file, and answering it needs the live roster —
        /// <see cref="RelationPlanner.Build"/> does it there and refuses.</para></summary>
        private bool ValidateRelations(out string why)
        {
            why = null;
            if (Relations == null) return true;

            if (!string.IsNullOrEmpty(Relations.Default)
                && !ReputationScale.IsKnownStance(Relations.Default))
            {
                why = "Relations.Default: '" + Relations.Default + "' is not a stance.";
                return false;
            }
            if (Relations.Stances == null) return true;

            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Dictionary<string, string>> from in Relations.Stances)
            {
                if (string.IsNullOrEmpty(from.Key)) { why = "Relations.Stances has an empty tag."; return false; }
                if (from.Value == null) continue;

                foreach (KeyValuePair<string, string> to in from.Value)
                {
                    if (string.IsNullOrEmpty(to.Key))
                    {
                        why = "Relations." + from.Key + " has an empty target.";
                        return false;
                    }
                    if (!ReputationScale.IsKnownStance(to.Value))
                    {
                        why = "Relations." + from.Key + "." + to.Key + ": '" + to.Value
                            + "' is not a stance.";
                        return false;
                    }
                    if (to.Key == RelationRecipe.Wildcard) continue;
                    if (string.Equals(from.Key, to.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        why = "Relations." + from.Key + " names itself; a faction has no standing "
                            + "with itself.";
                        return false;
                    }

                    string key = string.CompareOrdinal(from.Key.ToUpperInvariant(), to.Key.ToUpperInvariant()) <= 0
                        ? from.Key + "-" + to.Key : to.Key + "-" + from.Key;
                    string already;
                    if (pairs.TryGetValue(key, out already)
                        && !string.Equals(already.Trim(), (to.Value ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        why = "Relations: " + key + " is authored twice, as '" + already + "' and '"
                            + to.Value + "'. A relation is one cell -- write it once.";
                        return false;
                    }
                    pairs[key] = to.Value;
                }
            }
            return true;
        }

        /// <summary>
        /// Checks every region against a world recipe: each <see cref="EstateRegion.Body"/> must
        /// match some planet's <see cref="RecipePlanet.Definition"/>, and each
        /// <see cref="EstateRegion.Feature"/> some feature the world holds.
        ///
        /// <para><paramref name="featureNames"/> is passed in rather than read off the recipe because
        /// the ring features are <b>derived</b> from the gas giants and so do not appear in
        /// <see cref="WorldRecipe.Features"/> — resolving against the recipe's authored list alone
        /// would reject "Cauldron Ring", which is the one feature this world actually has.</para>
        /// </summary>
        public bool ResolveAgainst(WorldRecipe world, IEnumerable<string> featureNames, out string why)
        {
            why = null;
            if (world == null) { why = "no world recipe to resolve against."; return false; }

            var bodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (world.Planets != null)
                foreach (RecipePlanet p in world.Planets)
                    if (p != null && !string.IsNullOrEmpty(p.Definition)) bodies.Add(p.Definition);

            var features = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (featureNames != null)
                foreach (string n in featureNames)
                    if (!string.IsNullOrEmpty(n)) features.Add(n);

            foreach (FactionEstate f in Factions ?? new List<FactionEstate>())
            {
                if (f == null || f.Regions == null) continue;
                foreach (EstateRegion r in f.Regions)
                {
                    if (r == null) continue;
                    if (!string.IsNullOrEmpty(r.Body) && !bodies.Contains(r.Body))
                    {
                        why = f.Tag + ": no planet in the world recipe has generator '" + r.Body
                            + "'. Bodies are keyed by generator subtype, not display name.";
                        return false;
                    }
                    if (!string.IsNullOrEmpty(r.Feature) && !features.Contains(r.Feature))
                    {
                        why = f.Tag + ": the world holds no feature named '" + r.Feature + "'.";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>Total nodes this recipe asks for, across every faction — the number a seeder is
        /// about to create, and the one worth printing before it does.</summary>
        public int TotalNodes()
        {
            int total = 0;
            foreach (FactionEstate f in Factions ?? new List<FactionEstate>())
            {
                if (f == null || f.Regions == null) continue;
                foreach (EstateRegion r in f.Regions)
                    if (r != null) total += r.Nodes;
            }
            return total;
        }

        /// <summary>Every capability install the recipe asks for, counting one per node that gets
        /// it. Level is not a multiplier here: three nodes at level 5 is three installs.</summary>
        public int TotalCapabilities()
        {
            int total = 0;
            foreach (FactionEstate f in Factions ?? new List<FactionEstate>())
            {
                if (f == null || f.Capabilities == null) continue;
                foreach (EstateCapability c in f.Capabilities)
                    if (c != null) total += c.Count;
            }
            return total;
        }

        /// <summary>Every hull the recipe asks for, across every faction and role. What a seeding
        /// run should grant when no node refuses one — the ceiling a real run is read against.</summary>
        public int TotalShips()
        {
            int total = 0;
            foreach (FactionEstate f in Factions ?? new List<FactionEstate>())
            {
                if (f == null || f.Ships == null) continue;
                foreach (EstateShips s in f.Ships)
                    if (s != null) total += s.Count;
            }
            return total;
        }
    }
}
