using System;

namespace Factions.Core
{
    /// <summary>
    /// Argument parsing for <c>factions world purge [orphans] [apply] [tag]</c>, in Core so it is
    /// testable without a session.
    ///
    /// <para>The parsing is here rather than inline at the verb because the previous shape had a
    /// silent failure in it: anything that was not the literal <c>apply</c> became the faction tag.
    /// A caller typing <c>orphans</c> would have got a tag filter named "orphans", matched no
    /// faction, and been told "no station records found -- nothing to purge" — a no-op that reads
    /// exactly like a clean world. An unrecognised token is now refused by name instead.</para>
    /// </summary>
    public struct WorldPurgeScope
    {
        /// <summary>Commit rather than describe.</summary>
        public bool Apply;

        /// <summary>Remove only station records no Factions node points at. The safety property of
        /// the whole mode: a node-backed station is never in the plan, so a live estate cannot be
        /// un-founded by a cleanup.</summary>
        public bool OrphansOnly;

        /// <summary>Limit to one faction tag, or null for every faction.</summary>
        public string TagFilter;

        public const string Usage = "Usage: factions world purge [orphans] [apply] [tag]";

        /// <summary>
        /// Parses the verb's arguments. <paramref name="startIndex"/> is the first index past the
        /// sub-verb itself.
        /// </summary>
        /// <param name="error">Null on success; a caller-facing reason on failure.</param>
        public static bool TryParse(string[] tokens, int startIndex, out WorldPurgeScope scope, out string error)
        {
            scope = new WorldPurgeScope();
            error = null;
            if (tokens == null) return true;

            for (int i = startIndex; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (string.IsNullOrEmpty(token)) continue;

                if (string.Equals(token, "apply", StringComparison.OrdinalIgnoreCase))
                {
                    scope.Apply = true;
                    continue;
                }
                if (string.Equals(token, "orphans", StringComparison.OrdinalIgnoreCase))
                {
                    scope.OrphansOnly = true;
                    continue;
                }
                if (scope.TagFilter == null)
                {
                    // A faction tag is 3-4 characters in SE. Refusing anything longer is what stops
                    // a misspelled keyword ("orphan", "orpans") from becoming a tag filter that
                    // quietly matches nothing.
                    if (token.Length > 4)
                    {
                        error = "'" + token + "' is not a faction tag (tags are at most 4 characters) "
                              + "and is not a recognised option. " + Usage;
                        return false;
                    }
                    scope.TagFilter = token;
                    continue;
                }

                error = "unexpected argument '" + token + "' (faction tag already given as '"
                      + scope.TagFilter + "'). " + Usage;
                return false;
            }
            return true;
        }
    }
}
