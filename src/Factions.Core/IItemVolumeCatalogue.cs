namespace Factions.Core
{
    /// <summary>Resolves physical volume (litres per unit) for items identified by their InvKey.
    /// InvKey convention: "Ore:Iron", "Ingot:Iron", "Comp:SteelPlate", bare SubTypeId otherwise.
    /// Implemented by the host plugin; the core sim uses it for volume-aware cargo loading.</summary>
    public interface IItemVolumeCatalogue
    {
        /// <summary>Volume in litres for one unit of the item identified by <paramref name="invKey"/>.
        /// Returns 0 when the item is unknown (caller should fall back to count caps).</summary>
        double VolumeLitersPerUnit(string invKey);
    }
}
