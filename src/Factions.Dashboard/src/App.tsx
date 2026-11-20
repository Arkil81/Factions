import React, { useRef, useEffect, useState, useCallback } from 'react'
import { useWorld } from './store/useWorld'
import { SceneManager } from './map/SceneManager'
import Sidebar from './components/Sidebar'
import NodePanel from './components/panels/NodePanel'
import FleetPanel from './components/panels/FleetPanel'
import FactionPanel from './components/panels/FactionPanel'
import RelationsMatrix from './components/RelationsMatrix'
import BankOverview from './components/BankOverview'
import PerfGlance from './components/PerfGlance'
import PerfView from './components/PerfView'
import { useWebSocket } from './hooks/useWebSocket'
import type { DemandsEvent, LiveEvent } from './hooks/useWebSocket'
import type { NodeDto, FleetDto, FactionDto, CampaignFocusDto, PerfSnapshot, PrefabCatalogue, OnlinePlayerDto } from './types/world'
import { fetchPerf, fetchPrefabs, fetchPlayers } from './api/client'
import { DEFAULT_BLOB_PARAMS } from './map/BlobTerritoryRenderer'
import type { BlobParams } from './map/BlobTerritoryRenderer'
import { openFleetDetail, returnFromFleetDetail } from './navigation/fleetNavigation'
import type { PanelSelection } from './navigation/fleetNavigation'
import { useAuth } from './hooks/useAuth'

type ViewMode = 'map' | 'relations' | 'bank' | 'perf'

const VIEW_LABELS: Record<ViewMode, string> = {
  map: '3D Map',
  relations: 'Relations',
  bank: 'Central Bank',
  perf: 'Perf',
}

function CommandBar() {
  const [cmd, setCmd] = useState('')
  const [status, setStatus] = useState<'idle' | 'ok' | 'err'>('idle')
  const timerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null)

  const send = async () => {
    const trimmed = cmd.trim()
    if (!trimmed) return
    try {
      const res = await fetch('/api/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command: trimmed }),
      })
      setStatus(res.ok ? 'ok' : 'err')
      if (res.ok) setCmd('')
    } catch {
      setStatus('err')
    }
    if (timerRef.current) clearTimeout(timerRef.current)
    timerRef.current = setTimeout(() => setStatus('idle'), 2500)
  }

  return (
    <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
      <input
        value={cmd}
        onChange={e => setCmd(e.target.value)}
        onKeyDown={e => e.key === 'Enter' && send()}
        placeholder="bridge command..."
        style={{
          background: 'var(--surface-2, #161b22)',
          border: '1px solid var(--border)',
          borderRadius: 4,
          padding: '3px 7px',
          fontSize: 11,
          color: 'var(--text-1)',
          outline: 'none',
          width: 160,
        }}
      />
      <button
        onClick={send}
        style={{
          background: 'none',
          border: '1px solid var(--border)',
          borderRadius: 4,
          padding: '3px 8px',
          cursor: 'pointer',
          fontSize: 11,
          color: status === 'ok' ? '#2ecc71' : status === 'err' ? '#e74c3c' : 'var(--text-2)',
        }}
      >
        {status === 'ok' ? 'OK' : status === 'err' ? 'ERR' : 'SEND'}
      </button>
    </div>
  )
}

function AuthBadge() {
  const auth = useAuth()
  if (!auth) return null

  if (!auth.authenticated) {
    return (
      <a
        href="/auth/login"
        style={{
          fontSize: 11,
          color: 'var(--accent, #58a6ff)',
          textDecoration: 'none',
          border: '1px solid var(--border)',
          borderRadius: 4,
          padding: '3px 8px',
        }}
      >
        Sign in via Steam
      </a>
    )
  }

  return (
    <span
      title={`SteamID ${auth.steamId}${auth.promoteLevel ? ` / ${auth.promoteLevel}` : ''}`}
      style={{ fontSize: 11, color: 'var(--text-2)', display: 'inline-flex', gap: 6, alignItems: 'center' }}
    >
      <span style={{ color: 'var(--text-1)' }}>
        {auth.name ?? auth.steamId}
      </span>
      {auth.isAdmin && <span style={{ color: '#f1c40f' }}>admin</span>}
      <a href="/auth/logout" style={{ color: 'var(--text-2)', fontSize: 10 }}>logout</a>
    </span>
  )
}

type Selection = PanelSelection

function DataAgeIndicator({ dataAsOf }: { dataAsOf: string | null }) {
  const [label, setLabel] = React.useState('')
  const [stale, setStale] = React.useState(false)

  React.useEffect(() => {
    function update() {
      if (!dataAsOf) { setLabel('no data files'); setStale(true); return }
      const diffMs = Date.now() - new Date(dataAsOf).getTime()
      const diffMin = diffMs / 60_000
      if (diffMin < 2) { setLabel('data current'); setStale(false) }
      else if (diffMin < 60) { setLabel(`data ${Math.round(diffMin)}m old`); setStale(diffMin > 10) }
      else { setLabel(`data ${Math.round(diffMin / 60)}h old`); setStale(true) }
    }
    update()
    const t = setInterval(update, 15_000)
    return () => clearInterval(t)
  }, [dataAsOf])

  return (
    <span
      title={dataAsOf ? `Brain files last written: ${new Date(dataAsOf).toLocaleString()}` : 'No brain files found'}
      style={{ fontSize: 11, color: stale ? '#e67e22' : 'var(--text-2)' }}
    >
      {stale ? 'WARN ' : ''}{label}
    </span>
  )
}

// ---------------------------------------------------------------------------
// Blob tuning overlay
// ---------------------------------------------------------------------------

type SliderDef = { key: keyof BlobParams; label: string; min: number; max: number; step: number }

const BLOB_SLIDERS: SliderDef[] = [
  { key: 'sigmaScale',    label: 'Sigma Scale',      min: 0.5,  max: 4.0,  step: 0.1  },
  { key: 'maxSearchDist', label: 'Max Search (km)',  min: 50,   max: 1500, step: 50   },
  { key: 'isolatedSigma', label: 'Isolated σ (km)',  min: 10,   max: 300,  step: 10   },
  { key: 'cullDist',      label: 'Cull Dist (km)',   min: 200,  max: 5000, step: 100  },
  { key: 'threshold',     label: 'Threshold',        min: 0.05, max: 0.9,  step: 0.05 },
  { key: 'marchSteps',   label: 'March Steps',      min: 8,    max: 48,   step: 4    },
  { key: 'alpha',        label: 'Alpha',            min: 0.05, max: 1.0,  step: 0.05 },
  { key: 'gapMargin',   label: 'Gap Margin',       min: 0.5,  max: 4.0,  step: 0.1  },
]

function BlobTuningPanel({
  params, open, onToggle, onChange,
}: {
  params: BlobParams
  open: boolean
  onToggle: () => void
  onChange: (p: BlobParams) => void
}) {
  const panelStyle: React.CSSProperties = {
    position: 'absolute', bottom: 40, right: 12, zIndex: 50,
    background: 'rgba(13,17,23,0.92)', border: '1px solid #30363d',
    borderRadius: 8, fontFamily: 'monospace', fontSize: 11, color: '#cdd9e5',
    minWidth: 240,
  }
  const btnStyle: React.CSSProperties = {
    background: 'none', border: 'none', color: '#58a6ff', cursor: 'pointer',
    padding: '6px 10px', width: '100%', textAlign: 'left', fontSize: 11,
  }
  return (
    <div style={panelStyle}>
      <button style={btnStyle} onClick={onToggle}>
        {open ? '▾' : '▸'} Blob Tuning
      </button>
      {open && (
        <div style={{ padding: '0 10px 10px' }}>
          {BLOB_SLIDERS.map(({ key, label, min, max, step }) => (
            <div key={key} style={{ marginBottom: 6 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                <span>{label}</span>
                <span style={{ color: '#79c0ff' }}>{Number(params[key]).toFixed(step < 1 ? 2 : 0)}</span>
              </div>
              <input
                type="range" min={min} max={max} step={step}
                value={params[key] as number}
                style={{ width: '100%', accentColor: '#58a6ff' }}
                onChange={e => onChange({ ...params, [key]: parseFloat(e.target.value) })}
              />
            </div>
          ))}
          <button
            style={{ ...btnStyle, marginTop: 4, border: '1px solid #30363d', borderRadius: 4, textAlign: 'center' }}
            onClick={() => onChange(DEFAULT_BLOB_PARAMS)}
          >
            Reset defaults
          </button>
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------------------

export default function App() {
  const { world, error, loading, lastFetch, refresh } = useWorld()
  const mapRef = useRef<HTMLDivElement>(null)
  const sceneRef = useRef<SceneManager | null>(null)

  const [visibleFactions, setVisibleFactions] = useState<Set<string>>(new Set())
  const [showEmpty, setShowEmpty] = useState(true)
  const [showRoutes, setShowRoutes] = useState(true)
  // Off by default: the raymarched territory-blob pass has a known per-frame
  // performance cost (see SceneManager's animate()) — opt-in until there's a
  // cheaper rendering approach.
  const [showBlobs, setShowBlobs] = useState(false)
  const [showPlanets, setShowPlanets] = useState(true)
  const [selection, setSelection] = useState<Selection | null>(null)
  const [fleetReturnSelection, setFleetReturnSelection] = useState<Selection | null>(null)
  const [viewMode, setViewMode] = useState<ViewMode>('map')

  const [liveDemands, setLiveDemands] = useState<Map<string, DemandsEvent>>(new Map())

  const [blobParams, setBlobParams] = useState<BlobParams>(DEFAULT_BLOB_PARAMS)
  const [showBlobPanel, setShowBlobPanel] = useState(false)

  const [orbitPlanet, setOrbitPlanet] = useState<string | null>(null)

  const [perf, setPerf] = useState<PerfSnapshot | null>(null)
  useEffect(() => {
    // Poll faster than the window is written. At an equal period the client drifts against the
    // writer and reads some windows twice while skipping others entirely; the `at` guard keeps the
    // extra polls from re-rendering when the file has not moved.
    const accept = (next: PerfSnapshot | null) =>
      setPerf(prev => (next && (!prev || prev.at !== next.at) ? next : prev))
    fetchPerf().then(accept)
    const t = setInterval(() => fetchPerf().then(accept), 20_000)
    return () => clearInterval(t)
  }, [])

  // Online players poll matches the plugin's 10s players.json write cadence.
  const [players, setPlayers] = useState<OnlinePlayerDto[]>([])
  useEffect(() => {
    fetchPlayers().then(setPlayers)
    const t = setInterval(() => fetchPlayers().then(setPlayers), 10_000)
    return () => clearInterval(t)
  }, [])

  useEffect(() => {
    sceneRef.current?.updatePlayers(players)
  }, [players, world])

  const selectPlayer = useCallback((p: OnlinePlayerDto) => {
    if (!p.hasPosition) return
    setViewMode('map')
    sceneRef.current?.focusPosition(p.x, p.y, p.z)
  }, [])

  // Fetched once, not polled — the prefab catalogue only changes on a server-side rescan.
  const [prefabs, setPrefabs] = useState<PrefabCatalogue>({})
  useEffect(() => {
    fetchPrefabs().then(setPrefabs)
  }, [])

  useEffect(() => {
    if (!mapRef.current) return
    const sm = new SceneManager(mapRef.current)
    sceneRef.current = sm
    sm.onNodeClick = (id) => setSelection({ kind: 'node', id })
    sm.onFleetClick = (id) => setSelection({ kind: 'fleet', id })
    sm.onOrbitChange = setOrbitPlanet
    return () => sm.dispose()
  }, [])

  useEffect(() => {
    if (!world) return
    setVisibleFactions(prev => {
      if (prev.size > 0) return prev
      return new Set(world.factions.map(f => f.tag))
    })
  }, [world])

  useEffect(() => {
    if (!world || !sceneRef.current) return
    sceneRef.current.updateScene(world, visibleFactions, showEmpty, blobParams, showBlobs, showPlanets)
  }, [world, visibleFactions, showEmpty, blobParams, showBlobs, showPlanets])

  useEffect(() => {
    sceneRef.current?.setRoutesVisible(showRoutes)
  }, [showRoutes, world])

  useEffect(() => {
    if (!sceneRef.current) return
    sceneRef.current.setSelected(
      selection?.kind === 'faction' ? null : (selection?.kind ?? null),
      selection?.kind === 'faction' ? null : (selection?.id ?? null),
    )
  }, [selection])

  // A flush sweep delivers one event per faction plus a heartbeat back to back, and every
  // fleet dispatch arrives on its own -- coalesce the world refetch those trigger into one trailing
  // call rather than one fetch per event.
  const refreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const scheduleRefresh = useCallback(() => {
    if (refreshTimerRef.current) return
    refreshTimerRef.current = setTimeout(() => {
      refreshTimerRef.current = null
      refresh()
    }, 250)
  }, [refresh])
  useEffect(() => () => {
    if (refreshTimerRef.current) clearTimeout(refreshTimerRef.current)
  }, [])

  // Applied per event as it arrives, never off a "last event" value: two events committed in one
  // render batch would leave the first unobserved, which permanently hid the roster's last faction
  // (FAC-001).
  const handleLiveEvent = useCallback((evt: LiveEvent) => {
    if (evt.type === 'demands') {
      setLiveDemands(prev => {
        const next = new Map(prev)
        next.set(evt.factionTag, evt)
        return next
      })
    } else if (evt.type === 'fleet_dispatch' || evt.type === 'heartbeat') {
      scheduleRefresh()
    }
  }, [scheduleRefresh])

  const { connected: wsConnected } = useWebSocket('/ws/live', handleLiveEvent)

  const toggleFaction = useCallback((tag: string) => {
    setVisibleFactions(prev => {
      const next = new Set(prev)
      if (next.has(tag)) next.delete(tag)
      else next.add(tag)
      return next
    })
  }, [])

  const selectFaction = useCallback((tag: string) => {
    setFleetReturnSelection(null)
    setSelection({ kind: 'faction', id: tag })
  }, [])

  const selectNode = useCallback((id: string) => {
    setFleetReturnSelection(null)
    setSelection({ kind: 'node', id })
    if (sceneRef.current && world) sceneRef.current.focusNode(id, world.nodes)
  }, [world])

  const selectCampaignFocus = useCallback((focus: CampaignFocusDto) => {
    setViewMode('map')
    if (sceneRef.current && world) sceneRef.current.focusCampaignRegion(focus, world.nodes)
  }, [world])

  const selectFleet = useCallback((id: string) => {
    const navigation = openFleetDetail(id, null)
    setFleetReturnSelection(navigation.returnSelection)
    setSelection(navigation.selection)
    if (sceneRef.current && world) sceneRef.current.focusFleet(id, world.fleets)
  }, [world])

  const selectFleetFromFaction = useCallback((id: string) => {
    const navigation = openFleetDetail(id, selection)
    setFleetReturnSelection(navigation.returnSelection)
    setSelection(navigation.selection)
    if (sceneRef.current && world) sceneRef.current.focusFleet(id, world.fleets)
  }, [selection, world])

  const returnToFleetList = useCallback(() => {
    setSelection(returnFromFleetDetail(fleetReturnSelection))
    setFleetReturnSelection(null)
  }, [fleetReturnSelection])

  const closePanel = useCallback(() => {
    setFleetReturnSelection(null)
    setSelection(null)
  }, [])

  const selectedNode: NodeDto | null =
    selection?.kind === 'node' && world ? world.nodes.find(n => n.id === selection.id) ?? null : null
  const selectedFleet: FleetDto | null =
    selection?.kind === 'fleet' && world ? world.fleets.find(f => f.fleetId === selection.id) ?? null : null
  const selectedFaction: FactionDto | null =
    selection?.kind === 'faction' && world ? world.factions.find(f => f.tag === selection.id) ?? null : null

  const factions = world?.factions ?? []
  const selectedFactionTag =
    selection?.kind === 'faction' ? selection.id
    : selectedNode?.ownerTag ?? null

  return (
    <div className="app">
      <Sidebar
        factions={factions}
        players={players}
        onSelectPlayer={selectPlayer}
        visibleFactions={visibleFactions}
        selectedFactionTag={selectedFactionTag}
        onToggleVisible={toggleFaction}
        onSelectFaction={selectFaction}
        showEmpty={showEmpty}
        onToggleEmpty={() => setShowEmpty(v => !v)}
        showRoutes={showRoutes}
        onToggleRoutes={() => setShowRoutes(v => !v)}
        showBlobs={showBlobs}
        onToggleBlobs={() => setShowBlobs(v => !v)}
        showPlanets={showPlanets}
        onTogglePlanets={() => setShowPlanets(v => !v)}
      />

      <div className="map-wrap">
        <div className="header">
          <h1>Factions Operator Dashboard</h1>
          {(['map', 'relations', 'bank', 'perf'] as ViewMode[]).map(v => (
            <button
              key={v}
              onClick={() => setViewMode(v)}
              style={{
                background: 'none',
                border: 'none',
                cursor: 'pointer',
                padding: '3px 10px',
                fontSize: 12,
                fontWeight: viewMode === v ? 600 : 400,
                color: viewMode === v ? 'var(--text-1)' : 'var(--text-2)',
                borderBottom: viewMode === v ? '2px solid var(--accent)' : '2px solid transparent',
              }}
            >
              {VIEW_LABELS[v]}
            </button>
          ))}
          <span style={{ flex: 1 }} />
          {loading && <span className="status-ok">Loading...</span>}
          {error && <span className="status-err">WARN {error}</span>}
          {!loading && !error && <span className="status-ok">LIVE</span>}
          <span
            title={wsConnected ? 'Live events connected' : 'Live events disconnected - reconnecting...'}
            style={{ fontSize: 10, color: wsConnected ? '#2ecc71' : '#e67e22' }}
          >
            {wsConnected ? 'live' : 'live?'}
          </span>
          {world && <span style={{ color: 'var(--text-2)', fontSize: 11 }}>
            {world.nodes.length} nodes / {world.fleets.length} fleets
          </span>}
          {world && <DataAgeIndicator dataAsOf={world.dataAsOf} />}
          {lastFetch && (
            <span className="last-fetch">
              Polled {lastFetch.toLocaleTimeString()}
            </span>
          )}
          <AuthBadge />
          <CommandBar />
          <button className="btn-refresh" onClick={refresh}>Refresh</button>
        </div>

        <div ref={mapRef} style={{ width: '100%', height: '100%', display: viewMode === 'map' ? 'block' : 'none' }} />

        {viewMode === 'relations' && world && (
          <RelationsMatrix world={world} onSelectFaction={selectFaction} />
        )}
        {viewMode === 'relations' && !world && (
          <div style={{ padding: 24, color: 'var(--text-2)' }}>Waiting for data...</div>
        )}

        {viewMode === 'bank' && world && (
          <BankOverview bank={world.centralBank} factions={world.factions} />
        )}
        {viewMode === 'bank' && !world && (
          <div style={{ padding: 24, color: 'var(--text-2)' }}>Waiting for data...</div>
        )}

        {viewMode === 'perf' && <PerfView perf={perf} />}

        {viewMode === 'map' && world && world.nodes.length > 0 && !orbitPlanet && (
          <div className="map-hint">Click a node, fleet, or planet / Scroll to zoom / Drag to orbit</div>
        )}
        {viewMode === 'map' && orbitPlanet && (
          <div className="map-hint">
            Orbiting <strong>{orbitPlanet}</strong> / Drag to orbit / Right-drag to tilt orbit / Scroll to zoom / Middle-click to release
          </div>
        )}
        {viewMode === 'map' && world && world.nodes.length === 0 && (
          <div className="map-hint">No nodes found - run <code>factions init</code> then <code>factions scan</code> on the server</div>
        )}
        {viewMode === 'map' && perf && <PerfGlance perf={perf} />}
        {viewMode === 'map' && (
          <BlobTuningPanel
            params={blobParams}
            open={showBlobPanel}
            onToggle={() => setShowBlobPanel(v => !v)}
            onChange={setBlobParams}
          />
        )}
      </div>

      {selectedNode && world && (
        <NodePanel
          node={selectedNode}
          world={world}
          onClose={closePanel}
          onSelectFaction={selectFaction}
          onSelectFleet={selectFleet}
          liveDemandsEvent={selectedNode.ownerTag ? liveDemands.get(selectedNode.ownerTag) ?? null : null}
        />
      )}
      {selectedFleet && world && !selectedNode && (
        <FleetPanel
          fleet={selectedFleet}
          world={world}
          prefabs={prefabs}
          onBack={fleetReturnSelection ? returnToFleetList : undefined}
          onClose={closePanel}
          onSelectFaction={selectFaction}
          onSelectNode={selectNode}
        />
      )}
      {selectedFaction && world && !selectedNode && !selectedFleet && (
        <FactionPanel
          faction={selectedFaction}
          world={world}
          onClose={closePanel}
          onSelectFaction={selectFaction}
          onSelectNode={selectNode}
          onSelectFleet={selectFleetFromFaction}
          onSelectCampaignFocus={selectCampaignFocus}
          liveDemandsEvent={selectedFaction ? liveDemands.get(selectedFaction.tag) ?? null : null}
        />
      )}
    </div>
  )
}
