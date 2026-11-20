using System;

namespace Factions.Wire
{
    /// <summary>Which reflector a directional antenna is. The byte values are Core's
    /// <c>RadioDishKind</c>; a value is never renumbered.</summary>
    public enum WireReflector : byte
    {
        /// <summary>The Antenna Dish: looks along the block's Up.</summary>
        Dish = 0,
        /// <summary>The Contact Pack's Parabolic Antenna: looks along the block's local +Z (Backward).</summary>
        Parabolic = 1,
    }

    /// <summary>What a client needs to aim its own reflectors at a call while it plays (FAC-419,
    /// docs/atc-voice.md, "Aiming by ear"): where the call comes from, how far this listener hears it
    /// with no reflector, and the reflectors' gain patterns. The client adds its reflectors' gain toward
    /// the source every audio chunk, from its ship's attitude at that moment, so turning the ship is
    /// heard with no message at all.</summary>
    public struct WireRadioAim
    {
        /// <summary>Where the call comes from, km: the station for a callout, the pilot for a pilot call.</summary>
        public float SourceX, SourceY, SourceZ;
        /// <summary>This listener's reach toward the source with no reflector gain, km.</summary>
        public float OmniReachKm;
        public float DishGainDb, DishBeamwidthDeg;
        public float ParabolicGainDb, ParabolicBeamwidthDeg;

        public bool HasSource { get { return OmniReachKm > 0; } }
    }

    /// <summary>
    /// The reflector pattern, shared by the server (which decides who could hear a call) and the
    /// client (which hears it). Core's <c>RadioPath.DirectionalGainDb</c> is the same formula; a test
    /// pins the two together, because the client cannot reference Core.
    ///
    /// <code>gain = peak - 12 (angle / beamwidth)^2 dB, floored at 0</code>
    /// </summary>
    public static class RadioAim
    {
        /// <summary>Which reflector an antenna subtype is, if any: the Contact Pack's Parabolic Antenna
        /// (<c>*CompactRadioAntennaReskin</c>), or anything whose subtype names a dish.</summary>
        public static bool TryKindOf(string subtype, out WireReflector kind)
        {
            kind = WireReflector.Dish;
            if (string.IsNullOrEmpty(subtype)) return false;
            if (subtype.IndexOf("CompactRadioAntennaReskin", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                kind = WireReflector.Parabolic;
                return true;
            }
            return subtype.IndexOf("Dish", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void PatternOf(WireReflector kind, WireRadioAim aim, out double peakDb, out double beamwidthDeg)
        {
            if (kind == WireReflector.Parabolic) { peakDb = aim.ParabolicGainDb; beamwidthDeg = aim.ParabolicBeamwidthDeg; }
            else { peakDb = aim.DishGainDb; beamwidthDeg = aim.DishBeamwidthDeg; }
        }

        /// <summary>One reflector's gain, dB, toward a direction (<paramref name="tx"/>..: listener to
        /// source, any length) given its axis (<paramref name="ax"/>..: any length).</summary>
        public static double GainDb(double peakDb, double beamwidthDeg,
                                    double ax, double ay, double az, double tx, double ty, double tz)
        {
            if (peakDb <= 0 || beamwidthDeg <= 0) return 0;
            double la = Math.Sqrt(ax * ax + ay * ay + az * az), lt = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            if (la <= 1e-9 || lt <= 1e-9) return 0;
            double cos = (ax * tx + ay * ty + az * tz) / (la * lt);
            double angleDeg = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
            double ratio = angleDeg / beamwidthDeg;
            double g = peakDb - 12.0 * ratio * ratio;
            return g > 0 ? g : 0;
        }

        /// <summary>Reach scales with the square root of the power gain.</summary>
        public static double GainFactor(double gainDb)
        {
            return Math.Pow(10.0, gainDb / 20.0);
        }

        /// <summary>The station's distance as a share of the reach this gain gives, 0 at the station, 1
        /// at the edge, above 1 beyond it (the squelch closes).</summary>
        public static double Share(double distanceKm, double omniReachKm, double gainDb)
        {
            double reach = omniReachKm * GainFactor(gainDb);
            return reach > 0 ? distanceKm / reach : double.PositiveInfinity;
        }
    }
}
