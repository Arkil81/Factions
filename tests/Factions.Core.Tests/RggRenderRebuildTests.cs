using System.Collections.Generic;
using FactionsUI.GasGiants;
using RealGasGiants;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The client's rebuild of Real Gas Giants' render entities after a SeamlessClient switch closes them,
    /// against a stand-in with RGG's member names and shapes (Data/Scripts/RealGasGiants/GasGiantManager.cs,
    /// workshop 3232085677). The characteristic failure is a switch that leaves the sphere closed, so the
    /// player sees the voxel core: the sphere must be remade through RGG's own factory and restyled.
    /// </summary>
    public class RggRenderRebuildTests
    {
        private static RggRenderRebuild Bound()
        {
            RggRenderRebuild b = RggRenderRebuild.Bind(typeof(RealGasGiantsCore), out string missing);
            Assert.Null(missing);
            Assert.NotNull(b);
            return b;
        }

        private static GasGiantManager.GasGiantComponent Live(string name)
        {
            var c = new GasGiantManager.GasGiantComponent { storageName = name };
            c.CreatePlanetEntity(out c.parentEnt, out c.modelEnt);
            c.Created = 0;
            return c;
        }

        private static bool IsClosed(object e) => ((FakeEntity)e).Closed;

        [Fact]
        public void A_closed_sphere_is_remade_by_rggs_factory_and_restyled()
        {
            RggRenderRebuild b = Bound();
            GasGiantManager.GasGiantComponent c = Live("Cauldron-1225884968d19000");
            c.gasGiantConfigInfo.Radius = 180f;
            FakeEntity oldModel = c.modelEnt;
            c.parentEnt.Closed = c.modelEnt.Closed = true;
            GasGiantManager.Instance = new GasGiantManager { giantComps = { c } };

            var restyled = new List<(object comp, object model, float radiusKm, object skin)>();
            List<RggRenderRebuild.Rebuilt> done = b.Repair(IsClosed,
                (comp, model) => restyled.Add((comp, model, b.RadiusKmOf(comp), b.SkinOf(comp))));

            Assert.Equal(1, c.Created);
            Assert.NotSame(oldModel, c.modelEnt);
            Assert.False(c.parentEnt.Closed);
            var r = Assert.Single(done);
            Assert.Equal("Cauldron-1225884968d19000", r.Planet);
            Assert.True(r.Model);
            var s = Assert.Single(restyled);
            Assert.Same(c, s.comp);
            Assert.Same(c.modelEnt, s.model);
            Assert.Equal(180f, s.radiusKm);
            Assert.Same(c.gasGiantSkinInfo.skin, s.skin);
        }

        [Fact]
        public void A_closed_interior_or_occluder_is_cleared_for_rgg_to_remake_and_an_open_one_is_kept()
        {
            RggRenderRebuild b = Bound();
            GasGiantManager.GasGiantComponent c = Live("Cauldron");
            c.parentInteriorEnt = new FakeEntity { Closed = true };
            c.modelInteriorEnt = new FakeEntity { Closed = true };
            var occluder = new FakeEntity();
            c.parentOccluderEnt = occluder;
            c.modelOccluderEnt = new FakeEntity();
            GasGiantManager.Instance = new GasGiantManager { giantComps = { c } };

            var r = Assert.Single(b.Repair(IsClosed, (comp, model) => { }));

            Assert.False(r.Model);
            Assert.True(r.Interior);
            Assert.False(r.Occluder);
            Assert.Equal(0, c.Created);
            Assert.Null(c.parentInteriorEnt);
            Assert.Null(c.modelInteriorEnt);
            Assert.Same(occluder, c.parentOccluderEnt);
        }

        [Fact]
        public void Nothing_closed_does_nothing()
        {
            RggRenderRebuild b = Bound();
            GasGiantManager.GasGiantComponent c = Live("Cauldron");
            FakeEntity model = c.modelEnt;
            GasGiantManager.Instance = new GasGiantManager { giantComps = { c } };

            Assert.Empty(b.Repair(IsClosed, (comp, m) => Assert.Fail("restyled an open sphere")));
            Assert.Equal(0, c.Created);
            Assert.Same(model, c.modelEnt);
        }

        [Fact]
        public void No_manager_yet_does_nothing()
        {
            RggRenderRebuild b = Bound();
            GasGiantManager.Instance = null;
            Assert.Empty(b.Repair(IsClosed, (comp, m) => Assert.Fail("restyled without a manager")));
        }

        [Fact]
        public void An_assembly_without_rggs_manager_names_what_is_missing()
        {
            Assert.Null(RggRenderRebuild.Bind(typeof(object), out string missing));
            Assert.Equal(RggRenderRebuild.ManagerTypeName, missing);
        }
    }

    public sealed class FakeEntity
    {
        public bool Closed;
    }
}

// Stand-in for the members of Real Gas Giants the rebuild reads, with RGG's own names and the shapes that
// matter: an out-pair factory, a float radius in km, and a private colour field.
namespace RealGasGiants
{
    using System.Collections.Generic;
    using Factions.Core.Tests;

    public class RealGasGiantsCore { }

    public class GasGiantSkinInfo
    {
        public Dictionary<string, string> skin = new Dictionary<string, string>();
    }

    public class GasGiantConfigInfo
    {
        public float Radius = 180f;
    }

    public class GasGiantManager
    {
        public static GasGiantManager Instance;
        public List<GasGiantComponent> giantComps = new List<GasGiantComponent>();

        public class GasGiantComponent
        {
            public string storageName;
            public FakeEntity parentEnt, modelEnt, parentInteriorEnt, modelInteriorEnt, parentOccluderEnt, modelOccluderEnt;
#pragma warning disable 169, 414
            private float planetColorHsv = 0.5f;
#pragma warning restore 169, 414
            public GasGiantSkinInfo gasGiantSkinInfo = new GasGiantSkinInfo();
            public GasGiantConfigInfo gasGiantConfigInfo = new GasGiantConfigInfo();
            public int Created;

            public void CreatePlanetEntity(out FakeEntity parentEnt, out FakeEntity planetEnt)
            {
                Created++;
                parentEnt = new FakeEntity();
                planetEnt = new FakeEntity();
            }
        }
    }
}
