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
        private static readonly string[] ReplyPrefixes = { "/r ", "/reply " };

        private readonly FactionRuntime _runtime;
        private readonly WorldModel _world;
        private ChatManagerServer _chat;

        // nodeId -> identity the offer went to, so an unanswered offer is not re-whispered every
        // tick and an answer can be matched back to the node it belongs to.
        private readonly Dictionary<string, long> _offeredTo =
            new Dictionary<string, long>(StringComparer.Ordinal);

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
            _chat = chat;
            if (_chat != null) _chat.MessageProcessing += OnChatMessageProcessing;
        }

        public void DetachChat()
        {
            if (_chat != null) _chat.MessageProcessing -= OnChatMessageProcessing;
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
                    if (node.PlayerRaidSucceededUtc == default(DateTime)) _offeredTo.Remove(node.Id);
                    continue;
                }
                if (_offeredTo.ContainsKey(node.Id)) continue;

                long identityId = attackerForNode != null ? attackerForNode(node.Id) : 0;
                if (identityId == 0) continue;

                MyPlayer player = MySession.Static.Players.TryGetPlayer(identityId);
                // A player on an agent is asked there; their answer comes back in its report.
                bool remote = player == null && IsRemote != null && RemoteWhisper != null && IsRemote(identityId);
                if (player == null && !remote) continue;      // offline; try again when they are back

                _offeredTo[node.Id] = identityId;
                string offer = "You hold " + node.Id + ". Its owner cannot dislodge you. "
                             + "Take it? Reply \"yes\" to claim it, \"no\" to leave it standing.";
                if (remote) RemoteWhisper(identityId, offer, true);
                else Whisper(player, offer);
                Log.Info("CONQUER_OFFERED node=" + node.Id + " identity=" + identityId
                         + " owner=" + (node.OwnerFactionTag ?? "?") + (remote ? " remote=True" : ""));
            }
        }

        private void OnChatMessageProcessing(Torch.API.Managers.TorchChatMessage msg, ref bool consumed)
        {
            try
            {
                if (!msg.AuthorSteamId.HasValue) return;   // TorchChatMessage is a struct
                bool isYes;
                if (!TryParseYesNo(msg.Message, out isYes)) return;

                long identityId = MySession.Static.Players.TryGetIdentityId(msg.AuthorSteamId.Value);
                if (identityId == 0L) return;
                if (Answer(identityId, isYes)) consumed = true;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ConquerOfferManager: chat handling threw.");
            }
        }

        /// <summary>A yes or no from <paramref name="identityId"/>, typed here or relayed from the agent
        /// they are on. False when no offer stands for them.</summary>
        public bool Answer(long identityId, bool isYes)
        {
            foreach (KeyValuePair<string, long> kv in _offeredTo)
            {
                if (kv.Value != identityId) continue;
                string nodeId = kv.Key;
                SpatialNode node;
                if (!_world.Graph.Nodes.TryGetValue(nodeId, out node)) return false;

                if (isYes) AcceptConquest(identityId, nodeId, node);
                else
                {
                    _offeredTo.Remove(nodeId);
                    PlayerSiege.Reset(node);   // declining ends the raid; the station recovers
                    Whisper(identityId,
                        "Understood. " + nodeId + " stays with " + (node.OwnerFactionTag ?? "its owner") + ".");
                }
                return true;
            }
            return false;
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
            _offeredTo.Remove(nodeId);

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

        /// <summary>Same reply grammar as the claim offer — a bare yes/no, optionally behind a
        /// whisper-reply prefix. Two conversations that look identical to a player should not want
        /// different words.</summary>
        internal static bool TryParseYesNo(string rawText, out bool isYes)
        {
            isYes = false;
            if (string.IsNullOrEmpty(rawText)) return false;
            string text = rawText.Trim();
            foreach (string prefix in ReplyPrefixes)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(prefix.Length).TrimStart();
                    break;
                }
            }
            text = text.TrimEnd('!', '.', '?', ' ');

            if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "y", StringComparison.OrdinalIgnoreCase))
            {
                isYes = true;
                return true;
            }
            return string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "n", StringComparison.OrdinalIgnoreCase);
        }
    }
}
