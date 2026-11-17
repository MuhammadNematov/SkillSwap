import i18n from 'i18next'
import LanguageDetector from 'i18next-browser-languagedetector'
import { initReactI18next } from 'react-i18next'
import en from './en.json'
import ru from './ru.json'
import tg from './tg.json'

// Новый язык = новый json-файл + одна строка здесь.
export const LANGUAGES = [
  { code: 'ru', label: 'Русский' },
  { code: 'en', label: 'English' },
  { code: 'tg', label: 'Тоҷикӣ' },
] as const

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      ru: { translation: ru },
      en: { translation: en },
      tg: { translation: tg },
    },
    fallbackLng: 'ru',
    supportedLngs: ['tg', 'ru', 'en'],
    nonExplicitSupportedLngs: true, // en-US → en
    interpolation: { escapeValue: false },
    // Сначала выбор пользователя, потом язык браузера (аналог Accept-Language)
    detection: { order: ['localStorage', 'navigator'], caches: ['localStorage'] },
  })

i18n.on('languageChanged', (lng) => {
  document.documentElement.lang = lng
})
document.documentElement.lang = i18n.language

export default i18n
