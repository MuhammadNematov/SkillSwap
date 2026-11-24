import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import type { CasePriority, CaseStatus, CaseType } from '../../api/cases'
import { formatDateTime } from '../../utils/dates'
import { CaseStatusChip } from '../../pages/Cases/parts'
import { CaseBadges } from '../../pages/Moderation/parts'
import RowLink from './RowLink'

interface CaseLike {
  type: CaseType
  category: string
  status: CaseStatus
  priority: CasePriority
  isOverdue: boolean
  fromCall: boolean
  description: string
  createdAt: string
  dueAt: string
  openedBy: { name: string }
  against: { name: string }
}

// Строка обращения в списках модератора и админа: тип, статус, метки, кто на кого, даты. Весь блок кликабелен.
export default function CaseRow({ to, c, extraTags, footer }: { to: string; c: CaseLike; extraTags?: ReactNode; footer?: ReactNode }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  return (
    <RowLink to={to}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-bold text-ink">{t(`cases.type.${c.type}`)}</span>
        <CaseStatusChip status={c.status} />
        <CaseBadges priority={c.priority} overdue={c.isOverdue} fromCall={c.fromCall} />
        {extraTags}
      </div>
      <div className="mt-1 text-sm text-ink-soft">{t(`cases.categories.${c.category}`)}</div>
      <p className="mt-1 line-clamp-2 text-sm text-ink-soft">{c.description}</p>
      <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-ink-soft">
        <span>
          {c.openedBy.name} → {c.against.name}
        </span>
        <span>{formatDateTime(c.createdAt, lang)}</span>
        {c.status === 'Open' && <span>{t('staffUi.dueAt', { time: formatDateTime(c.dueAt, lang) })}</span>}
        {footer}
      </div>
    </RowLink>
  )
}
