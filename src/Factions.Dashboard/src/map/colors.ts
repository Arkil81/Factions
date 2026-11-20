const PALETTE_HEX = [
  '#e74c3c', // red
  '#3498db', // blue
  '#2ecc71', // green
  '#f1c40f', // yellow
  '#9b59b6', // purple
  '#1abc9c', // teal
  '#e67e22', // orange
  '#e91e63', // pink
  '#00bcd4', // cyan
  '#8bc34a', // lime
]

const PALETTE_NUM = PALETTE_HEX.map(h => parseInt(h.slice(1), 16))

function tagHash(tag: string): number {
  let h = 0
  for (let i = 0; i < tag.length; i++) h = Math.imul(h * 31 + tag.charCodeAt(i), 1) | 0
  return Math.abs(h)
}

export function factionColorNum(tag: string): number {
  return PALETTE_NUM[tagHash(tag) % PALETTE_NUM.length]
}

export function factionColorHex(tag: string): string {
  return PALETTE_HEX[tagHash(tag) % PALETTE_HEX.length]
}

export const FLEET_KIND_COLORS: Record<string, string> = {
  Trade:     '#2ecc71',
  Raid:      '#e74c3c',
  Guard:     '#3498db',
  Patrol:    '#f1c40f',
  Haul:      '#1abc9c',
  // Kept in step with DEFENCE_HOLD_COLOR in fleetEngagement.ts: a reinforce's hull and the ring
  // it draws while holding are the same mission and read as one mark.
  Reinforce: '#5dade2',
}
