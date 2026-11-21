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
    /// The last step of a player raid: a node whose station a player broke into, held, and is
    /// offered outright.
    ///
    /// <para>Sibling of <see cref="ClaimOfferManager"/> and deliberately built the same way — whisper
    /// an offer, read a plain "yes"/"no" back, transfer on acceptance — because it is the same
    /// conversation with a different precondition. That one asks a faction present at an unclaimed
    /// node; this one asks the player who took a held one by force.</para>
    ///
    /// <para><b>The offer is the only thing that transfers ownership.</b> Succeeding at a raid does
    /// not take a station: <see cref="PlayerSiege"/> records the success and starts the clock, and
    /// nothing changes hands unless the attacker is still there when it expires and says yes. A
    /// player who wrecks a station and leaves has damaged it, which is what an NPC raid does too.</para>
    /// </summary>
    internal sealed class ConquerOfferManager
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);
        private const string WhisperAuthor = "Factions";

        private readonly FactionRuntime _runtime;
        private readonly WorldModel _world;
        private ChatManagerServer _chat;

        // nodeId -> identity the offer went to, so an unanswered offer is not re-whispered every
        // tick and an answer can be matched back to the node it belongs to.
        private readonly Dictionary<string, long> _offeredTo =
            new Dictionary<string, long>(StringComparer.Ordinal);

        // nodeId -> the attacker's faction tag when the offer was made; absent for a factionless
        // attacker. The offer belongs to that faction, and only someone who may command it answers.
        private readonly Dictionary<string, string> _offeredFaction =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Nodes whose offer has reached someone who may answer it. An offer whose attacker may not is
        // whispered to a leader of their faction once one is online.
        private readonly HashSet<string> _offerReachedLeader = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Set by the plugin on a Nexus authority: the players the agents report, so a leader
        /// on an agent can be offered a node a member of their faction took.</summary>
        public Func<IList<RemotePlayer>> RemotePlayers { get; set; }

        /// <summary>Set by the plugin on a Nexus authority: whether a player is on a reporting agent.
        /// Null: an attacker not on this server is treated as offline.</summary>
        public Func<long, bool> IsRemote { get; set; }

        /// <summary>Set by the plugin on a Nexus authority: whispers to a player on an agent
        /// (identity, text, whether it is the offer, whose yes or no the agent sends back).</summary>
        public Action<long, string, bool> RemoteWhisper { get; set; }

        public ConquerOfferManager(FactionRuntime runtime)
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

        /// <summary>Offers any node whose player raid has been held long enough. The attacker is
        /// carried on the siege rather than re-derived: whoever is standing there when the clock
        /// expires is not necessarily who took it.</summary>
        public void Tick(Func<string, long> attackerForNode)
        {
            if (_world == null || _chat == null) return;

            foreach (SpatialNode node in _world.Graph.Nodes.Values)
            {
                if (!PlayerSiege.ConquerOfferDue(node, _world.Tuning, _world.UtcNow))
                {
                    // The raid lapsed, or the node changed hands another way -- drop any standing
                    // offer so a later raid on the same node is offered fresh.
                    if (node.PlayerRaidSucceededUtc == default(DateTime)) Forget(node.Id);
                    continue;
                }
                if (_offeredTo.ContainsKey(node.Id))
                {
                    if (!_offerReachedLeader.Contains(node.Id)) OfferToLeader(node);
                    continue;
                }

                long identityId = attackerForNode != null ? attackerForNode(node.Id) : 0;
                if (identityId == 0) continue;

                MyPlayer player = MySession.Static.Players.TryGetPlayer(identityId);
                // A player on an agent is asked there; their answer comes back in its report.
                bool remote = player == null && IsRemote != null && RemoteWhisper != null && IsRemote(identityId);
                if (player == null && !remote) continue;      // offline; try again when they are back

                MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
                string tag = faction != null ? faction.Tag : null;
                bool mayCommand = tag == null || PlayerAuthority.MayCommand(identityId, tag);
                _offeredTo[node.Id] = identityId;
                if (tag != null) _offeredFaction[node.Id] = tag;
                if (mayCommand) _offerReachedLeader.Add(node.Id);
                string offer = OfferAuthority.ConquerOfferText(node.Id, tag, mayCommand);
                if (remote) RemoteWhisper(identityId, offer, true);
                else Whisper(player, offer);
                Log.Info("CONQUER_OFFERED node=" + node.Id + " identity=" + identityId
                         + " owner=" + (node.OwnerFactionTag ?? "?") + (remote ? " remote=True" : "")
                         + (mayCommand ? "" : " leader=False"));
                if (!mayCommand) OfferToLeader(node);
            }
        }

        private void Forget(string nodeId)
        {
            _offeredTo.Remove(nodeId);
            _offeredFaction.Remove(nodeId);
            _offerReachedLeader.Remove(nodeId);
        }

        /// <summary>Whispers a standing offer to an online leader of the attacker's faction, here or on
        /// an agent. Nothing when none is online; the next tick tries again.</summary>
        private void OfferToLeader(SpatialNode node)
        {
            string tag;
            long attacker;
            if (!_offeredFaction.TryGetValue(node.Id, out tag) || !_offeredTo.TryGetValue(node.Id, out attacker)) return;
            string text = OfferAuthority.ConquerOfferForLeaderText(node.Id, tag, NameOf(attacker));

            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                long id = player.Identity.IdentityId;
                if (id == attacker || !PlayerAuthority.IsLeader(id, tag)) continue;
                Whisper(player, text);
                _offerReachedLeader.Add(node.Id);
                Log.Info("CONQUER_OFFERED node=" + node.Id + " identity=" + id + " for=" + attacker + " faction=" + tag);
                return;
            }

            Func<IList<RemotePlayer>> remote = RemotePlayers;
            IList<RemotePlayer> others = remote != null ? remote() : null;
            if (others == null || RemoteWhisper == null) return;
            for (int i = 0; i < others.Count; i++)
            {
                long id = others[i].IdentityId;
                if (id == 0 || id == attacker || !PlayerAuthority.IsLeader(id, tag)) continue;
                RemoteWhisper(id, text, true);
                _offerReachedLeader.Add(node.Id);
                Log.Info("CONQUER_OFFERED node=" + node.Id + " identity=" + id + " for=" + attacker
                         + " faction=" + tag + " remote=True");
                return;
            }
        }

        private static string NameOf(long identityId)
        {
            MyIdentity identity = MySession.Static.Players.TryGetIdentity(identityId);
            return identity != null ? identity.DisplayName : null;
        }

        /// <summary>A yes or no from <paramref name="identityId"/>, typed here or relayed from the agent
        /// they are on. False when no offer stands for them.</summary>
        public bool Answer(long identityId, bool isYes, string nodeId)
        {
            if (identityId == 0L || string.IsNullOrEmpty(nodeId)) return false;
            string authorTag = FactionTagOf(identityId);
            foreach (KeyValuePair<string, long> kv in _offeredTo)
            {
                if (!string.Equals(kv.Key, nodeId, StringComparison.Ordinal)) continue;
                OfferReplyVerdict verdict = Judge(identityId, authorTag, kv.Key, kv.Value);
                if (verdict == OfferReplyVerdict.NotForThem) return false;

                SpatialNode node;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return false;
                if (verdict == OfferReplyVerdict.Refused)
                {
                    Log.Info("CONQUER_REFUSED node=" + nodeId + " identity=" + identityId + " faction=" + authorTag
                             + " leader=False");
                    Whisper(identityId, OfferAuthority.RefusalText(authorTag));
                    return true;
                }

                long attacker = kv.Value;
                if (isYes)
                {
                    AcceptConquest(identityId, nodeId, node);
                    if (attacker != identityId && !_offeredTo.ContainsKey(nodeId))
                        Whisper(attacker, (authorTag ?? "Your faction") + " has taken " + nodeId + ".");
                }
                else
                {
                    Forget(nodeId);
                    PlayerSiege.Reset(node);   // declining ends the raid; the station recovers
                    Whisper(identityId,
                        "Understood. " + nodeId + " stays with " + (node.OwnerFactionTag ?? "its owner") + ".");
                }
                return true;
            }
            return false;
        }

        /// <summary>Adds every conquer offer open to <paramref name="identityId"/>, and whether they
        /// may answer it, for <see cref="OfferReplyRouter"/>.</summary>
        public void CollectOpen(long identityId, List<OpenOffer> into)
        {
            if (identityId == 0L || _world == null) return;
            string authorTag = FactionTagOf(identityId);
            foreach (KeyValuePair<string, long> kv in _offeredTo)
            {
                OfferReplyVerdict verdict = Judge(identityId, authorTag, kv.Key, kv.Value);
                if (verdict == OfferReplyVerdict.NotForThem || !_world.Graph.Nodes.ContainsKey(kv.Key)) continue;
                into.Add(new OpenOffer { NodeId = kv.Key, Conquer = true, MayAnswer = verdict == OfferReplyVerdict.Answer });
            }
        }

        private static string FactionTagOf(long identityId)
        {
            MyFaction faction = MySession.Static.Factions.GetPlayerFaction(identityId);
            return faction != null ? faction.Tag : null;
        }

        /// <summary>Whether <paramref name="identityId"/>, of <paramref name="authorTag"/>, may answer
        /// the offer at <paramref name="nodeId"/> made to <paramref name="attacker"/>.</summary>
        private OfferReplyVerdict Judge(long identityId, string authorTag, string nodeId, long attacker)
        {
            string offeredTag;
            if (!_offeredFaction.TryGetValue(nodeId, out offeredTag))
            {
                // Offered to a factionless attacker: theirs alone, judged by the faction they are in
                // now, if they have since joined or founded one.
                if (attacker != identityId) return OfferReplyVerdict.NotForThem;
                if (authorTag == null) return OfferReplyVerdict.Answer;
                offeredTag = authorTag;
            }
            if (authorTag == null) return OfferReplyVerdict.NotForThem;
            return OfferAuthority.Judge(authorTag, offeredTag, PlayerAuthority.MayCommand(identityId, authorTag));
        }

        private void AcceptConquest(long identityId, string nodeId, SpatialNode node)
        {
            MyFaction seFaction = MySession.Static.Factions.GetPlayerFaction(identityId);
            if (seFaction == null)
            {
                Whisper(identityId,
                    "You need a faction to hold territory. Found one and we'll ask again.");
                return;
            }

            string previousOwner = node.OwnerFactionTag;
            FactionState playerState = _runtime.EnsurePlayerFactionRegistered(seFaction.Tag, seFaction.FactionId);

            // Everything below reports a completed conquest, so none of it may run on a transfer that
            // did not happen -- including the siege reset, which would otherwise throw away the raid
            // the player would need to try again.
            if (!_runtime.CaptureNodeForPlayerFaction(playerState, nodeId))
            {
                Log.Warn("CONQUER_TRANSFER_FAILED node=" + nodeId + " to=" + seFaction.Tag
                         + " from=" + (previousOwner ?? "?") + " identity=" + identityId);
                Whisper(identityId,
                    "Something went wrong taking " + nodeId + " -- it stays with "
                    + (previousOwner ?? "its owner") + " for now. The raid still stands; try again.");
                return;
            }

            // The siege is over: the node is theirs, so its zone belongs to the new owner on the
            // ordinary terms rather than staying breached forever.
            PlayerSiege.Reset(node);
            Forget(nodeId);

            Log.Info("CONQUER_ACCEPTED node=" + nodeId + " to=" + seFaction.Tag
                     + " from=" + (previousOwner ?? "?") + " identity=" + identityId
                     + NodeHoldings.Of(node).Suffix());
            Whisper(identityId,
                seFaction.Tag + " now holds " + nodeId + ", taken from " + (previousOwner ?? "its former owner") + ".");
        }

        private void Whisper(long identityId, string message)
        {
            MyPlayer player = MySession.Static.Players.TryGetPlayer(identityId);
            if (player != null) { Whisper(player, message); return; }
            Action<long, string, bool> remote = RemoteWhisper;
            if (remote != null && IsRemote != null && IsRemote(identityId)) remote(identityId, message, false);
        }

        private void Whisper(MyPlayer recipient, string message)
        {
            if (_chat == null || recipient == null) return;
            _chat.SendMessageAsOther(WhisperAuthor, message, Color.OrangeRed, recipient.Id.SteamId);
        }

    }
}
