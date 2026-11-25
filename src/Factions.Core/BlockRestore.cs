using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// What a block rebuilt from its own object builder, in the same frame it was removed, must not
    /// carry over from the removed one (FAC-522).
    ///
    /// <para>SE writes the live block's <c>EntityId</c> into the builder and <c>MyCubeBlock.Init</c>
    /// reuses any non-zero id. The removed block is closed only after the frame, so its id is still
    /// registered and the rebuild throws <c>DuplicateIdException</c>. A zero id makes SE allocate a
    /// new one. The builder's <c>Name</c> defaults to that same id as a string, and
    /// <c>MyEntities.RemoveName</c> drops a name whoever holds it, so closing the removed block would
    /// unregister the rebuilt one's name. A default name is therefore cleared as well; a name
    /// someone set is kept.</para>
    /// </summary>
    public static class BlockRestore
    {
        /// <summary>The name the rebuilt block is given: null when <paramref name="name"/> is empty
        /// or is the removed block's default (its entity id), otherwise <paramref name="name"/>.</summary>
        public static string RestoredName(string name, long removedEntityId)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return name == removedEntityId.ToString(CultureInfo.InvariantCulture) ? null : name;
        }
    }
}
