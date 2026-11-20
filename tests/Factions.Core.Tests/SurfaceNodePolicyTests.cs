using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class SurfaceNodePolicyTests
    {
        private static WorldRecipe Recipe(params RecipePlanet[] planets)
        {
            var r = new WorldRecipe();
            r.Planets = new List<RecipePlanet>(planets);
            return r;
        }

        private static SurfaceNodePolicy Build(WorldRecipe r)
        {
            SurfaceNodePolicy p;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(r, out p, out why), why);
            return p;
        }

        [Fact]
        public void AuthoredFalse_ForbidsThatGenerator()
        {
            SurfaceNodePolicy p = Build(Recipe(
                new RecipePlanet { Name = "Cauldron", Definition = "RealGasGiant", AllowSurfaceNodes = false }));

            // seFlag true is the live case: SE says landable, the recipe overrules it.
            Assert.False(p.AllowsSurface("RealGasGiant", seFlag: true));
        }

        [Fact]
        public void AuthoredTrue_AllowsEvenWhenSeSaysNo()
        {
            SurfaceNodePolicy p = Build(Recipe(
                new RecipePlanet { Name = "Odd", Definition = "Weird", AllowSurfaceNodes = true }));

            Assert.True(p.AllowsSurface("Weird", seFlag: false));
        }

        [Fact]
        public void UnauthoredGenerator_DefersToTheSeFlag_BothWays()
        {
            SurfaceNodePolicy p = Build(Recipe(
                new RecipePlanet { Name = "Cauldron", Definition = "RealGasGiant", AllowSurfaceNodes = false }));

            Assert.True(p.AllowsSurface("Tellus", seFlag: true));
            Assert.False(p.AllowsSurface("Tellus", seFlag: false));
        }

        [Fact]
        public void EmptyRecipe_LeavesEveryBodyToSe()
        {
            SurfaceNodePolicy p = Build(Recipe());
            Assert.Equal(0, p.Count);
            Assert.True(p.AllowsSurface("RealGasGiant", seFlag: true));
        }

        [Fact]
        public void NullRecipe_IsNotAnError_AndDefersToSe()
        {
            SurfaceNodePolicy p;
            string why;
            Assert.True(SurfaceNodePolicy.TryBuild(null, out p, out why));
            Assert.Null(why);
            Assert.True(p.AllowsSurface("RealGasGiant", seFlag: true));
        }

        [Fact]
        public void PlanetsWithNoOpinion_ContributeNothing()
        {
            SurfaceNodePolicy p = Build(Recipe(
                new RecipePlanet { Name = "Tellus", Definition = "Tellus" },
                new RecipePlanet { Name = "Cauldron", Definition = "RealGasGiant", AllowSurfaceNodes = false }));

            Assert.Equal(1, p.Count);
        }

        [Fact]
        public void TwoEntriesSharingAGenerator_MustAgree()
        {
            SurfaceNodePolicy p = Build(Recipe(
                new RecipePlanet { Name = "A", Definition = "RealGasGiant", AllowSurfaceNodes = false },
                new RecipePlanet { Name = "B", Definition = "RealGasGiant", AllowSurfaceNodes = false }));

            Assert.Equal(1, p.Count);
            Assert.False(p.AllowsSurface("RealGasGiant", seFlag: true));
        }

        [Fact]
        public void ContradictoryEntries_AreRejectedRatherThanLastWins()
        {
            SurfaceNodePolicy p;
            string why;
            bool ok = SurfaceNodePolicy.TryBuild(Recipe(
                new RecipePlanet { Name = "A", Definition = "RealGasGiant", AllowSurfaceNodes = false },
                new RecipePlanet { Name = "B", Definition = "RealGasGiant", AllowSurfaceNodes = true }),
                out p, out why);

            Assert.False(ok);
            Assert.Null(p);
            Assert.Contains("RealGasGiant", why);
        }

        [Fact]
        public void AnOpinionWithNoDefinition_IsRejected()
        {
            SurfaceNodePolicy p;
            string why;
            bool ok = SurfaceNodePolicy.TryBuild(Recipe(
                new RecipePlanet { Name = "Nameless", AllowSurfaceNodes = false }), out p, out why);

            Assert.False(ok);
            Assert.Contains("Nameless", why);
        }

        [Fact]
        public void GeneratorMatchIsCaseInsensitive()
        {
            SurfaceNodePolicy p = Build(Recipe(
                new RecipePlanet { Name = "Cauldron", Definition = "RealGasGiant", AllowSurfaceNodes = false }));

            Assert.False(p.AllowsSurface("realgasgiant", seFlag: true));
        }

        [Fact]
        public void NullOrEmptyGenerator_DefersToSe()
        {
            SurfaceNodePolicy p = Build(Recipe(
                new RecipePlanet { Name = "Cauldron", Definition = "RealGasGiant", AllowSurfaceNodes = false }));

            Assert.True(p.AllowsSurface(null, seFlag: true));
            Assert.True(p.AllowsSurface("", seFlag: true));
        }
    }
}
