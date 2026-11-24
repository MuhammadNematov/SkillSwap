import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

// Фильтры списка живут в адресной строке (?status=Open&type=Conduct): ссылку с обзора можно открыть сразу с нужным фильтром,
// а «назад» и обновление страницы их не теряют. Пустое значение убирает параметр из адреса.
export function useUrlParams<K extends string>(keys: readonly K[]) {
  const [params, setParams] = useSearchParams()
  const values = Object.fromEntries(keys.map((k) => [k, params.get(k) ?? ''])) as Record<K, string>
  const set = useCallback(
    (key: K, value: string) =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          if (value) next.set(key, value)
          else next.delete(key)
          next.delete('page')
          return next
        },
        { replace: true },
      ),
    [setParams],
  )
  const reset = useCallback(
    () =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          keys.forEach((k) => next.delete(k))
          next.delete('page')
          return next
        },
        { replace: true },
      ),
    [setParams, keys],
  )
  return { values, set, reset }
}
