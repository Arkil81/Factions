import type { WorldSnapshot, PerfSnapshot, FinanceSnapshotDto, PrefabCatalogue, OnlinePlayerDto } from '../types/world'

export async function fetchWorld(): Promise<WorldSnapshot> {
  const res = await fetch('/api/world')
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`)
  return res.json()
}

export async function fetchStatus(): Promise<{ ok: boolean; dataDir: string; utc: string }> {
  const res = await fetch('/api/status')
  if (!res.ok) throw new Error(`API ${res.status}`)
  return res.json()
}

export async function fetchPerf(): Promise<PerfSnapshot | null> {
  try {
    const res = await fetch('/api/perf')
    if (!res.ok) return null
    return res.json()
  } catch {
    return null
  }
}

export async function fetchFactionLog(tag: string, lines = 200): Promise<string[]> {
  const res = await fetch(`/api/factions/${encodeURIComponent(tag)}/log?lines=${lines}`)
  if (!res.ok) throw new Error(`API ${res.status}`)
  return res.json()
}

export async function fetchFactionFinance(tag: string): Promise<FinanceSnapshotDto> {
  const res = await fetch(`/api/factions/${encodeURIComponent(tag)}/finance`)
  if (!res.ok) throw new Error(`API ${res.status}`)
  return res.json()
}

// players.json is the plugin's Newtonsoft output passed through raw, so keys are PascalCase.
export async function fetchPlayers(): Promise<OnlinePlayerDto[]> {
  try {
    const res = await fetch('/api/players')
    if (!res.ok) return []
    const raw = await res.json()
    const online: any[] = raw?.Online ?? []
    return online.map(p => ({
      steamId: String(p.SteamId ?? ''),
      identityId: Number(p.IdentityId ?? 0),
      name: String(p.Name ?? p.SteamId ?? '?'),
      hasPosition: Boolean(p.HasPosition),
      x: Number(p.X ?? 0),
      y: Number(p.Y ?? 0),
      z: Number(p.Z ?? 0),
    }))
  } catch {
    return []
  }
}

export async function fetchPrefabs(): Promise<PrefabCatalogue> {
  try {
    const res = await fetch('/api/prefabs')
    if (!res.ok) return {}
    return res.json()
  } catch {
    return {}
  }
}
