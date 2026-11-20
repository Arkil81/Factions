using System;
using System.Collections.Generic;
using System.Globalization;
using Factions.Core;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Keeps crew on the deck of a moving hull in zero gravity (docs/ground-operations.md, Moving hulls).
    ///
    /// <para>In zero gravity SE's magnetic boots apply no force toward the floor: the character is
    /// carried by Havok matching it to the deck it stands on, and once it is off the deck nothing on a
    /// dedicated server brings it back. A player's own client switches on jetpack and dampeners when
    /// he loses the floor; that code is skipped on a dedicated server, so a bot drifts on its last
    /// velocity until it leaves the hull.</para>
    ///
    /// <para>Every frame, each crewman on his hull is read for support (the raw Havok flag). Four
    /// frames without it in zero gravity, near his hull, is a loss; back on the floor within half a
    /// second is <c>back</c>, by jetpack <c>landed</c>, out of reach <c>departed</c>. Reattach:
    /// for half a second his velocity is set to the hull's at
    /// his position plus 0.7 m/s back toward the floor he left; still off it after that, his jetpack
    /// carries him back to where he last stood at up to 1.5 m/s, relative to the hull, and goes off
    /// within 0.4 m of it. Losses are counted whether the switch is on or not.</para>
    /// </summary>
    internal static class CrewFooting
    {
        private const int LossFrames = 4;
        private const int ReattachFrames = 30;
        private const int GiveUpFrames = 600;
        private const float PushMs = 0.7f;
        private const float ReturnMs = 1.5f;
        private const double LandM = 0.4;
        private const double NearHullM = 3.0;
        private const double ZeroGravity = 0.5;
        private const int Trail = 15;

        private sealed class Foot
        {
            public MyCubeGrid Hull;
            public int Unsupported;
            public bool Lost, Jetpack;
            public int LostFor;
            public bool HasStood;
            public Vector3D FeetLocal;
            public Vector3D NormalLocal;
            public Vector3 HullVelocity;
            public bool HasHullVelocity;
            public int Losses, Back, Jetpacked, Landed, Departed;
            public readonly float[] AwayMs = new float[Trail];
            public readonly float[] HullStepMs = new float[Trail];
            public readonly int[] FrameMod = new int[Trail];
            public int TrailAt;
        }

        private static readonly Dictionary<long, Foot> Feet = new Dictionary<long, Foot>();
        private static readonly List<MyAgentBot> Bots = new List<MyAgentBot>();
        private static readonly List<MyCubeGrid> Hulls = new List<MyCubeGrid>();
        private static readonly List<long> Gone = new List<long>();
        private static int _frame;

        /// <summary><c>factions debug footing</c>: one line per loss and per outcome.</summary>
        internal static bool Debug;

        internal static void Tick()
        {
            _frame++;
            Bots.Clear();
            Hulls.Clear();
            CrewPresence.FillCrew(Bots, Hulls);
            if (Bots.Count == 0) { if (Feet.Count > 0) Feet.Clear(); return; }
            for (int i = 0; i < Bots.Count; i++)
            {
                try { Step(Bots[i], Hulls[i]); }
                catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Crew footing update failed."); }
            }
            if ((_frame & 255) != 0) return;
            Gone.Clear();
            foreach (KeyValuePair<long, Foot> entry in Feet)
                if (entry.Value.Hull == null || entry.Value.Hull.Closed || MyEntities.GetEntityById(entry.Key) == null) Gone.Add(entry.Key);
            foreach (long id in Gone) Feet.Remove(id);
        }

        private static void Step(MyAgentBot bot, MyCubeGrid hull)
        {
            MyCharacter character = bot != null ? bot.AgentEntity : null;
            if (character == null || character.IsDead || character.MarkedForClose || hull == null || hull.Physics == null) return;
            Foot foot;
            if (!Feet.TryGetValue(character.EntityId, out foot)) { foot = new Foot { Hull = hull }; Feet[character.EntityId] = foot; }
            foot.Hull = hull;
            Vector3 hullVelocity = hull.Physics.LinearVelocity;
            float hullStep = foot.HasHullVelocity ? (hullVelocity - foot.HullVelocity).Length() : 0f;
            foot.HullVelocity = hullVelocity;
            foot.HasHullVelocity = true;
            if (character.Parent != null || character.Physics == null || character.Physics.CharacterProxy == null
                || CrewOrderControl.IsCrossing(character) || CrewCombat.IsRecovering(character))
            {
                foot.Unsupported = 0;
                if (foot.Lost) End(foot, character, "seated_or_driven");
                return;
            }
            Vector3D at = character.PositionComp.GetPosition();
            MatrixD world = hull.WorldMatrix;
            Vector3 relative = character.Physics.LinearVelocity - hull.Physics.GetVelocityAtPoint(at);
            Vector3D normal = foot.HasStood ? Vector3D.TransformNormal(foot.NormalLocal, world) : world.Up;
            int slot = foot.TrailAt++ % Trail;
            foot.AwayMs[slot] = (float)Vector3D.Dot(relative, normal);
            foot.HullStepMs[slot] = hullStep;
            foot.FrameMod[slot] = _frame % 60;

            if (character.Physics.CharacterProxy.Supported)
            {
                foot.Unsupported = 0;
                Vector3 support = character.Physics.CharacterProxy.SupportNormal;
                if (support.LengthSquared() > 0.25f)
                {
                    foot.NormalLocal = Vector3D.TransformNormal(support, hull.PositionComp.WorldMatrixNormalizedInv);
                    foot.FeetLocal = Vector3D.Transform(at, hull.PositionComp.WorldMatrixNormalizedInv);
                    foot.HasStood = true;
                }
                if (foot.Lost) End(foot, character, foot.Jetpack ? "landed" : "back");
                return;
            }
            if (++foot.Unsupported < LossFrames || !foot.HasStood) return;
            if (MyGravityProviderSystem.CalculateTotalGravityInPoint(at).Length() >= ZeroGravity) return;
            if (!foot.Lost)
            {
                if (!NearHull(hull, at)) return;
                foot.Lost = true;
                foot.LostFor = 0;
                foot.Losses++;
                if (Debug) LogLoss(foot, character, hull, relative, normal);
            }
            foot.LostFor++;
            if ((!NearHull(hull, at) && foot.LostFor > 60) || foot.LostFor > GiveUpFrames)
            {
                foot.Departed++;
                End(foot, character, "departed");
                return;
            }
            Vector3D feet = Vector3D.Transform(foot.FeetLocal, world);
            if (foot.LostFor <= ReattachFrames)
            {
                character.Physics.LinearVelocity = hull.Physics.GetVelocityAtPoint(at) - (Vector3)normal * PushMs;
                return;
            }
            if (!foot.Jetpack && character.JetpackComp != null)
            {
                character.JetpackComp.TurnOnJetpack(true);
                foot.Jetpack = character.JetpackComp.TurnedOn;
                if (foot.Jetpack) foot.Jetpacked++;
            }
            Vector3D back = feet - at;
            double distance = back.Length();
            if (foot.Jetpack && distance <= LandM)
            {
                End(foot, character, "landed");
                return;
            }
            Vector3 toward = distance > 1e-3 ? (Vector3)(back / distance) * (float)Math.Min(ReturnMs, distance * 2) : Vector3.Zero;
            character.Physics.LinearVelocity = hull.Physics.GetVelocityAtPoint(at) + toward;
        }

        private static void End(Foot foot, MyCharacter character, string how)
        {
            if (how == "back") foot.Back++;
            else if (how == "landed") foot.Landed++;
            if (foot.Jetpack && character.JetpackComp != null) character.JetpackComp.TurnOnJetpack(false);
            if (Debug)
                FactionsPlugin.Log.Info("CREW_FOOTING_END character=" + character.EntityId + " how=" + how
                    + " frames=" + foot.LostFor + " jetpack=" + foot.Jetpack);
            foot.Lost = false;
            foot.Jetpack = false;
            foot.LostFor = 0;
            foot.Unsupported = 0;
        }

        private static bool NearHull(MyCubeGrid hull, Vector3D at)
        {
            Vector3D local = Vector3D.Transform(at, hull.PositionComp.WorldMatrixNormalizedInv);
            BoundingBoxD box = hull.PositionComp.LocalAABB;
            box.Inflate(NearHullM);
            return box.Contains(local) == ContainmentType.Contains;
        }

        /// <summary>The 15 frames before the loss: the largest speed away from the floor relative to
        /// the hull, the largest one-frame change in the hull's velocity, and where in the 60-frame
        /// cycle that change fell.</summary>
        private static void LogLoss(Foot foot, MyCharacter character, MyCubeGrid hull, Vector3 relative, Vector3D normal)
        {
            float away = float.MinValue, step = 0f;
            int stepMod = -1;
            for (int i = 0; i < Trail; i++)
            {
                away = Math.Max(away, foot.AwayMs[i]);
                if (foot.HullStepMs[i] > step) { step = foot.HullStepMs[i]; stepMod = foot.FrameMod[i]; }
            }
            double along = Vector3D.Dot(relative, normal);
            double across = (relative - (Vector3)normal * (float)along).Length();
            FactionsPlugin.Log.Info("CREW_FOOTING_LOST character=" + character.EntityId + " hull=" + hull.EntityId
                + " away_ms=" + N(along) + " along_floor_ms=" + N(across) + " away_max_ms=" + N(away)
                + " hull_step_max_ms=" + N(step) + " at_frame_mod60=" + stepMod
                + " hull_speed_ms=" + N(hull.Physics.LinearVelocity.Length())
                + " hull_spin_rad_s=" + N(hull.Physics.AngularVelocity.Length()));
        }

        private static string N(double v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>One crewman's footing for <c>crew visits</c>.</summary>
        internal static string Describe(long characterId)
        {
            Foot foot;
            if (!Feet.TryGetValue(characterId, out foot)) return " footing=unread";
            return " footing=" + (foot.Jetpack ? "jetpack" : foot.Lost ? "lost" : "floor")
                + " losses=" + foot.Losses + " back=" + foot.Back + " jetpacked=" + foot.Jetpacked
                + " landed=" + foot.Landed + " departed=" + foot.Departed;
        }

        internal static void Close()
        {
            Feet.Clear();
            Bots.Clear();
            Hulls.Clear();
        }
    }
}
