using System;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>
    /// The rules for a world's grid speed limit, kept SE-free so the host, the sim and the tests
    /// read one implementation.
    ///
    /// <para>SE holds the limit on <c>MySector.EnvironmentDefinition.LargeShipMaxSpeed</c> /
    /// <c>SmallShipMaxSpeed</c> and clamps every read through
    /// <c>MyGridPhysics.LargeShipMaxLinearVelocity()</c>. <see cref="Clamp"/> mirrors that clamp so a
    /// value this plugin reports is the value SE will actually enforce, rather than the one that was
    /// asked for.</para>
    ///
    /// <para><see cref="SpeedFloorKmPerMin"/> is the second half and the reason this type exists at
    /// all: the host already derives <see cref="FactionTuning.FleetSpeedMinKmPerMin"/> from the
    /// large-grid limit at session load, so anything that changes the limit and does not recompute
    /// the floor leaves the transit model describing a world that no longer exists.</para>
    /// </summary>
    public static class GridSpeedPolicy
    {
        /// <summary>SE's own upper clamp on a grid speed limit, in m/s — the speed of light, from
        /// <c>MyGridPhysics.LargeShipMaxLinearVelocity()</c>. Reproduced rather than referenced
        /// because Core carries no SE reference.</summary>
        public const double MaxSpeedMs = 149896220.0;

        /// <summary>Sentinel meaning "leave whatever SE loaded alone". Zero rather than a nullable so
        /// the tuning field round-trips through a JSON file that has never heard of it.</summary>
        public const double NoOverride = 0.0;

        /// <summary>SE's clamp, applied to a value before it is reported or stored, so a number this
        /// plugin prints is one the physics will honour.</summary>
        public static double Clamp(double metresPerSecond)
        {
            if (double.IsNaN(metresPerSecond)) return 0.0;
            if (metresPerSecond < 0.0) return 0.0;
            if (metresPerSecond > MaxSpeedMs) return MaxSpeedMs;
            return metresPerSecond;
        }

        /// <summary>True when a persisted tuning value asks for an override at all.</summary>
        public static bool IsOverride(double tuningValue)
        {
            return !double.IsNaN(tuningValue) && tuningValue > NoOverride;
        }

        /// <summary>
        /// Parses an operator-supplied speed. Culture-invariant on purpose: the value crosses a chat
        /// command, a file bridge and a JSON file, and a decimal comma in any one of them would make
        /// the same text mean different speeds on different hosts.
        /// </summary>
        /// <param name="error">Null on success; a player-facing reason on failure.</param>
        public static bool TryParseSpeed(string text, out double metresPerSecond, out string error)
        {
            metresPerSecond = 0.0;
            error = null;
            if (string.IsNullOrEmpty(text)) { error = "no speed given"; return false; }

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
            // Zero is the "no override" sentinel in the tuning file, so it cannot also mean "a world
            // where nothing moves" -- that would make the stored value ambiguous on the next load.
            if (parsed <= 0.0)
            {
                error = "speed must be greater than 0 m/s";
                return false;
            }
            if (parsed > MaxSpeedMs)
            {
                error = "speed must be at most " + MaxSpeedMs.ToString("0", CultureInfo.InvariantCulture)
                        + " m/s (SE clamps above that)";
                return false;
            }

            metresPerSecond = parsed;
            return true;
        }

        /// <summary>
        /// The fleet speed floor derived from the world's large-grid limit, in km/min. Single-sourced
        /// here because the host applies it at session load and again whenever the limit is changed,
        /// and the two must agree.
        /// </summary>
        public static double SpeedFloorKmPerMin(double largeShipMaxSpeedMs)
        {
            return Clamp(largeShipMaxSpeedMs) * 60.0 / 1000.0;
        }
    }
}
