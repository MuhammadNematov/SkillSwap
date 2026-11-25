import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getModCases, type CaseType, type ModScope } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import CaseRow from '../../components/staff/CaseRow'
import EmptyState from '../../components/staff/EmptyState'
import FilterBar from '../../components/staff/FilterBar'
import { SkeletonList } from '../../components/staff/Skeleton'
import { SEARCH_MAX_LENGTH, useDebouncedSearch } from '../../hooks/useDebouncedSearch'
import { useUrlParams } from '../../hooks/useUrlParams'

const TYPES: CaseType[] = ['Conduct', 'NoShowAbuse', 'ReviewDispute']
const KEYS = ['type', 'q'] as const

// Список обращений модератора: свободные (очередь), мои в работе или апелляции на проверке. Раздел выбирается в меню слева.
export default function ModQueue({ scope }: { scope: ModScope }) {
  const { t } = useTranslation()
  const { values, set, reset } = useUrlParams(KEYS)
  const [input, setInput] = useDebouncedSearch(values.q, (v) => set('q', v))
  const needle = values.q
  // Тип и поиск применяет сервер: список приходит уже отфильтрованным (до 50 записей)
  const { data = [], isLoading, error } = useQuery({
    queryKey: ['modCases', scope, needle],
    queryFn: () => getModCases(scope, needle),
    placeholderData: keepPreviousData,
  })
  const shown = data.filter((c) => !values.type || c.type === values.type)
  const filtered = !!values.type || !!needle
  const clear = () => {
    reset()
    setInput('')
  }

  return (
    <div className="space-y-4">
      <FilterBar
        search={input}
        onSearch={setInput}
        searchPlaceholder={t('staffUi.searchCases')}
        searchMaxLength={SEARCH_MAX_LENGTH}
        filters={[
          {
            key: 'type',
            label: t('admin.cases.filterType'),
            value: values.type,
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
      ) : data.length === 0 ? (
        <EmptyState icon={scope === 'Queue' ? 'inbox' : scope === 'Mine' ? 'folder' : 'appeal'} title={t(`moderation.empty.${scope}`)} hint={t(`staffUi.mod.emptyHint.${scope}`)} />
      ) : shown.length === 0 ? (
        <EmptyState
          icon="search"
          title={t('staffUi.nothingFound')}
          hint={t('staffUi.nothingFoundHint')}
          action={
            filtered && (
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
                to={`/moderation/cases/${c.id}`}
                c={c}
                footer={c.pendingQuestions > 0 && <span className="font-semibold text-amber-700">{t('moderation.pendingAnswers', { count: c.pendingQuestions })}</span>}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
