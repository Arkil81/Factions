import React, { useEffect, useState } from 'react'
import type { NodeDto, WorldSnapshot } from '../../types/world'
import { factionColorHex } from '../../map/colors'
import { nodeColor, nodeIcon, nodeLabel } from '../../map/nodeLabel'
import type { DemandsEvent } from '../../hooks/useWebSocket'
import { bomHave, bomProgress, bomShortfall, groupReservedByInstall, stripInvKeyPrefix } from '../../installBom'
import { formatCooldownRemaining, mineCooldownRemainingMs } from '../../mineCooldown'
import {
  DEFENCE_HOLD_COLOR, ENGAGEMENT_COLOR, defencesAtNode, engagementElapsedRatio,
  engagementRemainingMs, engagementsAtNode, formatEngagementRemaining,
} from '../../fleetEngagement'
import {
  formatSiegeDuration, siegeColor, siegeDwellRemainingMs, siegeElapsedMs, siegeOf, siegePhase,
} from '../../nodeSiege'

interface ThroughputEntry {
  factionTag: string
  nodeId: string
  cap: string
  primaryInputId: string
  primaryOutputId: string
  nominalItemsPerMinute: number
  observedItemsPerMinute: number
  effectiveMachineSpeed: number
  outputScale: number
  slotCount: number
}

interface ProductionHistoryEntry {
  factionTag: string
  nodeId: string
  recipeId: string
  invKey: string
  isOutput: boolean
  quantity: number
  startUtc: string
  endUtc: string
}

interface Props {
  node: NodeDto
  world: WorldSnapshot
  onClose: () => void
  onSelectFaction: (tag: string) => void
  onSelectFleet: (id: string) => void
  /** The owning faction's live demand snapshot, which carries the install BOM each reserved key is
   *  being collected toward. Null before the first flush, or for an unowned node. */
  liveDemandsEvent: DemandsEvent | null
}

type InventoryItemType = 'Ore' | 'Ingot' | 'Component' | 'Unknown'

interface InventoryItemInfo {
  label: string
  type: InventoryItemType
  icon: string
}

function inventoryItemInfo(key: string): InventoryItemInfo {
  const match = /^(Ore|Ingot|Comp):(.+)$/.exec(key)
  if (!match) {
    return { label: key, type: 'Unknown', icon: '?' }
  }

  switch (match[1]) {
    case 'Ore':
      return { label: match[2], type: 'Ore', icon: 'O' }
    case 'Ingot':
      return { label: match[2], type: 'Ingot', icon: 'I' }
    case 'Comp':
      return { label: match[2], type: 'Component', icon: 'C' }
    default:
      return { label: match[2], type: 'Unknown', icon: '?' }
  }
}

function gpsStringFor(node: NodeDto): string {
  const name = (node.displayName ?? node.id).replace(/:/g, ' ')
  const x = (node.x * 1000).toFixed(2)
  const y = (node.y * 1000).toFixed(2)
  const z = (node.z * 1000).toFixed(2)
  const color = nodeColor(node)
  return `GPS:${name}:${x}:${y}:${z}:${color}:`
}

function CopyGpsButton({ node }: { node: NodeDto }) {
  const [copied, setCopied] = useState(false)
  useEffect(() => {
    if (!copied) return
    const id = setTimeout(() => setCopied(false), 1500)
    return () => clearTimeout(id)
  }, [copied])

  return (
    <button
      onClick={() => navigator.clipboard.writeText(gpsStringFor(node)).then(() => setCopied(true))}
      title="Copy SE GPS coordinate to clipboard"
      style={{
        background: 'none', border: 'none', cursor: 'pointer', padding: '0 0 0 6px',
        fontSize: 12, color: copied ? '#2ecc71' : 'var(--text-2)', lineHeight: 1,
      }}
    >
      {copied ? '✓' : '⎘'}
    </button>
  )
}

function InventoryTable({ title, inventory }: { title: string; inventory: Record<string, number> | null }) {
  if (!inventory || Object.keys(inventory).length === 0) return null

  return (
    <div>
      <div className="section-title">{title}</div>
      <table className="data-table">
        <thead>
          <tr>
            <th className="item-type-col" aria-label="Item type"></th>
            <th>Item</th>
            <th style={{ textAlign: 'right' }}>Qty</th>
          </tr>
        </thead>
        <tbody>
          {Object.entries(inventory)
            .sort((a, b) => b[1] - a[1])
            .map(([k, v]) => {
              const item = inventoryItemInfo(k)
              return (
                <tr key={k}>
                  <td className="item-type-col">
                    <span
                      className={`item-type-icon item-type-${item.type.toLowerCase()}`}
                      title={`${item.type} (${k})`}
                      aria-label={`${item.type}: ${item.label}`}
                    >
                      {item.icon}
                    </span>
                  </td>
                  <td>{item.label}</td>
                  <td style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{v.toLocaleString()}</td>
                </tr>
              )
            })}
        </tbody>
      </table>
    </div>
  )
}

export default function NodePanel({ node, world, onClose, onSelectFaction, onSelectFleet, liveDemandsEvent }: Props) {
  const ownerFaction = node.ownerTag
    ? world.factions.find(f => f.tag === node.ownerTag)
    : null

  const mineCooldownMs = mineCooldownRemainingMs(node.mineCooldownUntilUtc, world.utcNow)

  // Raids holding at this node for their engagement dwell. This is the defender's view of the
  // window: the raid has not landed yet, and it resolves at whatever fraction of the fight has run.
  // Reinforces hold the same stop kind at a node they are shoring up, and are read separately --
  // matched on the action kind, or a faction's own garrison lands in its own "under attack" list.
  const engagements = engagementsAtNode(world.fleets, node.id)
  const defences = defencesAtNode(world.fleets, node.id)
  // A player siege has no fleet behind it, so it is read off the node itself -- and it has to join
  // the second-ticking clock below on its own, or a node being besieged with no raid in the air
  // renders a countdown that never moves. See docs/issues/FAC-258.
  const siege = siegeOf(node)
  const [nowMs, setNowMs] = useState(() => Date.now())
  useEffect(() => {
    if (engagements.length === 0 && defences.length === 0 && !siege) return
    setNowMs(Date.now())
    const t = setInterval(() => setNowMs(Date.now()), 1000)
    return () => clearInterval(t)
  }, [engagements.length, defences.length, siege !== null])

  const [throughput, setThroughput] = useState<ThroughputEntry[]>([])
  useEffect(() => {
    fetch('/api/production')
      .then(r => r.json())
      .then((d: { entries?: ThroughputEntry[] }) => {
        setThroughput((d.entries ?? []).filter(e => e.nodeId === node.id))
      })
      .catch(() => {})
  }, [node.id])

  const [history, setHistory] = useState<ProductionHistoryEntry[]>([])
  useEffect(() => {
    let cancelled = false
    const load = () => {
      fetch('/api/production-history')
        .then(r => r.json())
        .then((d: { entries?: ProductionHistoryEntry[] }) => {
          if (cancelled) return
          setHistory(
            (d.entries ?? [])
              .filter(e => e.nodeId === node.id)
              .sort((a, b) => new Date(b.endUtc).getTime() - new Date(a.endUtc).getTime()),
          )
        })
        .catch(() => {})
    }
    load()
    const id = setInterval(load, 10_000)
    return () => { cancelled = true; clearInterval(id) }
  }, [node.id])

  return (
    <div className="detail-panel">
      <div className="panel-header">
        {ownerFaction && (
          <button
            onClick={() => onSelectFaction(ownerFaction.tag)}
            title={`Back to ${ownerFaction.tag}`}
            style={{
              background: 'none', border: 'none', cursor: 'pointer', padding: '0 4px 0 0',
              fontSize: 11, color: factionColorHex(ownerFaction.tag), flexShrink: 0,
            }}
          >
            ← {ownerFaction.tag}
          </button>
        )}
        <span style={{ fontSize: 18 }}>{nodeIcon(node)}</span>
        <span className="panel-title">{node.displayName ?? node.id}</span>
        <button className="panel-close" onClick={onClose}>✕</button>
      </div>
      <div className="panel-body">
        {/* A player siege, above the fleet block: it is the phase in which the node can actually be
            lost, and it is the one attack that nothing else on this panel would mention. */}
        {siege && (() => {
          const phase = siegePhase(siege)
          const color = siegeColor(siege)
          const dwellLeft = siegeDwellRemainingMs(siege, nowMs)
          const heading = phase === 'raided'
            ? 'Raided — conquer clock running'
            : phase === 'breached' ? 'Safe zone breached' : 'Under siege'
          // Before the breach the bar is the shield being worn down, after it the raid's progress
          // toward taking the node. Two different quantities, and labelling them apart is the whole
          // point of drawing either.
          const barRatio = phase === 'besieged' ? siege.integrity : siege.raidProgress
          const barLabel = phase === 'besieged'
            ? `Shield ${Math.round(siege.integrity * 100)}%`
            : `${Math.round(siege.raidProgress * 100)}% of the damage needed to take the node`
          return (
            <div style={{ border: `1px solid ${color}66`, background: color + '14', borderRadius: 4, padding: 8 }}>
              <div className="section-title" style={{ color, marginTop: 0 }}>{heading}</div>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, marginBottom: 2 }}>
                <span>{barLabel}</span>
                <span style={{ color, fontVariantNumeric: 'tabular-nums' }}>
                  {dwellLeft !== null && dwellLeft > 0
                    ? `breach in ${formatSiegeDuration(dwellLeft)}`
                    : `${formatSiegeDuration(siegeElapsedMs(siege, nowMs))} in`}
                </span>
              </div>
              <div className="progress-bar" style={{ height: 3 }}>
                <div className="progress-fill" style={{ width: `${Math.round(barRatio * 100)}%`, background: color }} />
              </div>
              <div style={{ fontSize: 11, color: 'var(--text-2)', marginTop: 4 }}>
                {phase === 'besieged'
                  ? 'A player is firing on the safe zone. The shield drops only once the pool is empty AND it has been held down for the full dwell; stop firing and it regenerates.'
                  : phase === 'breached'
                    ? 'The safe zone is down and the station itself is being shot. The station keeps firing back.'
                    : 'The raid has succeeded. The node is offered to its attacker when the conquer clock runs out.'}
              </div>
            </div>
          )
        })()}
        {/* Under attack. First thing in the body because it is the only thing here with a deadline. */}
        {engagements.length > 0 && (
          <div style={{ border: `1px solid ${ENGAGEMENT_COLOR}66`, background: ENGAGEMENT_COLOR + '14', borderRadius: 4, padding: 8 }}>
            <div className="section-title" style={{ color: ENGAGEMENT_COLOR, marginTop: 0 }}>
              Under attack ({engagements.length})
            </div>
            {engagements.map(f => {
              const remaining = engagementRemainingMs(f, nowMs)
              const ratio = engagementElapsedRatio(f, nowMs)
              return (
                <button
                  key={f.fleetId}
                  type="button"
                  onClick={() => onSelectFleet(f.fleetId)}
                  style={{ marginBottom: 6, cursor: 'pointer', background: 'none', border: 'none', padding: 0, color: 'inherit', width: '100%', textAlign: 'left' }}
                >
                  <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, marginBottom: 2 }}>
                    <span>
                      <span className="chip" style={{ background: ENGAGEMENT_COLOR + '33', color: ENGAGEMENT_COLOR }}>{f.kind}</span>
                      {' '}
                      <span style={{ color: factionColorHex(f.actorTag) }}>{f.actorTag}</span>
                      {' · '}{f.convoySize} ship{f.convoySize !== 1 ? 's' : ''}
                    </span>
                    <span style={{ color: ENGAGEMENT_COLOR, fontVariantNumeric: 'tabular-nums' }}>
                      {remaining > 0 ? `${formatEngagementRemaining(remaining)} left` : 'Resolving'}
                    </span>
                  </div>
                  <div className="progress-bar" style={{ height: 3 }}>
                    <div className="progress-fill" style={{ width: `${ratio * 100}%`, background: ENGAGEMENT_COLOR }} />
                  </div>
                </button>
              )
            })}
            <div style={{ fontSize: 11, color: 'var(--text-2)', marginTop: 4 }}>
              The attack lands when the dwell ends. Stopped before then, it resolves at the
              fraction of the fight already run.
            </div>
          </div>
        )}
        {/* Reinforcements standing here. The same dwell as above, on the other side of it: the hold
            gain lands when it ends, and a raid that arrives first takes it away. */}
        {defences.length > 0 && (
          <div style={{ border: `1px solid ${DEFENCE_HOLD_COLOR}66`, background: DEFENCE_HOLD_COLOR + '14', borderRadius: 4, padding: 8 }}>
            <div className="section-title" style={{ color: DEFENCE_HOLD_COLOR, marginTop: 0 }}>
              Reinforcing ({defences.length})
            </div>
            {defences.map(f => {
              const remaining = engagementRemainingMs(f, nowMs)
              const ratio = engagementElapsedRatio(f, nowMs)
              return (
                <button
                  key={f.fleetId}
                  type="button"
                  onClick={() => onSelectFleet(f.fleetId)}
                  style={{ marginBottom: 6, cursor: 'pointer', background: 'none', border: 'none', padding: 0, color: 'inherit', width: '100%', textAlign: 'left' }}
                >
                  <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, marginBottom: 2 }}>
                    <span>
                      <span className="chip" style={{ background: DEFENCE_HOLD_COLOR + '33', color: DEFENCE_HOLD_COLOR }}>{f.kind}</span>
                      {' '}
                      <span style={{ color: factionColorHex(f.actorTag) }}>{f.actorTag}</span>
                      {' · '}{f.convoySize} ship{f.convoySize !== 1 ? 's' : ''}
                    </span>
                    <span style={{ color: DEFENCE_HOLD_COLOR, fontVariantNumeric: 'tabular-nums' }}>
                      {remaining > 0 ? `${formatEngagementRemaining(remaining)} left` : 'Resolving'}
                    </span>
                  </div>
                  <div className="progress-bar" style={{ height: 3 }}>
                    <div className="progress-fill" style={{ width: `${ratio * 100}%`, background: DEFENCE_HOLD_COLOR }} />
                  </div>
                </button>
              )
            })}
            <div style={{ fontSize: 11, color: 'var(--text-2)', marginTop: 4 }}>
              The hold gain lands when the dwell ends. These ships fight anything that attacks the
              node while they stand here, and deliver nothing if they are destroyed.
            </div>
          </div>
        )}
        <div className="kv-list">
          {node.displayName && (
            <div className="kv-row">
              <span className="kv-label">ID</span>
              <span className="kv-value" style={{ fontFamily: 'monospace', fontSize: 11 }}>{node.id}</span>
            </div>
          )}
          <div className="kv-row">
            <span className="kv-label">Ground</span>
            <span className="badge" style={{ background: nodeColor(node) + '33', color: nodeColor(node) }}>
              {nodeLabel(node)}
            </span>
          </div>
          <div className="kv-row">
            <span className="kv-label">Owner</span>
            {ownerFaction
              ? <span
                  className="badge"
                  style={{ background: factionColorHex(ownerFaction.tag) + '33', color: factionColorHex(ownerFaction.tag), cursor: 'pointer' }}
                  onClick={() => onSelectFaction(ownerFaction.tag)}
                >
                  {ownerFaction.tag} ({ownerFaction.personalityId})
                </span>
              : <span className="kv-value" style={{ color: 'var(--text-3)' }}>Unclaimed</span>
            }
          </div>
          <div className="kv-row">
            <span className="kv-label">Position (km)</span>
            <span style={{ display: 'flex', alignItems: 'center' }}>
              <span className="kv-value" style={{ fontFamily: 'monospace', fontSize: 11 }}>
                {node.x.toFixed(0)}, {node.y.toFixed(0)}, {node.z.toFixed(0)}
              </span>
              <CopyGpsButton node={node} />
            </span>
          </div>
          <div className="kv-row">
            <span className="kv-label">Station</span>
            <span
              className="badge"
              style={{
                background: node.hasStation
                  ? (node.hasInfrastructure ? '#2ecc7133' : '#d0d7de22')
                  : 'var(--bg-3)',
                color: node.hasStation
                  ? (node.hasInfrastructure ? '#2ecc71' : '#d0d7de')
                  : 'var(--text-2)',
              }}
            >
              {node.hasStation
                ? (node.hasInfrastructure ? 'Infrastructure' : 'Legacy Station')
                : 'None'}
            </span>
          </div>
        </div>

        <div>
          <div className="section-title">Node Metrics</div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Bar label="Hold Strength" value={node.holdStrength} color="#3498db" />
            {/* Worth and quantity, adjacent and on one scale, because reading them against each
                other is the point: worth is price-weighted and drives claim/found/raid scoring,
                richness is measured density and drives the mint and the drill. A thick ice seam
                shows low worth and high richness. See docs/issues/FAC-219. */}
            <Bar label="Worth" value={node.resourceValue} color="#2ecc71" />
            <Bar label="Ore density" value={node.richness} color="#e67e22" />
          </div>
          {mineCooldownMs !== null && (
            <div className="kv-list" style={{ marginTop: 8 }}>
              <div className="kv-row">
                <span className="kv-label">Mining Cooldown</span>
                <span
                  className="kv-value"
                  style={{ color: 'var(--warn, #f39c12)' }}
                  title={`mineable again ${new Date(node.mineCooldownUntilUtc!).toLocaleString()}`}
                >
                  {formatCooldownRemaining(mineCooldownMs)} left
                </span>
              </div>
            </div>
          )}
        </div>

        {node.capabilities && Object.keys(node.capabilities).length > 0 && (
          <div>
            <div className="section-title">Capabilities</div>
            <div className="kv-list">
              {Object.entries(node.capabilities).map(([cap, lvl]) => (
                <div key={cap} className="kv-row">
                  <span className="kv-label">{cap}</span>
                  <span className="badge">Lv {lvl}</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {node.shipStock && Object.keys(node.shipStock).length > 0 && (
          <div>
            <div className="section-title">Ship Stock</div>
            <div className="kv-list">
              {Object.entries(node.shipStock).map(([role, count]) => (
                <div key={role} className="kv-row">
                  <span className="kv-label">{role}</span>
                  <span className="kv-value">{count} ship{count !== 1 ? 's' : ''}</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {node.buildQueue && node.buildQueue.length > 0 && (
          <div>
            <div className="section-title">Build Queue</div>
            {node.buildQueue.map(entry => {
              const totalBom = Object.values(entry.bom).reduce((a, b) => a + b, 0)
              const totalColl = Object.values(entry.collected).reduce((a, b) => a + b, 0)
              const progress = totalBom > 0 ? totalColl / totalBom : 0
              const eta = new Date(entry.expiresUtc)
              return (
                <div key={entry.id} style={{ marginBottom: 8 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12 }}>
                    <span>{entry.role} {entry.prefabId ? `(${entry.prefabId})` : ''}</span>
                    <span style={{ color: 'var(--text-2)' }}>exp {eta.toLocaleTimeString()}</span>
                  </div>
                  <Bar label="" value={progress} color="#f1c40f" />
                  <div style={{ fontSize: 10, color: 'var(--text-3)', marginTop: 2 }}>
                    {Object.keys(entry.bom).map(k => {
                      const need = entry.bom[k]
                      const have = entry.collected[k] ?? 0
                      return `${k.replace(/^(Ore:|Comp:|Ingot:)/, '')}: ${have}/${need}`
                    }).join(' · ')}
                  </div>
                  {Object.keys(entry.reservedInputs).length > 0 && (
                    <div style={{ fontSize: 10, color: 'var(--text-3)', marginTop: 2 }}>
                      Reserved: {Object.entries(entry.reservedInputs)
                        .map(([k, qty]) => `${k.replace(/^(Ore:|Comp:|Ingot:)/, '')} ${qty}`)
                        .join(' · ')}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        )}

        {node.repairQueue && node.repairQueue.length > 0 && (
          <div>
            <div className="section-title">
              Repair Queue ({node.repairQueue.reduce((a, r) => a + r.count, 0)} hulls, smallest bill first)
            </div>
            {node.repairQueue.map(r => {
              const progress = r.billUnits > 0 ? r.collectedUnits / r.billUnits : 1
              const served = r.count > 1 ? `#${r.position}-${r.position + r.count - 1}` : `#${r.position}`
              const state =
                r.state === 'Repairing' && r.doneUtc ? `repairing, done ${new Date(r.doneUtc).toLocaleTimeString()}`
                : r.state === 'WaitingForParts' ? 'waiting for parts'
                : r.state === 'NoShipyard' ? 'no shipyard here'
                : r.state === 'YardPaused' ? 'yard paused'
                : r.state
              const missing = r.missing ? Object.entries(r.missing).sort((a, b) => b[1] - a[1]) : []
              return (
                <div key={`${r.position}-${r.prefabId}`} style={{ marginBottom: 8 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12 }}>
                    <span>{served} {r.count > 1 ? `${r.count} × ` : ''}{r.prefabId} ({(r.integrity * 100).toFixed(0)}%)</span>
                    <span style={{ color: 'var(--text-2)' }}>{state}</span>
                  </div>
                  <Bar label="" value={progress} color="#e67e22" />
                  <div style={{ fontSize: 10, color: 'var(--text-3)', marginTop: 2 }}>
                    bill {r.collectedUnits.toLocaleString()}/{r.billUnits.toLocaleString()} per hull
                    {missing.length > 0 && ' · missing ' + missing
                      .map(([k, n]) => `${k.replace(/^(Ore:|Comp:|Ingot:)/, '')} ${n.toLocaleString()}`)
                      .join(' · ')}
                  </div>
                </div>
              )
            })}
          </div>
        )}

        {history.length > 0 && (
          <div>
            <div className="section-title">Production History (last 5 min)</div>
            <table className="data-table data-table-fixed">
              <thead>
                <tr>
                  <th className="dir-col"></th>
                  <th className="item-type-col" aria-label="Item type"></th>
                  <th className="truncate-col">Item</th>
                  <th className="truncate-col">Recipe</th>
                  <th style={{ textAlign: 'right' }}>Qty</th>
                  <th style={{ textAlign: 'right' }}>Last seen</th>
                </tr>
              </thead>
              <tbody>
                {history.map((e, i) => {
                  const item = inventoryItemInfo(e.invKey)
                  const ageSec = Math.max(0, (Date.now() - new Date(e.endUtc).getTime()) / 1000)
                  const ageLabel = ageSec < 60 ? `${ageSec.toFixed(0)}s ago` : `${(ageSec / 60).toFixed(1)}m ago`
                  return (
                    <tr key={i}>
                      <td className="dir-col">
                        <span
                          title={e.isOutput ? 'Produced' : 'Consumed'}
                          style={{ color: e.isOutput ? '#2ecc71' : '#e74c3c', fontWeight: 700, fontSize: 11 }}
                        >
                          {e.isOutput ? '+' : '−'}
                        </span>
                      </td>
                      <td className="item-type-col">
                        <span
                          className={`item-type-icon item-type-${item.type.toLowerCase()}`}
                          title={`${item.type} (${e.invKey})`}
                          aria-label={`${item.type}: ${item.label}`}
                        >
                          {item.icon}
                        </span>
                      </td>
                      <td className="truncate-col" style={{ fontSize: 11 }} title={e.invKey}>{item.label}</td>
                      <td className="truncate-col" style={{ fontSize: 11, color: 'var(--text-3)' }} title={e.recipeId}>{e.recipeId}</td>
                      <td
                        style={{
                          textAlign: 'right', fontVariantNumeric: 'tabular-nums',
                          color: e.isOutput ? '#2ecc71' : '#e74c3c',
                        }}
                      >
                        {e.isOutput ? '+' : '−'}{e.quantity.toLocaleString()}
                      </td>
                      <td style={{ textAlign: 'right', color: 'var(--text-3)', fontSize: 10 }}>
                        {ageLabel}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}

        {throughput.length > 0 && (
          <div>
            <div className="section-title">Production (real / capacity)</div>
            <table className="data-table">
              <thead>
                <tr>
                  <th>Cap</th>
                  <th>Recipe</th>
                  <th style={{ textAlign: 'right' }}>/min real</th>
                  <th style={{ textAlign: 'right' }}>max</th>
                  <th style={{ textAlign: 'right' }}>slots</th>
                </tr>
              </thead>
              <tbody>
                {throughput.map((e, i) => {
                  const input  = e.primaryInputId.replace(/^(Ore:|Ingot:|Comp:)/, '')
                  const output = e.primaryOutputId.replace(/^(Ore:|Ingot:|Comp:)/, '')
                  const capColor: Record<string, string> = {
                    Refinery: '#e67e22', Manufactory: '#3498db',
                    ManufactoryII: '#5dade2', RefineryII: '#f0b27a',
                  }
                  const color = capColor[e.cap] ?? '#8b949e'
                  return (
                    <tr key={i}>
                      <td>
                        <span style={{ color, fontSize: 10, fontWeight: 700 }}>
                          {e.cap.replace('II', '²')}
                        </span>
                      </td>
                      <td style={{ fontSize: 11 }}>
                        <span style={{ color: 'var(--text-3)' }}>{input}</span>
                        {' → '}
                        <span style={{ color: 'var(--text-1)' }}>{output}</span>
                      </td>
                      <td
                        style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums', color: '#2ecc71' }}
                        title={`machine speed ${e.effectiveMachineSpeed.toFixed(2)}, yield x${e.outputScale.toFixed(2)}`}
                      >
                        {e.observedItemsPerMinute.toFixed(1)}
                      </td>
                      <td
                        style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums', color: 'var(--text-3)' }}
                        title="capacity at the current slot assignment; a much larger number than the real rate means this bank is starved of input"
                      >
                        {e.nominalItemsPerMinute.toFixed(1)}
                      </td>
                      <td style={{ textAlign: 'right', color: 'var(--text-3)' }}>
                        {e.slotCount}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}

        <ReservedInstallStock node={node} liveDemandsEvent={liveDemandsEvent} />
        <InventoryTable title="Inventory" inventory={node.inventory} />
      </div>
    </div>
  )
}

/**
 * This node's install reservations, grouped by the capability install each key is being collected
 * toward. `ReservedInventory` is capability-install stock only -- ship-build materials live in
 * `BuildQueueEntry.Collected`/`ReservedInputs` and are shown in the Build Queue section above.
 *
 * The need shown beside each key is faction-wide, because that is what the reservation targets:
 * `ProductionManager` reserves against the cumulative BOM of every active request across every
 * owned node, so this node holds a slice of one claim rather than a claim of its own.
 */
function ReservedInstallStock({ node, liveDemandsEvent }: { node: NodeDto; liveDemandsEvent: DemandsEvent | null }) {
  const reserved = node.reservedInventory
  if (!reserved || Object.keys(reserved).length === 0) return null

  const investments = liveDemandsEvent?.investments ?? []
  const groups = groupReservedByInstall(reserved, investments)

  return (
    <div>
      <div className="section-title">Reserved Inventory — capability installs</div>
      {!liveDemandsEvent && (
        <div style={{ fontSize: 10, color: 'var(--text-3)', marginBottom: 4 }}>
          ○ Waiting for the owner's live demand snapshot — showing held stock without its target BOM
        </div>
      )}
      {groups.map((g, gi) => {
        const bomRows = g.rows.map(r => r.row).filter((r): r is NonNullable<typeof r> => !!r)
        const progress = bomProgress(bomRows)
        return (
          <div key={gi} style={{ marginBottom: 8 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, marginBottom: 2 }}>
              <span style={{ color: g.caps.length > 0 ? 'var(--text-1)' : '#f39c12' }}>
                {g.caps.length > 0
                  ? `for ${g.caps.join(' + ')} install${g.byKind ? ' (flat cost — any key of this kind)' : ''}`
                  : 'unclaimed — no active install request'}
              </span>
              {bomRows.length > 0 && (
                <span style={{ color: 'var(--text-3)' }}>{(progress * 100).toFixed(0)}% faction-wide</span>
              )}
            </div>
            {bomRows.length > 0 && (
              <div className="progress-bar" style={{ marginBottom: 3 }}>
                <div
                  className="progress-fill"
                  style={{
                    width: `${progress * 100}%`,
                    background: bomRows.some(r => bomShortfall(r) > 0) ? '#f1c40f' : '#2ecc71',
                  }}
                />
              </div>
            )}
            <table className="data-table">
              <thead>
                <tr>
                  <th className="item-type-col" aria-label="Item type"></th>
                  <th>Item</th>
                  <th style={{ textAlign: 'right' }}>Here</th>
                  <th style={{ textAlign: 'right' }}>Faction</th>
                  <th style={{ textAlign: 'right' }}>Need</th>
                </tr>
              </thead>
              <tbody>
                {g.rows.map(({ key, here, row }) => {
                  const item = inventoryItemInfo(key)
                  const short = row ? bomShortfall(row) : 0
                  return (
                    <tr key={key}>
                      <td className="item-type-col">
                        <span
                          className={`item-type-icon item-type-${item.type.toLowerCase()}`}
                          title={`${item.type} (${key})`}
                          aria-label={`${item.type}: ${item.label}`}
                        >
                          {item.icon}
                        </span>
                      </td>
                      <td style={{ fontSize: 11 }} title={key}>{item.label}</td>
                      <td style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
                        {here.toLocaleString()}
                      </td>
                      <td
                        style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums', color: short > 0 ? '#e74c3c' : 'var(--text-2)' }}
                        title={row
                          ? `faction-wide: ${row.reserved.toLocaleString()} reserved + ${row.stock.toLocaleString()} routable`
                          : 'no active install request names this key'}
                      >
                        {row ? bomHave(row).toLocaleString() : '—'}
                      </td>
                      <td
                        style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums', color: short > 0 ? '#e74c3c' : 'var(--text-3)' }}
                        title={row && row.byKind ? `flat ${stripInvKeyPrefix(row.key)} install cost` : undefined}
                      >
                        {row ? row.need.toLocaleString() : '—'}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )
      })}
    </div>
  )
}

function Bar({ label, value, color }: { label: string; value: number; color: string }) {
  return (
    <div className="progress-wrap">
      {label && (
        <div className="progress-label">
          <span>{label}</span>
          <span>{(value * 100).toFixed(0)}%</span>
        </div>
      )}
      <div className="progress-bar">
        <div className="progress-fill" style={{ width: `${value * 100}%`, background: color }} />
      </div>
    </div>
  )
}
