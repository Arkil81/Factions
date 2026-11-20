const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/installBom.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadInstallBom() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-dashboard-test-'))
  try {
    // rootDir pinned to src/ so the emitted path stays src-relative if this module ever imports.
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--rootDir', join(dashboardRoot, 'src'), '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'installBom.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

function row(key, need, reserved, stock, byKind) {
  return { key, need, reserved, stock, byKind }
}

test('a key is only short when staged AND routable stock together fall short', () => {
  const { bomShortfall, bomHave } = loadInstallBom()
  // 340 staged + 80 routable against a need of 500: reserved alone would call this 160 short.
  assert.equal(bomHave(row('Comp:SteelPlate', 500, 340, 80)), 420)
  assert.equal(bomShortfall(row('Comp:SteelPlate', 500, 340, 80)), 80)
  assert.equal(bomShortfall(row('Comp:SteelPlate', 500, 0, 500)), 0)
  assert.equal(bomShortfall(row('Comp:Motor', 20, 0, 0)), 20)
})

test('progress is weighted by need, so a big half-met key outweighs a small satisfied one', () => {
  const { bomProgress } = loadInstallBom()
  const rows = [row('Comp:SteelPlate', 500, 0, 250), row('Comp:MetalGrid', 140, 0, 140), row('Comp:Motor', 20, 0, 20)]
  // Row-averaged would read 83%.
  assert.equal(bomProgress(rows), 410 / 660)
})

test('an overstocked key cannot push progress past one', () => {
  const { bomProgress } = loadInstallBom()
  assert.equal(bomProgress([row('Comp:SteelPlate', 500, 0, 50000), row('Comp:Motor', 20, 0, 20)]), 1)
})

test('an install with no material cost reads as complete, not as zero', () => {
  const { bomProgress } = loadInstallBom()
  assert.equal(bomProgress(undefined), 1)
  assert.equal(bomProgress([]), 1)
})

test('reserved stock is attributed to the install whose BOM names the key', () => {
  const { groupReservedByInstall } = loadInstallBom()
  const investments = [
    { cap: 'Shipyard', bom: [row('Comp:SteelPlate', 500, 340, 80), row('Comp:Motor', 20, 0, 0)] },
  ]
  const groups = groupReservedByInstall({ 'Comp:SteelPlate': 340 }, investments)

  assert.equal(groups.length, 1)
  assert.deepEqual(groups[0].caps, ['Shipyard'])
  assert.equal(groups[0].byKind, false)
  assert.equal(groups[0].rows[0].here, 340)
  // The row carried alongside is the faction-wide claim, not this node's slice.
  assert.equal(groups[0].rows[0].row.need, 500)
})

test('an exact BOM key wins over a flat cost that only wants the kind', () => {
  const { groupReservedByInstall } = loadInstallBom()
  const investments = [
    { cap: 'Refinery', bom: [row('ProcessedGood', 300, 0, 0, true)] },
    { cap: 'Shipyard', bom: [row('Comp:SteelPlate', 500, 340, 0)] },
  ]
  const groups = groupReservedByInstall({ 'Comp:SteelPlate': 340 }, investments)

  assert.equal(groups.length, 1)
  assert.deepEqual(groups[0].caps, ['Shipyard'])
})

test('a flat-cost install claims by ItemKind bucket when no BOM names the key', () => {
  const { groupReservedByInstall } = loadInstallBom()
  const investments = [{ cap: 'Refinery', bom: [row('Ore', 300, 12, 0, true)] }]
  const groups = groupReservedByInstall({ 'Ore:Iron': 12 }, investments)

  assert.equal(groups.length, 1)
  assert.equal(groups[0].byKind, true)
  assert.deepEqual(groups[0].caps, ['Refinery'])
})

test('stock no active request claims is grouped as unclaimed, and sorts last', () => {
  const { groupReservedByInstall } = loadInstallBom()
  const investments = [{ cap: 'Shipyard', bom: [row('Comp:SteelPlate', 500, 340, 0)] }]
  const groups = groupReservedByInstall({ 'Comp:SmallTube': 750, 'Comp:SteelPlate': 340 }, investments)

  assert.equal(groups.length, 2)
  assert.deepEqual(groups[0].caps, ['Shipyard'])
  assert.deepEqual(groups[1].caps, [])
  assert.equal(groups[1].rows[0].key, 'Comp:SmallTube')
  assert.equal(groups[1].rows[0].row, undefined)
})

test('with no live snapshot every key reads as unclaimed rather than vanishing', () => {
  const { groupReservedByInstall } = loadInstallBom()
  const groups = groupReservedByInstall({ 'Comp:SteelPlate': 340, 'Comp:Motor': 4 }, [])

  assert.equal(groups.length, 1)
  assert.deepEqual(groups[0].caps, [])
  assert.deepEqual(groups[0].rows.map(r => r.key), ['Comp:SteelPlate', 'Comp:Motor'])
})

test('a zero-quantity reserved key is not rendered as a claim', () => {
  const { groupReservedByInstall } = loadInstallBom()
  assert.deepEqual(groupReservedByInstall({ 'Comp:SteelPlate': 0 }, []), [])
})

test('the kind buckets match the ones ProductionManager reserves into', () => {
  const { itemKindOfInvKey } = loadInstallBom()
  assert.equal(itemKindOfInvKey('Ore:Iron'), 'Ore')
  assert.equal(itemKindOfInvKey('Ingot:Iron'), 'Ingot')
  assert.equal(itemKindOfInvKey('Comp:SteelPlate'), 'ProcessedGood')
  assert.equal(itemKindOfInvKey('Gas:Oxygen'), 'Oxygen')
  assert.equal(itemKindOfInvKey('Gas:Hydrogen'), 'Hydrogen')
})

test('only the inventory-key prefix is stripped, never part of the name', () => {
  const { stripInvKeyPrefix } = loadInstallBom()
  assert.equal(stripInvKeyPrefix('Comp:SteelPlate'), 'SteelPlate')
  assert.equal(stripInvKeyPrefix('ProcessedGood'), 'ProcessedGood')
  assert.equal(stripInvKeyPrefix('Ore:Iron'), 'Iron')
})
