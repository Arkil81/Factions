using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>What one unit of an item feeds, in SE's <c>Food</c> stat units: the stat's
    /// <c>Value × Time</c> on the item's <c>ConsumableItemDefinition</c>, so 1.00 is one
    /// <c>MealPack_SteakDinner</c> and a raw fruit is 0.06 (docs/food-and-population.md, "Food is
    /// measured in nutrition"). The host reads SE's definitions; Core never names an item.</summary>
    public interface INutritionCatalogue
    {
        /// <summary>Nutrition per unit of <paramref name="invKey"/>; 0 for anything not eaten.</summary>
        double NutritionPerUnit(string invKey);
    }

    /// <summary>What one upkeep pass did at one node, for the log line and the tests.</summary>
    public struct WorkerUpkeepResult
    {
        /// <summary>Hours charged. Zero when the pass only stamped the clock.</summary>
        public double Hours;
        public double NutritionOwed;
        public double NutritionEaten;
        public double OxygenOwed;
        public long OxygenDrawn;
        /// <summary>The share of the workers the node fed and gave air to, 0..1.</summary>
        public double FedShare;
        /// <summary>Workers who left this pass: over the beds, or unfed.</summary>
        public double Left;
        /// <summary>Units eaten per inventory key; null when nothing was eaten.</summary>
        public Dictionary<string, long> Eaten;
    }

    /// <summary>
    /// Workers eat food and breathe oxygen out of their node's stock every pass, and the share the
    /// node cannot keep leaves (docs/food-and-population.md, "Workers"; FAC-493).
    ///
    /// <para><b>Food is eaten by nutrition, lowest first.</b> Raw crops go before cooked meals, so the
    /// meals — which carry two to three times the nutrition in a fraction of the volume — stay
    /// available to haul. Grain and algae are not consumables in SE and feed nobody until a recipe
    /// turns them into a meal.</para>
    ///
    /// <para><b>Only the unfed leave, and gradually.</b> The fed share is the smaller of the food
    /// covered and the air covered; of the rest, <see cref="FactionTuning.WorkerUnfedLeavePerHour"/>
    /// leaves per hour. Below one person with any shortage, the node empties — decay goes to zero
    /// (operator, 2026-10-15).</para>
    ///
    /// <para><b>A node in breathable air draws no oxygen.</b> SE lets a character with its helmet off
    /// breathe wherever the planet's oxygen there is above <see cref="AmbientOxygenToBreathe"/>, so
    /// workers on a Tellus surface live on the air and only a vacuum or a thin-air node bills its
    /// stock (<see cref="BreathesAmbient"/>).</para>
    ///
    /// <para>A pass charges at most <see cref="MaxHoursPerPass"/>, so a server that was down does
    /// not bill its downtime to the first pass after the restart.</para>
    /// </summary>
    public static class WorkerUpkeep
    {
        public const double MaxHoursPerPass = 1.0;

        /// <summary>The ambient oxygen level above which a character breathes without a helmet: SE's
        /// <c>MyCharacterDefinition.PressureLevelForLowDamage</c> default, which vanilla
        /// <c>Characters.sbc</c> does not override.</summary>
        public const double AmbientOxygenToBreathe = 0.5;

        /// <summary>The oxygen key in node stock.</summary>
        public static readonly string OxygenKey = InventoryKeyMapper.GasPrefix + "Oxygen";

        /// <summary>Workers this node can house: its Habitat level times the beds a level installs.</summary>
        public static int Capacity(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null || n.Capabilities == null) return 0;
            int level;
            n.Capabilities.TryGetValue(NodeCapabilityKind.Habitat, out level);
            return level <= 0 ? 0 : level * Math.Max(0, t.HabitatBedsPerLevel);
        }

        /// <summary>The whole people living at this node — what a player is shown and what a bed
        /// count compares against.</summary>
        public static int Headcount(SpatialNode n)
        {
            if (n == null || n.Workers <= 0) return 0;
            return (int)Math.Floor(n.Workers + 1e-9);
        }

        /// <summary>Charges every node holding workers. Call from one place on one thread; it writes
        /// the nodes directly.</summary>
        public static void RunAll(IEnumerable<SpatialNode> nodes, FactionTuning t, INutritionCatalogue food,
            IPlanetCatalogue planets, DateTime now, Action<SpatialNode, WorkerUpkeepResult> report)
        {
            if (nodes == null) return;
            foreach (SpatialNode n in nodes)
            {
                if (n == null || (n.Workers <= 0 && n.WorkersAtUtc == default(DateTime))) continue;
                WorkerUpkeepResult r = Run(n, t, food, planets, now);
                if (report != null && (r.Hours > 0 || r.Left > 0)) report(n, r);
            }
        }

        /// <summary>One node's pass: workers over the beds leave at once, then the rest eat and
        /// breathe for the hours since the last pass and the unfed share leaves.</summary>
        public static WorkerUpkeepResult Run(SpatialNode n, FactionTuning t, INutritionCatalogue food,
            IPlanetCatalogue planets, DateTime now)
        {
            var r = new WorkerUpkeepResult { FedShare = 1.0 };
            if (n == null || t == null) return r;

            if (n.Workers <= 0)
            {
                n.Workers = 0;
                n.WorkersAtUtc = default(DateTime);
                n.WorkerFoodBank = 0;
                return r;
            }

            int cap = Capacity(n, t);
            if (n.Workers > cap)
            {
                r.Left += n.Workers - cap;
                n.Workers = cap;
                if (n.Workers <= 0)
                {
                    n.WorkersAtUtc = default(DateTime);
                    n.WorkerFoodBank = 0;
                    return r;
                }
            }

            if (n.WorkersAtUtc == default(DateTime) || now <= n.WorkersAtUtc)
            {
                if (n.WorkersAtUtc == default(DateTime)) n.WorkersAtUtc = now;
                return r;
            }

            double hours = Math.Min((now - n.WorkersAtUtc).TotalHours, MaxHoursPerPass);
            n.WorkersAtUtc = now;
            r.Hours = hours;

            double foodShare = Eat(n, t, food, hours, ref r);
            double airShare = BreathesAmbient(n, planets) ? 1.0 : Breathe(n, t, hours, ref r);
            r.FedShare = Math.Min(foodShare, airShare);

            if (r.FedShare < 1.0)
            {
                double unfed = n.Workers * (1.0 - r.FedShare);
                double leave = unfed * Math.Min(1.0, Math.Max(0.0, t.WorkerUnfedLeavePerHour) * hours);
                n.Workers -= leave;
                r.Left += leave;
                if (n.Workers < 1.0)
                {
                    r.Left += n.Workers;
                    n.Workers = 0;
                    n.WorkersAtUtc = default(DateTime);
                    n.WorkerFoodBank = 0;
                }
            }
            return r;
        }

        private static double Eat(SpatialNode n, FactionTuning t, INutritionCatalogue food, double hours,
            ref WorkerUpkeepResult r)
        {
            double owed = n.Workers * Math.Max(0.0, t.WorkerNutritionPerHour) * hours;
            r.NutritionOwed = owed;
            if (owed <= 0) return 1.0;

            double fromBank = Math.Min(n.WorkerFoodBank, owed);
            n.WorkerFoodBank -= fromBank;
            double remaining = owed - fromBank;
            r.NutritionEaten = fromBank;

            if (remaining > 0 && food != null)
            {
                foreach (KeyValuePair<string, double> edible in EdibleStock(n, food))
                {
                    if (remaining <= 0) break;
                    long have;
                    n.Inventory.TryGetValue(edible.Key, out have);
                    if (have <= 0) continue;
                    long units = Math.Min(have, (long)Math.Ceiling(remaining / edible.Value - 1e-9));
                    if (units <= 0) continue;
                    n.Inventory[edible.Key] = have - units;
                    double fed = units * edible.Value;
                    r.NutritionEaten += fed;
                    remaining -= fed;
                    if (r.Eaten == null) r.Eaten = new Dictionary<string, long>(StringComparer.Ordinal);
                    r.Eaten[edible.Key] = units;
                }
            }

            if (remaining < 0)
            {
                // The last unit overshot what was owed; what is left of it feeds the next pass.
                n.WorkerFoodBank += -remaining;
                r.NutritionEaten += remaining;
                remaining = 0;
            }
            return Math.Max(0.0, Math.Min(1.0, (owed - remaining) / owed));
        }

        private static double Breathe(SpatialNode n, FactionTuning t, double hours, ref WorkerUpkeepResult r)
        {
            double owed = n.Workers * Math.Max(0.0, t.WorkerOxygenPerHour) * hours;
            r.OxygenOwed = owed;
            if (owed <= 0) return 1.0;
            long have;
            n.Inventory.TryGetValue(OxygenKey, out have);
            long drawn = Math.Min(Math.Max(0L, have), (long)Math.Ceiling(owed - 1e-9));
            if (drawn > 0) n.Inventory[OxygenKey] = have - drawn;
            r.OxygenDrawn = drawn;
            return Math.Min(1.0, drawn / owed);
        }

        /// <summary>The node's edible keys with their nutrition, lowest nutrition first and ordinal
        /// within a tie, so the order is the same on every host.</summary>
        internal static List<KeyValuePair<string, double>> EdibleStock(SpatialNode n, INutritionCatalogue food)
        {
            var list = new List<KeyValuePair<string, double>>();
            foreach (KeyValuePair<string, long> kv in n.Inventory)
            {
                if (kv.Value <= 0) continue;
                double per = food.NutritionPerUnit(kv.Key);
                if (per > 0) list.Add(new KeyValuePair<string, double>(kv.Key, per));
            }
            list.Sort((a, b) =>
            {
                int c = a.Value.CompareTo(b.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            return list;
        }

        /// <summary>Whether the air at this node keeps its workers without bottled oxygen: it sits
        /// inside a breathable atmosphere where the planet's oxygen is above
        /// <see cref="AmbientOxygenToBreathe"/>. SE's level is the air density there times the body's
        /// <see cref="PlanetRecord.BreathableOxygen"/> (<c>MyPlanet.GetOxygenForPosition</c>, weather
        /// left out).</summary>
        public static bool BreathesAmbient(SpatialNode n, IPlanetCatalogue planets)
        {
            if (n == null || planets == null) return false;
            PlanetRecord body = ThrustPhysics.AtmosphereAt(planets, n.Position);
            if (body == null || body.BreathableOxygen <= 0) return false;
            double air = ThrustPhysics.AirDensity(body, n.Position.DistanceTo(body.Center));
            return air * body.BreathableOxygen > AmbientOxygenToBreathe;
        }

        /// <summary>Hours the node's current food keeps its workers;
        /// <see cref="double.PositiveInfinity"/> with nobody to feed or no food owed.</summary>
        public static double FoodHours(SpatialNode n, FactionTuning t, INutritionCatalogue food)
        {
            if (n == null || t == null || n.Workers <= 0 || t.WorkerNutritionPerHour <= 0)
                return double.PositiveInfinity;
            double nutrition = n.WorkerFoodBank;
            if (food != null)
                foreach (KeyValuePair<string, long> kv in n.Inventory)
                    if (kv.Value > 0) nutrition += kv.Value * food.NutritionPerUnit(kv.Key);
            return nutrition / (n.Workers * t.WorkerNutritionPerHour);
        }

        /// <summary>Hours the node's oxygen stock keeps its workers;
        /// <see cref="double.PositiveInfinity"/> with nobody to feed, no oxygen owed, or breathable
        /// air (<see cref="BreathesAmbient"/>).</summary>
        public static double AirHours(SpatialNode n, FactionTuning t, IPlanetCatalogue planets)
        {
            if (n == null || t == null || n.Workers <= 0 || t.WorkerOxygenPerHour <= 0
                || BreathesAmbient(n, planets))
                return double.PositiveInfinity;
            long oxygen;
            n.Inventory.TryGetValue(OxygenKey, out oxygen);
            return Math.Max(0L, oxygen) / (n.Workers * t.WorkerOxygenPerHour);
        }

        /// <summary>Hours the node's current food and oxygen keep its workers, the smaller of the
        /// two; <see cref="double.PositiveInfinity"/> with nobody to feed.</summary>
        public static double HoursSupplied(SpatialNode n, FactionTuning t, INutritionCatalogue food,
            IPlanetCatalogue planets)
        {
            return Math.Min(FoodHours(n, t, food), AirHours(n, t, planets));
        }
    }

    /// <summary>
    /// A Habitat asks for its workers' food and keeps it from haulers
    /// (docs/food-and-population.md, "Workers"). Before this the node's crops were ordinary stock: a
    /// FoodProcessor's input demand elsewhere hauled a Farm's harvest out from under the people it
    /// was feeding, and they left with the food a few metres away.
    ///
    /// <para><b>The target is nutrition</b>, not an item: <see cref="FactionTuning.WorkerFoodReserveHours"/>
    /// of what the node's workers eat. Whatever edible stock the node holds counts toward it, whatever
    /// the item.</para>
    ///
    /// <para><b>The keep follows the eating order</b> (<see cref="KeepUnits"/>): the target is allotted
    /// over the node's edible stock lowest nutrition first, as <see cref="WorkerUpkeep"/> eats it, so
    /// the units kept are the ones the next hours will eat and anything past the target is free to
    /// move.</para>
    ///
    /// <para><b>The demand names one item</b> (<see cref="PostForNode"/>): the most nutritious one the
    /// faction holds at another node, so a hauler can bring it now; with none held, the most
    /// nutritious one its installed capabilities produce, so the demand admits the recipe. It posts
    /// the fuel reserve's shape — a <c>Need:</c> row for the units missing and a gross <c>Item:</c>
    /// row beside it — at <see cref="DemandClass.Upkeep"/>.</para>
    /// </summary>
    public static class WorkerFoodDemand
    {
        /// <summary>The nutrition this node asks to hold: its workers' eating over
        /// <see cref="FactionTuning.WorkerFoodReserveHours"/>. Zero with nobody living there.</summary>
        public static double TargetNutrition(SpatialNode n, FactionTuning t)
        {
            if (n == null || t == null || n.Workers <= 0) return 0;
            return n.Workers * Math.Max(0.0, t.WorkerNutritionPerHour) * Math.Max(0.0, t.WorkerFoodReserveHours);
        }

        /// <summary>Units of <paramref name="invKey"/> the node keeps for its workers. Cheap for the
        /// common case — a node with nobody living there, or a key nobody eats, returns before
        /// reading the stock — because the surplus rule asks it for every (node, item) pair.</summary>
        public static long KeepUnits(SpatialNode n, string invKey, FactionTuning t, INutritionCatalogue food)
        {
            if (n == null || food == null || n.Workers <= 0 || string.IsNullOrEmpty(invKey)) return 0;
            if (food.NutritionPerUnit(invKey) <= 0) return 0;
            double remaining = TargetNutrition(n, t) - n.WorkerFoodBank;
            if (remaining <= 0) return 0;
            foreach (KeyValuePair<string, double> edible in WorkerUpkeep.EdibleStock(n, food))
            {
                long have;
                n.Inventory.TryGetValue(edible.Key, out have);
                long units = Math.Min(have, (long)Math.Ceiling(remaining / edible.Value - 1e-9));
                if (string.Equals(edible.Key, invKey, StringComparison.Ordinal)) return Math.Max(0L, units);
                remaining -= units * edible.Value;
                if (remaining <= 0) return 0;
            }
            return 0;
        }

        /// <summary>Posts one Habitat node's food rows and returns the item it asked for, or null when
        /// it holds its target or has nothing to name. <paramref name="producible"/> is every item the
        /// faction's installed capabilities output; <paramref name="isNew"/> is true when the
        /// <c>Item:</c> row did not stand before.</summary>
        public static string PostForNode(string factionTag, SpatialNode n, IProductionExecutionContext ctx,
            FactionTuning t, INutritionCatalogue food, IEnumerable<string> producible, DateTime expiresUtc,
            out long units, out double haveNutrition, out double targetNutrition, out bool isNew)
        {
            units = 0;
            haveNutrition = 0;
            targetNutrition = 0;
            isNew = false;
            if (n == null || ctx == null || food == null || n.Workers <= 0) return null;

            targetNutrition = TargetNutrition(n, t);
            haveNutrition = n.WorkerFoodBank;
            foreach (KeyValuePair<string, long> kv in n.Inventory)
            {
                double per = food.NutritionPerUnit(kv.Key);
                if (per <= 0) continue;
                long have = ctx.GetNodeInventory(n.Id, kv.Key);
                if (have > 0) haveNutrition += have * per;
            }
            double shortfall = targetNutrition - haveNutrition;
            if (shortfall <= 0) return null;

            string key = BestHeldElsewhere(factionTag, n, ctx, food) ?? BestOf(producible, food);
            if (key == null) return null;
            double perUnit = food.NutritionPerUnit(key);
            units = (long)Math.Ceiling(shortfall / perUnit - 1e-9);
            if (units <= 0) return null;

            // The Need: row carries what is missing, the Item: row the gross want: MatchVectors nets
            // the destination's stock itself.
            ctx.PostDemand(DemandEntry.ForNeed(factionTag, key, n.Id, expiresUtc, units, DemandClass.Upkeep));
            isNew = ctx.PostDemand(new DemandEntry
            {
                FactionTag = factionTag,
                Kind       = DemandRegister.ItemKind(key),
                NodeId     = n.Id,
                ExpiresUtc = expiresUtc,
                Quantity   = ctx.GetNodeInventory(n.Id, key) + units,
                Class      = DemandClass.Upkeep,
            });
            return key;
        }

        /// <summary>The most nutritious item this faction holds at any of its other nodes; ordinal
        /// within a tie, so every host names the same one.</summary>
        private static string BestHeldElsewhere(string factionTag, SpatialNode self, IProductionExecutionContext ctx,
            INutritionCatalogue food)
        {
            string best = null;
            double bestPer = 0;
            foreach (SpatialNode other in ctx.OwnedNodes(factionTag))
            {
                if (other == null || other.Id == self.Id) continue;
                foreach (KeyValuePair<string, long> kv in other.Inventory)
                {
                    double per = food.NutritionPerUnit(kv.Key);
                    if (per <= 0 || ctx.GetNodeInventory(other.Id, kv.Key) <= 0) continue;
                    if (Better(kv.Key, per, best, bestPer)) { best = kv.Key; bestPer = per; }
                }
            }
            return best;
        }

        private static string BestOf(IEnumerable<string> keys, INutritionCatalogue food)
        {
            if (keys == null) return null;
            string best = null;
            double bestPer = 0;
            foreach (string key in keys)
            {
                double per = food.NutritionPerUnit(key);
                if (per > 0 && Better(key, per, best, bestPer)) { best = key; bestPer = per; }
            }
            return best;
        }

        private static bool Better(string key, double per, string best, double bestPer)
        {
            if (best == null || per > bestPer) return true;
            return per == bestPer && string.CompareOrdinal(key, best) < 0;
        }
    }

    /// <summary>Why a recruitment was refused, in the order it is checked.</summary>
    public enum WorkerRecruitResult
    {
        Ok = 0,
        NodeNotFound,
        NotOwned,
        NoHabitat,
        NoCivilianPool,
        NoRoom,
        InsufficientTreasury,
    }

    /// <summary>
    /// The one rule for buying workers out of a civilian pool, shared by the station window's button
    /// and the reply it gets, so the two can never quote a different price or refuse for different
    /// reasons. FAC-493.
    ///
    /// <para>A recruit joins the node's workers at once: the pool is local, so there is nothing to
    /// fly. A request for more than the beds left, or than the treasury buys, is <b>trimmed</b>, not
    /// refused, as long as one worker fits — the player asked for workers, and the reply says how
    /// many they got.</para>
    /// </summary>
    public static class WorkerRecruitValidator
    {
        public static WorkerRecruitResult Validate(IWorldView world, FactionTuning t, string factionTag,
            string nodeId, int requested, out int granted, out long cost, out long have)
        {
            granted = 0;
            cost = 0;
            have = 0;
            SpatialNode n;
            if (world == null || world.Graph == null || string.IsNullOrEmpty(nodeId)
                || !world.Graph.Nodes.TryGetValue(nodeId, out n))
                return WorkerRecruitResult.NodeNotFound;
            if (!string.Equals(n.OwnerFactionTag, factionTag, StringComparison.OrdinalIgnoreCase))
                return WorkerRecruitResult.NotOwned;
            int cap = WorkerUpkeep.Capacity(n, t);
            if (cap <= 0) return WorkerRecruitResult.NoHabitat;
            if (!HasCivilianPool(n, world.Planets)) return WorkerRecruitResult.NoCivilianPool;

            int room = cap - WorkerUpkeep.Headcount(n);
            if (room <= 0) return WorkerRecruitResult.NoRoom;

            long price = Math.Max(0L, t.WorkerRecruitPrice);
            have = world.GetTreasury(factionTag);
            int want = Math.Min(Math.Max(1, requested), room);
            int affordable = price > 0 ? (int)Math.Min(want, Math.Max(0L, have) / price) : want;
            if (affordable <= 0)
            {
                cost = price;
                return WorkerRecruitResult.InsufficientTreasury;
            }
            granted = affordable;
            cost = price * granted;
            return WorkerRecruitResult.Ok;
        }

        /// <summary>Whether the body this node is on has a civilian pool to recruit from.</summary>
        public static bool HasCivilianPool(SpatialNode n, IPlanetCatalogue planets)
        {
            if (n == null || n.PlanetId == null || planets == null || planets.Planets == null) return false;
            foreach (PlanetRecord p in planets.Planets)
                if (p != null && p.Id == n.PlanetId) return p.Recruits;
            return false;
        }

        /// <summary>Adds recruits to a node: they start their clock now, so they owe nothing for
        /// time before they arrived.</summary>
        public static void Admit(SpatialNode n, int count, DateTime now)
        {
            if (n == null || count <= 0) return;
            if (n.Workers <= 0) n.WorkersAtUtc = now;
            n.Workers += count;
        }
    }
}
