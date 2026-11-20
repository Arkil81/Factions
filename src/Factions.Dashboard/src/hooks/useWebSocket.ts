import { useEffect, useRef, useState } from 'react'
import { connectLiveEvents } from '../api/liveEventSocket'
import type { LiveEvent } from '../api/liveEventSocket'

export type {
  FleetDispatchEvent,
  DemandItem,
  InstallBomRow,
  InvestmentItem,
  DemandTotals,
  DemandsEvent,
  HeartbeatEvent,
  LiveEvent,
} from '../api/liveEventSocket'

export interface UseWebSocketResult {
  connected: boolean
}

/**
 * Subscribes to the live event stream at `path`.
 *
 * `onEvent` is called once per event as it arrives. The hook deliberately keeps no "last event"
 * state: a consumer reading events off a state value only ever observes the one React committed, so
 * any event superseded within a render batch is dropped (FAC-001). Fold events into your own state
 * from the callback instead, with functional updates.
 */
export function useWebSocket(path: string, onEvent: (evt: LiveEvent) => void): UseWebSocketResult {
  const [connected, setConnected] = useState(false)
  const onEventRef = useRef(onEvent)

  // Kept in a ref so a changing callback identity never tears the socket down and reconnects it.
  useEffect(() => { onEventRef.current = onEvent }, [onEvent])

  useEffect(() => {
    const proto = window.location.protocol === 'https:' ? 'wss' : 'ws'
    const url = `${proto}://${window.location.host}${path}`
    return connectLiveEvents(url, {
      onEvent: evt => onEventRef.current(evt),
      onConnectedChange: setConnected,
    })
  }, [path])

  return { connected }
}
