using System;
using System.Linq;
using Xunit;

namespace Factions.Core.Tests
{
    public sealed class DemandRegisterTests
    {
        [Fact]
        public void Post_RefreshesOnlyTheExactCompositeTriple()
        {
            var demand = new DemandRegister();
            DateTime firstExpiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime refreshedExpiry = firstExpiry.AddMinutes(1);

            Assert.True(demand.Post(new DemandEntry
            {
                FactionTag = "F|K", Kind = "Item:X", NodeId = "N", Quantity = 3, ExpiresUtc = firstExpiry,
            }));
            Assert.True(demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "K|Item:X", NodeId = "N", Quantity = 7, ExpiresUtc = firstExpiry,
            }));
            Assert.False(demand.Post(new DemandEntry
            {
                FactionTag = "F|K", Kind = "Item:X", NodeId = "N", Quantity = 11, ExpiresUtc = refreshedExpiry,
            }));

            Assert.Equal(2, demand.Count);
            DemandEntry refreshed = Assert.Single(demand.GetFor("F|K", "Item:X"));
            Assert.Equal(11, refreshed.Quantity);
            Assert.Equal(refreshedExpiry, refreshed.ExpiresUtc);
        }

        [Fact]
        public void ConsumeAndPrune_KeepExactLookupAndSnapshotsConsistent()
        {
            var demand = new DemandRegister();
            DateTime now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Steel", NodeId = "A", Quantity = 1, ExpiresUtc = now.AddMinutes(5) });
            demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Nickel", NodeId = "B", Quantity = 2, ExpiresUtc = now.AddMinutes(-1) });
            demand.Post(new DemandEntry { FactionTag = "F", Kind = "Ship:Trade", NodeId = "A", Quantity = 0, ExpiresUtc = now.AddMinutes(5) });

            demand.Consume("F", "Item:Steel", "A");
            demand.Prune(now);

            Assert.Equal(1, demand.Count);
            Assert.Empty(demand.SnapshotItemDemands("F"));
            Assert.Single(demand.SnapshotShipDemands("F"));
            Assert.Equal("Ship:Trade", demand.SnapshotShipDemands("F").Single().Kind);
        }

        [Fact]
        public void Post_DifferentDestNodeId_SameOriginAndKind_KeepsBothDemandsDistinct()
        {
            // DestNodeId is part of the dedup key so two route-carrying demands from the same
            // origin node (e.g. two blocked hauls needing different destinations) don't silently
            // overwrite each other the way they would if only (FactionTag, Kind, NodeId) were keyed.
            var demand = new DemandRegister();
            DateTime expiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.True(demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "A", DestNodeId = "Dest1", ExpiresUtc = expiry,
            }));
            Assert.True(demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "A", DestNodeId = "Dest2", ExpiresUtc = expiry,
            }));

            Assert.Equal(2, demand.Count);
            Assert.Equal(2, demand.SnapshotShipDemands("F").Count);

            demand.Consume("F", "Ship:Trade", "A", "Dest1");

            DemandEntry remaining = Assert.Single(demand.SnapshotShipDemands("F"));
            Assert.Equal("Dest2", remaining.DestNodeId);
        }

        [Fact]
        public void Post_NullDestNodeId_StillDedupsAgainstAnotherNullDestNodeId_RegressionGuard()
        {
            // No-destination demands (e.g. the proactive per-Shipyard "keep this zone warm" check)
            // must keep refreshing the same entry, not accumulate one per tick.
            var demand = new DemandRegister();
            DateTime firstExpiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime refreshedExpiry = firstExpiry.AddMinutes(1);

            Assert.True(demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "A", ExpiresUtc = firstExpiry,
            }));
            Assert.False(demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Trade", NodeId = "A", ExpiresUtc = refreshedExpiry,
            }));

            Assert.Equal(1, demand.Count);
            Assert.Equal(refreshedExpiry, demand.SnapshotShipDemands("F").Single().ExpiresUtc);
        }

        [Fact]
        public void TryGet_ReturnsOnlyTheExactIndexedDemand_AndTracksCompaction()
        {
            var demand = new DemandRegister();
            DateTime expiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Haul", NodeId = "A", DestNodeId = "D1",
                MinCargoLiters = 90, ExpiresUtc = expiry,
            });
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Ship:Haul", NodeId = "A", DestNodeId = "D2",
                MinCargoLiters = 30, ExpiresUtc = expiry,
            });

            DemandEntry exact;
            Assert.True(demand.TryGet("F", "Ship:Haul", "A", "D2", out exact));
            Assert.Equal(30, exact.MinCargoLiters);
            Assert.False(demand.TryGet("F", "Ship:Haul", "A", "missing", out exact));

            demand.Consume("F", "Ship:Haul", "A", "D1");

            Assert.True(demand.TryGet("F", "Ship:Haul", "A", "D2", out exact));
            Assert.Equal(30, exact.MinCargoLiters);
        }

        [Fact]
        public void ConsumeAll_RemovesEverySiblingDestForTheSameNodeAndKind()
        {
            var demand = new DemandRegister();
            var expiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // ShipyardManager keeps one BuildQueueEntry per (node, role), so these three are one unit
            // of need -- retiring only the key that drove the build leaves siblings to re-drive it.
            demand.Post(DemandEntry.ForShip("F", "Trade", "A", expiry, destNodeId: "D1"));
            demand.Post(DemandEntry.ForShip("F", "Trade", "A", expiry, destNodeId: "D2"));
            demand.Post(DemandEntry.ForShip("F", "Trade", "A", expiry, destNodeId: null));
            // Must survive: different node, different kind, different faction.
            demand.Post(DemandEntry.ForShip("F", "Trade", "B", expiry, destNodeId: "D1"));
            demand.Post(DemandEntry.ForShip("F", "Mine", "A", expiry, destNodeId: "D1"));
            demand.Post(DemandEntry.ForShip("G", "Trade", "A", expiry, destNodeId: "D1"));
            Assert.Equal(6, demand.Count);

            Assert.Equal(3, demand.ConsumeAll("F", "Ship:Trade", "A"));
            Assert.Equal(3, demand.Count);

            DemandEntry survivor;
            Assert.False(demand.TryGet("F", "Ship:Trade", "A", "D1", out survivor));
            Assert.False(demand.TryGet("F", "Ship:Trade", "A", "D2", out survivor));
            Assert.False(demand.TryGet("F", "Ship:Trade", "A", null, out survivor));
            Assert.True(demand.TryGet("F", "Ship:Trade", "B", "D1", out survivor));
            Assert.True(demand.TryGet("F", "Ship:Mine", "A", "D1", out survivor));
            Assert.True(demand.TryGet("G", "Ship:Trade", "A", "D1", out survivor));

            Assert.Equal(0, demand.ConsumeAll("F", "Ship:Trade", "A"));
        }

        [Fact]
        public void Post_RefreshPreservesFirstPostedUtc_RegressionGuard()
        {
            var demand = new DemandRegister();
            var posted = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var later = posted.AddHours(3);

            demand.Post(DemandEntry.ForShip("F", "Trade", "A", posted.AddSeconds(30),
                destNodeId: "D1", minCargoLiters: 500, postedUtc: posted));
            // A refresh bumps ExpiresUtc; the age must NOT reset with it, or a demand open for hours
            // is indistinguishable from one posted this second.
            demand.Post(DemandEntry.ForShip("F", "Trade", "A", later.AddSeconds(30),
                destNodeId: "D1", minCargoLiters: 100, postedUtc: later));

            DemandEntry entry;
            Assert.True(demand.TryGet("F", "Ship:Trade", "A", "D1", out entry));
            Assert.Equal(posted, entry.FirstPostedUtc);
            Assert.Equal(later.AddSeconds(30), entry.ExpiresUtc);
            Assert.Equal(500, entry.MinCargoLiters); // existing stronger-cargo preserve still holds
        }

        [Fact]
        public void Post_RefreshAdoptsFirstPostedUtc_WhenTheExistingEntryHasNone()
        {
            var demand = new DemandRegister();
            var expiry = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var posted = expiry.AddMinutes(-5);

            // An "Item:"-shaped poster never sets it; a later Ship post under the same key should not
            // be dragged back to default(DateTime).
            demand.Post(new DemandEntry { FactionTag = "F", Kind = "Ship:Trade", NodeId = "A", ExpiresUtc = expiry });
            demand.Post(DemandEntry.ForShip("F", "Trade", "A", expiry, postedUtc: posted));

            DemandEntry entry;
            Assert.True(demand.TryGet("F", "Ship:Trade", "A", null, out entry));
            Assert.Equal(posted, entry.FirstPostedUtc);
        }

        [Fact]
        public void Prune_ReportsExpiredShipDemandsOnly()
        {
            var demand = new DemandRegister();
            var now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var expired = now.AddSeconds(-1);

            demand.Post(DemandEntry.ForShip("F", "Trade", "A", expired, postedUtc: now.AddHours(-2)));
            demand.Post(DemandEntry.ForShip("F", "Mine", "B", now.AddMinutes(5)));
            demand.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Steel", NodeId = "A", ExpiresUtc = expired });

            var reported = new System.Collections.Generic.List<DemandEntry>();
            demand.Prune(now, reported);

            Assert.Equal(1, demand.Count);
            Assert.Single(reported);
            Assert.Equal("Ship:Trade", reported[0].Kind);
            Assert.Equal(now.AddHours(-2), reported[0].FirstPostedUtc);
        }

        // ---- operational ("Need:") demand ------------------------------------------------------

        private static readonly DateTime Now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void OperationalNeed_SurvivesAnItemDemandForTheSameInventoryKey()
        {
            // The defect this separation exists for. Several posters share one unkeyed
            // "Item:{invKey}" slot and Post is last-write-wins, so an install BOM's real 2828-unit
            // requirement was overwritten by catalog demand's 11000 later in the same cycle. Read
            // back through the shared slot there is no way to tell which number is the real need.
            var demand = new DemandRegister();
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Now.AddMinutes(10), 2828));
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", ExpiresUtc = Now.AddMinutes(10), Quantity = 11000,
            });

            Assert.Equal(2828, demand.OperationalDemandFor("F", "Comp:SteelPlate"));
        }

        [Fact]
        public void OperationalNeed_RefreshReplacesRatherThanAccumulates()
        {
            var demand = new DemandRegister();
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Now.AddMinutes(10), 2828));
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Now.AddMinutes(11), 1200));

            // A shrinking need is a need being met, not a second need beside the first.
            Assert.Equal(1200, demand.OperationalDemandFor("F", "Comp:SteelPlate"));
        }

        [Fact]
        public void OperationalNeed_StopsCountingOnceItExpires()
        {
            var demand = new DemandRegister();
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Now.AddMinutes(10), 2828));
            Assert.Equal(2828, demand.OperationalDemandFor("F", "Comp:SteelPlate"));

            demand.Prune(Now.AddMinutes(11));
            Assert.Equal(0, demand.OperationalDemandFor("F", "Comp:SteelPlate"));
        }

        [Fact]
        public void OperationalNeed_FactionWideAndNodePinnedAreDisjoint()
        {
            var demand = new DemandRegister();
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Now.AddMinutes(10), 2828));
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", "N1", Now.AddMinutes(10), 140));

            // An install BOM is payable from anywhere the faction holds stock; a ship BOM is owed at
            // its shipyard. Neither reads as the other, and N2 owes nothing.
            Assert.Equal(2828, demand.OperationalDemandFor("F", "Comp:SteelPlate"));
            Assert.Equal(140, demand.OperationalDemandAtNode("N1", "Comp:SteelPlate"));
            Assert.Equal(0, demand.OperationalDemandAtNode("N2", "Comp:SteelPlate"));
        }

        [Fact]
        public void OperationalNeed_SumsAcrossNodesForTheFactionWideAggregate()
        {
            var demand = new DemandRegister();
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", null, Now.AddMinutes(10), 100));
            demand.Post(DemandEntry.ForNeed("G", "Comp:SteelPlate", null, Now.AddMinutes(10), 7));

            Assert.Equal(100, demand.OperationalDemandFor("F", "Comp:SteelPlate"));
            Assert.Equal(7, demand.OperationalDemandFor("G", "Comp:SteelPlate"));
        }

        [Fact]
        public void OperationalNeed_IsRemovedByConsumeAll()
        {
            var demand = new DemandRegister();
            demand.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", "N1", Now.AddMinutes(10), 140));

            Assert.Equal(1, demand.ConsumeAll("F", DemandRegister.NeedKind("Comp:SteelPlate"), "N1"));
            Assert.Equal(0, demand.OperationalDemandAtNode("N1", "Comp:SteelPlate"));
        }

        [Fact]
        public void PlainItemDemand_NeverCountsAsAnOperationalNeed()
        {
            // The circularity guard. PostCatalogSurplusForNode sizes its Item: demand as
            // reference x multiple; if that could reach the reference back, ref(n+1) = 2*ref(n) - stock
            // and it doubles every production cycle. Only a "Need:" kind is ever aggregated.
            var demand = new DemandRegister();
            demand.Post(new DemandEntry
            {
                FactionTag = "F", Kind = "Item:Comp:SteelPlate", NodeId = "N1",
                ExpiresUtc = Now.AddMinutes(10), Quantity = 200,
            });

            Assert.Equal(0, demand.OperationalDemandFor("F", "Comp:SteelPlate"));
            Assert.Equal(0, demand.OperationalDemandAtNode("N1", "Comp:SteelPlate"));
        }

    }
    /// <summary>The O(1) "Item:" aggregates the FAC-159 value gate reads. Kept beside the
    /// operational pair they mirror, and separate from DemandRegisterTests so the two scopes cannot
    /// be confused: one counts committed BOM units, this one counts ordinary want rows.</summary>
    public sealed class DemandRegisterItemAggregateTests
    {
        private static readonly DateTime Soon = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        private static DemandEntry Want(string tag, string invKey, string nodeId)
        {
            return new DemandEntry { FactionTag = tag, Kind = "Item:" + invKey, NodeId = nodeId, Quantity = 10, ExpiresUtc = Soon };
        }

        [Fact]
        public void ItemRowsAreCountedAtBothScopes()
        {
            var d = new DemandRegister();
            d.Post(Want("F", "Ore:Iron", "N1"));
            d.Post(Want("F", "Ore:Iron", null));

            Assert.Equal(1, d.ItemDemandRowsAtNode("N1", "Ore:Iron"));
            Assert.Equal(2, d.ItemDemandRowsFor("F", "Ore:Iron"));
            Assert.Equal(0, d.ItemDemandRowsAtNode("N2", "Ore:Iron"));
            Assert.Equal(0, d.ItemDemandRowsFor("F", "Ore:Stone"));
        }

        [Fact]
        public void AQuantityOfZeroStillCounts()
        {
            // Several posters legitimately leave Quantity unspecified. This aggregate answers "does
            // anything want this at all", so a 0-quantity row must not vanish from it -- the
            // operational aggregate beside it sums units and would correctly drop the same row.
            var d = new DemandRegister();
            d.Post(new DemandEntry { FactionTag = "F", Kind = "Item:Ore:Iron", NodeId = "N1", Quantity = 0, ExpiresUtc = Soon });

            Assert.Equal(1, d.ItemDemandRowsAtNode("N1", "Ore:Iron"));
        }

        [Fact]
        public void TheAggregateSurvivesAnIndexRebuild()
        {
            // RebuildIndex clears the maps and repopulates them, and Consume/Prune both trigger it.
            // A missing hook there would zero the aggregate on the first consumed demand and the
            // value gate would then read "nothing wants anything" world-wide. Nearly shipped exactly
            // that.
            var d = new DemandRegister();
            d.Post(Want("F", "Ore:Iron", "N1"));
            d.Post(Want("F", "Ore:Cobalt", "N1"));
            Assert.Equal(1, d.ItemDemandRowsAtNode("N1", "Ore:Iron"));

            d.Consume("F", "Item:Ore:Cobalt", "N1");     // forces RebuildIndex

            Assert.Equal(1, d.ItemDemandRowsAtNode("N1", "Ore:Iron"));
            Assert.Equal(0, d.ItemDemandRowsAtNode("N1", "Ore:Cobalt"));
        }

        [Fact]
        public void ANeedRowIsNotAnItemRow()
        {
            var d = new DemandRegister();
            d.Post(DemandEntry.ForNeed("F", "Comp:SteelPlate", "N1", Soon, 900));

            Assert.Equal(0, d.ItemDemandRowsAtNode("N1", "Comp:SteelPlate"));
            Assert.Equal(900, d.OperationalDemandAtNode("N1", "Comp:SteelPlate"));
        }
    }

    /// <summary>What the register remembers of demands after they leave it: the station window's
    /// demand history, and the node-pinned live rows shown above it.</summary>
    public sealed class DemandRegisterEndedDemandTests
    {

        private static readonly DateTime T0 = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static DemandEntry ItemAt(string node, string key, long qty, DateTime expires,
            DemandClass cls = DemandClass.RecipeInput)
        {
            return new DemandEntry
            {
                FactionTag = "F", Kind = "Item:" + key, NodeId = node, Quantity = qty, ExpiresUtc = expires,
                Class = cls,
            };
        }

        /// <summary>A lapse is recorded with the largest ask of the episode — not the last refresh —
        /// its first-posted time, and the time Prune saw it go.</summary>
        [Fact]
        public void Prune_RecordsALapsedDemand_WithItsPeakAndAge()
        {
            DateTime now = T0;
            var demand = new DemandRegister(() => now);
            demand.Post(ItemAt("N1", "Ingot:Iron", 300, T0.AddMinutes(1)));
            now = T0.AddSeconds(30);
            demand.Post(ItemAt("N1", "Ingot:Iron", 900, T0.AddMinutes(2)));
            now = T0.AddSeconds(60);
            demand.Post(ItemAt("N1", "Ingot:Iron", 100, T0.AddMinutes(3)));

            now = T0.AddMinutes(4);
            demand.Prune(now);

            Assert.Equal(0, demand.Count);
            DemandEpisode e = Assert.Single(demand.SnapshotEndedAtNode("F", "N1", now));
            Assert.Equal("Item:Ingot:Iron", e.Kind);
            Assert.Equal(900, e.PeakQuantity);
            Assert.Equal(T0, e.FirstPostedUtc);
            Assert.Equal(now, e.EndedUtc);
            Assert.False(e.Consumed);
            Assert.Equal(DemandClass.RecipeInput, e.Class);
        }

        [Fact]
        public void ConsumeAll_RecordsAMetShipDemand()
        {
            DateTime now = T0;
            var demand = new DemandRegister(() => now);
            demand.Post(DemandEntry.ForShip("F", "Trade", "N1", T0.AddHours(1), destNodeId: "N9"));
            now = T0.AddMinutes(5);
            Assert.Equal(1, demand.ConsumeAll("F", "Ship:Trade", "N1"));

            DemandEpisode e = Assert.Single(demand.SnapshotEndedAtNode("F", "N1", now));
            Assert.True(e.Consumed);
            Assert.Equal("N9", e.DestNodeId);
            Assert.Equal(now, e.EndedUtc);
        }

        /// <summary>A node's ended list is what the station window shows: that node's Item: and Ship:
        /// rows for the asking faction. A Need: row shadows an Item: row; a faction-wide row is kept,
        /// but in the faction's own list, not under any node.</summary>
        [Fact]
        public void EndedDemands_KeepOnlyNodePinnedItemAndShipRows_ForTheAskingFaction()
        {
            DateTime now = T0;
            var demand = new DemandRegister(() => now);
            demand.Post(ItemAt("N1", "Ore:Iron", 10, T0.AddMinutes(1)));
            demand.Post(ItemAt(null, "Ore:Nickel", 10, T0.AddMinutes(1)));
            demand.Post(DemandEntry.ForNeed("F", "Ore:Iron", "N1", T0.AddMinutes(1), 10));
            demand.Post(new DemandEntry { FactionTag = "G", Kind = "Item:Ore:Iron", NodeId = "N1", Quantity = 5, ExpiresUtc = T0.AddMinutes(1) });

            now = T0.AddMinutes(2);
            demand.Prune(now);

            DemandEpisode e = Assert.Single(demand.SnapshotEndedAtNode("F", "N1", now));
            Assert.Equal("Item:Ore:Iron", e.Kind);
            Assert.Single(demand.SnapshotEndedAtNode("G", "N1", now));
        }

        [Fact]
        public void EndedDemands_AgeOutOfTheWindow_AndComeBackMostRecentFirst()
        {
            DateTime now = T0;
            var demand = new DemandRegister(() => now);
            demand.Post(ItemAt("N1", "Ore:Iron", 10, T0.AddMinutes(1)));
            demand.Post(ItemAt("N1", "Ore:Nickel", 10, T0.AddMinutes(10)));
            now = T0.AddMinutes(2);
            demand.Prune(now);   // iron lapses
            now = T0.AddMinutes(11);
            demand.Prune(now);   // nickel lapses

            var ended = demand.SnapshotEndedAtNode("F", "N1", now);
            Assert.Equal(new[] { "Item:Ore:Nickel", "Item:Ore:Iron" }, ended.Select(x => x.Kind).ToArray());

            // Iron ended at +2 min, so it leaves the window at +32 min; nickel stays until +41.
            now = T0.AddMinutes(2).AddSeconds(DemandRegister.EndedRetentionSeconds + 1);
            demand.Prune(now);
            Assert.Equal("Item:Ore:Nickel", Assert.Single(demand.SnapshotEndedAtNode("F", "N1", now)).Kind);
        }

        [Fact]
        public void EndedDemands_AreCappedPerNode_OldestDropped()
        {
            DateTime now = T0;
            var demand = new DemandRegister(() => now);
            for (int i = 0; i < DemandRegister.MaxEndedPerNode + 5; i++)
            {
                demand.Post(ItemAt("N1", "K" + i, 1, now.AddSeconds(1)));
                now = now.AddSeconds(2);
                demand.Prune(now);
            }

            var ended = demand.SnapshotEndedAtNode("F", "N1", now);
            Assert.Equal(DemandRegister.MaxEndedPerNode, ended.Count);
            Assert.Equal("Item:K" + (DemandRegister.MaxEndedPerNode + 4), ended[0].Kind);
            Assert.Equal("Item:K5", ended[ended.Count - 1].Kind);
        }

        /// <summary>The faction window's history: faction-wide rows and rows at the named nodes, for
        /// this faction only, merged most recently ended first; a Need: row is still left out.</summary>
        [Fact]
        public void SnapshotEndedForFaction_MergesFactionWideAndNodeRows_MostRecentFirst()
        {
            DateTime now = T0;
            var demand = new DemandRegister(() => now);
            demand.Post(ItemAt("N1", "Ore:Iron", 10, T0.AddMinutes(1)));
            demand.Post(ItemAt(null, "Comp:SteelPlate", 10, T0.AddMinutes(3)));
            demand.Post(ItemAt("N2", "Ore:Nickel", 10, T0.AddMinutes(5)));
            demand.Post(ItemAt("N3", "Ore:Cobalt", 10, T0.AddMinutes(1)));
            demand.Post(DemandEntry.ForNeed("F", "Ore:Iron", null, T0.AddMinutes(1), 10));
            demand.Post(new DemandEntry { FactionTag = "G", Kind = "Item:Ore:Iron", Quantity = 5, ExpiresUtc = T0.AddMinutes(1) });
            demand.Post(new DemandEntry { FactionTag = "G", Kind = "Item:Ore:Iron", NodeId = "N1", Quantity = 5, ExpiresUtc = T0.AddMinutes(1) });

            now = T0.AddMinutes(2); demand.Prune(now);   // N1 iron, N3 cobalt, G's two
            now = T0.AddMinutes(4); demand.Prune(now);   // steel plate, faction-wide
            now = T0.AddMinutes(6); demand.Prune(now);   // N2 nickel

            var ended = demand.SnapshotEndedForFaction("F", new[] { "N1", "N2" }, now);
            Assert.Equal(new[] { "Item:Ore:Nickel", "Item:Comp:SteelPlate", "Item:Ore:Iron" },
                ended.Select(x => x.Kind).ToArray());
            Assert.Null(ended[1].NodeId);
            Assert.Equal("N1", ended[2].NodeId);

            DemandEpisode g = Assert.Single(demand.SnapshotEndedForFaction("G", null, now));
            Assert.Null(g.NodeId);
        }

        [Fact]
        public void FactionWideEndedDemands_AgeOut_AndAreCappedPerFaction()
        {
            DateTime now = T0;
            var demand = new DemandRegister(() => now);
            for (int i = 0; i < DemandRegister.MaxEndedFactionWidePerFaction + 3; i++)
            {
                demand.Post(ItemAt(null, "K" + i, 1, now.AddSeconds(1)));
                now = now.AddSeconds(2);
                demand.Prune(now);
            }
            demand.Post(new DemandEntry { FactionTag = "G", Kind = "Item:K", Quantity = 1, ExpiresUtc = now.AddSeconds(1) });
            now = now.AddSeconds(2);
            demand.Prune(now);

            var ended = demand.SnapshotEndedForFaction("F", null, now);
            Assert.Equal(DemandRegister.MaxEndedFactionWidePerFaction, ended.Count);
            Assert.Equal("Item:K" + (DemandRegister.MaxEndedFactionWidePerFaction + 2), ended[0].Kind);
            Assert.Equal("Item:K3", ended[ended.Count - 1].Kind);
            Assert.Single(demand.SnapshotEndedForFaction("G", null, now));

            now = now.AddSeconds(DemandRegister.EndedRetentionSeconds + 1);
            demand.Prune(now);
            Assert.Empty(demand.SnapshotEndedForFaction("F", null, now));
            Assert.Empty(demand.SnapshotEndedForFaction("G", null, now));
        }

        [Fact]
        public void SnapshotFactionWindowDemands_IsEveryItemAndShipRowTheFactionHas()
        {
            var demand = new DemandRegister(() => T0);
            demand.Post(ItemAt("N1", "Ore:Iron", 10, T0.AddMinutes(1)));
            demand.Post(ItemAt(null, "Comp:SteelPlate", 10, T0.AddMinutes(1)));
            demand.Post(DemandEntry.ForShip("F", "Haul", null, T0.AddMinutes(1)));
            demand.Post(DemandEntry.ForNeed("F", "Ore:Iron", "N1", T0.AddMinutes(1), 10));
            demand.Post(new DemandEntry { FactionTag = "F", Kind = "Position:Haul", NodeId = "N1", ExpiresUtc = T0.AddMinutes(1) });
            demand.Post(new DemandEntry { FactionTag = "G", Kind = "Item:Ore:Iron", NodeId = "N1", Quantity = 5, ExpiresUtc = T0.AddMinutes(1) });

            var rows = demand.SnapshotFactionWindowDemands("F");
            Assert.Equal(new[] { "Item:Ore:Iron", "Item:Comp:SteelPlate", "Ship:Haul" }, rows.Select(r => r.Kind).ToArray());
            Assert.Single(demand.SnapshotFactionWindowDemands("G"));
        }

        [Fact]
        public void SnapshotNodeDemands_IsTheFactionsItemAndShipRowsPinnedToTheNode()
        {
            var demand = new DemandRegister(() => T0);
            demand.Post(ItemAt("N1", "Ore:Iron", 10, T0.AddMinutes(1)));
            demand.Post(DemandEntry.ForShip("F", "Haul", "N1", T0.AddMinutes(1)));
            demand.Post(DemandEntry.ForNeed("F", "Ore:Iron", "N1", T0.AddMinutes(1), 10));
            demand.Post(ItemAt("N2", "Ore:Iron", 10, T0.AddMinutes(1)));
            demand.Post(ItemAt(null, "Ore:Iron", 10, T0.AddMinutes(1)));
            demand.Post(new DemandEntry { FactionTag = "F", Kind = "Position:Haul", NodeId = "N1", ExpiresUtc = T0.AddMinutes(1) });

            var rows = demand.SnapshotNodeDemands("F", "N1");
            Assert.Equal(new[] { "Item:Ore:Iron", "Ship:Haul" }, rows.Select(r => r.Kind).ToArray());
            Assert.Empty(demand.SnapshotNodeDemands("G", "N1"));
        }
    }
}
