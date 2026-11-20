using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What <see cref="StoreAccess.Decide"/> answered for one customer at one station.</summary>
    public struct StoreAccessDecision
    {
        public bool Refused;

        /// <summary>The sentence the customer is shown, e.g. "IMPR has embargoed EXP — trade
        /// refused". Null when served.</summary>
        public string Message;

        /// <summary>A single log token naming which rule refused: <c>embargo:&lt;holder&gt;</c>,
        /// <c>hostile:&lt;holder&gt;</c> or <c>personal:Enemies</c>. Null when served.</summary>
        public string Cause;

        public static readonly StoreAccessDecision Served = new StoreAccessDecision();
    }

    /// <summary>
    /// Whether a customer may buy, sell or take a service at a registered faction's station — the
    /// store blocks and the Services Terminal. docs/diplomacy-plan.md, rule 2.
    ///
    /// <list type="bullet">
    /// <item><description>A customer in a registered faction is refused iff
    /// <see cref="Diplomacy.TradeBarred"/> holds between that faction and the station's owner: the
    /// same predicate that stops convoy trade, so a member cannot walk in and buy what their faction
    /// is barred from shipping.</description></item>
    /// <item><description>A customer in no registered faction has no faction to judge, so their own
    /// reputation with the owner decides: refused iff its band is <c>Enemies</c>, by SE's
    /// <c>TranslateReputationToRelationship</c> (the band the host reads off
    /// <c>GetRelationBetweenPlayerAndFaction</c>).</description></item>
    /// <item><description>A member of the owning faction is never refused at its own
    /// stations.</description></item>
    /// </list>
    /// </summary>
    public static class StoreAccess
    {
        /// <summary>SE's hostile band name, as <c>MyRelationsBetweenFactions.Enemies.ToString()</c>
        /// and <see cref="ReputationScale.BandOf"/> both spell it.</summary>
        public const string EnemiesBand = "Enemies";

        /// <param name="owner">The station owner's state. Null (unregistered) is served: nothing here
        /// to judge by.</param>
        /// <param name="customerFaction">The customer's registered faction, or null when they are in
        /// no faction or one Factions does not track.</param>
        /// <param name="personalBand">The customer's personal reputation band toward the owner —
        /// read only when <paramref name="customerFaction"/> is null.</param>
        public static StoreAccessDecision Decide(FactionState owner, FactionState customerFaction,
                                                 string personalBand, FactionTuning tuning)
        {
            if (owner == null || string.IsNullOrEmpty(owner.Tag)) return StoreAccessDecision.Served;

            if (customerFaction == null)
            {
                if (!string.Equals(personalBand, EnemiesBand, StringComparison.OrdinalIgnoreCase))
                    return StoreAccessDecision.Served;
                return Refuse(owner.Tag + " will not trade with you", "personal:" + EnemiesBand);
            }

            if (string.Equals(owner.Tag, customerFaction.Tag, StringComparison.OrdinalIgnoreCase))
                return StoreAccessDecision.Served;

            if (!Diplomacy.TradeBarred(owner, customerFaction, tuning)) return StoreAccessDecision.Served;

            // Which half held. The owner's own stance first: it is the one that owns the counter the
            // customer is standing at. Within a side, the embargo first, as TradeManager.BarReason
            // does: it is the deliberate act, where hostility may be drift.
            string them = customerFaction.Tag ?? "?";
            if (customerFaction.Tag != null && owner.Embargoes.Contains(customerFaction.Tag))
                return Refuse(owner.Tag + " has embargoed " + them + " — trade refused", "embargo:" + owner.Tag);
            if (customerFaction.Tag != null && tuning != null
                && owner.RelationTo(customerFaction.Tag) < tuning.HostileRelationThreshold)
                return Refuse(owner.Tag + " is hostile to " + them + " — trade refused", "hostile:" + owner.Tag);
            if (customerFaction.Embargoes.Contains(owner.Tag))
                return Refuse(them + " has embargoed " + owner.Tag + " — trade refused", "embargo:" + them);
            return Refuse(them + " is hostile to " + owner.Tag + " — trade refused", "hostile:" + them);
        }

        private static StoreAccessDecision Refuse(string message, string cause)
        {
            return new StoreAccessDecision { Refused = true, Message = message, Cause = cause };
        }
    }

    /// <summary>
    /// One refusal message per customer per station owner per cooldown. A barred customer who
    /// clicks Buy ten times is told once; the refusal itself is not rate-limited, only the telling.
    /// </summary>
    public sealed class RefusalNoticeThrottle
    {
        private readonly Dictionary<string, DateTime> _lastSent = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when this (identity, owner) pair has not been told within
        /// <paramref name="cooldownSeconds"/>; records <paramref name="nowUtc"/> as the new stamp
        /// when it returns true. A cooldown of 0 or less tells every time.</summary>
        public bool ShouldNotify(long identityId, string ownerTag, DateTime nowUtc, double cooldownSeconds)
        {
            string key = identityId + "|" + (ownerTag ?? "");
            DateTime last;
            if (cooldownSeconds > 0 && _lastSent.TryGetValue(key, out last)
                && (nowUtc - last).TotalSeconds < cooldownSeconds)
                return false;

            // Bounded: a key per customer and owner that has ever been refused, so it cannot grow
            // past players × factions, but a long-running server should not keep stale stamps.
            if (_lastSent.Count > 4096) Prune(nowUtc, cooldownSeconds);
            _lastSent[key] = nowUtc;
            return true;
        }

        public int Count { get { return _lastSent.Count; } }

        private void Prune(DateTime nowUtc, double cooldownSeconds)
        {
            var stale = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in _lastSent)
                if ((nowUtc - kv.Value).TotalSeconds >= cooldownSeconds) stale.Add(kv.Key);
            foreach (string k in stale) _lastSent.Remove(k);
        }
    }
}
