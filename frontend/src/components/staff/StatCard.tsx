import type { UseQueryResult } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import Icon, { type IconName } from '../Icon'
import { Skeleton } from './Skeleton'

// Карточка-счётчик на «Обзоре»: клик ведёт в раздел с нужным фильтром. Если число больше нуля, значок и число выделены цветом
// (urgent: красным для того, что нельзя откладывать). Если счётчик не загрузился, вместо числа «—», а не выдуманный ноль.
export default function StatCard({
  to,
  icon,
  label,
  hint,
  query,
  urgent = false,
}: {
  to: string
  icon: IconName
  label: string
  hint: string
  query: UseQueryResult<number>
  urgent?: boolean
}) {
  const n = query.data
  const hot = !!n && n > 0
  const tone = hot ? (urgent ? 'bg-[rgb(var(--tag-red-bg))] text-[rgb(var(--tag-red-fg))]' : 'bg-brand-100 text-brand-600') : 'bg-brand-50 text-ink-faint'
  return (
    <Link to={to} className="row-link group items-start">
      <span className={`grid h-11 w-11 shrink-0 place-items-center rounded-xl ${tone}`}>
        <Icon name={icon} />
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-semibold text-ink-soft">{label}</p>
        {query.isLoading ? <Skeleton className="mt-1 h-8 w-12" /> : <p className="text-3xl font-bold leading-tight text-ink">{query.isError ? '—' : n}</p>}
        <p className="mt-0.5 text-xs text-ink-soft">{hint}</p>
      </div>
      <Icon name="chevronRight" className="mt-3 h-5 w-5 shrink-0 text-ink-faint transition group-hover:translate-x-0.5 group-hover:text-brand-600" />
    </Link>
  )
}
