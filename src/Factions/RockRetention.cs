using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;

namespace Factions
{
    /// <summary>
    /// Weak references to the objects behind each feature rock the materialiser builds, so <c>factions bench gc</c>
    /// can say which of them outlive their rock. A moving observer was measured retaining ~32 KB of managed heap per
    /// rock built and closed; this names the type that holds it without a profiler. Bounded ring; the references are
    /// weak, so tracking keeps nothing alive.
    /// </summary>
    internal static class RockRetention
    {
        private const int Capacity = 20000;

        private sealed class Entry
        {
            internal WeakReference Voxel, Storage, Provider, Body;
        }

        private static readonly Entry[] Ring = new Entry[Capacity];
        private static int _next, _count;

        internal static void Track(MyVoxelBase voxel, object storage, object provider)
        {
            if (voxel == null) return;
            Ring[_next] = new Entry
            {
                Voxel = new WeakReference(voxel),
                Storage = storage != null ? new WeakReference(storage) : null,
                Provider = provider != null ? new WeakReference(provider) : null,
                Body = voxel.Physics != null ? new WeakReference(voxel.Physics) : null,
            };
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>Call after a full collection. Counts tracked rocks whose entity is closed and, of those, how many
        /// of each object are still reachable.</summary>
        internal static string Census()
        {
            int tracked = 0, open = 0, closed = 0, voxel = 0, storage = 0, provider = 0, body = 0;
            for (int i = 0; i < _count; i++)
            {
                Entry e = Ring[i];
                if (e == null) continue;
                tracked++;
                var v = e.Voxel.Target as MyVoxelBase;
                if (v != null && !v.Closed) { open++; continue; }
                closed++;
                if (v != null) voxel++;
                if (e.Storage != null && e.Storage.IsAlive) storage++;
                if (e.Provider != null && e.Provider.IsAlive) provider++;
                if (e.Body != null && e.Body.IsAlive) body++;
            }
            return "rockRetention tracked=" + tracked + " open=" + open + " closed=" + closed
                + " closedAlive:voxel=" + voxel + ",storage=" + storage + ",provider=" + provider + ",body=" + body;
        }
    }
}
