using Factions.Core;

namespace Factions
{
    /// <summary>Window aggregate of completed Core production ticks.</summary>
    internal sealed class ProductionTelemetryWindow
    {
        public int Calls;
        public long Factions;
        public long OwnedNodes;
        public long CapabilityBanks;
        public long ElapsedClamps;
        public long SlotsVisited;
        public long RecipesExamined;
        public long RecipesSkippedNoDemand;
        public long BlockedRecipes;
        public long BlockedInputPosts;
        public long DemandPosts;
        public long DemandRefreshes;
        public long CatalogOffersScanned;
        public long InvestmentPasses;
        public long RefinementPasses;
        public long Restarts;
        public long PreparationTicks;
        public long CapabilityBankTicks;
        public long BlockedDemandTicks;
        public long InvestmentRefinementTicks;
        public long CatalogTicks;
        public long MaxCycleAgeTicks;

        public void Record(ProductionTickTelemetry value)
        {
            Calls++;
            Factions += value.Factions;
            OwnedNodes += value.OwnedNodes;
            CapabilityBanks += value.CapabilityBanks;
            ElapsedClamps += value.ElapsedClamps;
            SlotsVisited += value.SlotsVisited;
            RecipesExamined += value.RecipesExamined;
            RecipesSkippedNoDemand += value.RecipesSkippedNoDemand;
            BlockedRecipes += value.BlockedRecipes;
            BlockedInputPosts += value.BlockedInputPosts;
            DemandPosts += value.DemandPosts;
            DemandRefreshes += value.DemandRefreshes;
            CatalogOffersScanned += value.CatalogOffersScanned;
            InvestmentPasses += value.InvestmentPasses;
            RefinementPasses += value.RefinementPasses;
            Restarts += value.Restarts;
            PreparationTicks += value.PreparationTicks;
            CapabilityBankTicks += value.CapabilityBankTicks;
            BlockedDemandTicks += value.BlockedDemandTicks;
            InvestmentRefinementTicks += value.InvestmentRefinementTicks;
            CatalogTicks += value.CatalogTicks;
            if (value.MaxCycleAgeTicks > MaxCycleAgeTicks) MaxCycleAgeTicks = value.MaxCycleAgeTicks;
        }

        public void Reset()
        {
            Calls = 0;
            Factions = OwnedNodes = CapabilityBanks = SlotsVisited = RecipesExamined = ElapsedClamps = 0;
            RecipesSkippedNoDemand = 0;
            BlockedRecipes = BlockedInputPosts = DemandPosts = DemandRefreshes = 0;
            CatalogOffersScanned = InvestmentPasses = RefinementPasses = Restarts = 0;
            PreparationTicks = CapabilityBankTicks = BlockedDemandTicks = 0;
            InvestmentRefinementTicks = CatalogTicks = MaxCycleAgeTicks = 0;
        }
    }
}
