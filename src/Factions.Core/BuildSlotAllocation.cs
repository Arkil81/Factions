using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Decides which build queue entries hold a faction's shipyard slots, across every yard
    /// the faction owns rather than one queue at a time.
    ///
    /// <para><b>Why it is not a per-node question.</b> A yard works
    /// <see cref="SpatialNode.BuildSlotCount"/> entries and the rest wait, so ranking inside one
    /// queue decides which role that yard serves. Ranked per node against a role table, a faction
    /// whose Trade demand never empties gives every yard's slot to Trade and its combat orders never
    /// start — measured live 2026-09-15 at SPRT: 9 yards, 43 entries, 2 combat hulls in 243 builds,
    /// with a Patrol entry logged `waiting behind 3..6` at nine separate yards and admitted to a slot
    /// not once. The same table read faction-wide asks the answerable question instead: of the slots
    /// this faction has, how many should each role hold.</para>
    ///
    /// <para><b>The share follows live demand, and bootstrap overrides it.</b> A role's quota is its
    /// share of the faction's total capacity, weighted by how many <c>Ship:{role}</c> demand rows the
    /// faction currently carries. Above that sits one absolute rule: a role the faction owns
    /// <b>no</b> suitable hull for is uncapped, because that is the state the role table was
    /// introduced for (FAC-204 — a faction with no drill cannot mine its way to one) and it clears
    /// itself the moment the first hull exists. That, and not a standing ranking, is the whole job
    /// the table was doing.</para>
    ///
    /// <para><b>Within a round it is the longest wait that is served, not the largest share.</b> A
    /// faction with fewer slots than queued roles would otherwise serve the same role every round,
    /// and a role whose entries churn always presents a younger entry than a role whose entry has
    /// been waiting. Share governs how many rounds a role wins; age governs who goes first.</para>
    ///
    /// <para><b>What this does not decide.</b> <see cref="BuildQueueEntry.Priority"/> keeps its other
    /// job untouched — which entry wins a contested component against this node's other builds and
    /// against a capability install on the same scale
    /// (<see cref="SpatialNode.BuildQueueReserved"/>, <see cref="InstallReserveFloor"/>).
    /// <see cref="SpatialNode.ClaimOrderedBuildQueue"/> answers that one. A rank a <b>human</b> moved
    /// off its role default is still an instruction about the slot, though, and
    /// <see cref="TierOf"/> reads it as one — see there.</para></summary>
    public static class BuildSlotAllocation
    {
        /// <summary>Marks a shelf build's pseudo-role, whatever capability role it names. Every hull
        /// destined for the store is one purpose competing for one share, not four.</summary>
        private const string StoreKey = "*Store";

        /// <summary>Prefixes a build named by hull's design family, so a design whose name happens
        /// to be a role's cannot share that role's quota.</summary>
        private const string DesignKeyPrefix = "#";

        /// <summary>One queued entry with everything the allocation sorts and groups it by, resolved
        /// once so the passes below cannot disagree about an entry's tier or its place in the
        /// queue.</summary>
        private sealed class Candidate
        {
            public BuildQueueEntry Entry;
            public int NodeIndex;
            /// <summary>Position in its own yard's <see cref="SpatialNode.ClaimOrderedBuildQueue"/>,
            /// so where age ties the component ranking still breaks it.</summary>
            public int ClaimIndex;
            public int Tier;
            public string Key;
            public bool Chosen;
        }

        /// <summary>Re-decides which entries hold <paramref name="factionTag"/>'s slots and writes the
        /// result to each yard's <see cref="SpatialNode.ActiveBuildIds"/>. Idempotent, O(entries), and
        /// safe to run as often as a caller likes — it reads persisted state and derives nothing that
        /// outlives the next call.
        ///
        /// <para><paramref name="log"/>, when supplied, gets a <c>BUILD_SLOTS</c> line for each yard
        /// whose working set actually changed. The choice is derived and never persisted, so without
        /// it nothing outside this call can say which entry a yard is feeding or why — and the
        /// symptom this exists to fix, a role that is worked nowhere, is only visible as an absence
        /// over a day of <c>SHIP_BUILT</c> lines.</para></summary>
        public static void Allocate(WorldModel world, string factionTag, FactionTuning tuning,
            List<string> log = null)
        {
            if (world == null || string.IsNullOrEmpty(factionTag)) return;

            var yards = new List<SpatialNode>();
            foreach (SpatialNode n in world.Graph.OwnedBy(factionTag))
            {
                // A paused yard claims nothing and services nothing whatever this says, and an empty
                // queue has nothing to allocate. Both drop back to the node-local fallback rather
                // than carrying a stale set.
                if (n.BuildQueue.Count == 0 || n.ShipyardPaused) { n.ActiveBuildIds = null; continue; }
                yards.Add(n);
            }
            if (yards.Count == 0) return;

            HashSet<string> bootstrapKeys = BootstrapRoles(world, factionTag);

            var candidates = new List<Candidate>();
            int totalCapacity = 0;
            for (int i = 0; i < yards.Count; i++)
            {
                List<BuildQueueEntry> claimOrder = yards[i].ClaimOrderedBuildQueue();
                for (int j = 0; j < claimOrder.Count; j++)
                {
                    BuildQueueEntry e = claimOrder[j];
                    // A free build occupies no slot and needs none (SpatialNode.IsActiveBuild).
                    // Expiry is deliberately NOT a filter here: the ordering this replaces did not
                    // apply one either, and an entry past its stamp still holds its place until
                    // something prunes it.
                    if (e.Bom.Count == 0) continue;
                    candidates.Add(new Candidate
                    {
                        Entry = e, NodeIndex = i, ClaimIndex = j,
                        Tier = TierOf(e, tuning, bootstrapKeys, world.PrefabCatalogue),
                        Key = KeyOf(e, world.PrefabCatalogue),
                    });
                }
                totalCapacity += yards[i].BuildSlotCount();
            }

            var chosen = new List<HashSet<string>>(yards.Count);
            for (int i = 0; i < yards.Count; i++) chosen.Add(new HashSet<string>(StringComparer.Ordinal));
            if (candidates.Count == 0)
            {
                for (int i = 0; i < yards.Count; i++) yards[i].ActiveBuildIds = chosen[i];
                return;
            }

            candidates.Sort(Compare);
            var used = new int[yards.Count];

            // A refit is work the owner asked for by hand and a rank a human raised is the same kind
            // of instruction; a role the faction owns no hull for is the one automatic rule above the
            // share. None of the three is a quantity to be balanced against the others.
            Admit(candidates, yards, chosen, used, delegate (Candidate c) { return c.Tier <= TierBootstrap; });

            QuotaRounds(world, factionTag, tuning, candidates, yards, chosen, used, totalCapacity);

            // Nothing is left idle for want of a quota: a slot this faction cannot give to its share
            // goes to whoever is next, because an unworked yard builds nothing for anybody. This is
            // also what keeps "chosen" and "inside the first BuildSlotCount of OrderedBuildQueue" the
            // same set, which SpatialNode.IsActiveBuild depends on.
            Admit(candidates, yards, chosen, used, null);

            for (int i = 0; i < yards.Count; i++)
            {
                if (log != null && !SameSet(yards[i].ActiveBuildIds, chosen[i]))
                    log.Add(factionTag + " BUILD_SLOTS node=" + yards[i].Id
                            + " working=" + Join(chosen[i], yards[i], true)
                            + " waiting=" + Join(chosen[i], yards[i], false));
                yards[i].ActiveBuildIds = chosen[i];
            }
        }

        private static bool SameSet(HashSet<string> previous, HashSet<string> current)
        {
            if (previous == null || previous.Count != current.Count) return false;
            foreach (string id in current) if (!previous.Contains(id)) return false;
            return true;
        }

        /// <summary>Comma-joined entry ids on one side of the split, or "-" for none. Ids carry their
        /// own role (<see cref="SpatialNode.MintBuildEntryId"/>), which is what makes the line answer
        /// "which role is this yard feeding" without repeating it; a build named by hull reads
        /// <c>B:seq</c>.</summary>
        private static string Join(HashSet<string> active, SpatialNode yard, bool wantActive)
        {
            var sb = new System.Text.StringBuilder();
            foreach (BuildQueueEntry e in yard.ClaimOrderedBuildQueue())
            {
                if (e.Bom.Count == 0) continue;
                if (active.Contains(e.Id) != wantActive) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(e.Id);
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        /// <summary>Gives each role its demand-weighted share of the faction's slots, one entry per
        /// role per round so a role with a large share does not fill every yard before a role with a
        /// small one has started. Only the automatically-ranked tiers are apportioned; the rest are
        /// already placed, or are deliberately last.</summary>
        private static void QuotaRounds(WorldModel world, string factionTag, FactionTuning tuning,
            List<Candidate> candidates, List<SpatialNode> yards, List<HashSet<string>> chosen,
            int[] used, int totalCapacity)
        {
            var entryCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (Candidate c in candidates)
            {
                if (c.Tier != TierFleet && c.Tier != TierStore) continue;
                int have;
                if (!entryCount.TryGetValue(c.Key, out have)) order.Add(c.Key);   // candidates are sorted, so this is age order
                entryCount[c.Key] = have + 1;
            }
            if (order.Count == 0) return;

            Dictionary<string, int> quota = Apportion(world, factionTag, entryCount, totalCapacity);
            var assigned = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in order) assigned[key] = 0;

            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (string key in order)
                {
                    if (assigned[key] >= quota[key]) continue;
                    string wanted = key;
                    if (!AdmitOne(candidates, yards, chosen, used,
                                  delegate (Candidate c) { return string.Equals(c.Key, wanted, StringComparison.Ordinal); }))
                        continue;
                    assigned[key] = assigned[key] + 1;
                    progress = true;
                }
            }
        }

        /// <summary>Largest-remainder share of <paramref name="totalCapacity"/> by live
        /// <c>Ship:{role}</c> demand row count, floored at one slot for any role that has an entry at
        /// all and capped at the entries it actually has. Bootstrap roles never reach here — they are
        /// already placed, above the share entirely.</summary>
        private static Dictionary<string, int> Apportion(WorldModel world, string factionTag,
            Dictionary<string, int> entryCount, int totalCapacity)
        {
            var weight = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> kv in entryCount) weight[kv.Key] = 0.0;

            foreach (DemandEntry d in world.Demand.SnapshotShipDemands(factionTag))
            {
                if (d.Kind == null || d.Kind.Length <= 5) continue;
                string key = d.DestinedForSale ? StoreKey : d.Kind.Substring(5);
                double have;
                if (!weight.TryGetValue(key, out have)) continue;   // no entry queued for it
                weight[key] = have + 1.0;
            }

            // A queued entry with no live demand behind it is still real work -- the orphan pass feeds
            // exactly that case -- so every role with an entry carries at least unit weight.
            double total = 0.0;
            foreach (string key in new List<string>(weight.Keys))
            {
                if (weight[key] < 1.0) weight[key] = 1.0;
                total += weight[key];
            }

            var quota = new Dictionary<string, int>(StringComparer.Ordinal);
            var remainder = new List<KeyValuePair<string, double>>();
            int handed = 0;
            foreach (KeyValuePair<string, double> kv in weight)
            {
                double exact = totalCapacity * kv.Value / total;
                int whole = (int)Math.Floor(exact);
                if (whole < 1) whole = 1;                               // never zero out a queued role
                if (whole > entryCount[kv.Key]) whole = entryCount[kv.Key];
                quota[kv.Key] = whole;
                handed += whole;
                remainder.Add(new KeyValuePair<string, double>(kv.Key, exact - Math.Floor(exact)));
            }

            remainder.Sort(delegate (KeyValuePair<string, double> a, KeyValuePair<string, double> b)
            {
                int byFraction = b.Value.CompareTo(a.Value);
                return byFraction != 0 ? byFraction : string.CompareOrdinal(a.Key, b.Key);
            });
            for (int i = 0; handed < totalCapacity && i < remainder.Count; i++)
            {
                string key = remainder[i].Key;
                if (quota[key] >= entryCount[key]) continue;
                quota[key] = quota[key] + 1;
                handed++;
            }

            return quota;
        }

        /// <summary>Gives a slot to every candidate <paramref name="match"/> accepts, in sorted order,
        /// while its yard has one. Null matches everything.</summary>
        private static void Admit(List<Candidate> candidates, List<SpatialNode> yards,
            List<HashSet<string>> chosen, int[] used, Predicate<Candidate> match)
        {
            while (AdmitOne(candidates, yards, chosen, used, match)) { }
        }

        /// <summary>Gives a slot to the first still-unchosen candidate <paramref name="match"/> accepts
        /// whose yard has one, and reports whether it found one.</summary>
        private static bool AdmitOne(List<Candidate> candidates, List<SpatialNode> yards,
            List<HashSet<string>> chosen, int[] used, Predicate<Candidate> match)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                if (c.Chosen) continue;
                if (match != null && !match(c)) continue;
                if (used[c.NodeIndex] >= yards[c.NodeIndex].BuildSlotCount()) continue;
                c.Chosen = true;
                chosen[c.NodeIndex].Add(c.Entry.Id);
                used[c.NodeIndex] = used[c.NodeIndex] + 1;
                return true;
            }
            return false;
        }

        /// <summary>Tier first; then, among the two tiers a human ranked by hand, the rank itself;
        /// then the longest wait, ties broken by the yards' own claim order so an all-default queue is
        /// exactly the first-come order it was before slot choice became faction-wide.</summary>
        private static int Compare(Candidate a, Candidate b)
        {
            int byTier = a.Tier.CompareTo(b.Tier);
            if (byTier != 0) return byTier;
            if (a.Tier == TierPlayerRaised || a.Tier == TierPlayerLowered)
            {
                int byRank = b.Entry.Priority.CompareTo(a.Entry.Priority);
                if (byRank != 0) return byRank;
            }
            int byAge = a.Entry.CreatedUtc.CompareTo(b.Entry.CreatedUtc);
            if (byAge != 0) return byAge;
            int byNode = a.NodeIndex.CompareTo(b.NodeIndex);
            return byNode != 0 ? byNode : a.ClaimIndex.CompareTo(b.ClaimIndex);
        }

        private const int TierRefit         = 0;
        private const int TierPlayerRaised  = 1;
        private const int TierBootstrap     = 2;
        private const int TierFleet         = 3;
        private const int TierStore         = 4;
        private const int TierPlayerLowered = 5;

        /// <summary>Which band an entry competes in.
        ///
        /// <para>A refit is a station rebuild the owner asked for by hand and is admitted first, as
        /// its own rank already promised. A rank a human moved off the role default is read as an
        /// instruction about the slot in whichever direction they moved it — raised goes before the
        /// share, lowered goes after it — so <c>!factions station queue priority</c> still does what
        /// its help text says while the automatic case is decided by the share. That case is
        /// identified by <see cref="BuildQueueEntry.PriorityByPlayer"/> and never by comparing the
        /// rank to today's table: an entry that outlived a change to the table carries a rank the
        /// table no longer answers, and reading that as an instruction admits it ahead of the whole
        /// fleet. Below the human
        /// instructions sits the one automatic rule the share must not bound: a role the faction owns
        /// no suitable hull for is served before any share is apportioned, because a share of
        /// something is no use to a faction that has none of it. A shelf build sorts below the fleet
        /// for the reason its own rank carried: it is the one hull nothing else is waiting
        /// on.</para></summary>
        private static int TierOf(BuildQueueEntry e, FactionTuning tuning, HashSet<string> bootstrapKeys,
            IPrefabCatalogue cat)
        {
            if (ShipyardManager.IsStationRefit(e)) return TierRefit;
            if (e.PriorityByPlayer)
            {
                int start = ShipyardManager.RolePriority(e.Role, tuning, e.DestinedForSale);
                if (e.Priority > start) return TierPlayerRaised;
                if (e.Priority < start) return TierPlayerLowered;
            }
            if (!e.DestinedForSale && IsBootstrap(e, bootstrapKeys, cat)) return TierBootstrap;
            return e.DestinedForSale ? TierStore : TierFleet;
        }

        /// <summary>The fleet roles <paramref name="factionTag"/> owns no hull for anywhere in its
        /// estate — the state a role table was introduced for (FAC-204: a faction with no drill
        /// cannot mine its way to one), and the only automatic rule allowed above the share.
        ///
        /// <para>Empty when no catalogue is wired, so the offline sim never reads its whole queue as
        /// bootstrap; a catalogue that cannot classify a hull answers "not suitable" for every one of
        /// them, which is indistinguishable from owning none.</para></summary>
        private static HashSet<string> BootstrapRoles(WorldModel world, string factionTag)
        {
            var roles = new HashSet<string>(StringComparer.Ordinal);
            if (world.PrefabCatalogue == null) return roles;
            for (int i = 0; i < ShipyardManager.PlayerQueueRoles.Length; i++)
                roles.Add(ShipyardManager.PlayerQueueRoles[i]);

            foreach (SpatialNode n in world.Graph.OwnedBy(factionTag))
            {
                if (roles.Count == 0) break;
                var owned = new List<string>();
                foreach (string role in roles)
                    if (ShipPool.HasSuitableForRole(n, role, world.PrefabCatalogue)) owned.Add(role);
                for (int i = 0; i < owned.Count; i++) roles.Remove(owned[i]);
            }
            return roles;
        }

        /// <summary>Whether <paramref name="e"/> builds for a role the faction owns no hull for. An
        /// entry carrying a role asks about that role; one named by hull asks about every role its
        /// hull fits (<see cref="ShipyardManager.EntryAnswersRole"/>), since the first hull of any of
        /// them clears the state bootstrap exists for.</summary>
        private static bool IsBootstrap(BuildQueueEntry e, HashSet<string> bootstrapKeys, IPrefabCatalogue cat)
        {
            if (!ShipyardManager.IsNamedByHull(e)) return bootstrapKeys.Contains(e.Role);
            foreach (string role in bootstrapKeys)
                if (ShipyardManager.EntryAnswersRole(e, role, cat)) return true;
            return false;
        }

        /// <summary>The share an entry competes in: its fleet role, the one pseudo-role its purpose
        /// puts it in, or -- for a build named by hull -- its design. A design is what a player or a
        /// doctrine line asked for, so each design takes its turn the way each role does, and no
        /// <c>Ship:{role}</c> demand row weights it: what a player builds is the player's share to
        /// set, through the lines' own concurrency.</summary>
        private static string KeyOf(BuildQueueEntry e, IPrefabCatalogue cat)
        {
            if (ShipyardManager.IsStationRefit(e)) return ShipyardManager.StationRefitRole;
            if (e.DestinedForSale) return StoreKey;
            if (ShipyardManager.IsNamedByHull(e))
                return DesignKeyPrefix + (PrefabVersions.FamilyOf(cat, e.PrefabId) ?? e.PrefabId ?? string.Empty);
            return e.Role;
        }
    }
}
