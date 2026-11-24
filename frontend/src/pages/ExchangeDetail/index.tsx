import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { errorMessage } from '../../api/errors'
import { abortExchange, completeExchange, extendExchange, getExchange, getSessions } from '../../api/exchanges'
import type { LessonSession } from '../../api/types'
import Avatar from '../../components/Avatar'
import CancelSessionModal from '../../components/CancelSessionModal'
import CaseModal from '../../components/CaseModal'
import Chat from '../../components/Chat'
import FormField from '../../components/FormField'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import SessionItem from '../../components/SessionItem'
import SessionProposalModal from '../../components/SessionProposalModal'
import SkillChip from '../../components/SkillChip'
import Spinner from '../../components/Spinner'
import { useInvalidateAll } from '../../hooks/useSessionActions'

const DAY = 86_400_000

// Карточка обмена: баланс уроков, срок, план уроков, действия и чат (ТЗ, раздел 4, шаги 4–5)
export default function ExchangeDetail() {
  const { t } = useTranslation()
  const { id = '' } = useParams()
  const invalidate = useInvalidateAll()

  const [proposing, setProposing] = useState(false)
  const [aborting, setAborting] = useState(false)
  const [complaining, setComplaining] = useState(false)
  const [cancelling, setCancelling] = useState<LessonSession | null>(null)

  const { data: exchange, isLoading, error } = useQuery({ queryKey: ['exchange', id], queryFn: () => getExchange(id) })
  const { data: sessions = [] } = useQuery({ queryKey: ['sessions', id], queryFn: () => getSessions(id) })

  const complete = useMutation({ mutationFn: completeExchange, onSuccess: invalidate })
  const extend = useMutation({ mutationFn: extendExchange, onSuccess: invalidate })
  // Три окончательные неявки партнёра: можно выйти без последствий для себя
  const leaveNoShow = useMutation({ mutationFn: () => abortExchange({ id, reason: 'partner_no_show' }), onSuccess: invalidate })
  const actionError = complete.error ?? extend.error ?? leaveNoShow.error

  if (isLoading) return <Spinner />
  if (error || !exchange) return <div className="card text-sm text-red-700">{errorMessage(t, error)}</div>

  const active = exchange.status === 'Active'
  const start = new Date(exchange.startDate).getTime()
  const end = new Date(exchange.endDate).getTime()
  const daysLeft = Math.max(0, Math.ceil((end - Date.now()) / DAY))
  const percent = Math.min(100, Math.max(0, ((Date.now() - start) / (end - start)) * 100))
  const total = exchange.myLessons + exchange.partnerLessons
  const myShare = total === 0 ? 50 : (exchange.myLessons / total) * 100
  const imbalance = exchange.showBalanceReminder ?? Math.abs(exchange.myLessons - exchange.partnerLessons) > 3 // ТЗ 6.1: мягкое напоминание

  return (
    <div className="space-y-5">
      <Link to="/app/exchanges" className="inline-flex items-center gap-1 text-sm text-slate-500 hover:text-brand-700">
        <Icon name="arrowRight" className="h-4 w-4 rotate-180" />
        {t('exchange.back')}
      </Link>

      <section className="card overflow-hidden p-0">
        <div className="flex flex-wrap items-center gap-4 p-5">
          <Avatar name={exchange.partner.name} src={exchange.partner.avatarUrl} className="h-16 w-16" />
          <div className="min-w-0 flex-1">
            <Link to={`/app/users/${exchange.partner.id}`} className="text-2xl font-extrabold text-slate-900 hover:text-brand-700">
              {exchange.partner.name}
            </Link>
            <div className="mt-1.5 flex flex-wrap items-center gap-2 text-sm text-slate-600">
              <SkillChip name={exchange.mySkill} />
              <Icon name="swap" className="h-4 w-4 text-slate-400" />
              <SkillChip name={exchange.partnerSkill} tone="green" />
            </div>
          </div>
          <span className="rounded-full bg-brand-100 px-3 py-1 text-sm font-semibold text-brand-700">{t(`exchangeStatus.${exchange.status}`)}</span>
          <button className="btn-secondary px-3 py-1.5" onClick={() => setComplaining(true)}>
            {t('cases.complain')}
          </button>
        </div>

        {/* Счёт уроков и срок обмена: самое важное сразу под именем партнёра */}
        <div className="grid gap-px border-t border-ink-line bg-ink-line md:grid-cols-5">
          <div className="bg-brand-50 p-5 md:col-span-3">
            <div className="flex items-center justify-between text-xs font-semibold uppercase tracking-wider text-slate-500">
              <span>{t('exchange.balance')}</span>
              <span className="normal-case tracking-normal text-slate-400">{t('exchange.balanceHint')}</span>
            </div>
            <div className="mt-3 flex items-center justify-center gap-5 sm:gap-8">
              <div className="text-center">
                <div className="text-5xl font-extrabold leading-none text-brand-700 sm:text-6xl">{exchange.myLessons}</div>
                <div className="mt-1.5 text-sm font-semibold text-slate-600">{t('exchange.you')}</div>
              </div>
              <div className="text-3xl font-bold text-slate-300">:</div>
              <div className="text-center">
                <div className="text-5xl font-extrabold leading-none text-emerald-600 sm:text-6xl">{exchange.partnerLessons}</div>
                <div className="mt-1.5 max-w-[140px] truncate text-sm font-semibold text-slate-600">{exchange.partner.name}</div>
              </div>
            </div>
            <div className="mt-4 flex h-2.5 overflow-hidden rounded-full bg-slate-200/70">
              <div className="bg-brand-600 transition-all" style={{ width: `${myShare}%` }} />
              <div className="bg-emerald-500 transition-all" style={{ width: `${100 - myShare}%` }} />
            </div>
            {imbalance && <p className="mt-3 rounded-lg bg-amber-50/80 px-3 py-2 text-sm text-amber-800">{t('exchange.balanceWarning')}</p>}
          </div>
          <div className="flex flex-col justify-center bg-brand-50 p-5 md:col-span-2">
            <div className="text-xs font-semibold uppercase tracking-wider text-slate-500">{t('exchange.term')}</div>
            <div className="mt-2 flex items-end justify-between">
              <span className="text-4xl font-extrabold leading-none text-slate-900">{daysLeft}</span>
              <span className="text-sm font-semibold text-slate-500">{Math.round(percent)}%</span>
            </div>
            <p className="mt-1 text-sm text-slate-500">{t('exchange.daysLeft', { count: daysLeft })}</p>
            <div className="mt-3 h-2.5 overflow-hidden rounded-full bg-slate-200/70">
              <div className="h-full bg-brand-600 transition-all" style={{ width: `${percent}%` }} />
            </div>
          </div>
        </div>
      </section>

      <div className="grid gap-5 lg:grid-cols-5">
        <div className="space-y-5 lg:col-span-2">
          <section className="card">
            <div className="flex items-center justify-between gap-2">
              <h2 className="font-bold text-slate-900">{t('exchange.plan')}</h2>
              {active && (
                <button className="btn-secondary px-3 py-1.5" onClick={() => setProposing(true)}>
                  {t('exchange.proposeLesson')}
                </button>
              )}
            </div>
            {sessions.length === 0 ? (
              <p className="mt-3 text-sm text-slate-500">{t('exchange.noSessions')}</p>
            ) : (
              <ul className="mt-3 space-y-3">
                {sessions.map((s) => (
                  <SessionItem key={s.id} session={s} onCancel={setCancelling} />
                ))}
              </ul>
            )}
          </section>

          {exchange.status === 'Aborted' && (
            <section className="card text-sm text-slate-600">
              <div className="font-semibold text-slate-800">
                {exchange.abortedForNoShow ? t(exchange.abortedBy === 'partner' ? 'exchange.abortedNoShowOther' : 'exchange.abortedNoShowMe') : t(`exchange.abortedBy_${exchange.abortedBy ?? 'system'}`)}
              </div>
              {exchange.abortReason && (
                <p className="mt-1">{t([`exchange.abortReason_${exchange.abortReason}`, 'exchange.abortReasonText'], { text: exchange.abortReason })}</p>
              )}
            </section>
          )}

          {active && (
            <section className="card space-y-3">
              {actionError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, actionError)}</p>}
              {exchange.completedByMe ? (
                <p className="text-sm text-slate-500">{t('exchange.waitingPartner')}</p>
              ) : (
                <button className="btn-primary w-full" disabled={complete.isPending} onClick={() => complete.mutate(exchange.id)}>
                  {t('exchange.complete')}
                </button>
              )}
              {exchange.myExtensionAccepted && !exchange.isExtended && <p className="text-sm text-slate-500">{t('exchange.extendWaiting')}</p>}
              {exchange.partnerExtensionAccepted && !exchange.myExtensionAccepted && !exchange.isExtended && (
                <p className="text-sm text-brand-700">{t('exchange.extendProposed')}</p>
              )}
              {!exchange.isExtended && !exchange.myExtensionAccepted && (
                <button className="btn-secondary w-full" disabled={extend.isPending} onClick={() => extend.mutate(exchange.id)}>
                  {t('exchange.extend')}
                </button>
              )}
              {exchange.canAbortForNoShow && (
                <div className="rounded-lg bg-amber-50 px-3 py-3 text-sm text-amber-800">
                  <p>{t('exchange.noShowsHint', { count: exchange.partnerNoShows ?? 3, name: exchange.partner.name })}</p>
                  <button className="btn-secondary mt-2 w-full" disabled={leaveNoShow.isPending} onClick={() => leaveNoShow.mutate()}>
                    {t('exchange.leaveNoShow')}
                  </button>
                </div>
              )}
              <button className="w-full rounded-xl px-5 py-2.5 text-sm font-semibold text-red-600 hover:bg-red-50/70" onClick={() => setAborting(true)}>
                {t('exchange.abort')}
              </button>
            </section>
          )}
        </div>

        <div className="lg:col-span-3">
          <Chat exchange={exchange} />
        </div>
      </div>

      {proposing && <SessionProposalModal exchangeId={exchange.id} partnerId={exchange.partner.id} partnerName={exchange.partner.name} onClose={() => setProposing(false)} />}
      {cancelling && <CancelSessionModal session={cancelling} onClose={() => setCancelling(null)} />}
      {aborting && <AbortModal exchangeId={exchange.id} onClose={() => setAborting(false)} />}
      {complaining && <CaseModal exchangeId={exchange.id} partnerName={exchange.partner.name} onClose={() => setComplaining(false)} />}
    </div>
  )
}

// Досрочный выход: причина обязательна (ТЗ 6.1)
function AbortModal({ exchangeId, onClose }: { exchangeId: string; onClose: () => void }) {
  const { t } = useTranslation()
  const invalidate = useInvalidateAll()
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<{ reason: string }>()
  const mutation = useMutation({
    mutationFn: ({ reason }: { reason: string }) => abortExchange({ id: exchangeId, reason: reason.trim() }),
    onSuccess: () => {
      invalidate()
      onClose()
    },
  })

  return (
    <Modal title={t('exchange.abort')} onClose={onClose}>
      <form onSubmit={handleSubmit((v) => mutation.mutate(v))} className="space-y-4" noValidate>
        <FormField label={t('exchange.abortReason')} error={errors.reason}>
          <textarea
            rows={4}
            maxLength={500}
            className="input resize-none"
            {...register('reason', {
              required: 'validation.required',
              validate: (v) => v.trim().length > 0 || 'validation.required',
            })}
          />
        </FormField>
        {mutation.isError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, mutation.error)}</p>}
        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className="btn-primary" disabled={mutation.isPending}>
            {mutation.isPending ? t('common.loading') : t('exchange.abortConfirm')}
          </button>
        </div>
      </form>
    </Modal>
  )
}
