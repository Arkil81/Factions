const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/mineCooldown.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadMineCooldown() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-dashboard-test-'))
  try {
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--rootDir', join(dashboardRoot, 'src'), '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'mineCooldown.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

const NOW = '2026-08-31T14:00:00Z'

// The characteristic failure FAC-210 names: a panel that shows a number for EVERY node. 1,418 of
// 1,459 live nodes have never been mined, and the API omits the field for them -- so the only thing
// standing between "18 nodes are locked out" and "every node is locked out" is that these three
// no-cooldown shapes all read null.
test('a node with no active cooldown reads null, however it got there', () => {
  const { mineCooldownRemainingMs } = loadMineCooldown()
  // Never mined: the field is absent from the JSON entirely.
  assert.equal(mineCooldownRemainingMs(undefined, NOW), null)
  assert.equal(mineCooldownRemainingMs(null, NOW), null)
  // Expired: set, in the past. A different fact from never-mined, the same answer.
  assert.equal(mineCooldownRemainingMs('2026-08-31T13:59:00Z', NOW), null)
  // Exactly due is over, not one last tick of cooldown.
  assert.equal(mineCooldownRemainingMs(NOW, NOW), null)
  // DateTime.MinValue must never reach here, but if it did it is a year-1 expiry, not a cooldown.
  assert.equal(mineCooldownRemainingMs('0001-01-01T00:00:00Z', NOW), null)
  // An unparseable deadline is not an infinite one.
  assert.equal(mineCooldownRemainingMs('not a date', NOW), null)
})

test('an active cooldown is measured against the snapshot clock, not the browser', () => {
  const { mineCooldownRemainingMs } = loadMineCooldown()
  // The live max when FAC-210 was filed: IMPR/N223, 24.4 h out.
  assert.equal(mineCooldownRemainingMs('2026-09-01T14:24:00Z', NOW), 24.4 * 3600_000)
  assert.equal(mineCooldownRemainingMs('2026-08-31T14:17:00Z', NOW), 17 * 60_000)
  // Same deadline, an hour-stale snapshot: the answer moves with `utcNow`, so a tab left open does
  // not silently count the cooldown down against a clock the server never saw.
  assert.equal(mineCooldownRemainingMs('2026-08-31T14:17:00Z', '2026-08-31T13:00:00Z'), 77 * 60_000)
})

test('remaining time renders as duration, and hours keep their minutes', () => {
  const { formatCooldownRemaining } = loadMineCooldown()
  assert.equal(formatCooldownRemaining(17 * 60_000), '17m')
  // Rounding up to 60 crosses into the hour branch rather than printing a bare "60m".
  assert.equal(formatCooldownRemaining(59.6 * 60_000), '1h00m')
  // 3h08m, not 3h8m -- the padding is what makes a column of these line up.
  assert.equal(formatCooldownRemaining(3.13 * 3600_000), '3h08m')
  assert.equal(formatCooldownRemaining(24.4 * 3600_000), '24h24m')
})
