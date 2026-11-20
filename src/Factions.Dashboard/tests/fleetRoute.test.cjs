const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/map/fleetRoute.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadFleetRoute() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-dashboard-test-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'fleetRoute.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

test('fleet route starts at the displayed fleet position and ends at its next stop', () => {
  const { routeToNextStop } = loadFleetRoute()
  const current = { x: 125, y: -30, z: 80 }
  const nextStop = { x: 900, y: 40, z: -700 }

  assert.deepEqual(routeToNextStop(current, nextStop), [current, nextStop])
})

test('only the clearance polyline ahead of the current target is routed', () => {
  const { routedStopCount } = loadFleetRoute()

  // No stops, or nothing routed yet.
  assert.equal(routedStopCount(null), 0)
  assert.equal(routedStopCount([]), 0)

  // Direct leg: the target IS the next semantic stop, so the Return behind it is unplanned.
  assert.equal(routedStopCount([{ kind: 'Return' }]), 0)

  // Mid-arc: three chords plus the tangent-out onto the destination. The Return is a separate leg.
  assert.equal(
    routedStopCount([
      { kind: 'Navigation' }, { kind: 'Navigation' }, { kind: 'Navigation' },
      { kind: 'Service' }, { kind: 'Return' },
    ]),
    4,
  )

  // Last chord of an arc: the destination is already the current target.
  assert.equal(routedStopCount([{ kind: 'Service' }, { kind: 'Return' }]), 0)

  // Waypoints behind an unrouted stop belong to a leg the fleet is not on.
  assert.equal(
    routedStopCount([{ kind: 'Service' }, { kind: 'Navigation' }, { kind: 'Navigation' }]),
    0,
  )
})

test('vertices the marker has passed collapse onto it, so the route never doubles back', () => {
  const { passedVertexCount } = loadFleetRoute()

  // On the first segment only the marker itself moves — the original behaviour.
  assert.equal(passedVertexCount(0, 4), 1)
  // Past the second waypoint, three leading points are behind the marker.
  assert.equal(passedVertexCount(2, 4), 3)
  // Never past the end of the buffer.
  assert.equal(passedVertexCount(9, 4), 4)
  assert.equal(passedVertexCount(0, 0), 0)

  // The defect this encodes: with the marker on segment 2, collapsing only point 0 leaves the line
  // running backwards to the waypoints already flown. Route along +x, marker at x=25.
  const route = [0, 10, 20, 30]
  const marker = 25
  const segment = 2

  const collapse = (n) => [...Array(n).fill(marker), ...route.slice(n)]
  const backtracks = (pts) => {
    for (let i = 0; i < pts.length - 1; i++) if (pts[i + 1] < pts[i]) return true
    return false
  }

  assert.equal(backtracks(collapse(1)), true)                             // the old behaviour
  assert.equal(backtracks(collapse(passedVertexCount(segment, route.length))), false)
})
