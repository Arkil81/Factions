/** A player siege, as the dashboard has to read it.
 *
 *  A siege is the one attack on a node that arrives with no fleet behind it. Everything the map
 *  drew for a fight up to now hung off a `FleetDto` — the engagement ring is built inside the
 *  per-fleet loop and tested with `isEngaging` — so a player breaching and raiding a station drew
 *  nothing at all, on a board that would have flashed for an NPC doing the same thing to the same
 *  node. See docs/issues/FAC-258.
 *
 *  Times are read against a caller-supplied `nowMs`, which is `Date.now()` everywhere this is used
 *  today — the same clock `fleetEngagement.ts` reads, and for the same reason: a siege countdown
 *  has to tick between polls, and the snapshot's `utcNow` only moves when a new snapshot lands.
 *  Every stamp on the wire is absolute UTC, so nothing here depends on the two agreeing beyond the
 *  browser clock being roughly right.
 */
import type { NodeDto, SiegeDto } from './types/world'

/** Which of the three things a siege is at this moment. The distinction is what an operator wants
 *  at a glance: a shield being worn down is still deniable, a breached station is a fight in
 *  progress, and a won raid is a node about to change hands. */
export type SiegePhase = 'besieged' | 'breached' | 'raided'

/** A shield being worn down. Shares the engagement colour: an NPC raid and a player siege are the
 *  same event to whoever owns the node. */
export const SIEGE_COLOR = '#ff6b4a'
/** The shield is down and the station itself is being shot — hotter, because this is the phase in
 *  which the node can actually be lost. */
export const BREACH_COLOR = '#ff2d2d'

export function siegePhase(siege: SiegeDto): SiegePhase {
  if (siege.succeededUtc) return 'raided'
  if (siege.breachedUtc) return 'breached'
  return 'besieged'
}

export function siegeColor(siege: SiegeDto): string {
  return siegePhase(siege) === 'besieged' ? SIEGE_COLOR : BREACH_COLOR
}

/** The siege on this node, or null. Written as a reading rather than a field test so a caller
 *  cannot accidentally treat the `siege` key's presence on an older snapshot as truth. */
export function siegeOf(node: Pick<NodeDto, 'siege'>): SiegeDto | null {
  return node.siege ?? null
}

/** Milliseconds left before the attacker has held the siege long enough to breach, or null once
 *  the shield is already down — after a breach the dwell is spent and the clock means nothing.
 *
 *  Note this is only half the breach condition: the pool must also be empty at that moment. A
 *  countdown at zero says the attacker has held it long enough, not that the shield has dropped. */
export function siegeDwellRemainingMs(siege: SiegeDto, nowMs = Date.now()): number | null {
  if (siege.breachedUtc) return null
  const start = new Date(siege.startedUtc).getTime()
  if (!isFinite(start) || siege.dwellSeconds <= 0) return null
  return Math.max(0, start + siege.dwellSeconds * 1000 - nowMs)
}

/** How long the attack has been running, in milliseconds. Counts from the first absorbed round and
 *  keeps counting through the breach, because that is the number that answers "how long has this
 *  been going on while I was not looking". */
export function siegeElapsedMs(siege: SiegeDto, nowMs = Date.now()): number {
  const start = new Date(siege.startedUtc).getTime()
  if (!isFinite(start)) return 0
  return Math.max(0, nowMs - start)
}

/** A duration an operator reads at a glance — "2m 41s", "38s". Seconds stay visible throughout:
 *  a siege is minutes long and its last part is the part still worth acting on. */
export function formatSiegeDuration(ms: number): string {
  const secs = Math.max(0, Math.floor(ms / 1000))
  if (secs < 60) return `${secs}s`
  const mins = Math.floor(secs / 60)
  if (mins < 60) return `${mins}m ${secs % 60}s`
  return `${Math.floor(mins / 60)}h ${mins % 60}m`
}

/** One line for a panel row or a map label. Says the phase first, because that is what decides
 *  whether anyone has to do anything about it. */
export function siegeSummary(siege: SiegeDto, nowMs = Date.now()): string {
  switch (siegePhase(siege)) {
    case 'raided':
      return `raided — conquer clock running (${formatSiegeDuration(siegeElapsedMs(siege, nowMs))})`
    case 'breached':
      return `breached — station under fire, ${Math.round(siege.raidProgress * 100)}% to loss`
    default: {
      const left = siegeDwellRemainingMs(siege, nowMs)
      const shield = `shield ${Math.round(siege.integrity * 100)}%`
      return left === null ? shield : `${shield}, breach in ${formatSiegeDuration(left)}`
    }
  }
}
