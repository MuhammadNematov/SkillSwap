import type { ReactNode } from 'react'
import Icon, { type IconName } from '../Icon'

// Пустое состояние: иконка, короткий текст и, где есть смысл, действие (например, «Сбросить фильтры»)
export default function EmptyState({ icon, title, hint, action }: { icon: IconName; title: string; hint?: string; action?: ReactNode }) {
  return (
    <div className="card flex flex-col items-center gap-2 px-6 py-10 text-center">
      <span className="grid h-12 w-12 place-items-center rounded-full bg-brand-100 text-brand-600">
        <Icon name={icon} className="h-6 w-6" />
      </span>
      <p className="font-semibold text-ink">{title}</p>
      {hint && <p className="max-w-sm text-sm text-ink-soft">{hint}</p>}
      {action && <div className="pt-2">{action}</div>}
    </div>
  )
}
