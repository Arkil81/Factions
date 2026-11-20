using System.Collections.Generic;
using Factions.Wire;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Game.Components;

namespace FactionsUI;

/// <summary>
/// Applies the server's missile munition speed scale to this client's own ammo definitions.
///
/// <para><b>Why the client needs telling at all.</b> A missile is not a streamed entity: the server
/// broadcasts an object builder (<c>MyMultiplayer.RaiseEvent(... OnShootMissile, builder)</c>) and
/// every client then simulates its own copy, writing
/// <c>RigidBody.MaxLinearVelocity = DesiredSpeed</c> from its OWN
/// <c>MyMissileAmmoDefinition</c>. So a server that multiplies its ammo speeds changes nothing here,
/// and this client's copy of the same round flies slower than the one the server is scoring hits
/// with — the round explodes in one place on the server and somewhere behind it on screen.</para>
///
/// <para><b>The arithmetic is the server's, restated.</b> Speed scales linearly and acceleration by
/// the square, so a round's spin-up <i>distance</i> is unchanged and only its flight <i>time</i>
/// compresses — <c>Factions.Core.MunitionSpeedPolicy</c> carries the reasoning. Core is not compiled
/// into this plugin (only <c>Factions.Wire</c> and <c>Factions.Shared</c> are), so the two
/// multiplications live here in full rather than behind a reference.</para>
///
/// <para><b>Baselines, for the same reason the server keeps them.</b> The operator can change the
/// scale live, and a definition that has already been multiplied is not what the next multiplier
/// applies to.</para>
/// </summary>
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class MunitionSpeedComponent : MySessionComponentBase
{
    private readonly struct Baseline
    {
        public Baseline(float desired, float initial, float acceleration)
        {
            Desired = desired;
            Initial = initial;
            Acceleration = acceleration;
        }

        public float Desired { get; }
        public float Initial { get; }
        public float Acceleration { get; }
    }

    private static readonly Dictionary<MyDefinitionId, Baseline> Baselines = new();

    /// <summary>What the server last told us, for the diagnostic line. One before the first message,
    /// which is also what an unlinked or ungranted client keeps.</summary>
    public static double Scale { get; private set; } = 1.0;

    public override void LoadData()
    {
        UplinkComponent.RegisterHandler(WireMessageKind.MunitionSpeedConfig, OnConfig);
    }

    private static void OnConfig(byte[] data)
    {
        if (!FactionsWire.TryReadMunitionSpeedConfig(data, out double scale)) return;

        // A zero or negative multiplier would leave every round hanging in the air, and the server
        // only ever sends a clamped positive value -- so treat it as a malformed message rather than
        // an instruction.
        if (scale <= 0.0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            Plugin.Log("MUNITION_SPEED_CONFIG ignored -- implausible scale " + scale);
            return;
        }

        if (MyDefinitionManager.Static == null)
        {
            Plugin.Log("MUNITION_SPEED_CONFIG ignored -- no definitions yet.");
            return;
        }

        int scaled = 0;
        float fastest = 0f;
        string fastestName = null;
        foreach (MyMissileAmmoDefinition def in MissileAmmo())
        {
            if (!Baselines.TryGetValue(def.Id, out Baseline b))
            {
                b = new Baseline(def.DesiredSpeed, def.MissileInitialSpeed, def.MissileAcceleration);
                Baselines[def.Id] = b;
            }

            def.DesiredSpeed = (float)(b.Desired * scale);
            def.MissileInitialSpeed = (float)(b.Initial * scale);
            def.MissileAcceleration = (float)(b.Acceleration * scale * scale);
            scaled++;

            if (def.DesiredSpeed <= fastest) continue;
            fastest = def.DesiredSpeed;
            fastestName = def.Id.SubtypeName;
        }

        Scale = scale;
        Plugin.Log("MUNITION_SPEED_CONFIG scale=" + scale + " over " + scaled + " missile ammo definition(s)"
                   + (fastestName != null ? "; fastest " + fastestName + " " + fastest + " m/s" : ""));
    }

    /// <summary>Every missile ammo definition this client can simulate. Reached through the ammo
    /// magazines that name it — <c>MyDefinitionManager</c> exposes no ammo enumerator — and through
    /// the typed store as well, because SE's resolved definitions live in two disjoint dictionaries
    /// and neither is the whole picture.</summary>
    private static IEnumerable<MyMissileAmmoDefinition> MissileAmmo()
    {
        Dictionary<MyDefinitionId, MyMissileAmmoDefinition> found = new();

        foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions())
            AddFromMagazine(def, found);
        foreach (MyDefinitionBase def in MyDefinitionManager.Static.GetAllDefinitions<MyDefinitionBase>())
            AddFromMagazine(def, found);

        foreach (MyAmmoDefinition def in MyDefinitionManager.Static.GetAllDefinitions<MyAmmoDefinition>())
            if (def is MyMissileAmmoDefinition missile)
                found[missile.Id] = missile;

        return found.Values;
    }

    private static void AddFromMagazine(MyDefinitionBase def,
                                        Dictionary<MyDefinitionId, MyMissileAmmoDefinition> found)
    {
        if (def is not MyAmmoMagazineDefinition magazine) return;
        if (!MyDefinitionManager.Static.TryGetAmmoDefinition(magazine.AmmoDefinitionId, out MyAmmoDefinition ammo)) return;
        if (ammo is MyMissileAmmoDefinition missile) found[missile.Id] = missile;
    }
}
