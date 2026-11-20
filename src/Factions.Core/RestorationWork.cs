using System;
using System.Collections.Generic;
using System.Globalization;

namespace Factions.Core
{
    /// <summary>What a step does. <see cref="AttachTop"/> puts a new top part (rotor head, hinge top,
    /// piston top, wheel) on a base whose top is gone, so the grid that hung on it can be built
    /// again.</summary>
    public enum RestorationStepKind : byte { Weld = 0, Place = 1, AttachTop = 2 }

    /// <summary>One unit of restoration work: weld a damaged block to full, place a missing one, or
    /// attach a missing top part. <see cref="Bill"/> is what the step costs in components (subtype to
    /// count): the stack's shortfall for a weld, the definition's full bill for a placement or a top.
    /// <see cref="Grid"/> names the grid of the restored group the step works on
    /// (<see cref="RestorationGroupPlan.Grids"/>); 0 is the selected grid.</summary>
    public sealed class RestorationStep
    {
        public RestorationStepKind Kind;
        public int Grid;
        public int ReferenceIndex;
        /// <summary>The live block a weld acts on; -1 for a placement.</summary>
        public int CurrentIndex = -1;
        /// <summary>Where the block goes, in its grid's frame: live coordinates for a grid that
        /// exists, design coordinates for one built on a top the restore attaches.</summary>
        public RestorationPlacement Placement;
        public Dictionary<string, int> Bill = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>The steps of a restoration in the order they run.</summary>
    public sealed class RestorationSequence
    {
        public readonly List<RestorationStep> Steps = new List<RestorationStep>();
        public int Welds;
        public int Places;
        /// <summary>Top parts attached to bases whose top is gone.</summary>
        public int Attaches;
        /// <summary>Missing blocks no chain of placements reaches from a surviving block. SE refuses
        /// a block with no mounted neighbour, so they are left out.</summary>
        public int Unreachable;
        /// <summary>Design blocks whose cells another block holds; never placed.</summary>
        public int Blocked;

        /// <summary>Components every step together costs.</summary>
        public Dictionary<string, long> Need()
        {
            var need = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (RestorationStep s in Steps)
                foreach (KeyValuePair<string, int> kv in s.Bill)
                {
                    if (kv.Value <= 0) continue;
                    long have;
                    need.TryGetValue(kv.Key, out have);
                    need[kv.Key] = have + kv.Value;
                }
            return need;
        }
    }

    /// <summary>
    /// The order restoration work runs in: every damaged block is welded first, then missing blocks
    /// are placed outward from the blocks that survive. SE places a block only against a mounted
    /// neighbour, so a placement is ordered after whatever it touches: a breadth-first walk from the
    /// surviving cells, the first ring in design order, each later ring in the order the walk meets
    /// it. Blocked design blocks are not placed. Pure and deterministic.
    /// </summary>
    public static class RestorationOrder
    {
        private static readonly GridCell[] Faces =
        {
            new GridCell(1, 0, 0), new GridCell(-1, 0, 0),
            new GridCell(0, 1, 0), new GridCell(0, -1, 0),
            new GridCell(0, 0, 1), new GridCell(0, 0, -1),
        };

        public static RestorationSequence Build(RestorationPlan plan, IReadOnlyList<RestorationBlock> current)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            var seq = new RestorationSequence { Blocked = plan.Blocked.Count };

            foreach (RestorationEntry e in plan.Damaged)
            {
                seq.Steps.Add(new RestorationStep
                {
                    Kind = RestorationStepKind.Weld,
                    ReferenceIndex = e.ReferenceIndex,
                    CurrentIndex = e.CurrentIndex,
                    Placement = e.Placement,
                    Bill = new Dictionary<string, int>(e.Bill, StringComparer.Ordinal),
                });
                seq.Welds++;
            }

            var occupied = new HashSet<GridCell>();
            if (current != null)
                foreach (RestorationBlock b in current)
                    if (b != null) AddCells(occupied, b.Min, b.Max);

            List<RestorationEntry> missing = plan.Missing;
            var missingAt = new Dictionary<GridCell, int>();
            for (int i = 0; i < missing.Count; i++)
            {
                GridCell lo = missing[i].Placement.Min, hi = missing[i].Placement.Max;
                for (int x = lo.X; x <= hi.X; x++)
                    for (int y = lo.Y; y <= hi.Y; y++)
                        for (int z = lo.Z; z <= hi.Z; z++)
                        {
                            var c = new GridCell(x, y, z);
                            if (!missingAt.ContainsKey(c)) missingAt[c] = i;
                        }
            }

            var queued = new bool[missing.Count];
            var queue = new Queue<int>();
            for (int i = 0; i < missing.Count; i++)
            {
                if (!Touches(missing[i].Placement, occupied)) continue;
                queued[i] = true;
                queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                RestorationEntry e = missing[i];
                seq.Steps.Add(new RestorationStep
                {
                    Kind = RestorationStepKind.Place,
                    ReferenceIndex = e.ReferenceIndex,
                    Placement = e.Placement,
                    Bill = new Dictionary<string, int>(e.Bill, StringComparer.Ordinal),
                });
                seq.Places++;

                GridCell lo = e.Placement.Min, hi = e.Placement.Max;
                for (int x = lo.X; x <= hi.X; x++)
                    for (int y = lo.Y; y <= hi.Y; y++)
                        for (int z = lo.Z; z <= hi.Z; z++)
                            foreach (GridCell f in Faces)
                            {
                                int j;
                                if (!missingAt.TryGetValue(new GridCell(x, y, z) + f, out j) || queued[j]) continue;
                                queued[j] = true;
                                queue.Enqueue(j);
                            }
            }

            for (int i = 0; i < queued.Length; i++) if (!queued[i]) seq.Unreachable++;
            return seq;
        }

        private static void AddCells(HashSet<GridCell> into, GridCell lo, GridCell hi)
        {
            for (int x = lo.X; x <= hi.X; x++)
                for (int y = lo.Y; y <= hi.Y; y++)
                    for (int z = lo.Z; z <= hi.Z; z++)
                        into.Add(new GridCell(x, y, z));
        }

        /// <summary>A cell face-adjacent to the footprint, outside it, is occupied.</summary>
        private static bool Touches(RestorationPlacement p, HashSet<GridCell> occupied)
        {
            for (int x = p.Min.X; x <= p.Max.X; x++)
                for (int y = p.Min.Y; y <= p.Max.Y; y++)
                    for (int z = p.Min.Z; z <= p.Max.Z; z++)
                        foreach (GridCell f in Faces)
                        {
                            GridCell n = new GridCell(x, y, z) + f;
                            if (n.X >= p.Min.X && n.X <= p.Max.X && n.Y >= p.Min.Y && n.Y <= p.Max.Y
                                && n.Z >= p.Min.Z && n.Z <= p.Max.Z) continue;
                            if (occupied.Contains(n)) return true;
                        }
            return false;
        }
    }

    /// <summary>What the station node sells of one component: its store row.</summary>
    public struct RestorationOffer
    {
        public long Available;
        public long UnitPrice;
    }

    /// <summary>
    /// Where a restoration's components come from, in the order they are drawn: the ship's own
    /// cargo, then the station node's store row at its price, then the central bank at
    /// <see cref="RestorationBilling.BankPremium"/> over the item's value. Keys are component
    /// subtypes. A component with no bank price cannot be bought from the bank.
    /// </summary>
    public sealed class RestorationSupply
    {
        public readonly Dictionary<string, long> Ship = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, RestorationOffer> Node = new Dictionary<string, RestorationOffer>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> BankPrice = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>One component of a bill, split by source.</summary>
    public sealed class RestorationBillLine
    {
        public string Component;
        public long Need;
        public long FromShip;
        public long FromNode;
        public long NodeCredits;
        public long FromBank;
        public long BankCredits;
        /// <summary>Units no source could supply.</summary>
        public long Short;

        public long Credits { get { return NodeCredits + BankCredits; } }
    }

    public sealed class RestorationBill
    {
        /// <summary>By component, ordinal.</summary>
        public readonly List<RestorationBillLine> Lines = new List<RestorationBillLine>();
        public long NodeCredits;
        public long BankCredits;
        public long Credits { get { return NodeCredits + BankCredits; } }
        public long FromShip;
        public long FromNode;
        public long FromBank;

        /// <summary>The first component (ordinal) no source can supply in full; null when none.</summary>
        public string FirstShort
        {
            get
            {
                foreach (RestorationBillLine l in Lines) if (l.Short > 0) return l.Component;
                return null;
            }
        }

        /// <summary>The line that costs the most credits; null for a bill that costs nothing.</summary>
        public RestorationBillLine Costliest
        {
            get
            {
                RestorationBillLine best = null;
                foreach (RestorationBillLine l in Lines)
                    if (l.Credits > 0 && (best == null || l.Credits > best.Credits)) best = l;
                return best;
            }
        }
    }

    /// <summary>
    /// Splits a component need across <see cref="RestorationSupply"/>: ship cargo first, then the
    /// node at its store price, then the bank at a premium. Pricing never mutates the supply;
    /// <see cref="Consume"/> does, once the work the bill paid for has been done.
    /// </summary>
    public static class RestorationBilling
    {
        /// <summary>The central bank sells a component at this multiple of the item's value, so a
        /// node short of stock makes a restore dearer, never free.</summary>
        public const double BankPremium = 1.5;

        /// <summary>Credits for one component bought from the bank, from its unit value: at least 1.</summary>
        public static long BankUnitPrice(long unitValue)
        {
            double p = Math.Ceiling(Math.Max(1, unitValue) * BankPremium);
            return p >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)p);
        }

        public static RestorationBill Price(IEnumerable<KeyValuePair<string, long>> need, RestorationSupply supply)
        {
            var sorted = new SortedDictionary<string, long>(StringComparer.Ordinal);
            if (need != null)
                foreach (KeyValuePair<string, long> kv in need)
                {
                    if (kv.Value <= 0 || string.IsNullOrEmpty(kv.Key)) continue;
                    long have;
                    sorted.TryGetValue(kv.Key, out have);
                    sorted[kv.Key] = have + kv.Value;
                }

            var bill = new RestorationBill();
            foreach (KeyValuePair<string, long> kv in sorted)
            {
                var line = new RestorationBillLine { Component = kv.Key, Need = kv.Value };
                long left = kv.Value;

                long ship = 0;
                if (supply != null) supply.Ship.TryGetValue(kv.Key, out ship);
                line.FromShip = Math.Min(left, Math.Max(0, ship));
                left -= line.FromShip;

                RestorationOffer offer;
                if (left > 0 && supply != null && supply.Node.TryGetValue(kv.Key, out offer) && offer.UnitPrice > 0)
                {
                    line.FromNode = Math.Min(left, Math.Max(0, offer.Available));
                    line.NodeCredits = Mul(line.FromNode, offer.UnitPrice);
                    left -= line.FromNode;
                }

                long bankPrice;
                if (left > 0 && supply != null && supply.BankPrice.TryGetValue(kv.Key, out bankPrice) && bankPrice > 0)
                {
                    line.FromBank = left;
                    line.BankCredits = Mul(left, bankPrice);
                    left = 0;
                }

                line.Short = left;
                bill.Lines.Add(line);
                bill.NodeCredits += line.NodeCredits;
                bill.BankCredits += line.BankCredits;
                bill.FromShip += line.FromShip;
                bill.FromNode += line.FromNode;
                bill.FromBank += line.FromBank;
            }
            return bill;
        }

        public static RestorationBill Price(IEnumerable<KeyValuePair<string, int>> need, RestorationSupply supply)
        {
            var wide = new List<KeyValuePair<string, long>>();
            if (need != null)
                foreach (KeyValuePair<string, int> kv in need) wide.Add(new KeyValuePair<string, long>(kv.Key, kv.Value));
            return Price(wide, supply);
        }

        /// <summary>Takes what <paramref name="bill"/> drew out of the ship's cargo and the node's
        /// row, so the next step prices against what is left.</summary>
        public static void Consume(RestorationBill bill, RestorationSupply supply)
        {
            if (bill == null || supply == null) return;
            foreach (RestorationBillLine l in bill.Lines)
            {
                long ship;
                if (l.FromShip > 0 && supply.Ship.TryGetValue(l.Component, out ship))
                    supply.Ship[l.Component] = Math.Max(0, ship - l.FromShip);
                RestorationOffer offer;
                if (l.FromNode > 0 && supply.Node.TryGetValue(l.Component, out offer))
                {
                    offer.Available = Math.Max(0, offer.Available - l.FromNode);
                    supply.Node[l.Component] = offer;
                }
            }
        }

        private static long Mul(long units, long price)
        {
            if (units <= 0 || price <= 0) return 0;
            if (units > long.MaxValue / price) return long.MaxValue;
            return units * price;
        }
    }

    public enum RestorationJobStatus : byte { Running = 0, Paused = 1, Done = 2, Stopped = 3 }

    /// <summary>What the game does with one step. The job decides whether a step is paid for and
    /// affordable; the applier does it and takes the payment.</summary>
    public interface IRestorationApplier
    {
        /// <summary>The payer's credits now.</summary>
        long Balance { get; }

        /// <summary>Welds or places the step's block. False when the game refused it: nothing is
        /// charged for it, and it counts as skipped.</summary>
        bool Apply(RestorationStep step);

        /// <summary>Pays for a step that was just applied: the ship-cargo units are taken, the node
        /// and bank units charged to the payer. False when the charge was refused.</summary>
        bool Settle(RestorationStep step, RestorationBill bill);
    }

    /// <summary>
    /// A timed restoration: <see cref="BlocksPerInterval"/> steps every
    /// <see cref="IntervalSeconds"/>, while the ship stays within range of the terminal and has not
    /// been hit for <see cref="DamageQuietSeconds"/>. Each step is priced against the supply as it
    /// stands, applied, and only then paid for — a block the game refuses costs nothing. A step no
    /// source can supply, or the payer cannot afford, or that would take the total past the
    /// confirmed budget, stops the job with a reason naming the component.
    ///
    /// <para><b>Passes.</b> The steps run in order once; a placement or top the game refused is
    /// queued and tried again after the pass, because the neighbour it mounts on may have been placed
    /// later in the same pass. Passes repeat over what is still refused until one builds nothing;
    /// what is left then counts as skipped. A refused weld is not retried: its block is gone.</para>
    /// </summary>
    public sealed class RestorationJob
    {
        public const double IntervalSeconds = 2.0;
        public const int BlocksPerInterval = 4;
        public const double DamageQuietSeconds = 15.0;

        /// <summary>Every step, in first-pass order.</summary>
        public readonly List<RestorationStep> Steps;
        public readonly long Budget;
        /// <summary>Position in the current pass.</summary>
        public int Cursor;
        public RestorationJobStatus Status = RestorationJobStatus.Running;
        /// <summary>"range" or "damage" while paused.</summary>
        public string PauseReason;
        /// <summary>Why a stopped job stopped, in the player's terms.</summary>
        public string StopReason;
        public DateTime NextDueUtc;

        public int Placed;
        public int Welded;
        public int Attached;
        public int Skipped;
        /// <summary>Passes started: 1, plus one for each retry of refused steps.</summary>
        public int Passes = 1;
        /// <summary>Steps a later pass built after an earlier one refused them.</summary>
        public int Retried;
        public long Paid;
        public long PaidNode;
        public long PaidBank;
        public long FromShipUnits;
        public long FromNodeUnits;
        public long FromBankUnits;

        private List<RestorationStep> _pass;
        private List<RestorationStep> _refused = new List<RestorationStep>();
        private readonly HashSet<RestorationStep> _everRefused = new HashSet<RestorationStep>();
        private int _builtThisPass;

        public RestorationJob(List<RestorationStep> steps, long budget, DateTime startUtc)
        {
            Steps = steps ?? new List<RestorationStep>();
            _pass = new List<RestorationStep>(Steps);
            Budget = budget;
            NextDueUtc = startUtc;
            if (Steps.Count == 0) Status = RestorationJobStatus.Done;
        }

        /// <summary>Steps not yet built or given up: the rest of this pass and the refused ones
        /// waiting for the next.</summary>
        public int Remaining { get { return Math.Max(0, _pass.Count - Cursor) + _refused.Count; } }
        public bool Finished { get { return Status == RestorationJobStatus.Done || Status == RestorationJobStatus.Stopped; } }

        /// <summary>The <paramref name="k"/>-th step still to run in this pass (0 is the next); null
        /// past the pass's end.</summary>
        public RestorationStep Upcoming(int k)
        {
            int i = Cursor + k;
            return k >= 0 && i < _pass.Count ? _pass[i] : null;
        }

        /// <summary>Seconds <paramref name="steps"/> steps take at the job's pace, uninterrupted.</summary>
        public static int EstimatedSeconds(int steps)
        {
            if (steps <= 0) return 0;
            int intervals = (steps + BlocksPerInterval - 1) / BlocksPerInterval;
            return (int)Math.Ceiling(intervals * IntervalSeconds);
        }

        public void Stop(string reason)
        {
            if (Finished) return;
            Status = RestorationJobStatus.Stopped;
            StopReason = reason;
            PauseReason = null;
        }

        /// <summary>
        /// Runs the steps due at <paramref name="nowUtc"/>. Pauses (without stepping) while
        /// <paramref name="inRange"/> is false or the ship was hit within
        /// <see cref="DamageQuietSeconds"/> of now, and resumes by itself when both clear.
        /// <paramref name="supply"/> is read and drawn down; the host rebuilds it from the world
        /// before each call. Returns the steps attempted.
        /// </summary>
        public int Pump(DateTime nowUtc, bool inRange, DateTime lastDamageUtc, RestorationSupply supply, IRestorationApplier applier)
        {
            if (Finished) return 0;
            if (!inRange) { Pause("range"); return 0; }
            if (lastDamageUtc != DateTime.MinValue && (nowUtc - lastDamageUtc).TotalSeconds < DamageQuietSeconds)
            {
                Pause("damage");
                return 0;
            }
            if (Status == RestorationJobStatus.Paused)
            {
                Status = RestorationJobStatus.Running;
                PauseReason = null;
                NextDueUtc = nowUtc;
            }
            if (nowUtc < NextDueUtc) return 0;
            NextDueUtc = nowUtc.AddSeconds(IntervalSeconds);

            int attempted = 0;
            while (attempted < BlocksPerInterval && Cursor < _pass.Count)
            {
                RestorationStep step = _pass[Cursor];
                RestorationBill bill = RestorationBilling.Price(step.Bill, supply);

                string shortOf = bill.FirstShort;
                if (shortOf != null)
                {
                    Stop(shortOf + " is not in the ship's cargo, not sold at this station, and not sold by the bank.");
                    break;
                }
                if (bill.Credits > 0)
                {
                    if (Paid + bill.Credits > Budget)
                    {
                        Stop("the next block would take the total past the " + Num(Budget) + " credits you confirmed ("
                             + Describe(bill.Costliest) + "). Ask for a new bill.");
                        break;
                    }
                    long balance = applier.Balance;
                    if (bill.Credits > balance)
                    {
                        Stop("you cannot afford the next block: " + Describe(bill.Costliest) + ", "
                             + Num(bill.Credits) + " credits against a balance of " + Num(balance) + ".");
                        break;
                    }
                }

                Cursor++;
                attempted++;
                if (!applier.Apply(step))
                {
                    if (step.Kind == RestorationStepKind.Weld) Skipped++;
                    else
                    {
                        _refused.Add(step);
                        _everRefused.Add(step);
                    }
                    continue;
                }
                if (step.Kind != RestorationStepKind.Weld)
                {
                    _builtThisPass++;
                    if (_everRefused.Contains(step)) Retried++;
                }
                if (!applier.Settle(step, bill))
                {
                    Count(step);
                    Stop("your account refused the charge of " + Num(bill.Credits) + " credits for "
                         + Describe(bill.Costliest) + ".");
                    break;
                }
                Count(step);
                RestorationBilling.Consume(bill, supply);
                Paid += bill.Credits;
                PaidNode += bill.NodeCredits;
                PaidBank += bill.BankCredits;
                FromShipUnits += bill.FromShip;
                FromNodeUnits += bill.FromNode;
                FromBankUnits += bill.FromBank;
            }

            if (!Finished && Cursor >= _pass.Count && !NextPass()) Status = RestorationJobStatus.Done;
            return attempted;
        }

        /// <summary>At the end of a pass: starts the next over the refused steps when this pass built
        /// something, else gives them up as skipped. True when a new pass has steps to run. Runs after
        /// an interval's steps, never inside one, so the host prices the next interval — the new
        /// pass's first steps (<see cref="Upcoming"/>) — against the supply it builds for them.</summary>
        private bool NextPass()
        {
            if (Cursor < _pass.Count) return true;
            if (_refused.Count == 0) return false;
            if (_builtThisPass == 0)
            {
                Skipped += _refused.Count;
                _refused.Clear();
                return false;
            }
            _pass = _refused;
            _refused = new List<RestorationStep>();
            Cursor = 0;
            _builtThisPass = 0;
            Passes++;
            return true;
        }

        private void Pause(string reason)
        {
            Status = RestorationJobStatus.Paused;
            PauseReason = reason;
        }

        private void Count(RestorationStep step)
        {
            if (step.Kind == RestorationStepKind.Weld) Welded++;
            else if (step.Kind == RestorationStepKind.AttachTop) Attached++;
            else Placed++;
        }

        private static string Describe(RestorationBillLine line)
        {
            if (line == null) return "components";
            long bought = line.FromNode + line.FromBank;
            return bought.ToString(CultureInfo.InvariantCulture) + " " + line.Component;
        }

        private static string Num(long v) { return v.ToString("N0", CultureInfo.InvariantCulture); }
    }
}
