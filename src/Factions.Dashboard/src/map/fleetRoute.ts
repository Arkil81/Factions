export interface FleetRoutePoint {
  x: number
  y: number
  z: number
}

/**
 * The visible route is the remaining itinerary: it starts at the fleet's
 * displayed position and ends at the current leg's next stop. Origin is
 * deliberately excluded because it is already behind the fleet.
 */
export function routeToNextStop(current: FleetRoutePoint, nextStop: FleetRoutePoint): FleetRoutePoint[] {
  return [current, nextStop]
}

/** The stop kind a computed clearance waypoint carries, as the API spells it. */
const NAVIGATION_KIND = 'Navigation'

/**
 * How many leading entries of a fleet's remaining stops belong to the clearance polyline it is
 * flying right now. Everything past that count is a leg the server has not routed yet: legs are
 * planned one at a time, on arrival at the stop before them, so a later stop is a bare destination
 * with no waypoints between here and it. Drawing one puts a straight line through whatever body
 * lies between — most visibly the gas giant at the system origin.
 *
 * The Navigation run carries the whole answer. A run of one or more means an arc is still ahead,
 * and the semantic stop closing that run is the point the arc was planned to reach, so it is routed
 * too. An empty run means the current target is already that point — the last chord of an arc, or a
 * plain direct leg — and whatever follows it is unrouted either way.
 *
 * Mirrors `FleetItinerary.RoutedStopCount` in Core, which the in-game map is served through.
 */
export function routedStopCount(stops: ReadonlyArray<{ kind: string }> | null | undefined): number {
  if (!stops) return 0
  let navigation = 0
  while (navigation < stops.length && stops[navigation].kind === NAVIGATION_KIND) navigation++
  if (navigation === 0) return 0
  return navigation < stops.length ? navigation + 1 : navigation
}

/**
 * How many leading vertices of a drawn route the fleet marker has already passed, and which
 * therefore collapse onto the marker rather than staying where they were.
 *
 * The route line's points are `[marker, ...remainingStops]`, so point `i` is path vertex `i` for
 * `i >= 1`. A marker interpolating along segment `segment` sits past every vertex up to that index.
 * Leaving them in place draws the line backwards from the marker to a waypoint it already flew
 * through, then forward again — one route reading as two lines out of the fleet.
 */
export function passedVertexCount(segment: number, pointCount: number): number {
  if (pointCount <= 0) return 0
  if (!(segment > 0)) return 1
  return Math.min(segment + 1, pointCount)
}
