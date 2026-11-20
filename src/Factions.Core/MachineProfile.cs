

namespace Factions.Core
{
    /// <summary>
    /// One capability kind's physical machine model, as configuration. Every field except the
    /// productivity bonus (see <see cref="FactionTuning.ProductionProductivityBonus"/>) is a raw SE
    /// constant — a block definition value or a world session setting — copied verbatim by the host;
    /// see <c>FactionsPlugin.ResolveMachineProfiles</c>. Core never reads SE APIs, so an unstamped
    /// profile falls back to <see cref="MachinePhysics.DefaultProfile"/>.
    ///
    /// <para>Shaped as a class with public fields so Newtonsoft round-trips it through
    /// <c>tuning.json</c> exactly like <see cref="FactionTuning.InstallBoms"/> — which also means an
    /// operator can hand-edit a stamped profile and <c>factions reload</c> it without a restart.</para>
    /// </summary>
    public sealed class MachineProfile
    {
        /// <summary>SBC <c>RefineSpeed</c> (MyRefineryDefinition) or <c>AssemblySpeed</c> (MyAssemblerDefinition).</summary>
        public double BaseSpeed;

        /// <summary>SBC <c>MaterialEfficiency</c>; scales output amount. 1.0 for assemblers.</summary>
        public double MaterialEfficiency = 1.0;

        /// <summary>World <c>AssemblerEfficiencyMultiplier</c>; divides input amounts. 1.0 for refineries.</summary>
        public double InputEfficiencyDivisor = 1.0;

        /// <summary>World <c>RefinerySpeedMultiplier</c> / <c>AssemblerSpeedMultiplier</c>.</summary>
        public double WorldSpeedMultiplier = 1.0;

        /// <summary>Machines in one installed capability level. Each further level adds this many again.</summary>
        public int MachineCount;

        /// <summary>SE SubtypeId the definition values came from. Provenance for the startup log.</summary>
        public string SourceBlockSubtypeId;
    }

    /// <summary>
    /// A <see cref="MachineProfile"/> resolved against tuning into the four numbers the production
    /// loop actually multiplies by. Resolved once per faction pass and threaded through the bank
    /// loop, so a resumed cycle keeps the model it started with.
    /// </summary>
    public struct MachineModel
    {
        /// <summary>Effective per-machine speed: <c>(BaseSpeed + productivityBonus) * WorldSpeedMultiplier</c>.</summary>
        public double Speed;

        /// <summary>Multiplies every output amount.</summary>
        public double OutputScale;

        /// <summary>Divides every input amount. 1.0 = no division.</summary>
        public double InputDivisor;

        /// <summary>Machines per capability level.</summary>
        public int MachineCount;
    }

    /// <summary>
    /// The SE production formulae, ported. Mirrors <c>MyRefinery.ProcessQueueItems</c> and its
    /// assembler counterpart:
    /// <code>
    /// batches = elapsed * (BaseSpeed + Productivity) * WorldSpeedMultiplier / BaseProductionTimeInSeconds
    /// output  = batches * item.Amount * MaterialEfficiency
    /// input   = batches * item.Amount / AssemblerEfficiencyMultiplier
    /// </code>
    /// This is the one place the formula lives; every call site goes through these helpers so the
    /// batch loop, slot allocation, demand sizing, investment triggers and throughput reporting
    /// cannot drift apart.
    /// </summary>
    public static class MachinePhysics
    {
        // Values transcribed from the live SBCs; kept identical to the pre-profile constants so an
        // unstamped Core (simulator, unit tests) produces exactly what it produced before profiles
        // existed. Real world multipliers and MaterialEfficiency arrive host-stamped.
        // Order mirrors NodeCapabilityKind: Refinery=0, Manufactory=1, Shipyard=2, FuelDepot=3,
        // ManufactoryII=4, RefineryII=5, SensorArray=6, PrototechForge=7, PrototechRefinery=8,
        // Farm=9, FoodProcessor=10.
        //   Refinery I    = 6x Blast Furnace   (RefineSpeed 0.65)
        //   Manufactory I = 6x BasicAssembler  (AssemblySpeed 0.70)
        //   ManufactoryII = 6x LargeAssembler  (no AssemblySpeed tag -> SE C# default 1.00)
        //   RefineryII    = 6x LargeRefinery   (RefineSpeed 1.30)
        //   FuelDepot     = 6x O2/H2 Generator (no speed tag exists on MyOxygenGeneratorDefinition:
        //                   its rate is IceConsumptionPerSecond, which the gas recipe already carries
        //                   as one second of input per TimeSecs=1 batch -- so the machine multiplier
        //                   is a plain 1.00 and the SE numbers stay in the recipe where they belong)
        //   PrototechForge     = 6x LargePrototechAssembler (no AssemblySpeed tag -> 1.00; the block
        //                        buys the PrototechComponents blueprint class, not speed)
        //   PrototechRefinery  = 6x LargePrototechRefinery  (RefineSpeed 5.50, MaterialEfficiency 2.00
        //                        host-stamped from the block definition)
        //   Farm          = 1x IrrigationSystem and the 30 LargeBlockFarmPlots it waters. 30 is the
        //                   fruit-limited count: the system makes 600 L/min and a fruit plot draws
        //                   20 L/min, so every crop fits under it. Six slots at speed 5.00 rather than
        //                   thirty at 1.00 -- the same throughput, a fifth of the slots to tick. The
        //                   farm recipe carries SE's own growth time, so 1.00 per plot is SE's rate.
        //   FoodProcessor = 6x FoodProcessor   (AssemblySpeed 1.00)
        //   Shipyard, SensorArray and Habitat have no recipe-based production -- count 0 skips them.
        private static readonly double[] s_defaultSpeed = { 0.65, 0.70, 0.0, 1.00, 1.00, 1.30, 0.0, 1.00, 5.50, 5.00, 1.00, 0.0 };
        private static readonly int[]    s_defaultCount = { 6, 6, 0, 6, 6, 6, 0, 6, 6, 6, 6, 0 };

        /// <summary>Number of capability kinds the default tables cover.</summary>
        public static int CapabilityCount { get { return s_defaultSpeed.Length; } }

        /// <summary>The unstamped profile for a capability: SBC speed and machine count, with world
        /// multipliers and efficiencies neutral.</summary>
        public static MachineProfile DefaultProfile(NodeCapabilityKind cap)
        {
            int idx = (int)cap;
            var p = new MachineProfile();
            if (idx < 0 || idx >= s_defaultSpeed.Length) return p;
            p.BaseSpeed    = s_defaultSpeed[idx];
            p.MachineCount = s_defaultCount[idx];
            return p;
        }

        /// <summary>SE: <c>(RefineSpeed + UpgradeValues["Productivity"]) * RefinerySpeedMultiplier</c>.</summary>
        public static double EffectiveSpeed(MachineProfile profile, double productivityBonus)
        {
            if (profile == null || profile.BaseSpeed <= 0) return 0.0;
            double world = profile.WorldSpeedMultiplier > 0 ? profile.WorldSpeedMultiplier : 1.0;
            double bonus = productivityBonus > 0 ? productivityBonus : 0.0;
            return (profile.BaseSpeed + bonus) * world;
        }

        /// <summary>SE: <c>batches * item.Amount * MaterialEfficiency * UpgradeValues["Effectiveness"]</c>.
        /// Effectiveness is 1.0 for a speed-module fit and is folded into MaterialEfficiency.</summary>
        public static double OutputScale(MachineProfile profile)
        {
            if (profile == null || profile.MaterialEfficiency <= 0) return 1.0;
            return profile.MaterialEfficiency;
        }

        /// <summary>Resolves one capability's model. <paramref name="productivityBonus"/> is the
        /// modelled upgrade fitment, not an SE constant.</summary>
        public static MachineModel Resolve(MachineProfile profile, double productivityBonus)
        {
            var m = new MachineModel();
            if (profile == null) return m;
            m.Speed        = EffectiveSpeed(profile, productivityBonus);
            m.OutputScale  = OutputScale(profile);
            m.InputDivisor = profile.InputEfficiencyDivisor > 0 ? profile.InputEfficiencyDivisor : 1.0;
            m.MachineCount = profile.MachineCount;
            return m;
        }

        /// <summary>One model per capability kind, indexed by <see cref="NodeCapabilityKind"/> value.</summary>
        public static MachineModel[] ResolveAll(FactionTuning tuning)
        {
            var models = new MachineModel[s_defaultSpeed.Length];
            double bonus = tuning != null ? tuning.ProductionProductivityBonus : 0.0;
            for (int i = 0; i < models.Length; i++)
            {
                var cap = (NodeCapabilityKind)i;
                MachineProfile profile = tuning != null ? tuning.MachineProfileFor(cap) : DefaultProfile(cap);
                models[i] = Resolve(profile, TakesProductivityBonus(cap) ? bonus : 0.0);
            }
            return models;
        }

        /// <summary>Whether a capability's machines can carry the upgrade modules
        /// <see cref="FactionTuning.ProductionProductivityBonus"/> models. A farm plot has no module
        /// ports, so a Farm grows at SE's own rate and nothing faster.</summary>
        public static bool TakesProductivityBonus(NodeCapabilityKind cap)
        {
            return cap != NodeCapabilityKind.Farm;
        }

        /// <summary>SE: an assembler divides prerequisite amounts by
        /// <c>AssemblerEfficiencyMultiplier</c> — unless the block definition or the blueprint itself
        /// sets <c>IgnoreEfficiencyMultiplier</c> (MyAssembler.GetEfficiencyMultiplierForBlueprint).
        /// The blueprint-level opt-out covers the hand tools and weapons ManufactoryII builds, so the
        /// divisor is per recipe, not per capability.</summary>
        public static double InputAmount(double recipeAmount, MachineModel model, bool recipeIgnoresEfficiency)
        {
            if (recipeIgnoresEfficiency || model.InputDivisor <= 0) return recipeAmount;
            return recipeAmount / model.InputDivisor;
        }

        /// <summary>Formats one resolved capability for the host's startup provenance log.</summary>
        public static string Describe(NodeCapabilityKind cap, MachineProfile profile, double productivityBonus)
        {
            if (profile == null) return cap + " (unstamped)";
            return cap
                + " block=" + (profile.SourceBlockSubtypeId ?? "(default)")
                + " base=" + profile.BaseSpeed.ToString("0.00")
                + " prod=" + productivityBonus.ToString("0.00")
                + " world=" + profile.WorldSpeedMultiplier.ToString("0.00")
                + " eff=" + EffectiveSpeed(profile, productivityBonus).ToString("0.00")
                + " matEff=" + profile.MaterialEfficiency.ToString("0.00")
                + " inDiv=" + profile.InputEfficiencyDivisor.ToString("0.00")
                + " count=" + profile.MachineCount;
        }
    }
}
