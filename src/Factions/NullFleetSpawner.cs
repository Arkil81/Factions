using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.Entities;

namespace Factions
{
    /// <summary>
    /// Fleet spawner with no MES dependency. SpawnXxx methods are no-ops (return 0) so the engine
    /// runs in abstract-only mode; GridExists checks the SE entity registry so reboot reconciliation
    /// can clean dead entity IDs from persisted rosters even without MES. Despawn is also a no-op.
    /// <para><see cref="SkipsTransit"/> = true, which tells FactionRuntime to set
    /// EstimatedArrivalUtc = DepartedUtc (immediate resolution) instead of using distance-based
    /// transit time — preserving all existing sim behaviour with no branching in the resolver.</para>
    /// Replace with a MesFleetSpawner when MES CustomSpawnRequest is wired.
    /// </summary>
    public sealed class NullFleetSpawner : IFleetSpawner
    {
        public bool SkipsTransit { get { return true; } }

        public long SpawnRaid(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }

        public long SpawnTrade(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }

        public long SpawnPatrol(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 centerKm, string prefabId) { return 0L; }

        public long SpawnHaul(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }
        public long SpawnMine(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }
        public long SpawnGift(string fleetKey, string factionTag, long factionId, Vec3 posKm, Vec3 targetKm, string prefabId) { return 0L; }

        /// <summary>Never materialises, so a squadron produces no lead and no escorts;
        /// <paramref name="escortsOut"/> is left untouched.</summary>
        public long SpawnSquadron(string fleetKey, ActionKind kind, string factionTag, long factionId,
                                  Vec3 posKm, Vec3 targetKm, IReadOnlyList<string> prefabIds, List<long> escortsOut) { return 0L; }

        /// <summary>Never materialises, so it never observes damage — always null, which the
        /// observation loop reads as "no evidence of combat".</summary>
        public DateTime? LastDamageUtc(long entityId) { return null; }
        public bool ClosedByUs(long entityId) { return false; }

        public bool GridExists(long entityId)
        {
            return MyEntities.GetEntityById(entityId) != null;
        }

        /// <summary>Nothing was ever materialised, so no grid can be named.</summary>
        public string GridPrefabId(long entityId) { return null; }

        /// <summary>No-op: nothing was ever materialised, so no grid is ever closed. Zero here means
        /// "closed nothing", not "the close failed".</summary>
        public int Despawn(long entityId) { return 0; }

        public Vec3? GetGridPositionKm(long entityId) { return null; }

        public double GetGridSpeedKmPerMin(long entityId) { return -1; }

        public string GetGridOwnerFactionTag(long entityId) { return null; }

        public bool Retarget(long entityId, Vec3 newTargetKm) { return false; }

        public void TickFlight() { }                                      // nothing was ever materialised

        public bool BeginManoeuvre(long entityId, Vec3 targetKm) { return false; }
        public bool BeginLaunch(long entityId, Vec3 targetKm, bool flyThrough) { return false; }

        public bool ConsumeLaunchGiveUp(long entityId) { return false; }
        public bool ConsumeThrustLost(long entityId) { return false; }

        public void ReleaseControl(long entityId) { }

        public bool IsManoeuvring(long entityId) { return false; }

        public bool IsManoeuvreComplete(long entityId) { return false; }
    }
}
