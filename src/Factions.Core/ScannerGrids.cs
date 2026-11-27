using System;
using System.Globalization;
using System.Text;

namespace Factions.Core
{
    /// <summary>A grid the sim does not own, as the host sampled it: a player's ship or base, a
    /// vanilla NPC grid, or a derelict. One per mechanical group, led by its largest grid (FAC-533).</summary>
    public struct ScannerGridCandidate
    {
        /// <summary>The group's largest grid.</summary>
        public long EntityId;
        public Vec3 PositionKm;
        public Vec3 VelocityKmPerMin;
        /// <summary>From the same sample a player's own signature readout comes from. Zero means not
        /// sampled yet and reads as the reference 1.0.</summary>
        public double Signature;
        /// <summary>Widest radius the grid is announcing itself over, km.</summary>
        public double BroadcastRadiusKm;
        /// <summary>The majority owner; zero for an unowned grid.</summary>
        public long OwnerIdentityId;
        /// <summary>The owner's faction tag, or null for a factionless owner and an unowned grid.</summary>
        public string OwnerTag;
        /// <summary>The owner's name; null for an NPC owner, whose identity name says nothing.</summary>
        public string OwnerName;
        /// <summary>Whether the owner and every member of the owner's faction are offline past the
        /// offline-raid grace (FAC-534). Only ever true for a human owner.</summary>
        public bool OwnerOffline;
        public bool Large;
        /// <summary>Blocks across the whole mechanical group.</summary>
        public int Blocks;
        public string Name;
        /// <summary>Summed weapon DPS across the group. Server only.</summary>
        public double WeaponDps;
    }

    /// <summary>The grid half of the scanner: detection, and what each tier of it shows.</summary>
    public static class ScannerGrids
    {
        public const string KeyPrefix = "grid:";

        public static string KeyOf(long entityId)
        {
            return KeyPrefix + entityId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Below this a grid is not moving for the HUD: it is drawn where it is.</summary>
        private const double MovingKmPerMin = 0.006;   // 0.1 m/s

        /// <summary>The contact for a detected grid, before grading. Everything is filled in;
        /// <see cref="Withhold"/> then removes what the viewer's tier has not earned.</summary>
        public static ScannerContact ToContact(ScannerGridCandidate g, SensorReading reading)
        {
            Vec3 v = g.VelocityKmPerMin;
            bool moving = v.X * v.X + v.Y * v.Y + v.Z * v.Z > MovingKmPerMin * MovingKmPerMin;
            return new ScannerContact
            {
                Key = KeyOf(g.EntityId),
                Kind = ScannerContactKind.Grid,
                Label = "Grid",
                DisplayName = string.IsNullOrEmpty(g.Name) ? null : g.Name,
                PositionKm = g.PositionKm,
                VelocityKmPerMin = moving ? v : new Vec3(),
                Moving = moving,
                EntityId = g.EntityId,
                OwnerTag = g.OwnerTag,
                OwnerIdentityId = g.OwnerIdentityId,
                OwnerName = g.OwnerName,
                OwnerOffline = g.OwnerOffline,
                GridLarge = g.Large,
                GridBlocks = g.Blocks,
                WeaponDps = g.WeaponDps,
                MarginDb = reading.MarginDb,
                // A broadcasting antenna or beacon announces the grid's name and owner: SE's own HUD
                // already draws both for anyone in its radius.
                Transponder = g.BroadcastRadiusKm > 0
            };
        }

        /// <summary>
        /// What a grid contact shows at its tier. The label is composed here, on the server, so a
        /// client draws it as it comes and nothing above the tier reaches the wire.
        ///
        /// <list type="bullet">
        /// <item>Contact: <c>Grid</c>, grey, no name.</item>
        /// <item>Class: <c>Large grid, 340 blocks</c>.</item>
        /// <item>Identity: the owner in front (<c>Name [TAG]</c>, <c>[TAG]</c> for an NPC,
        /// <c>Unowned</c>), the grid's name, and <c>owner offline</c>.</item>
        /// <item>Armament: a weapons word at the end.</item>
        /// </list>
        /// </summary>
        public static void Withhold(ref ScannerContact c, FactionTuning tuning)
        {
            bool classed = IntelGrade.ShowsClass(c.Tier);
            bool identified = IntelGrade.ShowsIdentity(c.Tier);
            bool armed = c.Tier >= IntelTier.Armament;
            c.Label = Label(classed, identified, armed, c.GridLarge, c.GridBlocks,
                            c.OwnerIdentityId, c.OwnerTag, c.OwnerName, c.OwnerOffline, c.WeaponDps, tuning);
            c.HasFlightPlan = false;
            c.TargetPosKm = new Vec3();
            if (!identified)
            {
                c.DisplayName = null;
                c.OwnerTag = null;
                c.OwnerIdentityId = 0;
                c.OwnerName = null;
                c.OwnerOffline = false;
            }
            if (!classed)
            {
                c.GridLarge = false;
                c.GridBlocks = 0;
            }
        }

        public static string Label(bool classed, bool identified, bool armed, bool large, int blocks,
                                   long ownerIdentityId, string ownerTag, string ownerName, bool ownerOffline,
                                   double weaponDps, FactionTuning tuning)
        {
            if (!classed) return "Grid";
            var sb = new StringBuilder();
            if (identified)
            {
                sb.Append(OwnerText(ownerIdentityId, ownerTag, ownerName)).Append(" · ");
            }
            sb.Append(large ? "Large grid" : "Small grid");
            if (blocks > 0) sb.Append(", ").Append(blocks.ToString(CultureInfo.InvariantCulture)).Append(" blocks");
            if (armed) sb.Append(" · ").Append(WeaponsWord(weaponDps, tuning));
            if (identified && ownerOffline) sb.Append(" · owner offline");
            return sb.ToString();
        }

        public static string OwnerText(long ownerIdentityId, string ownerTag, string ownerName)
        {
            if (ownerIdentityId == 0) return "Unowned";
            string tag = string.IsNullOrEmpty(ownerTag) ? null : "[" + ownerTag + "]";
            if (string.IsNullOrEmpty(ownerName)) return tag ?? "Unknown owner";
            return tag == null ? ownerName : ownerName + " " + tag;
        }

        /// <summary><c>unarmed</c>, <c>armed</c>, or <c>heavily armed</c> from
        /// <see cref="FactionTuning.IntelGridHeavyDps"/> up.</summary>
        public static string WeaponsWord(double dps, FactionTuning tuning)
        {
            if (dps <= 0) return "unarmed";
            double heavy = tuning != null ? tuning.IntelGridHeavyDps : 20000.0;
            return dps >= heavy ? "heavily armed" : "armed";
        }
    }
}
