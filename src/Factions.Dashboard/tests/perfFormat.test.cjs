const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/perfFormat.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadPerfFormat() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-dashboard-test-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--rootDir', join(dashboardRoot, 'src'), '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'perfFormat.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

const { readPerfRow, formatMs, isPerfSnapshotStale, formatSnapshotAge,
        perfBarFraction, summarisePerfRows, formatWindowFootprint,
        formatCount, humaniseKey, splitTelemetry, timingRows,
        formatPercentileCoverage } = loadPerfFormat()

// Both fixtures are real 60 s windows off the live server, Factions-2026-09-08.log 22:45:32Z and
// 22:46:32Z. ProductionIntervalSeconds and its five siblings are 180 while the perf window is 60, so
// the second shape is what an operator sees in two refreshes out of every three.
const RAN   = { calls: 249, avgMs: 0.703, maxMs: 14.43, totalMs: 175.047 }
const IDLE  = { calls: 0,   avgMs: 0,     maxMs: 0,     totalMs: 0 }

test('a bucket that did not run reads as idle, not as a very small duration', () => {
  // The reported defect: both rows formatted to "<0.01ms" and the only thing separating them was the
  // absence of a dim call-count badge.
  assert.equal(readPerfRow(IDLE).idle, true)
  assert.equal(readPerfRow(RAN).idle, false)
  assert.notEqual(formatMs(readPerfRow(RAN).totalMs), formatMs(0))
})

test('a bucket that ran and was genuinely sub-0.01ms is still not idle', () => {
  // flight, live: 3594 calls averaging under 0.0005 ms. It has to stay distinguishable from a bucket
  // with no calls, or the fix trades one collision for another.
  const flight = { calls: 3594, avgMs: 0, maxMs: 0.008, totalMs: 1.4 }
  assert.equal(readPerfRow(flight).idle, false)
})

test('total ranks buckets the way avg does not', () => {
  // Live 23:06:32Z. obs is the window's largest consumer and shows the third-smallest average; yard
  // shows the largest average on the panel and costs a fourteenth of obs.
  const obs  = { calls: 3594, avgMs: 0.119, maxMs: 0.296, totalMs: 427.7 }
  const yard = { calls: 1, avgMs: 29.579, maxMs: 29.579, totalMs: 29.579 }
  assert.ok(obs.avgMs < yard.avgMs)
  assert.ok(readPerfRow(obs).totalMs > readPerfRow(yard).totalMs)

  const { peakMs } = summarisePerfRows([obs, yard])
  assert.equal(peakMs, obs.totalMs)
  assert.ok(perfBarFraction(obs.totalMs, peakMs) > perfBarFraction(yard.totalMs, peakMs))
})

test('the bar ranks against the heaviest row, because a window-relative one ranks nothing', () => {
  // Every row of the real 23:06:32Z window sits under 0.75% of 60 s, so a window-relative bar draws
  // the same empty track for the panel's largest consumer and its smallest.
  const obs   = { calls: 3594, avgMs: 0.119, maxMs: 0.296, totalMs: 427.7 }
  const stock = { calls: 1, avgMs: 0.675, maxMs: 0.675, totalMs: 0.675 }
  assert.ok(obs.totalMs / 60000 < 0.01 && stock.totalMs / 60000 < 0.01)

  const { peakMs } = summarisePerfRows([obs, stock])
  assert.equal(perfBarFraction(obs.totalMs, peakMs), 1)
  // The lightest row keeps a visible stub rather than an empty track, but stays far below the peak.
  assert.equal(perfBarFraction(stock.totalMs, peakMs), 0.04)
  assert.equal(perfBarFraction(0, peakMs), 0)
})

test('the footprint says what the panel as a whole cost the window', () => {
  // Summed from the real 23:06:32Z window; no single row carries this and it was not shown at all.
  const rows = [
    { calls: 3, avgMs: 2.32, maxMs: 4.108, totalMs: 6.96 },
    { calls: 249, avgMs: 0.703, maxMs: 14.43, totalMs: 175.0 },
    { calls: 3594, avgMs: 0.119, maxMs: 0.296, totalMs: 427.7 },
    { calls: 0, avgMs: 0, maxMs: 0, totalMs: 0 },
  ]
  const { totalMs } = summarisePerfRows(rows)
  assert.ok(Math.abs(totalMs - 609.66) < 0.01)
  assert.equal(formatWindowFootprint(1360, 60), '1.36s of 60s (2.3%)')
  assert.equal(formatWindowFootprint(204, 60), '204ms of 60s (0.3%)')
})

test('totalMs falls back to the rounded product for a host build that omits it, and says so', () => {
  const { totalMs, approximate } = readPerfRow({ calls: 249, avgMs: 0.703, maxMs: 14.43 })
  assert.equal(approximate, true)
  assert.ok(Math.abs(totalMs - 175.047) < 0.001)
  assert.equal(readPerfRow(RAN).approximate, false)
})

test('the bar never overflows its track', () => {
  assert.equal(perfBarFraction(500, 100), 1)
  assert.equal(perfBarFraction(10, 0), 0)
})

test('a snapshot stops being live two windows past its stamp', () => {
  const at = '2026-09-08T23:06:32.000Z'
  const t = Date.parse(at)
  assert.equal(isPerfSnapshotStale(at, 60, t + 60_000), false)
  assert.equal(isPerfSnapshotStale(at, 60, t + 200_000), true)
  // A raised PerfWindowSeconds moves the threshold with it rather than reporting every window stale.
  assert.equal(isPerfSnapshotStale(at, 180, t + 200_000), false)
  assert.equal(isPerfSnapshotStale('not a date', 60, t), true)
})

test('snapshot age switches to minutes once seconds stop being readable', () => {
  const at = '2026-09-08T23:06:32.000Z'
  const t = Date.parse(at)
  assert.equal(formatSnapshotAge(at, t + 12_000), '12s ago')
  assert.equal(formatSnapshotAge(at, t + 600_000), '10m ago')
  assert.equal(formatSnapshotAge(at, t - 5_000), '0s ago')
})

// ---------------------------------------------------------------------------
// The blocks the panel serves but never drew, and the shapes that let it draw them without a
// hand-written list of every field. Fixtures are the real 180 s window at 2026-09-09T15:48:47Z.
// ---------------------------------------------------------------------------

const OPS_TELEMETRY = {
  calls: 1, factions: 13, ownedNodes: 679, demands: 6031,
  routeCacheHits: 11064, routeCacheMisses: 25672, shipSuitabilityChecks: 87117,
  preparationMs: 3.164, targetMatchingMs: 28.786, directDispatchMs: 29.35,
  alternatePickupMs: 65, alternateScanMs: 54.032, fallbackPublicationMs: 0,
  yields: 286, restarts: 0, sweepsCompleted: 1,
}

const PRODUCTION_TELEMETRY = {
  calls: 1, slotsVisited: 1020, recipesSkippedNoDemand: 8479, restarts: 1,
  maxCycleAgeMs: 1406.016, preparationMs: 6.657, investmentRefinementMs: 66.675, catalogMs: 26.586,
}

test('a telemetry block splits into cost and count on the wire naming, not on a field list', () => {
  const { timings, counters } = splitTelemetry(OPS_TELEMETRY)
  const timingKeys = timings.map(t => t.key)
  const counterKeys = counters.map(c => c.key)

  assert.ok(timingKeys.includes('alternatePickupMs'))
  assert.ok(timingKeys.includes('fallbackPublicationMs'), 'a phase that cost nothing is still a phase')
  assert.ok(counterKeys.includes('shipSuitabilityChecks'))
  assert.ok(counterKeys.includes('routeCacheMisses'))
  assert.equal(timingKeys.some(k => !k.endsWith('Ms')), false)
  assert.equal(counterKeys.some(k => k.endsWith('Ms')), false)
})

test('a millisecond gauge is not ranked against the phases it dwarfs', () => {
  // maxCycleAgeMs is how old the oldest in-flight cycle is -- 1406 ms against a 66.7 ms phase. Sorted
  // in with the timings it takes the bar's peak and every real phase reads as free.
  const { timings, gauges } = splitTelemetry(PRODUCTION_TELEMETRY)
  assert.deepEqual(gauges.map(g => g.key), ['maxCycleAgeMs'])
  assert.equal(timings.some(t => t.key === 'maxCycleAgeMs'), false)

  const peak = timings.reduce((m, t) => t.value > m ? t.value : m, 0)
  assert.equal(peak, 66.675)
  assert.ok(perfBarFraction(26.586, peak) > 0.3)
})

test('an absent telemetry block is three empty lists, not a throw', () => {
  for (const absent of [undefined, null]) {
    const split = splitTelemetry(absent)
    assert.deepEqual(split.timings, [])
    assert.deepEqual(split.gauges, [])
    assert.deepEqual(split.counters, [])
  }
})

test('a wire key reads as a label', () => {
  assert.equal(humaniseKey('alternatePickupMs'), 'alternate pickup')
  assert.equal(humaniseKey('routeCacheMisses'), 'route cache misses')
  assert.equal(humaniseKey('calls'), 'calls')
  assert.equal(humaniseKey('tier1'), 'tier1')
})

test('timing rows come off the wire object itself, so a bucket added host-side is not withheld', () => {
  // The failure this replaces: every row was named in the component, so a host build that added a
  // bucket kept serving it to a panel that could not see it.
  const host = {
    baseUpdate: { calls: 10800, avgMs: 0.02, maxMs: 3.1, totalMs: 216 },
    featureRock: { calls: 3, avgMs: 1.0, maxMs: 1.2, totalMs: 3 },
    aBucketNoOneHasWrittenYet: { calls: 1, avgMs: 5, maxMs: 5, totalMs: 5 },
  }
  assert.deepEqual(timingRows(host).map(r => r.key),
    ['baseUpdate', 'featureRock', 'aBucketNoOneHasWrittenYet'])
  assert.equal(timingRows(host)[2].label, 'a bucket no one has written yet')
})

test('a nested map of windows is not itself a row', () => {
  // brainPhases carries think/enact/log beside thinkStages, which is a map and has no call count.
  const brainPhases = {
    think: { calls: 10, avgMs: 3.849, maxMs: 13.24, totalMs: 38.492 },
    thinkStages: {
      sense: { calls: 10, avgMs: 0.319, maxMs: 0.69, totalMs: 3.194 },
      generate: { calls: 10, avgMs: 2.078, maxMs: 5.977, totalMs: 20.781 },
    },
    enact: { calls: 10, avgMs: 0.331, maxMs: 2.797, totalMs: 3.308 },
    log: { calls: 10, avgMs: 0.013, maxMs: 0.034, totalMs: 0.134 },
  }
  assert.deepEqual(timingRows(brainPhases).map(r => r.key), ['think', 'enact', 'log'])
  assert.deepEqual(timingRows(brainPhases.thinkStages).map(r => r.key), ['sense', 'generate'])
  assert.deepEqual(timingRows(undefined), [])
})

test('percentile coverage is stated only when the ring is smaller than the window', () => {
  // Equal at a 60 s window; diverges the moment PerfWindowSeconds is raised past the ring.
  assert.equal(formatPercentileCoverage(10800, 10800), null)
  assert.equal(formatPercentileCoverage(0, 0), null)
  assert.equal(formatPercentileCoverage(4096, 10800),
    'p50/p95/p99 cover the last 4,096 of 10,800 calls')
})

test('counts are grouped, because a bare 87117 is misread', () => {
  assert.equal(formatCount(87117), '87,117')
  assert.equal(formatCount(0), '0')
  assert.equal(formatCount(999), '999')
})
