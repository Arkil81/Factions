const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/map/fleetTransit.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadTransit() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-fleet-transit-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'fleetTransit.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

test('trail milestones emit once per crossed 100 km boundary', () => {
  const { trailMilestoneDistances } = loadTransit()
  assert.equal(typeof trailMilestoneDistances, 'function')
  assert.deepEqual(trailMilestoneDistances(95, 305), [100, 200, 300])
})

test('trail milestones do not emit for reverse motion or an unchanged distance', () => {
  const { trailMilestoneDistances } = loadTransit()
  assert.deepEqual(trailMilestoneDistances(300, 300), [])
  assert.deepEqual(trailMilestoneDistances(300, 200), [])
})

// A fleet mid-way through a 1000 km clearance polyline: the current leg is the 200 km hop that ends
// 400 km along the route, so 200 km of the route precede the polyline the client can draw.
function routedFleet(overrides = {}) {
  return Object.assign({
    departedUtc: '2026-08-21T12:00:00Z',
    estimatedArrivalUtc: '2026-08-21T12:10:00Z',
    transitDistanceKm: 200,
    fleetSpeedMinKmPerMin: 5,
    fleetSpeedKmPerMin: 1500,
    fleetAccelKmPerMin2: 4608,
    completionRatio: 0,
    routeDistanceKm: 1000,
    routeCoveredKm: 400,
    routeDepartedUtc: '2026-08-21T12:00:00Z',
  }, overrides)
}

test('a fleet without route fields is not treated as flying a clearance route', () => {
  const { isOnClearanceRoute, routeFlownKm, routeLegProgressKm } = loadTransit()
  const plain = routedFleet({ routeDistanceKm: 0, routeDepartedUtc: undefined })
  assert.equal(isOnClearanceRoute(plain), false)
  assert.equal(routeFlownKm(plain), null)
  assert.equal(routeLegProgressKm(plain), null)
})

test('clearance route progress spans the whole polyline and only ever advances', () => {
  const { routeFlownKm } = loadTransit()
  const fleet = routedFleet()
  const start = Date.parse(fleet.routeDepartedUtc)

  assert.equal(routeFlownKm(fleet, start), 0)
  const early = routeFlownKm(fleet, start + 10_000)
  const later = routeFlownKm(fleet, start + 20_000)
  assert.ok(later > early, 'route odometer must advance')
  // Clamped to the polyline length however long the fleet overruns its schedule.
  assert.equal(routeFlownKm(fleet, start + 3_600_000), 1000)
})

test('leg progress is measured from the start of the leg the client can draw', () => {
  const { routeFlownKm, routeLegProgressKm } = loadTransit()
  const fleet = routedFleet()
  // Far enough in that the fleet is genuinely past the start of the drawable leg.
  const now = Date.parse(fleet.routeDepartedUtc) + 30_000
  assert.ok(routeFlownKm(fleet, now) > 200)

  // routeCoveredKm is the distance at the END of the current leg, so the drawable leg starts
  // 400 - 200 = 200 km along the route.
  assert.equal(routeLegProgressKm(fleet, now), routeFlownKm(fleet, now) - 200)
})

test('leg progress never runs backwards before the current leg begins', () => {
  const { routeLegProgressKm } = loadTransit()
  const fleet = routedFleet()
  assert.equal(routeLegProgressKm(fleet, Date.parse(fleet.routeDepartedUtc)), 0)
})
