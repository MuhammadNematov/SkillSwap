import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { decideBan, getBanRequests, type BanRequest, type BanRequestStatus } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'

const TONE: Record<BanRequestStatus, string> = {
  Pending: 'bg-amber-100/80 text-amber-700',
  Approved: 'bg-red-100/80 text-red-700',
  Rejected: 'bg-slate-100/80 text-slate-600',
}

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
        <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${TONE[ban.status]}`}>{t(`admin.bans.status.${ban.status}`)}</span>
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

export default function BansTab() {
  const { t } = useTranslation()
  const [status, setStatus] = useState<BanRequestStatus | ''>('Pending')
  const { data = [], isLoading, error } = useQuery({ queryKey: ['adminBans', status], queryFn: () => getBanRequests(status || undefined) })
  return (
    <div className="space-y-4">
      <div className="flex gap-2">
        <button className={`chip ${status === 'Pending' ? 'chip-on' : ''}`} onClick={() => setStatus('Pending')}>
          {t('admin.bans.pending')}
        </button>
        <button className={`chip ${status === '' ? 'chip-on' : ''}`} onClick={() => setStatus('')}>
          {t('admin.bans.all')}
        </button>
      </div>
      {isLoading ? (
        <Spinner />
      ) : error ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, error)}</p>
      ) : data.length === 0 ? (
        <div className="card text-sm text-slate-500">{t('admin.bans.empty')}</div>
      ) : (
        <div className="space-y-3">
          {data.map((b) => (
            <BanCard key={b.id} ban={b} />
          ))}
        </div>
      )}
    </div>
  )
}
