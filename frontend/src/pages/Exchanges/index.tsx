import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { errorMessage } from '../../api/errors'
import { acceptRequest, declineRequest, getExchangeRequests, getExchanges, withdrawRequest } from '../../api/exchanges'
import type { ExchangeRequest, ExchangeStatus, RequestStatus } from '../../api/types'
import Avatar from '../../components/Avatar'
import SkillChip from '../../components/SkillChip'
import Select from '../../components/Select'
import Spinner from '../../components/Spinner'
import { getUserProfile } from '../../api/users'
import { useInvalidateAll } from '../../hooks/useSessionActions'
import { formatDateTime } from '../../utils/dates'

type Tab = 'exchanges' | 'incoming' | 'outgoing'

const EXCHANGE_TONE: Record<ExchangeStatus, string> = {
  Active: 'bg-emerald-100/80 text-emerald-700',
  Frozen: 'bg-sky-100/80 text-sky-700',
  Completed: 'bg-brand-100/80 text-brand-700',
  Aborted: 'bg-slate-100/80 text-slate-600',
}
const REQUEST_TONE: Record<RequestStatus, string> = {
  Pending: 'bg-amber-100/80 text-amber-700',
  Accepted: 'bg-emerald-100/80 text-emerald-700',
  Declined: 'bg-red-100/80 text-red-700',
  Withdrawn: 'bg-slate-100/80 text-slate-600',
  Expired: 'bg-slate-100/80 text-slate-600',
}

const DAY = 86_400_000

export default function Exchanges() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const navigate = useNavigate()
  const invalidate = useInvalidateAll()
  const [params] = useSearchParams()
  const initialTab = params.get('tab')
  const [tab, setTab] = useState<Tab>(initialTab === 'incoming' || initialTab === 'outgoing' ? initialTab : 'exchanges')
  const [pickedSkill, setPickedSkill] = useState<Record<string, string>>({}) // requestId → навык партнёра

  const exchanges = useQuery({ queryKey: ['exchanges'], queryFn: getExchanges })
  const incoming = useQuery({ queryKey: ['requests', 'in'], queryFn: () => getExchangeRequests('in') })
  const outgoing = useQuery({ queryKey: ['requests', 'out'], queryFn: () => getExchangeRequests('out') })

  const accept = useMutation({
    mutationFn: acceptRequest,
    onSuccess: (exchangeId) => {
      invalidate()
      navigate(`/app/exchanges/${exchangeId}`)
    },
  })
  const decline = useMutation({ mutationFn: declineRequest, onSuccess: invalidate })
  const withdraw = useMutation({ mutationFn: withdrawRequest, onSuccess: invalidate })
  const actionError = accept.error ?? decline.error ?? withdraw.error

  const pendingIncoming = (incoming.data ?? []).filter((r) => r.status === 'Pending').length
  const tabs: { key: Tab; label: string; badge?: number }[] = [
    { key: 'exchanges', label: t('exchanges.tabExchanges') },
    { key: 'incoming', label: t('exchanges.tabIncoming'), badge: pendingIncoming },
    { key: 'outgoing', label: t('exchanges.tabOutgoing') },
  ]

  function renderRequest(r: ExchangeRequest) {
    const pending = r.status === 'Pending'
    return (
      <article key={r.id} className="card">
        <div className="flex items-start gap-3">
          <Avatar name={r.partner.name} src={r.partner.avatarUrl} className="h-12 w-12" />
          <div className="min-w-0 flex-1">
            <Link to={`/app/users/${r.partner.id}`} className="font-bold text-slate-900 hover:text-brand-700">
              {r.partner.name}
            </Link>
            <div className="text-xs text-slate-400">
              {t('exchanges.expiresOn', { date: formatDateTime(r.expiresAt, lang) })} · {t(`request.days${r.durationDays}`)}
            </div>
          </div>
          <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${REQUEST_TONE[r.status]}`}>
            {t(`requestStatus.${r.status}`)}
          </span>
        </div>

        <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-sm text-slate-600">
          <span className="flex items-center gap-1.5">
            {t('exchanges.iTeach')}: {r.mySkill ? <SkillChip name={r.mySkill} /> : <em>{t('exchanges.inMessage')}</em>}
          </span>
          <span className="flex items-center gap-1.5">
            {t('exchanges.iLearn')}:{' '}
            {r.needsPartnerSkill ? (
              pending && r.direction === 'in' ? (
                <PartnerSkillSelect partnerId={r.partner.id} value={pickedSkill[r.id] ?? ''} onChange={(v) => setPickedSkill({ ...pickedSkill, [r.id]: v })} />
              ) : (
                <em>{t('exchanges.inMessage')}</em>
              )
            ) : (
              <SkillChip name={r.partnerSkill} tone="green" />
            )}
          </span>
        </div>

        <p className="mt-3 rounded-xl bg-brand-50 px-3 py-2 text-sm text-slate-700">{r.message}</p>

        {pending && (
          <div className="mt-4 flex gap-2">
            {r.direction === 'in' ? (
              <>
                <button className="btn-primary px-5" disabled={accept.isPending || (r.needsPartnerSkill && !pickedSkill[r.id])} onClick={() => accept.mutate({ id: r.id, fromTeachesSkillId: r.needsPartnerSkill ? pickedSkill[r.id] : undefined })}>
                  {t('exchanges.accept')}
                </button>
                <button className="btn-secondary px-5" disabled={decline.isPending} onClick={() => decline.mutate(r.id)}>
                  {t('exchanges.decline')}
                </button>
              </>
            ) : (
              <button className="btn-secondary px-5" disabled={withdraw.isPending} onClick={() => withdraw.mutate(r.id)}>
                {t('exchanges.withdraw')}
              </button>
            )}
          </div>
        )}
      </article>
    )
  }

  function renderList() {
    if (tab === 'exchanges') {
      if (exchanges.isLoading) return <Spinner />
      const list = exchanges.data ?? []
      if (list.length === 0) {
        return (
          <div className="card text-sm text-slate-500">
            {t('exchanges.empty')}{' '}
            <Link to="/app/matches" className="font-medium text-brand-600 hover:underline">
              {t('exchanges.goToMatches')}
            </Link>
          </div>
        )
      }
      return (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {list.map((e) => {
            const daysLeft = Math.max(0, Math.ceil((new Date(e.endDate).getTime() - Date.now()) / DAY))
            return (
              <Link key={e.id} to={`/app/exchanges/${e.id}`} className="card block transition hover:bg-surface">
                <div className="flex items-center gap-3">
                  <Avatar name={e.partner.name} src={e.partner.avatarUrl} className="h-12 w-12" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate font-bold text-slate-900">{e.partner.name}</div>
                    <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${EXCHANGE_TONE[e.status]}`}>
                      {t(`exchangeStatus.${e.status}`)}
                    </span>
                  </div>
                  <div className="text-right">
                    <div className="text-xl font-bold text-brand-700">
                      {e.myLessons} : {e.partnerLessons}
                    </div>
                  </div>
                </div>
                <div className="mt-3 space-y-1 text-sm text-slate-600">
                  <div>
                    {t('exchanges.iTeach')}: <b>{e.mySkill}</b>
                  </div>
                  <div>
                    {t('exchanges.iLearn')}: <b>{e.partnerSkill}</b>
                  </div>
                </div>
                {e.status === 'Active' && (
                  <div className="mt-3 text-xs text-slate-400">{t('exchanges.daysLeft', { count: daysLeft })}</div>
                )}
              </Link>
            )
          })}
        </div>
      )
    }

    const query = tab === 'incoming' ? incoming : outgoing
    if (query.isLoading) return <Spinner />
    const list = query.data ?? []
    if (list.length === 0) {
      return <div className="card text-sm text-slate-500">{t(tab === 'incoming' ? 'exchanges.emptyIncoming' : 'exchanges.emptyOutgoing')}</div>
    }
    return <div className="grid gap-4 lg:grid-cols-2">{list.map(renderRequest)}</div>
  }

  return (
    <div className="space-y-5">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('exchanges.title')}</h1>

      <div className="flex flex-wrap gap-2">
        {tabs.map((tb) => (
          <button
            key={tb.key}
            onClick={() => setTab(tb.key)}
            className={`flex items-center gap-2 rounded-xl border px-4 py-2 text-sm font-medium transition ${
              tab === tb.key
                ? 'border-brand-600 bg-brand-600 text-white'
                : 'border-ink-line bg-brand-50 text-slate-600 hover:bg-surface'
            }`}
          >
            {tb.label}
            {!!tb.badge && <span className="rounded-full bg-red-500 px-1.5 text-xs text-white">{tb.badge}</span>}
          </button>
        ))}
      </div>

      {actionError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, actionError)}</p>}

      {renderList()}
    </div>
  )
}

// Отправитель не указал свой навык: получатель выбирает из его «Умею» при принятии
function PartnerSkillSelect({ partnerId, value, onChange }: { partnerId: string; value: string; onChange: (v: string) => void }) {
  const { t } = useTranslation()
  const { data } = useQuery({ queryKey: ['profile', partnerId], queryFn: () => getUserProfile(partnerId) })
  return (
    <Select
      className="w-56"
      value={value}
      ariaLabel={t('exchanges.pickSkill')}
      onChange={onChange}
      options={[{ value: '', label: t('exchanges.pickSkill') }, ...(data?.teach ?? []).map((x) => ({ value: x.skillId, label: x.name }))]}
    />
  )
}
