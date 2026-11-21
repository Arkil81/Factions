using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Factions.Core;
using Factions.Wire;
using HarmonyLib;
using NLog;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.Game.ObjectBuilders.Components;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Restoration at the Services Terminal (docs/ship-restoration.md): <b>Register design</b>
    /// snapshots a whole ship for a fee; <b>Restore</b> shows the bill and, on confirmation, welds
    /// the ship's damaged blocks and places its missing ones back, a few every interval, while it
    /// stays near the terminal and is not being shot.
    ///
    /// <para><b>One button, two offers.</b> The terminal's Active Grids row carries one
    /// <c>Restore</c> button. The quote it asks for is a restore bill when the ship is short of its
    /// design, and the registration fee when it is whole — a damaged ship cannot be registered and a
    /// whole one has nothing to restore, so the server's answer decides which is offered.</para>
    ///
    /// <para><b>Components flow ship → node → bank</b> (<see cref="RestorationBilling"/>): the ship's
    /// own cargo containers first, then the station node's store row at its price with the buyer's
    /// friendly discount, then the central bank at <see cref="RestorationBilling.BankPremium"/> over
    /// the item's value. The player's own inventory is not drawn on. Each block is paid for as it is
    /// placed or welded, never before: node purchases settle to the node and its owner's ledger once
    /// an interval; bank purchases leave circulation.</para>
    ///
    /// <para><b>Gates</b> are refuel's and rearm's (<see cref="TerminalAccess.TryAuthoriseCustomer"/>):
    /// range to the terminal, vanilla's use rule, the owner's trade bar, not the station, the caller
    /// owns the ship. The ship must be within <see cref="RangeM"/> of the terminal — the Active Grids
    /// list's own radius — to start, and a running restore pauses when it leaves that radius or takes
    /// damage, and resumes when it is back and quiet.</para>
    ///
    /// <para><b>The whole mechanical group.</b> The ship is the selected grid and the rotor, hinge,
    /// piston and suspension subgrids its design joins to it (<see cref="RestorationGroup"/>): each
    /// paired subgrid is welded and has its missing blocks placed into its own grid, and a base whose
    /// top is gone gets a new top first. One job, one bill, one order — all welds, then grid by grid.
    /// Ships joined by connector or merge block are not part of it. A design subgrid the restore cannot
    /// pair is left out and said so in the quote and in <c>RESTORE_BILL</c>'s <c>grids=</c>.</para>
    ///
    /// <para><b>A restart ends a restore.</b> Jobs are not persisted: every block placed so far is
    /// in the world and paid for, and pressing Restore again prices what is left. Nothing is owed and
    /// nothing is pre-paid, so dropping the job loses nothing.</para>
    /// </summary>
    internal sealed class RestorationService
    {
        private static readonly Logger Log = LogManager.GetLogger(FactionsPlugin.PluginName);

        /// <summary>Credits to register a design.</summary>
        internal const long RegisterFee = 50000;

        /// <summary>How close to the terminal a ship must stay. Vanilla's Active Grids list shows
        /// grids within this radius.</summary>
        internal const double RangeM = 200.0;

        /// <summary>How long a quote's reference is kept for the confirmation that follows it.</summary>
        private static readonly TimeSpan QuoteTtl = TimeSpan.FromMinutes(10);

        private const string ComponentPrefix = "Comp:";

        /// <summary>SE's own "add top" path on a base (private): creates the top part's grid, fully
        /// built when asked, and attaches it. Null when it does not resolve; a missing top is then
        /// refused (<c>RESTORE_TOP_REFUSED … reason=top-path-unresolved</c>) and nothing else changes.</summary>
        private static readonly MethodInfo CreateTopPartAndAttachMethod = AccessTools.Method(typeof(MyMechanicalConnectionBlockBase),
            "CreateTopPartAndAttach", new[] { typeof(long), typeof(MyMechanicalConnectionBlockBase.MyTopBlockSize), typeof(bool) });

        /// <summary>The base's own check that nothing sits where its top would go (private).</summary>
        private static readonly MethodInfo CanPlaceTopMethod = AccessTools.Method(typeof(MyMechanicalConnectionBlockBase),
            "CanPlaceTop", Type.EmptyTypes);

        private readonly UplinkManager _uplink;
        private readonly StationStoreManager _store;
        private readonly Func<TerminalWorld> _world;
        private readonly Func<RestorationReferences> _refs;
        private readonly Func<string, long> _itemValue;
        private readonly RegisterDesign _register;

        /// <summary>Registers <paramref name="live"/> as its own design, its fee paid to the owner of
        /// <paramref name="venue"/>'s node; false when it was not written, so it lasts until restart.</summary>
        internal delegate bool RegisterDesign(RestorationLiveGroup live, long payerIdentity, ulong payerSteam,
                                              SpatialNode node, long fee, out string fingerprint);

        private RestorationReferences Refs { get { return _refs(); } }

        private SpatialGraph GraphHere
        {
            get
            {
                TerminalWorld w = _world();
                return w != null ? w.Graph : null;
            }
        }

        private readonly Dictionary<long, Job> _jobs = new Dictionary<long, Job>();
        private readonly Dictionary<long, CachedTarget> _targets = new Dictionary<long, CachedTarget>();
        private readonly Dictionary<long, DateTime> _lastHit = new Dictionary<long, DateTime>();
        private readonly List<long> _scratchIds = new List<long>();
        private bool _damageHooked;

        /// <summary>A grid of a running restore: the design grid, the live grid it is built on —
        /// null for a grid whose top is not attached yet — and the design-to-live transform.</summary>
        private sealed class Slot
        {
            public RestorationGridPlan Plan;
            public MyCubeGrid Live;
            public GridTransform DesignToLive;
        }

        private sealed class CachedTarget
        {
            public RestorationTarget Target;
            public DateTime Utc;
        }

        /// <summary>Where a restore takes place and who pays: a terminal and its node, or — for a
        /// bench restore with no terminal near — an anchor point and no node.</summary>
        private sealed class Venue
        {
            public long TerminalId;
            public MyCubeBlock Terminal;
            public SpatialNode Node;
            public double OfferMultiplier = 1.0;
            public Vector3D Anchor;
            public long PayerIdentity;
            public ulong PayerSteam;
        }

        private sealed class Job
        {
            public RestorationJob Core;
            public long GridId;
            public string GridName;
            public Venue Venue;
            public RestorationTarget Target;
            public RestorationGroupPlan Plan;
            /// <summary>One per grid of <see cref="Plan"/>, at the same index.</summary>
            public readonly List<Slot> Slots = new List<Slot>();
            /// <summary>Every grid the job builds on: the ship's, its paired subgrids', and each top it
            /// has attached. The placement scope and the damage pause cover these.</summary>
            public readonly HashSet<long> GridIds = new HashSet<long>();
            public int Welds;
            public int Places;
            public readonly Dictionary<string, long> BankPrice = new Dictionary<string, long>(StringComparer.Ordinal);
            public RestorationJobStatus LastStatus;
            public string LastPause;
            public readonly Dictionary<string, long> PendingNodeUnits = new Dictionary<string, long>(StringComparer.Ordinal);
            public long PendingNodeCredits;
            public bool LimitHit;
        }

        public RestorationService(FactionRuntime runtime, UplinkManager uplink, StationStoreManager store)
            : this(() => TerminalWorld.Of(runtime), () => runtime.RestorationRefs,
                   key => runtime.World != null ? runtime.World.ItemUnitValue(key) : 0,
                   (RestorationLiveGroup live, long payer, ulong steam, SpatialNode node, long fee, out string fingerprint) =>
                   {
                       if (node != null && !string.IsNullOrEmpty(node.OwnerFactionTag) && runtime.World != null)
                           runtime.World.Earn(node.OwnerFactionTag, fee);
                       return runtime.CommitRegistration(live, payer, fee, out fingerprint);
                   },
                   uplink, store)
        {
        }

        /// <param name="itemValue">A component's value at the world's prices, by its stripped key;
        /// 0 when unknown, and the bank then does not sell it.</param>
        public RestorationService(Func<TerminalWorld> world, Func<RestorationReferences> refs, Func<string, long> itemValue,
                                  RegisterDesign register, UplinkManager uplink, StationStoreManager store)
        {
            _world = world;
            _refs = refs;
            _itemValue = itemValue;
            _register = register;
            _uplink = uplink;
            _store = store;
            if (_uplink == null) return;
            _uplink.RegisterHandler(WireMessageKind.RestoreQuoteRequest, OnQuoteRequest);
            _uplink.RegisterHandler(WireMessageKind.RestoreRequest, OnRestoreRequest);
        }

        private bool Granted(ulong steamId)
        {
            return _uplink != null && (_uplink.GrantedFeatures(steamId) & WireFeatures.Restoration) != 0;
        }

        // ---- gates -------------------------------------------------------------------------------

        private bool TryResolve(ulong steamId, long terminalEntityId, long gridEntityId, out Venue venue,
            out MyCubeGrid grid, out string refusal)
        {
            return TryResolve(steamId, terminalEntityId, gridEntityId, true, out venue, out grid, out refusal);
        }

        /// <param name="requireRange">False to answer about a ship that has left range — a running
        /// restore's state and its Stop are offered wherever the ship is.</param>
        private bool TryResolve(ulong steamId, long terminalEntityId, long gridEntityId, bool requireRange,
            out Venue venue, out MyCubeGrid grid, out string refusal)
        {
            venue = null;
            grid = null;
            if (_store == null) { refusal = "Factions is not ready on this server."; return false; }

            TerminalContext ctx;
            if (!TerminalAccess.TryAuthoriseCustomer(_world(), steamId, terminalEntityId, out ctx, out refusal)) return false;

            if (!MyEntities.TryGetEntityById<MyCubeGrid>(gridEntityId, out grid, false)
                || grid == null || grid.MarkedForClose || grid.Closed)
            {
                grid = null;
                refusal = "That ship is no longer here.";
                return false;
            }
            // The ship is its mechanical group, listed under the group's main grid as vanilla's Repair
            // tab lists it; a subgrid's id names the same ship.
            grid = RestorationGrids.MainOf(grid);

            var station = new HashSet<MyCubeGrid>(MyCubeGridGroups.Static.Mechanical.GetGroupNodes(ctx.Block.CubeGrid));
            foreach (MyCubeGrid g in MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid))
            {
                if (station.Contains(g))
                {
                    refusal = "That is the station itself. Select a ship beside it.";
                    return false;
                }
            }
            if (grid.IsStatic)
            {
                refusal = NameOf(grid) + " is a station; only ships are restored.";
                return false;
            }
            if (!grid.BigOwners.Contains(ctx.IdentityId))
            {
                refusal = "You do not own " + NameOf(grid) + ".";
                return false;
            }

            venue = new Venue
            {
                TerminalId = terminalEntityId,
                Terminal = ctx.Block,
                Node = ctx.Node,
                OfferMultiplier = DockSupplyManager.OffersMultiplier(ctx),
                Anchor = ctx.Block.PositionComp.GetPosition(),
                PayerIdentity = ctx.IdentityId,
                PayerSteam = steamId,
            };
            if (requireRange && !InRange(grid, venue))
            {
                refusal = NameOf(grid) + " is more than " + Num((long)RangeM) + " m from this terminal. Bring it closer.";
                return false;
            }
            refusal = null;
            return true;
        }

        private static bool InRange(MyCubeGrid grid, Venue venue)
        {
            if (grid == null || grid.Closed || grid.MarkedForClose) return false;
            if (venue.Terminal != null)
            {
                if (venue.Terminal.Closed || venue.Terminal.MarkedForClose) return false;
                venue.Anchor = venue.Terminal.PositionComp.GetPosition();
            }
            return Vector3D.Distance(grid.PositionComp.WorldAABB.Center, venue.Anchor) <= RangeM + grid.PositionComp.WorldAABB.HalfExtents.Length();
        }

        // ---- quote -------------------------------------------------------------------------------

        private void OnQuoteRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId;
            if (!FactionsWire.TryReadRestoreQuoteRequest(data, out terminalEntityId, out gridEntityId))
            {
                Log.Warn("Restore quote request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            WireRestoreQuote reply;
            bool managed = TerminalAccess.IsFactionsStationTerminal(GraphHere, terminalEntityId);
            try
            {
                if (!managed || gridEntityId == 0)
                    reply = new WireRestoreQuote { TerminalEntityId = terminalEntityId, GridEntityId = gridEntityId };
                else
                    reply = BuildQuote(steamId, terminalEntityId, gridEntityId);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Restore quote failed for " + steamId + ".");
                reply = Refusal(terminalEntityId, gridEntityId, "The restore quote failed on the server.");
            }
            if (reply == null) return;
            reply.Managed = managed;
            _uplink.Send(steamId, FactionsWire.WriteRestoreQuote(reply));
        }

        private WireRestoreQuote BuildQuote(ulong steamId, long terminalEntityId, long gridEntityId)
        {
            Venue venue;
            MyCubeGrid grid;
            string refusal;
            if (!TryResolve(steamId, terminalEntityId, gridEntityId, false, out venue, out grid, out refusal))
                return Refusal(terminalEntityId, gridEntityId, refusal);

            Job running = JobCovering(grid.EntityId);
            if (running != null) return RunningQuote(terminalEntityId, gridEntityId, running);
            if (!InRange(grid, venue))
                return Refusal(terminalEntityId, gridEntityId,
                    NameOf(grid) + " is more than " + Num((long)RangeM) + " m from this terminal. Bring it closer.");

            RestorationLiveGroup live = RestorationGrids.LiveGroup(grid);
            RestorationTarget target;
            bool unmarked;
            string error;
            if (!TryTarget(grid, live.Root, out target, out unmarked, out error))
            {
                if (!unmarked) return Refusal(terminalEntityId, gridEntityId, "No design for " + NameOf(grid) + ": " + error);
                string matchRefusal;
                long gridId = grid.EntityId;
                if (!Refs.StartRestoreMatchFor(grid, (t, why) => OnMatched(steamId, terminalEntityId, gridId, t, why), out matchRefusal))
                    return Refusal(terminalEntityId, gridEntityId, "Cannot find the design of " + NameOf(grid) + " now: " + matchRefusal);
                return new WireRestoreQuote
                {
                    TerminalEntityId = terminalEntityId,
                    GridEntityId = gridEntityId,
                    Offer = WireRestoreOffer.Pending,
                    Message = "Matching " + NameOf(grid) + " against known designs…",
                };
            }
            return QuoteFor(venue, grid, live, target, terminalEntityId);
        }

        /// <summary>A running or paused restore, as the panel shows it while the button reads Stop.</summary>
        private static WireRestoreQuote RunningQuote(long terminalEntityId, long gridEntityId, Job job)
        {
            RestorationJob c = job.Core;
            return new WireRestoreQuote
            {
                TerminalEntityId = terminalEntityId,
                GridEntityId = gridEntityId,
                Offer = WireRestoreOffer.Running,
                Message = c.Status == RestorationJobStatus.Paused
                    ? (c.PauseReason == "damage" ? "Paused: the ship is taking damage." : "Paused: the ship is out of the terminal's " + Num((long)RangeM) + " m range.")
                    : "Restoring.",
                Source = job.Target.Source,
                Score = job.Target.Score,
                Welds = job.Welds,
                Places = job.Places,
                Seconds = RestorationJob.EstimatedSeconds(c.Remaining),
                Placed = c.Placed + c.Attached,
                Welded = c.Welded,
                Skipped = c.Skipped,
                Total = c.Steps.Count,
                Paid = c.Paid,
                PauseReason = c.Status == RestorationJobStatus.Paused ? c.PauseReason : "",
            };
        }

        /// <summary>The running restore that builds on <paramref name="gridEntityId"/> — the ship's own
        /// grid or one of its subgrids; null when none does.</summary>
        private Job JobCovering(long gridEntityId)
        {
            Job job;
            if (_jobs.TryGetValue(gridEntityId, out job)) return job;
            foreach (Job j in _jobs.Values)
                if (j.GridIds.Contains(gridEntityId)) return j;
            return null;
        }

        /// <summary>A match finished: re-check the gates (the player may have walked off) and send
        /// the quote, or say why there is none.</summary>
        private void OnMatched(ulong steamId, long terminalEntityId, long gridEntityId, RestorationTarget target, string why)
        {
            WireRestoreQuote reply;
            try
            {
                Venue venue;
                MyCubeGrid grid;
                string refusal;
                if (!TryResolve(steamId, terminalEntityId, gridEntityId, out venue, out grid, out refusal)) return;
                RestorationLiveGroup live = RestorationGrids.LiveGroup(grid);
                if (target == null)
                {
                    reply = UnmatchedAnswer(terminalEntityId, grid, live, why);
                }
                else
                {
                    _targets[gridEntityId] = new CachedTarget { Target = target, Utc = DateTime.UtcNow };
                    reply = QuoteFor(venue, grid, live, target, terminalEntityId);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Restore quote after a match failed for " + steamId + ".");
                reply = Refusal(terminalEntityId, gridEntityId, "The restore quote failed on the server.");
            }
            reply.Managed = true;
            _uplink.Send(steamId, FactionsWire.WriteRestoreQuote(reply));
        }

        /// <summary>No design is known. A whole ship can be registered; a damaged one cannot.</summary>
        private WireRestoreQuote UnmatchedAnswer(long terminalEntityId, MyCubeGrid grid, RestorationLiveGroup live, string why)
        {
            bool whole = true;
            foreach (RestorationBlock b in live.AllBlocks())
                if (b.BelowFullIntegrity) { whole = false; break; }
            if (whole) return RegisterOffer(terminalEntityId, grid, "No design is known for this ship.", null);
            return Refusal(terminalEntityId, grid.EntityId, "No design is known for " + NameOf(grid) + ": " + why
                + ". It was not bought or spawned from a known hull and has no registered design. Repair it, then register it here to make it restorable.");
        }

        /// <summary>The ship is whole: its design may be registered for the fee, unless it already is.</summary>
        private WireRestoreQuote RegisterOffer(long terminalEntityId, MyCubeGrid grid, string lead, RestorationTarget target)
        {
            RestorationLiveGroup live;
            string refusal;
            WireRestoreQuote q;
            if (!Refs.CheckRegistrable(grid, false, out live, out refusal))
            {
                q = Refusal(terminalEntityId, grid.EntityId, lead + " Registering it is " + refusal);
            }
            else
            {
                string fingerprint = RestorationDiff.Fingerprint(live.Assembly.Grids);
                string registered = Refs.RegisteredFingerprint(grid.EntityId);
                if (registered != null && string.Equals(registered, fingerprint, StringComparison.Ordinal))
                {
                    q = Refusal(terminalEntityId, grid.EntityId, lead + " Its design is registered as it stands.");
                }
                else
                {
                    q = new WireRestoreQuote
                    {
                        TerminalEntityId = terminalEntityId,
                        GridEntityId = grid.EntityId,
                        Offer = WireRestoreOffer.Register,
                        Credits = RegisterFee,
                        Message = lead + " Register its design for " + Num(RegisterFee) + " credits: a registered ship is restored to exactly"
                                + " this, your own additions and its subgrids included."
                                + (registered != null ? " This replaces the design registered earlier." : ""),
                    };
                }
            }
            if (target != null)
            {
                q.Source = target.Source;
                q.Score = target.Score;
            }
            return q;
        }

        /// <summary>The group plan of <paramref name="live"/> against <paramref name="target"/>'s design.</summary>
        private static RestorationGroupPlan PlanFor(RestorationLiveGroup live, RestorationTarget target)
        {
            RestorationDesign design = target.Design;
            return RestorationGroup.Plan(design.Assembly, target.RootGrid, live.Assembly, 0, target.Transform);
        }

        private WireRestoreQuote QuoteFor(Venue venue, MyCubeGrid grid, RestorationLiveGroup live, RestorationTarget target,
            long terminalEntityId)
        {
            RestorationGroupPlan plan = PlanFor(live, target);
            RestorationSequence seq = plan.Sequence;
            string unpaired = UnpairedNote(plan);
            if (seq.Steps.Count == 0)
            {
                WireRestoreQuote whole = RegisterOffer(terminalEntityId, grid, "Nothing of the design is missing or damaged." + unpaired, target);
                whole.Blocked = seq.Blocked;
                whole.Unreachable = seq.Unreachable;
                whole.Extra = plan.Extra;
                return whole;
            }

            RestorationSupply supply = BuildSupply(grid, venue, seq.Need().Keys);
            RestorationBill bill = RestorationBilling.Price(seq.Need(), supply);
            int seconds = RestorationJob.EstimatedSeconds(seq.Steps.Count);

            LogBill(grid, venue.PayerIdentity, venue, target, plan, bill, seconds);

            _targets[grid.EntityId] = new CachedTarget { Target = target, Utc = DateTime.UtcNow };

            var lines = new List<WireRestoreLine>();
            foreach (RestorationBillLine l in bill.Lines)
                lines.Add(new WireRestoreLine
                {
                    Name = l.Component,
                    Need = l.Need,
                    FromShip = l.FromShip,
                    FromNode = l.FromNode,
                    NodeCredits = l.NodeCredits,
                    FromBank = l.FromBank,
                    BankCredits = l.BankCredits,
                    Short = l.Short,
                });

            string shortOf = bill.FirstShort;
            return new WireRestoreQuote
            {
                TerminalEntityId = terminalEntityId,
                GridEntityId = grid.EntityId,
                Offer = shortOf == null ? WireRestoreOffer.Restore : WireRestoreOffer.None,
                Message = (shortOf == null
                    ? "Charged per block as it is built. Keep the ship within " + Num((long)RangeM) + " m of this terminal; leaving or taking fire pauses it."
                    : shortOf + " is not in the cargo, not sold at this station and not sold by the bank, so the restore cannot run.")
                    + SubgridNote(plan) + unpaired,
                Source = target.Source,
                Score = target.Score,
                Welds = seq.Welds,
                Places = seq.Places + seq.Attaches,
                Blocked = seq.Blocked,
                Extra = plan.Extra,
                Unreachable = seq.Unreachable,
                Credits = bill.Credits,
                Seconds = seconds,
                Lines = lines.ToArray(),
            };
        }

        /// <summary><c>RESTORE_BILL</c>: the whole group's bill; <c>grids=</c> is each grid with work,
        /// <c>design grid:welds/places</c> (<c>+attach</c> when its top is attached first), then each
        /// design subgrid left out as <c>design grid:reason</c>.</summary>
        private static void LogBill(MyCubeGrid grid, long payer, Venue venue, RestorationTarget target, RestorationGroupPlan plan,
            RestorationBill bill, int seconds)
        {
            RestorationSequence seq = plan.Sequence;
            Log.Info("RESTORE_BILL grid=" + grid.EntityId + " player=" + payer
                   + " node=" + (venue.Node != null ? venue.Node.Id : "none") + " source=" + target.Source
                   + " steps=" + seq.Steps.Count + " welds=" + seq.Welds + " places=" + seq.Places + " attaches=" + seq.Attaches
                   + " blocked=" + seq.Blocked + " unreachable=" + seq.Unreachable
                   + " grids=" + plan.GridsField()
                   + (plan.UnresolvedJoints > 0 ? " unresolvedJoints=" + plan.UnresolvedJoints : "")
                   + " fromShip=" + bill.FromShip + " fromNode=" + bill.FromNode + " nodeCredits=" + bill.NodeCredits
                   + " fromBank=" + bill.FromBank + " bankCredits=" + bill.BankCredits
                   + " total=" + bill.Credits + " seconds=" + seconds + " short=" + (bill.FirstShort ?? "none"));
        }

        /// <summary>How many subgrids the restore covers, for the quote; empty for a single-grid ship.</summary>
        private static string SubgridNote(RestorationGroupPlan plan)
        {
            int subgrids = plan.Grids.Count - 1;
            if (subgrids <= 0) return "";
            int attached = plan.Sequence.Attaches;
            return " Covers " + subgrids + " subgrid(s)" + (attached > 0 ? ", " + attached + " on a new rotor, hinge or piston top" : "") + ".";
        }

        /// <summary>The design subgrids the restore leaves out, in the player's terms; empty when none.</summary>
        private static string UnpairedNote(RestorationGroupPlan plan)
        {
            if (plan.Unpaired.Count == 0 && plan.UnresolvedJoints == 0) return "";
            var why = new List<string>();
            foreach (RestorationUnpaired u in plan.Unpaired)
            {
                string w = UnpairedReason(u.Reason);
                if (!why.Contains(w)) why.Add(w);
            }
            if (plan.UnresolvedJoints > 0) why.Add(plan.UnresolvedJoints + " joint(s) of the design name no part of it");
            return " " + plan.Unpaired.Count + " subgrid(s) of the design are left out: " + string.Join("; ", why.ToArray()) + ".";
        }

        private static string UnpairedReason(string reason)
        {
            switch (reason)
            {
                case RestorationGroup.TopDiffers: return "the ship's rotor, hinge or piston part is another type than the design's";
                case RestorationGroup.BaseNotRestorable: return "the part that holds it is missing and cannot be placed back";
                case RestorationGroup.BaseGone: return "the part that holds it is not on the ship";
                case RestorationGroup.LiveGridTaken: return "the ship's subgrid there already stands for another";
                case RestorationGroup.ParentUnpaired: return "it hangs on a subgrid that is left out";
                default: return reason;
            }
        }

        private static string BillText(MyCubeGrid grid, RestorationTarget target, RestorationGroupPlan plan, RestorationBill bill, int seconds)
        {
            RestorationSequence seq = plan.Sequence;
            var sb = new StringBuilder();
            sb.Append("Restore ").Append(NameOf(grid)).Append(" to its design (").Append(target.Source).Append("): weld ")
              .Append(seq.Welds).Append(", place ").Append(seq.Places).Append(" block(s)");
            if (seq.Attaches > 0) sb.Append(", attach ").Append(seq.Attaches).Append(" top part(s)");
            sb.Append(", about ").Append(Duration(seconds)).Append('.');
            string subgrids = SubgridNote(plan);
            if (subgrids.Length > 0) sb.Append(subgrids);
            if (seq.Blocked > 0) sb.AppendLine().Append(seq.Blocked).Append(" design block(s) are left out: your own blocks sit in their cells.");
            if (seq.Unreachable > 0) sb.AppendLine().Append(seq.Unreachable).Append(" design block(s) are left out: nothing left on the ship reaches them.");
            string unpaired = UnpairedNote(plan);
            if (unpaired.Length > 0) sb.AppendLine().Append(unpaired.TrimStart());
            sb.AppendLine().AppendLine();
            foreach (RestorationBillLine l in bill.Lines)
            {
                sb.Append(l.Component).Append(' ').Append(Num(l.Need)).Append(':');
                bool any = false;
                if (l.FromShip > 0) { sb.Append(" cargo ").Append(Num(l.FromShip)); any = true; }
                if (l.FromNode > 0) { sb.Append(any ? "," : "").Append(" station ").Append(Num(l.FromNode)).Append(" (").Append(Num(l.NodeCredits)).Append(" cr)"); any = true; }
                if (l.FromBank > 0) { sb.Append(any ? "," : "").Append(" bank ").Append(Num(l.FromBank)).Append(" (").Append(Num(l.BankCredits)).Append(" cr)"); any = true; }
                if (l.Short > 0) sb.Append(any ? "," : "").Append(" NOT AVAILABLE ").Append(Num(l.Short));
                sb.AppendLine();
            }
            sb.AppendLine().Append("Total ").Append(Num(bill.Credits)).Append(" credits: station ").Append(Num(bill.NodeCredits))
              .Append(", bank ").Append(Num(bill.BankCredits)).Append(" at a premium. Charged per block as it is built.");
            string shortOf = bill.FirstShort;
            if (shortOf != null)
                sb.AppendLine().Append(shortOf).Append(" is not in the cargo, not sold at this station and not sold by the bank, so the restore cannot run.");
            else
                sb.AppendLine().Append("Keep the ship within ").Append(Num((long)RangeM)).Append(" m of this terminal; leaving or taking fire pauses it.");
            return sb.ToString();
        }

        private bool TryTarget(MyCubeGrid grid, List<RestorationBlock> current, out RestorationTarget target, out bool unmarked,
            out string error)
        {
            if (Refs.TryRestorationTarget(grid, null, current, out target, out unmarked, out error)) return true;
            if (!unmarked) return false;
            CachedTarget cached;
            if (_targets.TryGetValue(grid.EntityId, out cached) && DateTime.UtcNow - cached.Utc <= QuoteTtl)
            {
                target = cached.Target;
                unmarked = false;
                error = null;
                return true;
            }
            return false;
        }

        // ---- start / register / stop -------------------------------------------------------------

        private void OnRestoreRequest(ulong steamId, byte[] data)
        {
            long terminalEntityId, gridEntityId, maxCredits;
            WireRestoreAction action;
            if (!FactionsWire.TryReadRestoreRequest(data, out terminalEntityId, out gridEntityId, out action, out maxCredits))
            {
                Log.Warn("Restore request from " + steamId + " was unreadable.");
                return;
            }
            if (!Granted(steamId)) return;

            bool ok;
            string message;
            long balanceAfter = 0;
            try
            {
                ok = HandleRequest(steamId, terminalEntityId, gridEntityId, action, maxCredits, out message);
                balanceAfter = ServicesTerminalHelpers.BalanceOf(MySession.Static.Players.TryGetIdentityId(steamId));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Restore request failed for " + steamId + ".");
                ok = false;
                message = "The request failed on the server. Nothing is charged for blocks that were not built.";
            }
            if (!ok)
                Log.Info("RESTORE_REFUSED grid=" + gridEntityId + " player=" + steamId + " action=" + action + " reason=" + message);
            _uplink.Send(steamId, FactionsWire.WriteRestoreResult(gridEntityId, ok, false, message, balanceAfter));
        }

        private bool HandleRequest(ulong steamId, long terminalEntityId, long gridEntityId, WireRestoreAction action,
            long maxCredits, out string message)
        {
            if (action == WireRestoreAction.Stop)
            {
                Job job;
                long identity = MySession.Static.Players.TryGetIdentityId(steamId);
                job = JobCovering(gridEntityId);
                if (job == null) { message = "No restore of that ship is running."; return false; }
                if (job.Venue.PayerIdentity != identity) { message = "That restore is not yours to stop."; return false; }
                job.Core.Stop("stopped at the terminal.");
                Finish(job, "stopped");
                message = "Restore stopped. " + Summary(job);
                return true;
            }

            Venue venue;
            MyCubeGrid grid;
            if (!TryResolve(steamId, terminalEntityId, gridEntityId, out venue, out grid, out message)) return false;

            if (action == WireRestoreAction.Register) return Register(venue, grid, maxCredits, out message);
            return Start(venue, grid, maxCredits, out message);
        }

        private bool Register(Venue venue, MyCubeGrid grid, long maxCredits, out string message)
        {
            if (maxCredits < RegisterFee)
            {
                message = "The registration fee is " + Num(RegisterFee) + " credits. Ask again for the offer.";
                return false;
            }
            if (JobCovering(grid.EntityId) != null)
            {
                message = NameOf(grid) + " is being restored; register it once that is done.";
                return false;
            }
            RestorationLiveGroup live;
            string refusal;
            if (!Refs.CheckRegistrable(grid, false, out live, out refusal))
            {
                message = "Registering " + NameOf(grid) + " is " + refusal;
                return false;
            }
            long balance = ServicesTerminalHelpers.BalanceOf(venue.PayerIdentity);
            if (balance < RegisterFee || !MyBankingSystem.ChangeBalance(venue.PayerIdentity, -RegisterFee))
            {
                message = "Registering costs " + Num(RegisterFee) + " credits; your balance is " + Num(balance) + ".";
                return false;
            }
            string fingerprint;
            bool persisted = _register(live, venue.PayerIdentity, venue.PayerSteam, venue.Node, RegisterFee, out fingerprint);
            _targets.Remove(grid.EntityId);
            message = NameOf(grid) + "'s design is registered: " + live.BlockCount + " blocks"
                    + (live.Grids.Count > 1 ? " in " + live.Grids.Count + " grids" : "") + ". It can now be restored to exactly this."
                    + (persisted ? "" : " (The server could not save it; it lasts until the next restart.)");
            return true;
        }

        private bool Start(Venue venue, MyCubeGrid grid, long maxCredits, out string message)
        {
            if (JobCovering(grid.EntityId) != null)
            {
                message = NameOf(grid) + " is already being restored.";
                return false;
            }
            RestorationLiveGroup live = RestorationGrids.LiveGroup(grid);
            RestorationTarget target;
            bool unmarked;
            string error;
            if (!TryTarget(grid, live.Root, out target, out unmarked, out error))
            {
                message = unmarked ? "The bill has expired. Press Restore again for a new one." : "No design for " + NameOf(grid) + ": " + error;
                return false;
            }
            return StartJob(venue, grid, live, target, maxCredits, out message);
        }

        private bool StartJob(Venue venue, MyCubeGrid grid, RestorationLiveGroup live, RestorationTarget target,
            long budget, out string message)
        {
            RestorationGroupPlan plan = PlanFor(live, target);
            RestorationSequence seq = plan.Sequence;
            if (seq.Steps.Count == 0)
            {
                message = "Nothing of " + NameOf(grid) + "'s design is missing or damaged.";
                return false;
            }
            DateTime now = DateTime.UtcNow;
            var job = new Job
            {
                Core = new RestorationJob(seq.Steps, Math.Max(0, budget), now),
                GridId = grid.EntityId,
                GridName = NameOf(grid),
                Venue = venue,
                Target = target,
                Plan = plan,
                Welds = seq.Welds,
                Places = seq.Places + seq.Attaches,
                LastStatus = RestorationJobStatus.Running,
            };
            foreach (RestorationGridPlan g in plan.Grids)
            {
                MyCubeGrid liveGrid = g.Attach ? null : live.Grids[g.LiveGrid];
                job.Slots.Add(new Slot { Plan = g, Live = liveGrid, DesignToLive = g.Transform });
                if (liveGrid != null) job.GridIds.Add(liveGrid.EntityId);
            }
            FillBankPrices(job.BankPrice, seq.Need().Keys);
            _jobs[grid.EntityId] = job;
            _lastHit.Remove(grid.EntityId);
            HookDamage();

            int seconds = RestorationJob.EstimatedSeconds(seq.Steps.Count);
            Log.Info("RESTORE_STARTED grid=" + grid.EntityId + " player=" + venue.PayerIdentity
                   + " terminal=" + venue.TerminalId + " node=" + (venue.Node != null ? venue.Node.Id : "none")
                   + " source=" + target.Source + " steps=" + seq.Steps.Count + " welds=" + seq.Welds
                   + " places=" + seq.Places + " attaches=" + seq.Attaches + " grids=" + plan.Grids.Count
                   + " budget=" + budget + " seconds=" + seconds);
            message = "Restoring " + NameOf(grid) + ": " + seq.Steps.Count + " block(s)"
                    + (plan.Grids.Count > 1 ? " on " + plan.Grids.Count + " grids" : "") + ", about " + Duration(seconds)
                    + ". Keep it within " + Num((long)RangeM) + " m of the terminal; it pauses if it leaves or takes fire.";
            return true;
        }

        // ---- the timed work ----------------------------------------------------------------------

        /// <summary>Advances every running restore. Called every frame; a count check when none runs.</summary>
        public void Pump()
        {
            if (_jobs.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            _scratchIds.Clear();
            _scratchIds.AddRange(_jobs.Keys);
            foreach (long id in _scratchIds)
            {
                Job job;
                if (!_jobs.TryGetValue(id, out job)) continue;
                try { PumpJob(job, now); }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Restore of grid " + id + " failed; stopped.");
                    job.Core.Stop("the restore failed on the server.");
                    Finish(job, "failed");
                }
            }
        }

        private void PumpJob(Job job, DateTime now)
        {
            MyCubeGrid grid;
            if (!MyEntities.TryGetEntityById<MyCubeGrid>(job.GridId, out grid, false) || grid == null
                || grid.Closed || grid.MarkedForClose)
            {
                job.Core.Stop("the ship is gone.");
                Finish(job, "lost");
                return;
            }
            if (!grid.BigOwners.Contains(job.Venue.PayerIdentity))
            {
                job.Core.Stop("you no longer own the ship.");
                Finish(job, "owner");
                return;
            }

            bool inRange = InRange(grid, job.Venue);
            DateTime hit;
            if (!_lastHit.TryGetValue(job.GridId, out hit)) hit = DateTime.MinValue;
            if (now < job.Core.NextDueUtc && inRange && job.Core.Status == RestorationJobStatus.Running) return;

            RestorationSupply supply = null;
            var applier = new Applier(this, job, grid);
            if (inRange && (hit == DateTime.MinValue || (now - hit).TotalSeconds >= RestorationJob.DamageQuietSeconds))
            {
                RefreshWeldBills(job);
                var need = new HashSet<string>(StringComparer.Ordinal);
                for (int k = 0; k < RestorationJob.BlocksPerInterval; k++)
                {
                    RestorationStep step = job.Core.Upcoming(k);
                    if (step == null) break;
                    foreach (string c in step.Bill.Keys) need.Add(c);
                }
                supply = BuildSupply(grid, job.Venue, need, job.BankPrice);
            }

            int attempted = job.Core.Pump(now, inRange, hit, supply ?? new RestorationSupply(), applier);
            FlushNode(job);
            if (job.LimitHit && !job.Core.Finished) job.Core.Stop("your block limit (PCU) is reached.");

            if (attempted > 0 && DebugTraces.Restoration)
                Log.Info("RESTORE_PROGRESS grid=" + job.GridId + " placed=" + job.Core.Placed + " welded=" + job.Core.Welded
                       + " attached=" + job.Core.Attached + " skipped=" + job.Core.Skipped + " pass=" + job.Core.Passes
                       + " paid=" + job.Core.Paid + " left=" + job.Core.Remaining);

            ReportTransitions(job);
        }

        /// <summary>A weld is billed for what its block lacks when it is welded, not when the bill was
        /// quoted: a block hit again since costs more, one the Repair tab or a hand welder finished
        /// costs nothing, so no component is paid for twice.</summary>
        private static void RefreshWeldBills(Job job)
        {
            var missing = new Dictionary<string, int>();
            RestorationJob core = job.Core;
            for (int k = 0; k < RestorationJob.BlocksPerInterval; k++)
            {
                RestorationStep step = core.Upcoming(k);
                if (step == null) break;
                if (step.Kind != RestorationStepKind.Weld) continue;
                MyCubeGrid grid = step.Grid >= 0 && step.Grid < job.Slots.Count ? job.Slots[step.Grid].Live : null;
                if (grid == null || grid.Closed || grid.MarkedForClose) continue;
                var min = new Vector3I(step.Placement.Min.X, step.Placement.Min.Y, step.Placement.Min.Z);
                MySlimBlock slim = grid.GetCubeBlock(min);
                if (slim == null || slim.Min != min) continue;
                missing.Clear();
                slim.GetMissingComponents(missing);
                step.Bill.Clear();
                foreach (KeyValuePair<string, int> kv in missing)
                    if (kv.Value > 0 && !string.IsNullOrEmpty(kv.Key)) step.Bill[kv.Key] = kv.Value;
            }
        }

        private void ReportTransitions(Job job)
        {
            RestorationJob core = job.Core;
            if (core.Finished)
            {
                Finish(job, core.Status == RestorationJobStatus.Done ? "complete" : "stopped");
                return;
            }
            if (core.Status == RestorationJobStatus.Paused
                && (job.LastStatus != RestorationJobStatus.Paused || job.LastPause != core.PauseReason))
            {
                Log.Info("RESTORE_PAUSED grid=" + job.GridId + " reason=" + core.PauseReason + " placed=" + core.Placed
                       + " welded=" + core.Welded + " left=" + core.Remaining);
                Notice(job, "Restore of " + job.GridName + " paused: "
                            + (core.PauseReason == "damage" ? "it is taking damage." : "it is out of the terminal's " + Num((long)RangeM) + " m range."));
            }
            else if (core.Status == RestorationJobStatus.Running && job.LastStatus == RestorationJobStatus.Paused)
            {
                Log.Info("RESTORE_RESUMED grid=" + job.GridId + " left=" + core.Remaining);
                Notice(job, "Restore of " + job.GridName + " resumed.");
            }
            job.LastStatus = core.Status;
            job.LastPause = core.PauseReason;
        }

        private void Finish(Job job, string end)
        {
            if (!_jobs.Remove(job.GridId)) return;
            _lastHit.Remove(job.GridId);
            FlushNode(job);
            RestorationJob core = job.Core;
            Log.Info("RESTORE_DONE grid=" + job.GridId + " end=" + end + " placed=" + core.Placed + " welded=" + core.Welded
                   + " attached=" + core.Attached + " skipped=" + core.Skipped + " passes=" + core.Passes + " retried=" + core.Retried
                   + " grids=" + job.GridIds.Count + " paid=" + core.Paid + " node=" + core.PaidNode + " bank=" + core.PaidBank
                   + " fromShip=" + core.FromShipUnits + " fromNode=" + core.FromNodeUnits + " fromBank=" + core.FromBankUnits
                   + " left=" + core.Remaining + (core.StopReason != null ? " reason=" + core.StopReason : ""));
            if (end == "stopped" && core.StopReason == "stopped at the terminal.") return;
            bool done = core.Status == RestorationJobStatus.Done;
            Notice(job, (done ? "Restore of " + job.GridName + " is complete. "
                              : "Restore of " + job.GridName + " stopped: " + core.StopReason + " ") + Summary(job), done);
        }

        private static string Summary(Job job)
        {
            RestorationJob c = job.Core;
            return "Placed " + c.Placed + ", welded " + c.Welded + (c.Attached > 0 ? ", attached " + c.Attached + " top part(s)" : "")
                 + (c.Skipped > 0 ? ", " + c.Skipped + " refused by the game" : "")
                 + "; paid " + Num(c.Paid) + " credits (station " + Num(c.PaidNode) + ", bank " + Num(c.PaidBank) + ")"
                 + (c.FromShipUnits > 0 ? ", " + Num(c.FromShipUnits) + " component(s) from cargo" : "") + ".";
        }

        private string StatusText(Job job)
        {
            RestorationJob c = job.Core;
            string state = c.Status == RestorationJobStatus.Paused
                ? "paused (" + (c.PauseReason == "damage" ? "taking damage" : "out of range") + ")"
                : "running";
            return "Restore of " + job.GridName + " is " + state + ": " + c.Remaining + " block(s) left, about "
                 + Duration(RestorationJob.EstimatedSeconds(c.Remaining)) + ". " + Summary(job);
        }

        /// <param name="ok">False for a restore that stopped short; the HUD shows it in red.</param>
        private void Notice(Job job, string text, bool ok = true)
        {
            ulong steam = job.Venue.PayerSteam;
            if (steam == 0 || _uplink == null || !Granted(steam)) return;
            _uplink.Send(steam, FactionsWire.WriteRestoreResult(job.GridId, ok, true, text,
                ServicesTerminalHelpers.BalanceOf(job.Venue.PayerIdentity)));
        }

        /// <summary>Node purchases of this interval: the node is debited, its owner earns.</summary>
        private void FlushNode(Job job)
        {
            if (job.PendingNodeUnits.Count == 0 && job.PendingNodeCredits == 0) return;
            if (job.Venue.Node != null && _store != null)
                _store.HandleDockSupplySettled(job.Venue.Node, job.Venue.PayerIdentity, "restore",
                    new Dictionary<string, long>(job.PendingNodeUnits), job.PendingNodeCredits);
            job.PendingNodeUnits.Clear();
            job.PendingNodeCredits = 0;
        }

        // ---- supply ------------------------------------------------------------------------------

        private RestorationSupply BuildSupply(MyCubeGrid grid, Venue venue, IEnumerable<string> components)
        {
            var bank = new Dictionary<string, long>(StringComparer.Ordinal);
            FillBankPrices(bank, components);
            return BuildSupply(grid, venue, components, bank);
        }

        private RestorationSupply BuildSupply(MyCubeGrid grid, Venue venue, IEnumerable<string> components,
            Dictionary<string, long> bankPrices)
        {
            var supply = new RestorationSupply();
            var wanted = new HashSet<string>(components, StringComparer.Ordinal);
            foreach (MyInventory inventory in CargoOf(grid, venue.PayerIdentity))
            {
                foreach (MyPhysicalInventoryItem item in inventory.GetItems())
                {
                    if (item.Content == null || !(item.Content is MyObjectBuilder_Component)) continue;
                    string sub = item.Content.SubtypeName;
                    if (!wanted.Contains(sub)) continue;
                    long have;
                    supply.Ship.TryGetValue(sub, out have);
                    supply.Ship[sub] = have + (long)Math.Floor((double)item.Amount);
                }
            }

            if (venue.Node != null && _store != null)
            {
                foreach (KeyValuePair<string, StationListing> kv in _store.OffersFor(venue.Node))
                {
                    if (!kv.Key.StartsWith(ComponentPrefix, StringComparison.Ordinal)) continue;
                    string sub = kv.Key.Substring(ComponentPrefix.Length);
                    if (!wanted.Contains(sub)) continue;
                    supply.Node[sub] = new RestorationOffer
                    {
                        Available = DockSupply.OfferUnits(kv.Key, kv.Value.Quantity),
                        UnitPrice = DockSupply.ApplyMultiplier(kv.Value.UnitPrice, venue.OfferMultiplier),
                    };
                }
            }

            foreach (string sub in wanted)
            {
                long price;
                if (bankPrices != null && bankPrices.TryGetValue(sub, out price) && price > 0) supply.BankPrice[sub] = price;
            }
            return supply;
        }

        /// <summary>The bank's price per component: its value at the world's prices, times the
        /// premium. A component the price catalogue does not know has no bank price.</summary>
        private void FillBankPrices(Dictionary<string, long> into, IEnumerable<string> components)
        {
            foreach (string sub in components)
            {
                if (into.ContainsKey(sub)) continue;
                long value = _itemValue(ItemPriceResolver.StripInventoryPrefix(ComponentPrefix + sub));
                if (value > 0) into[sub] = RestorationBilling.BankUnitPrice(value);
            }
        }

        /// <summary>Every inventory aboard the ship — the mechanical group's, in any block the payer
        /// may open: cargo containers, connectors, seats, welders, assembler outputs. Only components
        /// are ever drawn, so a reactor's fuel or a turret's ammunition is read and left alone.</summary>
        private static List<MyInventory> CargoOf(MyCubeGrid grid, long identityId)
        {
            var list = new List<MyInventory>();
            foreach (MyCubeGrid g in MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid))
            {
                foreach (MyCubeBlock block in g.GetFatBlocks())
                {
                    var terminal = block as MyTerminalBlock;
                    if (terminal == null || !terminal.HasInventory || terminal.Closed || !terminal.IsFunctional
                        || !terminal.HasPlayerAccess(identityId)) continue;
                    for (int i = 0; i < terminal.InventoryCount; i++)
                    {
                        MyInventory inventory = terminal.GetInventory(i);
                        if (inventory != null) list.Add(inventory);
                    }
                }
            }
            return list;
        }

        // ---- the game side of a step -------------------------------------------------------------

        private sealed class Applier : IRestorationApplier
        {
            private readonly RestorationService _owner;
            private readonly Job _job;
            private readonly MyCubeGrid _grid;
            /// <summary>The grid the current step builds on; the ship's own grid or a subgrid.</summary>
            private MyCubeGrid _on;

            public Applier(RestorationService owner, Job job, MyCubeGrid grid)
            {
                _owner = owner;
                _job = job;
                _grid = grid;
            }

            public long Balance { get { return ServicesTerminalHelpers.BalanceOf(_job.Venue.PayerIdentity); } }

            public bool Apply(RestorationStep step)
            {
                _refusal = null;
                _on = null;
                bool done;
                RestorationPlacement at = step.Placement;
                Slot slot = step.Grid >= 0 && step.Grid < _job.Slots.Count ? _job.Slots[step.Grid] : null;
                if (slot == null) { _refusal = "no-grid"; done = false; }
                else if (step.Kind == RestorationStepKind.AttachTop) done = AttachTop(slot);
                else if (slot.Live == null || slot.Live.Closed || slot.Live.MarkedForClose)
                {
                    _refusal = slot.Live == null ? "top-not-attached" : "grid-gone";
                    done = false;
                }
                else
                {
                    _on = slot.Live;
                    if (slot.Plan.Attach)
                        at = RestorationDiff.Place(_job.Target.Design.Assembly.Grids[slot.Plan.DesignGrid][step.ReferenceIndex], slot.DesignToLive);
                    done = step.Kind == RestorationStepKind.Weld ? Weld(step, at) : Place(step, slot, at);
                }
                if (!done && DebugTraces.Restoration)
                {
                    if (step.Kind == RestorationStepKind.AttachTop)
                        Log.Info("RESTORE_TOP_REFUSED grid=" + _grid.EntityId + " on=" + (_on != null ? _on.EntityId : 0L)
                               + " designGrid=" + (slot != null ? slot.Plan.DesignGrid : -1)
                               + " top=" + ShortId(step.Placement.DefinitionId) + " reason=" + (_refusal ?? "unknown"));
                    else
                        Log.Info((step.Kind == RestorationStepKind.Weld ? "RESTORE_WELD_REFUSED" : "RESTORE_PLACE_REFUSED")
                               + " grid=" + _grid.EntityId + " on=" + (_on != null ? _on.EntityId : 0L)
                               + " designGrid=" + (slot != null ? slot.Plan.DesignGrid : -1)
                               + " block=" + ShortId(step.Placement.DefinitionId)
                               + " cell=" + at.Min + " reason=" + (_refusal ?? "unknown"));
                }
                return done;
            }

            /// <summary>Why the last step was refused, for the RESTORE_*_REFUSED lines.</summary>
            private string _refusal;

            private bool Weld(RestorationStep step, RestorationPlacement at)
            {
                GridCell m = at.Min;
                var min = new Vector3I(m.X, m.Y, m.Z);
                MySlimBlock slim = _on.GetCubeBlock(min);
                if (slim == null || slim.BlockDefinition == null || slim.Min != min
                    || !string.Equals(slim.BlockDefinition.Id.ToString(), at.DefinitionId, StringComparison.Ordinal))
                {
                    _refusal = "block-gone";
                    return false;
                }
                // Vanilla's own repair service, MyRepairServiceComponent.Repair: the skeleton reset
                // clears deformation, the integrity set is the replicated server path.
                if (slim.HasDeformation) _on.ResetBlockSkeleton(slim, true);
                slim.SetIntegrity_Server(slim.MaxIntegrity, slim.MaxIntegrity, MyIntegrityChangeEnum.Repair, _job.Venue.PayerIdentity);
                return true;
            }

            /// <summary>
            /// Puts a new top part on a base whose top is gone — SE's own path, the one a stator's
            /// "Add Top" button runs (<c>MyMechanicalConnectionBlockBase.CreateTopPartAndAttach</c>),
            /// fully built, with the top size that makes the design's top: a large rotor head on a
            /// large stator, or a small head on a large one. A base the restore has just placed already
            /// carries the top SE attaches to every newly built base; it is kept when it is the design's.
            /// Then the grid's frame is taken from the new top, so its blocks can be placed.
            /// </summary>
            private bool AttachTop(Slot slot)
            {
                RestorationDesign design = _job.Target.Design;
                RestorationJoint dj = design.Assembly.Joints[slot.Plan.Joint];
                Slot parent = _job.Slots[slot.Plan.Parent];
                if (parent.Live == null || parent.Live.Closed || parent.Live.MarkedForClose)
                {
                    _refusal = "base-grid-missing";
                    return false;
                }
                _on = parent.Live;
                RestorationBlock designBase = design.Assembly.Grids[dj.BaseGrid][dj.BaseBlock];
                RestorationBlock designTop = design.Assembly.Grids[dj.TopGrid][dj.TopBlock];
                RestorationPlacement bp = RestorationDiff.Place(designBase, parent.DesignToLive);
                var min = new Vector3I(bp.Min.X, bp.Min.Y, bp.Min.Z);
                MySlimBlock slim = parent.Live.GetCubeBlock(min);
                var mech = slim != null ? slim.FatBlock as MyMechanicalConnectionBlockBase : null;
                if (mech == null || slim.Min != min
                    || !string.Equals(slim.BlockDefinition.Id.ToString(), designBase.DefinitionId, StringComparison.Ordinal))
                {
                    _refusal = "base-missing";
                    return false;
                }

                long owner = _job.Venue.PayerIdentity;
                if (mech.TopBlock == null)
                {
                    MyMechanicalConnectionBlockBase.MyTopBlockSize size;
                    MyCubeBlockDefinition topDef;
                    if (!TopSizeFor(mech, designTop.DefinitionId, out size, out topDef))
                    {
                        _refusal = "top-type (" + ShortId(slim.BlockDefinition.Id.ToString()) + " makes no " + ShortId(designTop.DefinitionId) + ")";
                        return false;
                    }
                    if (CreateTopPartAndAttachMethod == null || CanPlaceTopMethod == null)
                    {
                        _refusal = "top-path-unresolved";
                        return false;
                    }
                    if (!MySession.Static.CheckLimitsAndNotify(owner, topDef.GetLimitType(), topDef.PCU, 1, parent.Live.BlocksCount))
                    {
                        _job.LimitHit = true;
                        _refusal = "block-limit";
                        return false;
                    }
                    if (!(bool)CanPlaceTopMethod.Invoke(mech, null))
                    {
                        _refusal = "top-obstructed";
                        return false;
                    }
                    using (RestorationPlacementScope.Enter(_job.GridIds))
                        CreateTopPartAndAttachMethod.Invoke(mech, new object[] { owner, size, true });
                }

                MyAttachableTopBlockBase top = mech.TopBlock;
                if (top == null || top.CubeGrid == null || top.SlimBlock == null)
                {
                    _refusal = "top-not-created";
                    return false;
                }
                if (!string.Equals(top.BlockDefinition.Id.ToString(), designTop.DefinitionId, StringComparison.Ordinal))
                {
                    _refusal = "top-type (attached " + ShortId(top.BlockDefinition.Id.ToString()) + ")";
                    return false;
                }
                slot.Live = top.CubeGrid;
                slot.DesignToLive = RestorationDiff.AlignOn(designTop, RestorationGrids.FromSlim(top.SlimBlock));
                _job.GridIds.Add(top.CubeGrid.EntityId);
                if (DebugTraces.Restoration)
                    Log.Info("RESTORE_TOP_ATTACHED grid=" + _grid.EntityId + " designGrid=" + slot.Plan.DesignGrid
                           + " base=" + parent.Live.EntityId + "@" + bp.Min + " top=" + ShortId(designTop.DefinitionId)
                           + " on=" + top.CubeGrid.EntityId);
                return true;
            }

            private bool Place(RestorationStep step, Slot slot, RestorationPlacement p)
            {
                List<MyObjectBuilder_CubeBlock> sources = _job.Target.Design.Sources[slot.Plan.DesignGrid];
                if (sources == null || step.ReferenceIndex < 0 || step.ReferenceIndex >= sources.Count)
                {
                    _refusal = "no-source-block";
                    return false;
                }
                MyObjectBuilder_CubeBlock source = sources[step.ReferenceIndex];
                MyCubeBlockDefinition def;
                if (source == null || !MyDefinitionManager.Static.TryGetCubeBlockDefinition(source.GetId(), out def) || def == null)
                {
                    _refusal = "unknown-definition";
                    return false;
                }

                var min = new Vector3I(p.Min.X, p.Min.Y, p.Min.Z);
                var max = new Vector3I(p.Max.X, p.Max.Y, p.Max.Z);
                if (_on.GetCubeBlock(min) != null)
                {
                    _refusal = "cell-occupied";
                    return false;
                }

                long owner = _job.Venue.PayerIdentity;
                // No PCU or block-limit check on this path in SE; the one the welder applies.
                if (!MySession.Static.CheckLimitsAndNotify(owner, def.GetLimitType(), def.PCU, 1, _on.BlocksCount))
                {
                    _job.LimitHit = true;
                    _refusal = "block-limit";
                    return false;
                }

                var orientation = new MyBlockOrientation((Base6Directions.Direction)(byte)p.Forward, (Base6Directions.Direction)(byte)p.Up);
                var ob = (MyObjectBuilder_CubeBlock)source.Clone();
                PrepareBuilder(ob, owner, _job.Target.IsDesign);
                ob.Min = min;
                ob.BlockOrientation = orientation;

                Quaternion q;
                orientation.GetQuaternion(out q);
                Vector3I center = MySlimBlock.ComputePositionInGrid(new MatrixI(orientation), def, min);
                var location = new MyCubeGrid.MyBlockLocation(def.Id, min, max, center, q, ob.EntityId, owner);
                var visuals = new MyCubeGrid.MyBlockVisuals(((Vector3)ob.ColorMaskHSV).PackHSVToUint(),
                    MyStringHash.GetOrCompute(ob.SkinSubtypeId ?? ""));

                // HasDLC refuses Sync.MyId on a dedicated server, which would drop a DLC block without
                // a word. The ship already carried the block, so the check is lifted for this call.
                bool dlc = def.DLCs != null && def.DLCs.Length > 0;
                bool ownAll = MyFakes.OWN_ALL_DLCS;
                if (dlc) MyFakes.OWN_ALL_DLCS = true;
                try
                {
                    // A Factions station's safe zone allows only LandingGearLock, and the build request
                    // asks it for Building on the grid and on the block's box. The scope answers yes to
                    // those two questions for the ship's grids, on this thread, for this call only
                    // (RestorationSafeZonePatch); the zone itself is not changed.
                    using (RestorationPlacementScope.Enter(_job.GridIds))
                    {
                        _on.BuildBlockRequestInternal(visuals, location, ob, 0, true, owner, Sync.MyId, false);
                        MySlimBlock placed = _on.GetCubeBlock(min);
                        if (placed == null || placed.BlockDefinition == null || placed.BlockDefinition.Id != def.Id || placed.Min != min)
                        {
                            _refusal = Diagnose(def, min, max, orientation, q, center);
                            return false;
                        }
                        // SE forces share mode to Faction on this path; a design's own setting is put back.
                        if (_job.Target.IsDesign && placed.FatBlock != null && source.ShareMode != MyOwnershipShareModeEnum.Faction)
                            placed.FatBlock.ChangeBlockOwnerRequest(owner, source.ShareMode);
                        return true;
                    }
                }
                finally
                {
                    if (dlc) MyFakes.OWN_ALL_DLCS = ownAll;
                }
            }

            /// <summary>Which of the build request's own checks refused a block, asked again in its
            /// order (MyCubeGrid.BuildBlockRequestInternal). Runs inside the placement scope, so a
            /// safe-zone answer here is the scope's.</summary>
            private string Diagnose(MyCubeBlockDefinition def, Vector3I min, Vector3I max, MyBlockOrientation orientation,
                Quaternion q, Vector3I center)
            {
                try
                {
                    if (!Sandbox.Game.Entities.MySessionComponentSafeZones.IsActionAllowed(_on, MySafeZoneAction.Building, 0L, 0uL))
                        return "safezone-building";
                    var dlcComponent = MySession.Static.GetComponent<MySessionComponentDLC>();
                    if (dlcComponent != null && !dlcComponent.HasDefinitionDLC(def.Id, Sync.MyId)) return "dlc";
                    if (!_on.CanPlaceBlock(min, max, orientation, def)) return "placement (cells, voxel, safe-zone area or physics)";
                    MyCubeBlockDefinition.MountPoint[] mounts = def.GetBuildProgressModelMountPoints(MyComponentStack.NewBlockIntegrity);
                    Vector3I position = center;
                    if (!MyCubeGrid.CheckConnectivity(_on, def, mounts, ref q, ref position)) return "connectivity (no mounted neighbour)";
                    return "build-request-returned-no-block";
                }
                catch (Exception ex)
                {
                    return "diagnosis-failed:" + ex.GetType().Name;
                }
            }

            private static string ShortId(string definitionId)
            {
                if (string.IsNullOrEmpty(definitionId)) return "?";
                return definitionId.StartsWith("MyObjectBuilder_", StringComparison.Ordinal)
                    ? definitionId.Substring("MyObjectBuilder_".Length) : definitionId;
            }

            public bool Settle(RestorationStep step, RestorationBill bill)
            {
                long payer = _job.Venue.PayerIdentity;
                if (bill.Credits > 0 && !MyBankingSystem.ChangeBalance(payer, -bill.Credits)) return false;

                List<MyInventory> cargo = null;
                foreach (RestorationBillLine l in bill.Lines)
                {
                    if (l.FromShip > 0)
                    {
                        if (cargo == null) cargo = CargoOf(_grid, payer);
                        TakeFromCargo(cargo, l.Component, l.FromShip);
                    }
                    if (l.FromNode > 0)
                    {
                        string key = ComponentPrefix + l.Component;
                        long have;
                        _job.PendingNodeUnits.TryGetValue(key, out have);
                        _job.PendingNodeUnits[key] = have + l.FromNode;
                    }
                }
                _job.PendingNodeCredits += bill.NodeCredits;
                return true;
            }
        }

        /// <summary>The top size whose part, on <paramref name="mech"/>, is the design's top
        /// <paramref name="designTopId"/>: the base definition's top part at its own grid size, its
        /// small-grid part, or (a large base) its medium part — as <c>CreateTopPartAndAttach</c>
        /// chooses it. False when none is.</summary>
        private static bool TopSizeFor(MyMechanicalConnectionBlockBase mech, string designTopId,
            out MyMechanicalConnectionBlockBase.MyTopBlockSize size, out MyCubeBlockDefinition topDef)
        {
            size = MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal;
            topDef = null;
            var def = ((MyCubeBlock)mech).BlockDefinition as MyMechanicalConnectionBlockBaseDefinition;
            if (def == null || MyDefinitionManager.Static == null) return false;
            MyCubeSize gridSize = mech.CubeGrid.GridSizeEnum;
            var sizes = new[]
            {
                MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal,
                MyMechanicalConnectionBlockBase.MyTopBlockSize.Small,
                MyMechanicalConnectionBlockBase.MyTopBlockSize.Medium,
            };
            foreach (MyMechanicalConnectionBlockBase.MyTopBlockSize s in sizes)
            {
                bool medium = s == MyMechanicalConnectionBlockBase.MyTopBlockSize.Medium && gridSize == MyCubeSize.Large;
                MyCubeBlockDefinitionGroup group = MyDefinitionManager.Static.TryGetDefinitionGroup(medium ? def.MediumTopPart : def.TopPart);
                if (group == null) continue;
                MyCubeSize partSize = s != MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal && gridSize == MyCubeSize.Large
                    ? MyCubeSize.Small : gridSize;
                MyCubeBlockDefinition d = group[partSize];
                if (d == null || !string.Equals(d.Id.ToString(), designTopId, StringComparison.Ordinal)) continue;
                size = s;
                topDef = d;
                return true;
            }
            return false;
        }

        private static void TakeFromCargo(List<MyInventory> cargo, string subtype, long units)
        {
            var id = new MyDefinitionId(typeof(MyObjectBuilder_Component), subtype);
            long left = units;
            foreach (MyInventory inventory in cargo)
            {
                if (left <= 0) break;
                long have = (long)Math.Floor((double)inventory.GetItemAmount(id));
                long take = Math.Min(have, left);
                if (take <= 0) continue;
                inventory.RemoveItemsOfType((MyFixedPoint)(double)take, id);
                left -= take;
            }
            if (left > 0) Log.Warn("RESTORE cargo short: " + left + " " + subtype + " priced from cargo were not there to take.");
        }

        /// <summary>A block goes back empty and owned by the payer: no cargo, ammunition, gas or
        /// charge carried over from the snapshot, whole, and — from a prefab rather than the owner's
        /// own design — without the prefab's script.</summary>
        private static void PrepareBuilder(MyObjectBuilder_CubeBlock ob, long owner, bool isDesign)
        {
            ob.EntityId = ob.GetType() == typeof(MyObjectBuilder_CubeBlock) ? 0 : MyEntityIdentifier.AllocateId();
            ob.Owner = owner;
            ob.BuiltBy = owner;
            ob.IntegrityPercent = 1f;
            ob.BuildPercent = 1f;
            ob.DeformationRatio = 0f;
            ob.ConstructionInventory = null;
            ob.ConstructionStockpile = null;
            if (!isDesign) ob.ShareMode = MyOwnershipShareModeEnum.Faction;

            if (ob.ComponentContainer != null && ob.ComponentContainer.Components != null)
                foreach (MyObjectBuilder_ComponentContainer.ComponentData c in ob.ComponentContainer.Components)
                    if (c != null) EmptyInventory(c.Component as MyObjectBuilder_InventoryBase);

            var tank = ob as MyObjectBuilder_GasTank;
            if (tank != null) tank.FilledRatio = 0f;
            var battery = ob as MyObjectBuilder_BatteryBlock;
            if (battery != null) battery.CurrentStoredPower = 0f;
            var pb = ob as MyObjectBuilder_MyProgrammableBlock;
            if (pb != null && !isDesign) pb.Program = null;

            // A joint's other end is named by the design's entity ids: a placed base or top joins
            // nothing until the restore attaches its top.
            var mc = ob as MyObjectBuilder_MechanicalConnectionBlock;
            if (mc != null) mc.TopBlockId = null;
            var motor = ob as MyObjectBuilder_MotorBase;
            if (motor != null)
            {
                motor.RotorEntityId = null;
                motor.WeldedEntityId = null;
            }
            var top = ob as MyObjectBuilder_AttachableTopBlockBase;
            if (top != null) top.ParentEntityId = 0;
        }

        private static void EmptyInventory(MyObjectBuilder_InventoryBase inventory)
        {
            var single = inventory as MyObjectBuilder_Inventory;
            if (single != null && single.Items != null) single.Items.Clear();
            var aggregate = inventory as MyObjectBuilder_InventoryAggregate;
            if (aggregate != null && aggregate.Inventories != null)
                foreach (MyObjectBuilder_InventoryBase inner in aggregate.Inventories) EmptyInventory(inner);
        }

        // ---- damage ------------------------------------------------------------------------------

        private void HookDamage()
        {
            if (_damageHooked) return;
            Sandbox.Game.GameSystems.MyDamageSystem ds = Sandbox.Game.GameSystems.MyDamageSystem.Static;
            if (ds == null) return;
            ds.RegisterAfterDamageHandler(1000, OnAfterDamage);
            _damageHooked = true;
        }

        private void OnAfterDamage(object target, MyDamageInformation info)
        {
            if (_jobs.Count == 0 || info.Amount <= 0) return;
            var block = target as MySlimBlock;
            if (block == null || block.CubeGrid == null) return;
            Job job = JobCovering(block.CubeGrid.EntityId);
            if (job != null) _lastHit[job.GridId] = DateTime.UtcNow;
        }

        // ---- bench -------------------------------------------------------------------------------

        /// <summary><c>factions bench restore &lt;gridEntityId|looking&gt; [confirm]</c>: the terminal's
        /// quote, and with <c>confirm</c> the restore itself, without the client. The terminal is the
        /// nearest Services Terminal on a Factions station within <see cref="RangeM"/> of the ship; with
        /// none, the ship's position is the anchor and nothing is bought from a node. The payer is the
        /// looking player, else the ship's owner. The customer gates (use rule, trade bar) are skipped.</summary>
        internal string BenchRestore(string target, bool confirm)
        {
            MyCubeGrid grid;
            long identityId;
            string error;
            if (!Refs.TryFindRestoreTarget(target, out grid, out identityId, out error)) return "restore: " + error;
            if (grid.IsStatic) return "restore: grid " + grid.EntityId + " is a station.";
            if (identityId == 0 && grid.BigOwners.Count > 0) identityId = grid.BigOwners[0];
            if (identityId == 0) return "restore: grid " + grid.EntityId + " has no owner to pay.";

            Job running = JobCovering(grid.EntityId);
            if (running != null)
            {
                if (!confirm) return "restore: " + StatusText(running);
                running.Core.Stop("stopped from the bench.");
                Finish(running, "stopped");
                return "restore: stopped. " + Summary(running);
            }

            Venue venue = BenchVenue(grid, identityId);
            RestorationLiveGroup live = RestorationGrids.LiveGroup(grid);
            RestorationTarget t;
            bool unmarked;
            if (!TryTarget(grid, live.Root, out t, out unmarked, out error))
            {
                if (!unmarked) return "restore: grid " + grid.EntityId + ": " + error;
                string refusal;
                long gridId = grid.EntityId;
                if (!Refs.StartRestoreMatchFor(grid, (matched, why) => OnBenchMatched(gridId, matched, why), out refusal))
                    return "restore: " + refusal;
                return "restore: grid " + grid.EntityId + ": nothing marks its design; matching. The result follows in the bridge "
                     + "outbox; run the command again (with confirm) once it reports.";
            }

            RestorationGroupPlan plan = PlanFor(live, t);
            RestorationSequence seq = plan.Sequence;
            RestorationSupply supply = BuildSupply(grid, venue, seq.Need().Keys);
            RestorationBill bill = RestorationBilling.Price(seq.Need(), supply);
            int seconds = RestorationJob.EstimatedSeconds(seq.Steps.Count);
            _targets[grid.EntityId] = new CachedTarget { Target = t, Utc = DateTime.UtcNow };
            LogBill(grid, identityId, venue, t, plan, bill, seconds);

            string head = "restore: grid " + grid.EntityId + " terminal=" + venue.TerminalId
                        + " node=" + (venue.Node != null ? venue.Node.Id : "none") + " payer=" + identityId + " | ";
            string text = head + BillText(grid, t, plan, bill, seconds).Replace("\r", "").Replace("\n", " | ");
            if (!confirm || seq.Steps.Count == 0) return text;
            if (bill.FirstShort != null) return text + " | not started.";
            string message;
            if (!StartJob(venue, grid, live, t, bill.Credits, out message)) return text + " | " + message;
            return text + " | " + message;
        }

        private void OnBenchMatched(long gridId, RestorationTarget target, string why)
        {
            FactionsPlugin plugin = FactionsPlugin.Instance;
            if (target != null) _targets[gridId] = new CachedTarget { Target = target, Utc = DateTime.UtcNow };
            if (plugin != null)
                plugin.AppendOutbox("OK: restore " + gridId + " (match) => "
                    + (target != null ? "matched " + target.Source + "; run 'factions bench restore " + gridId + "' for the bill."
                                      : "unmatched: " + why));
        }

        private Venue BenchVenue(MyCubeGrid grid, long identityId)
        {
            var venue = new Venue
            {
                PayerIdentity = identityId,
                PayerSteam = MySession.Static.Players.TryGetSteamId(identityId),
                Anchor = grid.PositionComp.WorldAABB.Center,
            };
            MyCubeBlock best = null;
            SpatialNode bestNode = null;
            double bestDistance = double.MaxValue;
            var sphere = new BoundingSphereD(grid.PositionComp.WorldAABB.Center, RangeM + grid.PositionComp.WorldAABB.HalfExtents.Length());
            foreach (MyEntity e in MyEntities.GetTopMostEntitiesInSphere(ref sphere))
            {
                var g = e as MyCubeGrid;
                if (g == null || g == grid || g.Closed) continue;
                IMyFactionStation station = MySession.Static.Factions.GetStationByGridId(g.EntityId);
                if (station == null) continue;
                SpatialNode node = TerminalAccess.FindNodeByStation(GraphHere, station.Id);
                if (node == null || string.IsNullOrEmpty(node.OwnerFactionTag)) continue;
                foreach (MyCubeBlock block in g.GetFatBlocks())
                {
                    if (block == null || block.BlockDefinition == null
                        || !string.Equals(block.BlockDefinition.Id.SubtypeName, "ServicesTerminal", StringComparison.OrdinalIgnoreCase)) continue;
                    double d = Vector3D.Distance(block.PositionComp.GetPosition(), grid.PositionComp.WorldAABB.Center);
                    if (d < bestDistance) { bestDistance = d; best = block; bestNode = node; }
                }
            }
            if (best != null)
            {
                venue.Terminal = best;
                venue.TerminalId = best.EntityId;
                venue.Node = bestNode;
                venue.Anchor = best.PositionComp.GetPosition();
            }
            return venue;
        }

        // ---- text --------------------------------------------------------------------------------

        private static WireRestoreQuote Refusal(long terminalEntityId, long gridEntityId, string message)
        {
            return new WireRestoreQuote
            {
                TerminalEntityId = terminalEntityId,
                GridEntityId = gridEntityId,
                Offer = WireRestoreOffer.None,
                Message = message,
            };
        }

        private static string Duration(int seconds)
        {
            if (seconds < 90) return seconds + " s";
            return ((seconds + 59) / 60) + " min";
        }

        private static string NameOf(MyCubeGrid grid)
        {
            return grid == null || string.IsNullOrEmpty(grid.DisplayName) ? "that ship" : grid.DisplayName;
        }

        private static string Num(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
