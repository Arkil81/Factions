using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Provides prefab metadata for the Core simulation. The host plugin implements this with data
    /// scanned from the SE <c>Content/Data/Prefabs</c> directory at session load.
    ///
    /// <para>Faction hint values are SE faction type names ("Miner", "Trader", "Builder",
    /// "Military", "Pirate", "Factorum") or "All" for prefabs not tied to a specific type.
    /// Role values correspond to brain action names ("Trade", "Raid", "Patrol", "Guard",
    /// "Ferry", "Gift", "Store") or encounter categories.</para>
    /// </summary>
    public interface IPrefabCatalogue
    {
        /// <summary>Total litres of cargo-container space across all grids in the prefab.
        /// Returns 0 when the prefabId is unknown or the prefab has no cargo containers.</summary>
        long CargoLiters(string prefabId);

        /// <summary>Litres of gas-tank capacity on the prefab, keyed by the gas's inventory key
        /// (<c>"Gas:Hydrogen"</c>, <c>"Gas:Oxygen"</c>, or a modded gas). Sparse: only gases the
        /// prefab actually has a tank for appear, so a missing key means this hull cannot hold that
        /// gas at all. Returns an empty dictionary for an unknown or tankless prefab.
        ///
        /// <para>Deliberately not folded into <see cref="CargoLiters"/>. A gas tank is a
        /// <c>MyGasTankDefinition</c>, a sibling of <c>MyCargoContainerDefinition</c> rather than a
        /// subclass, and SE has no way to put loose gas in a cargo container — the item that fits in
        /// a hold is a bottle, which is a different thing. Each tank stores exactly one gas
        /// (<c>MyGasTankDefinition.StoredGasId</c>), so hydrogen and oxygen capacity never share a
        /// number.</para></summary>
        IReadOnlyDictionary<string, long> GasTankLiters(string prefabId);

        /// <summary>Component-cost sum for the prefab (analogous to <c>MinimalPricePerUnit</c> for items).
        /// Used as the base price when listing a built ship in a station store block.
        /// Returns 0 for unknown prefabs.</summary>
        long Cost(string prefabId);

        /// <summary>Faction-type hint for the prefab: "Miner", "Trader", "Builder", "Military",
        /// "Pirate", "Factorum", or "All" if the prefab is not faction-specific.
        /// Returns "All" for unknown prefabs.</summary>
        string FactionHint(string prefabId);

        /// <summary>The prefab's weapon loadout: one <see cref="WeaponBand"/> per distinct weapon
        /// range present (Dps summed across blocks sharing that range). Deliberately not collapsed
        /// to a single power/range pair — combat resolution needs to know that a ship's short-range
        /// weapons stay inert during a long-range engagement turn. Returns an empty list for unknown
        /// or unarmed prefabs.</summary>
        IReadOnlyList<WeaponBand> WeaponBands(string prefabId);

        /// <summary>Returns the dominant grid class of the prefab:
        /// <c>"Station"</c> if any grid is static, <c>"Large"</c> if any grid is large-grid,
        /// <c>"Small"</c> if all grids are small-grid, or <c>"Unknown"</c> if the prefab is
        /// not in the catalogue. Static takes priority over size — a large static grid is a
        /// station, not a ship.</summary>
        string GridSize(string prefabId);

        /// <summary>Returns all prefab names suitable for the given role and faction type.
        /// Prefers entries whose <see cref="FactionHint"/> exactly matches <paramref name="factionHint"/>;
        /// falls back to "All" entries when no faction-specific match exists.
        /// Returns an empty list if no prefabs are configured for the role.</summary>
        IReadOnlyList<string> GetNamesForRole(string role, string factionHint);

        /// <summary>The prefab's real per-block thrust data, sparse and keyed by
        /// <see cref="ThrustDirection"/> (only directions with at least one mounted thruster are
        /// present — see <see cref="ThrustBand"/>). Returns an empty dictionary for unknown or
        /// unscanned prefabs. Combine with <see cref="ThrustPhysics"/> to evaluate real force at a
        /// given gravity/atmosphere.</summary>
        IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId);

        /// <summary>Dry hull mass (kg, no cargo), summed from every block's <c>Mass</c> at scan
        /// time. Returns 0 for a prefab the scanner has never resolved — treated as "unknown, don't
        /// gate" by route-feasibility checks (see <see cref="ThrustPhysics.DryTwr"/> callers),
        /// distinct from a genuinely scanned prefab with zero thrust in a given direction.</summary>
        double DryMassKg(string prefabId);

        /// <summary>Relative mining-throughput score, summed across every ship-drill block on the
        /// prefab: per-block swept cross-section, <c>CutOutRadius²</c> (see
        /// <see cref="MineTargeting.DrillBlockScore"/> for why area rather than volume, and why
        /// <c>Speed</c> is not a factor; see <see cref="ShipPool.IsMineSuitable"/> for the role
        /// gate). 0 for a prefab with no drill blocks or unknown to
        /// the catalogue — the real gate for Mine-role eligibility, not cargo capacity.</summary>
        double DrillYieldScore(string prefabId);
    }

    /// <summary>Optional companion to <see cref="IPrefabCatalogue"/> for catalogues that support
    /// player-captured, faction-restricted prefabs (see `!factions ship capture`). Kept separate
    /// from <see cref="IPrefabCatalogue"/> itself (rather than widening it) since C# 7.3 has no
    /// default interface methods and <see cref="IPrefabCatalogue"/> has many test-fake
    /// implementations that have no concept of prefab ownership. Callers should cast
    /// (<c>catalogue as IOwnedPrefabCatalogue</c>) and treat a null result as "no ownership
    /// scoping in play" — every prefab usable by every faction.</summary>
    public interface IOwnedPrefabCatalogue
    {
        /// <summary>The faction tag this prefab is restricted to, or null/empty if it's usable by
        /// any faction (either a vanilla-scanned prefab, or a "capture ... all"-scoped one).</summary>
        string OwnerFactionTag(string prefabId);
    }

    /// <summary>Optional companion to <see cref="IPrefabCatalogue"/> for catalogues that hold
    /// <b>station</b> designs alongside ship hulls — the designs a founded node can be rebuilt to
    /// (see <see cref="RefitStationValidator"/>). Kept separate for the same reason
    /// <see cref="IOwnedPrefabCatalogue"/> is: C# 7.3 has no default interface methods, and every
    /// test fake implementing <see cref="IPrefabCatalogue"/> would otherwise have to grow two
    /// members it has no concept of.
    ///
    /// <para><b>An absent implementation means "no station designs", not "everything qualifies".</b>
    /// That is the opposite default to <see cref="IOwnedPrefabCatalogue"/>, and deliberately so: a
    /// catalogue that cannot tell a static design from a hull must not let a ship be built in place
    /// of a station. A caster that finds null refuses the refit.</para></summary>
    public interface IStationPrefabCatalogue
    {
        /// <summary>True when <paramref name="prefabId"/> is a known station design — a static grid
        /// captured or scanned as a station, never a fleet hull. False for an unknown prefab, so a
        /// true answer also means the catalogue knows the id.</summary>
        bool IsStationPrefab(string prefabId);

        /// <summary>Where the design was measured standing: <c>"Surface"</c> or <c>"Space"</c> (an
        /// orbital capture reads as the latter — see <see cref="StationPrefabSite"/> for the
        /// two-way collapse the refit gate compares on). Null for a prefab with no recorded site,
        /// which no refit accepts.</summary>
        string Site(string prefabId);
    }

    /// <summary>What a hull can hold of each fuel — the capacity side of docs/fuel-plan.md, beside
    /// the burn side (<see cref="ThrustBand.MaxPowerMw"/> and friends). Gas tank litres are already
    /// on <see cref="IPrefabCatalogue.GasTankLiters"/>; this adds the electric store. Every value is
    /// 0/null for a prefab scanned before these existed, and <see cref="HasFuelData"/> is what a
    /// range gate must consult before refusing anything — an unscanned hull is "unknown, don't
    /// gate", the same rule <see cref="IPrefabCatalogue.DryMassKg"/> already sets.</summary>
    public sealed class FuelManifest
    {
        /// <summary>Inventory key of the reactor fuel (<c>"Ingot:Uranium"</c>, or a modded reactor's
        /// ingot), read from <c>MyReactorDefinition.FuelInfos</c>. Null when the hull has no reactor.</summary>
        public string ReactorFuelKey;
        /// <summary>Kilograms of reactor fuel the hull's reactors can hold, from
        /// <c>InventoryMaxVolume</c> divided by the fuel item's volume per kg.</summary>
        public double ReactorFuelCapacityKg;
        /// <summary>Summed <c>MyBatteryBlockDefinition.MaxStoredPower</c>, MWh.</summary>
        public double BatteryMwh;
        /// <summary>True once the scanner has actually resolved this prefab's power blocks, so a
        /// zero above means "none", not "not looked".</summary>
        public bool HasFuelData;

        public static readonly FuelManifest Unknown = new FuelManifest();
    }

    /// <summary>The attitude a hull is built to fly: the nose its author gave it, and the side that
    /// ends up on top. <see cref="HullOrientation.SelectNose"/> picks both, and the remote control the
    /// spawner injects is mounted to match, so this is the frame SE's autopilot flies the hull in.
    ///
    /// <para><see cref="Known"/> false means the catalogue has not resolved the hull, and callers fall
    /// back to <c>Down</c> as the lift bucket — the pre-FAC-215 assumption that a hull flies
    /// belly-down.</para></summary>
    public sealed class HullFrame
    {
        public ThrustDirection Nose;
        public ThrustDirection Up;
        /// <summary>Which evidence picked the nose, for the same reason the spawner logs it: a wrong
        /// attitude is traceable to the signal that produced it.</summary>
        public HullNoseSource Source;
        public bool Known;

        public static readonly HullFrame Unknown = new HullFrame { Nose = ThrustDirection.Forward, Up = ThrustDirection.Up };

        /// <summary>The band bucket that pushes the hull toward <see cref="Up"/> — its lift. A thruster
        /// mounted facing a direction pushes the grid the opposite way, so the bucket is the opposite
        /// of the direction the force goes (see <see cref="ThrustBand"/>). <c>Down</c> for an unknown
        /// frame, which is the belly-down hull.</summary>
        public ThrustDirection LiftBand
        {
            get { return Known ? HullOrientation.Opposite(Up) : ThrustDirection.Down; }
        }
    }

    /// <summary>Optional companion to <see cref="IPrefabCatalogue"/> carrying each hull's flight
    /// frame. Kept separate for the same reason <see cref="IFuelPrefabCatalogue"/> is (C# 7.3, dozens
    /// of test fakes). Cast (<c>catalogue as IHullFramePrefabCatalogue</c>); a null result, or a frame
    /// whose <see cref="HullFrame.Known"/> is false, means "belly-down", the assumption every lift
    /// gate made before FAC-215.</summary>
    public interface IHullFramePrefabCatalogue
    {
        /// <summary>Never null: <see cref="HullFrame.Unknown"/> for a prefab the catalogue does not
        /// know.</summary>
        HullFrame HullFrame(string prefabId);
    }

    /// <summary>Optional companion to <see cref="IPrefabCatalogue"/> carrying the fuel manifest.
    /// Kept separate for the same reason <see cref="IOwnedPrefabCatalogue"/> is (C# 7.3, dozens of
    /// test fakes). Cast (<c>catalogue as IFuelPrefabCatalogue</c>); a null result, or a manifest
    /// whose <see cref="FuelManifest.HasFuelData"/> is false, means "unknown, don't gate".</summary>
    public interface IFuelPrefabCatalogue
    {
        /// <summary>Never null: <see cref="FuelManifest.Unknown"/> for a prefab the catalogue does
        /// not know.</summary>
        FuelManifest FuelManifest(string prefabId);
    }

    /// <summary>Optional companion to <see cref="IPrefabCatalogue"/> carrying what each hull's
    /// weapons fire and how much of it they hold. Kept separate for the same reason
    /// <see cref="IFuelPrefabCatalogue"/> is (C# 7.3, dozens of test fakes). Cast
    /// (<c>catalogue as IMunitionPrefabCatalogue</c>); a null result, or an empty list, means the
    /// hull has nothing to arm and fights as it always did.</summary>
    public interface IMunitionPrefabCatalogue
    {
        /// <summary>Null or empty for an unarmed hull and for a prefab the catalogue does not
        /// know.</summary>
        IReadOnlyList<MunitionSlot> MunitionSlots(string prefabId);
    }
}
