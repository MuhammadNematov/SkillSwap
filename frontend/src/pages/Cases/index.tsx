import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getMyCases } from '../../api/cases'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'
import { CaseStatusChip } from './parts'

const TABS = ['All', 'Opened', 'Against'] as const

// Мои обращения: поданные мной и поданные на меня (последние видны, когда модератор взял их в работу)
export default function Cases() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const [role, setRole] = useState<(typeof TABS)[number]>('All')
  const { data = [], isLoading } = useQuery({ queryKey: ['cases', role], queryFn: () => getMyCases(role) })

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('cases.title')}</h1>
      <div className="flex gap-2">
        {TABS.map((k) => (
          <button key={k} className={`chip ${role === k ? 'chip-on' : ''}`} onClick={() => setRole(k)}>
            {t(`cases.tab_${k}`)}
          </button>
        ))}
      </div>

      {isLoading ? (
        <Spinner />
      ) : data.length === 0 ? (
        <div className="card text-sm text-slate-500">{t('cases.empty')}</div>
      ) : (
        <ul className="space-y-3">
          {data.map((c) => (
            <li key={c.id}>
              <Link to={`/app/cases/${c.id}`} className="card block transition hover:bg-brand-50">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="font-bold text-slate-900">{t(`cases.type.${c.type}`)}</span>
                  <CaseStatusChip status={c.status} />
                </div>
                <div className="mt-1 text-sm text-slate-600">{t(`cases.categories.${c.category}`)}</div>
                <div className="mt-1 text-xs text-slate-400">
                  {c.myRole === 'Opener' ? t('cases.against', { name: c.against.name }) : t('cases.from', { name: c.openedBy.name })} · {formatDateTime(c.createdAt, lang)}
                </div>
                {c.pendingQuestions > 0 && <div className="mt-2 text-xs font-semibold text-amber-700">{t('cases.questionsWaiting', { count: c.pendingQuestions })}</div>}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
