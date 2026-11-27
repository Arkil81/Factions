using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class AdminIntelViewTests
    {
        [Fact]
        public void Off_ForEveryAdmin_UntilTheyTurnItOn()
        {
            var v = new AdminIntelView();
            Assert.False(v.IsOn(76561198000000001UL));
            Assert.True(v.Set(76561198000000001UL, true));
            Assert.True(v.IsOn(76561198000000001UL));
            Assert.False(v.IsOn(76561198000000002UL));
            Assert.False(v.Set(76561198000000001UL, true));
            Assert.True(v.Set(76561198000000001UL, false));
            Assert.False(v.IsOn(76561198000000001UL));
        }

        [Fact]
        public void Choice_SurvivesTheFile_AndTheSteamIdKeepsFullPrecision()
        {
            var v = new AdminIntelView();
            v.Set(76561198012345679UL, true);
            AdminIntelView back = AdminIntelView.FromJson(v.ToJson());
            Assert.True(back.IsOn(76561198012345679UL));
            Assert.False(back.IsOn(76561198012345678UL));
            Assert.Equal(1, back.Count);
        }

        [Fact]
        public void AMissingOrEmptyFile_IsEveryoneOff()
        {
            Assert.Equal(0, AdminIntelView.FromJson(null).Count);
            Assert.Equal(0, AdminIntelView.FromJson("{}").Count);
            Assert.False(AdminIntelView.FromJson("{\"On\":[\"x\"]}").IsOn(0UL));
        }

        [Fact]
        public void SteamIdZero_IsNeverOn()
        {
            var v = new AdminIntelView();
            Assert.False(v.Set(0UL, true));
            Assert.False(v.IsOn(0UL));
        }
    }
}
