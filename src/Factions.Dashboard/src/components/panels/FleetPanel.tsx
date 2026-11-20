import React, { useEffect, useState } from 'react'
import type { FleetDto, WorldSnapshot, PrefabCatalogue } from '../../types/world'
import { factionColorHex, FLEET_KIND_COLORS } from '../../map/colors'
import { kinematicTransitRatio } from '../../map/fleetTransit'
import { echelonMark } from '../../map/fleetEchelon'
import {
  DEFENCE_HOLD_COLOR, ENGAGEMENT_COLOR, engagementElapsedRatio, engagementRemainingMs,
  engagementTotalMs, formatEngagementRemaining, isDwelling, isEngaging, isHoldingDefence,
} from '../../fleetEngagement'

interface Props {
  fleet: FleetDto
  world: WorldSnapshot
  prefabs: PrefabCatalogue
  onBack?: () => void
  onClose: () => void
  onSelectFaction: (tag: string) => void
  onSelectNode: (id: string) => void
}

function etaString(fleet: FleetDto, nowMs: number): string {
  const arr = new Date(fleet.estimatedArrivalUtc).getTime()
  const diff = arr - nowMs
  if (diff <= 0) return 'Arrived'
  const secs = Math.floor(diff / 1000)
  if (secs < 60) return `${secs}s`
  if (secs < 3600) return `${Math.floor(secs / 60)}m ${secs % 60}s`
  return `${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m`
}

interface ItineraryEntry {
  kind: string
  nodeId: string | null
  detail: string
}

/** Remaining stops as readable rows. A run of clearance waypoints collapses into one entry — a
 * routed detour is a handful of them, and their raw coordinates say nothing an operator can act on. */
function buildItinerary(fleet: FleetDto): ItineraryEntry[] {
  const entries: ItineraryEntry[] = []
  let clearanceRun = 0
  for (const stop of fleet.remainingStops ?? []) {
    if (stop.kind === 'Navigation' && !stop.nodeId) { clearanceRun++; continue }
    if (clearanceRun > 0) {
      entries.push({ kind: 'Routing', nodeId: null, detail: `${clearanceRun} clearance waypoints` })
      clearanceRun = 0
    }
    entries.push({
      kind: stop.kind,
      nodeId: stop.nodeId || null,
      detail: stop.hasPosition ? `${stop.x.toFixed(0)}, ${stop.y.toFixed(0)}, ${stop.z.toFixed(0)}` : '?',
    })
  }
  if (clearanceRun > 0)
    entries.push({ kind: 'Routing', nodeId: null, detail: `${clearanceRun} clearance waypoints` })
  return entries
}

export default function FleetPanel({ fleet, world, prefabs, onBack, onClose, onSelectFaction, onSelectNode }: Props) {
  // One clock drives both readings. Transit interpolates a distance profile and the dwell counts
  // a duration down, but each is a function of "now", so they share the same one-second tick.
  const [nowMs, setNowMs] = useState(() => Date.now())
  const kindColor = FLEET_KIND_COLORS[fleet.kind] ?? '#888'
  const itinerary = buildItinerary(fleet)

  useEffect(() => {
    setNowMs(Date.now())
    const t = setInterval(() => setNowMs(Date.now()), 1000)
    return () => clearInterval(t)
  }, [fleet])

  const ratio = kinematicTransitRatio(fleet, nowMs)
  // Attacking and defending share one stop kind, one clock and one bar; what differs is what lands
  // at the end of it. `dwelling` drives everything that only cares the fleet is parked on its
  // target -- the route rows, the suppressed transit bar -- and the two readings below decide how
  // it is coloured and what it is called.
  const dwelling = isDwelling(fleet)
  const engaging = isEngaging(fleet)
  const defending = isHoldingDefence(fleet)
  const dwellColour = engaging ? ENGAGEMENT_COLOR : DEFENCE_HOLD_COLOR
  const dwellRemainingMs = engagementRemainingMs(fleet, nowMs)
  const dwellRatio = engagementElapsedRatio(fleet, nowMs)
  const dwellTotalSecs = Math.round(engagementTotalMs(fleet) / 1000)

  const actorColor = factionColorHex(fleet.actorTag)
  const targetColor = fleet.targetTag ? factionColorHex(fleet.targetTag) : null
  const spawnStatus = fleet.materialisedEntityId > 0 ? `Grid ${fleet.materialisedEntityId}` : 'Abstract transit'
  const isGuarding = fleet.resolved && fleet.kind === 'Guard'

  // Prefab lookup: shipPrefabId is "" for free-built convoys, null for fleets dispatched
  // before this field existed. Stats below scale per-ship values by convoySize to show the
  // convoy's totals, matching how BuildConvoyWeaponBands scales WeaponBands server-side.
  const shipPrefab = fleet.shipPrefabId ? prefabs[fleet.shipPrefabId] : null
  const shipStatusMsg =
    fleet.shipPrefabId === '' ? 'Free-built (no known prefab)'
    : fleet.shipPrefabId && !shipPrefab ? `Prefab "${fleet.shipPrefabId}" not in catalogue`
    : 'No prefab recorded (ground unit or pre-Muster data)'
  const convoyDps = shipPrefab ? (shipPrefab.weaponBands ?? []).reduce((sum, b) => sum + b.dps, 0) * fleet.convoySize : 0

  return (
    <div className="detail-panel">
      <div className="panel-header">
        <span className={`chip chip-${fleet.kind}`}>{fleet.kind}</span>
        <span className="panel-title">Fleet {fleet.fleetId.slice(0, 8)}</span>
        {dwelling && (
          <span
            className="chip"
            style={{ background: dwellColour + '33', color: dwellColour }}
          >{engaging ? 'In combat' : 'Holding'} · {formatEngagementRemaining(dwellRemainingMs)}</span>
        )}
        {onBack && <button className="panel-back" onClick={onBack}>← Back to fleets</button>}
        <button className="panel-close" onClick={onClose}>✕</button>
      </div>
      <div className="panel-body">

        {/* Actors */}
        <div className="kv-list">
          <div className="kv-row">
            <span className="kv-label">Actor</span>
            <span
              className="badge"
              style={{ background: actorColor + '33', color: actorColor, cursor: 'pointer' }}
              onClick={() => onSelectFaction(fleet.actorTag)}
            >{fleet.actorTag}</span>
          </div>
          {fleet.targetTag && (
            <div className="kv-row">
              <span className="kv-label">Target</span>
              <span
                className="badge"
                style={{ background: targetColor! + '33', color: targetColor!, cursor: 'pointer' }}
                onClick={() => onSelectFaction(fleet.targetTag!)}
              >{fleet.targetTag}</span>
            </div>
          )}
          <div className="kv-row">
            <span className="kv-label">Convoy size</span>
            <span className="kv-value">
              {fleet.convoySize} ship{fleet.convoySize !== 1 ? 's' : ''}
              {/* The mark the map draws over this fleet. Shown here because the map has no legend,
                  and a mark you cannot decode is worse than no mark. */}
              {echelonMark(fleet.convoySize) && (
                <span style={{ marginLeft: 6, letterSpacing: 2, color: kindColor }}>{echelonMark(fleet.convoySize)}</span>
              )}
            </span>
          </div>
          <div className="kv-row">
            <span className="kv-label">Grid status</span>
            <span className="kv-value" style={{ color: fleet.materialisedEntityId > 0 ? '#2ecc71' : 'var(--text-2)' }}>
              {spawnStatus}
            </span>
          </div>
        </div>

        {/* Ship loadout */}
        <div>
          <div className="section-title">Ship</div>
          {shipPrefab ? (
            <div className="kv-list">
              <div className="kv-row">
                <span className="kv-label">Prefab</span>
                <span className="kv-value" style={{ fontSize: 11, fontFamily: 'monospace' }}>
                  {fleet.shipPrefabId}{fleet.convoySize > 1 ? ` × ${fleet.convoySize}` : ''}
                </span>
              </div>
              <div className="kv-row">
                <span className="kv-label">Combat power</span>
                <span className="kv-value">
                  {convoyDps > 0 ? `${convoyDps.toLocaleString(undefined, { maximumFractionDigits: 0 })} Dps` : 'Unarmed'}
                </span>
              </div>
              {shipPrefab.weaponBands && shipPrefab.weaponBands.length > 0 && (
                <div className="kv-row" style={{ alignItems: 'flex-start' }}>
                  <span className="kv-label">Weapon bands</span>
                  <span className="kv-value" style={{ fontSize: 11 }}>
                    {shipPrefab.weaponBands.map((b, i) => (
                      <div key={i}>{(b.dps * fleet.convoySize).toFixed(0)} Dps @ {b.rangeM.toFixed(0)}m</div>
                    ))}
                  </span>
                </div>
              )}
              <div className="kv-row">
                <span className="kv-label">Cargo capacity</span>
                <span className="kv-value">{(shipPrefab.cargoLiters * fleet.convoySize).toLocaleString()} L</span>
              </div>
              <div className="kv-row">
                <span className="kv-label">Hull cost</span>
                <span className="kv-value">{(shipPrefab.cost * fleet.convoySize).toLocaleString()} cr</span>
              </div>
            </div>
          ) : (
            <div className="empty-msg">{shipStatusMsg}</div>
          )}
        </div>

        {/* Progress. An engagement dwell is not transit: the fleet is parked on its target and the
            bar is the fight running out, so it gets its own section rather than a relabelled one. */}
        <div>
          <div className="section-title">
            {engaging ? 'Engagement' : defending ? 'Holding Position' : isGuarding ? 'Guarding' : 'Transit Progress'}
          </div>
          {dwelling && (
            <>
              <div className="progress-bar" style={{ marginBottom: 6 }}>
                <div className="progress-fill" style={{ width: `${dwellRatio * 100}%`, background: dwellColour }} />
              </div>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, color: 'var(--text-2)' }}>
                <span>{engaging ? 'Fight' : 'Dwell'} elapsed {(dwellRatio * 100).toFixed(0)}%</span>
                <span style={{ color: dwellColour, fontVariantNumeric: 'tabular-nums' }}>
                  {dwellRemainingMs > 0 ? `${formatEngagementRemaining(dwellRemainingMs)} left` : 'Resolving'}
                </span>
              </div>
              <div style={{ fontSize: 11, color: 'var(--text-2)', marginTop: 6 }}>
                Holding at{' '}
                {fleet.targetNodeId
                  ? <button className="badge" style={{ cursor: 'pointer' }} onClick={() => fleet.targetNodeId && onSelectNode(fleet.targetNodeId)}>{fleet.targetNodeId}</button>
                  : 'its target'}
                {' '}for a {dwellTotalSecs}s dwell. {engaging
                  ? 'The attack lands when it ends'
                  : 'The hold gain lands when it ends'} — until then a station, a Guard or a player
                can still cut it short, and it resolves at whatever fraction of the dwell has run.
              </div>
            </>
          )}
          {!dwelling && !isGuarding && (
            <>
              <div className="progress-bar" style={{ marginBottom: 6 }}>
                <div className="progress-fill" style={{ width: `${ratio * 100}%`, background: kindColor }} />
              </div>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, color: 'var(--text-2)' }}>
                <span>{(ratio * 100).toFixed(1)}%</span>
                <span>ETA {etaString(fleet, nowMs)}</span>
              </div>
            </>
          )}
          {!dwelling && isGuarding && (
            <div style={{ color: '#3498db', fontSize: 12 }}>Actively guarding target node</div>
          )}
        </div>

        {/* Route */}
        <div>
          <div className="section-title">Route</div>
          <div className="kv-list">
            {/* A dwell leg departs and arrives at the same node, so an origin/destination pair and a
                0 km distance would describe a journey that is not happening. One "at" row instead. */}
            {dwelling ? (
              <div className="kv-row">
                <span className="kv-label">Holding at</span>
                <span className="kv-value" style={{ fontSize: 11 }}>
                  {fleet.targetNodeId
                    ? <button className="badge" style={{ cursor: 'pointer' }} onClick={() => fleet.targetNodeId && onSelectNode(fleet.targetNodeId)}>{fleet.targetNodeId}</button>
                    : `${fleet.targetX.toFixed(0)}, ${fleet.targetY.toFixed(0)}, ${fleet.targetZ.toFixed(0)}`
                  }
                </span>
              </div>
            ) : (
              <>
                <div className="kv-row">
                  <span className="kv-label">Origin</span>
                  <span className="kv-value" style={{ fontSize: 11, fontFamily: 'monospace' }}>
                    {fleet.originNodeId
                      ? <button className="badge" style={{ cursor: 'pointer' }} onClick={() => fleet.originNodeId && onSelectNode(fleet.originNodeId)}>{fleet.originNodeId}</button>
                      : `${fleet.originX.toFixed(0)}, ${fleet.originY.toFixed(0)}, ${fleet.originZ.toFixed(0)}`
                    }
                  </span>
                </div>
                <div className="kv-row">
                  <span className="kv-label">Destination</span>
                  <span className="kv-value" style={{ fontSize: 11 }}>
                    {fleet.targetNodeId
                      ? <button className="badge" style={{ cursor: 'pointer' }} onClick={() => fleet.targetNodeId && onSelectNode(fleet.targetNodeId)}>{fleet.targetNodeId}</button>
                      : `${fleet.targetX.toFixed(0)}, ${fleet.targetY.toFixed(0)}, ${fleet.targetZ.toFixed(0)}`
                    }
                  </span>
                </div>
              </>
            )}
            {fleet.currentStopKind === 'Pickup' && fleet.pickupItemKey && (
              <div className="kv-row">
                <span className="kv-label">Picking up</span>
                <span className="kv-value" style={{ fontSize: 11 }}>
                  {fleet.pickupQuantity.toLocaleString()}x {fleet.pickupItemKey}
                </span>
              </div>
            )}
            {itinerary.length > 0 && (
              <div className="kv-row" style={{ alignItems: 'flex-start' }}>
                <span className="kv-label">Then</span>
                <span className="kv-value" style={{ fontSize: 11 }}>
                  {itinerary.map((entry, i) => (
                    <div key={i} style={{ marginBottom: i < itinerary.length - 1 ? 4 : 0 }}>
                      <span className="badge" style={{ marginRight: 4 }}>{entry.kind}</span>
                      {entry.nodeId
                        ? <button className="badge" style={{ cursor: 'pointer' }} onClick={() => onSelectNode(entry.nodeId!)}>{entry.nodeId}</button>
                        : entry.detail}
                    </div>
                  ))}
                </span>
              </div>
            )}
            {!dwelling && (
              <div className="kv-row">
                <span className="kv-label">Distance</span>
                <span className="kv-value">
                  {Math.sqrt(
                    (fleet.targetX - fleet.originX) ** 2 +
                    (fleet.targetY - fleet.originY) ** 2 +
                    (fleet.targetZ - fleet.originZ) ** 2
                  ).toFixed(0)} km
                </span>
              </div>
            )}
            {fleet.pathNodeIds && fleet.pathNodeIds.length > 0 && (
              <div className="kv-row" style={{ alignItems: 'flex-start' }}>
                <span className="kv-label">Waypoints</span>
                <span className="kv-value" style={{ fontSize: 11 }}>{fleet.pathNodeIds.join(' → ')}</span>
              </div>
            )}
          </div>
        </div>

        {/* Cargo */}
        {(fleet.kind === 'Trade' || fleet.kind === 'Haul') && (
          <div>
            <div className="section-title">Cargo</div>
            <div className="kv-row" style={{ marginBottom: 6 }}>
              <span className="kv-label">Total value</span>
              <span className="kv-value">{fleet.cargoValue.toLocaleString()} cr</span>
            </div>
            {fleet.cargo && Object.keys(fleet.cargo).length > 0 ? (
              <table className="data-table">
                <thead><tr><th>Item</th><th style={{ textAlign: 'right' }}>Qty</th></tr></thead>
                <tbody>
                  {Object.entries(fleet.cargo)
                    .sort((a, b) => b[1] - a[1])
                    .map(([k, v]) => (
                      <tr key={k}>
                        <td>{k}</td>
                        <td style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{v.toLocaleString()}</td>
                      </tr>
                    ))}
                </tbody>
              </table>
            ) : (
              <div className="empty-msg">No cargo manifest</div>
            )}
          </div>
        )}

        {/* Ferry info */}
        {fleet.kind === 'Trade' && fleet.ferryRole && (
          <div className="kv-row">
            <span className="kv-label">Ferry role</span>
            <span className="badge">{fleet.ferryRole}</span>
          </div>
        )}

      </div>
    </div>
  )
}
