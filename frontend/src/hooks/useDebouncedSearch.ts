import { useEffect, useRef, useState } from 'react'

export const SEARCH_DELAY_MS = 300
export const SEARCH_MAX_LENGTH = 100

// Поле поиска со значением в адресе (?q=…): запрос уходит на сервер через 300 мс после последнего символа, а не на каждую букву.
// Если адрес изменили снаружи (сброс фильтров, переход по ссылке, «назад»), поле подхватывает новое значение; собственное значение
// из адреса поле не перезаписывает, поэтому текст, который человек продолжает набирать, не пропадает.
export function useDebouncedSearch(urlValue: string, setUrlValue: (value: string) => void, delay = SEARCH_DELAY_MS) {
  const [input, setInput] = useState(urlValue)
  const sent = useRef(urlValue)

  useEffect(() => {
    const text = input.trim()
    if (text === urlValue) return
    const timer = setTimeout(() => {
      sent.current = text
      setUrlValue(text)
    }, delay)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [input, delay])

  useEffect(() => {
    if (urlValue === sent.current) return
    sent.current = urlValue
    setInput(urlValue)
  }, [urlValue])

  return [input, setInput] as const
}
