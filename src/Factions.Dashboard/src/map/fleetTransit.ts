export interface TransitFleet {
  departedUtc: string
  estimatedArrivalUtc: string
  transitDistanceKm: number
  fleetSpeedMinKmPerMin: number
  fleetSpeedKmPerMin: number
  fleetAccelKmPerMin2: number
  completionRatio: number
  /** Active clearance polyline. routeCoveredKm is measured from the route's start to the END of the
   * current leg, so the leg the client can draw begins at routeCoveredKm - transitDistanceKm.
   * All zero/absent when the fleet is flying a plain leg. */
  routeDistanceKm?: number
  routeCoveredKm?: number
  routeDepartedUtc?: string
}

export function trailMilestoneDistances(previousKm: number, currentKm: number, spacingKm = 100): number[] {
  if (spacingKm <= 0 || currentKm <= previousKm) return []
  const first = Math.floor(previousKm / spacingKm + 1) * spacingKm
  const milestones: number[] = []
  for (let distance = first; distance <= currentKm; distance += spacingKm) milestones.push(distance)
  return milestones
}

/** Distance covered under the server's trapezoidal accelerate/cruise/decelerate profile, or null
 * when the inputs cannot describe one. Mirrors FactionTuning.TransitProgressRatio. */
function profileCoveredKm(
  distanceKm: number,
  elapsedMins: number,
  vMin: number,
  vMax: number,
  accel: number,
): number | null {
  if (distanceKm <= 0 || vMin < 0 || vMax <= 0 || vMax < vMin || accel <= 0) return null
  const delta = vMax - vMin
  if (delta <= 0) return Math.max(0, Math.min(distanceKm, elapsedMins * vMax))
  const ramp = delta * delta / (2 * accel)
  const dAccel = Math.min(distanceKm / 2, ramp)
  const vPeak = vMin + delta * (dAccel / ramp)
  const accelMins = 2 * dAccel / (vMin + vPeak)
  const cruiseMins = Math.max(0, distanceKm - 2 * dAccel) / vPeak
  const total = 2 * accelMins + cruiseMins
  if (elapsedMins <= 0) return 0
  if (elapsedMins >= total) return distanceKm
  if (elapsedMins <= accelMins) {
    return vMin * elapsedMins + (vPeak - vMin) * elapsedMins * elapsedMins / (2 * accelMins)
  }
  if (elapsedMins <= accelMins + cruiseMins) {
    return dAccel + vPeak * (elapsedMins - accelMins)
  }
  const remaining = total - elapsedMins
  return distanceKm - (vMin * remaining + (vPeak - vMin) * remaining * remaining / (2 * accelMins))
}

export function kinematicTransitRatio(fleet: TransitFleet, nowMs = Date.now()): number {
  const elapsed = (nowMs - new Date(fleet.departedUtc).getTime()) / 60000
  const covered = profileCoveredKm(
    fleet.transitDistanceKm, elapsed,
    fleet.fleetSpeedMinKmPerMin, fleet.fleetSpeedKmPerMin, fleet.fleetAccelKmPerMin2,
  )
  if (covered === null) return fleet.completionRatio
  return Math.max(0, Math.min(1, covered / fleet.transitDistanceKm))
}

/** True when the fleet is flying a generated clearance polyline around a body. */
export function isOnClearanceRoute(fleet: TransitFleet): boolean {
  return (fleet.routeDistanceKm ?? 0) > 0 && !!fleet.routeDepartedUtc
}

/** Distance flown along the WHOLE clearance polyline, counted from the route's own departure. It
 * only ever increases, so it is the right input for trail spacing — per-leg distance restarts at
 * every waypoint and would stall the trail on the short chords of an arc. */
export function routeFlownKm(fleet: TransitFleet, nowMs = Date.now()): number | null {
  if (!isOnClearanceRoute(fleet)) return null
  const total = fleet.routeDistanceKm as number
  const elapsed = (nowMs - new Date(fleet.routeDepartedUtc as string).getTime()) / 60000
  const covered = profileCoveredKm(
    total, elapsed,
    fleet.fleetSpeedMinKmPerMin, fleet.fleetSpeedKmPerMin, fleet.fleetAccelKmPerMin2,
  )
  if (covered === null) return null
  return Math.max(0, Math.min(total, covered))
}

/** Distance flown along the part of the polyline a client can draw, which starts at the current
 * leg's origin. Animating this instead of the current leg alone is what makes an arc read as one
 * continuous curve rather than a hop that freezes at each waypoint between polls. */
export function routeLegProgressKm(fleet: TransitFleet, nowMs = Date.now()): number | null {
  const flown = routeFlownKm(fleet, nowMs)
  if (flown === null) return null
  const legStartKm = Math.max(0, (fleet.routeCoveredKm ?? 0) - fleet.transitDistanceKm)
  return Math.max(0, flown - legStartKm)
}
