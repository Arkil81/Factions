const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/map/fleetEchelon.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadFleetEchelon() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-dashboard-test-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'fleetEchelon.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

const { fleetEchelon, echelonMark } = loadFleetEchelon()

test('a lone hull draws nothing', () => {
  assert.deepEqual(fleetEchelon(1), { glyph: 'none', count: 0 })
  assert.equal(echelonMark(1), '')
})

test('a fleet with no recorded size reads as a lone hull', () => {
  assert.equal(fleetEchelon(0).glyph, 'none')
  assert.equal(fleetEchelon(-3).glyph, 'none')
})

test('at two and three the dots are the hulls', () => {
  assert.deepEqual(fleetEchelon(2), { glyph: 'dot', count: 2 })
  assert.deepEqual(fleetEchelon(3), { glyph: 'dot', count: 3 })
  assert.equal(echelonMark(3).length, 3)
})

test('bands change only at their own boundary', () => {
  for (const [size, expected] of [
    [4, { glyph: 'bar', count: 1 }],
    [6, { glyph: 'bar', count: 1 }],
    [7, { glyph: 'bar', count: 2 }],
    [11, { glyph: 'bar', count: 2 }],
    [12, { glyph: 'bar', count: 3 }],
    [17, { glyph: 'bar', count: 3 }],
    [18, { glyph: 'cross', count: 1 }],
  ]) {
    assert.deepEqual(fleetEchelon(size), expected, `convoy of ${size}`)
  }
})

test('the top band is where a squadron at the hull ceiling lands, and it does not grow past it', () => {
  // RaidSquadronMaxHulls is 24, so the cross is reachable rather than decorative.
  assert.deepEqual(fleetEchelon(24), { glyph: 'cross', count: 1 })
  assert.deepEqual(fleetEchelon(500), { glyph: 'cross', count: 1 })
  assert.equal(echelonMark(500), echelonMark(18))
})

test('the mark never grows with the fleet: at most three of anything', () => {
  for (let size = 0; size <= 200; size++) {
    const mark = echelonMark(size)
    assert.ok(mark.length <= 3, `convoy of ${size} drew ${mark.length} marks`)
  }
})

test('every step up the ladder changes the mark', () => {
  const marks = [1, 2, 3, 4, 7, 12, 18].map(echelonMark)
  assert.equal(new Set(marks).size, marks.length)
})
