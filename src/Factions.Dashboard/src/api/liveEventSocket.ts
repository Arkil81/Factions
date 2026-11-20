import type { InstallBomRow } from '../installBom'
export type { InstallBomRow }

export interface FleetDispatchEvent {
  type: 'fleet_dispatch'
  at: string
  factionTag: string
  fleetId: string
  kind: string
  originNodeId: string
  targetNodeId: string
  targetTag: string | null
}

/** What raised a demand, weakest to strongest — the same DemandClass the dispatcher weighs.
 *  Absent only on a row from a server older than the field. */
export type DemandClass =
  | 'Unclassified' | 'Catalog' | 'StoreStock' | 'RecipeInput' | 'Install' | 'ShipBuild'

export interface DemandItem {
  kind: string
  nodeId: string
  qty: number
  exp: string
  /** Why this demand exists. A shortfall of ore feeding a blocked hull and the hull's own missing
   *  component are the same chain at different weights, and the quantity alone never shows it. */
  cls?: DemandClass
  /** The score the snapshot was ranked by — the same one the dispatcher orders on. Item rows only;
   *  Ship:/Position: rows carry no quantity, so they are unscored rather than zero. */
  urgency?: number
  /** What the demanding node holds of the key. Present exactly when `urgency` is: absent means the
   *  row was never measured, `0` means the node holds none. */
  have?: number
  // Carried only when they constrain something.
  minCargoLiters?: number
  requiredPlanetId?: string | null
  requiredZone?: 'DeepSpace' | 'Orbit' | 'Surface' | null
  // Ship: demands only.
  destNodeId?: string | null
  viaNodeId?: string | null
  firstPostedUtc?: string | null
}

export interface InvestmentItem {
  cap: string
  urgency: number
  exp?: string
  /** Owned node the brain would install this capability at; null when nothing qualifies. */
  targetNodeId?: string | null
  /** Set when the request is scoped to one planet's local capacity. */
  targetPlanetId?: string | null
  /** Which half of that body was asked for: 'Surface' (its ground) or 'Orbital' (its well). */
  targetSite?: string | null
  /** True when a region-scoped request resolved inside the region it named. A region-scoped request
   *  that resolves at all always did — there is no off-region fallback — so this reads false only on
   *  an unresolved target, where blockedReason carries the reason. */
  onTargetPlanet?: boolean
  /** Why no node qualified, when targetNodeId is null. */
  blockedReason?: string | null
  /** False when the faction cannot currently fund a Develop action. */
  affordable?: boolean
  /** The install's BOM and progress against it; omitted when the capability costs no items. */
  bom?: InstallBomRow[]
}

/** Full per-kind demand counts, present only when `demands` carries a capped prefix of them. */
export interface DemandTotals {
  ship?: number
  position?: number
  item?: number
  other?: number
}

export interface DemandsEvent {
  type: 'demands'
  at: string
  factionTag: string
  demands: DemandItem[]
  investments: InvestmentItem[]
  demandTotals?: DemandTotals
}

export interface HeartbeatEvent {
  type: 'heartbeat'
  at: string
  simTick: number
}

export type LiveEvent = FleetDispatchEvent | DemandsEvent | HeartbeatEvent

/** The slice of the browser WebSocket this module uses, so tests can supply their own. */
export interface LiveEventSocketLike {
  close(): void
  onopen: ((ev: unknown) => void) | null
  onclose: ((ev: unknown) => void) | null
  onerror: ((ev: unknown) => void) | null
  onmessage: ((ev: { data: unknown }) => void) | null
}

export interface LiveEventSink {
  /** Called once per received event, as it arrives. */
  onEvent(evt: LiveEvent): void
  onConnectedChange(connected: boolean): void
}

export interface LiveEventSocketOptions {
  createSocket?: (url: string) => LiveEventSocketLike
  reconnectDelayMs?: number
  schedule?: (fn: () => void, ms: number) => unknown
  cancel?: (handle: unknown) => void
}

/**
 * Opens `url`, hands every event to `sink.onEvent` as it arrives, and reconnects after a drop until
 * the returned disposer is called.
 *
 * Events are delivered by call, never through a "last event" value the consumer observes afterwards:
 * two events that land in one React render batch leave the first one unobserved, which is how a
 * faction's demands snapshot could reach the browser on every flush sweep and still never be applied
 * (FAC-001 — the last faction in the roster is followed immediately by the sweep's heartbeat, so it
 * was the one systematically superseded).
 */
export function connectLiveEvents(
  url: string,
  sink: LiveEventSink,
  opts: LiveEventSocketOptions = {},
): () => void {
  const createSocket = opts.createSocket ?? ((u: string) => new WebSocket(u) as unknown as LiveEventSocketLike)
  const reconnectDelayMs = opts.reconnectDelayMs ?? 3000
  const schedule = opts.schedule ?? ((fn: () => void, ms: number) => setTimeout(fn, ms))
  const cancel = opts.cancel ?? ((h: unknown) => clearTimeout(h as ReturnType<typeof setTimeout>))

  let disposed = false
  let socket: LiveEventSocketLike | null = null
  let reconnectHandle: unknown = null

  function connect() {
    if (disposed) return
    const ws = createSocket(url)
    socket = ws

    ws.onopen = () => { if (!disposed) sink.onConnectedChange(true) }
    ws.onclose = () => {
      if (disposed) return
      sink.onConnectedChange(false)
      reconnectHandle = schedule(connect, reconnectDelayMs)
    }
    ws.onerror = () => ws.close()
    ws.onmessage = (e: { data: unknown }) => {
      if (disposed) return
      let evt: LiveEvent
      try {
        evt = JSON.parse(e.data as string) as LiveEvent
      } catch {
        return // malformed line — ignore, the stream continues
      }
      sink.onEvent(evt)
    }
  }

  connect()

  return () => {
    disposed = true
    if (reconnectHandle !== null) cancel(reconnectHandle)
    socket?.close()
  }
}
