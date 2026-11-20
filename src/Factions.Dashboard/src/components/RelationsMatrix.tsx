import React from 'react'
import type { WorldSnapshot } from '../types/world'
import { factionColorHex } from '../map/colors'

interface Props {
  world: WorldSnapshot
  onSelectFaction: (tag: string) => void
}

function standingColor(v: number | null): string {
  if (v === null) return 'var(--surface-2, #161b22)'
  if (v >= 0) {
    const r = Math.round(46 + (1 - v) * 80)
    const g = Math.round(100 + v * 104)
    const b = Math.round(v * 50)
    return `rgb(${r},${g},${b})`
  }
  const a = Math.abs(v)
  return `rgb(${Math.round(180 + a * 51)},${Math.round(76 * (1 - a))},${Math.round(60 * (1 - a))})`
}

export default function RelationsMatrix({ world, onSelectFaction }: Props) {
  const factions = [...world.factions].sort((a, b) => a.tag.localeCompare(b.tag))
  if (factions.length === 0) return <div style={{ padding: 24, color: 'var(--text-2)' }}>No faction data</div>

  const CELL = 36
  const ROW_LABEL_W = 80

  return (
    <div style={{ padding: '16px 20px', overflow: 'auto', height: '100%', boxSizing: 'border-box' }}>
      <div style={{ marginBottom: 12, display: 'flex', alignItems: 'baseline', gap: 16 }}>
        <span style={{ fontSize: 13, fontWeight: 600, color: 'var(--text-1)' }}>Faction Relations</span>
        <span style={{ fontSize: 11, color: 'var(--text-2)' }}>row → column standing · click any tag to open faction panel</span>
        {/* Legend */}
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginLeft: 'auto', fontSize: 10, color: 'var(--text-2)' }}>
          <div style={{ width: 14, height: 14, background: standingColor(-1), borderRadius: 2 }} /> hostile
          <div style={{ width: 14, height: 14, background: standingColor(0), borderRadius: 2 }} /> neutral
          <div style={{ width: 14, height: 14, background: standingColor(1), borderRadius: 2 }} /> allied
          <div style={{ width: 14, height: 14, background: 'var(--surface-2, #161b22)', border: '1px solid var(--border)', borderRadius: 2 }} /> unknown
        </div>
      </div>

      <div style={{ overflowX: 'auto' }}>
        <table style={{ borderCollapse: 'collapse', tableLayout: 'fixed' }}>
          <thead>
            <tr>
              {/* corner */}
              <th style={{ width: ROW_LABEL_W, minWidth: ROW_LABEL_W }} />
              {factions.map(col => (
                <th key={col.tag} style={{ width: CELL, minWidth: CELL, padding: '0 2px 6px', textAlign: 'center' }}>
                  <span
                    onClick={() => onSelectFaction(col.tag)}
                    title={col.tag}
                    style={{
                      display: 'inline-block', fontSize: 9, fontWeight: 700,
                      color: factionColorHex(col.tag), cursor: 'pointer',
                      writingMode: 'vertical-rl', transform: 'rotate(180deg)',
                      maxHeight: 60, overflow: 'hidden', textOverflow: 'ellipsis',
                    }}
                  >{col.tag}</span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {factions.map(row => (
              <tr key={row.tag}>
                {/* Row label */}
                <td style={{ paddingRight: 8, whiteSpace: 'nowrap' }}>
                  <span
                    onClick={() => onSelectFaction(row.tag)}
                    style={{ display: 'flex', alignItems: 'center', gap: 5, cursor: 'pointer' }}
                  >
                    <span style={{
                      width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
                      background: factionColorHex(row.tag),
                      boxShadow: `0 0 4px ${factionColorHex(row.tag)}`,
                    }} />
                    <span style={{ fontSize: 11, fontWeight: 600, color: factionColorHex(row.tag) }}>{row.tag}</span>
                  </span>
                </td>
                {/* Cells */}
                {factions.map(col => {
                  if (col.tag === row.tag) {
                    return (
                      <td key={col.tag} style={{
                        width: CELL, height: CELL, textAlign: 'center', verticalAlign: 'middle',
                        background: '#0d1117', border: '1px solid var(--border)',
                        fontSize: 10, color: 'var(--text-3)',
                      }}>—</td>
                    )
                  }
                  const v = row.relations[col.tag] ?? null
                  const fatigue = row.fatigue?.[col.tag] ?? 0
                  const truce = row.truceUntil?.[col.tag]
                  const tip = v !== null
                    ? `${row.tag} → ${col.tag}: ${v.toFixed(3)}${fatigue > 0 ? ` | fatigue ${(fatigue * 100).toFixed(0)}%` : ''}${truce ? ' | truce' : ''}`
                    : `${row.tag} → ${col.tag}: unknown`
                  return (
                    <td
                      key={col.tag}
                      title={tip}
                      onClick={() => onSelectFaction(col.tag)}
                      style={{
                        width: CELL, height: CELL, textAlign: 'center', verticalAlign: 'middle',
                        background: standingColor(v),
                        border: '1px solid rgba(0,0,0,0.35)',
                        fontSize: 8, color: 'rgba(255,255,255,0.65)',
                        cursor: 'pointer', userSelect: 'none',
                        position: 'relative',
                      }}
                    >
                      {v !== null ? v.toFixed(2) : ''}
                      {truce && <span style={{ position: 'absolute', top: 1, right: 2, fontSize: 7 }}>T</span>}
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
