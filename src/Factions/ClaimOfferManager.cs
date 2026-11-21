using System;
using System.Collections.Generic;
using Factions.Core;
using NLog;
using Sandbox.Game.World;
using Torch.Managers.ChatManager;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Drives the Magnate-tier claim-offer conversation: notices a player faction present at a
    /// claimable node (via <see cref="PlayerPresenceManager"/>'s <see cref="SpatialNode.Presences"/>
    /// output), whispers an offer, captures a plain-chat "yes"/"no" reply, and on acceptance
    /// transfers ownership via <see cref="WorldModel.Claim"/>. A solo player (no SE faction) is
    /// nudged to found one instead of being silently ignored.
    ///
    /// <para>Race/decline/re-offer policy is single-sourced in <see cref="ClaimOfferCoordinator"/>
    /// (Factions.Core, offline-testable); this class is purely the SE-facing adapter — presence to
    /// faction resolution, chat I/O, node ownership mutation.</para>
    ///
    /// <para>Runs on the game thread, on the same slow tick as <see cref="PlayerPresenceManager"/>,
    /// after it — <see cref="Tick"/> reads <see cref="SpatialNode.Presences"/>, which that manager
    /// populates.</para>
    /// </summary>
    internal sealed class ClaimOfferManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);
        private const string WhisperAuthor = "Factions";

        private readonly FactionRuntime _runtime;
        private readonly WorldModel _world;
        private readonly Dictionary<string, ClaimOfferState> _stateByNodeId =
            new Dictionary<string, ClaimOfferState>(StringComparer.Ordinal);

        // nodeId -> faction tag last whispered, so an unanswered offer isn't re-whispered every tick.
        private readonly Dictionary<string, string> _lastWhisperedFactionTag =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // "nodeId@factionTag" -> first tick this faction was seen present at this node. The
        // coordinator needs arrival order, not just current membership, to honor "first come, first
        // asked"; entries are dropped once the faction is no longer present (see Tick's cleanup pass).
        private readonly Dictionary<string, DateTime> _firstSeenUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        // "nodeId@identityId" already nudged this continuous presence, so a solo player isn't
        // re-nudged every ~5s tick while they linger.
        private readonly HashSet<string> _nudgedSoloPlayers = new HashSet<string>(StringComparer.Ordinal);

        // Nodes whose standing offer has been whispered to someone who may answer it. An offer first
        // whispered to a member who may not is whispered again to a leader once one is online.
        private readonly HashSet<string> _offerReachedLeader = new HashSet<string>(StringComparer.Ordinal);

        private ChatManagerServer _chat;

        /// <summary>Set on a Nexus authority: the players the agents report. A member there is offered
        /// the claim as one here is; their yes or no comes back in the agent's report
        /// (<see cref="Answer"/>).</summary>
        public Func<IList<RemotePlayer>> RemotePlayers { get; set; }

        /// <summary>Set on a Nexus authority: a whisper to a player on an agent (steam id, text, whether
        /// it is the offer, whose answer the agent sends back). False when they are on none.</summary>
        public Func<ulong, string, bool, bool> RemoteWhisper { get; set; }

        /// <summary>Who an offer or a whisper goes to: a player connected here, or one on an agent.</summary>
        private struct Recipient
        {
            public MyPlayer Local;
            public ulong Steam;
            public long Identity;
            public bool IsSome { get { return Local != null || Steam != 0UL; } }
        }

        public ClaimOfferManager(FactionRuntime runtime)
        {
            _runtime = runtime;
            _world = runtime.World;
        }

        public void AttachChat(ChatManagerServer chat)
        {
            _chat = chat;   // whispers only; replies reach Answer through OfferReplyRouter
        }

        public void DetachChat()
        {
            _chat = null;
        }

        public void Tick()
        {
            if (_world?.Graph == null || MySession.Static == null) return;

            var presentNow = new HashSet<string>(StringComparer.Ordinal);   // "nodeId@factionTag"
            var soloPresentNow = new HashSet<string>(StringComparer.Ordinal); // "nodeId@identityId"

            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (!NeutralFaction.IsClaimable(node.OwnerFactionTag)) continue;
                if (node.Presences.Count == 0) continue;

                var seenTags = new HashSet<string>(StringComparer.Ordinal);
                foreach (Presence p in node.Presences)
                {
                    if (p.Kind != ActorKind.Player) continue;
                    long identityId;
                    if (!long.TryParse(p.ActorId, out identityId)) continue;

                    string tag = ResolveEligibleFactionTag(identityId);
                    if (tag != null)
                    {
                        if (seenTags.Add(tag))
                        {
                            string key = node.Id + "@" + tag;
                            presentNow.Add(key);
                            if (!_firstSeenUtc.ContainsKey(key)) _firstSeenUtc[key] = DateTime.UtcNow;
                        }
                        continue;
                    }

                    // No SE faction at all (as opposed to a faction that just happens to be
                    // ineligible) — nudge them toward founding one instead of staying silent.
                    if (MySession.Static.Factions.GetPlayerFaction(identityId) != null) continue;
                    string soloKey = node.Id + "@" + identityId;
                    soloPresentNow.Add(soloKey);
                    if (_nudgedSoloPlayers.Add(soloKey)) NudgeSoloPlayer(identityId, node);
                }

                if (seenTags.Count == 0) continue;

                var arrivalOrder = new List<string>(seenTags);
                arrivalOrder.Sort((a, b) => FirstSeen(node.Id, a).CompareTo(FirstSeen(node.Id, b)));

                ClaimOfferState state;
                if (!_stateByNodeId.TryGetValue(node.Id, out state))
                    _stateByNodeId[node.Id] = state = new ClaimOfferState();

                string offeree = ClaimOfferCoordinator.ResolveOfferee(state, arrivalOrder);

                string lastWhispered;
                _lastWhisperedFactionTag.TryGetValue(node.Id, out lastWhispered);
                if (offeree != lastWhispered)
                {
                    _lastWhisperedFactionTag[node.Id] = offeree;
                    _offerReachedLeader.Remove(node.Id);
                    if (offeree != null) WhisperOffer(node, offeree, false);
                }
                else if (offeree != null && !_offerReachedLeader.Contains(node.Id))
                {
                    WhisperOffer(node, offeree, true);
                }
            }

            RemoveStale(_firstSeenUtc, presentNow);
            RemoveStale(_nudgedSoloPlayers, soloPresentNow);
        }

        private DateTime FirstSeen(string nodeId, string factionTag)
        {
            DateTime t;
            _firstSeenUtc.TryGetValue(nodeId + "@" + factionTag, out t);
            return t;
        }

        private static void RemoveStale(Dictionary<string, DateTime> dict, HashSet<string> stillPresent)
        {
            var stale = new List<string>();
            foreach (string key in dict.Keys)
                if (!stillPresent.Contains(key)) stale.Add(key);
            foreach (string key in stale) dict.Remove(key);
        }

        private static void RemoveStale(HashSet<string> set, HashSet<string> stillPresent)
        {
            set.RemoveWhere(key => !stillPresent.Contains(key));
        }

        /// <summary>Null for a solo player or (defensively) a faction with no live human member —
        /// in practice the latter can't happen for an online identityId's own faction.</summary>
        private static string ResolveEligibleFactionTag(long identityId)
        {
            MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (faction == null || FactionsPlugin.IsEffectivelyNpc(faction)) return null;
            return faction.Tag;
        }

        /// <summary>Whispers the offer to an online leader of <paramref name="factionTag"/>, else to an
        /// online member, who is told a leader must answer. <paramref name="leaderOnly"/>: only a leader
        /// will do, for an offer a member has already been told about.</summary>
        private void WhisperOffer(SpatialNode node, string factionTag, bool leaderOnly)
        {
            bool mayCommand;
            Recipient recipient = FindOnlineMember(factionTag, out mayCommand);
            if (!recipient.IsSome || (leaderOnly && !mayCommand)) return;
            string msg = OfferAuthority.ClaimOfferText(SiteDescription(node), node.Id, factionTag, mayCommand);
            bool remote = recipient.Local == null;
            if (!Whisper(recipient, msg, true)) return;
            if (mayCommand) _offerReachedLeader.Add(node.Id);
            Log.Info("Claim offer sent: " + factionTag + " for node " + node.Id
                + (mayCommand ? "" : " to a member who is not a leader") + (remote ? " remote=True" : ""));
        }

        /// <summary>How a site reads in a whisper: what is under it, or that it is only a claim.</summary>
        private string SiteDescription(SpatialNode node)
        {
            string ore = NodeDisplay.DominantOre(node);
            return string.IsNullOrEmpty(ore) ? "site" : ore + " site";
        }

        private void NudgeSoloPlayer(long identityId, SpatialNode node)
        {
            Whisper(RecipientOf(identityId), "This " + SiteDescription(node) + " (" + node.Id + ") is unclaimed, but you'll "
                + "need a faction to claim it. Found one, then come back.", false);
        }

        /// <summary>A player by this server's identity: connected here, else on an agent by Steam id.</summary>
        private static Recipient RecipientOf(long identityId)
        {
            MyPlayer player = MySession.Static.Players.TryGetPlayer(identityId);
            if (player != null) return new Recipient { Local = player };
            return new Recipient { Steam = MySession.Static.Players.TryGetSteamId(identityId) };
        }

        private bool Whisper(Recipient recipient, string message, bool offer)
        {
            if (recipient.Local != null)
            {
                if (_chat == null) return false;
                _chat.SendMessageAsOther(WhisperAuthor, message, Color.CornflowerBlue, recipient.Local.Id.SteamId);
                return true;
            }
            Func<ulong, string, bool, bool> remote = RemoteWhisper;
            return recipient.Steam != 0UL && remote != null && remote(recipient.Steam, message, offer);
        }

        /// <summary>An online member of <paramref name="factionTag"/>, by this server's identity for
        /// their Steam id: the first who may answer for the faction, else the first found. Players
        /// connected here come before players on an agent.</summary>
        private Recipient FindOnlineMember(string factionTag, out bool mayCommand)
        {
            mayCommand = false;
            var candidates = new List<Recipient>();
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                MyFaction faction = MySession.Static.Factions.GetPlayerFaction(player.Identity.IdentityId);
                if (faction != null && string.Equals(faction.Tag, factionTag, StringComparison.Ordinal))
                    candidates.Add(new Recipient { Local = player, Identity = player.Identity.IdentityId });
            }
            Func<IList<RemotePlayer>> remote = RemotePlayers;
            IList<RemotePlayer> others = remote != null ? remote() : null;
            if (others != null)
                for (int i = 0; i < others.Count; i++)
                {
                    RemotePlayer r = others[i];
                    if (r.SteamId == 0UL) continue;
                    long identity = MySession.Static.Players.TryGetIdentityId(r.SteamId);
                    if (identity == 0) identity = r.IdentityId;
                    MyFaction faction = identity != 0 ? MySession.Static.Factions.GetPlayerFaction(identity) : null;
                    if (faction != null && string.Equals(faction.Tag, factionTag, StringComparison.Ordinal))
                        candidates.Add(new Recipient { Steam = r.SteamId, Identity = identity });
                }

            var commands = new List<bool>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
                commands.Add(PlayerAuthority.MayCommand(candidates[i].Identity, factionTag));
            int pick = OfferAuthority.PickRecipient(commands);
            if (pick < 0) return default(Recipient);
            mayCommand = commands[pick];
            return candidates[pick];
        }

        /// <summary>Adds every claim offer open to <paramref name="identityId"/>'s faction, and
        /// whether they may answer it, for <see cref="OfferReplyRouter"/>.</summary>
        public void CollectOpen(long identityId, List<OpenOffer> into)
        {
            if (identityId == 0L || _world == null || _world.Graph == null) return;
            string factionTag = ResolveEligibleFactionTag(identityId);
            if (factionTag == null) return;
            bool mayCommand = PlayerAuthority.MayCommand(identityId, factionTag);
            foreach (KeyValuePair<string, ClaimOfferState> kv in _stateByNodeId)
            {
                if (OfferAuthority.Judge(factionTag, kv.Value.OfferedFactionTag, mayCommand) == OfferReplyVerdict.NotForThem) continue;
                if (!_world.Graph.Nodes.ContainsKey(kv.Key)) continue;
                into.Add(new OpenOffer { NodeId = kv.Key, Conquer = false, MayAnswer = mayCommand });
            }
        }

        /// <summary>A whisper to <paramref name="identityId"/> (this server's identity), here or on the
        /// agent they are on. <paramref name="offer"/>: the agent captures their next yes or no.</summary>
        public void Tell(long identityId, string message, bool offer)
        {
            Whisper(RecipientOf(identityId), message, offer);
        }

        /// <summary>A yes or no from <paramref name="identityId"/> (this server's identity), typed here
        /// or relayed from the agent they are on, to the offer for <paramref name="nodeId"/>. False when
        /// no offer for that node stands for their faction.</summary>
        public bool Answer(long identityId, bool isYes, string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return false;
            if (identityId == 0L || _world == null || _world.Graph == null) return false;
            string factionTag = ResolveEligibleFactionTag(identityId);
            if (factionTag == null) return false;

            foreach (KeyValuePair<string, ClaimOfferState> kv in _stateByNodeId)
            {
                OfferReplyVerdict verdict = OfferAuthority.Judge(factionTag, kv.Value.OfferedFactionTag,
                    PlayerAuthority.MayCommand(identityId, factionTag));
                if (verdict == OfferReplyVerdict.NotForThem) continue;
                if (!string.Equals(kv.Key, nodeId, StringComparison.Ordinal)) continue;
                SpatialNode node;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return false;
                if (verdict == OfferReplyVerdict.Refused)
                {
                    Log.Info("Claim reply refused: " + factionTag + " for node " + nodeId + ", identity "
                        + identityId + " is not a leader");
                    Whisper(RecipientOf(identityId), OfferAuthority.RefusalText(factionTag), false);
                    return true;
                }
                if (isYes)
                {
                    MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
                    long seFactionId = seFaction != null ? seFaction.FactionId : 0L;
                    FactionState playerState = _runtime.EnsurePlayerFactionRegistered(factionTag, seFactionId);
                    _runtime.ClaimNodeForPlayerFaction(playerState, nodeId);
                    kv.Value.OfferedFactionTag = null;
                    _lastWhisperedFactionTag.Remove(nodeId);
                    _offerReachedLeader.Remove(nodeId);
                    Log.Info("Claim accepted: " + factionTag + " claimed node " + nodeId);
                    Whisper(RecipientOf(identityId), factionTag + " now holds " + nodeId + ".", false);
                }
                else
                {
                    ClaimOfferCoordinator.Decline(kv.Value, factionTag);
                    Whisper(RecipientOf(identityId), "Understood. " + nodeId + " remains unclaimed.", false);
                }
                return true;
            }
            return false;
        }
    }
}
