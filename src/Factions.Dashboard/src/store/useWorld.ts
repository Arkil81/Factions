import { useState, useEffect, useCallback } from 'react'
import { fetchWorld } from '../api/client'
import type { WorldSnapshot } from '../types/world'

// Well inside the plugin's 60s flush, so the view is at most one flush behind rather than a
// flush plus a poll. The API's own 5s snapshot cache absorbs the extra requests.
const POLL_MS = 10_000

export function useWorld() {
  const [world, setWorld] = useState<WorldSnapshot | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [lastFetch, setLastFetch] = useState<Date | null>(null)

  const load = useCallback(async () => {
    try {
      const data = await fetchWorld()
      setWorld(data)
      setError(null)
      setLastFetch(new Date())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
    const t = setInterval(load, POLL_MS)
    return () => clearInterval(t)
  }, [load])

  return { world, error, loading, lastFetch, refresh: load }
}
