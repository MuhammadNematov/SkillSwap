import { useEffect, useState } from 'react'

// Совпадает ли медиа-запрос сейчас (и перерисовка при повороте телефона или изменении окна)
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => (typeof window !== 'undefined' ? window.matchMedia(query).matches : false))
  useEffect(() => {
    const mq = window.matchMedia(query)
    const on = () => setMatches(mq.matches)
    on()
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [query])
  return matches
}

// Широкий экран для звонка: чат панелью справа. Сюда попадает и телефон, повёрнутый горизонтально (844 px).
export const WIDE_CALL = '(min-width: 768px)'
