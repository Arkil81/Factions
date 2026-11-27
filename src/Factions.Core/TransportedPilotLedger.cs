using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What to do with a loose character Nexus is about to send to another server.</summary>
    public enum LeftoverPilotAction
    {
        /// <summary>Not a pilot sent with a ship here recently, or its player is still on this server:
        /// let Nexus send it.</summary>
        Send,
        /// <summary>A leftover, but the switch is off: let Nexus send it and say so.</summary>
        SendObserved,
        /// <summary>A leftover: close it here instead of sending it.</summary>
        Close,
    }

    /// <summary>
    /// The pilots this server just sent to another server inside a ship, so the copy of them that stays
    /// behind is not sent a second time (FAC-530).
    ///
    /// <para>Nexus's grid transfer serializes each seated pilot into the message but leaves the
    /// character seated in the source cockpit. The ship is closed once the pilot has left this server,
    /// which ejects that character. Nexus's boundary scanner then sees a player character with no grid
    /// near it, standing in another server's sector, and sends it there as a loose character. It lands
    /// before the player has reconnected, so the destination spawns it and clears the player's other
    /// characters, including the one it had just seated in the ship.</para>
    ///
    /// <para>A character is a leftover when it was a recorded pilot within <see cref="WindowSeconds"/>
    /// and its player is not on this server. It is matched on its entity id, or on its identity in case
    /// the eject gives it a new entity. A player who is still here is never a leftover: their
    /// character is the only one they have.</para>
    /// </summary>
    public sealed class TransportedPilotLedger
    {
        /// <summary>How long after the ship was sent a loose character still counts as left behind.
        /// Nexus closes the ship once the pilot is offline here, or after ten Update60 passes (about
        /// 10 s, longer at low sim speed), and the scanner then picks the character up on its next pass.
        /// Two minutes covers both with room to spare. The window never reaches a player who came back,
        /// because a returning player is online here.</summary>
        public const double WindowSeconds = 120.0;

        private struct Entry
        {
            public long EntityId;
            public long IdentityId;
            public double SentAtSeconds;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly object _gate = new object();

        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>Records a pilot sent with a ship at <paramref name="nowSeconds"/>, replacing any
        /// earlier record for the same entity or identity.</summary>
        public void Record(long entityId, long identityId, double nowSeconds)
        {
            lock (_gate)
            {
                Prune(nowSeconds);
                for (int i = _entries.Count - 1; i >= 0; i--)
                    if (_entries[i].EntityId == entityId || (identityId != 0 && _entries[i].IdentityId == identityId))
                        _entries.RemoveAt(i);
                _entries.Add(new Entry { EntityId = entityId, IdentityId = identityId, SentAtSeconds = nowSeconds });
            }
        }

        /// <summary>True when this character was sent as a pilot in the last
        /// <see cref="WindowSeconds"/>. <paramref name="byEntity"/> says whether its entity id matched,
        /// or only its identity.</summary>
        public bool TryMatch(long entityId, long identityId, double nowSeconds, out double ageSeconds, out bool byEntity)
        {
            ageSeconds = 0;
            byEntity = false;
            lock (_gate)
            {
                Prune(nowSeconds);
                int identityMatch = -1;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].EntityId == entityId)
                    {
                        ageSeconds = nowSeconds - _entries[i].SentAtSeconds;
                        byEntity = true;
                        return true;
                    }
                    if (identityId != 0 && _entries[i].IdentityId == identityId) identityMatch = i;
                }
                if (identityMatch < 0) return false;
                ageSeconds = nowSeconds - _entries[identityMatch].SentAtSeconds;
                return true;
            }
        }

        /// <summary>What to do with a loose character Nexus is about to send.</summary>
        public static LeftoverPilotAction Decide(bool switchOn, bool matched, bool playerOnlineHere)
        {
            if (!matched || playerOnlineHere) return LeftoverPilotAction.Send;
            return switchOn ? LeftoverPilotAction.Close : LeftoverPilotAction.SendObserved;
        }

        private void Prune(double nowSeconds)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (nowSeconds - _entries[i].SentAtSeconds > WindowSeconds)
                    _entries.RemoveAt(i);
        }
    }
}
