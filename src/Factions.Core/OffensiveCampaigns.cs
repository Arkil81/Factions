using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Every offensive campaign objective a faction carries — a <b>raid campaign</b>
    /// (<see cref="CampaignObjectiveKind.Terrorise"/>: hit a region the faction will not hold) or a
    /// <b>conquest campaign</b> (<see cref="CampaignObjectiveKind.SeizeRegion"/>: take a region, or
    /// one named node) — from the moment the brain nominates it to the moment it closes.
    ///
    /// <para><b>The split this class sits on.</b> The brain answers whether, where and why: it
    /// nominates a region with a <see cref="CampaignCause"/> and, every think, writes the ranked
    /// <see cref="CampaignObjective.Candidates"/> the objective is worth attacking through.
    /// <c>FleetManager</c> answers when, which of them and with what — tempo, purse, readiness and
    /// the dispatch itself. See <c>docs/campaigns.md</c>.</para>
    ///
    /// <para><b>One close for all of them.</b> <see cref="Maintain"/> is the single path that scores
    /// and removes an offensive focus. The antagonist's campaign keeps everything that is its own —
    /// escalation, revenge, the standing DPS it carries between campaigns and its
    /// <c>TERROR_CAMPAIGN CLOSED</c> line — as a branch inside that path rather than as a second
    /// one, so a rule about how a campaign ends cannot drift between the two.
    /// <see cref="TerrorCampaigns.Maintain"/> forwards here.</para>
    ///
    /// <para><b>Offensive foci do not count against the defensive capacity.</b>
    /// <see cref="FactionTuning.CampaignFocusCapacity"/> bounds how many places a faction defends;
    /// an offence is not a defence, and letting one crowd out the other would mean a faction that
    /// decides to attack stops being able to decide to hold. The terror campaign shipped under that
    /// rule and every offensive kind now follows it.</para>
    /// </summary>
    public static class OffensiveCampaigns
    {
        /// <summary>Focus id prefix for a raid campaign that is not the antagonist's (which keeps
        /// <see cref="TerrorCampaigns.FocusIdPrefix"/>, so its foci stay greppable as terror).</summary>
        public const string RaidFocusIdPrefix = "raid:";

        /// <summary>Focus id prefix for a conquest campaign.</summary>
        public const string ConquestFocusIdPrefix = "conquest:";

        /// <summary>Whether this kind is an offence — something dispatched <em>at</em> a place the
        /// faction does not hold, rather than a defence of one it does.</summary>
        public static bool IsOffensive(CampaignObjectiveKind kind)
        {
            return kind == CampaignObjectiveKind.Terrorise || kind == CampaignObjectiveKind.SeizeRegion;
        }

        /// <summary>The word a log line and a doc page call this kind: <c>raid</c> or
        /// <c>conquest</c>. The enum values keep their names because persistence stores them.</summary>
        public static string KindWord(CampaignObjectiveKind kind)
        {
            if (kind == CampaignObjectiveKind.Terrorise) return "raid";
            if (kind == CampaignObjectiveKind.SeizeRegion) return "conquest";
            return "defend";
        }

        /// <summary>One offensive objective and the focus carrying it.</summary>
        public struct Entry
        {
            public CampaignFocus Focus;
            public CampaignObjective Objective;
        }

        /// <summary>Every active offensive objective, in focus order. Allocates only when the faction
        /// actually has one.</summary>
        public static List<Entry> Active(FactionState s)
        {
            var result = new List<Entry>();
            if (s == null) return result;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null) continue;
                foreach (CampaignObjective o in f.Objectives)
                    if (o != null && IsOffensive(o.Kind))
                        result.Add(new Entry { Focus = f, Objective = o });
            }
            return result;
        }

        /// <summary>The offensive objective that dispatched <paramref name="fleetId"/>, or null if
        /// none did — the fleet belongs to another channel, or the objective that sent it has since
        /// closed or dispatched again.
        ///
        /// <para>Matched on the stamp <c>FleetManager</c> writes at dispatch rather than on the
        /// fleet's target node: several objectives can rank the same node, and crediting a loss to
        /// all of them would report a fight that only one of them paid for.</para></summary>
        public static CampaignObjective FindByDispatch(FactionState s, string fleetId)
        {
            if (s == null || string.IsNullOrEmpty(fleetId)) return null;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null) continue;
                foreach (CampaignObjective o in f.Objectives)
                    if (o != null && IsOffensive(o.Kind)
                        && string.Equals(o.LastDispatchFleetId, fleetId, StringComparison.Ordinal))
                        return o;
            }
            return null;
        }

        /// <summary>Whether attacking <paramref name="node"/> advances <paramref name="objective"/>,
        /// whichever offensive kind it is.</summary>
        public static bool Advances(CampaignObjective objective, SpatialNode node, string factionTag)
        {
            if (objective == null) return false;
            if (objective.Kind == CampaignObjectiveKind.Terrorise)
                return TerrorCampaigns.Advances(objective, node, factionTag);
            return SeizeObjectives.Advances(objective, node, factionTag);
        }

        /// <summary>The focus id an objective of this shape takes. Stable, so a second nomination of
        /// the same region resolves to the same focus rather than stacking one beside it.</summary>
        public static string FocusId(CampaignObjectiveKind kind, CampaignCause cause, NodeRegion region, string targetNodeId)
        {
            if (kind == CampaignObjectiveKind.Terrorise && cause == CampaignCause.Terror)
                return TerrorCampaigns.FocusIdPrefix + region.ToKey();
            string prefix = kind == CampaignObjectiveKind.SeizeRegion ? ConquestFocusIdPrefix : RaidFocusIdPrefix;
            return prefix + (string.IsNullOrEmpty(targetNodeId) ? region.ToKey() : "node:" + targetNodeId);
        }

        /// <summary>Whether an active objective of <paramref name="kind"/> already covers this region
        /// (or this node). The dedup the nominators apply, so a standing campaign is reinforced by
        /// the brain's ranking rather than re-nominated every think.</summary>
        public static bool Covers(FactionState s, CampaignObjectiveKind kind, NodeRegion region, string targetNodeId)
        {
            if (s == null) return false;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null) continue;
                foreach (CampaignObjective o in f.Objectives)
                {
                    if (o == null || o.Kind != kind) continue;
                    if (!string.IsNullOrEmpty(targetNodeId) || !string.IsNullOrEmpty(o.TargetNodeId))
                    {
                        if (string.Equals(o.TargetNodeId, targetNodeId, StringComparison.Ordinal)) return true;
                        continue;
                    }
                    if (NodeRegion.FromObjective(o).Equals(region)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Start a raid or conquest campaign: a focus centred on the staging node — its only region
        /// member, so the defensive readers that bias Reinforce and Patrol toward region members see
        /// nothing foreign — carrying one objective with its cause, its DPS budget and its expiry.
        /// Refuses a duplicate (<see cref="Covers"/>) and a nameless target.
        /// <para>The budget is <see cref="TerrorCampaigns.Budget"/> over a census of the target
        /// region's defenders: what stands there is what the fleet has to beat, whoever is asking.</para>
        /// </summary>
        public static bool TryStart(FactionState s, FactionTuning t, DateTime nowUtc, string stagingNodeId,
            NodeRegion region, string targetNodeId, CampaignObjectiveKind kind, CampaignCause cause,
            IWorldView world, IPrefabCatalogue catalogue, out CampaignObjective started)
        {
            started = null;
            if (s == null || t == null || string.IsNullOrEmpty(stagingNodeId)) return false;
            if (!IsOffensive(kind)) return false;
            bool nodeScoped = !string.IsNullOrEmpty(targetNodeId);
            if (region.IsNone && !nodeScoped) return false;
            if (Covers(s, kind, region, targetNodeId)) return false;

            long census = TerrorCampaigns.RegionCensusDps(world, catalogue, region, s.Tag, t);
            var objective = new CampaignObjective
            {
                Kind = kind,
                Cause = cause,
                TargetNodeId = nodeScoped ? targetNodeId : null,
                WantedCombatDps = TerrorCampaigns.Budget(s, t, census),
            };
            if (!region.IsNone) region.ApplyTo(objective);

            var focus = new CampaignFocus
            {
                Id = FocusId(kind, cause, region, targetNodeId),
                CenterNodeId = stagingNodeId,
                Priority = s.Drives[Drive.Rivalry],
                SetAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc + t.OffensiveCampaignDuration,
            };
            focus.RegionMembers.Add(stagingNodeId);
            focus.Objectives.Add(objective);
            s.CampaignFoci.Add(focus);
            CampaignFocusLifecycle.RefreshCompatibilityView(s);
            started = objective;
            return true;
        }

        // ---- The player's own directive (docs/campaigns.md -> A player faction) ----------------

        /// <summary>Focus id prefix for a hand-placed player directive. Distinct from the two
        /// nominator prefixes above so a player's campaign is greppable as one, and so a brain
        /// nomination and a directive naming the same place can never resolve to one focus.</summary>
        public const string PlayerFocusIdPrefix = "player:";

        /// <summary>Why a player directive was refused, or <see cref="Ok"/>. A code rather than a
        /// message because the refusal crosses the wire to a client that renders its own words.</summary>
        public enum PlayerResult
        {
            Ok = 0,
            /// <summary>Nothing to act on: no state, no tuning, or no world.</summary>
            NotReady = 1,
            /// <summary>No node of that id in the graph.</summary>
            UnknownNode = 2,
            /// <summary>The faction already owns it. There is nothing to take and nothing to hit.</summary>
            OwnNode = 3,
            /// <summary>Neither the faction nor the player has ever seen it. The map only draws known
            /// nodes, so this is the same answer the map already gives by omission.</summary>
            NotKnown = 4,
            /// <summary>Its owner is a formal ally.</summary>
            PactPartner = 5,
            /// <summary><see cref="FactionTuning.PlayerCampaignCap"/> directives are already standing.</summary>
            CapReached = 6,
            /// <summary>A campaign of this kind already covers that node or region.</summary>
            Duplicate = 7,
            /// <summary>Region scope was asked for and the node stands in no region a campaign can
            /// name — open deep space, or (for a conquest) a feature, whose membership no node
            /// records and which <see cref="SeizeObjectives.Advances"/> can therefore never
            /// advance.</summary>
            NoRegion = 8,
            /// <summary>The faction owns nothing to stage from, so no fleet could ever leave.</summary>
            NoStaging = 9,
            /// <summary>Not a raid and not a conquest.</summary>
            BadKind = 10,
            /// <summary>Cancel only: no standing player directive of that focus id.</summary>
            NotFound = 11,
            /// <summary>Defend only: the node is not the faction's own. A defensive focus is a
            /// picket around ground the faction holds; there is nothing of theirs to patrol around
            /// someone else's station (<see cref="CampaignFocusLifecycle.TryStartPlayer"/>).</summary>
            NotOwnNode = 12
        }

        /// <summary>How many hand-placed directives of any kind the faction is holding — the number
        /// <see cref="FactionTuning.PlayerCampaignCap"/> bounds. Counts foci, not objectives: a
        /// directive is one focus carrying one objective, offensive or defensive, so a defensive
        /// focus takes a slot exactly as a raid does. "How many places can this faction be
        /// concentrating on" does not depend on which way it is facing.</summary>
        public static int PlayerDirectiveCount(FactionState s)
        {
            int n = 0;
            if (s == null) return 0;
            foreach (CampaignFocus f in s.CampaignFoci)
                if (f != null && f.PlayerIssued) n++;
            return n;
        }

        /// <summary>Every standing player-issued offensive objective, in focus order.</summary>
        public static List<Entry> ActivePlayer(FactionState s)
        {
            var result = new List<Entry>();
            if (s == null) return result;
            foreach (CampaignFocus f in s.CampaignFoci)
            {
                if (f == null || !f.PlayerIssued) continue;
                foreach (CampaignObjective o in f.Objectives)
                    if (o != null && IsOffensive(o.Kind))
                        result.Add(new Entry { Focus = f, Objective = o });
            }
            return result;
        }

        /// <summary>Whether <paramref name="n"/> may be a campaign target for <paramref name="s"/>.
        ///
        /// <para>A brain-nominated campaign asks <see cref="TerrorCampaigns.IsRaidable"/>, which is
        /// the full diplomatic gate: standing, truce, open peace offer, pact. A <b>player</b>
        /// directive asks only the three things that are not the player's to decide — it is not our
        /// own ground, it is not the graveyard faction's, and its owner is not a formal ally. The
        /// rest of that gate exists to stop a brain starting a war nobody chose; a player choosing
        /// exactly that is the feature, and applying it would leave a directive placed on a
        /// neutral-standing rival with an empty candidate list and no explanation.</para></summary>
        public static bool IsCampaignTarget(FactionState s, SpatialNode n, FactionTuning t,
            DateTime nowUtc, bool playerIssued)
        {
            if (s == null || n == null) return false;
            if (!playerIssued) return TerrorCampaigns.IsRaidable(s, n, t, nowUtc);
            string owner = n.OwnerFactionTag;
            if (owner == s.Tag) return false;
            if (owner == NeutralFaction.Tag) return false;
            return !Diplomacy.InPactWith(s, owner);
        }

        /// <summary>
        /// Place one hand-issued campaign objective for a player faction: a raid
        /// (<see cref="CampaignObjectiveKind.Terrorise"/>) or a conquest
        /// (<see cref="CampaignObjectiveKind.SeizeRegion"/>) on <paramref name="targetNodeId"/>, or
        /// on the region that node stands in when <paramref name="regionScope"/> is set.
        ///
        /// <para><b>The same record a brain nomination produces</b>, so every downstream reader — the
        /// candidate projection, <c>FleetManager</c>'s offensive block, <see cref="Maintain"/>'s
        /// close, persistence — is the one that already exists. What differs is
        /// <see cref="CampaignFocus.PlayerIssued"/>, which is what lets the manager run this
        /// objective for a faction whose brain is switched off, and
        /// <see cref="CampaignCause.Directive"/>.</para>
        ///
        /// <para><b><see cref="CampaignObjective.WantedCombatDps"/> is deliberately 0.</b> That field
        /// is the shipyard's build target (<c>ShipyardManager.PostProactiveShipDemands</c> sizes
        /// proactive Raid demands against it), and a player is not asking the yard to size anything —
        /// they are pointing the hulls their faction already has at a place. A census-sized budget
        /// here would put a whole region's defender DPS onto a player faction's build queue as a
        /// side effect of naming a target. It is not a dispatch gate either way (slice B, v0.202.0),
        /// so a zero costs the directive nothing.</para>
        ///
        /// <para>Staging is the faction's own node nearest the target — the focus centre, whose loss
        /// is one of the three things <see cref="Maintain"/> closes a campaign on. The dedup is the
        /// nominators' own <see cref="Covers"/>, so a directive cannot stack a second campaign of the
        /// same kind onto a place one already covers, whoever placed the first.</para>
        ///
        /// <para><paramref name="knowledgeSources"/> is the viewer's knowledge, exactly as the map
        /// builds it: the player's own scanner store plus their faction's. The map draws only known
        /// nodes, and this mirrors that rather than inventing a second rule. On success the target is
        /// written into the <b>faction's</b> store as well — the player has just told their faction
        /// where to go, and without it a region directive resolved from one player's scanner would
        /// rank an empty list.</para>
        /// </summary>
        public static PlayerResult TryStartPlayer(FactionState s, FactionTuning t, DateTime nowUtc,
            IWorldView world, string targetNodeId, CampaignObjectiveKind kind, bool regionScope,
            IList<FactionKnowledge> knowledgeSources, out CampaignFocus placed)
        {
            placed = null;
            if (s == null || t == null || world == null || world.Graph == null) return PlayerResult.NotReady;
            if (!IsOffensive(kind)) return PlayerResult.BadKind;
            if (string.IsNullOrEmpty(targetNodeId)) return PlayerResult.UnknownNode;

            SpatialNode target;
            if (!world.Graph.Nodes.TryGetValue(targetNodeId, out target) || target == null)
                return PlayerResult.UnknownNode;
            if (target.OwnerFactionTag == s.Tag) return PlayerResult.OwnNode;
            if (Diplomacy.InPactWith(s, target.OwnerFactionTag)) return PlayerResult.PactPartner;
            if (!KnowledgeView.IsDiscoveredBy(world.Graph, s.Tag, knowledgeSources, target.Id,
                                              nowUtc, t.DiscoveryHorizon, t.BirthrightRangeKm))
                return PlayerResult.NotKnown;

            NodeRegion region = NodeRegion.None;
            if (regionScope)
            {
                region = NodeRegion.Of(target);
                // A conquest names its region through PlacementRegionKind, and no node records
                // feature membership -- SeizeObjectives.Advances answers false for one, so such an
                // objective could never be advanced by anything.
                if (region.IsNone || (kind == CampaignObjectiveKind.SeizeRegion && region.IsFeature))
                    return PlayerResult.NoRegion;
            }
            string nodeScopeId = regionScope ? null : target.Id;

            if (PlayerDirectiveCount(s) >= Math.Max(0, t.PlayerCampaignCap)) return PlayerResult.CapReached;
            if (Covers(s, kind, region, nodeScopeId)) return PlayerResult.Duplicate;

            string staging = NearestOwnedNodeId(s, world, target);
            if (staging == null) return PlayerResult.NoStaging;

            var objective = new CampaignObjective
            {
                Kind = kind,
                Cause = CampaignCause.Directive,
                TargetNodeId = nodeScopeId,
                WantedCombatDps = 0,
            };
            if (!region.IsNone) region.ApplyTo(objective);

            var focus = new CampaignFocus
            {
                Id = PlayerFocusId(kind, region, nodeScopeId),
                CenterNodeId = staging,
                Priority = 1.0,
                SetAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc + t.PlayerCampaignDuration,
                PlayerIssued = true,
            };
            focus.RegionMembers.Add(staging);
            focus.Objectives.Add(objective);
            s.CampaignFoci.Add(focus);
            if (s.Knowledge != null) s.Knowledge.Touch(target, nowUtc);
            CampaignFocusLifecycle.RefreshCompatibilityView(s);
            placed = focus;
            return PlayerResult.Ok;
        }

        /// <summary>The focus id a player directive takes. Stable, so the same order placed twice
        /// resolves to the same focus and is refused as a duplicate rather than stacked.</summary>
        public static string PlayerFocusId(CampaignObjectiveKind kind, NodeRegion region, string targetNodeId)
        {
            string word = kind == CampaignObjectiveKind.SeizeRegion ? "conquest:"
                        : kind == CampaignObjectiveKind.DefendRegion ? "defend:" : "raid:";
            return PlayerFocusIdPrefix + word
                   + (string.IsNullOrEmpty(targetNodeId) ? region.ToKey() : "node:" + targetNodeId);
        }

        /// <summary>Cancel one standing player directive by focus id. Only a player-issued focus can
        /// be cancelled this way: a brain's campaign is not the player's to call off, and letting one
        /// id reach both would make a client able to unpick an NPC's war.</summary>
        public static PlayerResult CancelPlayer(FactionState s, string focusId, out CampaignFocus removed)
        {
            removed = null;
            if (s == null) return PlayerResult.NotReady;
            if (string.IsNullOrEmpty(focusId)) return PlayerResult.NotFound;
            for (int i = 0; i < s.CampaignFoci.Count; i++)
            {
                CampaignFocus f = s.CampaignFoci[i];
                if (f == null || !f.PlayerIssued) continue;
                if (!string.Equals(f.Id, focusId, StringComparison.Ordinal)) continue;
                removed = f;
                s.CampaignFoci.RemoveAt(i);
                CampaignFocusLifecycle.RefreshCompatibilityView(s);
                return PlayerResult.Ok;
            }
            return PlayerResult.NotFound;
        }

        /// <summary>The faction's own node nearest <paramref name="target"/>, or null when it owns
        /// none. Ties break on the id so the same world always stages from the same place.</summary>
        private static string NearestOwnedNodeId(FactionState s, IWorldView world, SpatialNode target)
        {
            string best = null;
            double bestDistance = double.MaxValue;
            foreach (SpatialNode mine in world.Graph.Nodes.Values)
            {
                if (mine == null || mine.OwnerFactionTag != s.Tag) continue;
                double d = mine.Position.DistanceTo(target.Position);
                if (d > bestDistance) continue;
                if (d == bestDistance && string.CompareOrdinal(mine.Id, best) >= 0) continue;
                bestDistance = d;
                best = mine.Id;
            }
            return best;
        }

        /// <summary>The scored close of one offensive campaign, for the log line and for tests.</summary>
        public struct Verdict
        {
            public string RegionKey;
            public int Raids;
            public int Won;
            public int HullsLost;
            public long MetDps;
            /// <summary>"repelled", "succeeded", "expired" (a conquest that ran out of time with
            /// fights to its name but nothing taken), or "idle" (no fight was had under it).</summary>
            public string Outcome;
        }

        /// <summary>
        /// The one close path for every offensive campaign, run every manager tick before the generic
        /// focus expiry so no offensive focus is ever dropped unscored.
        ///
        /// <para>Three things end a campaign: its expiry passing, its staging node no longer being
        /// ours, or — for a conquest only — the ground being taken, which is the objective being
        /// <em>met</em> rather than run out. Each close is scored from the faction's own
        /// <see cref="FactionState.Fights"/> since the focus was set.</para>
        ///
        /// <para>The antagonist's branch is unchanged: idle escalation decay, repelled → escalation
        /// and revenge, succeeded → de-escalation, the strongest opposition met becoming
        /// <see cref="FactionState.TerrorMetDps"/>, and one <c>TERROR_CAMPAIGN CLOSED</c> line.
        /// Every other offensive close writes <c>CAMPAIGN_OBJECTIVE CLOSED</c>.</para>
        /// </summary>
        public static int Maintain(FactionState s, FactionTuning t, DateTime nowUtc, Func<string, bool> ownsCenter,
            IWorldView world, IPrefabCatalogue catalogue, IList<string> log)
        {
            if (s == null || t == null) return 0;

            if (s.TerrorEscalation > 0 && t.TerrorEscalationDecay > TimeSpan.Zero
                && s.TerrorEscalationUtc != DateTime.MinValue && nowUtc - s.TerrorEscalationUtc >= t.TerrorEscalationDecay)
            {
                s.TerrorEscalation--;
                s.TerrorEscalationUtc = nowUtc;
                s.TerrorStandingDps = s.TerrorEscalation > 0 ? TerrorCampaigns.Budget(s, t, StandingCensus(s, t, world, catalogue)) : 0;
                if (log != null)
                    log.Add("TERROR_ESCALATION DECAYED " + s.Tag + " escalation=" + s.TerrorEscalation
                            + " standing_dps=" + s.TerrorStandingDps);
            }

            int closed = 0;
            for (int i = s.CampaignFoci.Count - 1; i >= 0; i--)
            {
                CampaignFocus focus = s.CampaignFoci[i];
                if (focus == null) continue;
                CampaignObjective objective = null;
                foreach (CampaignObjective o in focus.Objectives)
                    if (o != null && IsOffensive(o.Kind)) { objective = o; break; }
                if (objective == null) continue;

                bool expired = focus.ExpiresAtUtc != DateTime.MinValue && focus.ExpiresAtUtc <= nowUtc;
                bool lostStaging = !expired && ownsCenter != null && !ownsCenter(focus.CenterNodeId);
                bool taken = !expired && !lostStaging
                             && objective.Kind == CampaignObjectiveKind.SeizeRegion
                             && GroundTaken(s, objective, world);
                if (!expired && !lostStaging && !taken) continue;

                bool isTerror = objective.Kind == CampaignObjectiveKind.Terrorise
                                && objective.Cause == CampaignCause.Terror;
                Verdict v = Score(s, t, focus, objective, nowUtc, world, catalogue, isTerror, taken);
                s.CampaignFoci.RemoveAt(i);
                closed++;
                if (log == null) continue;

                if (isTerror)
                    log.Add("TERROR_CAMPAIGN CLOSED " + s.Tag + " " + NodeRegion.FromObjective(objective).Describe()
                            + " raids=" + v.Raids + " won=" + v.Won + " lost=" + v.HullsLost + " met_dps=" + v.MetDps
                            + " verdict=" + v.Outcome + (lostStaging ? " staging_lost" : "")
                            + " escalation=" + s.TerrorEscalation + " standing_dps=" + s.TerrorStandingDps);
                else
                    log.Add("CAMPAIGN_OBJECTIVE CLOSED " + s.Tag
                            + " kind=" + KindWord(objective.Kind)
                            + " cause=" + objective.Cause
                            + " region=" + (NodeRegion.FromObjective(objective).ToKey() ?? "-")
                            + " node=" + (objective.TargetNodeId ?? "-")
                            + " raids=" + v.Raids + " won=" + v.Won + " lost=" + v.HullsLost
                            + " met_dps=" + v.MetDps + " verdict=" + v.Outcome
                            + (lostStaging ? " staging_lost" : ""));
            }
            if (closed > 0) CampaignFocusLifecycle.RefreshCompatibilityView(s);
            return closed;
        }

        /// <summary>Whether a conquest objective's ground is ours: the named node, or — for a region
        /// objective — at least one node of the region held and no node of it left in a rival's
        /// hands. A neutral node is not a conquest target, so it does not keep the campaign open.</summary>
        private static bool GroundTaken(FactionState s, CampaignObjective objective, IWorldView world)
        {
            if (world == null) return false;
            if (!string.IsNullOrEmpty(objective.TargetNodeId))
            {
                SpatialNode target;
                return world.Graph.Nodes.TryGetValue(objective.TargetNodeId, out target)
                    && target.OwnerFactionTag == s.Tag;
            }
            NodeRegion region = NodeRegion.FromObjective(objective);
            if (region.IsNone) return false;
            bool holdsAny = false;
            foreach (SpatialNode n in world.Graph.Nodes.Values)
            {
                if (n == null || !region.Contains(n)) continue;
                string owner = n.OwnerFactionTag;
                if (owner == s.Tag) { holdsAny = true; continue; }
                if (owner != null && owner != NeutralFaction.Tag) return false;
            }
            return holdsAny;
        }

        private static Verdict Score(FactionState s, FactionTuning t, CampaignFocus focus, CampaignObjective objective,
            DateTime nowUtc, IWorldView world, IPrefabCatalogue catalogue, bool isTerror, bool taken)
        {
            NodeRegion region = NodeRegion.FromObjective(objective);
            var v = new Verdict { RegionKey = region.ToKey(), Outcome = "idle" };
            List<FightRecord> fights = string.IsNullOrEmpty(objective.TargetNodeId)
                ? FightLedger.AttacksOn(s, v.RegionKey, focus.SetAtUtc)
                : AttacksOnNode(s, objective.TargetNodeId, focus.SetAtUtc);
            foreach (FightRecord f in fights)
            {
                v.Raids++;
                if (f.AttackerWon) v.Won++;
                v.HullsLost += f.AttackerHullsLost;
                if (f.DefenderDps > v.MetDps) v.MetDps = f.DefenderDps;
            }

            // A conquest that took the ground succeeded whatever the raid tally says: the objective
            // is the territory, not the exchange rate.
            if (taken) { v.Outcome = "succeeded"; return v; }
            // Nothing was fought under it, so nothing was learned.
            if (v.Raids == 0) return v;

            // Repelled when more of its attacks failed than landed — an attack fails when it was
            // destroyed before landing or came out at or under the return threshold, which is the
            // same event that loses its hulls.
            bool repelled = v.Raids - v.Won > v.Won;
            if (repelled) v.Outcome = "repelled";
            else if (objective.Kind == CampaignObjectiveKind.SeizeRegion) v.Outcome = "expired";
            else v.Outcome = "succeeded";
            if (!isTerror) return v;

            int cap = Math.Max(0, t.TerrorEscalationCap);
            int before = s.TerrorEscalation;
            if (repelled)
            {
                s.TerrorEscalation = Math.Min(cap, s.TerrorEscalation + 1);
                s.TerrorRevenge = true;
            }
            else
            {
                s.TerrorEscalation = Math.Max(0, s.TerrorEscalation - 1);
                s.TerrorRevenge = false;
            }
            if (s.TerrorEscalation != before) s.TerrorEscalationUtc = nowUtc;
            s.TerrorMetDps = v.MetDps;
            s.TerrorStandingDps = s.TerrorEscalation > 0
                ? TerrorCampaigns.Budget(s, t, TerrorCampaigns.RegionCensusDps(world, catalogue, region, s.Tag, t)) : 0;
            return v;
        }

        /// <summary>The attacks <paramref name="s"/> pressed against one node since
        /// <paramref name="sinceUtc"/> — what a node-scoped objective's close is scored from, since
        /// it has no region key to look itself up by.</summary>
        private static List<FightRecord> AttacksOnNode(FactionState s, string nodeId, DateTime sinceUtc)
        {
            var result = new List<FightRecord>();
            if (s == null || string.IsNullOrEmpty(nodeId)) return result;
            foreach (FightRecord f in s.Fights)
                if (f.Attacking && f.Utc >= sinceUtc && string.Equals(f.NodeId, nodeId, StringComparison.Ordinal))
                    result.Add(f);
            return result;
        }

        /// <summary>The census behind a standing requirement re-sized outside a close: the last
        /// region targeted, which is where the next campaign goes if it was repelled.</summary>
        private static long StandingCensus(FactionState s, FactionTuning t, IWorldView world, IPrefabCatalogue catalogue)
        {
            NodeRegion region;
            if (s.LastTerrorRegionKey == null || !NodeRegion.TryFromKey(s.LastTerrorRegionKey, out region)) return 0;
            return TerrorCampaigns.RegionCensusDps(world, catalogue, region, s.Tag, t);
        }
    }
}
