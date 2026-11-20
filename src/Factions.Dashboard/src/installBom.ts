/**
 * A capability install's material gate, as the live `demands` event reports it — the same
 * need/held shape a Shipyard `BuildQueueEntry` reports its BOM in, so an install and a ship build
 * read the same way.
 *
 * Every quantity here is **faction-wide**, because that is what the reservation targets:
 * `ProductionManager` reserves against the cumulative BOM of every active investment request,
 * across every owned node. A node holds a slice of one claim, never a claim of its own.
 */

/** One row of the gate, mirroring `InstallMaterialRow` in Core. */
export interface InstallBomRow {
  /** `Comp:SteelPlate` for a BOM-backed capability, or an ItemKind name when `byKind`. */
  key: string
  need: number
  /** Already staged out of routable inventory into ReservedInventory, faction-wide. */
  reserved: number
  /** Ordinary routable inventory, faction-wide. Counts toward the install just as `reserved` does. */
  stock: number
  /** Present only on the flat-cost path, where `key` names an ItemKind bucket, not a component. */
  byKind?: boolean
}

/** The slice of an investment signal this module needs — structural, so it does not have to know
 *  about the rest of the live event. */
export interface InstallBomCarrier {
  cap: string
  bom?: InstallBomRow[]
}

/** `Comp:SteelPlate` -> `SteelPlate`; a flat ItemKind key ("Ingot") passes through unchanged. */
export function stripInvKeyPrefix(key: string): string {
  return key.replace(/^(Ore|Ingot|Comp|Gas):/, '')
}

/** What the faction holds toward one row. Staged and routable stock both count, which is the sum
 *  the Develop gate itself checks — reserved alone would report a key as short that the faction has
 *  plenty of and has simply not claimed yet this tick. */
export function bomHave(row: InstallBomRow): number {
  return row.reserved + row.stock
}

export function bomShortfall(row: InstallBomRow): number {
  return Math.max(0, row.need - bomHave(row))
}

/** Fraction of the install's whole material need that is covered, weighted by need so a large
 *  half-met key outweighs a small satisfied one. 1 when there is nothing to collect. */
export function bomProgress(rows: InstallBomRow[] | undefined): number {
  if (!rows || rows.length === 0) return 1
  let need = 0
  let covered = 0
  for (const r of rows) {
    if (r.need <= 0) continue
    need += r.need
    covered += Math.min(bomHave(r), r.need)
  }
  return need > 0 ? covered / need : 1
}

/** The ItemKind bucket an inventory key falls into — mirrors `ProductionManager.ItemKindFromInvKey`,
 *  which is what decides where a flat-cost install reservation lands. */
export function itemKindOfInvKey(key: string): string {
  if (key.startsWith('Ore:')) return 'Ore'
  if (key.startsWith('Ingot:')) return 'Ingot'
  if (key.startsWith('Comp:')) return 'ProcessedGood'
  if (key.startsWith('Gas:')) return key === 'Gas:Oxygen' ? 'Oxygen' : 'Hydrogen'
  return 'Hydrogen'
}

/** One node's reserved stock, attributed to the install that claims it. */
export interface ReservedGroup {
  /** Capabilities claiming these keys; empty when nothing active claims them. */
  caps: string[]
  /** True when the claim is a flat ItemKind cost — it wants "any key of this kind", not this key. */
  byKind: boolean
  rows: { key: string; here: number; row?: InstallBomRow }[]
}

/**
 * Splits a node's `reservedInventory` into per-install groups.
 *
 * A key is matched to its install by exact BOM key first, and only then by ItemKind bucket — the
 * flat-cost path claims "any key of this kind", so matching it first would let a flat Refinery cost
 * swallow a component another capability's BOM names outright.
 *
 * A key nothing active claims is a **stale reservation**: its backing request has expired and
 * `ReserveInstallStock`'s release phase has not drained it back to routable inventory yet.
 */
export function groupReservedByInstall(
  reserved: Record<string, number>,
  investments: InstallBomCarrier[],
): ReservedGroup[] {
  const byExactKey = new Map<string, { cap: string; row: InstallBomRow }[]>()
  const byKind = new Map<string, { cap: string; row: InstallBomRow }[]>()
  for (const inv of investments) {
    for (const row of inv.bom ?? []) {
      const index = row.byKind ? byKind : byExactKey
      const list = index.get(row.key)
      if (list) list.push({ cap: inv.cap, row })
      else index.set(row.key, [{ cap: inv.cap, row }])
    }
  }

  const groups = new Map<string, ReservedGroup>()
  for (const [key, here] of Object.entries(reserved)) {
    if (here <= 0) continue
    let claims = byExactKey.get(key)
    let kindClaim = false
    if (!claims || claims.length === 0) {
      claims = byKind.get(itemKindOfInvKey(key))
      kindClaim = !!claims && claims.length > 0
    }
    const caps = claims ? claims.map(c => c.cap) : []
    const groupKey = `${kindClaim ? 'kind' : 'key'}|${caps.join('+')}`
    let group = groups.get(groupKey)
    if (!group) {
      group = { caps, byKind: kindClaim, rows: [] }
      groups.set(groupKey, group)
    }
    group.rows.push({ key, here, row: claims && claims.length > 0 ? claims[0].row : undefined })
  }

  const out = Array.from(groups.values())
  for (const g of out) g.rows.sort((a, b) => b.here - a.here)
  // Claimed groups first; an unclaimed group is a tail note, not the headline.
  out.sort((a, b) => (a.caps.length === 0 ? 1 : 0) - (b.caps.length === 0 ? 1 : 0))
  return out
}
