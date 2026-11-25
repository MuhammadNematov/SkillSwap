import { useTranslation } from 'react-i18next'
import Icon from '../Icon'
import Select, { type SelectOption } from '../Select'

export interface FilterDef {
  key: string
  label: string // «Статус», «Тип»: для подписи выбранного значения
  value: string
  options: readonly SelectOption[] // первый вариант — «Все» (значение '')
  onChange: (value: string) => void
  className?: string
}

// Панель фильтров: поиск, компактные выпадающие списки, под ними выбранные значения (каждое снимается крестиком)
// и одна кнопка «Сбросить», пока что-то выбрано
export default function FilterBar({
  search,
  onSearch,
  searchPlaceholder,
  searchMaxLength,
  filters,
  extra,
  onReset,
}: {
  search?: string
  onSearch?: (value: string) => void
  searchPlaceholder?: string
  searchMaxLength?: number
  filters: FilterDef[]
  extra?: React.ReactNode
  onReset: () => void
}) {
  const { t } = useTranslation()
  const active = filters.filter((f) => f.value !== '')
  const hasSearch = !!search?.trim()
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        {onSearch && (
          <div className="relative min-w-[200px] flex-1 sm:max-w-sm">
            <span className="pointer-events-none absolute left-3.5 top-1/2 -translate-y-1/2 text-ink-faint">
              <Icon name="search" className="h-4 w-4" />
            </span>
            <input
              className="input h-10 py-0 pl-10"
              type="search"
              value={search ?? ''}
              placeholder={searchPlaceholder}
              maxLength={searchMaxLength}
              aria-label={searchPlaceholder}
              onChange={(e) => onSearch(e.target.value)}
            />
          </div>
        )}
        {filters.map((f) => (
          <Select key={f.key} className={f.className ?? 'w-44'} value={f.value} ariaLabel={f.label} options={f.options} onChange={f.onChange} />
        ))}
        {extra}
      </div>
      {(active.length > 0 || hasSearch) && (
        <div className="flex flex-wrap items-center gap-2" aria-label={t('staffUi.filters.active')}>
          {active.map((f) => (
            <button
              key={f.key}
              type="button"
              className="tag tag-brand gap-1.5 hover:opacity-80"
              onClick={() => f.onChange('')}
              aria-label={t('staffUi.filters.remove', { name: f.label })}
            >
              {f.label}: {f.options.find((o) => o.value === f.value)?.label ?? f.value}
              <Icon name="x" className="h-3 w-3" />
            </button>
          ))}
          <button type="button" className="text-xs font-semibold text-brand-600 hover:underline" onClick={onReset}>
            {t('staffUi.filters.reset')}
          </button>
        </div>
      )}
    </div>
  )
}
