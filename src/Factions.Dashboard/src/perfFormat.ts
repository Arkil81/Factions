import type { PerfWindowData } from './types/world'

/** What one perf row has to say. `idle` is its own state, not a very small duration: the manager
 *  cadences are three times the window length, so a bucket with no calls at all is the majority
 *  reading for most rows and was previously formatted as `<0.01ms` -- identical to a bucket that ran
 *  and was fast. */
export interface PerfRowReading {
  idle: boolean
  /** The bucket's whole share of the window, in ms. 0 when idle. */
  totalMs: number
  /** True when totalMs was reconstructed from a rounded avgMs because the host build predates the
   *  totalMs field, so the figure is an approximation. */
  approximate: boolean
}

export function readPerfRow(w: PerfWindowData): PerfRowReading {
  if (!w || w.calls === 0) return { idle: true, totalMs: 0, approximate: false }
  const approximate = w.totalMs === undefined
  return { idle: false, totalMs: approximate ? w.avgMs * w.calls : (w.totalMs as number), approximate }
}

/** Bar width as a fraction of the heaviest row on the panel, NOT of the window. The plugin's whole
 *  footprint is single-digit percent of a window, so a window-relative bar is a flat empty track on
 *  every row and ranks nothing. The window footprint is a single figure and belongs in the footer.
 *  <para>A floor keeps a row that did run from reading as an empty track.</para> */
export function perfBarFraction(totalMs: number, peakMs: number): number {
  if (!(peakMs > 0) || !(totalMs > 0)) return 0
  return Math.max(0.04, Math.min(1, totalMs / peakMs))
}

/** Summed cost and heaviest row across the panel's buckets, skipping the idle ones. */
export function summarisePerfRows(rows: PerfWindowData[]): { totalMs: number, peakMs: number } {
  let totalMs = 0
  let peakMs = 0
  for (const w of rows) {
    const r = readPerfRow(w)
    if (r.idle) continue
    totalMs += r.totalMs
    if (r.totalMs > peakMs) peakMs = r.totalMs
  }
  return { totalMs, peakMs }
}

/** "1.36s of 60.0s (2.3%)" -- what the plugin actually cost the window, which no single row says. */
export function formatWindowFootprint(totalMs: number, windowSeconds: number): string {
  const windowMs = Math.max(1, windowSeconds * 1000)
  const pct = (totalMs / windowMs) * 100
  const total = totalMs >= 1000 ? (totalMs / 1000).toFixed(2) + 's' : totalMs.toFixed(0) + 'ms'
  return total + ' of ' + windowSeconds.toFixed(0) + 's (' + pct.toFixed(1) + '%)'
}

/** Milliseconds at a readable width for a 10px monospace overlay. Callers must not hand this a
 *  zero-call bucket -- see readPerfRow. */
export function formatMs(ms: number): string {
  if (ms < 0.005) return '<0.01'
  if (ms < 1) return ms.toFixed(2)
  if (ms < 100) return ms.toFixed(1)
  return ms.toFixed(0)
}

/** True once perf.json has stopped being rewritten -- the server is down or paused -- so every
 *  number in the panel is frozen history rather than a live reading. Two windows plus a margin, so a
 *  window that merely ran long does not raise it. */
export function isPerfSnapshotStale(atIso: string, targetWindowSeconds: number, nowMs: number): boolean {
  const at = new Date(atIso).getTime()
  if (!isFinite(at)) return true
  return (nowMs - at) / 1000 > targetWindowSeconds * 2 + 30
}

export function formatSnapshotAge(atIso: string, nowMs: number): string {
  const sec = Math.max(0, (nowMs - new Date(atIso).getTime()) / 1000)
  return sec < 90 ? `${Math.round(sec)}s ago` : `${Math.round(sec / 60)}m ago`
}

/** Counts at operator width -- a raw 87117 in a table cell is read wrong more often than not. */
export function formatCount(n: number): string {
  if (!isFinite(n)) return '-'
  return Math.round(n).toLocaleString('en-US')
}

/** A wire key as a column or tile label: `alternatePickupMs` reads `alternate pickup`. The trailing
 *  `Ms` goes because the table carries the unit in its header. */
export function humaniseKey(key: string): string {
  const base = key.length > 2 && key.endsWith('Ms') ? key.slice(0, -2) : key
  return base.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase()
}

export interface TelemetryEntry {
  key: string
  label: string
  value: number
}

/** A telemetry block sorted into what it cost, what it measured, and what it counted.
 *  <para>The host writes all three into one flat object and the suffix is the only thing separating
 *  them: `<name>Ms` is a duration, and a `max` prefix on one of those makes it a gauge (an age, a
 *  deferral) rather than a share of the sweep. Splitting on the naming rule rather than on a list of
 *  field names means a field added host-side arrives in the right table on its own.</para> */
export interface TelemetrySplit {
  /** Durations that add up to what the sweep cost. Rankable against each other. */
  timings: TelemetryEntry[]
  /** Millisecond gauges. Real readings, but not a share of anything -- never ranked with timings. */
  gauges: TelemetryEntry[]
  counters: TelemetryEntry[]
}

export function splitTelemetry(block: object | null | undefined): TelemetrySplit {
  const timings: TelemetryEntry[] = []
  const gauges: TelemetryEntry[] = []
  const counters: TelemetryEntry[] = []
  if (block) {
    for (const key of Object.keys(block)) {
      const value = (block as Record<string, unknown>)[key]
      if (typeof value !== 'number' || !isFinite(value)) continue
      const entry: TelemetryEntry = { key, label: humaniseKey(key), value }
      if (key.length > 2 && key.endsWith('Ms')) (key.startsWith('max') ? gauges : timings).push(entry)
      else counters.push(entry)
    }
  }
  return { timings, gauges, counters }
}

export interface TimingRow<T> {
  key: string
  label: string
  w: T
}

/** The timing windows of a homogeneous wire object (`host`, `econPhases`, `thinkStages`), in the
 *  order the host wrote them. Iterated rather than named, so a bucket added host-side reaches the
 *  panel without an edit here; a nested map of windows is skipped, having no call count of its own. */
export function timingRows<T extends PerfWindowData>(block: object | null | undefined): TimingRow<T>[] {
  const out: TimingRow<T>[] = []
  if (!block) return out
  for (const key of Object.keys(block)) {
    const value = (block as Record<string, unknown>)[key]
    if (!value || typeof value !== 'object') continue
    if (typeof (value as { calls?: unknown }).calls !== 'number') continue
    out.push({ key, label: humaniseKey(key), w: value as T })
  }
  return out
}

/** What p50/p95/p99 actually describe when the percentile ring is smaller than the window, or null
 *  when it covered the whole of it. avg/max/overBudgetCount always describe every call. */
export function formatPercentileCoverage(sampledFrames: number, calls: number): string | null {
  if (!(calls > 0) || sampledFrames >= calls) return null
  return 'p50/p95/p99 cover the last ' + formatCount(sampledFrames)
    + ' of ' + formatCount(calls) + ' calls'
}
