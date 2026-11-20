using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Publishes the online-player roster plus a persistent SteamID→username index to
    /// <c>players.json</c> so the dashboard API can resolve an authenticated SteamID to an
    /// in-game identity. Runs on the SE game thread; the API process only reads the file.
    /// SteamIDs are serialized as strings — a SteamID64 exceeds JS number precision.
    /// </summary>
    internal sealed class PlayerIndexWriter
    {
        // Faster than the planet snapshot: the dashboard shows live player positions from this file.
        private const double RefreshSeconds = 10.0;
        private const int MaxKnownPlayers = 500;

        private readonly string _path;
        private readonly Dictionary<string, KnownPlayerEntry> _known =
            new Dictionary<string, KnownPlayerEntry>(StringComparer.Ordinal);
        private DateTime _nextWriteUtc = DateTime.MinValue;

        /// <summary>Authority on a Nexus cluster: the players on agents. They are online on the
        /// cluster, and the dashboard resolves them from this one file.</summary>
        public Func<IList<Factions.Core.RemotePlayer>> RemotePlayers { get; set; }

        /// <summary>A remote player's display name as their agent last reported it; null when none has.</summary>
        public Func<ulong, string> RemoteName { get; set; }

        public PlayerIndexWriter(string storageDir)
        {
            _path = Path.Combine(storageDir, "players.json");
            LoadExisting();
        }

        private void LoadExisting()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var file = JsonConvert.DeserializeObject<PlayerIndexFile>(File.ReadAllText(_path));
                if (file == null || file.Known == null) return;
                foreach (var pair in file.Known)
                    if (pair.Value != null) _known[pair.Key] = pair.Value;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "players.json load failed; starting with an empty index.");
            }
        }

        /// <summary>When this identity was last online, for anything that weights a player by
        /// activity. A currently-connected player reads as <paramref name="nowUtc"/>; otherwise the
        /// answer comes from the persisted known-player index, so it survives a restart.
        ///
        /// <para>This index is the canonical activity record — it is already written every ten
        /// seconds, already trimmed, and already persisted. A second last-seen store beside it would
        /// be a copy that can disagree.</para></summary>
        /// <returns>False for an identity that has never been seen online, which is not the same as
        /// "seen a long time ago" and must not be treated as one.</returns>
        public bool TryGetLastSeen(long identityId, DateTime nowUtc, out DateTime lastSeenUtc)
        {
            lastSeenUtc = default(DateTime);
            if (identityId == 0) return false;

            if (MySession.Static != null)
            {
                MyPlayer online = MySession.Static.Players.TryGetPlayer(identityId);
                if (online != null && !online.IsBot) { lastSeenUtc = nowUtc; return true; }
            }

            // Keyed by SteamID rather than identity, so this is a scan -- bounded by MaxKnownPlayers.
            foreach (KnownPlayerEntry entry in _known.Values)
            {
                if (entry == null || entry.IdentityId != identityId) continue;
                lastSeenUtc = entry.LastSeenUtc;
                return true;
            }
            return false;
        }

        public void Tick(DateTime now)
        {
            if (now < _nextWriteUtc) return;
            _nextWriteUtc = now.AddSeconds(RefreshSeconds);
            if (MySession.Static == null) return;
            try
            {
                var file = new PlayerIndexFile { At = now };
                foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
                {
                    if (player.IsBot || player.Identity == null) continue;
                    string steamId = player.Id.SteamId.ToString();
                    string name = player.Identity.DisplayName;
                    long identityId = player.Identity.IdentityId;

                    var online = new OnlinePlayerEntry
                    {
                        SteamId = steamId,
                        IdentityId = identityId,
                        Name = name,
                    };
                    // Position in km (matching node coordinates): from the character, or the
                    // controlled entity when the player is seated/piloting without a character.
                    VRage.Game.Entity.MyEntity posEntity = player.Character;
                    if (posEntity == null && player.Controller != null && player.Controller.ControlledEntity != null)
                        posEntity = player.Controller.ControlledEntity.Entity;
                    if (posEntity != null)
                    {
                        VRageMath.Vector3D pos = posEntity.PositionComp.GetPosition();
                        online.HasPosition = true;
                        online.X = pos.X / 1000.0;
                        online.Y = pos.Y / 1000.0;
                        online.Z = pos.Z / 1000.0;
                    }
                    file.Online.Add(online);

                    KnownPlayerEntry entry;
                    if (!_known.TryGetValue(steamId, out entry))
                    {
                        entry = new KnownPlayerEntry();
                        _known[steamId] = entry;
                    }
                    entry.Name = name;
                    entry.IdentityId = identityId;
                    entry.LastSeenUtc = now;
                    entry.PromoteLevel = MySession.Static.GetUserPromoteLevel(player.Id.SteamId).ToString();
                }
                AddRemote(file, now);
                TrimKnown();
                file.Known = _known;

                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.None));
                // Replace atomically in the same directory: the API reader always sees either
                // the previous complete index or the new one, never a truncated file.
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "Player index write failed.");
            }
        }

        /// <summary>The agents' players, after this server's own: one connected here as well is
        /// already listed, and <see cref="RemotePlayers"/> leaves them out.</summary>
        private void AddRemote(PlayerIndexFile file, DateTime now)
        {
            Func<IList<Factions.Core.RemotePlayer>> source = RemotePlayers;
            IList<Factions.Core.RemotePlayer> remote = source != null ? source() : null;
            if (remote == null) return;
            for (int i = 0; i < remote.Count; i++)
            {
                Factions.Core.RemotePlayer r = remote[i];
                if (r.SteamId == 0) continue;
                string steamId = r.SteamId.ToString();

                // This server's identity where it holds one: Nexus does not promise the same id on
                // every shard, and the dashboard resolves the identity against this server's state.
                long identityId = MySession.Static.Players.TryGetIdentityId(r.SteamId);
                if (identityId == 0) identityId = r.IdentityId;

                KnownPlayerEntry entry;
                _known.TryGetValue(steamId, out entry);
                string name = RemoteName != null ? RemoteName(r.SteamId) : null;
                if (name == null)
                {
                    MyIdentity identity = identityId != 0 ? MySession.Static.Players.TryGetIdentity(identityId) : null;
                    name = identity != null ? identity.DisplayName : (entry != null ? entry.Name : null);
                }

                file.Online.Add(new OnlinePlayerEntry
                {
                    SteamId = steamId,
                    IdentityId = identityId,
                    Name = name,
                    HasPosition = true,
                    X = r.PositionM.X / 1000.0,
                    Y = r.PositionM.Y / 1000.0,
                    Z = r.PositionM.Z / 1000.0,
                    Server = r.HomeServerId,
                });

                if (entry == null)
                {
                    entry = new KnownPlayerEntry();
                    _known[steamId] = entry;
                }
                if (name != null) entry.Name = name;
                entry.IdentityId = identityId;
                entry.LastSeenUtc = now;
                entry.PromoteLevel = MySession.Static.GetUserPromoteLevel(r.SteamId).ToString();
            }
        }

        private void TrimKnown()
        {
            if (_known.Count <= MaxKnownPlayers) return;
            var ordered = new List<KeyValuePair<string, KnownPlayerEntry>>(_known);
            ordered.Sort((a, b) => a.Value.LastSeenUtc.CompareTo(b.Value.LastSeenUtc));
            int remove = _known.Count - MaxKnownPlayers;
            for (int i = 0; i < remove; i++) _known.Remove(ordered[i].Key);
        }

        private sealed class PlayerIndexFile
        {
            public DateTime At { get; set; }
            public List<OnlinePlayerEntry> Online { get; set; } = new List<OnlinePlayerEntry>();
            public Dictionary<string, KnownPlayerEntry> Known { get; set; }
        }

        private sealed class OnlinePlayerEntry
        {
            public string SteamId { get; set; }
            public long IdentityId { get; set; }
            public string Name { get; set; }
            public bool HasPosition { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
            /// <summary>The Nexus server the player is connected to, when it is not this one.</summary>
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public byte? Server { get; set; }
        }

        private sealed class KnownPlayerEntry
        {
            public string Name { get; set; }
            public long IdentityId { get; set; }
            public DateTime LastSeenUtc { get; set; }
            public string PromoteLevel { get; set; }
        }
    }
}
