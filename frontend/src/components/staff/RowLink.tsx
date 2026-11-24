import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import Icon from '../Icon'

// Кликабельная строка списка: рамка подсвечивается при наведении, справа стрелка «›»
export default function RowLink({ to, children }: { to: string; children: ReactNode }) {
  return (
    <Link to={to} className="row-link group">
      <div className="min-w-0 flex-1">{children}</div>
      <Icon name="chevronRight" className="h-5 w-5 shrink-0 text-ink-faint transition group-hover:translate-x-0.5 group-hover:text-brand-600" />
    </Link>
  )
}
