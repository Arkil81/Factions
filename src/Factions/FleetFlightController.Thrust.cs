using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Factions.Core;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRageMath;

namespace Factions
{
    internal sealed partial class FleetFlightController
    {
        // Snapshot/blueprint serialization can run off-thread. Only active controlled blocks are
        // registered, so temporary burns never become saved prefab/world settings.
        internal static readonly ConcurrentDictionary<long, float> OriginalCivilianOverrides =
            new ConcurrentDictionary<long, float>();

        public void ReleaseAll()
        {
            _dead.Clear();
            foreach (long id in _controlled.Keys) _dead.Add(id);
            for (int i = 0; i < _dead.Count; i++) Release(_dead[i]);
        }

        /// <summary>Cached blocks, not a grid scan per frame. Analogue writes and capacity reads
        /// run at 10 Hz; membership refreshes once a second. Original settings survive every exit.</summary>
        private sealed class AnalogueThrust
        {
            private sealed class Entry
            {
                public MyThrust Block;
                public float Original;
                public float Written;
                public Vector3I Push;
            }

            private readonly MyCubeGrid _grid;
            private readonly MyEntityThrustComponent _thrust;
            private readonly bool _dampeners;
            private readonly List<Entry> _entries = new List<Entry>();
            private readonly HashSet<long> _seen = new HashSet<long>();
            private DateTime _nextMembership;
            private DateTime _nextUpdate;
            public AxisThrust Available;
            public Vector3 Input;
            public int Writes;

            public AnalogueThrust(MyCubeGrid grid, MyEntityThrustComponent thrust, DateTime now)
            {
                _grid = grid;
                _thrust = thrust;
                _dampeners = thrust.DampenersEnabled;
                RefreshMembership(now);
                ReadCapacity();
            }

            private void RefreshMembership(DateTime now)
            {
                _nextMembership = now.AddSeconds(1);
                _seen.Clear();
                for (int i = 0; i < _entries.Count; i++)
                {
                    MyThrust b = _entries[i].Block;
                    if (!b.Closed && !b.MarkedForClose && b.CubeGrid == _grid) _seen.Add(b.EntityId);
                }
                foreach (MyCubeBlock block in _grid.GetFatBlocks())
                {
                    MyThrust b = block as MyThrust;
                    if (b == null || b.Closed || b.MarkedForClose || !_seen.Add(b.EntityId)) continue;
                    float original = ((Sandbox.ModAPI.Ingame.IMyThrust)b).ThrustOverridePercentage;
                    OriginalCivilianOverrides[b.EntityId] = original;
                    _entries.Add(new Entry { Block = b, Original = original, Written = original,
                                             Push = -b.ThrustForwardVector });
                }
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    Entry e = _entries[i];
                    if (!e.Block.Closed && !e.Block.MarkedForClose && e.Block.CubeGrid == _grid) continue;
                    Restore(e);
                    _entries.RemoveAt(i);
                }
            }

            private void ReadCapacity()
            {
                Available = new AxisThrust();
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry e = _entries[i];
                    MyThrust b = e.Block;
                    if (b.Closed || b.MarkedForClose || b.CubeGrid != _grid
                        || !b.Enabled || !b.IsFunctional || !b.IsPowered) continue;
                    // This is independent of the override: SE's max-direction tables exclude it.
                    double n = ((Sandbox.ModAPI.Ingame.IMyThrust)b).MaxEffectiveThrust;
                    if (e.Push.X > 0) Available.RightN += n;
                    else if (e.Push.X < 0) Available.LeftN += n;
                    else if (e.Push.Y > 0) Available.UpN += n;
                    else if (e.Push.Y < 0) Available.DownN += n;
                    else if (e.Push.Z > 0) Available.BackwardN += n;
                    else Available.ForwardN += n;
                }
            }

            public void Prepare(DateTime now)
            {
                _thrust.AutopilotEnabled = false;
                _thrust.DampenersEnabled = false;
                _thrust.ControlThrust = Vector3.Zero;
                _thrust.AutoPilotControlThrust = Vector3.Zero;
                if (now >= _nextMembership) RefreshMembership(now);
                if (now >= _nextUpdate) ReadCapacity();
            }

            public void Apply(FlightCommand cmd, ManoeuvreKind kind, Vector3 velocity, Vector3 gravity,
                              float mass, ref Matrix toLocal, DateTime now)
            {
                if (now < _nextUpdate) return;
                _nextUpdate = now.AddSeconds(CivilianThrust.UpdateSeconds);
                Vec3 wanted = cmd.DesiredVelocityMs.Value;
                Vector3 w = Vector3.Transform(new Vector3((float)wanted.X, (float)wanted.Y, (float)wanted.Z), ref toLocal);
                Vector3 v = Vector3.Transform(velocity, ref toLocal);
                Vector3 g = Vector3.Transform(gravity, ref toLocal);
                Vec3 fractions = CivilianThrust.Fractions(ToVec3(w), ToVec3(v), ToVec3(g), mass, Available,
                    kind == ManoeuvreKind.ApproachAndStop ? CivilianThrust.ApproachResponseSeconds : CivilianThrust.ResponseSeconds);
                Input = new Vector3((float)fractions.X, (float)fractions.Y, (float)fractions.Z);
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry e = _entries[i];
                    MyThrust b = e.Block;
                    if (b.Closed || b.MarkedForClose || b.CubeGrid != _grid) continue;
                    double signed = e.Push.X != 0 ? fractions.X * e.Push.X
                                  : e.Push.Y != 0 ? fractions.Y * e.Push.Y : fractions.Z * e.Push.Z;
                    float want = (float)Math.Max(0, signed);
                    if (!b.Enabled || !b.IsFunctional || !b.IsPowered) want = 0;
                    if (!CivilianThrust.ShouldWrite(e.Written, want)) continue;
                    ((Sandbox.ModAPI.Ingame.IMyThrust)b).ThrustOverridePercentage = want;
                    e.Written = want;
                    Writes++;
                }
            }

            private static void Restore(Entry e)
            {
                // Leave the serialization guard in place if restoring a live block throws.
                if (!e.Block.Closed && !e.Block.MarkedForClose)
                    ((Sandbox.ModAPI.Ingame.IMyThrust)e.Block).ThrustOverridePercentage = e.Original;
                float ignored;
                OriginalCivilianOverrides.TryRemove(e.Block.EntityId, out ignored);
            }

            public void Release()
            {
                // Restore every surviving block even if one has become invalid during a split.
                for (int i = 0; i < _entries.Count; i++)
                {
                    try { Restore(_entries[i]); }
                    catch (Exception ex) { Log.Warn("FLIGHT_THRUST_RESTORE eid=" + _grid.EntityId
                        + " block=" + _entries[i].Block.EntityId + " error=" + ex.Message); }
                }
                _thrust.ControlThrust = Vector3.Zero;
                _thrust.DampenersEnabled = _dampeners;
                _thrust.AutopilotEnabled = true;
                _thrust.MarkDirty();
                if (DebugFlight)
                {
                    int restored = 0, total = 0;
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        Entry e = _entries[i];
                        if (e.Block.Closed || e.Block.MarkedForClose) continue;
                        total++;
                        float value = ((Sandbox.ModAPI.Ingame.IMyThrust)e.Block).ThrustOverridePercentage;
                        if (Math.Abs(value - e.Original) < 0.00001f) restored++;
                    }
                    Log.Info("FLIGHT_THRUST_RELEASE eid=" + _grid.EntityId + " restored=" + restored
                        + "/" + total + " dampeners=" + _thrust.DampenersEnabled
                        + " autopilot=" + _thrust.AutopilotEnabled);
                }
            }
        }

        private AxisThrust AvailableAxisThrust(MyCubeGrid grid, MyEntityThrustComponent thrust)
        {
            FlightState st;
            if (_controlled.TryGetValue(grid.EntityId, out st))
            {
                if (st.Analogue != null) return st.Analogue.Available;
                if (DateTime.UtcNow < st.ReleasedThrustAxesUntil) return st.ReleasedThrustAxes;
            }
            return ReadAxisThrust(thrust);
        }

        private static bool ReversedAxis(float current, float previous)
        {
            return Math.Abs(current) > 0.005f && Math.Abs(previous) > 0.005f && current * previous < 0;
        }

        private static void NoteThrustInput(FlightState st, Vector3 input)
        {
            if (!DebugFlight) return;
            Vector3 prior = st.TraceInput;
            float step = (input - prior).Length();
            if (step > 0.05f) st.ThrustChanges++;
            if (ReversedAxis(input.X, prior.X) || ReversedAxis(input.Y, prior.Y)
                || ReversedAxis(input.Z, prior.Z)) st.ThrustReversals++;
            if (step > st.ThrustMaxStep) st.ThrustMaxStep = step;
            st.TraceInput = input;
        }
    }
}
