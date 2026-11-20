const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/navigation/fleetNavigation.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadNavigation() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-dashboard-test-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'fleetNavigation.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

test('opening a fleet from a faction detail retains that detail as the back destination', () => {
  const { openFleetDetail, returnFromFleetDetail } = loadNavigation()
  const faction = { kind: 'faction', id: 'SPRT' }
  const navigation = openFleetDetail('fleet-42', faction)
  assert.deepEqual(navigation, { selection: { kind: 'fleet', id: 'fleet-42' }, returnSelection: faction })
  assert.deepEqual(returnFromFleetDetail(navigation.returnSelection), faction)
})

test('opening a fleet from the map has no back destination', () => {
  const { openFleetDetail, returnFromFleetDetail } = loadNavigation()
  const navigation = openFleetDetail('fleet-42', null)
  assert.equal(navigation.returnSelection, null)
  assert.equal(returnFromFleetDetail(navigation.returnSelection), null)
})
