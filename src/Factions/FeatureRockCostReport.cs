using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Factions.Core;
using Factions.Wire;
using Sandbox.Engine.Voxels;            // MyStorageBase
using Sandbox.Game.Entities;            // MyEntities, MyVoxelBase
using Sandbox.Game.World;               // MySession
using VRage.Game.Entity;                // MyEntity

namespace Factions
{
    /// <summary>
    /// Measures what feature-rock replication actually costs.
    ///
    /// <para><b>This number is no longer the decision.</b> FAC-090 opened by asking whether the saving
    /// justified the feature; FAC-092 then established that a pristine rock's replicated "content" is a
    /// regeneration instruction, not voxels, so every client already rebuilds our rock locally with its
    /// own material pool and renders the wrong ore unless it runs the same material patch. The feature
    /// is on the correctness path now. What follows is a sanity check on a side effect.</para>
    ///
    /// <para><b>Why a verb and not a counter.</b> The only honest measure of "what does one rock cost
    /// to send" is the bytes SE itself would put on the wire, and the only way to get those is to ask
    /// the storage to serialize — <c>MyStorageBase.Save</c>, which serializes and compresses the
    /// whole octree. That is far too expensive to run on a spawn path; doing it per rock per spawn
    /// would be the voxel stall this feature set already has a documented history of. Run once, on
    /// demand, against rock that is already standing, it costs one operator's patience and nothing
    /// else.</para>
    ///
    /// <para><b>It measures both sides with the real codecs.</b> The replicated cost comes from
    /// <c>Storage.Save</c>; the derived cost comes from <see cref="FactionsWire.WriteFeatureRockObjects"/>
    /// encoding the very same rock. Neither number is an estimate, and the ratio between them cannot
    /// flatter the feature, because the feature's own encoder is what produces the second one.</para>
    ///
    /// <para><b>What it cannot see, and a correction.</b> The figure is the cost of ONE stream-in.
    /// It was once a recurring charge — <c>AddVoxelMap</c> did not set <c>AsteroidName</c>, and
    /// <c>MyVoxelReplicable.Serialize</c> ends its cache test with
    /// <c>sendContent |= Voxel.AsteroidName == null;</c>, an unconditional OR, so SE's per-client
    /// voxel cache could never register a hit and the whole blob went again on every approach.
    /// <see cref="FeatureRockMaterializer"/> now sets the field, so the cache serves the second and
    /// later approaches. Do <b>not</b> multiply this by a session's fly-bys; that would credit this
    /// feature with a saving the one-line <c>AsteroidName</c> fix already took.</para>
    /// </summary>
    internal static class FeatureRockCostReport
    {
        /// <summary>Rocks serialized when the caller names no count. Enough for a stable median at a
        /// cost measured in tens of milliseconds, on a call an operator made deliberately.</summary>
        private const int DefaultSample = 20;

        /// <summary>Hard ceiling regardless of what is asked for. <c>Save</c> compresses an octree
        /// each time and this runs on the update thread; a mistyped argument must not be able to
        /// stall the server for a minute.</summary>
        private const int MaxSample = 100;

        /// <summary>Mirrors <see cref="PlayerRockManager"/>'s own keep band, so "rock near this
        /// player" means the same thing in the measurement as it does in the feature.</summary>
        private const double KeepHysteresis = 1.15;
        private const double DefaultViewDistanceM = 15000.0;

        public static string Run(string[] tokens)
        {
            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;
            if (rock == null)
                return "rockcost: no feature-rock materialiser is active -- no session loaded, or the "
                     + "world authors no features.";

            int sample = DefaultSample;
            if (tokens != null && tokens.Length > 2)
            {
                int parsed;
                if (!int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
                    return "rockcost: sample size must be a positive integer. Usage: factions rockcost [n]";
                sample = Math.Min(parsed, MaxSample);
            }

            var sb = new StringBuilder();
            sb.Append("rockcost: live cells=").Append(rock.LiveCells)
              .Append(" objects=").Append(rock.LiveObjects).AppendLine();

            // ---- what SE would send, measured from the storage itself --------------------------

            var sizes = new List<int>();
            var measured = new List<FeatureRockMaterializer.LiveRock>();
            foreach (FeatureRockMaterializer.LiveRock live in rock.LiveRocks())
            {
                if (sizes.Count >= sample) break;
                if (!rock.OwnsPristine(live.EntityId)) continue;   // only pristine rock is withheld

                int bytes;
                if (!TrySerializedSize(live.EntityId, out bytes)) continue;
                sizes.Add(bytes);
                measured.Add(live);
            }

            if (sizes.Count == 0)
            {
                sb.Append("  no pristine rock could be serialized -- nothing is materialised right now "
                        + "(no player near a feature), so there is nothing to measure. Fly a player "
                        + "into a belt and run this again.");
                return sb.ToString();
            }

            sizes.Sort();
            long total = 0;
            for (int i = 0; i < sizes.Count; i++) total += sizes[i];
            int p50 = sizes[sizes.Count / 2];
            double mean = (double)total / sizes.Count;

            sb.Append("  replicated (MyStorageBase.Save, n=").Append(sizes.Count).Append("): ")
              .Append("min=").Append(Bytes(sizes[0]))
              .Append(" p50=").Append(Bytes(p50))
              .Append(" mean=").Append(Bytes((long)mean))
              .Append(" max=").Append(Bytes(sizes[sizes.Count - 1])).AppendLine();

            // ---- what this feature sends instead, encoded by its own codec ---------------------

            var wire = new List<WireRockObject>(measured.Count);
            for (int i = 0; i < measured.Count; i++)
            {
                FeatureRockObject plan = measured[i].Plan;
                wire.Add(new WireRockObject
                {
                    StorageName = plan.StorageName,
                    EntityId = measured[i].EntityId,
                    XKm = plan.PositionKm.X,
                    YKm = plan.PositionKm.Y,
                    ZKm = plan.PositionKm.Z,
                    SizeM = plan.SizeM,
                    MaterialSubTypeId = plan.MaterialSubTypeId,
                    ShapeSeed = plan.ShapeSeed,
                });
            }
            int derivedBytes = FactionsWire.WriteFeatureRockObjects(
                wire, null, true, FeatureRockMaterializer.GeneratorVersion).Length;
            double derivedPer = (double)derivedBytes / wire.Count;

            sb.Append("  derived   (FeatureRockObjects, same ").Append(wire.Count).Append(" rocks): ")
              .Append(Bytes(derivedBytes)).Append(" total, ")
              .Append(Bytes((long)derivedPer)).Append(" each").AppendLine();

            double ratio = mean / Math.Max(1.0, derivedPer);
            sb.Append("  ratio: replication is ").Append(ratio.ToString("F1", CultureInfo.InvariantCulture))
              .Append("x the named form").AppendLine();

            // ---- and what that is worth per player, at this world's real distances --------------

            double keepKm = ViewDistanceKm() * KeepHysteresis;
            int players = 0;
            foreach (KeyValuePair<ulong, Vec3> player in OnlinePlayersKm())
            {
                players++;
                int near = 0;
                double keep2 = keepKm * keepKm;
                foreach (FeatureRockMaterializer.LiveRock live in rock.LiveRocks())
                {
                    if (live.Plan.PositionKm.DistanceSquaredTo(player.Value) > keep2) continue;
                    if (!rock.OwnsPristine(live.EntityId)) continue;
                    near++;
                }
                sb.Append("  steam=").Append(player.Key)
                  .Append(" pristine rock within ").Append(keepKm.ToString("F1", CultureInfo.InvariantCulture))
                  .Append("km: ").Append(near)
                  .Append(" -> replicated ").Append(Bytes((long)(near * mean)))
                  .Append(", derived ").Append(Bytes((long)(near * derivedPer)))
                  .AppendLine();
            }
            if (players == 0)
                sb.AppendLine("  no players online -- the per-player figures need someone standing in a belt.");

            sb.Append("  NOTE: this is what a client pays the FIRST time it meets each rock -- since "
                    + "AsteroidName is set, SE's own cache serves later approaches, so do NOT multiply "
                    + "it by a session's fly-bys. And note what the replicated blob IS: a regeneration "
                    + "instruction, not voxels (FAC-092). The client rebuilds the rock either way; what "
                    + "this feature changes is whether it rebuilds the RIGHT one. Do not decide the "
                    + "feature on this number.");
            return sb.ToString();
        }

        /// <summary>Serialized size of one rock's storage, as SE itself would put it on the wire.
        /// Returns false for anything that cannot be measured, which is never an error here — a rock
        /// closed between enumeration and this call is simply not part of the sample.</summary>
        private static bool TrySerializedSize(long entityId, out int bytes)
        {
            bytes = 0;
            try
            {
                MyEntity entity;
                if (!MyEntities.TryGetEntityById(entityId, out entity)) return false;
                var voxel = entity as MyVoxelBase;
                if (voxel == null) return false;
                var storage = voxel.Storage as MyStorageBase;
                if (storage == null) return false;

                byte[] data = null;
                storage.Save(out data);
                if (data == null) return false;
                bytes = data.Length;
                return true;
            }
            catch (Exception ex)
            {
                FactionsPlugin.Log.Warn(ex, "rockcost: serializing voxel " + entityId + " failed.");
                return false;
            }
        }

        private static IEnumerable<KeyValuePair<ulong, Vec3>> OnlinePlayersKm()
        {
            if (MySession.Static == null) yield break;
            foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Character == null) continue;
                VRageMath.Vector3D p = player.Character.PositionComp.GetPosition();
                yield return new KeyValuePair<ulong, Vec3>(
                    player.Id.SteamId, new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0));
            }
        }

        private static double ViewDistanceKm()
        {
            try
            {
                double v = MySession.Static.Settings.ViewDistance;
                return (v > 0 ? v : DefaultViewDistanceM) / 1000.0;
            }
            catch { return DefaultViewDistanceM / 1000.0; }
        }

        private static string Bytes(long n)
        {
            if (n < 1024) return n.ToString(CultureInfo.InvariantCulture) + "B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "KB";
            return (n / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + "MB";
        }
    }
}
