using System;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// The rules for scaling missile-class munition speed, kept SE-free so the host, the client
    /// plugin's arithmetic and the tests read one implementation.
    ///
    /// <para><b>Why a scale exists at all.</b> SE's ammo content is tuned against a 100 m/s grid
    /// speed limit. A server that raises <see cref="FactionTuning.GridMaxSpeedLarge"/> to 500 does
    /// not move the ammo with it, and <c>MissileAmmoDefinition</c> rounds are hard-capped at their
    /// own <c>DesiredSpeed</c> — vanilla artillery and the assault cannon both fire at exactly the
    /// large-grid limit, so a round chasing a receding grid closes at zero. This scale multiplies
    /// the missile definitions back into relation with the world they are fired in.</para>
    ///
    /// <para><b>It multiplies rather than floors on purpose.</b> Munition speeds carry a design
    /// ordering — a 150 m/s torpedo is slow so point defence can engage it, a 2000 m/s railgun slug
    /// is fast because that is its whole character. A floor would flatten that ordering; a
    /// multiplier preserves it.</para>
    ///
    /// <para><b>Acceleration scales by the square</b> — see <see cref="ScaleAcceleration"/>. Range
    /// (<c>MaxTrajectory</c>) is a distance and is deliberately untouched: this changes how long a
    /// round takes to arrive, not how far it reaches.</para>
    /// </summary>
    public static class MunitionSpeedPolicy
    {
        /// <summary>Sentinel meaning "leave the definitions as they loaded". One rather than zero
        /// because this is a multiplier — the value that changes nothing is its identity, and it
        /// round-trips through a JSON file that has never heard of the key.</summary>
        public const double NoScale = 1.0;

        /// <summary>Upper bound on the operator's multiplier. A scaled round travels
        /// <c>speed / 60</c> metres per physics frame and relies on Havok's bullet-quality
        /// continuous collision to hit anything at all; SE itself ships nothing faster than the
        /// 2000 m/s railgun slug (33 m/frame), so this is a guard rail rather than a measured
        /// ceiling — 20x the fastest vanilla round is already well past what anyone has flown.</summary>
        public const double MaxScale = 20.0;

        /// <summary>True when a persisted tuning value asks for anything other than the definitions
        /// as loaded.</summary>
        public static bool IsOverride(double tuningValue)
        {
            if (double.IsNaN(tuningValue) || tuningValue <= 0.0) return false;
            return Math.Abs(tuningValue - NoScale) > 1e-9;
        }

        /// <summary>Brings a value into the range this will actually apply. A non-positive or
        /// non-finite value becomes <see cref="NoScale"/> rather than zero: a zero multiplier would
        /// stop every round dead in the air, which is never what a malformed value meant.</summary>
        public static double Clamp(double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0) return NoScale;
            if (scale > MaxScale) return MaxScale;
            return scale;
        }

        /// <summary>
        /// Parses an operator-supplied multiplier. Culture-invariant on purpose: the value crosses a
        /// chat command, a file bridge and a JSON file, and a decimal comma in any one of them would
        /// make the same text mean different speeds on different hosts.
        /// </summary>
        /// <param name="error">Null on success; a player-facing reason on failure.</param>
        public static bool TryParseScale(string text, out double scale, out string error)
        {
            scale = NoScale;
            error = null;
            if (string.IsNullOrEmpty(text)) { error = "no scale given"; return false; }

            double parsed;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                error = "'" + text + "' is not a number";
                return false;
            }
            if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                error = "'" + text + "' is not a finite number";
                return false;
            }
            if (parsed <= 0.0)
            {
                error = "scale must be greater than 0 (1 = the definitions as they loaded)";
                return false;
            }
            if (parsed > MaxScale)
            {
                error = "scale must be at most " + MaxScale.ToString("0.###", CultureInfo.InvariantCulture);
                return false;
            }

            scale = parsed;
            return true;
        }

        /// <summary>A munition's speed field under the scale, in m/s.</summary>
        public static float ScaleSpeed(float baseSpeedMs, double scale)
        {
            return (float)(baseSpeedMs * Clamp(scale));
        }

        /// <summary>
        /// A munition's acceleration under the scale, in m/s².
        ///
        /// <para><b>The square is the point.</b> A round that accelerates covers
        /// <c>v² / (2a)</c> metres reaching its top speed. Scaling speed by <i>k</i> and
        /// acceleration by <i>k</i> too would stretch that distance by <i>k</i>, so a torpedo would
        /// spend five times as much of its (unchanged) <c>MaxTrajectory</c> still speeding up, and
        /// point defence would meet a different weapon than the content author designed. Scaling
        /// acceleration by <i>k²</i> holds the spin-up <i>distance</i> fixed and simply runs the
        /// whole flight <i>k</i> times faster, which is what "the same fight at this server's
        /// speeds" means.</para>
        /// </summary>
        public static float ScaleAcceleration(float baseAccelMs2, double scale)
        {
            double k = Clamp(scale);
            return (float)(baseAccelMs2 * k * k);
        }
    }
}
