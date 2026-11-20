// A player siege, read the way the map and the node panel read it. The failure this guards is not
// a wrong number -- it is a siege that renders as nothing at all, which is what the dashboard did
// through a live twenty-minute breach-and-raid on 2026-09-04 (docs/issues/FAC-258).
const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/nodeSiege.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadSiege() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-node-siege-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'nodeSiege.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

const NOW = new Date('2026-09-04T13:30:00.000Z').getTime()

// Shield half worn down, one minute into a five-minute dwell.
const BESIEGED = {
  integrity: 0.5,
  startedUtc: '2026-09-04T13:29:00.000Z',
  dwellSeconds: 300,
  raidProgress: 0,
}

// Shield down four minutes ago, a quarter of the damage needed already done.
const BREACHED = {
  integrity: 0,
  startedUtc: '2026-09-04T13:20:00.000Z',
  dwellSeconds: 300,
  breachedUtc: '2026-09-04T13:26:00.000Z',
  raidProgress: 0.25,
}

const RAIDED = {
  ...BREACHED,
  raidProgress: 1,
  succeededUtc: '2026-09-04T13:28:00.000Z',
}

test('a node with no siege reads as none, and the three phases are told apart by their stamps', () => {
  const { siegeOf, siegePhase } = loadSiege()

  // The common case, and the one that must stay silent: every node on an ordinary board.
  assert.equal(siegeOf({ siege: null }), null)
  assert.equal(siegeOf({}), null)
  assert.equal(siegeOf({ siege: BESIEGED }), BESIEGED)

  assert.equal(siegePhase(BESIEGED), 'besieged')
  assert.equal(siegePhase(BREACHED), 'breached')
  // succeededUtc outranks breachedUtc: a won raid is still a breached zone, and reporting it as
  // merely breached loses the fact that the node is about to change hands.
  assert.equal(siegePhase(RAIDED), 'raided')
})

test('the breach countdown runs from the siege start and stops existing once the shield is down', () => {
  const { siegeDwellRemainingMs, formatSiegeDuration } = loadSiege()

  // 300 s of dwell, 60 s served.
  assert.equal(siegeDwellRemainingMs(BESIEGED, NOW), 240_000)
  assert.equal(formatSiegeDuration(240_000), '4m 0s')

  // Held past the dwell: the attacker has served their time, so it clamps rather than going
  // negative. This is NOT "the shield has dropped" -- the pool must also be empty at that moment.
  const held = { ...BESIEGED, startedUtc: '2026-09-04T13:20:00.000Z' }
  assert.equal(siegeDwellRemainingMs(held, NOW), 0)

  // After the breach the dwell is spent and a countdown would be a lie about what happens next.
  assert.equal(siegeDwellRemainingMs(BREACHED, NOW), null)
  assert.equal(siegeDwellRemainingMs(RAIDED, NOW), null)
})

test('elapsed time keeps counting through the breach', () => {
  const { siegeElapsedMs, formatSiegeDuration } = loadSiege()

  assert.equal(siegeElapsedMs(BESIEGED, NOW), 60_000)
  // Ten minutes since the first absorbed round, not four since the breach: the question is how long
  // this has been going on unnoticed.
  assert.equal(siegeElapsedMs(BREACHED, NOW), 600_000)
  assert.equal(formatSiegeDuration(600_000), '10m 0s')
  assert.equal(formatSiegeDuration(38_000), '38s')
})

test('the colour separates the phase that can still be denied from the one that cannot', () => {
  const { siegeColor, SIEGE_COLOR, BREACH_COLOR } = loadSiege()

  assert.equal(siegeColor(BESIEGED), SIEGE_COLOR)
  assert.equal(siegeColor(BREACHED), BREACH_COLOR)
  assert.equal(siegeColor(RAIDED), BREACH_COLOR)
  assert.notEqual(SIEGE_COLOR, BREACH_COLOR)
})

test('the summary line names the phase first, and the number that phase is measured by', () => {
  const { siegeSummary } = loadSiege()

  assert.equal(siegeSummary(BESIEGED, NOW), 'shield 50%, breach in 4m 0s')
  assert.equal(siegeSummary(BREACHED, NOW), 'breached — station under fire, 25% to loss')
  assert.match(siegeSummary(RAIDED, NOW), /^raided — conquer clock running/)
})
