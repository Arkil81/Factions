# `Factions.Shared` — source compiled into BOTH the server plugin and the client plugin

Not a project. A directory of `.cs` files that `src/Factions/Factions.csproj` and
`src/FactionsUI/FactionsUI.csproj` each `<Compile Include>` into their own assembly, exactly as both
already do with [`src/Factions.Wire`](../Factions.Wire). `FactionsUI.xml` lists it as a Pulsar
`SourceDirectory` for the same reason.

## What belongs here

Only code where **the server and the client must behave identically, and a divergence would be
silent**. That is a much narrower bar than "both sides happen to need it":

- [`FeatureRockMaterialControl.cs`](FeatureRockMaterialControl.cs) — the Harmony patch that narrows
  SE's voxel-material pool to a feature's authored ore. Both ends run it while building the same rock
  from the same seed. If only one end applied it, the same rock would be ice on one side and gold on
  the other, with nothing to signal it (see [client-plugin, Part 8](../../docs/client-plugin.md)).
- [`WingAntennaActions.cs`](WingAntennaActions.cs) — the wing actions on every antenna's toolbar
  list. A toolbar slot saves an action id and each end resolves it against its own registration: an
  id only one end registers is a slot that silently does nothing on the other (see
  [client-plugin, Part 18](../../docs/client-plugin.md)).
- [`HarmonyPatcher.cs`](HarmonyPatcher.cs) — the pass both plugins run instead of `Harmony.PatchAll`:
  a class that fails to patch costs its own patch group, which is rolled back, and nothing else.
  Two hand-kept copies would drift, and the drift would show only on the day an SE update renames
  something, which is the one day the rule matters (FAC-435). Group a top-level class with
  `[PatchGroup("Name")]`; a nested patch class is in its outer type's group already.
- [`LandingProbe.cs`](LandingProbe.cs) and [`LandingProbeMath.cs`](LandingProbeMath.cs) — the
  `LANDING_PROBE` lines a gear lock to and release from a voxel writes on each end (server
  `factions debug landing`, client "Log diagnostics"). Their purpose is comparing the two ends'
  terrain under a landed ship, so a measurement computed differently on one end would read as
  divergence that is not there. The box snap and checksum are SE-free and unit-tested.

Anything a server may legitimately do differently from a client belongs in its own project. Shared
source is a constraint, not a convenience: everything here has to compile under **both** language
ceilings.

## Constraints

- **C# 7.3.** `src/Factions` is `LangVersion 7.3`. FactionsUI is 14, but that does not help a file
  that has to compile in both.
- **No `Factions.Core` reference.** FactionsUI does not have one.
- **Only assemblies both projects reference** — SE/VRage, `0Harmony`. Not Torch, not NLog, not
  Newtonsoft.
- **No `FactionsPlugin`.** It does not exist on the client. Report through return values and public
  state, and let each host log it.
