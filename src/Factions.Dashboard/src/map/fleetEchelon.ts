/**
 * How many hulls a fleet is, drawn the way a tactical map draws unit strength: as a mark above the
 * marker, never as a bigger marker.
 *
 * Marker size used to carry it — `6 + convoySize * 1.5` — so a 24-hull squadron drew an arrowhead
 * six times the area of a lone trader, buried the nodes underneath it, and grew again every time
 * the squadron ceiling did. APP-6 puts the echelon field above the frame for exactly that reason:
 * the symbol has to stay the size it is or a dense board stops being readable.
 *
 * The ladder counts hulls exactly while counting is still legible, then bands:
 *
 * | Convoy | Mark      |                                                                     |
 * |--------|-----------|---------------------------------------------------------------------|
 * | 1      | *nothing* | most fleets are one hull; a mark on every marker carries no signal   |
 * | 2      | `● ●`     | at two and three the dot count **is** the hull count                 |
 * | 3      | `● ● ●`   |                                                                     |
 * | 4-6    | `▮`       |                                                                     |
 * | 7-11   | `▮ ▮`     |                                                                     |
 * | 12-17  | `▮ ▮ ▮`   |                                                                     |
 * | 18+    | `✕`       | the top band is reachable: `RaidSquadronMaxHulls` is 24              |
 */

export type EchelonGlyph = 'none' | 'dot' | 'bar' | 'cross'

export interface FleetEchelon {
  glyph: EchelonGlyph
  /** How many marks to draw; 0 for `none`. */
  count: number
}

/** Bands, ascending by the smallest convoy that reaches each one. */
const LADDER: Array<{ minShips: number; glyph: EchelonGlyph; count: number }> = [
  { minShips: 1,  glyph: 'none',  count: 0 },
  { minShips: 2,  glyph: 'dot',   count: 2 },
  { minShips: 3,  glyph: 'dot',   count: 3 },
  { minShips: 4,  glyph: 'bar',   count: 1 },
  { minShips: 7,  glyph: 'bar',   count: 2 },
  { minShips: 12, glyph: 'bar',   count: 3 },
  { minShips: 18, glyph: 'cross', count: 1 },
]

/** A convoy size of 0 is a legacy fleet whose size was never written; it reads as one hull. */
export function fleetEchelon(convoySize: number): FleetEchelon {
  let band = LADDER[0]
  for (const entry of LADDER) {
    if (convoySize >= entry.minShips) band = entry
  }
  return { glyph: band.glyph, count: band.count }
}

const MARKS: Record<EchelonGlyph, string> = { none: '', dot: '●', bar: '▮', cross: '✕' }

/** The mark the map draws, as text. The fleet panel is where a reader decodes the map, so it shows
 *  the same mark rather than a name for it. */
export function echelonMark(convoySize: number): string {
  const echelon = fleetEchelon(convoySize)
  return MARKS[echelon.glyph].repeat(echelon.count)
}
