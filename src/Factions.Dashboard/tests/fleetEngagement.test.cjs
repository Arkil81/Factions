// The dwell's reading, guarded against the two fleets that can defeat it silently: one that shares
// the Engage leg's field shape without being on it, and one that is on it without attacking.
const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/fleetEngagement.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadEngagement() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-fleet-engagement-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'fleetEngagement.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

// Verbatim from http://127.0.0.1:7891/api/world on the live server, 2026-09-03T20:05:09Z.
// A Mine leg is duration-billed exactly as an Engage leg is, so it carries the identical field
// shape: origin == target, transitDistanceKm 0, arrival set from a duration rather than a distance.
const LIVE_MINE = {
  fleetId: '56D5E4', kind: 'Mine', actorTag: 'IMPR',
  originNodeId: 'N113', targetNodeId: 'N113', transitDistanceKm: 0,
  departedUtc: '2026-09-03T20:02:41.4237656Z',
  estimatedArrivalUtc: '2026-09-03T20:05:45.2797656Z',
  completionRatio: 0.8079579486119572,
  currentStopKind: 'Mine',
}
const MINE_MIDPOINT = new Date('2026-09-03T20:04:13.3517656Z').getTime()

// Verbatim from the same endpoint, 2026-09-03T19:20:34Z — the only Engage leg on a 160-fleet board.
const LIVE_ENGAGE = {
  fleetId: 'AB932A', kind: 'Conquer', actorTag: 'SPRT', targetTag: 'IGSL',
  originNodeId: 'N204', targetNodeId: 'N204', transitDistanceKm: 0,
  departedUtc: '2026-09-03T19:18:17.2623043Z',
  estimatedArrivalUtc: '2026-09-03T19:23:17.2623043Z',
  completionRatio: 0.4583254206666667,
  currentStopKind: 'Engage',
}
const ENGAGE_SNAPSHOT = new Date('2026-09-03T19:20:34.7599305Z').getTime()

// Verbatim from the same endpoint, 2026-09-15T19:34:46Z. A Reinforce holds an Engage leg at a node
// it is DEFENDING -- same stop kind, same field shape, opposite meaning. On that 328-fleet board
// four of the six Engage legs were this, so a stop-kind-only reading called two-thirds of the
// fights on the map by the wrong name. Note actorTag === targetTag: SPRT attacking SPRT.
const LIVE_REINFORCE = {
  fleetId: 'D063DF', kind: 'Reinforce', actorTag: 'SPRT', targetTag: 'SPRT',
  originNodeId: 'N209', targetNodeId: 'N209', transitDistanceKm: 0,
  departedUtc: '2026-09-15T19:30:04.7102178Z',
  estimatedArrivalUtc: '2026-09-15T19:35:04.7102178Z',
  completionRatio: 0.9401032753333334,
  currentStopKind: 'Engage',
}
const REINFORCE_SNAPSHOT = new Date('2026-09-15T19:34:46.7412004Z').getTime()

test('a live Mine dwell is not an engagement', () => {
  const E = loadEngagement()
  assert.equal(E.isEngaging(LIVE_MINE), false)
})

test('isEngaging is what stands between a Mine dwell and a working fight timer', () => {
  const E = loadEngagement()
  // The point of the guard: every reading downstream of isEngaging produces a real, correctly
  // formatted, entirely wrong answer for a Mine leg, because the field shape is identical. These
  // assertions document that the numbers ARE plausible — so nothing downstream may be reached
  // without gating on isEngaging first.
  assert.equal(E.engagementTotalMs(LIVE_MINE), 183856)
  const remaining = E.engagementRemainingMs(LIVE_MINE, MINE_MIDPOINT)
  assert.ok(remaining > 0 && remaining < 183856, 'a Mine leg yields a plausible countdown')
  assert.equal(E.formatEngagementRemaining(remaining), '1m 31s')
  const ratio = E.engagementElapsedRatio(LIVE_MINE, MINE_MIDPOINT)
  assert.ok(ratio > 0.49 && ratio < 0.51, 'a Mine leg yields a plausible elapsed ratio')
})

test('a node with only Mine dwells at it reports no attackers', () => {
  const E = loadEngagement()
  assert.deepEqual(E.engagementsAtNode([LIVE_MINE], 'N113'), [])
})

test('every non-Engage stop kind reads as not engaging', () => {
  const E = loadEngagement()
  // Asserted on a fleet whose ACTION kind is an attack, so the stop-kind half of the conjunction
  // is what each case actually exercises.
  for (const kind of ['Service', 'Return', 'Navigation', 'Pickup', 'Mine', 'Intercept', '', 'engage', 'ENGAGE']) {
    assert.equal(
      E.isEngaging(Object.assign({}, LIVE_ENGAGE, { currentStopKind: kind })), false,
      `stop kind '${kind}' must not read as an engagement`)
    assert.equal(
      E.isDwelling(Object.assign({}, LIVE_ENGAGE, { currentStopKind: kind })), false,
      `stop kind '${kind}' must not read as a dwell`)
  }
})

test('a live Reinforce dwell is a defence, not an attack', () => {
  const E = loadEngagement()
  assert.equal(E.isDwelling(LIVE_REINFORCE), true)
  assert.equal(E.isEngaging(LIVE_REINFORCE), false)
  assert.equal(E.isHoldingDefence(LIVE_REINFORCE), true)
  assert.equal(E.dwellColor(LIVE_REINFORCE), E.DEFENCE_HOLD_COLOR)
  assert.equal(E.dwellColor(LIVE_ENGAGE), E.ENGAGEMENT_COLOR)
  assert.equal(E.dwellColor(LIVE_MINE), null)
})

test('the reinforce reading is what stands between a garrison and a phantom raid', () => {
  const E = loadEngagement()
  // Same shape as the Mine guard above, and the same point: every downstream reading produces a
  // real, correctly formatted, entirely wrong answer for a Reinforce, because the leg IS a dwell.
  // Only the action kind separates "SPRT is attacking N209" from "SPRT is defending N209".
  assert.equal(E.engagementTotalMs(LIVE_REINFORCE), 300000)
  const ours = E.engagementElapsedRatio(LIVE_REINFORCE, REINFORCE_SNAPSHOT)
  assert.ok(Math.abs(ours - LIVE_REINFORCE.completionRatio) < 1e-5,
    `client ${ours} vs server ${LIVE_REINFORCE.completionRatio}`)
  assert.equal(E.formatEngagementRemaining(E.engagementRemainingMs(LIVE_REINFORCE, REINFORCE_SNAPSHOT)), '17s')
  assert.equal(LIVE_REINFORCE.actorTag, LIVE_REINFORCE.targetTag,
    'a reinforce targets its own faction — read as an attack it accuses a faction of raiding itself')
})

test('a node with only reinforcements at it reports no attackers', () => {
  const E = loadEngagement()
  assert.deepEqual(E.engagementsAtNode([LIVE_REINFORCE], 'N209'), [])
  assert.deepEqual(E.defencesAtNode([LIVE_REINFORCE], 'N209').map(f => f.fleetId), ['D063DF'])
})

test('the two sides of a contested node are read apart', () => {
  const E = loadEngagement()
  // A raid and a reinforce holding at the same node at the same time: the case the dwell exists
  // for. Each list must contain exactly the one that belongs in it.
  const raid = Object.assign({}, LIVE_ENGAGE, { fleetId: 'RAID', targetNodeId: 'N209' })
  const board = [raid, LIVE_REINFORCE, LIVE_MINE]
  assert.deepEqual(E.engagementsAtNode(board, 'N209').map(f => f.fleetId), ['RAID'])
  assert.deepEqual(E.defencesAtNode(board, 'N209').map(f => f.fleetId), ['D063DF'])
})

test('no other action kind on an Engage leg reads as an attack or a defence', () => {
  const E = loadEngagement()
  for (const kind of ['Trade', 'Haul', 'Mine', 'Guard', 'Patrol', 'Gift', 'raid', 'reinforce', '']) {
    const f = Object.assign({}, LIVE_ENGAGE, { kind })
    assert.equal(E.isEngaging(f), false, `action kind '${kind}' must not read as an attack`)
    assert.equal(E.isHoldingDefence(f), false, `action kind '${kind}' must not read as a defence`)
  }
  // The three Core kinds that do attack, and the one that defends.
  for (const kind of ['Raid', 'Retaliate', 'Conquer']) {
    assert.equal(E.isEngaging(Object.assign({}, LIVE_ENGAGE, { kind })), true, kind)
  }
  assert.equal(E.isHoldingDefence(Object.assign({}, LIVE_ENGAGE, { kind: 'Reinforce' })), true)
})

test('a live Engage dwell reproduces the server\'s own completionRatio', () => {
  const E = loadEngagement()
  assert.equal(E.isEngaging(LIVE_ENGAGE), true)
  assert.equal(E.engagementTotalMs(LIVE_ENGAGE), 300000)
  const ours = E.engagementElapsedRatio(LIVE_ENGAGE, ENGAGE_SNAPSHOT)
  // The server samples this field once when it builds the snapshot; the client re-derives it from
  // the fleet's own clock so it keeps moving between polls. At the sampling instant the two must
  // agree to within the snapshot's own sub-millisecond timestamp precision.
  assert.ok(Math.abs(ours - LIVE_ENGAGE.completionRatio) < 1e-5,
    `client ${ours} vs server ${LIVE_ENGAGE.completionRatio}`)
})

test('the countdown clamps at both ends', () => {
  const E = loadEngagement()
  const before = new Date('2026-09-03T19:00:00Z').getTime()
  const after = new Date('2026-09-03T20:00:00Z').getTime()
  assert.equal(E.engagementElapsedRatio(LIVE_ENGAGE, before), 0)
  assert.equal(E.engagementElapsedRatio(LIVE_ENGAGE, after), 1)
  assert.equal(E.engagementRemainingMs(LIVE_ENGAGE, after), 0)
})

test('the formatter keeps seconds visible for the whole dwell', () => {
  const E = loadEngagement()
  assert.equal(E.formatEngagementRemaining(0), '0s')
  assert.equal(E.formatEngagementRemaining(38000), '38s')
  assert.equal(E.formatEngagementRemaining(161000), '2m 41s')
  assert.equal(E.formatEngagementRemaining(3600000), '1h 0m')
})

test('engagementsAtNode picks only the fights at that node', () => {
  const E = loadEngagement()
  const other = Object.assign({}, LIVE_ENGAGE, { fleetId: 'OTHER', targetNodeId: 'N999' })
  const inbound = Object.assign({}, LIVE_ENGAGE, { fleetId: 'INBOUND', currentStopKind: 'Service' })
  const garrison = Object.assign({}, LIVE_REINFORCE, { fleetId: 'GARRISON', targetNodeId: 'N204' })
  const found = E.engagementsAtNode([LIVE_ENGAGE, other, inbound, LIVE_MINE, garrison], 'N204')
  assert.deepEqual(found.map(f => f.fleetId), ['AB932A'])
})
