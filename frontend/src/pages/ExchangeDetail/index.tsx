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
  const [cancelling, setCancelling] = useState<LessonSession | null>(null)

  const { data: exchange, isLoading, error } = useQuery({ queryKey: ['exchange', id], queryFn: () => getExchange(id) })
  const { data: sessions = [] } = useQuery({ queryKey: ['sessions', id], queryFn: () => getSessions(id) })

  const complete = useMutation({ mutationFn: completeExchange, onSuccess: invalidate })
  const extend = useMutation({ mutationFn: extendExchange, onSuccess: invalidate })
  const actionError = complete.error ?? extend.error

  if (isLoading) return <Spinner />
  if (error || !exchange) return <div className="card text-sm text-red-700">{errorMessage(t, error)}</div>

  const active = exchange.status === 'Active'
  const start = new Date(exchange.startDate).getTime()
  const end = new Date(exchange.endDate).getTime()
  const daysLeft = Math.max(0, Math.ceil((end - Date.now()) / DAY))
  const percent = Math.min(100, Math.max(0, ((Date.now() - start) / (end - start)) * 100))
  const total = exchange.myLessons + exchange.partnerLessons
  const myShare = total === 0 ? 50 : (exchange.myLessons / total) * 100
  const imbalance = Math.abs(exchange.myLessons - exchange.partnerLessons) > 3 // ТЗ 6.1: мягкое напоминание

  return (
    <div className="space-y-5">
      <Link to="/app/exchanges" className="inline-flex items-center gap-1 text-sm text-slate-500 hover:text-brand-700">
        <Icon name="arrowRight" className="h-4 w-4 rotate-180" />
        {t('exchange.back')}
      </Link>

      <section className="card flex flex-wrap items-center gap-4">
        <Avatar name={exchange.partner.name} className="h-14 w-14" />
        <div className="min-w-0 flex-1">
          <Link to={`/app/users/${exchange.partner.id}`} className="text-xl font-bold text-slate-900 hover:text-brand-700">
            {exchange.partner.name}
          </Link>
          <div className="mt-1.5 flex flex-wrap items-center gap-2 text-sm text-slate-600">
            <SkillChip name={exchange.mySkill} />
            <Icon name="swap" className="h-4 w-4 text-slate-400" />
            <SkillChip name={exchange.partnerSkill} tone="green" />
          </div>
        </div>
        <span className="rounded-full bg-white/70 px-3 py-1 text-sm font-medium text-slate-700">{t(`exchangeStatus.${exchange.status}`)}</span>
      </section>

      <div className="grid gap-5 lg:grid-cols-5">
        <div className="space-y-5 lg:col-span-2">
          <section className="card">
            <h2 className="font-bold text-slate-900">{t('exchange.balance')}</h2>
            <div className="mt-3 text-center text-4xl font-extrabold text-brand-700">
              {exchange.myLessons} : {exchange.partnerLessons}
            </div>
            <div className="mt-3 flex h-2 overflow-hidden rounded-full bg-slate-200/70">
              <div className="bg-brand-600" style={{ width: `${myShare}%` }} />
              <div className="bg-emerald-500" style={{ width: `${100 - myShare}%` }} />
            </div>
            <div className="mt-2 flex justify-between text-xs text-slate-500">
              <span>{t('exchange.you')}</span>
              <span>{exchange.partner.name}</span>
            </div>
            <p className="mt-2 text-xs text-slate-400">{t('exchange.balanceHint')}</p>
            {imbalance && <p className="mt-3 rounded-lg bg-amber-50/80 px-3 py-2 text-sm text-amber-800">{t('exchange.balanceWarning')}</p>}
          </section>

          <section className="card">
            <h2 className="font-bold text-slate-900">{t('exchange.term')}</h2>
            <div className="mt-3 flex justify-between text-sm text-slate-600">
              <span>{t('exchange.progress')}</span>
              <span>{Math.round(percent)}%</span>
            </div>
            <div className="mt-1.5 h-2 overflow-hidden rounded-full bg-slate-200/70">
              <div className="h-full bg-brand-600" style={{ width: `${percent}%` }} />
            </div>
            <p className="mt-2 text-sm text-slate-500">{t('exchange.daysLeft', { count: daysLeft })}</p>
          </section>

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
              {!exchange.isExtended && (
                <button className="btn-secondary w-full" disabled={extend.isPending} onClick={() => extend.mutate(exchange.id)}>
                  {t('exchange.extend')}
                </button>
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

      {proposing && <SessionProposalModal exchangeId={exchange.id} partnerName={exchange.partner.name} onClose={() => setProposing(false)} />}
      {cancelling && <CancelSessionModal session={cancelling} onClose={() => setCancelling(null)} />}
      {aborting && <AbortModal exchangeId={exchange.id} onClose={() => setAborting(false)} />}
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
