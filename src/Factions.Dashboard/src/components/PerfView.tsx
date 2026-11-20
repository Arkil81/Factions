import React from 'react'
import type {
  FrameTimingWindowData, PerfSnapshot, PerfWindowData, SchedulerActorData,
} from '../types/world'
import type { TimingRow } from '../perfFormat'
import {
  formatCount, formatMs, formatPercentileCoverage, formatSnapshotAge, formatWindowFootprint,
  isPerfSnapshotStale, perfBarFraction, readPerfRow, splitTelemetry, summarisePerfRows, timingRows,
} from '../perfFormat'

/** A row that ran and cost nothing measurable is not the same reading as one that never ran, so a
 *  literal zero keeps its own cell rather than borrowing the sub-0.01 form. */
function ms(value: number): string {
  return value === 0 ? '0' : formatMs(value)
}

function Idle() {
  return <span className="perf-idle">&mdash;</span>
}

function Bar({ value, peak }: { value: number, peak: number }) {
  return <span className="perf-bar"><i style={{ width: `${perfBarFraction(value, peak) * 100}%` }} /></span>
}

function Section({ title, note, children }: {
  title: string
  note?: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <section className="perf-section">
      <div className="section-title">{title}</div>
      {note && <div className="perf-note">{note}</div>}
      {children}
    </section>
  )
}

function Metrics({ items }: { items: Array<[string, React.ReactNode, string?]> }) {
  return (
    <div className="perf-metric-grid">
      {items.map(([label, value, tone]) => (
        <div className="perf-metric" key={label}>
          <div className="perf-metric-label">{label}</div>
          <div className="perf-metric-value" style={tone ? { color: tone } : undefined}>{value}</div>
        </div>
      ))}
    </div>
  )
}

type Row = TimingRow<PerfWindowData> & { depth?: number }

/** Buckets measured as a share of the window. Ranked by the bar against the heaviest row present,
 *  never against the window: the whole plugin is single-digit percent of one, so a window-relative
 *  track is empty on every row and ranks nothing. */
function WindowTable({ rows, header, windowSeconds }: {
  rows: Row[]
  header: string
  windowSeconds: number
}) {
  const { peakMs, totalMs } = summarisePerfRows(rows.map(r => r.w))
  return (
    <div className="perf-table-wrap">
      <table className="data-table perf-table">
        <thead>
          <tr>
            <th>{header}</th>
            <th className="num">Total ms</th>
            <th className="bar-col">Share</th>
            <th className="num">Avg ms</th>
            <th className="num">Max ms</th>
            <th className="num">Calls</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(({ key, label, w, depth }) => {
            const r = readPerfRow(w)
            return (
              <tr key={key}>
                <td style={depth ? { paddingLeft: depth * 12 } : undefined}>{label}</td>
                <td className="num">{r.idle ? <Idle /> : ms(r.totalMs)}</td>
                <td className="bar-col">{r.idle ? null : <Bar value={r.totalMs} peak={peakMs} />}</td>
                <td className="num">{r.idle ? <Idle /> : ms(w.avgMs)}</td>
                <td className="num">{r.idle ? <Idle /> : ms(w.maxMs)}</td>
                <td className="num">{r.idle ? <Idle /> : formatCount(w.calls)}</td>
              </tr>
            )
          })}
        </tbody>
        <tfoot>
          <tr>
            <td>sum</td>
            <td className="num">{ms(totalMs)}</td>
            <td colSpan={4}>{formatWindowFootprint(totalMs, windowSeconds)}</td>
          </tr>
        </tfoot>
      </table>
    </div>
  )
}

/** Per-frame actors, which carry percentiles and a budget the window buckets do not. */
function FrameTable({ rows, header }: { rows: TimingRow<FrameTimingWindowData>[], header: string }) {
  const { peakMs } = summarisePerfRows(rows.map(r => r.w))
  return (
    <div className="perf-table-wrap">
      <table className="data-table perf-table">
        <thead>
          <tr>
            <th>{header}</th>
            <th className="num">Total ms</th>
            <th className="bar-col">Share</th>
            <th className="num">Avg</th>
            <th className="num">p50</th>
            <th className="num">p95</th>
            <th className="num">p99</th>
            <th className="num">Max</th>
            <th className="num">Budget</th>
            <th className="num">Over</th>
            <th className="num">Calls</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(({ key, label, w }) => {
            const r = readPerfRow(w)
            if (r.idle) {
              return (
                <tr key={key}>
                  <td>{label}</td>
                  <td className="num"><Idle /></td>
                  <td className="bar-col" />
                  <td className="num" colSpan={8}><Idle /></td>
                </tr>
              )
            }
            return (
              <tr key={key}>
                <td>{label}</td>
                <td className="num">{ms(r.totalMs)}</td>
                <td className="bar-col"><Bar value={r.totalMs} peak={peakMs} /></td>
                <td className="num">{ms(w.avgMs)}</td>
                <td className="num">{ms(w.p50Ms)}</td>
                <td className="num">{ms(w.p95Ms)}</td>
                <td className="num">{ms(w.p99Ms)}</td>
                <td className="num">{ms(w.maxMs)}</td>
                <td className="num">{ms(w.budgetMs)}</td>
                <td className="num" style={w.overBudgetCount > 0 ? { color: '#e67e22' } : undefined}>
                  {formatCount(w.overBudgetCount)}
                </td>
                <td className="num">{formatCount(w.calls)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/** Cost and scheduling of each frame actor. `excess` is attributable over-budget time and is what
 *  orders the rechunk queue; `est err` is how wrong the admission estimate was, which is what lets
 *  an actor be admitted into a frame it cannot finish in. */
function SchedulerCostTable({ actors }: { actors: SchedulerActorData[] }) {
  const peak = actors.reduce((m, a) => a.excessMs > m ? a.excessMs : m, 0)
  return (
    <div className="perf-table-wrap">
      <table className="data-table perf-table">
        <thead>
          <tr>
            <th>Actor</th>
            <th className="num">Interval</th>
            <th className="num">Est ms</th>
            <th className="num">Est err avg</th>
            <th className="num">Est err max</th>
            <th className="num">Calls</th>
            <th className="num">Avg ms</th>
            <th className="num">p99</th>
            <th className="num">Max ms</th>
            <th className="num">Over</th>
            <th className="num">Excess ms</th>
            <th className="bar-col">Excess</th>
          </tr>
        </thead>
        <tbody>
          {actors.map(a => (
            <tr key={a.name}>
              <td>{a.name}{a.sweepInProgress && <span className="perf-flag"> sweeping</span>}</td>
              <td className="num">{formatInterval(a.intervalMs)}</td>
              <td className="num">{ms(a.estimateMs)}</td>
              <td className="num">{ms(a.estErrorAvgMs)}</td>
              <td className="num">{ms(a.estErrorMaxMs)}</td>
              <td className="num">{formatCount(a.calls)}</td>
              <td className="num">{ms(a.avgMs)}</td>
              <td className="num">{ms(a.p99Ms)}</td>
              <td className="num">{ms(a.maxMs)}</td>
              <td className="num" style={a.overBudgetCount > 0 ? { color: '#e67e22' } : undefined}>
                {formatCount(a.overBudgetCount)}
              </td>
              <td className="num">{ms(a.excessMs)}</td>
              <td className="bar-col"><Bar value={a.excessMs} peak={peak} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/** How the shared budget treated each actor. A large `max deferral` against a short interval is the
 *  starvation signal; `denied` counts the frames the budget turned the actor away. */
function SchedulerAdmissionTable({ actors }: { actors: SchedulerActorData[] }) {
  return (
    <div className="perf-table-wrap">
      <table className="data-table perf-table">
        <thead>
          <tr>
            <th>Actor</th>
            <th className="num">Admitted</th>
            <th className="num">Completed</th>
            <th className="num">Yielded</th>
            <th className="num">Skipped</th>
            <th className="num">Not due</th>
            <th className="num">Denied</th>
            <th className="num">Forced</th>
            <th className="num">Max deferral</th>
          </tr>
        </thead>
        <tbody>
          {actors.map(a => (
            <tr key={a.name}>
              <td>{a.name}</td>
              <td className="num">{formatCount(a.admissions)}</td>
              <td className="num">{formatCount(a.completions)}</td>
              <td className="num">{formatCount(a.yields)}</td>
              <td className="num">{formatCount(a.skips)}</td>
              <td className="num">{formatCount(a.notDue)}</td>
              <td className="num">{formatCount(a.deniedByBudget)}</td>
              <td className="num">{formatCount(a.forcedAdmissions)}</td>
              <td className="num" style={a.maxDeferralMs > a.intervalMs ? { color: '#e67e22' } : undefined}>
                {formatInterval(a.maxDeferralMs)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function formatInterval(msValue: number): string {
  if (!isFinite(msValue)) return '-'
  if (msValue >= 1000) return (msValue / 1000).toFixed(msValue >= 10_000 ? 0 : 1) + 's'
  return ms(msValue) + 'ms'
}

/** One manager's sweep telemetry: what the sweep cost by phase, and what it walked to get there. */
function TelemetryBlock({ title, note, block }: {
  title: string
  note?: React.ReactNode
  block: object | undefined
}) {
  if (!block) return null
  const { timings, gauges, counters } = splitTelemetry(block)
  const peak = timings.reduce((m, t) => t.value > m ? t.value : m, 0)
  const sum = timings.reduce((s, t) => s + t.value, 0)

  return (
    <Section title={title} note={note}>
      {timings.length > 0 && (
        <div className="perf-table-wrap">
          <table className="data-table perf-table">
            <thead>
              <tr>
                <th>Phase</th>
                <th className="num">ms</th>
                <th className="bar-col">Share</th>
              </tr>
            </thead>
            <tbody>
              {timings.map(t => (
                <tr key={t.key}>
                  <td>{t.label}</td>
                  <td className="num">{ms(t.value)}</td>
                  <td className="bar-col"><Bar value={t.value} peak={peak} /></td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <td>sum</td>
                <td className="num">{ms(sum)}</td>
                <td />
              </tr>
            </tfoot>
          </table>
        </div>
      )}
      {gauges.length > 0 && (
        <div className="perf-counter-grid">
          {gauges.map(g => (
            <div className="perf-counter" key={g.key}>
              <span className="perf-counter-label">{g.label}</span>
              <span className="perf-counter-value">{ms(g.value)}<span className="perf-unit">ms</span></span>
            </div>
          ))}
        </div>
      )}
      {counters.length > 0 && (
        <div className="perf-counter-grid">
          {counters.map(c => (
            <div className="perf-counter" key={c.key}>
              <span className="perf-counter-label">{c.label}</span>
              <span className="perf-counter-value">{formatCount(c.value)}</span>
            </div>
          ))}
        </div>
      )}
    </Section>
  )
}

export default function PerfView({ perf }: { perf: PerfSnapshot | null }) {
  const [now, setNow] = React.useState(() => Date.now())
  React.useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 5_000)
    return () => clearInterval(t)
  }, [])

  if (!perf) return <div className="perf-view-empty">Waiting for a perf window...</div>

  const targetSec = perf.windowTargetSeconds ?? perf.windowSeconds
  const stale = isPerfSnapshotStale(perf.at, targetSec, now)
  const frame = perf.frame
  const coverage = frame ? formatPercentileCoverage(frame.sampledFrames, frame.calls) : null

  const managerRows: Row[] = ([
    ['brain', perf.brain], ['prod', perf.prod], ['ops', perf.ops], ['yard', perf.yard],
    ['fleet', perf.fleet], ['trade', perf.trade], ['stock', perf.stock], ['econ', perf.econ],
    ['obs', perf.obs], ['stObs', perf.stObs], ['astObs', perf.astObs], ['flight', perf.flight],
    ['flush', perf.flushTotal],
  ] as Array<[string, PerfWindowData | undefined]>)
    .filter(([, w]) => !!w)
    .map(([key, w]) => ({ key, label: key, w: w as PerfWindowData }))

  const bp = perf.brainPhases
  const brainRows: Row[] = bp
    ? ([{ key: 'think', label: 'think', w: bp.think }] as Row[])
        .concat(timingRows<PerfWindowData>(bp.thinkStages).map(r => ({ ...r, depth: 1 })))
        .concat([
          { key: 'enact', label: 'enact', w: bp.enact },
          { key: 'log', label: 'log', w: bp.log },
        ])
    : []

  const econRows = timingRows<PerfWindowData>(perf.econPhases)
  const flushRows: Row[] = [
    { key: 'flushBrains', label: 'brains', w: perf.flushBrains },
    { key: 'flushProduction', label: 'production snapshot', w: perf.flushProduction },
    { key: 'flushSnapshot', label: 'world snapshot', w: perf.flushSnapshot },
  ]
  const hostRows = timingRows<FrameTimingWindowData>(perf.host)

  const sched = perf.scheduler
  const actors = sched?.actors ?? []
  const passActive = !!sched?.globalPassPhase && sched.globalPassPhase !== 'idle'

  return (
    <div className="perf-view">
      <div className="perf-view-head">
        <h2>Performance</h2>
        <span className="perf-note">
          {perf.windowSeconds.toFixed(0)}s window
          {perf.windowTargetSeconds !== undefined && ` (target ${perf.windowTargetSeconds.toFixed(0)}s)`}
          {' · '}written {formatSnapshotAge(perf.at, now)}
        </span>
        {stale && <span className="perf-stale">STALE &mdash; the file has stopped being rewritten</span>}
      </div>

      {frame && (
        <Section
          title="Frame contract"
          note={<>The Factions-owned body of <code>FactionsPlugin.Update()</code>, against its soft
            budget. {coverage ?? 'Percentiles cover every call in the window.'}</>}
        >
          <Metrics items={[
            ['Avg', <>{ms(frame.avgMs)}<span className="perf-unit">ms</span></>,
              frame.avgMs >= frame.budgetMs ? '#e67e22' : undefined],
            ['Budget', <>{ms(frame.budgetMs)}<span className="perf-unit">ms</span></>],
            ['p50', <>{ms(frame.p50Ms)}<span className="perf-unit">ms</span></>],
            ['p95', <>{ms(frame.p95Ms)}<span className="perf-unit">ms</span></>],
            ['p99', <>{ms(frame.p99Ms)}<span className="perf-unit">ms</span></>],
            ['Max', <>{ms(frame.maxMs)}<span className="perf-unit">ms</span></>],
            ['Over budget', `${formatCount(frame.overBudgetCount)} / ${formatCount(frame.calls)}`,
              frame.overBudgetCount > 0 ? '#e67e22' : undefined],
            ['Window cost', formatWindowFootprint(frame.totalMs ?? frame.avgMs * frame.calls, perf.windowSeconds)],
          ]} />
        </Section>
      )}

      <Section
        title="Plugin actors"
        note="Each manager's whole share of the window. Total ranks them; avg and max describe one
          call, and for a resumable manager that yields hundreds of times per sweep the two orderings
          invert."
      >
        <WindowTable rows={managerRows} header="Actor" windowSeconds={perf.windowSeconds} />
      </Section>

      {hostRows.length > 0 && (
        <Section
          title="Host frame actors"
          note="Per-frame work with its own budget and percentiles. The three observation rows are
            the same work the plugin-actor table reports as obs/stObs/astObs, measured per frame
            rather than per window, so the two tables do not add up. baseUpdate is external Torch/SE
            correlation only and is not charged to the frame contract."
        >
          <FrameTable rows={hostRows} header="Actor" />
        </Section>
      )}

      {brainRows.length > 0 && (
        <Section title="Brain phases" note="Think's seven stages are indented under it.">
          <WindowTable rows={brainRows} header="Phase" windowSeconds={perf.windowSeconds} />
        </Section>
      )}

      {econRows.length > 0 && (
        <Section
          title="Econ phases"
          note="A full global pass can span several windows, so these read all-zero while a pass is
            healthy and mid-flight. The scheduler's econ-pass row below says which it is."
        >
          <WindowTable rows={econRows} header="Phase" windowSeconds={perf.windowSeconds} />
        </Section>
      )}

      <Section title="Persistence" note="The parts of the flush row above.">
        <WindowTable rows={flushRows} header="Flush" windowSeconds={perf.windowSeconds} />
      </Section>

      {sched && (
        <Section
          title="Scheduler"
          note="Admission against a shared per-frame budget. No-stack ratio is the window's worst
            frame over its largest single unit: at or below 1.0 no two units landed together."
        >
          <Metrics items={[
            ['Soft deadline', <>{ms(sched.softDeadlineMs)}<span className="perf-unit">ms</span></>],
            ['No-stack ratio', sched.noStackRatio === undefined ? '-' : sched.noStackRatio.toFixed(2),
              (sched.noStackRatio ?? 0) > 1 ? '#e67e22' : undefined],
            ['Units admitted', formatCount(sched.admittedUnitCount ?? 0)],
            ['Max units in a frame', formatCount(sched.maxUnitsInOneFrame ?? 0)],
            ['Frames with no admission', formatCount(sched.framesWithNoAdmission ?? 0)],
            ['Denied by budget', formatCount(sched.deniedByBudgetCount ?? 0)],
            ['Forced admissions', formatCount(sched.forcedAdmissionCount ?? 0),
              (sched.forcedAdmissionCount ?? 0) > 0 ? '#e67e22' : undefined],
            ['Sweeps in flight', formatCount(sched.deferredWorkCount),
              sched.deferredWorkCount > 0 ? '#f1c40f' : undefined],
            ['Oldest deferred', formatInterval(sched.oldestDeferredAgeMs)],
            ['Due unsafe', formatCount(sched.dueUnsafeCount)],
            ['Expired before unsafe', formatCount(sched.expiredBeforeUnsafeCount)],
            ['Shared budget', sched.sharedBudgetEnabled === false ? 'off' : 'on'],
          ]} />

          {passActive && (
            <div className="perf-note perf-pass">
              econ pass: <strong>{sched.globalPassPhase}</strong>
              {sched.globalPassPhase === 'BuildingSnapshot' && !!sched.globalPassSnapshotBuildTotal &&
                ` ${sched.globalPassSnapshotBuildIndex}/${sched.globalPassSnapshotBuildTotal}`}
              {sched.globalPassPhase === 'AccruingIncome' && sched.globalPassIncomePhase &&
                ` ${sched.globalPassIncomePhase} ${sched.globalPassIncomeIndex}/${sched.globalPassIncomeTotal}`}
              {sched.globalPassElapsedMs !== undefined && ` · ${formatInterval(sched.globalPassElapsedMs)} so far`}
              {!!sched.globalPassSnapshotRestarts && ` · restarts ${sched.globalPassSnapshotRestarts}`}
            </div>
          )}

          {!!sched.rechunkQueue?.length && (
            <div className="perf-note">
              Rechunk queue, worst first: <strong>{sched.rechunkQueue.join(', ')}</strong>
            </div>
          )}

          {actors.length > 0 && <SchedulerCostTable actors={actors} />}
          {actors.length > 0 && <SchedulerAdmissionTable actors={actors} />}
        </Section>
      )}

      <TelemetryBlock
        title="Production sweep"
        note="One ProductionManager sweep, across however many frames it took."
        block={perf.productionTelemetry}
      />
      <TelemetryBlock
        title="Operations sweep"
        note="One OperationsManager sweep: demand matching, route planning and dispatch."
        block={perf.operationsTelemetry}
      />
      <TelemetryBlock
        title="Trade sweep"
        note="One TradeManager sweep: partner assessment and inter-faction dispatch."
        block={perf.tradeTelemetry}
      />
    </div>
  )
}
