using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Factions.Wire;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.Managers.ChatManager;
using VRage.Game;
using VRage.Game.Definitions.Reputation;
using VRage.Game.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Hails a pilot when an NPC faction detects their ship entering its space: once per entry,
    /// in a tone set by their standing with that faction. In chat, and, for a pilot whose client has
    /// the traffic voice on, spoken over the radio in the faction's own controller voice
    /// (<see cref="ControllerVoice"/>, <see cref="TrafficCallouts.SendHail"/>).
    ///
    /// <para><b>Its own pass, on the 5 s player cadence.</b> The faction-side detected-player set is
    /// rebuilt on the global pass, which ships at 180 s, so a hail riding it could arrive minutes
    /// after the border. The scanner pass is 5 s but skips a pilot without a scanner level, and
    /// whether a faction greets you cannot depend on your own scanner. This walks the seated pilots
    /// directly; per pilot it is one dominance read and one sensor collection for the faction whose
    /// space they are in, both linear in the node count.</para>
    ///
    /// <para>Detection is <see cref="PlayerSensing.Detect"/> over the faction's own sensors (estate
    /// plus convoys in flight), so a quiet ship can cross unseen, and is hailed the moment it is
    /// first seen.</para>
    ///
    /// <para>Provenance: FAC-483 slice 1.</para>
    /// </summary>
    internal sealed class TrespassHailer
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        /// <summary>A re-entry this soon after the last hail or the last exit is the same visit.
        /// A code constant until the behaviour has been seen live.</summary>
        internal const double RepeatGuardSeconds = 300.0;

        /// <summary>One seated, real pilot, as the plugin's per-frame player cache holds them.</summary>
        internal struct Pilot
        {
            public long IdentityId;
            public ulong SteamId;
            public long GridEntityId;
            public Vec3 PositionKm;
            /// <summary>0 = not measured, read as the reference 1.0 by <see cref="PlayerSensing.Detect"/>.</summary>
            public double Signature;
            public double BroadcastRadiusKm;
            /// <summary>The seat grid's speed, m/s: the faster of physics velocity and displacement since the
            /// last pass (<see cref="TravelSpeedBook"/>), so FSD supercruise does not read as stopped.</summary>
            public double SpeedMs;
            /// <summary>The seat grid has a connector locked to another grid.</summary>
            public bool Docked;
            /// <summary>The Nexus agent the pilot is connected to; 0 for this server. Their ship is an
            /// entity there, and what they are told goes there.</summary>
            public byte Server;
        }

        private readonly FactionRuntime _runtime;
        private readonly Action<string> _decisionLog;
        private ChatManagerServer _chat;
        private Func<ulong, WireTrafficEvent, string, SpatialNode, string, bool> _voice;
        private readonly TrespassHailBook _book = new TrespassHailBook();
        private readonly TrespassDwellBook _dwell = new TrespassDwellBook();
        private readonly TrespassCrossingBook _crossings = new TrespassCrossingBook();
        private readonly List<SensorSource> _sensors = new List<SensorSource>();
        private readonly List<PlayerContact> _one = new List<PlayerContact>(1);
        private readonly List<PlayerContact> _seen = new List<PlayerContact>(1);

        public TrespassHailer(FactionRuntime runtime, Action<string> decisionLog)
        {
            _runtime = runtime;
            _decisionLog = decisionLog;
        }

        public void AttachChat(ChatManagerServer chat) { _chat = chat; }

        /// <summary>Set on a Nexus authority: a chat line to a player connected to an agent (steam id,
        /// author, text, SE font name). False when they are not on one, and the line goes here.</summary>
        public Func<ulong, string, string, string, bool> RemoteChat { get; set; }

        /// <summary>One chat line to one player, wherever they are connected. False when it was not sent.</summary>
        private bool Say(ulong steamId, string author, string text, Color colour, string font, string what)
        {
            if (steamId == 0UL || text == null) return false;
            try
            {
                Func<ulong, string, string, string, bool> remote = RemoteChat;
                if (remote != null && remote(steamId, author, text, font)) return true;
                if (_chat == null) return false;
                _chat.SendMessageAsOther(author, text, colour, steamId);
                return true;
            }
            catch (Exception ex) { Log.Warn(ex, what + " to " + steamId + " failed."); return false; }
        }
        /// <summary>The radio voice: (steam id, hail, faction tag, node, node name) -> sent.</summary>
        public void AttachVoice(Func<ulong, WireTrafficEvent, string, SpatialNode, string, bool> voice) { _voice = voice; }

        private bool Speak(ulong steamId, WireTrafficEvent ev, string ownerTag, SpatialNode node, string author)
        {
            if (_voice == null || steamId == 0UL) return false;
            try { return _voice(steamId, ev, ownerTag, node, author); }
            catch (Exception ex) { Log.Warn(ex, "Trespass voice to " + steamId + " failed."); return false; }
        }

        private static WireTrafficEvent VoiceEventOf(HailTone tone)
        {
            switch (tone)
            {
                case HailTone.Welcome: return WireTrafficEvent.HailWelcome;
                case HailTone.Warning: return WireTrafficEvent.HailWarning;
                default: return WireTrafficEvent.HailNotice;
            }
        }

        /// <param name="pilots">Every seated real pilot this pass.</param>
        /// <param name="online">Every online real identity, seated or not: a pilot who stands up
        /// inside a faction's space has not left it.</param>
        public void Tick(IList<Pilot> pilots, ICollection<long> online)
        {
            if (_runtime == null || _runtime.World == null || pilots == null) return;
            var field = _runtime.World.Influence as InfluenceField;
            if (field == null) return;
            DateTime now = _runtime.World.UtcNow;
            FactionTuning tuning = _runtime.Tuning;

            for (int i = 0; i < pilots.Count; i++)
            {
                Pilot p = pilots[i];
                string rawSpace = TrespassPolicy.SpaceOwnerAt(field, p.PositionKm, tuning.TradeReachThreshold);
                string space = rawSpace;
                FactionState owner = space != null ? _runtime.FindFaction(space) : null;
                int seen = -1;
                // A player faction's estate has no voice to hail with: its humans speak for it.
                if (owner == null || owner.IsPlayerFaction) space = null;
                else if (!Detects(owner, p, now, tuning)) { space = null; seen = 0; }
                else seen = 1;
                NoteCrossing(p, new TrespassCrossingBook.Where(rawSpace, owner != null && owner.IsPlayerFaction, seen));

                Standing standing = default(Standing);
                bool read = false;
                if (space != null)
                {
                    standing = ReadStanding(owner, p.IdentityId);
                    read = true;
                    MarkIfIntruder(owner, p, standing, field, now);
                }

                if (!_book.Observe(p.IdentityId, space, now, RepeatGuardSeconds)) continue;
                Hail(owner, p, read ? standing : ReadStanding(owner, p.IdentityId));
            }
            _book.Prune(online, now, RepeatGuardSeconds);
            _dwell.Prune(now);
            List<KeyValuePair<long, TrespassCrossingBook.Where>> loggedOut = _crossings.Prune(online);
            if (DebugTraces.Trespass && _decisionLog != null)
                for (int i = 0; i < loggedOut.Count; i++)
                    _decisionLog(TrespassCrossingBook.Line(loggedOut[i].Key, 0, loggedOut[i].Value,
                        TrespassCrossingBook.Where.Nowhere, null, null, "offline"));
        }

        /// <summary>Tracks the pilot's raw space every pass, so the first line after
        /// <c>factions debug trespass</c> is switched on reports a real change and not a first
        /// sighting. The line itself is built only while the trace is on.</summary>
        private void NoteCrossing(Pilot p, TrespassCrossingBook.Where now)
        {
            TrespassCrossingBook.Where was;
            if (!_crossings.Observe(p.IdentityId, now, out was)) return;
            if (DebugTraces.Trespass && _decisionLog != null)
                _decisionLog(TrespassCrossingBook.Line(p.IdentityId, p.GridEntityId, was, now, p.PositionKm,
                    p.SpeedMs, "move"));
        }

        private bool Detects(FactionState owner, Pilot p, DateTime now, FactionTuning tuning)
        {
            _sensors.Clear();
            PlayerSensing.CollectFactionSensors(_runtime.World.Graph, owner, tuning, now, _sensors);
            _one.Clear();
            _one.Add(new PlayerContact
            {
                IdentityId = p.IdentityId,
                GridEntityId = p.GridEntityId,
                PositionKm = p.PositionKm,
                Signature = p.Signature,
                BroadcastRadiusKm = p.BroadcastRadiusKm
            });
            return PlayerSensing.Detect(_sensors, _one, _seen, _runtime.SensorOcclusion) > 0;
        }

        /// <summary>A pilot's standing with one faction, as SE holds it.</summary>
        private struct Standing
        {
            public string Band;
            public int Rep;
            public bool Member;
            public string FactionName;
            /// <summary>SE's faction type name (<c>Pirate</c>, <c>Trader</c>, ...), or null.</summary>
            public string SeFactionType;
        }

        private static Standing ReadStanding(FactionState owner, long identityId)
        {
            var s = new Standing { Band = "Neutral", FactionName = owner.Tag };
            MyFactionCollection factions = MySession.Static != null ? MySession.Static.Factions : null;
            if (factions == null) return s;
            MyFaction se = factions.TryGetFactionByTag(owner.Tag);
            if (se != null)
            {
                if (!string.IsNullOrEmpty(se.Name)) s.FactionName = se.Name;
                s.SeFactionType = se.FactionType.ToString();
                Tuple<MyRelationsBetweenFactions, int> rel =
                    factions.GetRelationBetweenPlayerAndFaction(identityId, se.FactionId);
                if (rel != null) { s.Band = rel.Item1.ToString(); s.Rep = rel.Item2; }
            }
            MyFaction own = factions.GetPlayerFaction(identityId);
            s.Member = own != null && string.Equals(own.Tag, owner.Tag, StringComparison.OrdinalIgnoreCase);
            return s;
        }

        /// <summary>Marks the faction's nearest node intruded while an enemy ship is seen in its space,
        /// on every pass, so the mark lives as long as the sighting, at the rung the ship's stay has
        /// reached (<see cref="TrespassLadder"/>). One line per fresh mark, and a warning and a line
        /// when the stay reaches Intercept.</summary>
        private void MarkIfIntruder(FactionState owner, Pilot p, Standing standing, InfluenceField field, DateTime now)
        {
            if (!TrespassPolicy.IsIntruder(TrespassPolicy.ToneFor(standing.Band, standing.Member))) return;
            SpatialNode node = TrespassPolicy.NearestOwnedNode(_runtime.World.Graph, owner.Tag, p.PositionKm);
            if (node == null) return;

            double dwell = _dwell.Observe(p.IdentityId, owner.Tag, now);
            Personality personality = Personalities.Get(owner.PersonalityId);
            double control = field.Control(p.PositionKm, owner.Tag);
            double shadowS = TrespassLadder.ShadowSeconds(personality.Traits.Aggression, control, standing.Rep,
                TrespassLadder.StartsHostile(personality, standing.SeFactionType));
            TrespassRung rung = TrespassLadder.RungFor(dwell, shadowS);

            if (_runtime.World.MarkIntruder(node.Id, rung, p.GridEntityId) && _decisionLog != null)
                _decisionLog("TRESPASS_INTRUDER " + owner.Tag
                             + " node=" + node.Id
                             + " identity=" + p.IdentityId
                             + " eid=" + p.GridEntityId
                             + " band=" + standing.Band
                             + " rep=" + standing.Rep
                             + " distKm=" + node.Position.DistanceTo(p.PositionKm).ToString("0.#", CultureInfo.InvariantCulture)
                             + " shadowS=" + shadowS.ToString("0", CultureInfo.InvariantCulture)
                             + " thr->" + node.Threat.ToString("0.00", CultureInfo.InvariantCulture));

            ChargeLingering(owner, p, standing, node, rung, now);

            if (!_dwell.StepTo(p.IdentityId, owner.Tag, rung) || rung != TrespassRung.Intercept) return;
            bool sent = false;
            string text = TrespassLadder.InterceptWarning(standing.FactionName);
            string warner = TrespassPolicy.HailAuthor(node, standing.FactionName);
            sent = Say(p.SteamId, warner, text, ColourOf(HailTone.Warning), FontOf(HailTone.Warning), "Trespass warning");
            bool voiced = Speak(p.SteamId, WireTrafficEvent.HailIntercept, owner.Tag, node, warner);
            if (_decisionLog != null)
                _decisionLog("TRESPASS_RUNG " + owner.Tag
                             + " node=" + node.Id
                             + " identity=" + p.IdentityId
                             + " eid=" + p.GridEntityId
                             + " rung=intercept"
                             + " dwellS=" + dwell.ToString("0", CultureInfo.InvariantCulture)
                             + " shadowS=" + shadowS.ToString("0", CultureInfo.InvariantCulture)
                             + " control=" + control.ToString("0.00", CultureInfo.InvariantCulture)
                             + " rep=" + standing.Rep
                             + " sent=" + (sent ? "1" : "0")
                             + " voice=" + (voiced ? "1" : "0"));
        }

        /// <summary>Charges reputation for lingering after the Intercept warning:
        /// <see cref="TrespassLadder.LingerStepReputation"/> per full
        /// <see cref="TrespassLadder.LingerStepSeconds"/> of slow, undocked time in one stay
        /// (<see cref="TrespassDwellBook.Linger"/>). Reason <c>Hostility</c>: the pilot's own doing, shown
        /// on their HUD and captured as conduct, so it reaches a player faction's standing the way
        /// damage does. Only ever reached for an Enemies-band pilot, so it deepens an enmity and never
        /// starts one.</summary>
        private void ChargeLingering(FactionState owner, Pilot p, Standing standing, SpatialNode node,
                                     TrespassRung rung, DateTime now)
        {
            bool lingering = TrespassLadder.IsLingering(rung, p.SpeedMs, p.Docked);
            int steps = _dwell.Linger(p.IdentityId, owner.Tag, now, lingering);
            if (steps <= 0) return;

            int delta = -TrespassLadder.LingerStepReputation * steps;
            bool applied = false;
            MyFactionCollection factions = MySession.Static != null ? MySession.Static.Factions : null;
            MyFaction se = factions != null ? factions.TryGetFactionByTag(owner.Tag) : null;
            if (se != null)
            {
                try
                {
                    applied = factions.AddFactionPlayerReputation(p.IdentityId, se.FactionId, delta,
                        ReputationChangeReason.Hostility, propagate: false);
                }
                catch (Exception ex) { Log.Warn(ex, "Trespass reputation charge to " + p.IdentityId + " failed."); }
            }
            if (_decisionLog != null)
                _decisionLog("TRESPASS_REP " + owner.Tag
                             + " node=" + node.Id
                             + " identity=" + p.IdentityId
                             + " eid=" + p.GridEntityId
                             + " delta=" + delta
                             + " rep=" + standing.Rep
                             + " lingerS=" + _dwell.LingerSeconds(p.IdentityId, owner.Tag).ToString("0", CultureInfo.InvariantCulture)
                             + " applied=" + (applied ? "1" : "0")
                             + ServerSuffix(p));
        }

        private void Hail(FactionState owner, Pilot p, Standing standing)
        {
            string band = standing.Band;
            int rep = standing.Rep;
            string factionName = standing.FactionName;

            HailTone tone = TrespassPolicy.ToneFor(band, standing.Member);
            if (tone == HailTone.None) return;
            string text = TrespassPolicy.HailText(tone, factionName);

            SpatialNode station = TrespassPolicy.NearestOwnedNode(_runtime.World.Graph, owner.Tag, p.PositionKm);
            string author = TrespassPolicy.HailAuthor(station, factionName);
            bool sent = false;
            sent = Say(p.SteamId, author, text, ColourOf(tone), FontOf(tone), "Trespass hail");
            bool voiced = text != null && station != null
                          && Speak(p.SteamId, VoiceEventOf(tone), owner.Tag, station, author);

            if (_decisionLog != null)
                _decisionLog("TRESPASS_HAIL " + owner.Tag
                             + " identity=" + p.IdentityId
                             + " steam=" + p.SteamId
                             + " eid=" + p.GridEntityId
                             + " tone=" + TrespassPolicy.ToneName(tone)
                             + " band=" + band
                             + " rep=" + rep
                             + " node=" + (station != null ? station.Id : "-")
                             + " distKm=" + (station != null
                                 ? station.Position.DistanceTo(p.PositionKm).ToString("0.#", CultureInfo.InvariantCulture)
                                 : "-")
                             + " sig=" + p.Signature.ToString("0.###", CultureInfo.InvariantCulture)
                             + " sent=" + (sent ? "1" : "0")
                             + " voice=" + (voiced ? "1" : "0")
                             + ServerSuffix(p));
        }

        private static string ServerSuffix(Pilot p)
        {
            return p.Server != 0 ? " server=" + p.Server : "";
        }

        /// <summary>One step of a player's occupation of this faction's ground
        /// (<see cref="PlayerSiegeManager.Occupied"/>): every hold strip is hailed to whoever is on the
        /// node and charged against them, and the end is filed as a fight the owner can retaliate for
        /// (<see cref="FightLedger.RecordOccupation"/>). Not subject to the once-per-entry guard: the
        /// pilot has usually been hailed on entering the space, and occupying it is a new act. A player
        /// faction's ground is skipped -- its humans speak for it.</summary>
        public void OnOccupation(OccupationNotice e)
        {
            if (_runtime == null || _runtime.World == null || e.Node == null) return;
            FactionState owner = _runtime.FindFaction(e.OwnerTag);
            if (owner == null || owner.IsPlayerFaction) return;
            if (e.Ended) RememberOccupation(owner, e);
            else HailOccupier(owner, e);
        }

        private void HailOccupier(FactionState owner, OccupationNotice e)
        {
            long holder = e.HolderIdentityId;
            Standing standing = ReadStanding(owner, holder);
            string text = Occupation.HailText(e.Strip, standing.FactionName);
            if (text == null) return;
            string author = TrespassPolicy.HailAuthor(e.Node, standing.FactionName);
            ulong steamId = SteamIdOf(holder);

            bool sent = false;
            sent = Say(steamId, author, text, Color.OrangeRed, FontOf(HailTone.Warning), "Occupation hail");
            WireTrafficEvent ev = Occupation.IsFirstStrip(e.Strip)
                ? WireTrafficEvent.HailOccupation : WireTrafficEvent.HailOccupationHeld;
            bool voiced = Speak(steamId, ev, owner.Tag, e.Node, author);

            if (_decisionLog != null)
                _decisionLog("TRESPASS_HAIL " + owner.Tag
                             + " identity=" + holder
                             + " steam=" + steamId
                             + " tone=occupation"
                             + " strip=" + e.Strip
                             + " band=" + standing.Band
                             + " rep=" + standing.Rep
                             + " node=" + e.Node.Id
                             + " sent=" + (sent ? "1" : "0")
                             + " voice=" + (voiced ? "1" : "0"));

            ChargeOccupation(owner, e, standing);
        }

        /// <summary>One strip's reputation: what SE would have charged for the block damage the same
        /// strip costs on the station path (<see cref="Occupation.ReputationPerStrip"/>, read from SE's
        /// own reputation settings). Personal, <c>propagate: false</c>, reason <c>Hostility</c>, the
        /// way lingering is charged: it reaches the occupier's faction through the roster aggregate
        /// rather than as a second writer.</summary>
        private void ChargeOccupation(FactionState owner, OccupationNotice e, Standing standing)
        {
            int perStrip = OccupationReputationPerStrip();
            if (perStrip <= 0) return;
            int delta = -perStrip;
            bool applied = false;
            MyFactionCollection factions = MySession.Static != null ? MySession.Static.Factions : null;
            MyFaction se = factions != null ? factions.TryGetFactionByTag(owner.Tag) : null;
            if (se != null)
            {
                try
                {
                    applied = factions.AddFactionPlayerReputation(e.HolderIdentityId, se.FactionId, delta,
                        ReputationChangeReason.Hostility, propagate: false);
                }
                catch (Exception ex) { Log.Warn(ex, "Occupation reputation charge to " + e.HolderIdentityId + " failed."); }
            }
            if (_decisionLog != null)
                _decisionLog("TRESPASS_REP " + owner.Tag
                             + " node=" + e.Node.Id
                             + " identity=" + e.HolderIdentityId
                             + " cause=occupation"
                             + " strip=" + e.Strip
                             + " delta=" + delta
                             + " rep=" + standing.Rep
                             + " applied=" + (applied ? "1" : "0"));
        }

        private int OccupationReputationPerStrip()
        {
            try
            {
                MyReputationSettingsDefinition settings = MyDefinitionManager.Static != null
                    ? MyDefinitionManager.Static.GetDefinition<MyReputationSettingsDefinition>("DefaultReputationSettings")
                    : null;
                if (settings == null) return 0;
                return Occupation.ReputationPerStrip(_runtime.Tuning.PlayerRaidBlockDamageForSuccess,
                    settings.DamageSettings.ReputationLossDamage, settings.DamageSettings.Damaging);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Reading SE's reputation settings failed; occupation charges nothing.");
                return 0;
            }
        }

        /// <summary>Files an ended occupation as a fight against the owner, when the occupier's faction
        /// is registered, and logs it.</summary>
        private void RememberOccupation(FactionState owner, OccupationNotice e)
        {
            FactionState attacker = e.AttackerTag != null ? _runtime.FindFaction(e.AttackerTag) : null;
            FactionTuning tuning = _runtime.Tuning;
            bool recorded = FightLedger.RecordOccupation(owner, attacker, e.Node, e.Taken,
                _runtime.World.UtcNow, tuning != null ? tuning.FightHistoryCap : 1);
            if (_decisionLog != null)
                _decisionLog("OCCUPATION_ENDED " + owner.Tag
                             + " node=" + e.Node.Id
                             + " attacker=" + (e.AttackerTag ?? "-")
                             + " identity=" + e.HolderIdentityId
                             + " strips=" + e.Strip
                             + " taken=" + (e.Taken ? "1" : "0")
                             + " recorded=" + (recorded ? "1" : "0"));
        }

        private static ulong SteamIdOf(long identityId)
        {
            if (identityId == 0 || MySession.Static == null || MySession.Static.Players == null) return 0UL;
            try { return MySession.Static.Players.TryGetSteamId(identityId); }
            catch { return 0UL; }
        }

        /// <summary>The SE font a hail's tone reads in on an agent, where it is shown as a notice.</summary>
        private static string FontOf(HailTone tone)
        {
            switch (tone)
            {
                case HailTone.Welcome: return "Green";
                case HailTone.Warning: return "Red";
                default: return "Blue";
            }
        }

        private static Color ColourOf(HailTone tone)
        {
            switch (tone)
            {
                case HailTone.Welcome: return Color.LightGreen;
                case HailTone.Warning: return Color.OrangeRed;
                default: return Color.CornflowerBlue;
            }
        }
    }
}
