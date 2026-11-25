import { normalizeQuery } from './languageSearch'

export interface SearchableOption {
  value: string
  label: string
  hint?: string // короткая пометка справа (например, код языка)
  keywords?: readonly string[] // другие названия: на tg, ru, en
}

export const MAX_SELECT_RESULTS = 8

// Поиск по списку выбора: название и другие названия без учёта регистра (е = ё), код/пометка. Порядок: точное совпадение пометки, название начинается
// с запроса, пометка начинается с запроса, название содержит запрос. Не больше max записей; пустая строка поиска возвращает пустой список.
export function searchOptions<T extends SearchableOption>(options: readonly T[], query: string, max = MAX_SELECT_RESULTS): T[] {
  const q = normalizeQuery(query)
  if (!q) return []
  const rank = (o: T): number => {
    const names = [o.label, ...(o.keywords ?? [])].filter(Boolean).map(normalizeQuery)
    const hint = o.hint ? normalizeQuery(o.hint) : ''
    if (hint === q) return 0
    if (names.some((n) => n.startsWith(q))) return 1
    if (hint.startsWith(q)) return 2
    if (names.some((n) => n.includes(q))) return 3
    return -1
  }
  return options
    .filter((o) => o.value !== '')
    .map((o, index) => ({ o, r: rank(o), index }))
    .filter((x) => x.r >= 0)
    .sort((a, b) => a.r - b.r || a.index - b.index)
    .slice(0, max)
    .map((x) => x.o)
}
