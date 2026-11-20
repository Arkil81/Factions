namespace Factions.Core
{
    /// <summary>Resolves physical mass (kg per unit) for items identified by their InvKey.
    /// InvKey convention: "Ore:Iron", "Ingot:Iron", "Comp:SteelPlate", bare SubTypeId otherwise —
    /// same convention as <see cref="IItemVolumeCatalogue"/>. Implemented by the host plugin; the
    /// core sim uses it for mass-aware cargo/thrust-to-weight feasibility.</summary>
    public interface IItemMassCatalogue
    {
        /// <summary>Mass in kg for one unit of the item identified by <paramref name="invKey"/>.
        /// Returns 0 when the item is unknown (caller should treat loaded mass as dry-mass-only).</summary>
        double MassKgPerUnit(string invKey);
    }
}
