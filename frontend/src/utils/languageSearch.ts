import type { LanguageOption } from '../api/types'
import { pickName } from './localized'

// Самые частые языки занятий: быстрые кнопки в выборе языков
export const POPULAR_LANGUAGES = ['tg', 'ru', 'en', 'uz', 'fa', 'tr', 'ar', 'de', 'fr', 'zh']
export const MAX_LANGUAGE_RESULTS = 8

// Без регистра и без различия «е»/«ё»: так ищется и русское, и таджикское, и английское название
export const normalizeQuery = (s: string) => s.toLocaleLowerCase().replace(/ё/g, 'е').trim()

// Поиск языка по названию на tg, ru, en и по коду. Порядок: точный код, название начинается с запроса, код начинается с запроса, название содержит запрос.
// Уже выбранные не показываются. Не больше MAX_LANGUAGE_RESULTS.
export function searchLanguages(options: readonly LanguageOption[], query: string, selected: readonly string[], lang: string): LanguageOption[] {
  const q = normalizeQuery(query)
  if (!q) return []
  const rank = (o: LanguageOption): number => {
    const names = [o.names.tg, o.names.ru, o.names.en].filter(Boolean).map(normalizeQuery)
    if (o.code === q) return 0
    if (names.some((n) => n.startsWith(q))) return 1
    if (o.code.startsWith(q)) return 2
    if (names.some((n) => n.includes(q))) return 3
    return -1
  }
  return options
    .filter((o) => !selected.includes(o.code))
    .map((o) => ({ o, r: rank(o) }))
    .filter((x) => x.r >= 0)
    .sort((a, b) => a.r - b.r || pickName(a.o.names, lang).localeCompare(pickName(b.o.names, lang), lang))
    .slice(0, MAX_LANGUAGE_RESULTS)
    .map((x) => x.o)
}
