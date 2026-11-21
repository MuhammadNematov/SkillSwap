import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import {
  askQuestion,
  CAN_ALSO_WARN,
  DECISIONS,
  getModCase,
  getModExplanations,
  resolveCase,
  sendAppealConclusion,
  setExchangeFrozen,
  stopWaiting,
  takeCase,
  type CaseDecision,
  type ModCase,
} from '../../api/cases'
import { errorMessage } from '../../api/errors'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'
import { CaseStatusChip } from '../Cases/parts'
import ChatReader from './ChatReader'
import { CaseBadges, ExplanationItems, ReviewSummary } from './parts'

const errBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'
const area = 'input min-h-[96px] resize-y'

function useRefresh(id: string) {
  const qc = useQueryClient()
  return () => {
    qc.invalidateQueries({ queryKey: ['modCase', id] })
    qc.invalidateQueries({ queryKey: ['modCases'] })
    qc.invalidateQueries({ queryKey: ['modExpl', id] })
    qc.invalidateQueries({ queryKey: ['modChat', id] })
  }
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="card space-y-3">
      <h2 className="font-bold text-slate-900">{title}</h2>
      {children}
    </section>
  )
}

function Scores({ c }: { c: ModCase }) {
  const { t } = useTranslation()
  if (!c.review) return null
  return (
    <Section title={t('moderation.review')}>
      <ReviewSummary review={c.review} />
    </Section>
  )
}

function Explanations({ c }: { c: ModCase }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const q = useQuery({ queryKey: ['modExpl', c.id], queryFn: () => getModExplanations(c.id) })
  if (q.isLoading || !q.data || q.data.items.length === 0) return null
  return (
    <Section title={t('cases.explanations')}>
      {q.data.responseDeadlineAt && c.status === 'WaitingResponse' && (
        <p className="text-sm text-amber-700">{t('moderation.waitingUntil', { time: formatDateTime(q.data.responseDeadlineAt, lang) })}</p>
      )}
      <ExplanationItems items={q.data.items} people={[c.openedBy, c.against]} />
    </Section>
  )
}

function AskForm({ c, done }: { c: ModCase; done: () => void }) {
  const { t } = useTranslation()
  const [recipient, setRecipient] = useState<'Opener' | 'Against' | 'Both'>('Both')
  const [text, setText] = useState('')
  const ask = useMutation({ mutationFn: askQuestion, onSuccess: () => { setText(''); done() } })
  const ok = text.trim().length >= 1 && text.trim().length <= 2000
  return (
    <Section title={t('moderation.ask.title')}>
      <p className="text-sm text-slate-500">{t('moderation.ask.hint')}</p>
      <div className="flex flex-wrap gap-2">
        {(['Both', 'Opener', 'Against'] as const).map((r) => (
          <button key={r} type="button" className={`chip ${recipient === r ? 'chip-on' : ''}`} onClick={() => setRecipient(r)}>
            {r === 'Both' ? t('moderation.ask.both') : r === 'Opener' ? c.openedBy.name : c.against.name}
          </button>
        ))}
      </div>
      <textarea className={area} maxLength={2000} placeholder={t('moderation.ask.placeholder')} value={text} onChange={(e) => setText(e.target.value)} />
      {ask.error && <p className={errBox}>{errorMessage(t, ask.error)}</p>}
      <button className="btn-primary" disabled={!ok || ask.isPending} onClick={() => ask.mutate({ id: c.id, recipient, text: text.trim() })}>
        {ask.isPending ? t('common.loading') : t('moderation.ask.send')}
      </button>
    </Section>
  )
}

function StopWaitingForm({ c, done }: { c: ModCase; done: () => void }) {
  const { t } = useTranslation()
  const [reason, setReason] = useState('')
  const stop = useMutation({ mutationFn: stopWaiting, onSuccess: () => { setReason(''); done() } })
  const ok = reason.trim().length >= 20 && reason.trim().length <= 2000
  return (
    <Section title={t('moderation.stop.title')}>
      <p className="text-sm text-slate-500">{t('moderation.stop.hint')}</p>
      <textarea className={area} maxLength={2000} placeholder={t('moderation.reasonPlaceholder')} value={reason} onChange={(e) => setReason(e.target.value)} />
      {stop.error && <p className={errBox}>{errorMessage(t, stop.error)}</p>}
      <button className="btn-secondary" disabled={!ok || stop.isPending} onClick={() => stop.mutate({ id: c.id, reason: reason.trim() })}>
        {stop.isPending ? t('common.loading') : t('moderation.stop.send')}
      </button>
    </Section>
  )
}

function FreezeBar({ c }: { c: ModCase }) {
  const { t } = useTranslation()
  const [notice, setNotice] = useState<string | null>(null)
  const freeze = useMutation({
    mutationFn: setExchangeFrozen,
    onSuccess: (_, v) => setNotice(t(v.frozen ? 'moderation.freeze.frozen' : 'moderation.freeze.unfrozen')),
  })
  return (
    <Section title={t('moderation.freeze.title')}>
      <p className="text-sm text-slate-500">{t('moderation.freeze.hint')}</p>
      <div className="flex flex-wrap gap-2">
        <button className="btn-secondary" disabled={freeze.isPending} onClick={() => { setNotice(null); freeze.mutate({ id: c.id, frozen: true }) }}>
          {t('moderation.freeze.do')}
        </button>
        <button className="btn-secondary" disabled={freeze.isPending} onClick={() => { setNotice(null); freeze.mutate({ id: c.id, frozen: false }) }}>
          {t('moderation.freeze.undo')}
        </button>
      </div>
      {notice && <p className="text-sm text-emerald-700">{notice}</p>}
      {freeze.error && <p className={errBox}>{errorMessage(t, freeze.error)}</p>}
    </Section>
  )
}

function ResolveForm({ c, done }: { c: ModCase; done: () => void }) {
  const { t } = useTranslation()
  const [decision, setDecision] = useState<CaseDecision | null>(null)
  const [alsoWarned, setAlsoWarned] = useState(false)
  const [reason, setReason] = useState('')
  const resolve = useMutation({ mutationFn: resolveCase, onSuccess: done })
  const ok = !!decision && reason.trim().length >= 20 && reason.trim().length <= 2000
  const canWarn = !!decision && CAN_ALSO_WARN.includes(decision)
  return (
    <Section title={t('moderation.resolve.title')}>
      <fieldset className="space-y-1.5">
        {DECISIONS[c.type].map((d) => (
          <label key={d} className="flex cursor-pointer items-start gap-2 text-sm text-slate-700">
            <input
              type="radio"
              name="decision"
              className="mt-1"
              checked={decision === d}
              onChange={() => {
                setDecision(d)
                if (!CAN_ALSO_WARN.includes(d)) setAlsoWarned(false)
              }}
            />
            <span>
              <span className="font-medium text-slate-900">{t(`cases.decision.${d}`)}</span>
              <span className="block text-xs text-slate-500">{t(`moderation.decisionHint.${d}`)}</span>
            </span>
          </label>
        ))}
      </fieldset>
      {canWarn && (
        <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-700">
          <input type="checkbox" checked={alsoWarned} onChange={(e) => setAlsoWarned(e.target.checked)} />
          {t('moderation.resolve.alsoWarn')}
        </label>
      )}
      <div>
        <textarea className={area} maxLength={2000} placeholder={t('moderation.resolve.reasonPlaceholder')} value={reason} onChange={(e) => setReason(e.target.value)} />
        <p className="mt-1 text-xs text-slate-400">{t('moderation.reasonLength')}</p>
      </div>
      {resolve.error && <p className={errBox}>{errorMessage(t, resolve.error)}</p>}
      <button
        className="btn-primary"
        disabled={!ok || resolve.isPending}
        onClick={() => decision && resolve.mutate({ id: c.id, decision, alsoWarned: canWarn && alsoWarned, reason: reason.trim() })}
      >
        {resolve.isPending ? t('common.loading') : t('moderation.resolve.send')}
      </button>
    </Section>
  )
}

function AppealBlock({ c, done }: { c: ModCase; done: () => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const a = c.appeal
  const [text, setText] = useState('')
  const send = useMutation({ mutationFn: sendAppealConclusion, onSuccess: () => { setText(''); done() } })
  if (!a) return null
  const ok = text.trim().length >= 20 && text.trim().length <= 4000
  const canConclude = c.isAppealReviewer && c.status === 'AppealReview' && !a.reviewerConclusion
  return (
    <Section title={t('cases.appealTitle')}>
      <div className="text-xs text-slate-500">{t('moderation.appeal.filed', { time: formatDateTime(a.filedAt, lang) })}</div>
      <div>
        <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('moderation.appeal.reason')}</div>
        <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-800">{a.reason}</p>
      </div>
      {c.decision && (
        <div>
          <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('moderation.appeal.original')}</div>
          <p className="mt-1 text-sm text-slate-800">
            <b>{t(`cases.decision.${c.decision}`)}</b>
            {c.alsoWarned && <span className="text-slate-500"> · {t('moderation.resolve.alsoWarn')}</span>}
          </p>
          {c.decisionReason && <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-600">{c.decisionReason}</p>}
        </div>
      )}
      {a.reviewerConclusion && (
        <div>
          <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('moderation.appeal.conclusion')}</div>
          <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-800">{a.reviewerConclusion}</p>
        </div>
      )}
      {a.outcome && (
        <p className="text-sm font-semibold text-slate-900">
          {t(`cases.appealOutcome.${a.outcome}`)}
          {a.outcome === 'Changed' && a.finalDecision ? `: ${t(`cases.decision.${a.finalDecision}`)}` : ''}
        </p>
      )}
      {canConclude && (
        <div className="space-y-2 border-t border-ink-line pt-3">
          <p className="text-sm text-slate-500">{t('moderation.appeal.conclusionHint')}</p>
          <textarea className={area} maxLength={4000} placeholder={t('moderation.appeal.conclusionPlaceholder')} value={text} onChange={(e) => setText(e.target.value)} />
          <p className="text-xs text-slate-400">{t('moderation.appeal.conclusionLength')}</p>
          {send.error && <p className={errBox}>{errorMessage(t, send.error)}</p>}
          <button className="btn-primary" disabled={!ok || send.isPending} onClick={() => send.mutate({ id: c.id, conclusion: text.trim() })}>
            {send.isPending ? t('common.loading') : t('moderation.appeal.send')}
          </button>
        </div>
      )}
    </Section>
  )
}

// Карточка обращения для модератора: фабула, отзыв, апелляция, пояснения, переписка и действия по состоянию обращения
export default function ModCaseDetail() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { id = '' } = useParams()
  const refresh = useRefresh(id)
  const { data: c, isLoading, error } = useQuery({ queryKey: ['modCase', id], queryFn: () => getModCase(id) })
  const take = useMutation({ mutationFn: takeCase, onSuccess: refresh })

  if (isLoading) return <Spinner />
  if (error || !c) return <p className={errBox}>{errorMessage(t, error)}</p>

  const working = c.isMine && (c.status === 'InProgress' || c.status === 'WaitingResponse')
  const reviewing = c.isAppealReviewer && c.status === 'AppealReview'

  return (
    <>
      <Link to="/moderation" className="text-sm font-medium text-brand-600 hover:underline">
        ← {t('moderation.back')}
      </Link>

      <section className="card space-y-3">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="text-xl font-bold text-slate-900">{t(`cases.type.${c.type}`)}</h1>
          <CaseStatusChip status={c.status} />
          <CaseBadges priority={c.priority} overdue={c.isOverdue} fromCall={c.fromCall} />
        </div>
        <div className="text-sm text-slate-600">{t(`cases.categories.${c.category}`)}</div>
        <div className="flex flex-wrap gap-x-6 gap-y-1 text-sm text-slate-600">
          <span>
            {t('moderation.openedBy')}: <b className="text-slate-900">{c.openedBy.name}</b>
          </span>
          <span>
            {t('moderation.against')}: <b className="text-slate-900">{c.against.name}</b>
          </span>
          <span>{formatDateTime(c.createdAt, lang)}</span>
        </div>
        <p className="whitespace-pre-wrap break-words rounded-xl bg-brand-50 px-3 py-2 text-sm text-slate-800">{c.description}</p>
        {c.status === 'Open' && !c.isMine && (
          <div className="space-y-2">
            <p className="text-xs text-slate-500">{t('moderation.takeHint')}</p>
            {take.error && <p className={errBox}>{errorMessage(t, take.error)}</p>}
            <button className="btn-primary" disabled={take.isPending} onClick={() => take.mutate(c.id)}>
              {take.isPending ? t('common.loading') : t('moderation.take')}
            </button>
          </div>
        )}
        {c.decision && !c.appeal && (
          <div className="rounded-xl border border-ink-line px-3 py-2 text-sm">
            <b className="text-slate-900">{t(`cases.decision.${c.decision}`)}</b>
            {c.alsoWarned && <span className="text-slate-500"> · {t('moderation.resolve.alsoWarn')}</span>}
            {c.decisionReason && <p className="mt-1 whitespace-pre-wrap break-words text-slate-600">{c.decisionReason}</p>}
          </div>
        )}
      </section>

      <Scores c={c} />
      <AppealBlock c={c} done={refresh} />
      <Explanations c={c} />

      {working && c.status === 'WaitingResponse' && <StopWaitingForm c={c} done={refresh} />}
      {working && c.status === 'InProgress' && <AskForm c={c} done={refresh} />}
      {working && c.type !== 'ReviewDispute' && <FreezeBar c={c} />}
      {working && c.status === 'InProgress' && <ResolveForm c={c} done={refresh} />}
      {working && c.status === 'WaitingResponse' && <p className="text-sm text-slate-500">{t('moderation.resolveBlocked')}</p>}

      {(working || reviewing) && c.hasChatAccess && (
        <Section title={t('moderation.chat.title')}>
          <p className="text-xs text-slate-500">{t('moderation.chat.audit')}</p>
          <ChatReader caseId={c.id} />
        </Section>
      )}
    </>
  )
}
