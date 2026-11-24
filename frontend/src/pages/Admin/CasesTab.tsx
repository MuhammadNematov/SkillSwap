import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getAdminCases, type CaseStatus, type CaseType } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import CaseRow from '../../components/staff/CaseRow'
import EmptyState from '../../components/staff/EmptyState'
import FilterBar from '../../components/staff/FilterBar'
import { SkeletonList } from '../../components/staff/Skeleton'
import Tag from '../../components/staff/Tag'
import { useUrlParams } from '../../hooks/useUrlParams'

const STATUSES: CaseStatus[] = ['Open', 'InProgress', 'WaitingResponse', 'Resolved', 'Appealed', 'AppealReview', 'Closed', 'Withdrawn']
const TYPES: CaseType[] = ['Conduct', 'NoShowAbuse', 'ReviewDispute']
const KEYS = ['status', 'type'] as const

// Все обращения: админ видит статусы и материалы, но не переписку. Статус и тип берутся из адреса (/admin/cases?status=Open).
export default function CasesTab() {
  const { t } = useTranslation()
  const { values, set, reset } = useUrlParams(KEYS)
  const [q, setQ] = useState('')
  const status = values.status as CaseStatus | ''
  const type = values.type as CaseType | ''
  const { data = [], isLoading, error } = useQuery({ queryKey: ['adminCases', status, type], queryFn: () => getAdminCases(status, type) })

  // Поиск по уже загруженным обращениям (до 50): сервер поиск по тексту не принимает
  const needle = q.trim().toLowerCase()
  const shown = needle ? data.filter((c) => [c.description, c.openedBy.name, c.against.name].some((x) => x.toLowerCase().includes(needle))) : data
  const clear = () => {
    reset()
    setQ('')
  }

  return (
    <div className="space-y-4">
      <FilterBar
        search={q}
        onSearch={setQ}
        searchPlaceholder={t('staffUi.searchCases')}
        filters={[
          {
            key: 'status',
            label: t('admin.cases.filterStatus'),
            value: status,
            onChange: (v) => set('status', v),
            options: [{ value: '', label: t('staffUi.filters.allStatuses') }, ...STATUSES.map((s) => ({ value: s, label: t(`cases.status.${s}`) }))],
          },
          {
            key: 'type',
            label: t('admin.cases.filterType'),
            value: type,
            onChange: (v) => set('type', v),
            options: [{ value: '', label: t('staffUi.filters.allTypes') }, ...TYPES.map((k) => ({ value: k, label: t(`cases.type.${k}`) }))],
          },
        ]}
        onReset={clear}
      />

      {isLoading ? (
        <SkeletonList label={t('common.loading')} />
      ) : error ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, error)}</p>
      ) : shown.length === 0 ? (
        <EmptyState
          icon={status || type || needle ? 'search' : 'scale'}
          title={status || type || needle ? t('staffUi.nothingFound') : t('admin.cases.empty')}
          hint={status || type || needle ? t('staffUi.nothingFoundHint') : t('staffUi.admin.casesEmptyHint')}
          action={
            (status || type || needle) && (
              <button className="btn-secondary" onClick={clear}>
                {t('staffUi.filters.reset')}
              </button>
            )
          }
        />
      ) : (
        <ul className="space-y-3">
          {shown.map((c) => (
            <li key={c.id}>
              <CaseRow
                to={`/admin/cases/${c.id}`}
                c={c}
                extraTags={c.status === 'Appealed' && !c.appeal?.reviewerModeratorId && <Tag tone="amber">{t('admin.appeal.waitingReviewer')}</Tag>}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
