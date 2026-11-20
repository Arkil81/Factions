using System;
using System.Collections.Generic;
using Factions.Core;

namespace Factions.Sim
{
    /// <summary>
    /// Fleet spawner for the live-mode sim: no MES materialisation, but SkipsTransit = false so
    /// factions generate pending fleets with real transit time and BFS path routing. Used by the
    /// live mode so in-flight convoys are visible on the node graph between ticks.
    /// </summary>
    internal sealed class SimTransitSpawner : IFleetSpawner
    {
        public bool SkipsTransit { get { return false; } }
        public long SpawnRaid(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }
        public long SpawnTrade(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }
        public long SpawnPatrol(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 centerKm, string prefabId) { return 0L; }
        public long SpawnHaul(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }
        public long SpawnMine(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }
        public long SpawnGift(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }
        public long SpawnSquadron(string fleetKey, ActionKind kind, string factionTag, long factionId,
                                  Vec3 posKm, Vec3 targetKm, IReadOnlyList<string> prefabIds, List<long> escortsOut) { return 0L; }
        public bool GridExists(long entityId) { return false; }
        public DateTime? LastDamageUtc(long entityId) { return null; }
        public bool ClosedByUs(long entityId) { return false; }
        public int Despawn(long entityId) { return 0; }  // nothing was ever materialised, so nothing was closed
        public string GridPrefabId(long entityId) { return null; }
        public Vec3? GetGridPositionKm(long entityId) { return null; }
        public double GetGridSpeedKmPerMin(long entityId) { return -1; }
        public string GetGridOwnerFactionTag(long entityId) { return null; }
        public bool Retarget(long entityId, Vec3 newTargetKm) { return false; }
        public void TickFlight() { }
        public bool BeginManoeuvre(long entityId, Vec3 targetKm) { return false; }
        public bool BeginLaunch(long entityId, Vec3 targetKm, bool flyThrough) { return false; }

        public bool ConsumeLaunchGiveUp(long entityId) { return false; }
        public bool ConsumeThrustLost(long entityId) { return false; }
        public void ReleaseControl(long entityId) { }
        public bool IsManoeuvring(long entityId) { return false; }

        public bool IsManoeuvreComplete(long entityId) { return false; }
    }
}
