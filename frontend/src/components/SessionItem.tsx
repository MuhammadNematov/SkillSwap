import { useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { errorMessage } from '../api/errors'
import type { LessonSession, SessionStatus } from '../api/types'
import { confirmSessionManual } from '../api/exchanges'
import { useInvalidateAll, useSessionActions } from '../hooks/useSessionActions'
import { formatDateTime, formatInZone, isJoinWindowOpen, utcOffsetLabel } from '../utils/dates'
import Icon from './Icon'
import RescheduleModal from './RescheduleModal'

const TONE: Partial<Record<SessionStatus, string>> = {
  Scheduled: 'bg-brand-100/80 text-brand-700',
  Proposed: 'bg-amber-100/80 text-amber-700',
  InProgress: 'bg-emerald-100/80 text-emerald-700',
  Completed: 'bg-emerald-100/80 text-emerald-700',
}

export function SessionStatusChip({ status }: { status: SessionStatus }) {
  const { t } = useTranslation()
  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${TONE[status] ?? 'bg-slate-100/80 text-slate-600'}`}>
      {t(`session.status.${status}`)}
    </span>
  )
}

interface Props {
  session: LessonSession
  onCancel: (session: LessonSession) => void
  showPartner?: boolean
}

// Строка урока: дата и время (в моём поясе и у партнёра), статус, действия
export default function SessionItem({ session, onCancel, showPartner }: Props) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { confirm, decline } = useSessionActions()
  const invalidateAll = useInvalidateAll()
  const [rescheduling, setRescheduling] = useState(false)
  const happened = useMutation({ mutationFn: confirmSessionManual, onSuccess: invalidateAll })

  const canJoin = (session.status === 'Scheduled' || session.status === 'InProgress') && isJoinWindowOpen(session.startAt)
  const incomingProposal = session.status === 'Proposed' && !session.proposedByMe
  const error = confirm.error ?? decline.error ?? happened.error

  return (
    <li className="rounded-xl border border-ink-line bg-brand-50 p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 font-semibold text-slate-900">
          <Icon name="calendar" className="h-4 w-4 text-brand-600" />
          {formatDateTime(session.startAt, lang)}
        </div>
        <SessionStatusChip status={session.status} />
      </div>

      <div className="mt-1.5 space-y-0.5 text-sm text-slate-500">
        {session.topic && <div className="text-slate-700">{session.topic}</div>}
        <div>
          {showPartner && `${session.partnerName} · `}
          {t('session.teacher', { name: session.teacherIsMe ? t('session.you') : session.partnerName })}
        </div>
        <div className="text-xs text-slate-400">
          {t('session.partnerLocal', {
            time: `${formatInZone(session.startAt, session.partnerTimeZone, lang)} (${utcOffsetLabel(session.partnerTimeZone)})`,
          })}
        </div>
      </div>

      {error && <p className="mt-2 text-xs text-red-600">{errorMessage(t, error)}</p>}

      <div className="mt-3 flex flex-wrap gap-2">
        {session.status === 'Scheduled' && (
          <>
            {canJoin ? (
              <Link to={`/app/call/${session.id}`} className="btn-primary px-4 py-2">
                <Icon name="video" className="h-4 w-4" />
                {t('session.join')}
              </Link>
            ) : (
              <button className="btn-primary px-4 py-2" disabled title={t('dashboard.joinHint')}>
                <Icon name="video" className="h-4 w-4" />
                {t('session.join')}
              </button>
            )}
            <button className="btn-secondary px-4 py-2" onClick={() => setRescheduling(true)}>
              {t('session.reschedule')}
            </button>
            <button className="btn-secondary px-4 py-2" onClick={() => onCancel(session)}>
              {t('session.cancel')}
            </button>
          </>
        )}
        {session.status === 'Proposed' && session.proposedByMe && (
          <button className="btn-secondary px-4 py-2" onClick={() => onCancel(session)}>
            {t('session.withdraw')}
          </button>
        )}
        {session.status === 'Unconfirmed' &&
          (session.confirmedByMe ? (
            <span className="text-sm text-slate-500">{t('session.waitingConfirm')}</span>
          ) : (
            <button className="btn-primary px-4 py-2" disabled={happened.isPending} onClick={() => happened.mutate(session.id)}>
              {t('session.happened')}
            </button>
          ))}
        {incomingProposal && (
          <>
            <button className="btn-primary px-4 py-2" disabled={confirm.isPending} onClick={() => confirm.mutate(session.id)}>
              {t('session.accept')}
            </button>
            <button className="btn-secondary px-4 py-2" disabled={decline.isPending} onClick={() => decline.mutate(session.id)}>
              {t('exchanges.decline')}
            </button>
          </>
        )}
      </div>
      {rescheduling && <RescheduleModal session={session} onClose={() => setRescheduling(false)} />}
    </li>
  )
}
