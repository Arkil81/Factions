/** The dwell at a target, as the dashboard has to read it.
 *
 *  A fleet on a `FleetStopKind.Engage` leg is parked on the node it flew to and is spending a
 *  duration there; its effect lands when that leg resolves. Two opposite missions hold that same
 *  leg. A Raid, Retaliate or Conquer holds it for `FactionTuning.RaidDwellSeconds` to attack the
 *  node — the dwell is the fight, and it is the window in which a station, an NPC Guard or a
 *  player can still change the outcome. A Reinforce holds it for `FactionTuning.ReinforceDwellSeconds`
 *  to defend the node it came to shore up, delivering `ReinforceStep` when the leg resolves.
 *
 *  **The kind test is load-bearing, not defensive.** The stop kind alone says a fleet is dwelling,
 *  not that it is attacking: on a live board a faction's reinforces of its own stations outnumber
 *  the raids in progress, so reading the stop kind by itself paints a defender's own hull as an
 *  attack on its own node. `Factions.Wire.FleetEngagement` makes the identical conjunction for the
 *  in-game client, against Core's `EngagesOnArrival`.
 *
 *  Every time here is derived from the fleet's own clock rather than from `completionRatio` or
 *  `kinematicTransitRatio`. A dwell leg is billed from its duration, not from a distance flown:
 *  origin and target are the same node, so `transitDistanceKm` is 0, the kinematic helper has no
 *  profile to interpolate and falls through to `completionRatio` — which the API samples once when
 *  it builds the snapshot, and which therefore holds still between polls.
 */
import type { FleetDto } from './types/world'

/** The stop kind a fleet carries while it is holding at its target. */
export const ENGAGE_STOP_KIND = 'Engage'

/** The action kinds that hold the dwell in order to attack the node they are parked on. Mirrors
 *  Core's `EngagesOnArrival`; a fourth combat kind added there belongs here too. */
export const ATTACK_KINDS: readonly string[] = ['Raid', 'Retaliate', 'Conquer']

/** The action kinds that hold the same dwell in order to defend the node. `ActionKind.Guard` is
 *  not one of them: a Guard stands a duty window after resolving, not an Engage leg. */
export const DEFENCE_KINDS: readonly string[] = ['Reinforce']

/** A fight in progress, everywhere the dashboard draws one. Distinct from `FLEET_KIND_COLORS.Raid`:
 *  a raider in transit and a raider mid-fight are different things to look at. */
export const ENGAGEMENT_COLOR = '#ff6b4a'

/** A defensive hold in progress — the same dwell, read from the other side. Deliberately far from
 *  `ENGAGEMENT_COLOR` on the wheel: telling the two apart at a glance on the map is the whole
 *  point of separating them. */
export const DEFENCE_HOLD_COLOR = '#5dade2'

/** The fields a dwell reading needs — narrower than `FleetDto` so the map's animation
 *  structs and a panel row can both be passed straight in. */
export type EngagementFleet =
  Pick<FleetDto, 'kind' | 'currentStopKind' | 'departedUtc' | 'estimatedArrivalUtc'>

/** True while this fleet is holding at its target for a dwell, whichever side it is on. This is
 *  the reading for anything that cares that the fleet is parked rather than flying — a route row,
 *  a transit bar — where an attacker and a defender want the same treatment. */
export function isDwelling(fleet: EngagementFleet): boolean {
  return fleet.currentStopKind === ENGAGE_STOP_KIND
}

/** True while this fleet is holding at a node it is attacking. Fleets dispatched before the dwell
 *  existed, immediate dispatches, and every fleet at all when `RaidDwellSeconds` is 0 never carry
 *  this stop kind and read false. */
export function isEngaging(fleet: EngagementFleet): boolean {
  return isDwelling(fleet) && ATTACK_KINDS.indexOf(fleet.kind) >= 0
}

/** True while this fleet is holding at a node it is defending. */
export function isHoldingDefence(fleet: EngagementFleet): boolean {
  return isDwelling(fleet) && DEFENCE_KINDS.indexOf(fleet.kind) >= 0
}

/** The colour this dwell is drawn in, or null when the fleet is not on one. */
export function dwellColor(fleet: EngagementFleet): string | null {
  if (isEngaging(fleet)) return ENGAGEMENT_COLOR
  if (isHoldingDefence(fleet)) return DEFENCE_HOLD_COLOR
  return null
}

/** Total dwell length in milliseconds — `RaidDwellSeconds` or `ReinforceDwellSeconds` as this
 *  particular fleet was dispatched with it, which is why it is read off the fleet rather than
 *  assumed. */
export function engagementTotalMs(fleet: EngagementFleet): number {
  const start = new Date(fleet.departedUtc).getTime()
  const end = new Date(fleet.estimatedArrivalUtc).getTime()
  if (!isFinite(start) || !isFinite(end)) return 0
  return Math.max(0, end - start)
}

/** Milliseconds left in the dwell, clamped to 0 once it is up. On an Engage leg
 *  `estimatedArrivalUtc` is the leg's end, which is the moment the effect lands. */
export function engagementRemainingMs(fleet: EngagementFleet, nowMs = Date.now()): number {
  const end = new Date(fleet.estimatedArrivalUtc).getTime()
  if (!isFinite(end)) return 0
  return Math.max(0, end - nowMs)
}

/** 0..1 fraction of the dwell already spent. This is the ratio it resolves at if the fleet is
 *  destroyed right now (`PendingFleetResolver.LossRatioFor`), so it reads as progress toward the
 *  effect landing — not as distance covered, and it must not be labelled as transit. */
export function engagementElapsedRatio(fleet: EngagementFleet, nowMs = Date.now()): number {
  const total = engagementTotalMs(fleet)
  if (total <= 0) return 1
  return Math.max(0, Math.min(1, 1 - engagementRemainingMs(fleet, nowMs) / total))
}

/** A countdown an operator reads at a glance — "2m 41s", "38s". Seconds stay visible throughout:
 *  the whole dwell is only minutes long, and its last part is the part still worth acting on. */
export function formatEngagementRemaining(ms: number): string {
  const secs = Math.max(0, Math.floor(ms / 1000))
  if (secs < 60) return `${secs}s`
  const mins = Math.floor(secs / 60)
  if (mins < 60) return `${mins}m ${secs % 60}s`
  return `${Math.floor(mins / 60)}h ${mins % 60}m`
}

/** The attacks currently fighting at `nodeId`, whoever owns them. The defender's question — "is my
 *  station under attack right now" — is asked of a node, not of a fleet. */
export function engagementsAtNode(fleets: FleetDto[], nodeId: string): FleetDto[] {
  return fleets.filter(f => isEngaging(f) && f.targetNodeId === nodeId)
}

/** The reinforcements currently holding at `nodeId`. The other half of the same question: what is
 *  standing here, and how long before it delivers. */
export function defencesAtNode(fleets: FleetDto[], nodeId: string): FleetDto[] {
  return fleets.filter(f => isHoldingDefence(f) && f.targetNodeId === nodeId)
}
