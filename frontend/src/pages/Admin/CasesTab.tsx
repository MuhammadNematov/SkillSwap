import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getAdminCases, type CaseStatus, type CaseType } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'
import { CaseStatusChip } from '../Cases/parts'
import { CaseBadges } from '../Moderation/parts'

const STATUSES: CaseStatus[] = ['Open', 'InProgress', 'WaitingResponse', 'Resolved', 'Appealed', 'AppealReview', 'Closed', 'Withdrawn']
const TYPES: CaseType[] = ['Conduct', 'NoShowAbuse', 'ReviewDispute']

// Все обращения: админ видит статусы и материалы, но не переписку
export default function CasesTab() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const [status, setStatus] = useState<CaseStatus | ''>('')
  const [type, setType] = useState<CaseType | ''>('')
  const { data = [], isLoading, error } = useQuery({ queryKey: ['adminCases', status, type], queryFn: () => getAdminCases(status, type) })

  return (
    <div className="space-y-4">
      <div className="space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-slate-500">{t('admin.cases.filterStatus')}</span>
          <button className={`chip ${status === '' ? 'chip-on' : ''}`} onClick={() => setStatus('')}>
            {t('admin.cases.all')}
          </button>
          {STATUSES.map((s) => (
            <button key={s} className={`chip ${status === s ? 'chip-on' : ''}`} onClick={() => setStatus(s)}>
              {t(`cases.status.${s}`)}
            </button>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-slate-500">{t('admin.cases.filterType')}</span>
          <button className={`chip ${type === '' ? 'chip-on' : ''}`} onClick={() => setType('')}>
            {t('admin.cases.all')}
          </button>
          {TYPES.map((k) => (
            <button key={k} className={`chip ${type === k ? 'chip-on' : ''}`} onClick={() => setType(k)}>
              {t(`cases.type.${k}`)}
            </button>
          ))}
        </div>
      </div>

      {isLoading ? (
        <Spinner />
      ) : error ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, error)}</p>
      ) : data.length === 0 ? (
        <div className="card text-sm text-slate-500">{t('admin.cases.empty')}</div>
      ) : (
        <ul className="space-y-3">
          {data.map((c) => (
            <li key={c.id}>
              <Link to={`/admin/cases/${c.id}`} className="card block transition hover:bg-brand-50">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-bold text-slate-900">{t(`cases.type.${c.type}`)}</span>
                  <CaseStatusChip status={c.status} />
                  <CaseBadges priority={c.priority} overdue={c.isOverdue} fromCall={c.fromCall} />
                  {c.status === 'Appealed' && !c.appeal?.reviewerModeratorId && (
                    <span className="rounded-full bg-amber-100/80 px-2.5 py-0.5 text-xs font-medium text-amber-700">{t('admin.appeal.waitingReviewer')}</span>
                  )}
                </div>
                <div className="mt-1 text-sm text-slate-600">{t(`cases.categories.${c.category}`)}</div>
                <p className="mt-1 line-clamp-2 text-sm text-slate-500">{c.description}</p>
                <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-400">
                  <span>
                    {c.openedBy.name} → {c.against.name}
                  </span>
                  <span>{formatDateTime(c.createdAt, lang)}</span>
                  {c.status === 'Open' && <span>{t('admin.cases.dueAt', { time: formatDateTime(c.dueAt, lang) })}</span>}
                </div>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
