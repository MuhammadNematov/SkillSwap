import { useCallback, useEffect, useState } from 'react'

export type ThemeMode = 'light' | 'dark' | 'system'
const KEY = 'theme'

function read(): ThemeMode {
  try {
    const v = localStorage.getItem(KEY)
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch {
    return 'system'
  }
}

const prefersDark = () => window.matchMedia('(prefers-color-scheme: dark)').matches

function apply(mode: ThemeMode) {
  document.documentElement.classList.toggle('dark', mode === 'dark' || (mode === 'system' && prefersDark()))
}

// Тема: светлая, тёмная или как в системе. Выбор хранится в localStorage (только удобство, не данные).
export function useTheme() {
  const [mode, setModeState] = useState<ThemeMode>(read)

  useEffect(() => {
    apply(mode)
    if (mode !== 'system') return
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const on = () => apply('system')
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [mode])

  const setMode = useCallback((m: ThemeMode) => {
    try {
      if (m === 'system') localStorage.removeItem(KEY)
      else localStorage.setItem(KEY, m)
    } catch {
      /* без хранилища тема просто не запомнится */
    }
    setModeState(m)
  }, [])

  const isDark = mode === 'dark' || (mode === 'system' && prefersDark())
  return { mode, isDark, setMode }
}
