/**
 * How a node is named, coloured and iconed now that `NodeKind` is gone (FAC-102).
 *
 * A node used to be one of eight labels a die picked at creation, and `/api/world` handed that
 * label straight to the panels. It is now what is actually under it and what stands on it, which
 * the API carries as `dominantOre`, `surveyed` and the capability set.
 *
 * **This is the browser's copy of `FactionsUI/NodeLabels.cs`, deliberately.** The SE client derives
 * the same phrase from the same three answers, and a map that calls a node "Iron" while the client
 * calls it "Ore field" is the kind of disagreement nobody files a ticket about and everybody
 * distrusts. Precedence and wording are kept identical; only the palette is this front end's own.
 */

/**
 * Capabilities in the order a reader cares about finding one, most significant first — the same
 * order and the same wording as the client's `NodeLabels.BySignificance`.
 *
 * **Not ordinal order**, so it does not follow `NodeCapabilityKind` automatically: extending or
 * reordering that enum means updating this table and its counterpart on the client.
 */
const CAPABILITY_BY_SIGNIFICANCE: Array<[string, string]> = [
  ['Shipyard',           'Shipyard'],
  ['PrototechForge',     'Prototech forge'],
  ['PrototechRefinery',  'Prototech refinery'],
  ['RefineryII',    'Refinery II'],
  ['ManufactoryII', 'Manufactory II'],
  ['Refinery',      'Refinery'],
  ['Manufactory',   'Manufactory'],
  ['FuelDepot',     'Fuel depot'],
  ['FoodProcessor', 'Food processor'],
  ['Farm',          'Farm'],
  ['Habitat',       'Habitat'],
  ['SensorArray',   'Sensor array'],
]

/** The four ores the rare treatment is for — the whole set, as on the client. */
const RARE_ORES = new Set(['platinum', 'gold', 'silver', 'uranium'])

const ORE_COLORS: Record<string, string> = {
  ice: '#80deea',
}

const CAPABILITY_COLORS: Record<string, string> = {
  Shipyard:      '#9c27b0',
  PrototechForge:    '#00bfa5',
  PrototechRefinery: '#00bfa5',
  RefineryII:    '#607d8b',
  ManufactoryII: '#607d8b',
  Refinery:      '#607d8b',
  Manufactory:   '#607d8b',
  FuelDepot:     '#2196f3',
  FoodProcessor: '#8bc34a',
  Farm:          '#689f38',
  Habitat:       '#ff8a65',
  SensorArray:   '#7e57c2',
}

const CAPABILITY_ICONS: Record<string, string> = {
  Shipyard:      '🛠',
  PrototechForge:    '⚗',
  PrototechRefinery: '⚗',
  RefineryII:    '🏭',
  ManufactoryII: '⚙',
  Refinery:      '🏭',
  Manufactory:   '⚙',
  FuelDepot:     '⛽',
  FoodProcessor: '🍲',
  Farm:          '🌾',
  Habitat:       '🛏',
  SensorArray:   '📡',
}

const RARE_ORE_COLOR = '#ffd700'
const ORE_COLOR      = '#a0522d'
const STATION_COLOR  = '#d0d7de'
const BARREN_COLOR   = '#546e7a'
const UNSURVEYED_COLOR = '#37474f'

/** What a node holds, in the shape the three derivations below read. */
export interface NodeGround {
  dominantOre: string
  surveyed: boolean
  capabilities: Record<string, number> | null
  hasStation: boolean
}

export function isRareOre(ore: string): boolean {
  return RARE_ORES.has(ore.toLowerCase())
}

/**
 * The most significant installed capability's enum name, or null for none.
 *
 * Levels at or below zero are recorded but not installed, and an unrecognised name — a capability
 * added server-side that this build has never heard of — is returned as itself rather than dropped,
 * so it reads as what it is instead of as "Barren".
 */
export function mostSignificantCapability(capabilities: Record<string, number> | null): string | null {
  if (!capabilities) return null
  const installed = Object.entries(capabilities).filter(([, level]) => level > 0).map(([name]) => name)
  if (installed.length === 0) return null

  for (const [name] of CAPABILITY_BY_SIGNIFICANCE) {
    if (installed.some(n => n.toLowerCase() === name.toLowerCase())) return name
  }
  return installed[0]
}

/** "ManufactoryII" → "Manufactory II". The server sends enum names; a reader reads prose. */
function spaced(enumName: string): string {
  return enumName.replace(/([^A-Z])([A-Z])/g, '$1 $2')
}

/**
 * What to call a node in one phrase: its ore if it has any, else what stands on it, else what is
 * known about the ground.
 *
 * The unsurveyed case is why `surveyed` is on the wire at all. "Nothing here" and "nobody has
 * looked" are opposite instructions — do not bother, versus go and look — and an empty ore string
 * alone cannot tell them apart.
 */
export function nodeLabel(node: NodeGround): string {
  if (node.dominantOre) return node.dominantOre

  const cap = mostSignificantCapability(node.capabilities)
  if (cap) {
    const known = CAPABILITY_BY_SIGNIFICANCE.find(([name]) => name === cap)
    return known ? known[1] : spaced(cap)
  }
  if (node.hasStation) return 'Station'
  return node.surveyed ? 'Barren' : 'Unsurveyed'
}

/** The marker/badge colour, ranked exactly as {@link nodeLabel} is. */
export function nodeColor(node: NodeGround): string {
  if (node.dominantOre) {
    const ore = ORE_COLORS[node.dominantOre.toLowerCase()]
    if (ore) return ore
    return isRareOre(node.dominantOre) ? RARE_ORE_COLOR : ORE_COLOR
  }

  const cap = mostSignificantCapability(node.capabilities)
  if (cap) return CAPABILITY_COLORS[cap] ?? BARREN_COLOR
  if (node.hasStation) return STATION_COLOR
  return node.surveyed ? BARREN_COLOR : UNSURVEYED_COLOR
}

/** The panel icon, ranked exactly as {@link nodeLabel} is. */
export function nodeIcon(node: NodeGround): string {
  if (node.dominantOre) {
    if (node.dominantOre.toLowerCase() === 'ice') return '🧊'
    return isRareOre(node.dominantOre) ? '💎' : '⛏'
  }

  const cap = mostSignificantCapability(node.capabilities)
  if (cap) return CAPABILITY_ICONS[cap] ?? '⚙'
  if (node.hasStation) return '🛰'
  return node.surveyed ? '○' : '?'
}

/**
 * Nothing here worth drawing: no ore, nothing installed, nothing built — **and** the ground has
 * been looked at.
 *
 * The last term is the point. A node nobody has surveyed is deliberately not empty, because not
 * having looked is a reason to visit rather than a reason to hide it; since FAC-101 paces the
 * survey across ticks, "not yet sampled" is a common transient rather than an edge case. Same four
 * terms as the client's `MapWorld.IsEmpty`.
 */
export function isEmptyNode(node: NodeGround): boolean {
  return !node.dominantOre
    && mostSignificantCapability(node.capabilities) === null
    && !node.hasStation
    && node.surveyed
}
