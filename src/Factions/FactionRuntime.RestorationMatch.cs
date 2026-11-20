using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Factions.Core;
using Sandbox.Game.Entities;
using VRage.Game;

namespace Factions
{
    /// <summary>
    /// Restoration's last reference source: a grid nothing marks is pattern matched
    /// (<see cref="RestorationMatch"/>) against every SE prefab grid, the player captures and the
    /// registered designs. On request only, one grid at a time, and never inside one frame:
    /// <list type="number">
    ///   <item>the prefab index (<see cref="RestorationCatalogueIndex"/>) is built on the first
    ///   request, a slice of prefabs a frame;</item>
    ///   <item>candidates are selected, and the few selected have their block lists read, on the
    ///   game thread under the same slice;</item>
    ///   <item>the scoring — the expensive part — runs on a worker thread over those lists, and
    ///   touches no SE object;</item>
    ///   <item>the result is logged and written to the bridge outbox back on the game thread.</item>
    /// </list>
    /// </summary>
    public sealed partial class FactionRuntime
    {
        /// <summary>Game-thread time a pending match may spend per frame.</summary>
        private const double RestoreMatchSliceMs = 2.0;

        private RestorationCatalogueIndex _restoreIndex;
        private RestoreMatchJob _restoreMatch;

        private sealed class RestoreMatchJob
        {
            public long GridId;
            public string GridName;
            public List<RestorationBlock> Current;
            /// <summary>Captures and registered designs, read when the match started.</summary>
            public List<RestorationCatalogueEntry> Extra;
            public long StartTimestamp;
            public int Frames;
            public int CatalogueSize;
            public RestorationCandidateSelection Selection;
            public readonly List<RestorationMatchCandidate> Candidates = new List<RestorationMatchCandidate>();
            public readonly Dictionary<RestorationCatalogueEntry, int> Unknown = new Dictionary<RestorationCatalogueEntry, int>();
            public Task<RestorationMatchResult> Search;
            /// <summary>Who asked, when it was not the bench: handed the matched target, or null and
            /// the reason, instead of the outbox.</summary>
            public Action<RestorationTarget, string> Done;
        }

        private string StartRestoreMatch(MyCubeGrid grid)
        {
            if (_restoreMatch != null)
                return "restore-plan: grid " + grid.EntityId + ": a match for grid " + _restoreMatch.GridId
                     + " is still running; ask again when it reports.";
            BeginRestoreMatch(grid, null);
            return "restore-plan: grid " + grid.EntityId + " '" + grid.DisplayName + "': nothing marks its design; matching "
                 + _restoreMatch.Current.Count + " blocks against the prefab catalogue"
                 + (_restoreIndex.Ready ? "" : " (indexed first, over the next frames)")
                 + ". The plan follows in the bridge outbox; RESTORE_MATCH in the Torch log.";
        }

        /// <summary>Starts matching <paramref name="grid"/> for a caller other than the bench:
        /// <paramref name="done"/> receives the matched target, or null and why, on the game thread
        /// a few frames later. False (with the reason) when another match is running.</summary>
        internal bool StartRestoreMatchFor(MyCubeGrid grid, Action<RestorationTarget, string> done, out string refusal)
        {
            refusal = null;
            if (_restoreMatch != null)
            {
                refusal = "the server is matching another ship's design; ask again in a moment.";
                return false;
            }
            BeginRestoreMatch(grid, done);
            return true;
        }

        private void BeginRestoreMatch(MyCubeGrid grid, Action<RestorationTarget, string> done)
        {
            if (_restoreIndex == null) _restoreIndex = new RestorationCatalogueIndex();

            var job = new RestoreMatchJob
            {
                GridId = grid.EntityId,
                GridName = grid.DisplayName,
                Current = RestorationGrids.FromGrid(grid),
                Extra = new List<RestorationCatalogueEntry>(),
                StartTimestamp = Stopwatch.GetTimestamp(),
                Done = done,
            };
            if (_customPrefabStore != null)
            {
                foreach (var capture in _customPrefabStore.AllEntries)
                {
                    if (capture.Grids == null) continue;
                    for (int i = 0; i < capture.Grids.Length; i++)
                    {
                        RestorationCatalogueEntry e = _restoreIndex.EntryOf(capture.PrefabId, i, capture.Grids);
                        if (e != null) job.Extra.Add(e);
                    }
                }
                foreach (KeyValuePair<long, MyObjectBuilder_CubeGrid[]> design in _customPrefabStore.AllDesigns())
                {
                    RestorationCatalogueEntry e = _restoreIndex.EntryOf(
                        "design:" + design.Key.ToString(CultureInfo.InvariantCulture), 0, design.Value);
                    if (e != null) job.Extra.Add(e);
                }
            }
            _restoreMatch = job;
        }

        /// <summary>Advances a pending match by one slice. Called every frame; a count check when
        /// nothing is pending.</summary>
        private void PumpRestoreMatch()
        {
            RestoreMatchJob job = _restoreMatch;
            if (job == null) return;
            long deadline = Stopwatch.GetTimestamp() + (long)(RestoreMatchSliceMs * Stopwatch.Frequency / 1000.0);
            job.Frames++;
            try
            {
                if (!_restoreIndex.Ready)
                {
                    if (!_restoreIndex.Step(deadline)) return;
                    FactionsPlugin.Log.Info("RESTORE_MATCH_INDEX prefabs=" + _restoreIndex.Prefabs
                        + " grids=" + _restoreIndex.Entries.Count + " blocks=" + _restoreIndex.Blocks
                        + " definitions=" + _restoreIndex.Definitions + " frames=" + _restoreIndex.Frames
                        + " ms=" + _restoreIndex.ElapsedMs.ToString("0", CultureInfo.InvariantCulture));
                    return;
                }

                if (job.Selection == null)
                {
                    var catalogue = new List<RestorationCatalogueEntry>(job.Extra);
                    foreach (RestorationCatalogueEntry e in _restoreIndex.Entries)
                        if (_customPrefabStore == null || !_customPrefabStore.IsCapture(e.Id)) catalogue.Add(e);
                    job.CatalogueSize = catalogue.Count;
                    job.Selection = RestorationMatch.Select(job.Current, catalogue);
                    if (job.Selection.Selected.Count == 0) { FinishRestoreMatch(job, new RestorationMatchResult(), null); return; }
                }

                if (job.Candidates.Count < job.Selection.Selected.Count)
                {
                    do
                    {
                        RestorationCatalogueEntry e = job.Selection.Selected[job.Candidates.Count];
                        var handle = (RestorationCatalogueIndex.Handle)e.Tag;
                        int unknown;
                        List<RestorationBlock> blocks = RestorationGrids.FromBuilder(handle.Grid, out unknown);
                        job.Unknown[e] = unknown;
                        job.Candidates.Add(new RestorationMatchCandidate { Entry = e, Blocks = blocks });
                    }
                    while (job.Candidates.Count < job.Selection.Selected.Count && Stopwatch.GetTimestamp() < deadline);
                    return;
                }

                if (job.Search == null)
                {
                    List<RestorationBlock> current = job.Current;
                    List<RestorationMatchCandidate> candidates = job.Candidates;
                    job.Search = Task.Run(() => RestorationMatch.Match(current, candidates));
                    return;
                }
                if (!job.Search.IsCompleted) return;
                if (job.Search.IsFaulted || job.Search.IsCanceled)
                    FinishRestoreMatch(job, null, job.Search.Exception != null ? job.Search.Exception.GetBaseException() : new TaskCanceledException());
                else
                    FinishRestoreMatch(job, job.Search.Result, null);
            }
            catch (Exception ex)
            {
                FinishRestoreMatch(job, null, ex);
            }
        }

        private void FinishRestoreMatch(RestoreMatchJob job, RestorationMatchResult r, Exception error)
        {
            _restoreMatch = null;
            FactionsPlugin plugin = FactionsPlugin.Instance;
            string head = "restore-plan: grid " + job.GridId + " '" + job.GridName + "'";
            if (error != null)
            {
                FactionsPlugin.Log.Warn(error, "Restoration match of grid " + job.GridId + " failed.");
                if (job.Done != null) { NotifyMatch(job, null, "the design match failed on the server."); return; }
                if (plugin != null) plugin.AppendOutbox("ERROR: restore-plan " + job.GridId + ": the match failed: " + error.Message);
                return;
            }

            RestorationCandidateSelection sel = job.Selection;
            double ms = (Stopwatch.GetTimestamp() - job.StartTimestamp) * 1000.0 / Stopwatch.Frequency;
            string runnerUp = r.RunnerUp != null ? r.RunnerUp.Entry.Id + ":" + Fmt(r.RunnerUp.Score) : "none";
            FactionsPlugin.Log.Info("RESTORE_MATCH grid=" + job.GridId
                + " key=" + sel.Via + ":" + (sel.KeyDefinition != null ? ShortDef(sel.KeyDefinition) : "none")
                + " catalogue=" + job.CatalogueSize + " eligible=" + sel.Eligible + " pruned=" + sel.Pruned
                + " scored=" + r.Scored + " dropped=" + sel.Dropped + " pairCapped=" + r.PairCapHits
                + " result=" + (r.Accepted ? "matched:" + r.Best.Entry.Id : "unmatched")
                + " best=" + (r.Best != null ? r.Best.Entry.Id + ":" + Fmt(r.Best.Score) : "none")
                + " coverage=" + (r.Best != null ? Fmt(r.Best.Coverage) : "0")
                + " runnerUp=" + runnerUp + " margin=" + Fmt(r.Margin)
                + " frames=" + job.Frames + " ms=" + ms.ToString("0", CultureInfo.InvariantCulture));

            if (job.Done != null)
            {
                if (!r.Accepted)
                {
                    NotifyMatch(job, null, sel.Refused ?? ("no known design fits it closely enough (best "
                        + (r.Best != null ? Fmt(r.Best.Score) : "none") + ", " + Fmt(RestorationMatch.AcceptScore) + " needed)"));
                    return;
                }
                var matchedHandle = (RestorationCatalogueIndex.Handle)r.Best.Entry.Tag;
                int unknownBlocks;
                var sources = new List<MyObjectBuilder_CubeBlock>();
                List<RestorationBlock> blocks = RestorationGrids.FromBuilder(matchedHandle.Grid, out unknownBlocks, sources);
                NotifyMatch(job, new RestorationTarget
                {
                    Source = "matched:" + r.Best.Entry.Id,
                    Via = "matched",
                    IsDesign = r.Best.Entry.Id.StartsWith("design:", StringComparison.Ordinal),
                    Reference = blocks,
                    Sources = sources,
                    Transform = r.Best.Fit.Transform,
                    Unknown = unknownBlocks,
                    Score = r.Best.Score,
                    DesignGrids = matchedHandle.Grids,
                    RootGrid = r.Best.Entry.GridIndex,
                }, null);
                return;
            }

            string text;
            if (r.Accepted)
            {
                var handle = (RestorationCatalogueIndex.Handle)r.Best.Entry.Tag;
                RestorationMatchCandidate chosen = null;
                foreach (RestorationMatchCandidate c in job.Candidates)
                    if (ReferenceEquals(c.Entry, r.Best.Entry)) { chosen = c; break; }
                int unknown;
                job.Unknown.TryGetValue(r.Best.Entry, out unknown);
                var reference = new RestorationReference { Source = "matched:" + r.Best.Entry.Id, Via = "matched" };
                var fit = new RestorationFitResult
                {
                    Reference = new List<RestorationBlock>(chosen.Blocks),
                    GridIndex = r.Best.Entry.GridIndex,
                    Unknown = unknown,
                    Fit = r.Best.Fit,
                };
                text = RenderRestorePlan(job.GridId, job.GridName, reference, handle.GridCount, fit, job.Current,
                                         " score=" + Fmt(r.Best.Score) + " runnerUp=" + runnerUp);
            }
            else
            {
                string why = sel.Refused
                    ?? (r.Best == null
                        ? "no reference holds enough of its blocks (" + sel.Eligible + " share its key, " + sel.Pruned + " ruled out by block counts)"
                        : "the best fit, " + r.Best.Entry.Id + ", places " + Fmt(r.Best.Score) + " of its blocks (" + r.Best.Fit.Matched
                          + "), under the " + Fmt(RestorationMatch.AcceptScore) + " a match needs");
                text = head + ": unmatched — " + why + ". Name one with prefab=<id>, or register the ship while it is whole.";
            }
            if (plugin != null)
                plugin.AppendOutbox("OK: restore-plan " + job.GridId + " (match) => " + text.Replace("\r", "").Replace("\n", " | "));
        }

        private static void NotifyMatch(RestoreMatchJob job, RestorationTarget target, string reason)
        {
            try { job.Done(target, reason); }
            catch (Exception ex) { FactionsPlugin.Log.Warn(ex, "Restoration match callback for grid " + job.GridId + " failed."); }
        }

        private static string Fmt(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
    }
}
