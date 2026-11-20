import React, { useState, useEffect } from 'react'
import type { FactionDto, WorldSnapshot, FinanceSnapshotDto, CampaignFocusDto } from '../../types/world'
// WorldSnapshot used by OverviewTab via Props
import { factionColorHex } from '../../map/colors'
import { nodeLabel } from '../../map/nodeLabel'
import { DRIVE_NAMES, DRIVE_COLORS } from '../../types/world'
import type { DemandsEvent, DemandItem } from '../../hooks/useWebSocket'
import { fetchFactionLog, fetchFactionFinance } from '../../api/client'
import { InstallBomTable } from '../InstallBom'
import { bomProgress, bomShortfall } from '../../installBom'
import {
  DEFENCE_HOLD_COLOR, ENGAGEMENT_COLOR, engagementElapsedRatio, engagementRemainingMs,
  formatEngagementRemaining, isDwelling, isEngaging, isHoldingDefence,
} from '../../fleetEngagement'


const CAP_ABBR: Record<string, string> = {
  Refinery: 'R', Manufactory: 'M', Shipyard: 'S', FuelDepot: 'F',
}
const CAP_COLOR: Record<string, string> = {
  Refinery: '#e67e22', Manufactory: '#3498db', Shipyard: '#9b59b6', FuelDepot: '#2ecc71',
}

interface Props {
  faction: FactionDto
  world: WorldSnapshot
  onClose: () => void
  onSelectFaction: (tag: string) => void
  onSelectNode: (id: string) => void
  onSelectFleet: (id: string) => void
  onSelectCampaignFocus: (focus: CampaignFocusDto) => void
  liveDemandsEvent: DemandsEvent | null
}

type Tab = 'overview' | 'relations' | 'demands' | 'finance' | 'log'

export default function FactionPanel({ faction, world, onClose, onSelectFaction, onSelectNode, onSelectFleet, onSelectCampaignFocus, liveDemandsEvent }: Props) {
  const [tab, setTab] = useState<Tab>('overview')
  const color = factionColorHex(faction.tag)

  const nodes = world.nodes.filter(n => n.ownerTag === faction.tag)
  const fleets = world.fleets.filter(f => f.actorTag === faction.tag)
  // Raids fighting at this faction's own nodes right now. Matched on the node rather than on
  // targetTag so a raid on territory that changed hands mid-flight still lands on its real owner,
  // and on the action kind so this faction's own reinforces of its own stations stay out of it.
  const ownNodeIds = new Set(nodes.map(n => n.id))
  const underAttack = world.fleets.filter(f => isEngaging(f) && f.targetNodeId && ownNodeIds.has(f.targetNodeId))

  return (
    <div className="detail-panel">
      <div className="panel-header">
        <div
          style={{
            width: 12, height: 12, borderRadius: '50%',
            background: color, flexShrink: 0,
            boxShadow: `0 0 6px ${color}`,
          }}
        />
        <span className="panel-title">{faction.tag}</span>
        {faction.isDormant && <span className="badge" style={{ background: '#333', color: '#888', fontSize: 10 }}>dormant</span>}
        <button className="panel-close" onClick={onClose}>✕</button>
      </div>

      {/* Tabs */}
      <div style={{ display: 'flex', borderBottom: '1px solid var(--border)', padding: '0 14px' }}>
        {(['overview', 'relations', 'demands', 'finance', 'log'] as Tab[]).map(t => (
          <button
            key={t}
            onClick={() => setTab(t)}
            style={{
              background: 'none', border: 'none', cursor: 'pointer',
              padding: '7px 10px',
              fontSize: 12, fontWeight: tab === t ? 600 : 400,
              color: tab === t ? 'var(--text-1)' : 'var(--text-2)',
              borderBottom: tab === t ? `2px solid ${color}` : '2px solid transparent',
              marginBottom: -1,
            }}
          >
            {t.charAt(0).toUpperCase() + t.slice(1)}
          </button>
        ))}
      </div>

      <div className="panel-body">
        {tab === 'overview' && <OverviewTab faction={faction} nodes={nodes} fleets={fleets} underAttack={underAttack} color={color} onSelectNode={onSelectNode} onSelectFleet={onSelectFleet} onSelectCampaignFocus={onSelectCampaignFocus} />}
        {tab === 'relations' && <RelationsTab faction={faction} onSelectFaction={onSelectFaction} />}
        {tab === 'demands' && <DemandsTab faction={faction} liveDemandsEvent={liveDemandsEvent} nodes={nodes} onSelectNode={onSelectNode} />}
        {tab === 'finance' && <FinanceTab faction={faction} />}
        {tab === 'log' && <LogTab faction={faction} />}
      </div>
    </div>
  )
}

// ---- Overview tab ----------------------------------------------------------------

function OverviewTab({ faction, nodes, fleets, underAttack, color, onSelectNode, onSelectFleet, onSelectCampaignFocus }: {
  faction: FactionDto
  nodes: ReturnType<typeof Array.prototype.filter>
  fleets: ReturnType<typeof Array.prototype.filter>
  underAttack: ReturnType<typeof Array.prototype.filter>
  color: string
  onSelectNode: (id: string) => void
  onSelectFleet: (id: string) => void
  onSelectCampaignFocus: (focus: CampaignFocusDto) => void
}) {
  // A dwell is minutes long and the countdown has to move, so this tab keeps its own clock while
  // any of its rows is showing one.
  const [nowMs, setNowMs] = useState(() => Date.now())
  const showsCountdown = underAttack.length > 0 || fleets.some((f: any) => isDwelling(f))
  useEffect(() => {
    if (!showsCountdown) return
    setNowMs(Date.now())
    const t = setInterval(() => setNowMs(Date.now()), 1000)
    return () => clearInterval(t)
  }, [showsCountdown])

  return (
    <>
      {/* Header stats */}
      <div className="kv-list">
        <div className="kv-row">
          <span className="kv-label">Personality</span>
          <span className="badge">{faction.personalityId}</span>
        </div>
        <div className="kv-row">
          <span className="kv-label">Treasury</span>
          <span className="kv-value">{faction.treasury.toLocaleString()} cr</span>
        </div>
        {faction.debt > 0 && (
          <div className="kv-row">
            <span className="kv-label">Debt</span>
            <span className="kv-value" style={{ color: '#e74c3c' }}>−{faction.debt.toLocaleString()} cr</span>
          </div>
        )}
        <div className="kv-row">
          <span className="kv-label">Assets</span>
          <span className="kv-value">{faction.assetValue.toLocaleString()} cr</span>
        </div>
        <div className="kv-row">
          <span className="kv-label">Net Worth</span>
          <span className="kv-value" style={{ color: faction.netWorth >= 0 ? '#2ecc71' : '#e74c3c' }}>
            {faction.netWorth.toLocaleString()} cr
          </span>
        </div>
        <div className="kv-row">
          <span className="kv-label">Nodes</span>
          <span className="kv-value">{nodes.length}</span>
        </div>
        {typeof faction.shipCount === 'number' && (
          <div className="kv-row">
            <span className="kv-label">Ships</span>
            <span className="kv-value" title="Hulls parked at the faction's nodes plus hulls aboard its fleets">
              {faction.shipCount}
              <span className="kv-sub"> ({faction.idleShipCount ?? 0} idle · {faction.inFlightShipCount ?? 0} in flight)</span>
            </span>
          </div>
        )}
        <div className="kv-row">
          <span className="kv-label">Active fleets</span>
          <span className="kv-value">{fleets.length}</span>
        </div>
        {(faction.hullsInRepair ?? 0) > 0 && (
          <div className="kv-row">
            <span className="kv-label">Hulls in repair</span>
            <span className="kv-value" title="Damaged hulls in node repair queues; not in stock and not flying">
              {faction.hullsInRepair} ({faction.hullsWaitingForParts ?? 0} waiting for parts)
            </span>
          </div>
        )}
      </div>

      {faction.campaignFoci.length > 0 && (
        <div>
          <div className="section-title">Defensive Campaign Focuses</div>
          {faction.campaignFoci.map(focus => (
            <button
              key={focus.id}
              type="button"
              className="kv-row"
              onClick={() => onSelectCampaignFocus(focus)}
              title="Center the map on this campaign focus region"
              style={{ alignItems: 'flex-start', width: '100%', cursor: 'pointer', background: 'none', border: 'none', color: 'inherit', padding: '4px 0', textAlign: 'left' }}
            >
              <span className="kv-label">{focus.centerNodeId} · {(focus.priority * 100).toFixed(0)}%</span>
              <span className="kv-value" style={{ fontSize: 11 }}>
                {focus.objectives.map(objective => objective.kind).join(', ') || 'DefendRegion'} · {focus.expiresAtUtc === '0001-01-01T00:00:00' ? 'legacy expiry pending migration' : `expires ${new Date(focus.expiresAtUtc).toLocaleTimeString()}`}
              </span>
            </button>
          ))}
        </div>
      )}

      {/* Drives */}
      <div>
        <div className="section-title">Drives</div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {DRIVE_NAMES.map((name, i) => {
            const val = faction.drives[i] ?? 0
            return (
              <div key={name} className="progress-wrap">
                <div className="progress-label">
                  <span style={{ color: DRIVE_COLORS[i] }}>{name}</span>
                  <span>{(val * 100).toFixed(0)}%</span>
                </div>
                <div className="progress-bar">
                  <div className="progress-fill" style={{ width: `${val * 100}%`, background: DRIVE_COLORS[i] }} />
                </div>
              </div>
            )
          })}
        </div>
      </div>

      {/* Nodes list */}
      {nodes.length > 0 && (
        <div>
          <div className="section-title">Owned Nodes</div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
            {nodes.map((n: any) => {
              const caps: [string, number][] = n.capabilities ? Object.entries(n.capabilities) : []
              return (
                <div
                  key={n.id}
                  style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '3px 0', cursor: 'pointer' }}
                  onClick={() => onSelectNode(n.id)}
                >
                  <span style={{ fontSize: 12, color: 'var(--accent)' }}>{n.displayName ?? n.id}</span>
                  <div style={{ display: 'flex', gap: 5, alignItems: 'center' }}>
                    <span style={{ fontSize: 10, color: 'var(--text-3)' }}>{nodeLabel(n)}</span>
                    {caps.map(([cap, lvl]) => {
                      const abbr = CAP_ABBR[cap] ?? cap[0]
                      const pipColor = CAP_COLOR[cap] ?? '#8b949e'
                      return (
                        <span
                          key={cap}
                          title={`${cap} Lv${lvl}`}
                          style={{
                            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                            width: 14, height: 14, borderRadius: 3,
                            background: pipColor + '28', border: `1px solid ${pipColor}88`,
                            fontSize: 9, fontWeight: 700, color: pipColor, lineHeight: 1,
                          }}
                        >
                          {abbr}
                        </span>
                      )
                    })}
                    <div style={{ width: 30, height: 4, background: 'var(--bg-3)', borderRadius: 2, overflow: 'hidden' }}>
                      <div style={{ width: `${n.holdStrength * 100}%`, height: '100%', background: color, borderRadius: 2 }} />
                    </div>
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* Under attack — raids fighting at this faction's nodes right now. Above Active Fleets:
          what is being done to you outranks what you are doing. */}
      {underAttack.length > 0 && (
        <div>
          <div className="section-title" style={{ color: ENGAGEMENT_COLOR }}>
            Under attack ({underAttack.length})
          </div>
          {underAttack.map((f: any) => {
            const remaining = engagementRemainingMs(f, nowMs)
            const ratio = engagementElapsedRatio(f, nowMs)
            return (
              <button key={f.fleetId} type="button" style={{ marginBottom: 6, cursor: 'pointer', background: 'none', border: 'none', padding: 0, color: 'inherit', width: '100%', textAlign: 'left' }} onClick={() => onSelectFleet(f.fleetId)}>
                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, marginBottom: 2 }}>
                  <span>
                    <span className="chip" style={{ background: ENGAGEMENT_COLOR + '33', color: ENGAGEMENT_COLOR }}>{f.kind}</span>
                    {' '}{f.actorTag} @ {f.targetNodeId ?? '?'}
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
        </div>
      )}

      {/* Fleets */}
      {fleets.length > 0 && (
        <div>
          <div className="section-title">Active Fleets</div>
          {fleets.map((f: any) => {
            // A dwelling fleet is parked on its target: the bar is its dwell running out, not
            // ground covered. Which dwell decides the colour and the glyph — a raid landing on
            // someone and a reinforce landing on your own station are opposite events.
            const dwelling = isDwelling(f)
            const attacking = isEngaging(f)
            const defending = isHoldingDefence(f)
            const dwellColour = attacking ? ENGAGEMENT_COLOR : DEFENCE_HOLD_COLOR
            const dep = new Date(f.departedUtc).getTime()
            const arr = new Date(f.estimatedArrivalUtc).getTime()
            // Both readings are elapsed/total over the leg's own clock; on an Engage leg that clock
            // is the dwell — which is why the row says so instead of printing a bare percentage.
            const ratio = dwelling
              ? engagementElapsedRatio(f, nowMs)
              : arr > dep ? Math.max(0, Math.min(1, (nowMs - dep) / (arr - dep))) : 1
            const remaining = dwelling ? engagementRemainingMs(f, nowMs) : 0
            return (
              <button key={f.fleetId} type="button" style={{ marginBottom: 6, cursor: 'pointer', background: 'none', border: 'none', padding: 0, color: 'inherit', width: '100%', textAlign: 'left' }} onClick={() => onSelectFleet(f.fleetId)}>
                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, marginBottom: 2 }}>
                  <span>
                    <span className={`chip chip-${f.kind}`}>{f.kind}</span>
                    {attacking ? ` ⚔ ${f.targetNodeId ?? f.targetTag ?? '?'}`
                      : defending ? ` 🛡 ${f.targetNodeId ?? '?'}`
                      : ` → ${f.targetTag ?? f.targetNodeId ?? '?'}`}
                  </span>
                  {dwelling ? (
                    <span style={{ color: dwellColour, fontVariantNumeric: 'tabular-nums' }}>
                      {remaining > 0 ? `${formatEngagementRemaining(remaining)} left` : 'Resolving'}
                    </span>
                  ) : (
                    <span style={{ color: 'var(--text-2)' }}>{(ratio * 100).toFixed(0)}%</span>
                  )}
                </div>
                <div className="progress-bar" style={{ height: 3 }}>
                  <div className="progress-fill" style={{ width: `${ratio * 100}%`, background: dwelling ? dwellColour : factionColorHex(f.actorTag) }} />
                </div>
              </button>
            )
          })}
        </div>
      )}
    </>
  )
}

// ---- Relations tab ----------------------------------------------------------------

function RelationsTab({ faction, onSelectFaction }: { faction: FactionDto; onSelectFaction: (t: string) => void }) {
  const entries = Object.entries(faction.relations).sort((a, b) => a[1] - b[1])
  if (entries.length === 0) return <div className="empty-msg">No known actors · open Relations view for the full matrix</div>

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {entries.map(([tag, standing]) => {
        const fatigue = faction.fatigue?.[tag] ?? 0
        const truce = faction.truceUntil?.[tag]
        const color = standing >= 0
          ? `rgb(${Math.round(46 + (1 - standing) * 80)},${Math.round(100 + standing * 104)},${Math.round(standing * 50)})`
          : `rgb(${Math.round(180 + Math.abs(standing) * 51)},${Math.round(76 * (1 - Math.abs(standing)))},${Math.round(60 * (1 - Math.abs(standing)))})`
        return (
          <div key={tag}>
            <div className="relation-row">
              <span style={{ width: 48, fontSize: 11, fontWeight: 600, cursor: 'pointer', color: 'var(--accent)' }}
                onClick={() => onSelectFaction(tag)}>{tag}</span>
              <div className="relation-bar-bg" title={`Standing: ${standing.toFixed(3)}`}>
                {standing >= 0
                  ? <div className="relation-fill" style={{ left: '50%', width: `${standing * 50}%`, background: color }} />
                  : <div className="relation-fill" style={{ right: '50%', width: `${Math.abs(standing) * 50}%`, background: color }} />
                }
                <div style={{ position: 'absolute', left: '50%', top: 0, bottom: 0, width: 1, background: 'var(--text-3)' }} />
              </div>
              <span style={{ width: 36, textAlign: 'right', fontSize: 10, color: 'var(--text-2)' }}>{standing.toFixed(2)}</span>
            </div>
            {(fatigue > 0 || truce) && (
              <div style={{ display: 'flex', gap: 8, paddingLeft: 54, fontSize: 10, color: 'var(--text-3)' }}>
                {fatigue > 0 && <span>fatigue {(fatigue * 100).toFixed(0)}%</span>}
                {truce && <span>truce until {new Date(truce).toLocaleTimeString()}</span>}
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}

// ---- Demands tab ----------------------------------------------------------------

/** Milliseconds a demand has been continuously open, measured against the snapshot's own `at`
 *  rather than the browser clock so a stale tab does not inflate every age. */
function demandAgeMs(firstPostedUtc: string, at: string): number {
  return new Date(at).getTime() - new Date(firstPostedUtc).getTime()
}

function formatDemandAge(firstPostedUtc: string, at: string): string {
  const ms = demandAgeMs(firstPostedUtc, at)
  if (!isFinite(ms) || ms < 0) return '—'
  const mins = Math.floor(ms / 60000)
  if (mins < 60) return `${mins}m`
  const hours = Math.floor(mins / 60)
  if (hours < 24) return `${hours}h${mins % 60}m`
  return `${Math.floor(hours / 24)}d${hours % 24}h`
}

/** A ship demand should clear within an ops cycle or two once stock exists; anything still open
 *  after an hour is the "stuck demand" signal this column exists to surface. */
function demandAgeColor(firstPostedUtc: string, at: string): string {
  const ms = demandAgeMs(firstPostedUtc, at)
  if (ms >= 6 * 3600_000) return 'var(--danger, #e74c3c)'
  if (ms >= 3600_000) return 'var(--warn, #f39c12)'
  return 'var(--text-3)'
}

/** Colour by how expensive the class is to leave unmet, in the same order DemandClass ranks them.
 *  Unclassified never reaches here — an unstamped row shows no badge rather than a grey one, so a
 *  missing class reads as missing rather than as the weakest one. */
const DEMAND_CLASS_COLOR: Record<string, string> = {
  Catalog:     'var(--text-3)',
  StoreStock:  '#7f8c8d',
  RecipeInput: '#3498db',
  Install:     '#9b59b6',
  ShipBuild:   '#e67e22',
}

function DemandClassBadge({ cls }: { cls?: string }) {
  if (!cls || cls === 'Unclassified') return null
  return (
    <span
      style={{
        marginLeft: 6, fontSize: 9, letterSpacing: 0.3, textTransform: 'uppercase',
        color: DEMAND_CLASS_COLOR[cls] ?? 'var(--text-3)',
        border: `1px solid ${DEMAND_CLASS_COLOR[cls] ?? 'var(--text-3)'}`,
        borderRadius: 2, padding: '0 3px',
      }}
      title={`Raised as ${cls} — what the demand is for, and therefore what a shortfall of it costs`}
    >
      {cls}
    </span>
  )
}

/** How close the node is to covering the want. Red at nothing on hand: a row that sits here while
 *  its quantity never falls is one no dispatch can close, which is the signal worth seeing. */
function haveColor(have: number, qty: number): string {
  if (qty <= 0) return 'var(--text-3)'
  if (have <= 0) return 'var(--danger, #e74c3c)'
  if (have < qty * 0.1) return 'var(--warn, #f39c12)'
  return 'var(--text-3)'
}

/** One item demand. `have` beside `qty` is the whole reading: 464,074 wanted against 3,400 held is
 *  a demand no trip can retire, and it looks identical to a met one without the second number. */
function ItemDemandRow({ d, at, onSelectNode, nodeExists }: {
  d: DemandItem
  at: string
  onSelectNode: (id: string) => void
  nodeExists: boolean
}) {
  return (
    <div
      onClick={() => d.nodeId && nodeExists && onSelectNode(d.nodeId)}
      style={{ fontSize: 11, color: 'var(--text-2)', cursor: nodeExists ? 'pointer' : 'default' }}
    >
      <span style={{ color: 'var(--text-1)' }}>{d.kind.replace('Item:', '')}</span>
      {d.qty > 0 && <span style={{ marginLeft: 6 }}>×{d.qty.toLocaleString()}</span>}
      {d.have != null && (
        <span style={{ marginLeft: 6, fontSize: 10, color: haveColor(d.have, d.qty) }}
              title="on hand at the demanding node, against the quantity asked for">
          have {d.have.toLocaleString()}
        </span>
      )}
      {' '}@ {d.nodeId || '—'}
      <DemandClassBadge cls={d.cls} />
      {d.urgency != null && (
        <span style={{ marginLeft: 6, fontSize: 10, color: 'var(--text-3)' }}
              title="the score this snapshot was ranked by — the same one dispatch orders on">
          u {d.urgency.toFixed(2)}
        </span>
      )}
      {d.firstPostedUtc && (
        <span
          style={{ marginLeft: 6, fontSize: 10, color: demandAgeColor(d.firstPostedUtc, at) }}
          title={`first posted ${new Date(d.firstPostedUtc).toLocaleString()}`}
        >
          open {formatDemandAge(d.firstPostedUtc, at)}
        </span>
      )}
    </div>
  )
}

// A faction's demand set runs to thousands of rows, so the snapshot carries a capped prefix per
// kind plus the real totals. Say what is not on screen rather than showing a truncated list as if it
// were the whole set.
function omittedNote(shown: number, total?: number) {
  if (total == null || total <= shown) return null
  return (
    <span style={{ marginLeft: 6, fontWeight: 'normal', color: 'var(--text-3)' }}>
      showing {shown.toLocaleString()} of {total.toLocaleString()}
    </span>
  )
}

function DemandsTab({ faction, liveDemandsEvent, nodes, onSelectNode }: {
  faction: FactionDto
  liveDemandsEvent: DemandsEvent | null
  nodes: ReturnType<typeof Array.prototype.filter>
  onSelectNode: (id: string) => void
}) {
  // Prefer live data if available
  if (liveDemandsEvent) {
    const { demands, investments, at, demandTotals } = liveDemandsEvent
    const shipDemands = demands.filter(d => d.kind.startsWith('Ship:'))
    const posDemands  = demands.filter(d => d.kind.startsWith('Position:'))
    const itemDemands = demands.filter(d => d.kind.startsWith('Item:'))
    const isEmpty = demands.length === 0 && investments.length === 0
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <div style={{ fontSize: 10, color: '#2ecc71' }}>
          ⚡ Live · last update {new Date(at).toLocaleTimeString()}
        </div>
        {isEmpty && <div className="empty-msg">No active demands or investments</div>}
        {shipDemands.length > 0 && <>
          <div className="section-title">Ship Demands{omittedNote(shipDemands.length, demandTotals?.ship)}</div>
          {shipDemands.map((d, i) => {
            const nodeExists = !!d.nodeId && nodes.some((n: any) => n.id === d.nodeId)
            return (
              <div
                key={i}
                onClick={() => d.nodeId && nodeExists && onSelectNode(d.nodeId)}
                style={{ fontSize: 11, color: 'var(--text-2)', cursor: nodeExists ? 'pointer' : 'default' }}
              >
                <span style={{ color: 'var(--text-1)' }}>{d.kind}</span> @ {d.nodeId || '—'}
                <DemandClassBadge cls={d.cls} />
                {d.destNodeId && <span style={{ marginLeft: 4 }}>→ {d.destNodeId}</span>}
                {d.viaNodeId && (
                  <span style={{ marginLeft: 4, fontSize: 10, color: 'var(--text-3)' }}>via {d.viaNodeId}</span>
                )}
                <span style={{ marginLeft: 6, fontSize: 10, color: 'var(--text-3)' }}>
                  exp {new Date(d.exp).toLocaleTimeString()}
                </span>
                {/* Age survives TTL refresh, so a long-lived value here is the signal that a demand
                    is genuinely stuck rather than simply being re-posted each cycle. */}
                {d.firstPostedUtc && (
                  <span
                    style={{ marginLeft: 6, fontSize: 10, color: demandAgeColor(d.firstPostedUtc, at) }}
                    title={`first posted ${new Date(d.firstPostedUtc).toLocaleString()}`}
                  >
                    open {formatDemandAge(d.firstPostedUtc, at)}
                  </span>
                )}
                {(d.requiredZone || d.requiredPlanetId || (d.minCargoLiters ?? 0) > 0) && (
                  <span style={{ marginLeft: 6, fontSize: 10, color: 'var(--text-3)' }}>
                    {d.requiredZone && <>zone={d.requiredZone} </>}
                    {d.requiredPlanetId && <>planet={d.requiredPlanetId} </>}
                    {(d.minCargoLiters ?? 0) > 0 && <>minCargo={Math.round(d.minCargoLiters ?? 0).toLocaleString()}</>}
                  </span>
                )}
              </div>
            )
          })}
        </>}
        {posDemands.length > 0 && <>
          <div className="section-title">Positioning Demands{omittedNote(posDemands.length, demandTotals?.position)}</div>
          {posDemands.map((d, i) => (
            <div key={i} style={{ fontSize: 11, color: 'var(--text-2)' }}>
              <span style={{ color: 'var(--text-1)' }}>{d.kind}</span> @ {d.nodeId || '—'}
            </div>
          ))}
        </>}
        {itemDemands.length > 0 && <>
          <div className="section-title">
            Item Demands{omittedNote(itemDemands.length, demandTotals?.item)}
            <span style={{ marginLeft: 6, fontWeight: 'normal', fontSize: 10, color: 'var(--text-3)' }}>
              most urgent first
            </span>
          </div>
          {itemDemands.map((d, i) => (
            <ItemDemandRow
              key={i}
              d={d}
              at={at}
              onSelectNode={onSelectNode}
              nodeExists={!!d.nodeId && nodes.some((n: any) => n.id === d.nodeId)}
            />
          ))}
        </>}
        {investments.length > 0 && <>
          <div className="section-title">Investment Signals</div>
          {investments.map((r, i) => {
            const urgencyColor = r.urgency > 0.7 ? '#e74c3c' : r.urgency > 0.4 ? '#f39c12' : '#95a5a6'
            const target = r.targetNodeId
            const targetExists = !!target && nodes.some((n: any) => n.id === target)
            const bom = r.bom ?? []
            const matProgress = bomProgress(r.bom)
            const shortRows = bom.filter(row => bomShortfall(row) > 0)
            return (
              <div key={i} className="progress-wrap">
                <div className="progress-label">
                  <span>
                    {r.cap}
                    {target
                      ? <span
                          onClick={() => targetExists && onSelectNode(target)}
                          style={{ color: 'var(--text-2)', cursor: targetExists ? 'pointer' : 'default' }}
                        > &rarr; {target}</span>
                      : <span style={{ color: 'var(--text-3)' }}> &rarr; no target</span>}
                  </span>
                  <span style={{ color: urgencyColor }}>{(r.urgency * 100).toFixed(0)}%</span>
                </div>
                <div className="progress-bar">
                  <div className="progress-fill" style={{ width: `${r.urgency * 100}%`, background: urgencyColor }} />
                </div>
                {(r.blockedReason || r.targetPlanetId || r.affordable === false) && (
                  <div style={{ fontSize: 10, color: 'var(--text-3)', marginTop: 2 }}>
                    {r.blockedReason && <span style={{ color: '#f39c12' }}>{r.blockedReason} </span>}
                    {r.targetPlanetId && (
                      <span>
                        planet={r.targetPlanetId}
                        {r.targetSite && ` (${r.targetSite === 'Surface' ? 'ground' : 'well'})`}
                        {' '}
                      </span>
                    )}
                    {r.affordable === false && <span>treasury below develop cost</span>}
                  </div>
                )}
                {bom.length > 0 && (
                  <div style={{ marginTop: 4 }}>
                    <div className="progress-label" style={{ fontSize: 10 }}>
                      <span style={{ color: 'var(--text-3)' }}>
                        materials{shortRows.length > 0
                          ? <span style={{ color: '#e74c3c' }}> — short {shortRows.length} of {bom.length}</span>
                          : <span style={{ color: '#2ecc71' }}> — BOM covered</span>}
                      </span>
                      <span style={{ color: 'var(--text-3)' }}>{(matProgress * 100).toFixed(0)}%</span>
                    </div>
                    <div className="progress-bar">
                      <div
                        className="progress-fill"
                        style={{
                          width: `${matProgress * 100}%`,
                          background: shortRows.length > 0 ? '#f1c40f' : '#2ecc71',
                        }}
                      />
                    </div>
                    <InstallBomTable rows={bom} />
                  </div>
                )}
              </div>
            )
          })}
        </>}
      </div>
    )
  }

  // Fallback: persisted demand pressure from brain file
  const pressure = faction.demandPressure
  const hasData = pressure && Object.keys(pressure).length > 0
  const maxVal = hasData ? Math.max(...Object.values(pressure!)) : 0
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{
        fontSize: 11, color: 'var(--text-2)',
        background: 'var(--surface-2, #161b22)', border: '1px solid var(--border)',
        borderRadius: 4, padding: '6px 8px', lineHeight: 1.5,
      }}>
        ○ Waiting for live data · Values below reflect the last flush
      </div>
      {hasData
        ? <>
            <div className="section-title">Item Demand Pressure (last flush)</div>
            {Object.entries(pressure!)
              .sort((a, b) => b[1] - a[1])
              .map(([item, val]) => (
                <div key={item} className="progress-wrap">
                  <div className="progress-label">
                    <span>{item}</span>
                    <span>{val.toFixed(0)}</span>
                  </div>
                  <div className="progress-bar">
                    <div className="progress-fill" style={{ width: `${(val / maxVal) * 100}%`, background: '#f1c40f' }} />
                  </div>
                </div>
              ))}
          </>
        : <div className="empty-msg">No persisted demand pressure</div>
      }
    </div>
  )
}

// ---- Log tab ----------------------------------------------------------------

function FinanceTab({ faction }: { faction: FactionDto }) {
  const [data, setData] = useState<FinanceSnapshotDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setData(null)
    setError(null)
    fetchFactionFinance(faction.tag)
      .then(setData)
      .catch(e => setError(String(e)))
  }, [faction.tag])

  if (error) return <div className="empty-msg" style={{ color: '#e74c3c' }}>{error}</div>
  if (data === null) return <div className="empty-msg">Loading…</div>

  const fmt = (v: number) => `${v.toLocaleString()} cr`
  const report = data.report ?? []
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div className="kv-list">
        <div className="kv-row"><span className="kv-label">Treasury</span><span className="kv-value">{fmt(data.treasury)}</span></div>
        <div className="kv-row"><span className="kv-label">Assets</span><span className="kv-value">{fmt(data.assets)}</span></div>
        <div className="kv-row"><span className="kv-label">Net Worth</span><span className="kv-value" style={{ color: data.netWorth >= 0 ? '#2ecc71' : '#e74c3c' }}>{fmt(data.netWorth)}</span></div>
      </div>

      <div>
        <div className="section-title">Income / Expense Report</div>
        <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, color: 'var(--text-2)', marginBottom: 6 }}>
          <span>Income {fmt(data.incomePerQuantum)} / quantum</span>
          <span>Expense {fmt(data.expensePerQuantum)} / quantum</span>
          <span>Net {fmt(data.netPerQuantum)} / quantum</span>
        </div>
        {report.length === 0
          ? <div className="empty-msg">No monetary events available yet</div>
          : report.map((row, i) => {
              const sign = row.amount >= 0 ? '+' : '−'
              const tone = row.amount >= 0 ? '#2ecc71' : '#e74c3c'
              return (
                <div key={i} style={{ display: 'flex', justifyContent: 'space-between', gap: 8, fontSize: 11, padding: '3px 0', borderBottom: '1px solid var(--border)' }}>
                  <span style={{ color: 'var(--text-2)' }}>{row.scope ?? '—'} · {row.type ?? 'event'}</span>
                  <span style={{ color: tone, whiteSpace: 'nowrap' }}>{sign}{fmt(Math.abs(row.amount))}</span>
                </div>
              )
            })}
      </div>
    </div>
  )
}

function LogTab({ faction }: { faction: FactionDto }) {
  const [lines, setLines] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setLines(null)
    setError(null)
    fetchFactionLog(faction.tag)
      .then(setLines)
      .catch(e => setError(String(e)))
  }, [faction.tag])

  if (error) return <div className="empty-msg" style={{ color: '#e74c3c' }}>{error}</div>
  if (lines === null) return <div className="empty-msg">Loading…</div>
  if (lines.length === 0) return <div className="empty-msg">No log entries for {faction.tag} in the last two days</div>

  return (
    <div>
      <div className="section-title">Activity Log — {faction.tag} (newest first)</div>
      {[...lines].reverse().map((line, i) => (
        <div key={i} className="log-entry" style={{ fontFamily: 'monospace', fontSize: 11, whiteSpace: 'pre' }}>{line}</div>
      ))}
    </div>
  )
}
