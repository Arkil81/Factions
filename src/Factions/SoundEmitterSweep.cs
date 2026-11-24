using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// Prunes SE's static <c>MyEntity3DSoundEmitter.m_entityEmitters</c> of emitters whose entity has closed.
    /// With realistic sound on, every drill, turret and similar block registers its emitter there, and SE
    /// prunes it only from <c>UpdateEntityEmitters</c>, which returns early while
    /// <c>MyFpsManager.GetSessionTotalFrames()</c> is 0. That counter advances only on the render thread, so on
    /// a dedicated server it stays 0, the list only grows, and each stale emitter keeps its block, and through
    /// it the whole closed grid, reachable. Fleet churn turns that into unbounded heap growth.
    /// The removal mirrors SE's own: stop the sound, drop the entry. Runs on the game thread.
    /// </summary>
    internal static class SoundEmitterSweep
    {
        private const int IntervalFrames = 600;

        private static readonly FieldInfo ListField = typeof(MyEntity3DSoundEmitter)
            .GetField("m_entityEmitters", BindingFlags.Static | BindingFlags.NonPublic);

        private static int _frame;
        private static long _removedTotal;
        private static int _lastCount;
        private static bool _missingReported;

        internal static long RemovedTotal { get { return _removedTotal; } }

        internal static void Tick()
        {
            if (++_frame < IntervalFrames) return;
            _frame = 0;
            if (MySession.Static == null) return;
            Sweep();
        }

        /// <summary>Removes every emitter whose entity is null or closed; returns how many.</summary>
        internal static int Sweep()
        {
            var list = ListField != null ? ListField.GetValue(null) as List<MyEntity3DSoundEmitter> : null;
            if (list == null)
            {
                if (!_missingReported)
                {
                    _missingReported = true;
                    FactionsPlugin.Log.Warn("Sound emitter sweep disabled: MyEntity3DSoundEmitter.m_entityEmitters not found.");
                }
                return 0;
            }
            int removed = 0;
            lock (list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    MyEntity3DSoundEmitter e = list[i];
                    if (e != null && e.Entity != null && !e.Entity.Closed) continue;
                    try { if (e != null) e.StopSound(true); }
                    catch (Exception) { }
                    list.RemoveAt(i);
                    removed++;
                }
                _lastCount = list.Count;
            }
            _removedTotal += removed;
            return removed;
        }

        internal static string Census()
        {
            return "soundEmitters count=" + _lastCount + " removedTotal=" + _removedTotal;
        }
    }
}
