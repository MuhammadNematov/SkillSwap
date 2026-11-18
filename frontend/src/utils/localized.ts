import type { LocalizedName, UiLanguage } from '../api/types'

// Названия навыков приходят сразу на tg, ru и en — берём язык интерфейса
export function pickName(names: LocalizedName, lang: string): string {
  return names[lang as UiLanguage] ?? names.ru ?? names.en ?? Object.values(names)[0] ?? ''
}
