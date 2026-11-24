using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.Entities;

namespace Factions
{
    /// <summary>
    /// Weak references to every grid a fleet spawn produced, so <c>factions bench gc</c> can say how many outlive
    /// their close. Production Main's heap grew ~2 MB per fleet spawn while nearly every spawned grid was closed;
    /// a closed grid still reachable after a full collection is the retained object, and its eid is the handle a
    /// heap dump needs to find who holds it. Bounded ring; the references are weak, so tracking keeps nothing alive.
    /// </summary>
    internal static class GridRetention
    {
        private const int Capacity = 20000;
        private const int SampleEids = 8;

        private sealed class Entry
        {
            internal WeakReference Grid, Body;
            internal long Eid;
            internal int Blocks;
        }

        private static readonly Entry[] Ring = new Entry[Capacity];
        private static readonly object Gate = new object();
        private static int _next, _count;

        internal static void Track(List<MyCubeGrid> grids)
        {
            if (grids == null) return;
            lock (Gate)
            {
                foreach (MyCubeGrid g in grids)
                {
                    if (g == null) continue;
                    Ring[_next] = new Entry
                    {
                        Grid = new WeakReference(g),
                        Body = g.Physics != null ? new WeakReference(g.Physics) : null,
                        Eid = g.EntityId,
                        Blocks = g.BlocksCount,
                    };
                    _next = (_next + 1) % Capacity;
                    if (_count < Capacity) _count++;
                }
            }
        }

        /// <summary>Call after a full collection. Counts tracked grids still open, closed and collected, and closed
        /// but still reachable, with the block count they had at spawn and a few of their eids.</summary>
        internal static string Census()
        {
            int tracked = 0, open = 0, collected = 0, closedAlive = 0, bodyAlive = 0;
            long closedAliveBlocks = 0;
            var sample = new StringBuilder();
            int sampled = 0;
            lock (Gate)
            {
                for (int i = 0; i < _count; i++)
                {
                    Entry e = Ring[i];
                    if (e == null) continue;
                    tracked++;
                    var g = e.Grid.Target as MyCubeGrid;
                    if (g == null) { collected++; continue; }
                    if (!g.Closed) { open++; continue; }
                    closedAlive++;
                    closedAliveBlocks += e.Blocks;
                    if (e.Body != null && e.Body.IsAlive) bodyAlive++;
                    if (sampled < SampleEids)
                    {
                        sample.Append(sampled == 0 ? "" : ",").Append(e.Eid);
                        sampled++;
                    }
                }
            }
            return "gridRetention tracked=" + tracked + " open=" + open + " collected=" + collected
                + " closedAlive=" + closedAlive + " closedAliveBody=" + bodyAlive + " closedAliveBlocks=" + closedAliveBlocks
                + (sampled > 0 ? " closedAliveEids=" + sample : "");
        }
    }
}
