import { useEffect, useState } from 'react'

export type AuthState = {
  authenticated: boolean
  steamId?: string
  name?: string | null
  promoteLevel?: string | null
  isAdmin?: boolean
}

export function useAuth(): AuthState | null {
  const [auth, setAuth] = useState<AuthState | null>(null)

  useEffect(() => {
    fetch('/auth/me')
      .then(r => (r.ok ? r.json() : { authenticated: false }))
      .then(setAuth)
      .catch(() => setAuth({ authenticated: false }))
  }, [])

  return auth
}
