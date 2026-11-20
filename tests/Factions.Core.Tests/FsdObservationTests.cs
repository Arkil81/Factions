using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="FsdObservation"/> — the two FSD calibration lines. The line is the whole
    /// deliverable of the drive-side calibration, so the assertions are on its exact text: a field
    /// that silently renames or a 0 standing in for an unread value is the failure this catches.
    /// </summary>
    public class FsdObservationTests
    {
        [Fact]
        public void Format_states_both_draws_and_the_fsd_ratio()
        {
            string line = FsdObservation.Format(
                entityId: 12345L, steamId: 76561198000000001UL,
                massKg: 1_000_000.0, speedKmPerS: 8.3333,
                gridDrawMw: 21.5, gridDrawKnown: true,
                fsdDrawMw: 20.1, fsdDrawKnown: true,
                modelledMw: 20.0, drives: 2, largeGrid: true);

            Assert.Equal(
                "FSD_OBSERVED eid=12345 steam=76561198000000001 mass=1000000 v=8.3 "
                + "draw=21.50 fsddraw=20.10 modelled=20.00 ratio=1.005 drives=2 grid=Large",
                line);
        }

        /// <summary>With the drives' own sinks unreadable, the ratio falls back to the whole grid's
        /// draw rather than disappearing — and <c>fsddraw=</c> says which number was used.</summary>
        [Fact]
        public void Format_falls_back_to_grid_draw_when_the_drive_sinks_are_unreadable()
        {
            string line = FsdObservation.Format(
                entityId: 7L, steamId: 42UL,
                massKg: 500_000.0, speedKmPerS: 2.0,
                gridDrawMw: 10.0, gridDrawKnown: true,
                fsdDrawMw: 0.0, fsdDrawKnown: false,
                modelledMw: 8.0, drives: 1, largeGrid: false);

            Assert.Contains(" fsddraw=n/a ", line, StringComparison.Ordinal);
            Assert.Contains(" draw=10.00 ", line, StringComparison.Ordinal);
            Assert.Contains(" ratio=1.250 ", line, StringComparison.Ordinal);
            Assert.Contains(" grid=Small", line, StringComparison.Ordinal);
        }

        /// <summary>Nothing observed and nothing modelled both print <c>n/a</c>. A 0 here would read
        /// as a drive that drew nothing, which is a different finding from one that was not read.</summary>
        [Theory]
        [InlineData(false, 20.0)]   // no draw readable, model fine
        [InlineData(true, 0.0)]     // draw readable, model returns nothing to divide by
        public void Format_prints_na_when_a_side_of_the_ratio_is_missing(bool gridDrawKnown, double modelledMw)
        {
            string line = FsdObservation.Format(
                entityId: 1L, steamId: 2UL,
                massKg: 1000.0, speedKmPerS: 1.0,
                gridDrawMw: 5.0, gridDrawKnown: gridDrawKnown,
                fsdDrawMw: 0.0, fsdDrawKnown: false,
                modelledMw: modelledMw, drives: -1, largeGrid: true);

            Assert.Contains(" ratio=n/a ", line, StringComparison.Ordinal);
            Assert.Contains(" drives=n/a ", line, StringComparison.Ordinal);
            if (!gridDrawKnown) Assert.Contains(" draw=n/a ", line, StringComparison.Ordinal);
        }

        /// <summary>The live case this closes: <c>draw=0.71 fsddraw=0.00 modelled=4.07</c> printed
        /// <c>ratio=0.000</c>, which reads as a drive running at a fraction of a percent of the
        /// model. A drive at 0.00 MW has dropped out, and the grid draw beside it belongs to
        /// something else — so neither number may fill the ratio column.</summary>
        [Fact]
        public void Format_prints_na_when_the_drive_reads_zero_and_does_not_borrow_the_grid_draw()
        {
            string line = FsdObservation.Format(
                entityId: 3L, steamId: 4UL,
                massKg: 910_000.0, speedKmPerS: 1.5,
                gridDrawMw: 0.71, gridDrawKnown: true,
                fsdDrawMw: 0.0, fsdDrawKnown: true,
                modelledMw: 4.07, drives: 1, largeGrid: true);

            Assert.Contains(" fsddraw=0.00 ", line, StringComparison.Ordinal);   // the read itself stands
            Assert.Contains(" draw=0.71 ", line, StringComparison.Ordinal);
            Assert.Contains(" ratio=n/a ", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ratio=0.000", line, StringComparison.Ordinal);
            Assert.DoesNotContain("ratio=0.174", line, StringComparison.Ordinal);
        }

        /// <summary>A readable, non-zero drive draw is still preferred over the grid's.</summary>
        [Fact]
        public void Format_prefers_the_drive_draw_over_the_grid_draw_when_the_drive_is_running()
        {
            string line = FsdObservation.Format(
                entityId: 3L, steamId: 4UL,
                massKg: 910_000.0, speedKmPerS: 14.3,
                gridDrawMw: 60.0, gridDrawKnown: true,
                fsdDrawMw: 49.17, fsdDrawKnown: true,
                modelledMw: 47.35, drives: 1, largeGrid: true);

            Assert.Contains(" ratio=1.038 ", line, StringComparison.Ordinal);
        }

        /// <summary>The entry line carries the model's charge burst at four decimals — it is a tenth
        /// of a MWh for a 1,000 t hull, so two would round it away.</summary>
        [Fact]
        public void FormatEntry_states_the_modelled_charge_burst()
        {
            string line = FsdObservation.FormatEntry(
                entityId: 99L, steamId: 5UL, massKg: 1_000_000.0,
                chargeMwh: 0.13222, gridDrawMw: 30.0, drawKnown: true);

            Assert.Equal(
                "FSD_OBSERVED_ENTRY eid=99 steam=5 mass=1000000 charge=0.1322 draw=30.00",
                line);
        }

        [Fact]
        public void FormatEntry_prints_na_for_an_unread_draw()
        {
            string line = FsdObservation.FormatEntry(
                entityId: 99L, steamId: 5UL, massKg: 1000.0,
                chargeMwh: 0.0, gridDrawMw: 0.0, drawKnown: false);

            Assert.EndsWith(" draw=n/a", line, StringComparison.Ordinal);
        }

        /// <summary>The charge the entry line reports is <see cref="FuelBurn.FsdChargeMwh"/>'s, not a
        /// second transcription of the mod's constants.</summary>
        [Fact]
        public void FormatEntry_charge_matches_the_burn_model()
        {
            var tuning = new FactionTuning();
            double charge = FuelBurn.FsdChargeMwh(tuning, 1_000_000.0);
            string line = FsdObservation.FormatEntry(1L, 2UL, 1_000_000.0, charge, 0.0, false);

            Assert.Contains(" charge=" + charge.ToString("F4",
                System.Globalization.CultureInfo.InvariantCulture) + " ", line, StringComparison.Ordinal);
            Assert.True(charge > 0, "the default tuning models a charge burst");
        }
    }
}
