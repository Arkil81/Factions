import React from 'react'
import type { InstallBomRow } from '../installBom'
import { bomHave, bomShortfall, stripInvKeyPrefix } from '../installBom'

/** The per-key need/held table shared by the faction panel's Investment Signals and the node
 *  panel's reserved-stock readout — the same shape a Shipyard BuildQueueEntry reports its BOM in. */
export function InstallBomTable({ rows }: { rows: InstallBomRow[] }) {
  if (rows.length === 0) return null
  return (
    <div style={{ fontSize: 10, marginTop: 3, display: 'flex', flexDirection: 'column', gap: 1 }}>
      {rows.map(row => {
        const have = bomHave(row)
        const short = bomShortfall(row)
        return (
          <div
            key={row.key}
            style={{ display: 'flex', gap: 6, color: short > 0 ? '#e74c3c' : 'var(--text-3)' }}
            title={row.byKind ? `any ${row.key} counts toward this install` : row.key}
          >
            <span style={{ flex: '1 1 auto', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {stripInvKeyPrefix(row.key)}{row.byKind ? ' (any)' : ''}
            </span>
            <span style={{ fontVariantNumeric: 'tabular-nums' }}>
              {have.toLocaleString()}/{row.need.toLocaleString()}
            </span>
            <span
              style={{ fontVariantNumeric: 'tabular-nums', color: 'var(--text-3)', minWidth: 100, textAlign: 'right' }}
              title="staged into reserved stock + still routable"
            >
              res {row.reserved.toLocaleString()} + inv {row.stock.toLocaleString()}
            </span>
            <span style={{
              fontVariantNumeric: 'tabular-nums', minWidth: 68, textAlign: 'right',
              fontWeight: short > 0 ? 700 : 400,
            }}>
              {short > 0 ? `short ${short.toLocaleString()}` : 'ok'}
            </span>
          </div>
        )
      })}
    </div>
  )
}
