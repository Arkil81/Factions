using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.Entities;
using VRage.Game;

namespace Factions
{
    /// <summary>What a grid is restored against, placed into its frame: the reference blocks, each
    /// one's own object builder (what a placement clones), and the transform from the reference's
    /// coordinates to the grid's. <see cref="DesignGrids"/> is every grid of the design — the
    /// reference is grid <see cref="RootGrid"/> of it — so the subgrids joined to it by rotors,
    /// hinges and pistons are restored with it.</summary>
    internal sealed class RestorationTarget
    {
        /// <summary><c>prefab:&lt;id&gt;</c>, <c>design:&lt;entity id&gt;</c> or <c>matched:&lt;id&gt;</c>.</summary>
        public string Source;
        /// <summary>registered, spawn, fleet, store, explicit or matched.</summary>
        public string Via;
        /// <summary>The owner's own registered snapshot rather than a prefab: its block settings
        /// (programs, share mode) are the owner's and are kept.</summary>
        public bool IsDesign;
        public List<RestorationBlock> Reference;
        public List<MyObjectBuilder_CubeBlock> Sources;
        public GridTransform Transform;
        /// <summary>Reference blocks of a definition this world lacks, left out.</summary>
        public int Unknown;
        /// <summary>The pattern match's score, 0..1; negative when a mark named the design.</summary>
        public double Score = -1;
        public MyObjectBuilder_CubeGrid[] DesignGrids;
        public int RootGrid;

        private RestorationDesign _design;

        /// <summary>The design's grids and joints, read once.</summary>
        public RestorationDesign Design
        {
            get
            {
                if (_design != null) return _design;
                if (DesignGrids != null && RootGrid >= 0 && RootGrid < DesignGrids.Length)
                {
                    _design = RestorationGrids.Design(DesignGrids);
                }
                else
                {
                    _design = new RestorationDesign();
                    _design.Assembly.Grids.Add(Reference ?? new List<RestorationBlock>());
                    _design.Sources.Add(Sources ?? new List<MyObjectBuilder_CubeBlock>());
                    RootGrid = 0;
                }
                return _design;
            }
        }
    }

    internal sealed partial class RestorationReferences
    {
        /// <summary>The target of a grid its marks name (explicit prefab, registered design, spawn,
        /// store sale), best-fitting reference grid first. <paramref name="unmarked"/> when nothing
        /// names one — pattern matching (<see cref="StartRestoreMatchFor"/>) is then the way.</summary>
        internal bool TryRestorationTarget(MyCubeGrid grid, string explicitPrefab, List<RestorationBlock> current,
                                           out RestorationTarget target, out bool unmarked, out string error)
        {
            target = null;
            RestorationReference reference;
            if (!TryResolveRestorationReference(grid, explicitPrefab, out reference, out unmarked, out error)) return false;
            RestorationFitResult best = BestReferenceGrid(reference, current);
            if (best == null)
            {
                error = "the reference " + reference.Source + " holds no blocks this world knows.";
                return false;
            }
            target = new RestorationTarget
            {
                Source = reference.Source,
                Via = reference.Via,
                IsDesign = reference.Via == "registered",
                Reference = best.Reference,
                Sources = best.Sources,
                Transform = best.Fit.Transform,
                Unknown = best.Unknown,
                DesignGrids = reference.Grids,
                RootGrid = best.GridIndex,
            };
            return true;
        }
    }
}
