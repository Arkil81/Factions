using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>The operator's node removal (<c>factions removenode</c>): an owned node goes, and its
    /// owner's parked hulls fly to the owner's other ground instead of going with it — the difference
    /// from the bare <see cref="WorldModel.RemoveNode"/> the UNKN sweep uses, which destroys them.</summary>
    public class RetireNodeTests
    {
        [Fact]
        public void RetireNode_RemovesTheNode_AndFliesItsOwnersHullsHome()
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.7, "N389");
            n.OwnerFactionTag = "METC";
            n.ShipStock["Idris"] = 2;
            n.Inventory["Ore:Ice"] = 500;
            SpatialNode home = w.Graph.Add(new Vec3(50, 0, 0), 0.7, "N094");
            home.OwnerFactionTag = "METC";
            w.Graph.Connect("N389", "N094");
            var metc = new FactionState { Tag = "METC", RngSeed = 1 };

            RemovedNodeContents destroyed = w.RetireNode("N389", metc);

            Assert.False(w.Graph.Nodes.ContainsKey("N389"));
            Assert.DoesNotContain("N389", home.Neighbors);
            Assert.Equal(0, destroyed.Hulls);
            Assert.Equal(500, destroyed.ItemUnits);
            Assert.NotEmpty(w.ShipEvacuations);
        }

        [Fact]
        public void RetireNode_OnAMissingId_ChangesNothing()
        {
            WorldModel w = TestSupport.World();
            w.Graph.Add(new Vec3(0, 0, 0), 0.7, "N001");

            RemovedNodeContents destroyed = w.RetireNode("N999", null);

            Assert.Single(w.Graph.Nodes);
            Assert.Equal(0, destroyed.Hulls);
        }
    }
}
