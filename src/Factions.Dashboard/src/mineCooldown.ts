/** A node's mining cooldown, as the dashboard has to read it.
 *
 *  The API sends the absolute deadline and nothing else — no `onCooldown` boolean — because the
 *  world snapshot is cached for 5 s and held longer by the browser, so anything the server computes
 *  about "now" is stale on arrival. Everything about what is *currently* locked out is decided here.
 */

/** Milliseconds until a node's mining cooldown lifts, measured against the snapshot's own `utcNow`
 *  rather than the browser clock so a stale tab does not shrink every remaining time.
 *
 *  Null means no active cooldown, and it covers two different facts that a caller must not tell
 *  apart here: the node has never been mined (the field is absent), or its deadline has already
 *  passed. Both are eligible-to-mine. A caller that renders a row for null has stopped answering
 *  "which nodes are locked out" and started printing a cooldown for all 1,459 nodes. */
export function mineCooldownRemainingMs(
  mineCooldownUntilUtc: string | null | undefined,
  utcNow: string,
): number | null {
  if (!mineCooldownUntilUtc) return null
  const ms = new Date(mineCooldownUntilUtc).getTime() - new Date(utcNow).getTime()
  if (!isFinite(ms) || ms <= 0) return null
  return ms
}

/** Remaining time as an operator reads it — "3h08m", not an absolute timestamp. */
export function formatCooldownRemaining(ms: number): string {
  const mins = Math.round(ms / 60000)
  if (mins < 60) return `${mins}m`
  const hours = Math.floor(mins / 60)
  return `${hours}h${String(mins % 60).padStart(2, '0')}m`
}
