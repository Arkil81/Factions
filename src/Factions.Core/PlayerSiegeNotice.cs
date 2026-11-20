using System;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// What a player besieging a station is told, as text. The siege's clocks are all Core's
    /// (<see cref="PlayerSiege"/>), so the words that read them out live beside them: a retuned dwell
    /// or regen time changes the countdown and the instructions in the same place.
    ///
    /// <para>The shield's colour alone cannot carry this. It says the pool is low; it cannot say
    /// that the shield breaks only after the dwell, that a lull refills it, or how long is left —
    /// and without those a player who empties the pool once and waits loses the siege having been
    /// told nothing.</para>
    /// </summary>
    public static class PlayerSiegeNotice
    {
        /// <summary>The first fire of a fresh siege: what happened and what the attacker has to do
        /// next. Sent once, to chat, where it can be read back.</summary>
        public static string Began(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null) return null;
            return "You opened a siege on " + Where(n) + ". Its shield absorbs your fire: keep it "
                 + "under fire for " + Clock(t.RaidZoneSiegeDwellSeconds) + " to break it. Stop and it "
                 + "recovers within " + Seconds(t.RaidZoneIntegrityRegenSeconds) + "; left recovered for "
                 + Seconds(t.RaidZoneRaidLapseSeconds) + " and the siege is lost. "
                 + Owner(n) + " will send defenders.";
        }

        /// <summary>The flush that took the shield down: phase 2, and what wins it.</summary>
        public static string Breached(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null) return null;
            return "The shield of " + Where(n) + " is down and its turrets are live. Do "
                 + Amount(t.PlayerRaidBlockDamageForSuccess) + " damage to the station, or knock out "
                 + "its power, to raid it. Stay within " + Km(t.ObservationRadiusKm)
                 + " or the shield comes back.";
        }

        /// <summary>The first success of a raid: both conditions that stand between the attacker and
        /// the node, with their numbers. Sent before this success's hold strip lands, so it states the
        /// rule rather than a hold reading that is about to change.</summary>
        public static string RaidSucceeded(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null) return null;
            double strip = t.RaidHoldStrip(1.0);
            return "Raid on " + Where(n) + " succeeded. The node is offered to you once both hold: its "
                 + "hold is down to " + Percent(t.ConquerHoldCeiling) + " or less"
                 + (strip > 0 ? " (every further " + Amount(t.PlayerRaidBlockDamageForSuccess)
                              + " damage strips " + Percent(strip) + ")" : "")
                 + ", and " + Clock(t.PlayerConquerOfferMinutes * 60.0) + " has passed. Stay within "
                 + Km(t.ObservationRadiusKm) + " or the shield comes back.";
        }

        /// <summary>An abandoned siege, reset to zero.</summary>
        public static string Lapsed(SpatialNode n)
        {
            if (n == null) return null;
            return "Siege on " + Where(n) + " lost: the shield recovered and stood without fire. "
                 + "A new siege starts from the beginning.";
        }

        /// <summary>One short status line for the HUD while a station siege runs, or null when
        /// there is none. Most urgent instruction first: a recovered shield about to lapse the siege
        /// outranks the countdown to the breach.</summary>
        public static string Status(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || t == null || !PlayerSiege.HasStationSiege(n)) return null;

            switch (PlayerSiege.StateOf(n, now))
            {
                case PlayerSiege.ZoneState.Raided:
                    return Raided(n, t, now);

                case PlayerSiege.ZoneState.Breached:
                    double need = t.PlayerRaidBlockDamageForSuccess;
                    return "Shield DOWN at " + Where(n) + ": station damage "
                         + Amount(Math.Min(n.PlayerRaidBlockDamage, need)) + " / " + Amount(need)
                         + ", or knock out its power.";

                case PlayerSiege.ZoneState.Besieged:
                    double fraction = PlayerSiege.IntegrityFraction(n, t, now);
                    if (fraction >= 1.0)
                    {
                        double lapseLeft = t.RaidZoneRaidLapseSeconds;
                        if (n.SiegeLapseSinceUtc != default(DateTime))
                            lapseLeft -= (now - n.SiegeLapseSinceUtc).TotalSeconds;
                        return "Shield of " + Where(n) + " recovered. Fire within "
                             + Seconds(Math.Max(0.0, lapseLeft)) + " or the siege is lost.";
                    }
                    double dwellLeft = (PlayerSiege.BreachDueUtc(n, t) - now).TotalSeconds;
                    if (dwellLeft > 0)
                        return "Siege on " + Where(n) + ": shield " + Percent(fraction)
                             + ". Keep it under fire " + Clock(dwellLeft) + " more to break it.";
                    return "Siege on " + Where(n) + ": shield " + Percent(fraction)
                         + ". Empty it now to break it.";

                default:
                    return null;
            }
        }

        /// <summary>The raided phase names both offer conditions every time, with progress on each:
        /// the hold against <see cref="FactionTuning.ConquerHoldCeiling"/>, with the rounds of damage
        /// still needed and the next round's progress, and the wait since the first success. Showing
        /// only the condition still unmet reads as the goal changing under the player.</summary>
        private static string Raided(SpatialNode n, FactionTuning t, DateTime now)
        {
            double hold = Math.Max(0.0, n.HoldStrength);
            double ceiling = t.ConquerHoldCeiling;
            double offerLeft = (n.PlayerRaidSucceededUtc.AddMinutes(t.PlayerConquerOfferMinutes) - now).TotalSeconds;
            bool holdMet = hold <= ceiling;
            bool timeMet = offerLeft <= 0;
            if (holdMet && timeMet)
                return "Raided " + Where(n) + ": reply \"yes\" in chat to take the node.";

            string holdPart;
            if (holdMet)
                holdPart = "hold " + Percent(hold) + ", done";
            else
            {
                double strip = t.RaidHoldStrip(1.0);
                double need = t.PlayerRaidBlockDamageForSuccess;
                holdPart = "hold " + Percent(hold) + ", needs " + Percent(ceiling) + " or less";
                if (strip > 0 && need > 0)
                {
                    int rounds = Math.Max(1, (int)Math.Ceiling((hold - ceiling) / strip - 1e-9));
                    holdPart += " (about " + rounds.ToString(CultureInfo.InvariantCulture)
                              + (rounds == 1 ? " more round" : " more rounds") + "; next "
                              + Amount(Math.Min(n.PlayerRaidBlockDamage, need)) + " / " + Amount(need)
                              + " damage)";
                }
            }
            string timePart = timeMet ? "wait done" : "wait " + Clock(offerLeft) + " more";
            return "Raided " + Where(n) + ". To be offered the node: " + holdPart + "; " + timePart + ".";
        }

        // ---- presence raids: a node with no station, taken by standing on it -------------------

        /// <summary>A presence raid has begun (<see cref="PlayerSiege.BeginPresenceRaid"/>): what the
        /// attacker is doing, the two offer conditions, and that leaving loses it. Sent once, to chat.
        /// The opening strip has already landed, so this states the rule, not a hold reading.</summary>
        public static string PresenceBegan(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null) return null;
            double strip = t.RaidHoldStrip(1.0);
            return "You are occupying " + Ground(n) + ". There is no station to fight: holding position "
                 + "is the attack. The node is offered to you once its hold is down to "
                 + Percent(t.ConquerHoldCeiling) + " or less"
                 + (strip > 0 && t.RaidZoneSiegeDwellSeconds > 0
                     ? " (every " + Clock(t.RaidZoneSiegeDwellSeconds) + " you stay strips " + Percent(strip) + ")"
                     : "")
                 + " and " + Clock(t.PlayerConquerOfferMinutes * 60.0) + " has passed. Stay within "
                 + Km(PresenceHoldKm(t)) + "; leave and the occupation is lost. "
                 + Owner(n) + " will send defenders.";
        }

        /// <summary>A presence raid nobody is holding any more, reset to zero.</summary>
        public static string PresenceLapsed(SpatialNode n)
        {
            if (n == null) return null;
            return "Occupation of " + Ground(n) + " lost: you left. A new one starts from the beginning.";
        }

        /// <summary>One HUD line while a presence raid is held, or null when there is none. Names both
        /// offer conditions every time, as the station path's raided line does, with the countdown to
        /// the next hold strip in place of the damage still owed.</summary>
        public static string PresenceStatus(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || t == null || !PlayerSiege.IsPresenceRaid(n)) return null;

            double hold = Math.Max(0.0, n.HoldStrength);
            double ceiling = t.ConquerHoldCeiling;
            double offerLeft = (n.PlayerRaidSucceededUtc.AddMinutes(t.PlayerConquerOfferMinutes) - now).TotalSeconds;
            bool holdMet = hold <= ceiling;
            bool timeMet = offerLeft <= 0;
            if (holdMet && timeMet)
                return "Occupying " + Ground(n) + ": reply \"yes\" in chat to take the node.";

            string holdPart;
            if (holdMet)
                holdPart = "hold " + Percent(hold) + ", done";
            else
            {
                holdPart = "hold " + Percent(hold) + ", needs " + Percent(ceiling) + " or less";
                double strip = t.RaidHoldStrip(1.0);
                double interval = t.RaidZoneSiegeDwellSeconds;
                if (strip > 0 && interval > 0)
                {
                    int strips = Math.Max(1, (int)Math.Ceiling((hold - ceiling) / strip - 1e-9));
                    DateTime last = n.PlayerPresenceStripUtc != default(DateTime) ? n.PlayerPresenceStripUtc : now;
                    double nextIn = Math.Max(0.0, (last.AddSeconds(interval) - now).TotalSeconds);
                    holdPart += " (about " + strips.ToString(CultureInfo.InvariantCulture)
                              + (strips == 1 ? " more strip" : " more strips") + "; next in " + Clock(nextIn) + ")";
                }
            }
            string timePart = timeMet ? "wait done" : "wait " + Clock(offerLeft) + " more";
            return "Occupying " + Ground(n) + ". To be offered the node: " + holdPart + "; " + timePart
                 + ". Stay within " + Km(PresenceHoldKm(t)) + ".";
        }

        /// <summary>The owner gave the ground up while it was under a presence raid or a siege: the
        /// raid is over and the node is anyone's to claim. Names the prior owner, because the node's
        /// own owner tag is the neutral one by the time this is told.</summary>
        public static string AbandonedUnderRaid(string nodeId, string priorOwnerTag)
        {
            return (string.IsNullOrEmpty(priorOwnerTag) ? "Its owner" : priorOwnerTag) + " abandoned "
                 + nodeId + " under your attack. The node is unclaimed now: accept the claim offer to "
                 + "take it.";
        }

        // ---- the HUD status panel: the same states, as a few short lines ----------------------

        /// <summary>The status panel key for a node's siege or occupation. One per node: a node is
        /// either a station under siege or ground under occupation, never both.</summary>
        public static string PanelKey(string nodeId)
        {
            return "siege:" + nodeId;
        }

        /// <summary>A station siege as a panel item, or null when there is none. Same phases and the
        /// same urgency order as <see cref="Status"/>.</summary>
        public static StatusPanelItem StatusPanel(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || t == null || !PlayerSiege.HasStationSiege(n)) return null;
            string key = PanelKey(n.Id);
            string at = n.Id + " (" + Owner(n) + ")";

            switch (PlayerSiege.StateOf(n, now))
            {
                case PlayerSiege.ZoneState.Raided:
                    return RaidedPanel(n, t, now, key, "RAIDED  " + at, t.ObservationRadiusKm, false);

                case PlayerSiege.ZoneState.Breached:
                    double need = t.PlayerRaidBlockDamageForSuccess;
                    return new StatusPanelItem(key, StatusPanelTone.Pending)
                        .Add("SHIELD DOWN  " + at)
                        .Add("station damage " + Amount(Math.Min(n.PlayerRaidBlockDamage, need)) + " / " + Amount(need))
                        .Add("or knock out its power")
                        .Add("stay within " + Km(t.ObservationRadiusKm));

                case PlayerSiege.ZoneState.Besieged:
                    double fraction = PlayerSiege.IntegrityFraction(n, t, now);
                    if (fraction >= 1.0)
                    {
                        var lost = new StatusPanelItem(key, StatusPanelTone.Danger)
                            .Add("SIEGE  " + at)
                            .Add("shield recovered");
                        if (n.SiegeLapseSinceUtc != default(DateTime))
                            lost.Add("fire within " + StatusPanelItem.ClockToken + " or the siege is lost",
                                     n.SiegeLapseSinceUtc.AddSeconds(t.RaidZoneRaidLapseSeconds));
                        else
                            lost.Add("fire within " + Seconds(t.RaidZoneRaidLapseSeconds) + " or the siege is lost");
                        return lost;
                    }
                    var siege = new StatusPanelItem(key, StatusPanelTone.Pending)
                        .Add("SIEGE  " + at)
                        .Add("shield " + PercentBand(fraction));
                    DateTime due = PlayerSiege.BreachDueUtc(n, t);
                    if (due > now) siege.Add("keep it under fire " + StatusPanelItem.ClockToken + " to break it", due);
                    else siege.Add("empty it now to break it");
                    return siege;

                default:
                    return null;
            }
        }

        /// <summary>A presence raid as a panel item, or null when there is none. Same two offer
        /// conditions as <see cref="PresenceStatus"/>, with the next strip counted down.</summary>
        public static StatusPanelItem PresencePanel(SpatialNode n, FactionTuning t, DateTime now)
        {
            if (n == null || t == null || !PlayerSiege.IsPresenceRaid(n)) return null;
            return RaidedPanel(n, t, now, PanelKey(n.Id), "OCCUPYING  " + n.Id + " (" + Owner(n) + ")",
                               PresenceHoldKm(t), true);
        }

        /// <summary>The raided phase, shared by a station raid and a presence raid: the hold against
        /// the ceiling with what still has to happen to it, and the wait. Both conditions every time,
        /// for the reason <see cref="Raided"/> gives.</summary>
        private static StatusPanelItem RaidedPanel(SpatialNode n, FactionTuning t, DateTime now, string key,
                                                   string heading, double stayKm, bool presence)
        {
            double hold = Math.Max(0.0, n.HoldStrength);
            double ceiling = t.ConquerHoldCeiling;
            DateTime offerAt = n.PlayerRaidSucceededUtc.AddMinutes(t.PlayerConquerOfferMinutes);
            bool holdMet = hold <= ceiling;
            bool timeMet = now >= offerAt;

            if (holdMet && timeMet)
                return new StatusPanelItem(key, StatusPanelTone.Ready)
                    .Add("READY  " + n.Id + " (" + Owner(n) + ")")
                    .Add("reply \"yes\" in chat to take the node")
                    .Add("stay within " + Km(stayKm));

            var item = new StatusPanelItem(key, StatusPanelTone.Pending).Add(heading);
            item.Add(holdMet
                ? "hold " + Percent(hold) + ", done"
                : "hold " + Percent(hold) + ", needs " + Percent(ceiling) + " or less");

            double strip = t.RaidHoldStrip(1.0);
            if (!holdMet && strip > 0)
            {
                int left = Math.Max(1, (int)Math.Ceiling((hold - ceiling) / strip - 1e-9));
                string count = " (" + left.ToString(CultureInfo.InvariantCulture) + " left)";
                if (presence && t.RaidZoneSiegeDwellSeconds > 0)
                {
                    DateTime last = n.PlayerPresenceStripUtc != default(DateTime)
                        ? n.PlayerPresenceStripUtc : n.PlayerRaidSucceededUtc;
                    item.Add("next strip in " + StatusPanelItem.ClockToken + count,
                             last.AddSeconds(t.RaidZoneSiegeDwellSeconds));
                }
                else if (!presence && t.PlayerRaidBlockDamageForSuccess > 0)
                {
                    double need = t.PlayerRaidBlockDamageForSuccess;
                    item.Add("next round " + Amount(Math.Min(n.PlayerRaidBlockDamage, need)) + " / "
                             + Amount(need) + " damage" + count);
                }
            }

            if (timeMet) item.Add("wait done");
            else item.Add("offer in " + StatusPanelItem.ClockToken, offerAt);
            item.Add("stay within " + Km(stayKm));
            return item;
        }

        /// <summary>Whole percent in steps of five: a shield under fire moves every frame, and a
        /// panel that resent on every point would be the five-second notice again.</summary>
        private static string PercentBand(double fraction)
        {
            double f = Math.Max(0.0, Math.Min(1.0, fraction));
            return (Math.Round(f * 20.0) * 5.0).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>The radius a presence raid is held within once begun: the start radius plus its
        /// hysteresis.</summary>
        public static double PresenceHoldKm(FactionTuning t)
        {
            return t == null ? 0.0 : t.PresenceRaidRadiusKm + Math.Max(0.0, t.PresenceRaidHysteresisKm);
        }

        private static string Where(SpatialNode n)
        {
            return Owner(n) + "'s station at " + n.Id;
        }

        private static string Ground(SpatialNode n)
        {
            return Owner(n) + "'s ground at " + n.Id;
        }

        private static string Owner(SpatialNode n)
        {
            return string.IsNullOrEmpty(n.OwnerFactionTag) ? "Its owner" : n.OwnerFactionTag;
        }

        private static string Clock(double seconds)
        {
            int s = (int)Math.Ceiling(Math.Max(0.0, seconds));
            return (s / 60).ToString(CultureInfo.InvariantCulture) + ":"
                 + (s % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Seconds(double seconds)
        {
            return Math.Ceiling(Math.Max(0.0, seconds)).ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        private static string Percent(double fraction)
        {
            return Math.Round(Math.Max(0.0, Math.Min(1.0, fraction)) * 100.0)
                       .ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static string Amount(double value)
        {
            return Math.Round(Math.Max(0.0, value)).ToString("#,0", CultureInfo.InvariantCulture);
        }

        private static string Km(double km)
        {
            return km.ToString("0.#", CultureInfo.InvariantCulture) + " km";
        }
    }
}
