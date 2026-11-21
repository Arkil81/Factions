using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Factions.Commands;
using Factions.Wire;
using NLog;
using Sandbox.Game.World;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.Commands;
using Torch.Managers.ChatManager;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Factions chat commands typed by a player on a Nexus agent, run on the authority. An agent runs
    /// no simulation, so the commands that read or change it refuse there; it hands the line to the
    /// authority instead, with who typed it and where they stand, and tells the player what came back.
    /// The commands that act on the player's own surroundings (their ship's crew, a grid in front of
    /// them, the server's own settings) stay where the player is.
    ///
    /// <para>The agent's file bridge goes the same way: a frame from steam id 0 carries a bridge
    /// verb, which the authority runs as its own bridge would and answers into the agent's outbox.</para>
    /// </summary>
    internal sealed class NexusChatCommands
    {
        private static readonly Logger Log = LogManager.GetLogger("Factions");
        private const string WhisperAuthor = "Factions";

        /// <summary>How long the agent waits for the authority's answer before telling the player
        /// it did not come.</summary>
        internal static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Command paths an agent runs itself, because what they act on is on the agent.</summary>
        internal static readonly HashSet<string> RunHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "factions crew", "factions crew mount", "factions crew dismount", "factions crew ease",
            "factions crew auto", "factions dampprobe", "factions gridspeed", "factions missilespeed",
            "factions debug", "factions reload",
            // Captures read the grid the player looks at, which only the agent holds.
            "factions livery capture", "factions ship capture", "factions station scan",
        };

        private static readonly Regex ArgSplit = new Regex("(\"[^\"]+\"|\\S+)");

        private readonly ITorchBase _torch;
        private readonly Assembly _ours;
        private readonly Func<byte, byte[], bool> _send;
        private readonly byte _thisServer;
        private readonly byte _authority;
        private CommandManager _commands;
        private ChatManagerServer _chat;

        private sealed class Pending
        {
            public ulong Steam;
            /// <summary>Set for a bridge verb: the line, for the outbox.</summary>
            public string Verb;
            public DateTime SentUtc;
        }

        /// <summary>Authority: runs one bridge verb line and returns its answer, or null when the
        /// line is not a Factions verb.</summary>
        internal Func<string, string> RunVerb;

        /// <summary>Agent: writes the authority's answer to a forwarded bridge verb, line then answer;
        /// a null answer means none came.</summary>
        internal Action<string, string> VerbAnswered;

        private readonly Dictionary<long, Pending> _pending = new Dictionary<long, Pending>();
        private long _nextId;

        internal NexusChatCommands(ITorchBase torch, Func<byte, byte[], bool> send, byte thisServer, byte authority)
        {
            _torch = torch;
            _ours = typeof(NexusChatCommands).Assembly;
            _send = send;
            _thisServer = thisServer;
            _authority = authority;
        }

        private CommandManager Commands
        {
            get
            {
                if (_commands == null && _torch != null && _torch.CurrentSession != null)
                    _commands = _torch.CurrentSession.Managers.GetManager<CommandManager>();
                return _commands;
            }
        }

        /// <summary>The path a command is registered under, words joined by spaces.</summary>
        internal static string PathOf(Command command)
        {
            return command == null ? "" : string.Join(" ", command.Path);
        }

        // ---------------------------------------------------------------- agent

        /// <summary>Agent: start catching this plugin's commands. False when Torch's command or
        /// chat manager is missing, in which case they run here and refuse as before.</summary>
        internal bool AttachAgent()
        {
            CommandManager commands = Commands;
            _chat = _torch != null && _torch.CurrentSession != null
                ? _torch.CurrentSession.Managers.GetManager<ChatManagerServer>() : null;
            if (commands == null || _chat == null) return false;
            commands.OnCommandExecuting += OnCommandExecuting;
            _chat.MessageProcessing += OnChat;
            return true;
        }

        internal void Detach()
        {
            if (_commands != null) _commands.OnCommandExecuting -= OnCommandExecuting;
            if (_chat != null) _chat.MessageProcessing -= OnChat;
            _commands = null;
            _chat = null;
            _pending.Clear();
        }

        private bool ForwardsFrom(Command command)
        {
            return command != null && command.Module != null && command.Module.Assembly == _ours
                   && !RunHere.Contains(PathOf(command));
        }

        // Torch's own chat handler runs the command and ours sees the same line, in an order that is
        // not ours to choose; each decides from the command alone, so neither needs the other.
        // OnCommandExecuting stops Torch running it here, and OnChat sends the line on.
        private void OnCommandExecuting(Command command, IMyPlayer player, bool hasPermission, ref bool? hasPermissionOverride)
        {
            try
            {
                // False stops Torch running it here. The authority checks the promote level itself.
                if (ForwardsFrom(command)) hasPermissionOverride = false;
            }
            catch (Exception ex) { Log.Warn(ex, "Checking a chat command for forwarding threw."); }
        }

        private void OnChat(TorchChatMessage msg, ref bool consumed)
        {
            try
            {
                if (!msg.AuthorSteamId.HasValue || string.IsNullOrEmpty(msg.Message)) return;
                CommandManager commands = Commands;
                if (commands == null || msg.Message.Length < 2 || msg.Message[0] != commands.Prefix) return;
                string argText;
                if (!ForwardsFrom(commands.Commands.GetCommand(msg.Message.Substring(1), out argText))) return;
                consumed = true;
                Forward(msg.AuthorSteamId.Value, msg.Message);
            }
            catch (Exception ex) { Log.Warn(ex, "Forwarding a chat command to the authority threw."); }
        }

        private void Forward(ulong steam, string line)
        {
            var c = new RelayChatCommand { Id = ++_nextId, Text = line };
            MyPlayer player = FindOnline(steam);
            if (player != null)
            {
                c.IdentityId = player.Identity != null ? player.Identity.IdentityId : 0L;
                if (player.Character != null)
                {
                    Vector3D p = player.Character.PositionComp.GetPosition();
                    c.HasPosition = true;
                    c.X = p.X; c.Y = p.Y; c.Z = p.Z;
                }
            }
            c.Promote = MySession.Static != null ? (byte)MySession.Static.GetUserPromoteLevel(steam) : (byte)0;
            byte[] frame = NexusRelayWire.Write(RelayKind.ChatCommand, _thisServer, steam, NexusRelayWire.WriteChatCommand(c));
            if (_send == null || !_send(_authority, frame))
            {
                Whisper(steam, "The cluster's simulation server cannot be reached from here; try again shortly.");
                return;
            }
            _pending[c.Id] = new Pending { Steam = steam, SentUtc = DateTime.UtcNow };
        }

        private static MyPlayer FindOnline(ulong steam)
        {
            if (MySession.Static == null || MySession.Static.Players == null) return null;
            foreach (MyPlayer p in MySession.Static.Players.GetOnlinePlayers())
                if (p != null && !p.IsBot && p.Id.SteamId == steam) return p;
            return null;
        }

        /// <summary>Agent: sends a bridge verb line to the authority. False when it could not be
        /// sent; the answer otherwise arrives through <see cref="VerbAnswered"/>.</summary>
        internal bool ForwardVerb(string line)
        {
            var c = new RelayChatCommand { Id = ++_nextId, Text = line, Promote = (byte)MyPromoteLevel.Owner };
            byte[] frame = NexusRelayWire.Write(RelayKind.ChatCommand, _thisServer, 0UL, NexusRelayWire.WriteChatCommand(c));
            if (_send == null || !_send(_authority, frame)) return false;
            _pending[c.Id] = new Pending { Verb = line, SentUtc = DateTime.UtcNow };
            return true;
        }

        /// <summary>Agent: the authority's answer to a forwarded command or bridge verb.</summary>
        internal void OnReply(ulong steam, byte[] payload)
        {
            long id; List<string> lines;
            if (!NexusRelayWire.TryReadChatReply(payload, out id, out lines))
            {
                Log.Warn("NEXUS_CHAT_REPLY_UNREADABLE steam=" + steam);
                return;
            }
            Pending p;
            if (_pending.TryGetValue(id, out p)) _pending.Remove(id);
            if (steam == 0UL)
            {
                Action<string, string> answered = VerbAnswered;
                if (answered != null) answered(p != null ? p.Verb : "#" + id, string.Join("\n", lines));
                return;
            }
            for (int i = 0; i < lines.Count; i++) Whisper(steam, lines[i]);
        }

        /// <summary>Agent: tells each player whose command went unanswered.</summary>
        internal void Tick(DateTime nowUtc)
        {
            if (_pending.Count == 0) return;
            List<long> late = null;
            foreach (KeyValuePair<long, Pending> kv in _pending)
                if (nowUtc - kv.Value.SentUtc > AnswerTimeout) (late ?? (late = new List<long>())).Add(kv.Key);
            if (late == null) return;
            for (int i = 0; i < late.Count; i++)
            {
                Pending p = _pending[late[i]];
                _pending.Remove(late[i]);
                if (p.Verb != null)
                {
                    Action<string, string> answered = VerbAnswered;
                    if (answered != null) answered(p.Verb, null);
                }
                else Whisper(p.Steam, "The cluster's simulation server did not answer; the command may not have run.");
            }
        }

        private void Whisper(ulong steam, string line)
        {
            if (_chat == null || line == null) return;
            _chat.SendMessageAsOther(WhisperAuthor, line, Color.CornflowerBlue, steam);
        }

        // ---------------------------------------------------------------- authority

        /// <summary>Authority: runs a command forwarded by agent <paramref name="home"/> for
        /// <paramref name="steam"/> and sends back what it answered.</summary>
        internal void OnCommand(byte home, ulong steam, byte[] payload)
        {
            RelayChatCommand c;
            if (!NexusRelayWire.TryReadChatCommand(payload, out c))
            {
                Log.Warn("NEXUS_CHAT_COMMAND_UNREADABLE server=" + home + " steam=" + steam);
                return;
            }
            var lines = new List<string>();
            if (steam == 0UL)
            {
                Func<string, string> run = RunVerb;
                string answer = run != null ? run(c.Text ?? "") : null;
                lines.Add(answer ?? "not a Factions verb on the authority: " + (c.Text ?? ""));
                Log.Info("NEXUS_BRIDGE_VERB server=" + home + " verb=" + FirstWords(c.Text, 2) + " ran=" + (answer != null));
                if (_send != null)
                    _send(home, NexusRelayWire.Write(RelayKind.ChatReply, _thisServer, 0UL, NexusRelayWire.WriteChatReply(c.Id, lines)));
                return;
            }
            string path;
            bool ran = Run(steam, c, lines, out path);
            Log.Info("NEXUS_CHAT_COMMAND server=" + home + " steam=" + steam + " command=" + (path.Length == 0 ? "-" : path.Replace(' ', '.'))
                     + " ran=" + ran + " lines=" + lines.Count);
            byte[] frame = NexusRelayWire.Write(RelayKind.ChatReply, _thisServer, steam, NexusRelayWire.WriteChatReply(c.Id, lines));
            if (_send != null) _send(home, frame);
        }

        private static string FirstWords(string line, int n)
        {
            if (string.IsNullOrEmpty(line)) return "-";
            string[] w = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return w.Length == 0 ? "-" : string.Join(".", w.Take(n));
        }

        private bool Run(ulong steam, RelayChatCommand c, List<string> lines, out string path)
        {
            path = "";
            CommandManager commands = Commands;
            if (commands == null) { lines.Add("Factions plugin not ready."); return false; }
            string text = c.Text ?? "";
            if (text.Length < 2 || text[0] != commands.Prefix) { lines.Add("Not a command."); return false; }

            string argText;
            Command command = commands.Commands.GetCommand(text.Substring(1), out argText);
            if (command == null || command.Module == null || command.Module.Assembly != _ours)
            {
                lines.Add("Unknown command.");
                return false;
            }
            path = PathOf(command);
            if ((MyPromoteLevel)c.Promote < command.MinimumPromoteLevel)
            {
                lines.Add("You need to be a " + command.MinimumPromoteLevel + " or higher to use that command.");
                return false;
            }

            long identity = 0L;
            try { identity = MySession.Static != null ? MySession.Static.Players.TryGetIdentityId(steam) : 0L; }
            catch { identity = 0L; }
            if (identity == 0L) identity = c.IdentityId;
            if (identity == 0L) { lines.Add("Your identity is not known here yet; try again in a few seconds."); return false; }

            var caller = new CommandCaller
            {
                SteamId = steam,
                IdentityId = identity,
                Promote = (MyPromoteLevel)c.Promote,
                HasPosition = c.HasPosition,
                PositionM = new Vector3D(c.X, c.Y, c.Z),
            };
            List<string> args = ArgSplit.Matches(argText ?? "").Cast<Match>()
                .Select(m => m.ToString().Replace("\"", "")).ToList();
            var context = new RemoteCommandContext(_torch, command.Plugin, caller, argText, args);
            bool ok = command.TryInvoke(context);
            lines.AddRange(context.Responses);
            if (!ok) lines.Add("Invalid Syntax: " + command.SyntaxHelp);
            return ok;
        }
    }
}
