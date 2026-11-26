import { useId, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import type { LanguageOption } from '../api/types'
import { POPULAR_LANGUAGES, searchLanguages } from '../utils/languageSearch'
import { pickName } from '../utils/localized'
import Icon from './Icon'

interface Props {
  options: readonly LanguageOption[]
  value: string[] // коды выбранных языков в порядке выбора
  onChange: (codes: string[]) => void
  max?: number // как на бэкенде (LessonLanguages.MaxPerUser)
}

// Выбор языков занятий вместо ~180 кнопок на странице: выбранные метками с крестиком, популярные быстрыми кнопками и поиск с выпадающим списком
// (по названию на tg, ru, en и по коду). ↑↓ выбирают, Enter добавляет, Esc закрывает. Лимит max, подсказка при его достижении.
export default function LanguagePicker({ options, value, onChange, max = 10 }: Props) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const inputRef = useRef<HTMLInputElement>(null)
  const listId = useId()
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)

  const byCode = useMemo(() => new Map(options.map((o) => [o.code, o])), [options])
  const full = value.length >= max
  const nameOf = (code: string) => {
    const o = byCode.get(code)
    return o ? pickName(o.names, lang) : code.toUpperCase()
  }

  const results = useMemo(() => searchLanguages(options, query, value, lang), [options, query, value, lang])

  const popular = POPULAR_LANGUAGES.filter((c) => byCode.has(c) && !value.includes(c))

  function add(code: string) {
    if (full || value.includes(code)) return
    onChange([...value, code])
    setQuery('')
    setActive(0)
    setOpen(false)
    inputRef.current?.focus()
  }

  function remove(code: string) {
    onChange(value.filter((c) => c !== code))
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Escape') {
      setOpen(false)
    } else if (e.key === 'ArrowDown') {
      e.preventDefault()
      setOpen(true)
      if (results.length) setActive((a) => (a + 1) % results.length)
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setOpen(true)
      if (results.length) setActive((a) => (a - 1 + results.length) % results.length)
    } else if (e.key === 'Enter') {
      e.preventDefault() // Enter в поле не должен отправлять форму
      if (open && results[active]) add(results[active].code)
    } else if (e.key === 'Backspace' && query === '' && value.length > 0) {
      remove(value[value.length - 1])
    }
  }

  const showList = open && query.trim() !== '' && !full

  return (
    <div className="space-y-3">
      {value.length === 0 ? (
        <p className="text-sm text-slate-400">{t('languagePicker.none')}</p>
      ) : (
        <ul className="flex flex-wrap gap-2" aria-label={t('onboarding.languages')}>
          {value.map((code) => (
            <li key={code} className="inline-flex h-9 items-center gap-1 rounded-full bg-brand-600 pl-3.5 pr-1 text-sm font-semibold text-white">
              <span className="max-w-[12rem] truncate">{nameOf(code)}</span>
              <button
                type="button"
                onClick={() => remove(code)}
                className="inline-flex h-7 w-7 items-center justify-center rounded-full hover:bg-white/20 focus:outline-none focus-visible:ring-2 focus-visible:ring-white"
                aria-label={t('languagePicker.remove', { name: nameOf(code) })}
              >
                <Icon name="x" className="h-4 w-4" />
              </button>
            </li>
          ))}
        </ul>
      )}

      <p className={`text-xs ${full ? 'font-medium text-amber-700' : 'text-slate-400'}`} aria-live="polite">
        {full ? t('languagePicker.limitReached', { max }) : t('languagePicker.counter', { count: value.length, max })}
      </p>

      {popular.length > 0 && (
        <div>
          <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('languagePicker.popular')}</div>
          <div className="flex flex-wrap gap-2">
            {popular.map((code) => (
              <button
                key={code}
                type="button"
                className="chip disabled:cursor-not-allowed disabled:opacity-50"
                disabled={full}
                onClick={() => add(code)}
              >
                + {nameOf(code)}
              </button>
            ))}
          </div>
        </div>
      )}

      <div className="relative">
        <input
          ref={inputRef}
          type="text"
          role="combobox"
          aria-expanded={showList}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={showList && results[active] ? `${listId}-${results[active].code}` : undefined}
          autoComplete="off"
          enterKeyHint="done"
          className="input text-base sm:text-sm" // 16 px на телефоне: иначе iOS приближает страницу при фокусе
          placeholder={full ? t('languagePicker.limitShort', { max }) : t('languagePicker.search')}
          disabled={full}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value)
            setActive(0)
            setOpen(true)
          }}
          onFocus={() => setOpen(true)}
          onBlur={() => setOpen(false)}
          onKeyDown={onKeyDown}
        />
        {showList && (
          <ul
            id={listId}
            role="listbox"
            className="absolute z-20 mt-1 max-h-72 w-full overflow-y-auto rounded-xl border border-ink-line bg-surface p-1 shadow-glass"
          >
            {results.length === 0 ? (
              <li className="px-3 py-2.5 text-sm text-slate-500">{t('languagePicker.noResults')}</li>
            ) : (
              results.map((o, i) => (
                <li
                  key={o.code}
                  id={`${listId}-${o.code}`}
                  role="option"
                  aria-selected={i === active}
                  onMouseEnter={() => setActive(i)}
                  onMouseDown={(e) => {
                    e.preventDefault() // иначе поле теряет фокус раньше клика и список закрывается
                    add(o.code)
                  }}
                  className={`flex min-h-[44px] cursor-pointer items-center justify-between gap-3 rounded-lg px-3 py-2 text-sm ${i === active ? 'bg-brand-100 text-brand-800' : 'text-slate-700'}`}
                >
                  <span className="min-w-0 truncate">
                    {pickName(o.names, lang)}
                    {lang !== 'en' && o.names.en && <span className="ml-2 text-xs text-slate-400">{o.names.en}</span>}
                  </span>
                  <span className="shrink-0 text-xs uppercase text-slate-400">{o.code}</span>
                </li>
              ))
            )}
          </ul>
        )}
      </div>
    </div>
  )
}
