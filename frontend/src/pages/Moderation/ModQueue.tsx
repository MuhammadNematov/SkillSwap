import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getModCases, type ModScope } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'
import { CaseStatusChip } from '../Cases/parts'
import { CaseBadges } from './parts'

const SCOPES: ModScope[] = ['Queue', 'Mine', 'Appeals']

// Очередь модератора: свободные обращения, мои в работе и апелляции, которые мне назначили на проверку
export default function ModQueue() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const [scope, setScope] = useState<ModScope>('Queue')
  const { data = [], isLoading, error } = useQuery({ queryKey: ['modCases', scope], queryFn: () => getModCases(scope) })

  return (
    <>
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('moderation.title')}</h1>
      <div className="flex flex-wrap gap-2">
        {SCOPES.map((k) => (
          <button key={k} className={`chip ${scope === k ? 'chip-on' : ''}`} onClick={() => setScope(k)}>
            {t(`moderation.scope.${k}`)}
          </button>
        ))}
      </div>

      {isLoading ? (
        <Spinner />
      ) : error ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, error)}</p>
      ) : data.length === 0 ? (
        <div className="card text-sm text-slate-500">{t(`moderation.empty.${scope}`)}</div>
      ) : (
        <ul className="space-y-3">
          {data.map((c) => (
            <li key={c.id}>
              <Link to={`/moderation/cases/${c.id}`} className="card block transition hover:bg-brand-50">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-bold text-slate-900">{t(`cases.type.${c.type}`)}</span>
                  <CaseStatusChip status={c.status} />
                  <CaseBadges priority={c.priority} overdue={c.isOverdue} fromCall={c.fromCall} />
                </div>
                <div className="mt-1 text-sm text-slate-600">{t(`cases.categories.${c.category}`)}</div>
                <p className="mt-1 line-clamp-2 text-sm text-slate-500">{c.description}</p>
                <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-slate-400">
                  <span>
                    {c.openedBy.name} → {c.against.name}
                  </span>
                  <span>{formatDateTime(c.createdAt, lang)}</span>
                  {c.status === 'Open' && <span>{t('moderation.dueAt', { time: formatDateTime(c.dueAt, lang) })}</span>}
                  {c.pendingQuestions > 0 && <span className="font-semibold text-amber-700">{t('moderation.pendingAnswers', { count: c.pendingQuestions })}</span>}
                </div>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}
