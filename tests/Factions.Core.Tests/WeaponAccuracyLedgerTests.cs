using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="WeaponAccuracyLedger"/>, the accumulator behind the fire/hit instrument
    /// (FAC-069). The point of the instrument is to replace a guessed accuracy figure with a measured
    /// one, so the arithmetic that turns raw shots and damage into that figure is worth pinning.
    /// </summary>
    public class WeaponAccuracyLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        private static string Key { get { return WeaponAccuracyLedger.KeyFor("LargeGatlingTurret", "LargeCaliber"); } }

        [Fact]
        public void RealisedFractionComparesObservedDamageAgainstTheModelledFigure()
        {
            var ledger = new WeaponAccuracyLedger();
            // 10 rounds modelled at 90 damage each = 900 expected; 450 arrived.
            for (int i = 0; i < 10; i++) ledger.RecordShot(Key, 90f, T0);
            ledger.RecordDamage(Key, 450, T0);

            WeaponAccuracySample row = ledger.Get(Key);
            Assert.Equal(10, row.Shots);
            Assert.Equal(45.0, row.DamagePerShot, 3);
            Assert.Equal(0.5, row.RealisedFraction, 3);
        }

        [Fact]
        public void AWeaponThatFiresAndNeverConnectsRealisesNothing()
        {
            var ledger = new WeaponAccuracyLedger();
            for (int i = 0; i < 5; i++) ledger.RecordShot(Key, 90f, T0);

            WeaponAccuracySample row = ledger.Get(Key);
            Assert.Equal(0.0, row.DamagePerShot, 3);
            Assert.Equal(0.0, row.RealisedFraction, 3);
        }

        [Fact]
        public void NoShotsMeansNoOpinionRatherThanPerfectInaccuracy()
        {
            // Damage with no recorded shot cannot be turned into a fraction. Zero here has to be read
            // as "nothing measured", which is why the readout marks thin rows.
            var ledger = new WeaponAccuracyLedger();
            ledger.RecordDamage(Key, 500, T0);

            WeaponAccuracySample row = ledger.Get(Key);
            Assert.Equal(0, row.Shots);
            Assert.Equal(0.0, row.RealisedFraction, 3);
            Assert.False(row.HasSample(1));
        }

        [Fact]
        public void WeaponAndAmmoAreSeparateRows()
        {
            // One block can fire several ammo types with very different behaviour; merging them would
            // average away the thing being measured.
            var ledger = new WeaponAccuracyLedger();
            ledger.RecordShot(WeaponAccuracyLedger.KeyFor("Ace_Turret_80mmFlakTurret", "Ace_80mmShellFlak"), 1200f, T0);
            ledger.RecordShot(WeaponAccuracyLedger.KeyFor("Ace_Turret_80mmFlakTurret", "Ace_80mmShellHE"), 300f, T0);
            Assert.Equal(2, ledger.RowCount);
        }

        [Fact]
        public void RepeatedFiringMutatesOneRowRatherThanAddingRows()
        {
            // The file this serialises to must not grow with time -- that is the whole storage design.
            var ledger = new WeaponAccuracyLedger();
            for (int i = 0; i < 5000; i++)
            {
                ledger.RecordShot(Key, 90f, T0);
                ledger.RecordDamage(Key, 30, T0);
            }
            Assert.Equal(1, ledger.RowCount);
            Assert.Equal(5000, ledger.Get(Key).Shots);
        }

        [Fact]
        public void MergeContinuesAPreviousSampleAcrossARestart()
        {
            var ledger = new WeaponAccuracyLedger();
            ledger.RecordShot(Key, 90f, T0);
            ledger.RecordDamage(Key, 45, T0);

            var persisted = new Dictionary<string, WeaponAccuracySample>
            {
                { Key, new WeaponAccuracySample { Shots = 9, DamageEvents = 4, DamageDealt = 405,
                                                  ModelledDamagePerShot = 90,
                                                  FirstSeenUtc = "2026-08-31T10:00:00Z",
                                                  LastSeenUtc  = "2026-08-31T10:05:00Z" } },
            };
            ledger.Merge(persisted);

            WeaponAccuracySample row = ledger.Get(Key);
            Assert.Equal(10, row.Shots);
            Assert.Equal(450.0, row.DamageDealt, 3);
            Assert.Equal(0.5, row.RealisedFraction, 3);
            // The older stamp wins for first-seen, the newer for last-seen.
            Assert.Equal("2026-08-31T10:00:00Z", row.FirstSeenUtc);
            Assert.Equal("2026-09-01T12:00:00Z", row.LastSeenUtc);
        }

        [Fact]
        public void StampsAreIsoOnAHostWhoseLocaleSeparatesTimeDifferently()
        {
            // ':' in a format string is the culture's TIME SEPARATOR SPECIFIER, not a literal, so an
            // un-qualified ToString writes 2026-09-01T12.00.00Z on a host that separates time with a
            // dot -- into the persisted ledger, not just into a test. IsBefore's ordinal compare and
            // every reader of the file assume the ISO shape.
            //
            // The separator is forced onto a cloned DateTimeFormatInfo rather than taken from a named
            // culture on purpose: the container runs with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1,
            // where every named culture collapses to invariant and a locale-based check would pass
            // there while the real defect stayed live.
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                hostile.DateTimeFormat.TimeSeparator = ".";
                CultureInfo.CurrentCulture = hostile;

                var ledger = new WeaponAccuracyLedger();
                ledger.RecordShot(Key, 90f, T0);

                WeaponAccuracySample row = ledger.Get(Key);
                Assert.Equal("2026-09-01T12:00:00Z", row.FirstSeenUtc);
                Assert.Equal("2026-09-01T12:00:00Z", row.LastSeenUtc);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void MergeAdoptsRowsThisSessionHasNotSeen()
        {
            var ledger = new WeaponAccuracyLedger();
            ledger.Merge(new Dictionary<string, WeaponAccuracySample>
            {
                { Key, new WeaponAccuracySample { Shots = 3, DamageDealt = 90, ModelledDamagePerShot = 90 } },
            });
            Assert.Equal(1, ledger.RowCount);
            Assert.Equal(3, ledger.Get(Key).Shots);
        }

        [Fact]
        public void ClearDropsEverythingSoTestsDoNotBleedTogether()
        {
            var ledger = new WeaponAccuracyLedger();
            ledger.RecordShot(Key, 90f, T0);
            ledger.Clear();
            Assert.Equal(0, ledger.RowCount);
            Assert.Null(ledger.Get(Key));
        }

        [Fact]
        public void ANullOrEmptyMergeIsHarmless()
        {
            var ledger = new WeaponAccuracyLedger();
            ledger.RecordShot(Key, 90f, T0);
            ledger.Merge(null);
            ledger.Merge(new Dictionary<string, WeaponAccuracySample> { { "", null } });
            Assert.Equal(1, ledger.RowCount);
        }

        [Fact]
        public void ThinSamplesAreDistinguishableFromRealOnes()
        {
            var ledger = new WeaponAccuracyLedger();
            for (int i = 0; i < 19; i++) ledger.RecordShot(Key, 90f, T0);
            Assert.False(ledger.Get(Key).HasSample(20));
            ledger.RecordShot(Key, 90f, T0);
            Assert.True(ledger.Get(Key).HasSample(20));
        }
    }
}
