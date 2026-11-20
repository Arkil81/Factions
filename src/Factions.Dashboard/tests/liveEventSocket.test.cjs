const assert = require('node:assert/strict')
const { execFileSync } = require('node:child_process')
const { mkdtempSync, rmSync } = require('node:fs')
const { tmpdir } = require('node:os')
const { join, resolve } = require('node:path')
const test = require('node:test')

const dashboardRoot = resolve(__dirname, '..')
const source = join(dashboardRoot, 'src/api/liveEventSocket.ts')
const tsc = join(dashboardRoot, 'node_modules/.bin/tsc')

function loadSocket() {
  const outputDir = mkdtempSync(join(tmpdir(), 'factions-live-event-socket-'))
  try {
    // rootDir pinned to src/ so the emitted path is src-relative whatever else the module imports;
    // without it tsc infers the root from the input set and moves the output when an import is added.
    execFileSync(tsc, [source, '--module', 'commonjs', '--target', 'es2020', '--skipLibCheck', '--rootDir', join(dashboardRoot, 'src'), '--outDir', outputDir], { cwd: dashboardRoot, stdio: 'pipe' })
    return require(join(outputDir, 'api/liveEventSocket.js'))
  } finally {
    rmSync(outputDir, { recursive: true, force: true })
  }
}

/** Stands in for the browser WebSocket; `deliver` pushes one frame the way the server would. */
function fakeSocket() {
  const ws = {
    closed: false,
    onopen: null,
    onclose: null,
    onerror: null,
    onmessage: null,
    close() { this.closed = true },
    deliver(payload) { this.onmessage?.({ data: typeof payload === 'string' ? payload : JSON.stringify(payload) }) },
  }
  return ws
}

function sink() {
  const events = []
  const connected = []
  return {
    events,
    connected,
    onEvent: e => events.push(e),
    onConnectedChange: c => connected.push(c),
  }
}

function demands(tag) {
  return { type: 'demands', at: '2026-08-22T10:00:00.000Z', factionTag: tag, demands: [], investments: [] }
}

// The FAC-001 regression: one flush sweep delivers a snapshot per faction and then a heartbeat, all
// back to back. Every one of them must reach the sink -- the last faction's snapshot in particular,
// which is the one immediately followed by the heartbeat.
test('every event in a burst is delivered, including the one before the heartbeat', () => {
  const { connectLiveEvents } = loadSocket()
  const ws = fakeSocket()
  const s = sink()
  connectLiveEvents('ws://x/ws/live', s, { createSocket: () => ws })

  const tags = ['AAA', 'BBB', 'CCC', 'DDD', 'EXP']
  for (const tag of tags) ws.deliver(demands(tag))
  ws.deliver({ type: 'heartbeat', at: '2026-08-22T10:00:00.100Z', simTick: 42 })

  assert.equal(s.events.length, tags.length + 1)
  assert.deepEqual(s.events.filter(e => e.type === 'demands').map(e => e.factionTag), tags)
  assert.equal(s.events[s.events.length - 1].type, 'heartbeat')
})

test('a malformed frame is skipped without breaking the stream', () => {
  const { connectLiveEvents } = loadSocket()
  const ws = fakeSocket()
  const s = sink()
  connectLiveEvents('ws://x/ws/live', s, { createSocket: () => ws })

  ws.deliver(demands('AAA'))
  ws.deliver('{"type":"demands","factionTag":')  // truncated line
  ws.deliver(demands('BBB'))

  assert.deepEqual(s.events.map(e => e.factionTag), ['AAA', 'BBB'])
})

test('connection state is reported and a drop schedules a reconnect', () => {
  const { connectLiveEvents } = loadSocket()
  const sockets = []
  const s = sink()
  const scheduled = []
  connectLiveEvents('ws://x/ws/live', s, {
    createSocket: () => { const ws = fakeSocket(); sockets.push(ws); return ws },
    reconnectDelayMs: 3000,
    schedule: (fn, ms) => { scheduled.push({ fn, ms }); return scheduled.length },
    cancel: () => {},
  })

  sockets[0].onopen()
  assert.deepEqual(s.connected, [true])

  sockets[0].onclose()
  assert.deepEqual(s.connected, [true, false])
  assert.equal(scheduled.length, 1)
  assert.equal(scheduled[0].ms, 3000)

  scheduled[0].fn()
  assert.equal(sockets.length, 2)
  sockets[1].deliver(demands('AAA'))
  assert.deepEqual(s.events.map(e => e.factionTag), ['AAA'])
})

test('disposing stops delivery and prevents a queued reconnect', () => {
  const { connectLiveEvents } = loadSocket()
  const sockets = []
  const s = sink()
  const scheduled = []
  const cancelled = []
  const dispose = connectLiveEvents('ws://x/ws/live', s, {
    createSocket: () => { const ws = fakeSocket(); sockets.push(ws); return ws },
    schedule: (fn, ms) => { scheduled.push({ fn, ms }); return scheduled.length },
    cancel: h => cancelled.push(h),
  })

  sockets[0].onclose()
  dispose()

  assert.equal(sockets[0].closed, true)
  assert.deepEqual(cancelled, [1])

  scheduled[0].fn()                 // a reconnect that fired anyway must not open a second socket
  assert.equal(sockets.length, 1)
  sockets[0].deliver(demands('AAA'))
  assert.equal(s.events.length, 0)
})
