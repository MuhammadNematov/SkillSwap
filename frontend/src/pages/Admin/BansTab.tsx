import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { decideBan, getBanRequests, type BanRequest, type BanRequestStatus } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import EmptyState from '../../components/staff/EmptyState'
import FilterBar from '../../components/staff/FilterBar'
import { SkeletonList } from '../../components/staff/Skeleton'
import Tag, { type TagTone } from '../../components/staff/Tag'
import { SEARCH_MAX_LENGTH, useDebouncedSearch } from '../../hooks/useDebouncedSearch'
import { useUrlParams } from '../../hooks/useUrlParams'
import { formatDateTime } from '../../utils/dates'

const TONE: Record<BanRequestStatus, TagTone> = { Pending: 'amber', Approved: 'red', Rejected: 'gray' }

// Один запрос на блокировку; решение принимает только админ. Одобрение блокирует аккаунт сразу, поэтому с подтверждением.
export function BanCard({ ban }: { ban: BanRequest }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const qc = useQueryClient()
  const [confirm, setConfirm] = useState<'approve' | 'reject' | null>(null)
  const decide = useMutation({
    mutationFn: decideBan,
    onSuccess: () => {
      setConfirm(null)
      qc.invalidateQueries({ queryKey: ['adminBans'] })
      qc.invalidateQueries({ queryKey: ['adminCase'] })
    },
  })

  return (
    <div className="card space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-bold text-slate-900">{ban.userName}</span>
        <Tag tone={TONE[ban.status]}>{t(`admin.bans.status.${ban.status}`)}</Tag>
        <span className="text-xs text-slate-400">{formatDateTime(ban.createdAt, lang)}</span>
      </div>
      <div>
        <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('admin.bans.reason')}</div>
        <p className="mt-0.5 whitespace-pre-wrap break-words text-sm text-slate-800">{ban.reason}</p>
        {!ban.requestedById && <p className="mt-0.5 text-xs text-slate-500">{t('admin.bans.system')}</p>}
      </div>
      {ban.caseId && (
        <Link to={`/admin/cases/${ban.caseId}`} className="text-sm font-medium text-brand-600 hover:underline">
          {t('admin.bans.openCase')}
        </Link>
      )}
      {ban.status === 'Pending' && !confirm && (
        <div className="flex flex-wrap gap-2 pt-1">
          <button className="btn-primary bg-[#DC3545] hover:bg-[#c42f3d]" onClick={() => setConfirm('approve')}>
            {t('admin.bans.approve')}
          </button>
          <button className="btn-secondary" onClick={() => setConfirm('reject')}>
            {t('admin.bans.reject')}
          </button>
        </div>
      )}
      {confirm && (
        <div className="space-y-2 rounded-xl border border-ink-line bg-brand-50 p-3">
          <p className="text-sm text-slate-700">{confirm === 'approve' ? t('admin.bans.approveHint') : t('admin.bans.rejectHint')}</p>
          {decide.error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, decide.error)}</p>}
          <div className="flex gap-2">
            <button
              className={confirm === 'approve' ? 'btn-primary bg-[#DC3545] hover:bg-[#c42f3d]' : 'btn-primary'}
              disabled={decide.isPending}
              onClick={() => decide.mutate({ id: ban.id, approve: confirm === 'approve' })}
            >
              {decide.isPending ? t('common.loading') : t('admin.bans.confirm')}
            </button>
            <button className="btn-secondary" disabled={decide.isPending} onClick={() => setConfirm(null)}>
              {t('admin.bans.cancel')}
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

const BAN_KEYS = ['status', 'q'] as const

// Запросы на блокировку: по умолчанию ждущие решения. Статус хранится в адресе (/admin/bans?status=Approved), «all» — все.
export default function BansTab() {
  const { t } = useTranslation()
  const { values, set, reset } = useUrlParams(BAN_KEYS)
  const [input, setInput] = useDebouncedSearch(values.q, (v) => set('q', v))
  const needle = values.q
  const status = (values.status || 'Pending') as BanRequestStatus | 'all'
  const { data = [], isLoading, error } = useQuery({
    queryKey: ['adminBans', status, needle],
    queryFn: () => getBanRequests(status === 'all' ? undefined : status, needle),
    placeholderData: keepPreviousData,
  })
  const shown = data
  const isDefault = status === 'Pending'
  const clear = () => {
    reset()
    setInput('')
  }
  return (
    <div className="space-y-4">
      <FilterBar
        search={input}
        onSearch={setInput}
        searchPlaceholder={t('staffUi.searchBans')}
        searchMaxLength={SEARCH_MAX_LENGTH}
        filters={[
          {
            key: 'status',
            label: t('admin.status'),
            // «Ждут решения» — состояние по умолчанию, поэтому как выбранный фильтр не показывается
            value: isDefault ? '' : status,
            onChange: (v) => set('status', v),
            options: [
              { value: '', label: t('admin.bans.pending') },
              { value: 'all', label: t('admin.bans.all') },
              { value: 'Approved', label: t('admin.bans.status.Approved') },
              { value: 'Rejected', label: t('admin.bans.status.Rejected') },
            ],
          },
        ]}
        onReset={clear}
      />
      {isLoading ? (
        <SkeletonList rows={3} label={t('common.loading')} />
      ) : error ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, error)}</p>
      ) : shown.length === 0 ? (
        <EmptyState
          icon={needle ? 'search' : 'ban'}
          title={needle ? t('staffUi.nothingFound') : t('admin.bans.empty')}
          hint={needle ? t('staffUi.nothingFoundHint') : t('staffUi.admin.bansEmptyHint')}
          action={
            !isDefault || needle ? (
              <button className="btn-secondary" onClick={clear}>
                {t('staffUi.filters.reset')}
              </button>
            ) : undefined
          }
        />
      ) : (
        <div className="space-y-3">
          {shown.map((b) => (
            <BanCard key={b.id} ban={b} />
          ))}
        </div>
      )}
    </div>
  )
}
