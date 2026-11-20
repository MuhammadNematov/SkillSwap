import { useEffect, useState } from 'react'

// Текущее время, обновляемое по таймеру: кнопки «Войти» сами включаются, когда откроется окно входа
export function useNow(intervalMs = 15_000): Date {
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])
  return now
}
