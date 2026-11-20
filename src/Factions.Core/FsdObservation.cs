using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>
    /// The FSD calibration lines: what a player grid in supercruise is drawing, beside what
    /// <see cref="FuelBurn.FsdActivePowerMw"/> says it should draw at that mass and speed.
    ///
    /// <para>No NPC prefab carries an FSD block, so the drive half of the burn model cannot be
    /// checked on a materialised NPC hull the way <c>FUEL_OBSERVED</c> checks the thruster half.
    /// A player flying under the drive is the only sample there is, which is why these lines exist
    /// at all.</para>
    ///
    /// <para>Formatting sits here rather than at the call site so the two lines have one definition
    /// and can be asserted on without SE. The host reads the grid; this states what it read.</para>
    ///
    /// <para><b>An unreadable value prints <c>n/a</c>, never 0.</b> A drive whose power sink could
    /// not be reached and a drive drawing nothing are different findings, and a 0 that means the
    /// first is what turns a calibration into a wrong constant.</para>
    /// </summary>
    public static class FsdObservation
    {
        /// <summary>Seconds between samples for one grid. Draw is near-constant along a cruise at a
        /// held speed, so this is a calibration cadence and not a trace: the reader needs a handful
        /// of paired numbers, not one per tick.</summary>
        public const double IntervalSeconds = 10.0;

        public const string NotAvailable = "n/a";

        /// <summary>One in-cruise sample. <paramref name="fsdDrawKnown"/> false means the drives'
        /// own sinks could not be read, so <c>fsddraw=</c> prints <c>n/a</c> and <c>ratio=</c> falls
        /// back to the whole grid's draw — which includes gyros, lights and life support and is
        /// therefore an upper bound on the drive's share. A negative <paramref name="drives"/> means
        /// the block walk itself failed.
        ///
        /// <para><c>ratio=</c> is <c>n/a</c> when the model returns nothing to divide by, when
        /// neither draw is readable, and when the drive's own draw reads <b>zero</b>. A drive at
        /// 0.00 MW has dropped out or never engaged; the grid draw beside it is then some other
        /// block's, and substituting it would put a number in the calibration column that no drive
        /// produced.</para></summary>
        public static string Format(long entityId, ulong steamId, double massKg, double speedKmPerS,
                                    double gridDrawMw, bool gridDrawKnown,
                                    double fsdDrawMw, bool fsdDrawKnown,
                                    double modelledMw, int drives, bool largeGrid)
        {
            // A read that came back zero is a reading, and it says the drive was not running. It
            // does not licence the grid-draw fallback, which exists only for a sink that could not
            // be reached at all.
            bool fsdDrawUsable = fsdDrawKnown && fsdDrawMw > 0;
            bool haveObserved = fsdDrawUsable || (!fsdDrawKnown && gridDrawKnown);
            double observedMw = fsdDrawUsable ? fsdDrawMw : gridDrawMw;
            var sb = new StringBuilder(192);
            sb.Append("FSD_OBSERVED eid=").Append(entityId.ToString(CultureInfo.InvariantCulture))
              .Append(" steam=").Append(steamId.ToString(CultureInfo.InvariantCulture))
              .Append(" mass=").Append(Fixed(massKg, 0))
              .Append(" v=").Append(Fixed(speedKmPerS, 1))
              .Append(" draw=").Append(gridDrawKnown ? Fixed(gridDrawMw, 2) : NotAvailable)
              .Append(" fsddraw=").Append(fsdDrawKnown ? Fixed(fsdDrawMw, 2) : NotAvailable)
              .Append(" modelled=").Append(Fixed(modelledMw, 2))
              .Append(" ratio=").Append(haveObserved && modelledMw > 0
                                            ? Fixed(observedMw / modelledMw, 3) : NotAvailable)
              .Append(" drives=").Append(drives >= 0 ? drives.ToString(CultureInfo.InvariantCulture) : NotAvailable)
              .Append(" grid=").Append(largeGrid ? "Large" : "Small");
            return sb.ToString();
        }

        /// <summary>The engagement edge. <c>charge=</c> is the model's burst —
        /// <see cref="FuelBurn.FsdChargeMwh"/> — for the reader to compare against the drive's
        /// terminal <c>Charging Power</c> held for the mod's charge delay. The draw beside it is the
        /// whole grid's at that instant, and is a sample rather than the thing being calibrated.</summary>
        public static string FormatEntry(long entityId, ulong steamId, double massKg,
                                         double chargeMwh, double gridDrawMw, bool drawKnown)
        {
            var sb = new StringBuilder(128);
            sb.Append("FSD_OBSERVED_ENTRY eid=").Append(entityId.ToString(CultureInfo.InvariantCulture))
              .Append(" steam=").Append(steamId.ToString(CultureInfo.InvariantCulture))
              .Append(" mass=").Append(Fixed(massKg, 0))
              .Append(" charge=").Append(Fixed(chargeMwh, 4))
              .Append(" draw=").Append(drawKnown ? Fixed(gridDrawMw, 2) : NotAvailable);
            return sb.ToString();
        }

        private static string Fixed(double value, int decimals)
        {
            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                                  CultureInfo.InvariantCulture);
        }
    }
}
