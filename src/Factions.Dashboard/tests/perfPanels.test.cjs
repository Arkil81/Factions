const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, readFileSync, rmSync } = require('node:fs')
const { join, resolve } = require('node:path')
const test = require('node:test')

const React = require('react')
const { renderToStaticMarkup } = require('react-dom/server')

const dashboardRoot = resolve(__dirname, '..')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

// A real 180 s window off the live server, 2026-09-09T15:48:47Z: 13 factions, 799 owned nodes, an
// operations sweep that walked 87,117 ship-suitability checks. Rendering the panels against a
// hand-built object proves only that they render the object that was built for them.
const PERF = JSON.parse(readFileSync(join(__dirname, 'fixtures/perf-window.json'), 'utf8'))

// Compiled inside the package, not in the system temp dir: the emitted modules require react and
// react-dom, which only resolve from a directory under this package's node_modules.
function renderPanels(perf) {
  const outputDir = mkdtempSync(join(dashboardRoot, '.test-'))
  try {
    execFileSync(tsc, [
      join(dashboardRoot, 'src/components/PerfView.tsx'),
      join(dashboardRoot, 'src/components/PerfGlance.tsx'),
      '--module', 'commonjs', '--target', 'es2020', '--jsx', 'react-jsx',
      '--esModuleInterop', '--skipLibCheck',
      '--rootDir', join(dashboardRoot, 'src'), '--outDir', outputDir,
    ], { cwd: dashboardRoot, stdio: 'pipe' })
    const view = require(join(outputDir, 'components/PerfView.js')).default
    const glance = require(join(outputDir, 'components/PerfGlance.js')).default
    return {
      view: renderToStaticMarkup(React.createElement(view, { perf })),
      glance: perf ? renderToStaticMarkup(React.createElement(glance, { perf })) : '',
    }
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

const rendered = renderPanels(PERF)

test('the glance strip carries the frame contract and nothing else', () => {
  // The map corner takes no pointer events and can only ever be as tall as the map, so its content
  // is the one reading that has to be there without asking: the frame against its budget.
  const html = rendered.glance
  assert.match(html, /FRAME/)
  assert.match(html, /0\.40/)          // frame.avgMs
  assert.match(html, /0\.50ms budget/) // frame.budgetMs
  assert.match(html, /918\/10,800/)    // overBudgetCount / calls

  // Everything else moved. A manager bucket, a scheduler counter and a telemetry counter, none of
  // which the strip may grow back.
  for (const moved of ['astObs', 'Denied by budget', 'ship suitability checks']) {
    assert.equal(html.includes(moved), false, moved + ' belongs in the Perf view, not the strip')
  }
})

test('the perf view draws every block the wire carries', () => {
  const html = rendered.view
  for (const section of ['Frame contract', 'Plugin actors', 'Host frame actors', 'Brain phases',
                         'Econ phases', 'Persistence', 'Scheduler',
                         'Production sweep', 'Operations sweep', 'Trade sweep']) {
    assert.ok(html.includes(section), 'missing section: ' + section)
  }

  // The blocks this panel served and never drew: an ops phase timing, an ops counter, a trade
  // counter, a think stage, a scheduler scalar, an actor's denials and worst deferral, a host
  // bucket, and the production residue.
  for (const value of ['alternate pickup', '87,117', 'refused consent', 'threat bonus',
                       'No-stack ratio', '2,585', '9.6s', 'feature rock', 'recipes skipped no demand']) {
    assert.ok(html.includes(value), 'missing reading: ' + value)
  }

  // Column headers, on every table.
  for (const header of ['Total ms', 'Avg ms', 'Calls', 'Est err avg', 'Max deferral', 'Budget']) {
    assert.ok(html.includes(header), 'missing column header: ' + header)
  }
})

test('the view renders a payload from a host build that omits the newer blocks', () => {
  // /api/perf passes perf.json through verbatim, so during a deploy window the panel is handed
  // whatever the previous build wrote. Every block the older build never had must be absent from the
  // page rather than a crash on it.
  const older = JSON.parse(JSON.stringify(PERF))
  for (const key of ['brainPhases', 'astObs', 'flight', 'econPhases', 'host', 'frame',
                     'operationsTelemetry', 'tradeTelemetry', 'windowTargetSeconds']) {
    delete older[key]
  }
  delete older.scheduler.actors
  delete older.scheduler.rechunkQueue
  delete older.scheduler.noStackRatio

  const html = renderPanels(older)
  assert.ok(html.view.includes('Plugin actors'))
  assert.equal(html.view.includes('Brain phases'), false)
  assert.equal(html.view.includes('Operations sweep'), false)
  assert.match(html.glance, /frame timing not reported by this build/)
})

test('an absent snapshot is a waiting message, not a blank tab', () => {
  assert.match(renderPanels(null).view, /Waiting for a perf window/)
})
