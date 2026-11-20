using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>One thing that can see: where it is, and how far it detects a reference object.
    /// A player's grid, an owned node's passive envelope, an installed <see cref="NodeCapabilityKind.SensorArray"/>
    /// -- the detection test does not care which.</summary>
    public struct SensorSource
    {
        public Vec3 PositionKm;
        public double StrengthKm;
    }

    /// <summary>
    /// The <b>detected</b> tier for fleets: who can see a convoy right now.
    ///
    /// <para>This is the half of the two-tier model that is derived rather than persisted, and it
    /// exists only for fleets. A fleet is never entered into a discovered set -- a remembered fleet
    /// has moved, so the memory would be worthless -- which means there is nothing to look one up in
    /// and the question can only be answered by geometry, every time it is asked.</para>
    ///
    /// <para><b>A fleet is therefore Detected or Unknown, with no Discovered tier in between.</b> One
    /// leaving coverage vanishes rather than dimming. That asymmetry with nodes is the design's own
    /// rule, not a shortcut.</para>
    /// </summary>
    public static class FleetSensing
    {
        /// <summary>How loud a convoy is, from what it is built of and what it is carrying. Every
        /// input comes from the prefab catalogue's real scan data, so this adds no host work.
        ///
        /// <para>An unknown prefab yields a mass factor of 1.0 rather than 0 -- "unknown, do not
        /// gate", the same convention <see cref="IPrefabCatalogue.DryMassKg"/> documents for route
        /// feasibility. A fleet whose prefab was never scanned stays detectable at reference range
        /// instead of turning invisible, which is the failure that would matter.</para></summary>
        /// <summary>
        /// A convoy's signature including what it is <i>doing</i>.
        ///
        /// <para>Unlike a player grid, an abstract fleet has nothing to measure — no thrusters, no
        /// power bus, no antenna — so its excitation is <b>derived from its own record and the clock</b>
        /// rather than sampled. That is not a compromise: it needs no per-fleet state, survives a
        /// reload for free, and cannot drift out of step with what the fleet actually is.</para>
        ///
        /// <para><b>Only terms that can differ between two convoys are included.</b> Every fleet in
        /// transit is cruising and every one is nominally under FSD, so a thrust or supercruise term
        /// would be on for the whole population at once — it would raise the floor and separate
        /// nobody. What discriminates is a state <i>change</i> (a fresh departure) and a role-specific
        /// activity (drilling), and those are the two here.</para>
        ///
        /// <para>Pass a null <paramref name="tuning"/> for the base signature alone.</para>
        /// </summary>
        public static double SignatureOf(
            PendingFleetAction fleet, IPrefabCatalogue prefabs, IItemVolumeCatalogue volumes,
            FactionTuning tuning, DateTime nowUtc)
        {
            string prefabId = fleet.ShipPrefabId ?? "";
            double dryMassKg = 0;
            double weaponDps = 0;
            if (prefabs != null && prefabId.Length > 0)
            {
                dryMassKg = prefabs.DryMassKg(prefabId);
                IReadOnlyList<WeaponBand> bands = prefabs.WeaponBands(prefabId);
                if (bands != null)
                    for (int i = 0; i < bands.Count; i++) weaponDps += bands[i].Dps;
            }
            double signature = Signature.ForShip(
                dryMassKg, weaponDps, CargoFill(fleet, volumes), fleet.ConvoySize,
                ExcitationOf(fleet, tuning, nowUtc),
                tuning != null ? tuning.SignatureShipCeiling : 0.0);

            // A stealthy APPROACH is a real tactic and the model above supports it: a small, unarmed,
            // empty hull is genuinely hard to see coming. A stealthy RAID is not -- once the shooting
            // starts it is an event, not a secret, and it must not fall below detection because the
            // raider happened to be a cheap drone. So the dwell carries a FLOOR as well as the
            // multiplier: whatever is fighting reads at least as loud as a reference hull in a fight.
            // Deliberately a floor and not a fixed value, so a heavy squadron is still louder.
            if (tuning != null && fleet.CurrentStopKind == FleetStopKind.Engage
                && signature < tuning.SignatureEngagementFloor)
                signature = tuning.SignatureEngagementFloor;
            return signature;
        }

        /// <summary>What this convoy is doing, as a multiplier over its base signature. 1.0 when
        /// nothing applies, or when no tuning is supplied.
        ///
        /// <para><b>Departure burst</b> — a convoy that has just lit its drives and left is briefly
        /// conspicuous, fading over its own window. This is the term that separates one abstract fleet
        /// from another, because only a few have just left.</para>
        ///
        /// <para><b>Drilling</b> — a <see cref="FleetStopKind.Mine"/> dwell is the largest sustained
        /// draw in the game, and only Mine fleets on station have it.</para></summary>
        public static double ExcitationOf(PendingFleetAction fleet, FactionTuning tuning, DateTime nowUtc)
        {
            if (tuning == null) return 1.0;
            double sum = 0;

            // A fleet that has STOPPED did not just light its drives. AdvanceToNextStop stamps
            // DepartedUtc on every leg including a dwell, so without this the departure burst fires
            // while a raider sits still fighting or a miner sits still drilling -- and since detection
            // range is a product, the target gets QUIETER as the dwell runs. Backwards for a raid,
            // whose dwell exists to be reacted to, and wrong for Mine too.
            double window = PendingFleetResolver.IsDwellStop(fleet.CurrentStopKind)
                ? 0 : tuning.SignatureDecayFleetDepartureSeconds;
            if (window > 0 && fleet.DepartedUtc != default(DateTime) && nowUtc != default(DateTime))
            {
                double since = (nowUtc - fleet.DepartedUtc).TotalSeconds;
                if (since >= 0 && since < window)
                    sum += tuning.SignatureWeightFleetDeparture * (1.0 - since / window);
            }

            if (fleet.CurrentStopKind == FleetStopKind.Mine)
                sum += tuning.SignatureWeightFleetMining;

            // A fight is the loudest thing a fleet does. This is the multiplier half -- a big
            // squadron engaging is louder than a lone fighter engaging, which is the point of a
            // signature model. The half that guarantees a fight is *never* quiet is the floor in
            // SignatureOf, not this: a multiplier alone leaves a small enough raider undetectable.
            if (fleet.CurrentStopKind == FleetStopKind.Engage)
                sum += tuning.SignatureWeightFleetEngagement;

            return 1.0 + sum;
        }

        /// <summary>Fraction of the hold that is full, 0..1. Litres, by the same volume-aware
        /// accounting the cargo loader itself uses -- an item count would make a hold of ore and a
        /// hold of components read the same.</summary>
        public static double CargoFill(PendingFleetAction fleet, IItemVolumeCatalogue volumes)
        {
            if (fleet.CargoCapacityLiters <= 0 || fleet.Cargo == null || fleet.Cargo.Count == 0) return 0;
            if (volumes == null) return 0;
            double usedLiters = 0;
            foreach (KeyValuePair<string, long> item in fleet.Cargo)
            {
                double litersPerUnit = volumes.VolumeLitersPerUnit(item.Key);
                if (litersPerUnit > 0) usedLiters += item.Value * litersPerUnit;
            }
            double fill = usedLiters / fleet.CargoCapacityLiters;
            return fill < 0 ? 0 : (fill > 1 ? 1 : fill);
        }

        /// <summary>Every sensor a faction's estate provides. Appends rather than clears, so a caller
        /// can stack a player's own grid on top of their faction's nodes in one list.</summary>
        public static void CollectNodeSensors(
            SpatialGraph graph, string ownerTag, FactionTuning tuning, List<SensorSource> into)
        {
            if (graph == null || into == null || tuning == null || string.IsNullOrEmpty(ownerTag)) return;
            foreach (SpatialNode node in graph.Nodes.Values)
            {
                if (node == null || node.OwnerFactionTag != ownerTag) continue;
                double strength = Sensing.NodeStrengthKm(node, tuning);
                if (strength <= 0) continue;
                into.Add(new SensorSource { PositionKm = node.Position, StrengthKm = strength });
            }
        }

        /// <summary>Whether any of these sensors detects a target of this signature. Stops at the
        /// first that does: the answer is a bool, and a viewer's estate can be hundreds of nodes.</summary>
        public static bool IsDetectedBy(IList<SensorSource> sensors, Vec3 targetPosKm, double signature)
        {
            return IsDetectedBy(sensors, targetPosKm, signature, 0.0);
        }

        /// <summary>As above, floored at the target's own broadcast radius — see
        /// <see cref="Sensing.DetectionRangeKm(double,double,double)"/>.</summary>
        public static bool IsDetectedBy(
            IList<SensorSource> sensors, Vec3 targetPosKm, double signature, double targetBroadcastKm)
        {
            return IsDetectedBy(sensors, targetPosKm, signature, targetBroadcastKm, null);
        }

        /// <summary>As above, with planets able to stand between a sensor and the target. Each sensor
        /// is tested on its own line: an estate spread around a body still sees what any one of its
        /// nodes can see.</summary>
        public static bool IsDetectedBy(
            IList<SensorSource> sensors, Vec3 targetPosKm, double signature, double targetBroadcastKm,
            SensorOcclusion occlusion)
        {
            if (sensors == null) return false;
            for (int i = 0; i < sensors.Count; i++)
            {
                SensorSource sensor = sensors[i];
                if (Sensing.Detects(sensor.PositionKm, sensor.StrengthKm, targetPosKm, signature,
                                    targetBroadcastKm, occlusion)) return true;
            }
            return false;
        }
    }
}
