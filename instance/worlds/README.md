# `instance/worlds/` — named worlds, not examples

One folder per authored world. Each holds the two files that **are** that world:

| file | what it is |
|---|---|
| `world_recipe.json` | the authored description — bodies, placement, features. Run `factions world plan` against it to see what would be created before anything is. |
| `world_features.json` | the deep-space features, generated from the recipe. |
| `faction_recipe.json` | who holds what: each faction's regions and node counts, how many nodes start founded, what fleet it begins with, and the authored diplomacy. |

**`faction_recipe.json` is a separate file on purpose** (operator, 2026-08-26): factions are not the
world. The same bodies should be able to take a different faction set, and the estate brief is
thirteen tags against several regions each, which would dwarf the recipe it sat in. The cost of the
split is that a region here can name a body the world does not have — a resolve-time error, caught by
`factions world estate`'s dry run.

**These are the real thing, not samples.** `instance/world_recipe.example.json` used to be this world's
recipe wearing an `.example` suffix, which meant a second world could not be authored without
clobbering the first and nothing in the repo said which role the file was playing (FAC-099).

## Which file to read for what

- **Building or rebuilding a world** → the folder here. Copy both files into `Instance/Factions/`
  beside `tuning.json`.
- **Learning the schema** → these files, which are annotated with `"// ..."` comment keys, and
  `instance/world_features.example.json` for the feature shapes this world does not use
  (`Sphere`, `Shell`). There is deliberately **no** `world_recipe.example.json`: a second annotated
  copy of the schema would rot against the real one, and the real one is the copy a test keeps honest.

## The guard

`WorldRecipeRoundTripTests` plans `awl-factions-genesis/world_recipe.json` and asserts the derived
`Cauldron Ring` matches `awl-factions-genesis/world_features.json` band for band and material for
material. **So edit the recipe and regenerate the features file — do not hand-edit the features.**

That test exists because the two had already drifted: the ring was hand-tuned into the features file
while the recipe still described the ring it replaced, and a rebuild would have produced an annulus
15 km inside the atmosphere, at twice the half-thickness, in the wrong zone, at density zero, made of
nothing but Ice. Every test passed throughout, because they all asserted the derivation against
itself rather than against the artifact.

## Starting fleets

`faction_recipe.json`'s `Ships` block authors hulls by **role**, not by prefab id:

```json
{ "Tag": "IGSL", "Founded": 3,
  "Regions": [ ... ],
  "Ships": [ { "Role": "Mine", "Count": 2 }, { "Role": "Haul", "Count": 3 },
             { "Role": "Guard", "Count": 1 } ] }
```

A prefab id is a fact about the installed SE content and this file is authored offline; a role
resolves through `IPrefabCatalogue.GetNamesForRole` — the same picker `ShipyardManager` uses at build
time — so what the recipe asks for and what the faction would have built for itself cannot drift.

**Three role names, and no more, because there are only three pools.**
`ShipPool.IsSuitableFor` resolves Trade/Gift/Haul to one cargo predicate,
Raid/Retaliate/Conquer/Guard/Patrol to one combat predicate, and Mine to its own. A second role from
the same pool reads as a second intent while delivering more of the same hull, so the shipped file
uses `Mine`, `Haul` and `Guard`, and a test keeps it that way.

Hulls park at **founded** nodes only, round-robined across them in `Regions` order — so a faction's
power base is stocked before its forward presence, the same priority `Founded` itself uses. A hull
that could not hold altitude where it would park is skipped with a warning rather than granted; see
the dry-run gate in [world-builder.md](../../docs/world-builder.md#re-seeding-an-existing-world).

## Starting capabilities

`faction_recipe.json`'s `Capabilities` block says what a faction begins with installed:

```json
{ "Tag": "IGSL", "Founded": 3,
  "Capabilities": [ { "Kind": "Refinery", "Count": 1 }, { "Kind": "Manufactory", "Count": 1 },
                    { "Kind": "Shipyard", "Count": 1 }, { "Kind": "SensorArray", "Count": 1 } ] }
```

`Count` is how many founded nodes get it; `Level` is optional and defaults to 1, clamped at seed time
to `FactionTuning.MaxLevelFor` — which is **not** the same for every capability. Refinery and
RefineryII climb to `MaxRefineryUpgrades`, Manufactory and ManufactoryII to
`MaxManufactoryUpgrades`, and everything else stops at 1.

**Not to be confused with `capability_recipes.json`'s own `Capabilities` key**, which maps a
capability to the blueprint classes each of its levels can process. That file says what a Refinery
*can do*; this says who *has* one.

### Where each one lands is derived, not authored

The seeder puts a capability on the founded nodes with the highest
`NodeContent.CapabilityAffinityFor` — the same ranking `InvestmentTargeting` uses — so a fuel depot
finds ice and a refinery finds ore without anyone writing that down twice. Naming a node here would
mean authoring against ground the recipe has not sampled: a node's deposit is resolved when it is
committed, which is after the file is read.

### Order matters, and the seeder derives it

Installs are sorted into prerequisite order from `CapabilityPrerequisites` rather than a list, so a
new dependency edge cannot leave a stale order behind. The sort is **stable**: two capabilities that
do not depend on each other keep the order they are authored in.

That ordering does more than satisfy the gate. Affinity is re-read after every install, and a
`Shipyard` scores on `HasIndustry` — true only once something that refines or assembles already
stands on the node — so installing Refinery and Manufactory first makes the shipyard land on their
node by itself. Authoring the block in dependency order therefore also reads as the build-up.

**A dry run reports totals and validates the recipe, but does not place.** Affinity needs a committed
node; there is none before an apply. Unknown kinds, missing prerequisites, counts larger than the
estate and levels above the ceiling are all still caught without one.

## Nothing here is deployed

`build.ps1 -Deploy` copies only `tuning_defaults.json` and `station_name_additions.json` into
`Instance/Factions`. A world is placed by hand, deliberately — a deploy must never be able to
overwrite the running world's recipe.

## Worlds

- **`awl-factions-genesis/`** — the world the test server runs. Cauldron (gas giant, 180 km virtual
  body, 195–465 km ring) with Agni, Tellus and Kor on inner/middle/outer orbit bands.
  **`AnchorPlanetId` in its features file is an SE `EntityId` and does not survive a world
  regenerate**; the current value names this world's gas giant only.
