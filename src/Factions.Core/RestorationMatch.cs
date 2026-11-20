using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// One reference grid a damaged grid can be matched against: a grid of a catalogue prefab, a
    /// player capture, or a registered design. Carries only a per-definition block count — the
    /// compact form a whole catalogue is indexed in — never the block list, which the host reads
    /// for the few candidates a match selects.
    /// </summary>
    public sealed class RestorationCatalogueEntry
    {
        /// <summary>What a match names: a prefab id, or <c>design:&lt;entity id&gt;</c>.</summary>
        public string Id;
        /// <summary>Which of the reference's grids this entry is.</summary>
        public int GridIndex;
        /// <summary>Blocks in the grid.</summary>
        public int Blocks;
        /// <summary>Definition id (type and subtype) to how many blocks of it the grid holds.</summary>
        public Dictionary<string, int> Definitions = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>The host's handle on the grid, for reading its block list. Not read here.</summary>
        public object Tag;

        public void Count(string definitionId)
        {
            string d = definitionId ?? "";
            int have;
            Definitions.TryGetValue(d, out have);
            Definitions[d] = have + 1;
            Blocks++;
        }

        public static RestorationCatalogueEntry FromBlocks(string id, int gridIndex, IReadOnlyList<RestorationBlock> blocks)
        {
            var e = new RestorationCatalogueEntry { Id = id, GridIndex = gridIndex };
            if (blocks != null)
                for (int i = 0; i < blocks.Count; i++)
                    if (blocks[i] != null) e.Count(blocks[i].DefinitionId);
            return e;
        }
    }

    /// <summary>Which catalogue entries a match scores, and why the rest are not.</summary>
    public sealed class RestorationCandidateSelection
    {
        /// <summary>"anchor" (entries holding a surviving ship controller's definition), "shared"
        /// (no controller survived that any entry holds: entries holding the live grid's rarest
        /// shared definition) or "none".</summary>
        public string Via = "none";
        /// <summary>The definition candidates were keyed on — the ordinal-first anchor definition,
        /// or the rarest shared one.</summary>
        public string KeyDefinition;
        /// <summary>Entries holding the key.</summary>
        public int Eligible;
        /// <summary>Eligible entries whose definition counts alone put them under
        /// <see cref="RestorationMatch.AcceptScore"/> — they cannot be accepted, so are not scored.</summary>
        public int Pruned;
        /// <summary>Eligible entries past <see cref="RestorationMatch.MaxCandidates"/>, left unscored.</summary>
        public int Dropped;
        /// <summary>At most the candidate cap, highest upper bound first.</summary>
        public readonly List<RestorationCatalogueEntry> Selected = new List<RestorationCatalogueEntry>();
        /// <summary>Why no match can be tried at all; null when one can.</summary>
        public string Refused;

        public bool CapHit { get { return Dropped > 0; } }
    }

    /// <summary>A selected entry with its block list, read by the host.</summary>
    public sealed class RestorationMatchCandidate
    {
        public RestorationCatalogueEntry Entry;
        public IReadOnlyList<RestorationBlock> Blocks;
    }

    /// <summary>How one candidate fits the live grid under its best transform.</summary>
    public sealed class RestorationMatchScore
    {
        public RestorationCatalogueEntry Entry;
        public RestorationFit Fit;
        /// <summary>Surviving live blocks that land on a same-definition reference block ÷
        /// surviving live blocks.</summary>
        public double Score;
        /// <summary>Those blocks ÷ the reference's blocks — how much of the reference survives.</summary>
        public double Coverage;
    }

    public sealed class RestorationMatchResult
    {
        /// <summary>The best candidate cleared <see cref="RestorationMatch.AcceptScore"/> and
        /// <see cref="RestorationMatch.MinMatchedBlocks"/>.</summary>
        public bool Accepted;
        /// <summary>The best candidate, accepted or not; null when nothing was scored.</summary>
        public RestorationMatchScore Best;
        /// <summary>The best candidate with a different <see cref="RestorationCatalogueEntry.Id"/>.</summary>
        public RestorationMatchScore RunnerUp;
        public int Scored;
        /// <summary>Candidates whose transform search had more anchor pairs than it scored.</summary>
        public int PairCapHits;

        /// <summary>The best score minus the runner-up's; the best score when there is none.</summary>
        public double Margin
        {
            get { return Best == null ? 0.0 : Best.Score - (RunnerUp != null ? RunnerUp.Score : 0.0); }
        }
    }

    /// <summary>
    /// Finds a damaged grid's design when nothing marks it: every reference that holds the grid's
    /// surviving anchor (a cockpit or remote control; with none, its rarest shared definition) is
    /// aligned on it by <see cref="RestorationDiff.FindTransform"/>, and scored as the share of
    /// surviving blocks that land on a same-definition reference block. The best is accepted at or
    /// above <see cref="AcceptScore"/>; ties go to the reference the grid covers more of, then to
    /// the ordinal-first id, so the answer is deterministic whatever order the catalogue is in.
    ///
    /// <para>Work is bounded twice: at most <see cref="MaxCandidates"/> references are scored, and
    /// each scores at most <see cref="RestorationDiff.MaxAnchorPairs"/> transforms. A reference
    /// whose per-definition counts cannot reach the threshold is never read.</para>
    ///
    /// <para>Pure: <see cref="Match"/> touches only the lists it is given, so a host may run it off
    /// the game thread on block lists it snapshotted there.</para>
    /// </summary>
    public static class RestorationMatch
    {
        /// <summary>The share of surviving blocks that must land on the reference. Below it the grid
        /// is reported unmatched: a player build, or a hull rebuilt past recognition.</summary>
        public const double AcceptScore = 0.75;

        /// <summary>Fewer matched blocks than this cannot identify a design — a cockpit and a few
        /// armour blocks fit a great many.</summary>
        public const int MinMatchedBlocks = 10;

        /// <summary>The most references one match scores.</summary>
        public const int MaxCandidates = 32;

        /// <summary>The references a match of <paramref name="current"/> scores, highest possible
        /// score first (ties by id, then grid index), at most <paramref name="maxCandidates"/>.</summary>
        public static RestorationCandidateSelection Select(IReadOnlyList<RestorationBlock> current,
                                                           IReadOnlyList<RestorationCatalogueEntry> catalogue,
                                                           int maxCandidates = MaxCandidates)
        {
            if (current == null) throw new ArgumentNullException("current");
            var sel = new RestorationCandidateSelection();
            if (catalogue == null) catalogue = new RestorationCatalogueEntry[0];

            var live = new Dictionary<string, int>(StringComparer.Ordinal);
            var anchors = new SortedSet<string>(StringComparer.Ordinal);
            int surviving = 0;
            for (int i = 0; i < current.Count; i++)
            {
                RestorationBlock b = current[i];
                if (b == null) continue;
                surviving++;
                string d = b.DefinitionId ?? "";
                int have;
                live.TryGetValue(d, out have);
                live[d] = have + 1;
                if (b.IsAnchor) anchors.Add(d);
            }
            if (surviving < MinMatchedBlocks)
            {
                sel.Refused = surviving + " block(s) survive; a match needs at least " + MinMatchedBlocks + ".";
                return sel;
            }

            var eligible = new List<RestorationCatalogueEntry>();
            foreach (string a in anchors)
            {
                bool any = false;
                for (int i = 0; i < catalogue.Count; i++)
                    if (catalogue[i] != null && catalogue[i].Definitions.ContainsKey(a)) { any = true; break; }
                if (!any) continue;
                if (sel.KeyDefinition == null) sel.KeyDefinition = a;
                sel.Via = "anchor";
            }
            if (sel.Via == "anchor")
            {
                for (int i = 0; i < catalogue.Count; i++)
                {
                    RestorationCatalogueEntry e = catalogue[i];
                    if (e == null) continue;
                    foreach (string a in anchors)
                        if (e.Definitions.ContainsKey(a)) { eligible.Add(e); break; }
                }
            }
            else
            {
                // No surviving controller that any reference holds: the live definition the fewest
                // references share, ordinal-first on a tie.
                string pick = null;
                int pickCount = int.MaxValue;
                foreach (KeyValuePair<string, int> kv in live)
                {
                    int n = 0;
                    for (int i = 0; i < catalogue.Count; i++)
                        if (catalogue[i] != null && catalogue[i].Definitions.ContainsKey(kv.Key)) n++;
                    if (n == 0) continue;
                    if (n < pickCount || (n == pickCount && string.CompareOrdinal(kv.Key, pick) < 0)) { pick = kv.Key; pickCount = n; }
                }
                if (pick == null)
                {
                    sel.Refused = "no reference shares a block definition with the grid.";
                    return sel;
                }
                sel.Via = "shared";
                sel.KeyDefinition = pick;
                for (int i = 0; i < catalogue.Count; i++)
                    if (catalogue[i] != null && catalogue[i].Definitions.ContainsKey(pick)) eligible.Add(catalogue[i]);
            }
            sel.Eligible = eligible.Count;

            var bounded = new List<KeyValuePair<double, RestorationCatalogueEntry>>(eligible.Count);
            for (int i = 0; i < eligible.Count; i++)
            {
                double bound = UpperBound(live, surviving, eligible[i]);
                if (bound < AcceptScore) { sel.Pruned++; continue; }
                bounded.Add(new KeyValuePair<double, RestorationCatalogueEntry>(bound, eligible[i]));
            }
            bounded.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Value.Id, b.Value.Id);
                return c != 0 ? c : a.Value.GridIndex.CompareTo(b.Value.GridIndex);
            });
            for (int i = 0; i < bounded.Count; i++)
            {
                if (sel.Selected.Count >= maxCandidates) { sel.Dropped = bounded.Count - i; break; }
                sel.Selected.Add(bounded[i].Value);
            }
            return sel;
        }

        /// <summary>The most surviving blocks <paramref name="entry"/> could account for, whatever the
        /// transform: per definition, the smaller of the two counts. The score cannot exceed it.</summary>
        public static double UpperBound(Dictionary<string, int> live, int surviving, RestorationCatalogueEntry entry)
        {
            if (surviving <= 0) return 0.0;
            long n = 0;
            foreach (KeyValuePair<string, int> kv in live)
            {
                int have;
                if (entry.Definitions.TryGetValue(kv.Key, out have)) n += Math.Min(have, kv.Value);
            }
            return (double)n / surviving;
        }

        /// <summary>Scores every candidate against <paramref name="current"/> and picks the best.</summary>
        public static RestorationMatchResult Match(IReadOnlyList<RestorationBlock> current,
                                                   IReadOnlyList<RestorationMatchCandidate> candidates)
        {
            if (current == null) throw new ArgumentNullException("current");
            var result = new RestorationMatchResult();
            int surviving = 0;
            for (int i = 0; i < current.Count; i++) if (current[i] != null) surviving++;
            if (surviving == 0 || candidates == null) return result;

            var scores = new List<RestorationMatchScore>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                RestorationMatchCandidate c = candidates[i];
                if (c == null || c.Entry == null || c.Blocks == null || c.Blocks.Count == 0) continue;
                RestorationFit fit = RestorationDiff.FindTransform(c.Blocks, current);
                result.Scored++;
                if (fit.PairCapHit) result.PairCapHits++;
                int refBlocks = 0;
                for (int k = 0; k < c.Blocks.Count; k++) if (c.Blocks[k] != null) refBlocks++;
                scores.Add(new RestorationMatchScore
                {
                    Entry = c.Entry,
                    Fit = fit,
                    Score = Math.Min(1.0, (double)fit.Matched / surviving),
                    Coverage = refBlocks > 0 ? Math.Min(1.0, (double)fit.Matched / refBlocks) : 0.0,
                });
            }
            if (scores.Count == 0) return result;

            scores.Sort(Better);
            result.Best = scores[0];
            for (int i = 1; i < scores.Count; i++)
                if (!string.Equals(scores[i].Entry.Id, result.Best.Entry.Id, StringComparison.Ordinal)) { result.RunnerUp = scores[i]; break; }
            result.Accepted = result.Best.Score >= AcceptScore && result.Best.Fit.Matched >= MinMatchedBlocks;
            return result;
        }

        /// <summary>Higher score, then higher coverage, then ordinal id, then grid index.</summary>
        private static int Better(RestorationMatchScore a, RestorationMatchScore b)
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = b.Coverage.CompareTo(a.Coverage);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Entry.Id, b.Entry.Id);
            return c != 0 ? c : a.Entry.GridIndex.CompareTo(b.Entry.GridIndex);
        }
    }
}
