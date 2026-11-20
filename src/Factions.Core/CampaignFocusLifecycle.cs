using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Pure state transitions for the defensive multi-focus foundation.</summary>
    public static class CampaignFocusLifecycle
    {
        public static bool TryStart(FactionState state, FactionTuning tuning, long fleetCombatPower,
            DateTime nowUtc, string centerNodeId, ISet<string> regionMembers)
        {
            if (state == null || tuning == null || string.IsNullOrEmpty(centerNodeId)) return false;
            for (int i = state.CampaignFoci.Count - 1; i >= 0; i--)
            {
                CampaignFocus existing = state.CampaignFoci[i];
                if (existing == null || (existing.ExpiresAtUtc != DateTime.MinValue && existing.ExpiresAtUtc <= nowUtc))
                    state.CampaignFoci.RemoveAt(i);
            }
            RefreshCompatibilityView(state);
            // Capacity bounds how many places a faction defends. An offensive focus is not a
            // defence and does not take a slot (OffensiveCampaigns), or a faction that attacks a lot
            // could never start defending.
            if (DefensiveFocusCount(state) >= tuning.CampaignFocusCapacity(fleetCombatPower)) return false;

            var candidateMembers = new HashSet<string>();
            if (regionMembers != null)
                foreach (string nodeId in regionMembers)
                    if (!string.IsNullOrEmpty(nodeId)) candidateMembers.Add(nodeId);
            candidateMembers.Add(centerNodeId);

            var existingMembers = new HashSet<string>();
            foreach (CampaignFocus focus in state.CampaignFoci)
            {
                if (focus == null || !HasDefendRegionObjective(focus)) continue;
                if (focus.CenterNodeId == centerNodeId) return false;
                foreach (string nodeId in focus.RegionMembers) existingMembers.Add(nodeId);
            }

            bool addsCoverage = false;
            foreach (string nodeId in candidateMembers)
                if (!existingMembers.Contains(nodeId)) { addsCoverage = true; break; }
            if (!addsCoverage) return false;

            var focusToAdd = new CampaignFocus
            {
                Id = centerNodeId,
                CenterNodeId = centerNodeId,
                Priority = state.Drives[Drive.Security],
                SetAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc + tuning.CampaignCooldown
            };
            foreach (string nodeId in candidateMembers) focusToAdd.RegionMembers.Add(nodeId);
            focusToAdd.Objectives.Add(new CampaignObjective
            {
                Kind = CampaignObjectiveKind.DefendRegion,
                TargetNodeId = centerNodeId
            });
            state.CampaignFoci.Add(focusToAdd);
            RefreshCompatibilityView(state);
            return true;
        }

        /// <summary>
        /// Place one hand-issued <b>defensive</b> focus for a player faction, centred on
        /// <paramref name="centerNodeId"/> — a node the faction owns — over
        /// <paramref name="regionMembers"/>, the same neighbourhood a brain nomination would carry
        /// (<c>WorldModel.ComputeCampaignRegionMembers</c>, computed by the caller because
        /// <see cref="IWorldView"/> does not expose it). FAC-309 slice 5, and the defensive twin of
        /// <see cref="OffensiveCampaigns.TryStartPlayer"/>: the focus is the opt-in, and
        /// <c>FleetManager</c>'s Patrol block already reads <see cref="CampaignFocus.PlayerIssued"/>
        /// as its provenance gate for a faction whose brain is off, so nothing downstream changes.
        ///
        /// <para><b>The same record <see cref="TryStart"/> makes</b>, differing only in provenance
        /// (<see cref="CampaignFocus.PlayerIssued"/>), cause (<see cref="CampaignCause.Directive"/>),
        /// priority (1.0 — a player's order is not scored against their drives) and expiry
        /// (<see cref="FactionTuning.PlayerCampaignDuration"/>, not the brain's
        /// <see cref="FactionTuning.CampaignCooldown"/>). It is not counted against
        /// <see cref="FactionTuning.CampaignFocusCapacity"/> — that bounds a brain that restates its
        /// foci every think — but against <see cref="FactionTuning.PlayerCampaignCap"/>, shared with
        /// the offensive directives.</para>
        ///
        /// <para><b>Refusals, in order:</b> no such node; not the faction's own
        /// (<see cref="OffensiveCampaigns.PlayerResult.NotOwnNode"/> — a picket around someone
        /// else's station is not a defence); the cap; a duplicate — a standing player-issued
        /// defensive focus with the same centre, or one whose region already holds every member this
        /// one would add, since a second circuit over the same nodes is the wasted hull the per-node
        /// cooldown exists to refuse. Knowledge is not checked: the faction owns the node.</para>
        /// </summary>
        public static OffensiveCampaigns.PlayerResult TryStartPlayer(FactionState s, FactionTuning t, DateTime nowUtc,
            IWorldView world, string centerNodeId, ISet<string> regionMembers, out CampaignFocus placed)
        {
            placed = null;
            if (s == null || t == null || world == null || world.Graph == null) return OffensiveCampaigns.PlayerResult.NotReady;
            if (string.IsNullOrEmpty(centerNodeId)) return OffensiveCampaigns.PlayerResult.UnknownNode;
            SpatialNode center;
            if (!world.Graph.Nodes.TryGetValue(centerNodeId, out center) || center == null)
                return OffensiveCampaigns.PlayerResult.UnknownNode;
            if (center.OwnerFactionTag != s.Tag) return OffensiveCampaigns.PlayerResult.NotOwnNode;
            if (OffensiveCampaigns.PlayerDirectiveCount(s) >= Math.Max(0, t.PlayerCampaignCap))
                return OffensiveCampaigns.PlayerResult.CapReached;

            var members = new HashSet<string>();
            if (regionMembers != null)
                foreach (string nodeId in regionMembers)
                    if (!string.IsNullOrEmpty(nodeId)) members.Add(nodeId);
            members.Add(centerNodeId);

            var covered = new HashSet<string>();
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null || !f.PlayerIssued || !HasDefendRegionObjective(f)) continue;
                if (f.CenterNodeId == centerNodeId) return OffensiveCampaigns.PlayerResult.Duplicate;
                foreach (string nodeId in f.RegionMembers) covered.Add(nodeId);
            }
            bool addsCoverage = false;
            foreach (string nodeId in members)
                if (!covered.Contains(nodeId)) { addsCoverage = true; break; }
            if (!addsCoverage) return OffensiveCampaigns.PlayerResult.Duplicate;

            var focus = new CampaignFocus
            {
                Id = OffensiveCampaigns.PlayerFocusId(CampaignObjectiveKind.DefendRegion, NodeRegion.None, centerNodeId),
                CenterNodeId = centerNodeId,
                Priority = 1.0,
                SetAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc + t.PlayerCampaignDuration,
                PlayerIssued = true,
            };
            foreach (string nodeId in members) focus.RegionMembers.Add(nodeId);
            focus.Objectives.Add(new CampaignObjective
            {
                Kind = CampaignObjectiveKind.DefendRegion,
                Cause = CampaignCause.Directive,
                TargetNodeId = centerNodeId,
            });
            s.CampaignFoci.Add(focus);
            RefreshCompatibilityView(s);
            placed = focus;
            return OffensiveCampaigns.PlayerResult.Ok;
        }

        /// <summary>Gives the standing player-issued defensive focus centred on
        /// <paramref name="centerNodeId"/> a fresh <see cref="FactionTuning.PlayerCampaignDuration"/>
        /// from <paramref name="nowUtc"/>, and returns it; null when none is centred there. A
        /// station's repeat call for protection keeps its focus up this way instead of placing a
        /// second one.</summary>
        public static CampaignFocus RefreshPlayer(FactionState s, FactionTuning t, DateTime nowUtc, string centerNodeId)
        {
            if (s == null || t == null || string.IsNullOrEmpty(centerNodeId)) return null;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null || !f.PlayerIssued || f.CenterNodeId != centerNodeId || !HasDefendRegionObjective(f)) continue;
                DateTime until = nowUtc + t.PlayerCampaignDuration;
                if (until > f.ExpiresAtUtc) f.ExpiresAtUtc = until;
                return f;
            }
            return null;
        }

        /// <summary>
        /// The close for a player-issued defensive focus: gone when it has run out
        /// <see cref="FactionTuning.PlayerCampaignDuration"/> or when the faction no longer owns
        /// its centre. <see cref="Maintain"/> is the brain's and <c>FleetManager</c> runs it only for
        /// a brain-driven faction — it also migrates the legacy scalar focus, which a player faction
        /// has no business acquiring — and <see cref="OffensiveCampaigns.Maintain"/> scores and
        /// closes offensive objectives only. Without this a player's defensive focus would stand for
        /// ever, patrolling a region at <see cref="FactionTuning.PatrolCost"/> a circuit long after
        /// the player who placed it stopped playing.
        ///
        /// <para>Logs <c>CAMPAIGN_OBJECTIVE CLOSED &lt;tag&gt; kind=defend cause=Directive node=&lt;centre&gt;
        /// reason=expired|center_lost</c> — the same family the offensive close uses, so one grep
        /// finds every directive's end.</para>
        /// </summary>
        public static int MaintainPlayerIssued(FactionState state, DateTime nowUtc, Func<string, bool> ownsCenter,
            IList<string> log)
        {
            if (state == null) return 0;
            int removed = 0;
            for (int i = state.CampaignFoci.Count - 1; i >= 0; i--)
            {
                CampaignFocus focus = state.CampaignFoci[i];
                if (focus == null || !focus.PlayerIssued || !HasDefendRegionObjective(focus)) continue;
                bool expired = focus.ExpiresAtUtc != DateTime.MinValue && focus.ExpiresAtUtc <= nowUtc;
                bool lostCenter = !expired && ownsCenter != null && !ownsCenter(focus.CenterNodeId);
                if (!expired && !lostCenter) continue;
                state.CampaignFoci.RemoveAt(i);
                removed++;
                if (log != null)
                    log.Add("CAMPAIGN_OBJECTIVE CLOSED " + state.Tag + " kind=defend cause=Directive"
                            + " node=" + (focus.CenterNodeId ?? "-")
                            + " reason=" + (expired ? "expired" : "center_lost"));
            }
            if (removed > 0) RefreshCompatibilityView(state);
            return removed;
        }

        /// <summary>Whether the focus carries a <see cref="CampaignObjectiveKind.DefendRegion"/>
        /// objective — a defensive focus, as opposed to one standing on a staging node for an
        /// offensive objective.</summary>
        /// <summary>How many of <paramref name="state"/>'s foci defend a region: the count
        /// <see cref="FactionTuning.CampaignFocusCapacity"/> bounds. Offensive foci are not counted.</summary>
        public static int DefensiveFocusCount(FactionState state)
        {
            if (state == null) return 0;
            int n = 0;
            for (int i = 0; i < state.CampaignFoci.Count; i++)
                if (HasDefendRegionObjective(state.CampaignFoci[i])) n++;
            return n;
        }

        public static bool HasDefendRegionObjective(CampaignFocus focus)
        {
            if (focus == null) return false;
            for (int i = 0; i < focus.Objectives.Count; i++)
                if (focus.Objectives[i] != null && focus.Objectives[i].Kind == CampaignObjectiveKind.DefendRegion)
                    return true;
            return false;
        }

        public static int Maintain(FactionState state, DateTime nowUtc, Func<string, bool> ownsCenter,
            TimeSpan legacyLifetime)
        {
            if (state == null) return 0;
            if (state.CampaignFoci.Count == 0 && !string.IsNullOrEmpty(state.CampaignFocusRegion))
            {
                var legacyFocus = new CampaignFocus
                {
                    Id = state.CampaignFocusRegion,
                    CenterNodeId = state.CampaignFocusRegion,
                    Priority = state.CampaignFocusPriority,
                    SetAtUtc = state.CampaignSetAtUtc,
                    ExpiresAtUtc = state.CampaignSetAtUtc == DateTime.MinValue
                        ? DateTime.MinValue : state.CampaignSetAtUtc + legacyLifetime
                };
                foreach (string nodeId in state.CampaignFocusRegionMembers) legacyFocus.RegionMembers.Add(nodeId);
                if (legacyFocus.RegionMembers.Count == 0) legacyFocus.RegionMembers.Add(legacyFocus.CenterNodeId);
                legacyFocus.Objectives.Add(new CampaignObjective
                {
                    Kind = CampaignObjectiveKind.DefendRegion,
                    TargetNodeId = legacyFocus.CenterNodeId
                });
                state.CampaignFoci.Add(legacyFocus);
            }
            int removed = 0;
            for (int i = state.CampaignFoci.Count - 1; i >= 0; i--)
            {
                CampaignFocus focus = state.CampaignFoci[i];
                bool expired = focus == null || (focus.ExpiresAtUtc != DateTime.MinValue && focus.ExpiresAtUtc <= nowUtc);
                bool invalid = !expired && ownsCenter != null && !ownsCenter(focus.CenterNodeId);
                if (!expired && !invalid) continue;
                state.CampaignFoci.RemoveAt(i);
                removed++;
            }
            RefreshCompatibilityView(state);
            return removed;
        }

        public static void RefreshCompatibilityView(FactionState state)
        {
            if (state == null) return;
            state.CampaignFocusRegionMembers.Clear();
            if (state.CampaignFoci.Count == 0)
            {
                state.CampaignFocusRegion = null;
                state.CampaignFocusPriority = 0;
                state.CampaignSetAtUtc = DateTime.MinValue;
                return;
            }

            CampaignFocus primary = null;
            double highestPriority = 0;
            foreach (CampaignFocus focus in state.CampaignFoci)
            {
                if (focus == null) continue;
                if (primary == null) primary = focus;
                if (!string.IsNullOrEmpty(focus.CenterNodeId)) state.CampaignFocusRegionMembers.Add(focus.CenterNodeId);
                if (focus.Priority > highestPriority) highestPriority = focus.Priority;
                foreach (string nodeId in focus.RegionMembers)
                    if (!string.IsNullOrEmpty(nodeId)) state.CampaignFocusRegionMembers.Add(nodeId);
            }
            if (primary == null)
            {
                state.CampaignFocusRegion = null;
                state.CampaignFocusPriority = 0;
                state.CampaignSetAtUtc = DateTime.MinValue;
                return;
            }
            state.CampaignFocusRegion = primary.CenterNodeId;
            state.CampaignFocusPriority = highestPriority;
            state.CampaignSetAtUtc = primary.SetAtUtc;
        }
    }
}
