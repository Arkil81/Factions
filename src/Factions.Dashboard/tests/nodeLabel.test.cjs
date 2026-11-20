const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/map/nodeLabel.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadNodeLabel() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-dashboard-test-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'nodeLabel.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

function node(overrides) {
  return Object.assign({ dominantOre: '', surveyed: false, capabilities: null, hasStation: false }, overrides)
}

test('the ore outranks everything built on the node', () => {
  const { nodeLabel, nodeIcon } = loadNodeLabel()
  const n = node({ dominantOre: 'Iron', surveyed: true, capabilities: { Shipyard: 1 }, hasStation: true })
  assert.equal(nodeLabel(n), 'Iron')
  assert.equal(nodeIcon(n), '⛏')
})

test('ice and the rare ores are drawn apart from ordinary ore', () => {
  const { nodeIcon, nodeColor } = loadNodeLabel()
  assert.equal(nodeIcon(node({ dominantOre: 'Ice', surveyed: true })), '🧊')
  assert.equal(nodeIcon(node({ dominantOre: 'Platinum', surveyed: true })), '💎')
  assert.notEqual(
    nodeColor(node({ dominantOre: 'Ice', surveyed: true })),
    nodeColor(node({ dominantOre: 'Iron', surveyed: true })),
  )
  assert.notEqual(
    nodeColor(node({ dominantOre: 'Gold', surveyed: true })),
    nodeColor(node({ dominantOre: 'Iron', surveyed: true })),
  )
})

test('capabilities rank by significance, not by the order the server serialised them', () => {
  const { nodeLabel } = loadNodeLabel()
  // SensorArray first in insertion order and least significant of the three: reading the first key
  // would name this node "Sensor array" while the SE client's map calls it "Shipyard".
  const n = node({ surveyed: true, capabilities: { SensorArray: 1, Refinery: 1, Shipyard: 1 } })
  assert.equal(nodeLabel(n), 'Shipyard')
})

test('a capability recorded at level zero is not installed', () => {
  const { nodeLabel, isEmptyNode } = loadNodeLabel()
  const n = node({ surveyed: true, capabilities: { Shipyard: 0 } })
  assert.equal(nodeLabel(n), 'Barren')
  assert.equal(isEmptyNode(n), true)
})

test('a capability this build has never heard of reads as itself, not as barren', () => {
  const { nodeLabel } = loadNodeLabel()
  assert.equal(nodeLabel(node({ surveyed: true, capabilities: { CloningVat: 2 } })), 'Cloning Vat')
})

test('an enum name is spaced into prose', () => {
  const { nodeLabel } = loadNodeLabel()
  assert.equal(nodeLabel(node({ surveyed: true, capabilities: { ManufactoryII: 1 } })), 'Manufactory II')
})

test('a station with barren ground is a station, not empty space', () => {
  const { nodeLabel, isEmptyNode } = loadNodeLabel()
  const n = node({ surveyed: true, hasStation: true })
  assert.equal(nodeLabel(n), 'Station')
  assert.equal(isEmptyNode(n), false)
})

test('barren and unsurveyed are told apart', () => {
  const { nodeLabel, nodeColor } = loadNodeLabel()
  assert.equal(nodeLabel(node({ surveyed: true })), 'Barren')
  assert.equal(nodeLabel(node({ surveyed: false })), 'Unsurveyed')
  assert.notEqual(nodeColor(node({ surveyed: true })), nodeColor(node({ surveyed: false })))
})

test('an unsurveyed node is not empty, so the map does not hide somewhere worth looking', () => {
  const { isEmptyNode } = loadNodeLabel()
  assert.equal(isEmptyNode(node({ surveyed: false })), false)
  assert.equal(isEmptyNode(node({ surveyed: true })), true)
})

test('every derivation returns something renderable for a node with nothing on it', () => {
  const { nodeLabel, nodeColor, nodeIcon } = loadNodeLabel()
  const bare = node({})
  // The defect this replaced: KIND_COLORS[node.kind] + '33' produced the string "undefined33".
  assert.match(nodeColor(bare), /^#[0-9a-fA-F]{6}$/)
  assert.ok(nodeLabel(bare).length > 0)
  assert.ok(nodeIcon(bare).length > 0)
})
