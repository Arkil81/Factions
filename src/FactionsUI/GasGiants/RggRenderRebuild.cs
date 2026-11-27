using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace FactionsUI.GasGiants;

/// <summary>
/// Real Gas Giants' client render state, reached by reflection from its live session component, and the
/// rebuild of the entities a SeamlessClient server switch closes. SE-free: the closed-entity test and the
/// restyle of a new model are passed in.
///
/// <para><b>What RGG draws with.</b> The 180 km body players see is not the planet. Each
/// <c>GasGiantManager.GasGiantComponent</c> owns client-only entities of its own: the sphere
/// (<c>parentEnt</c>/<c>modelEnt</c>), made once in the component's constructor, and the interior and
/// lens-flare occluder (<c>parentInteriorEnt</c>/<c>parentOccluderEnt</c>), made lazily while their slot
/// is null. Rings are billboards and own no entity.</para>
///
/// <para><b>What a switch does to them.</b> SeamlessClient's switch closes every entity except planets and
/// characters and keeps the session. The planet stays in the scene, so RGG never retires the component and
/// never makes a new one; the closed entities stay closed and only the 9.5 km voxel core draws until a
/// relog. A rebuild through RGG's own <c>CreatePlanetEntity</c>, plus clearing the two lazy slots so RGG
/// remakes them, restores what a fresh load builds.</para>
/// </summary>
internal sealed class RggRenderRebuild
{
    internal const string CoreTypeName = "RealGasGiants.RealGasGiantsCore";
    internal const string ManagerTypeName = "RealGasGiants.GasGiantManager";
    internal const string ComponentTypeName = "GasGiantComponent";

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private FieldInfo _instance, _giantComps, _storageName;
    private FieldInfo _parent, _model, _parentInterior, _modelInterior, _parentOccluder, _modelOccluder;
    private FieldInfo _colorHsv, _skinInfo, _skin, _configInfo, _radiusKm;
    private MethodInfo _create;

    private RggRenderRebuild() { }

    /// <summary>One component whose entities were rebuilt or cleared.</summary>
    internal struct Rebuilt
    {
        public string Planet;
        public bool Model;
        public bool Interior;
        public bool Occluder;
    }

    /// <summary>Binds against the assembly <paramref name="coreType"/> was compiled into. Null, with the first
    /// member that did not resolve in <paramref name="missing"/>, when RGG's shape is not the one this
    /// rebuild was written against.</summary>
    internal static RggRenderRebuild Bind(Type coreType, out string missing)
    {
        missing = null;
        Type manager = coreType?.Assembly.GetType(ManagerTypeName);
        if (manager == null) { missing = ManagerTypeName; return null; }
        Type comp = manager.GetNestedType(ComponentTypeName, BindingFlags.Public | BindingFlags.NonPublic);
        if (comp == null) { missing = ManagerTypeName + "." + ComponentTypeName; return null; }

        var b = new RggRenderRebuild
        {
            _instance = manager.GetField("Instance", AnyStatic),
            _giantComps = manager.GetField("giantComps", Any),
            _storageName = comp.GetField("storageName", Any),
            _parent = comp.GetField("parentEnt", Any),
            _model = comp.GetField("modelEnt", Any),
            _parentInterior = comp.GetField("parentInteriorEnt", Any),
            _modelInterior = comp.GetField("modelInteriorEnt", Any),
            _parentOccluder = comp.GetField("parentOccluderEnt", Any),
            _modelOccluder = comp.GetField("modelOccluderEnt", Any),
            _colorHsv = comp.GetField("planetColorHsv", Any),
            _skinInfo = comp.GetField("gasGiantSkinInfo", Any),
            _configInfo = comp.GetField("gasGiantConfigInfo", Any),
            _create = comp.GetMethod("CreatePlanetEntity", Any),
        };
        b._skin = b._skinInfo?.FieldType.GetField("skin", Any);
        b._radiusKm = b._configInfo?.FieldType.GetField("Radius", Any);

        missing = b.FirstMissing();
        return missing == null ? b : null;
    }

    private string FirstMissing()
    {
        if (_instance == null) return ManagerTypeName + ".Instance";
        if (_giantComps == null || !typeof(IList).IsAssignableFrom(_giantComps.FieldType)) return ManagerTypeName + ".giantComps";
        if (_storageName == null) return ComponentTypeName + ".storageName";
        if (_parent == null) return ComponentTypeName + ".parentEnt";
        if (_model == null) return ComponentTypeName + ".modelEnt";
        if (_parentInterior == null) return ComponentTypeName + ".parentInteriorEnt";
        if (_modelInterior == null) return ComponentTypeName + ".modelInteriorEnt";
        if (_parentOccluder == null) return ComponentTypeName + ".parentOccluderEnt";
        if (_modelOccluder == null) return ComponentTypeName + ".modelOccluderEnt";
        if (_colorHsv == null) return ComponentTypeName + ".planetColorHsv";
        if (_skin == null) return ComponentTypeName + ".gasGiantSkinInfo.skin";
        if (_radiusKm == null || _radiusKm.FieldType != typeof(float)) return ComponentTypeName + ".gasGiantConfigInfo.Radius";
        if (_create == null) return ComponentTypeName + ".CreatePlanetEntity";
        ParameterInfo[] p = _create.GetParameters();
        if (p.Length != 2 || !p[0].IsOut || !p[1].IsOut) return ComponentTypeName + ".CreatePlanetEntity(out, out)";
        return null;
    }

    /// <summary>The sphere's colour mask, as RGG computed it from the planet's config.</summary>
    internal object ColorHsvOf(object comp) => _colorHsv.GetValue(comp);

    /// <summary>The sphere's texture changes for its configured skin.</summary>
    internal object SkinOf(object comp) => _skin.GetValue(_skinInfo.GetValue(comp));

    /// <summary>The virtual body's radius in km, which RGG scales the sphere model by (times 1000).</summary>
    internal float RadiusKmOf(object comp) => (float)_radiusKm.GetValue(_configInfo.GetValue(comp));

    /// <summary>Rebuilds every gas giant whose sphere is closed, and clears every closed interior or occluder
    /// slot so RGG remakes it. <paramref name="restyle"/> receives the component and the new model entity, to
    /// apply what RGG's constructor applies after <c>CreatePlanetEntity</c>. Nothing closed, nothing done.</summary>
    internal List<Rebuilt> Repair(Func<object, bool> isClosed, Action<object, object> restyle)
    {
        var done = new List<Rebuilt>();
        object manager = _instance.GetValue(null);
        if (manager == null) return done;
        if (!(_giantComps.GetValue(manager) is IList comps)) return done;

        foreach (object comp in comps)
        {
            if (comp == null) continue;
            var r = new Rebuilt { Planet = _storageName.GetValue(comp) as string };

            if (IsGone(_parent.GetValue(comp), isClosed) || IsGone(_model.GetValue(comp), isClosed))
            {
                var args = new object[2];
                _create.Invoke(comp, args);
                _parent.SetValue(comp, args[0]);
                _model.SetValue(comp, args[1]);
                restyle(comp, args[1]);
                r.Model = true;
            }
            r.Interior = ClearIfClosed(comp, _parentInterior, _modelInterior, isClosed);
            r.Occluder = ClearIfClosed(comp, _parentOccluder, _modelOccluder, isClosed);

            if (r.Model || r.Interior || r.Occluder) done.Add(r);
        }
        return done;
    }

    /// <summary>A sphere slot that is empty on a client is gone too: RGG fills it in the constructor and never
    /// clears it while the planet is in the scene.</summary>
    private static bool IsGone(object entity, Func<object, bool> isClosed) => entity == null || isClosed(entity);

    private static bool ClearIfClosed(object comp, FieldInfo parent, FieldInfo model, Func<object, bool> isClosed)
    {
        object p = parent.GetValue(comp);
        object m = model.GetValue(comp);
        if ((p == null || !isClosed(p)) && (m == null || !isClosed(m))) return false;
        parent.SetValue(comp, null);
        model.SetValue(comp, null);
        return true;
    }
}
