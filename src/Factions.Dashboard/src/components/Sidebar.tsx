import React from 'react'
import type { FactionDto, OnlinePlayerDto } from '../types/world'
import { factionColorHex } from '../map/colors'
import { DRIVE_COLORS } from '../types/world'

interface Props {
  factions: FactionDto[]
  players: OnlinePlayerDto[]
  onSelectPlayer: (player: OnlinePlayerDto) => void
  visibleFactions: Set<string>
  selectedFactionTag: string | null
  onToggleVisible: (tag: string) => void
  onSelectFaction: (tag: string) => void
  showEmpty: boolean
  onToggleEmpty: () => void
  showRoutes: boolean
  onToggleRoutes: () => void
  showBlobs: boolean
  onToggleBlobs: () => void
  showPlanets: boolean
  onTogglePlanets: () => void
}

export default function Sidebar({
  factions, players, onSelectPlayer, visibleFactions, selectedFactionTag, onToggleVisible, onSelectFaction,
  showEmpty, onToggleEmpty, showRoutes, onToggleRoutes, showBlobs, onToggleBlobs, showPlanets, onTogglePlanets,
}: Props) {
  return (
    <aside className="sidebar">
      <div className="sidebar-header">Factions ({factions.length})</div>
      <div className="sidebar-list">
        {factions.map(f => {
          const color = factionColorHex(f.tag)
          const isVisible = visibleFactions.has(f.tag)
          const isSelected = f.tag === selectedFactionTag
          return (
            <div
              key={f.tag}
              className={`faction-row${isSelected ? ' selected' : ''}${f.isDormant ? ' dormant' : ''}`}
              onClick={() => onSelectFaction(f.tag)}
            >
              <div className="faction-dot" style={{ background: color, opacity: isVisible ? 1 : 0.25 }} />
              <div className="faction-info">
                <div className="faction-tag">{f.tag}</div>
                <div className="faction-sub">
                  {f.personalityId} · {f.nodeCount} node{f.nodeCount !== 1 ? 's' : ''}
                  {f.isDormant ? ' · dormant' : ''}
                </div>
                <DrivesMini drives={f.drives} />
              </div>
              <span
                className="faction-eye"
                title={isVisible ? 'Hide on map' : 'Show on map'}
                onClick={e => { e.stopPropagation(); onToggleVisible(f.tag) }}
              >
                {isVisible ? '👁' : '○'}
              </span>
            </div>
          )
        })}
        {factions.length === 0 && (
          <div style={{ padding: '16px', color: 'var(--text-3)', fontSize: 12 }}>
            No faction data. Check the API server is running and the data directory is correct.
          </div>
        )}
      </div>

      <div style={{ borderTop: '1px solid var(--border)', padding: '8px 0' }}>
        <div className="sidebar-header" style={{ paddingTop: 0 }}>Players ({players.length})</div>
        {players.map(p => (
          <div
            key={p.steamId}
            title={p.hasPosition ? `Zoom to ${p.name}` : `${p.name} has no live position yet`}
            onClick={() => p.hasPosition && onSelectPlayer(p)}
            style={{
              display: 'flex', alignItems: 'center', gap: 8,
              padding: '4px 12px', fontSize: 12,
              cursor: p.hasPosition ? 'pointer' : 'default',
              color: p.hasPosition ? 'var(--text-1)' : 'var(--text-3)',
            }}
          >
            <span style={{
              width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
              background: '#2ecc71', boxShadow: '0 0 4px #2ecc71',
              opacity: p.hasPosition ? 1 : 0.3,
            }} />
            {p.name}
          </div>
        ))}
        {players.length === 0 && (
          <div style={{ padding: '2px 12px 4px', color: 'var(--text-3)', fontSize: 11 }}>
            No players online
          </div>
        )}
      </div>

      <div style={{ borderTop: '1px solid var(--border)', padding: '8px 12px' }}>
        <div style={{ fontSize: 10, fontWeight: 600, color: 'var(--text-3)', letterSpacing: '0.08em', marginBottom: 6 }}>
          LAYERS
        </div>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', fontSize: 12, color: showEmpty ? 'var(--text-1)' : 'var(--text-3)' }}>
          <input
            type="checkbox"
            checked={showEmpty}
            onChange={onToggleEmpty}
            style={{ cursor: 'pointer' }}
          />
          Empty nodes
        </label>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', fontSize: 12, marginTop: 6, color: showRoutes ? 'var(--text-1)' : 'var(--text-3)' }}>
          <input
            type="checkbox"
            checked={showRoutes}
            onChange={onToggleRoutes}
            style={{ cursor: 'pointer' }}
          />
          Fleet routes
        </label>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', fontSize: 12, marginTop: 6, color: showPlanets ? 'var(--text-1)' : 'var(--text-3)' }}>
          <input type="checkbox" checked={showPlanets} onChange={onTogglePlanets} style={{ cursor: 'pointer' }} />
          Planets
        </label>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', fontSize: 12, marginTop: 6, color: showBlobs ? 'var(--text-1)' : 'var(--text-3)' }}>
          <input
            type="checkbox"
            checked={showBlobs}
            onChange={onToggleBlobs}
            style={{ cursor: 'pointer' }}
          />
          Territory blobs (slow)
        </label>
      </div>
    </aside>
  )
}

function DrivesMini({ drives }: { drives: number[] }) {
  if (!drives || drives.length === 0) return null
  return (
    <div className="drive-mini">
      {drives.map((v, i) => (
        <div
          key={i}
          className="drive-mini-bar"
          style={{ background: DRIVE_COLORS[i], width: `${Math.round(v * 100)}%`, flex: `0 0 ${Math.max(v * 36, 2)}px` }}
          title={`Drive ${i}: ${(v * 100).toFixed(0)}%`}
        />
      ))}
    </div>
  )
}
