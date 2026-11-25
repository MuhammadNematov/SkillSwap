import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { MAX_SELECT_RESULTS, searchOptions, type SearchableOption } from '../utils/optionSearch'
import Icon from './Icon'

export type SelectOption = SearchableOption

// Выше этого числа вариантов в списке появляется поиск
export const SEARCH_THRESHOLD = 8

interface Props {
  value: string
  options: readonly SelectOption[]
  onChange: (value: string) => void
  ariaLabel?: string
  className?: string
  align?: 'left' | 'right'
  /** Поле поиска в списке. По умолчанию включено, если вариантов больше SEARCH_THRESHOLD. */
  searchable?: boolean
  searchPlaceholder?: string
  /** Значения, которые показываются сразу под «Любой» (до поиска), и «популярные» под разделителем. Остальное находится поиском. */
  pinned?: readonly string[]
  popular?: readonly string[]
  popularLabel?: string
  /** Свой поиск (например, по названиям на всех языках интерфейса); по умолчанию по названию, другим названиям и пометке. */
  search?: (query: string) => readonly SelectOption[]
  maxResults?: number
}

type Row = { kind: 'option'; option: SelectOption } | { kind: 'divider'; label: string }

// Свой выпадающий список вместо системного <select>. Короткий список открывается как обычное меню. Длинный (или с searchable) получает поле поиска в фокусе,
// ограничен по высоте (320 px) со своей прокруткой и не вылезает за экран: открывается вверх, если снизу мало места, а на телефоне выезжает нижней шторкой.
// Клавиатура: ↑ ↓ Enter Esc; клик вне закрывает. Выбранное отмечено галочкой.
export default function Select({
  value,
  options,
  onChange,
  ariaLabel,
  className = '',
  align = 'left',
  searchable,
  searchPlaceholder,
  pinned,
  popular,
  popularLabel,
  search,
  maxResults = MAX_SELECT_RESULTS,
}: Props) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const [up, setUp] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const listRef = useRef<HTMLUListElement>(null)
  const listId = useId()
  const current = options.find((o) => o.value === value) ?? options[0]
  const withSearch = searchable ?? options.length > SEARCH_THRESHOLD
  const byValue = useMemo(() => new Map(options.map((o) => [o.value, o])), [options])

  // Что показано: при поиске лучшие совпадения (до maxResults), иначе «Любой», выбранное, основные, разделитель и популярные; без них весь список
  const rows = useMemo<Row[]>(() => {
    const text = query.trim()
    if (withSearch && text) {
      const found = search ? search(text) : searchOptions(options, text, maxResults)
      return found.slice(0, maxResults).map((option) => ({ kind: 'option', option }))
    }

    if (withSearch && (pinned || popular)) {
      const out: Row[] = []
      const shown = new Set<string>()
      const push = (o: SelectOption | undefined) => {
        if (o && !shown.has(o.value)) {
          shown.add(o.value)
          out.push({ kind: 'option', option: o })
        }
      }
      push(byValue.get(''))
      if (value) push(byValue.get(value)) // выбранный язык всегда виден, даже если его нет среди основных
      pinned?.forEach((v) => push(byValue.get(v)))
      const more = (popular ?? []).map((v) => byValue.get(v)).filter((o): o is SelectOption => !!o && !shown.has(o.value))
      if (more.length > 0) {
        out.push({ kind: 'divider', label: popularLabel ?? t('select.popular') })
        more.forEach(push)
      }
      return out
    }

    return options.map((option) => ({ kind: 'option', option }))
  }, [query, withSearch, search, options, maxResults, pinned, popular, popularLabel, byValue, value, t])

  const items = rows.flatMap((r) => (r.kind === 'option' ? [r.option] : []))

  // Клик вне списка закрывает его
  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) close(false)
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [open])

  // Активная строка видна при прокрутке стрелками
  useEffect(() => {
    if (open) listRef.current?.querySelector('[aria-selected="true"][data-active="true"]')?.scrollIntoView({ block: 'nearest' })
  }, [active, open, rows])

  // На телефоне открытая шторка закрывает страницу: прокрутка страницы под ней не нужна
  useEffect(() => {
    if (!open || !withSearch || window.innerWidth >= 640) return
    const prev = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = prev
    }
  }, [open, withSearch])

  function close(focusButton = true) {
    setOpen(false)
    setQuery('')
    if (focusButton) buttonRef.current?.focus()
  }

  function toggle() {
    if (open) return close()
    const box = buttonRef.current?.getBoundingClientRect()
    if (box) {
      const below = window.innerHeight - box.bottom
      setUp(window.innerWidth >= 640 && below < 340 && box.top > below) // меню не должно вылезать за нижний край окна
    }
    setActive(Math.max(0, items.findIndex((o) => o.value === value)))
    setOpen(true)
  }

  function choose(index: number) {
    const picked = items[index]
    if (!picked) return
    onChange(picked.value)
    close()
  }

  function onKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault()
      close()
    } else if (e.key === 'ArrowDown') {
      e.preventDefault()
      if (!open) toggle()
      else if (items.length) setActive((a) => (a + 1) % items.length)
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      if (!open) toggle()
      else if (items.length) setActive((a) => (a - 1 + items.length) % items.length)
    } else if (e.key === 'Enter' || (e.key === ' ' && !withSearch)) {
      e.preventDefault() // Enter в поле не должен отправлять форму
      if (open) choose(active)
      else toggle()
    }
  }

  const panelPlace = withSearch
    ? `fixed inset-x-0 bottom-0 max-h-[80vh] rounded-t-2xl pb-[env(safe-area-inset-bottom)] sm:absolute sm:inset-x-auto sm:bottom-auto sm:max-h-80 sm:min-w-[16rem] sm:rounded-xl sm:pb-0 ${up ? 'sm:bottom-full sm:mb-2' : 'sm:top-full sm:mt-2'} ${align === 'right' ? 'sm:right-0' : 'sm:left-0'}`
    : `absolute mt-2 max-h-80 min-w-full rounded-xl ${align === 'right' ? 'right-0' : 'left-0'}`
  let optionIndex = -1

  return (
    <div ref={rootRef} className={`relative ${className}`}>
      <button
        ref={buttonRef}
        type="button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listId}
        aria-label={ariaLabel}
        onClick={toggle}
        onKeyDown={onKeyDown}
        className="flex w-full items-center justify-between gap-2 rounded-xl border border-ink-line bg-brand-50 px-3 py-2 text-sm font-medium text-slate-700 transition hover:bg-surface focus:outline-none focus:ring-2 focus:ring-brand-200"
      >
        <span className="truncate">{current?.label}</span>
        <Icon name="chevronDown" className={`h-4 w-4 shrink-0 text-brand-600 transition ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && withSearch && <div className="fixed inset-0 z-40 bg-black/40 sm:hidden" aria-hidden onClick={() => close(false)} />}
      {open && (
        <div className={`z-50 flex flex-col overflow-hidden border border-ink-line bg-surface p-1 shadow-glass ${panelPlace}`}>
          {withSearch && (
            <>
              <div className="flex items-center justify-between px-3 pb-1 pt-2 sm:hidden">
                <span className="font-bold text-ink">{ariaLabel}</span>
                <button type="button" className="rounded-lg p-1 text-ink-soft hover:bg-brand-50" aria-label={t('select.close')} onClick={() => close()}>
                  <Icon name="x" />
                </button>
              </div>
              <div className="p-1">
                <div className="relative">
                  <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-ink-faint">
                    <Icon name="search" className="h-4 w-4" />
                  </span>
                  <input
                    autoFocus
                    type="text"
                    role="searchbox"
                    autoComplete="off"
                    enterKeyHint="done"
                    aria-controls={listId}
                    aria-activedescendant={items[active] ? `${listId}-${active}` : undefined}
                    aria-label={searchPlaceholder ?? t('select.search')}
                    placeholder={searchPlaceholder ?? t('select.search')}
                    maxLength={60}
                    className="input h-10 py-0 pl-9 text-base sm:text-sm" // 16 px на телефоне: иначе iOS приближает страницу при фокусе
                    value={query}
                    onChange={(e) => {
                      setQuery(e.target.value)
                      setActive(0)
                    }}
                    onKeyDown={onKeyDown}
                  />
                </div>
              </div>
            </>
          )}
          <ul id={listId} ref={listRef} role="listbox" aria-label={ariaLabel} className="min-h-0 flex-1 overflow-y-auto overscroll-contain sm:max-h-64">
            {items.length === 0 ? (
              <li className="px-3 py-3 text-sm text-slate-500" role="presentation">
                {t('select.noResults')}
              </li>
            ) : (
              rows.map((row, i) => {
                if (row.kind === 'divider') {
                  return (
                    <li key={`d${i}`} role="presentation" className="mx-1 mt-1 border-t border-ink-line px-2 pb-1 pt-2 text-[11px] font-bold uppercase tracking-wide text-ink-faint">
                      {row.label}
                    </li>
                  )
                }
                optionIndex++
                const index = optionIndex
                const o = row.option
                return (
                  <li
                    key={o.value}
                    id={`${listId}-${index}`}
                    role="option"
                    aria-selected={o.value === value}
                    data-active={index === active}
                    onMouseEnter={() => setActive(index)}
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={() => choose(index)}
                    className={`flex min-h-[44px] cursor-pointer items-center justify-between gap-3 rounded-lg px-3 py-2 text-sm transition sm:min-h-0 ${
                      index === active ? 'bg-brand-100 text-brand-800' : 'text-slate-700'
                    } ${o.value === value ? 'font-semibold' : ''}`}
                  >
                    <span className="min-w-0 truncate">{o.label}</span>
                    <span className="flex shrink-0 items-center gap-2">
                      {o.hint && <span className="text-xs font-normal uppercase text-slate-400">{o.hint}</span>}
                      {o.value === value && <Icon name="check" className="h-4 w-4 text-brand-600" />}
                    </span>
                  </li>
                )
              })
            )}
          </ul>
        </div>
      )}
    </div>
  )
}
