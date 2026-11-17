import { createContext, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { logoutRequest, refreshSession } from '../api/auth'
import { setAccessToken, setAuthFailureHandler } from '../api/client'
import type { AuthSession, CurrentUser } from '../api/types'

export interface AuthContextValue {
  user: CurrentUser | null
  loading: boolean // true, пока проверяем, есть ли сохранённый сеанс
  setSession: (session: AuthSession) => void
  updateUser: (user: CurrentUser) => void
  logout: () => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [loading, setLoading] = useState(true)
  const started = useRef(false)

  const setSession = useCallback((session: AuthSession) => {
    setAccessToken(session.accessToken)
    setUser(session.user)
  }, [])

  const clearSession = useCallback(() => {
    setAccessToken(null)
    setUser(null)
  }, [])

  const logout = useCallback(async () => {
    try {
      await logoutRequest()
    } finally {
      clearSession()
    }
  }, [clearSession])

  useEffect(() => {
    // Если refresh не удался внутри перехватчика — выходим из интерфейса.
    setAuthFailureHandler(clearSession)

    // Защита от двойного запуска (React StrictMode в dev): refresh-токен ротируется,
    // повторное использование старого отзывает все сеансы (ТЗ 10.1).
    if (started.current) return
    started.current = true

    refreshSession()
      .then((session) => {
        if (session) setSession(session)
      })
      .catch(() => {})
      .finally(() => setLoading(false))

    return () => setAuthFailureHandler(null)
  }, [clearSession, setSession])

  const value = useMemo(
    () => ({ user, loading, setSession, updateUser: setUser, logout }),
    [user, loading, setSession, logout],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
