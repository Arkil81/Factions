import React from 'react'
import type { PerfSnapshot } from '../types/world'
import { formatCount, formatMs, formatPercentileCoverage, formatSnapshotAge,
         isPerfSnapshotStale } from '../perfFormat'

/** The always-on strip over the map: the frame contract and nothing else. The map corner can only
 *  ever be as tall as the map and takes no pointer events, so everything that needs room, headers or
 *  a scrollbar lives in the Perf view instead. */
export default function PerfGlance({ perf }: { perf: PerfSnapshot }) {
  const [now, setNow] = React.useState(() => Date.now())
  React.useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 5_000)
    return () => clearInterval(t)
  }, [])

  const targetSec = perf.windowTargetSeconds ?? perf.windowSeconds
  const stale = isPerfSnapshotStale(perf.at, targetSec, now)
  const frame = perf.frame
  const coverage = frame ? formatPercentileCoverage(frame.sampledFrames, frame.calls) : null

  return (
    <div className="perf-overlay">
      <div className="perf-header">
        FRAME{' '}
        <span style={{ opacity: 0.55, fontWeight: 400 }}>{perf.windowSeconds.toFixed(0)}s window</span>{' '}
        <span style={{ fontWeight: 400, color: stale ? '#e67e22' : 'var(--text-3)' }}>
          {stale ? 'STALE ' : ''}{formatSnapshotAge(perf.at, now)}
        </span>
      </div>

      {frame ? (
        <>
          <div className="perf-row">
            <span className="perf-label">avg</span>
            <span className="perf-val" style={{ color: frame.avgMs >= frame.budgetMs ? '#e67e22' : undefined }}>
              {formatMs(frame.avgMs)}<span className="perf-unit">ms</span>
            </span>
            <span className="perf-calls">
              of {formatMs(frame.budgetMs)}ms budget &middot; over {formatCount(frame.overBudgetCount)}/{formatCount(frame.calls)}
            </span>
          </div>
          <div className="perf-row">
            <span className="perf-label">p99</span>
            <span className="perf-val">{formatMs(frame.p99Ms)}<span className="perf-unit">ms</span></span>
            <span className="perf-calls">
              p50 {formatMs(frame.p50Ms)} &middot; p95 {formatMs(frame.p95Ms)} &middot; max {formatMs(frame.maxMs)}
            </span>
          </div>
          {coverage && (
            <div className="perf-row">
              <span className="perf-label" />
              <span className="perf-calls">{coverage}</span>
            </div>
          )}
        </>
      ) : (
        <div className="perf-row">
          <span className="perf-calls">frame timing not reported by this build</span>
        </div>
      )}

      <div className="perf-legend">Perf tab for the rest</div>
    </div>
  )
}
