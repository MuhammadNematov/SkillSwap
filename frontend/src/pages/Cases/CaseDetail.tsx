import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { answerExplanation, appealCase, getCase, getMyExplanations, withdrawCase, type Explanation } from '../../api/cases'
import { errorMessage } from '../../api/errors'
import { getMyId } from '../../api/identity'
import Icon from '../../components/Icon'
import Modal from '../../components/Modal'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'
import { CaseStatusChip } from './parts'

// Обращение: ход дела, вопросы модератора и мои ответы, решение и апелляция
export default function CaseDetail() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { id = '' } = useParams()
  const qc = useQueryClient()
  const me = getMyId()
  const [appealing, setAppealing] = useState(false)
  const [answers, setAnswers] = useState<Record<string, string>>({})

  const { data: c, isLoading, error } = useQuery({ queryKey: ['case', id], queryFn: () => getCase(id) })
  const showThread = !!c && c.status !== 'Open' && c.status !== 'Withdrawn'
  const { data: thread } = useQuery({ queryKey: ['caseThread', id], queryFn: () => getMyExplanations(id), enabled: showThread })

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['case', id] })
    qc.invalidateQueries({ queryKey: ['caseThread', id] })
    qc.invalidateQueries({ queryKey: ['cases'] })
  }
  const withdraw = useMutation({ mutationFn: withdrawCase, onSuccess: refresh })
  const answer = useMutation({
    mutationFn: answerExplanation,
    onSuccess: (_d, v) => {
      setAnswers((a) => ({ ...a, [v.questionId]: '' }))
      refresh()
    },
  })

  if (isLoading) return <Spinner />
  if (error || !c) return <div className="card text-sm text-red-700">{errorMessage(t, error)}</div>

  const items = thread?.items ?? []
  const open = c.status === 'WaitingResponse'
  const mineQuestions = (x: Explanation) => x.kind === 'Question' && x.recipientId === me

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <Link to="/app/cases" className="inline-flex items-center gap-1 text-sm text-slate-500 hover:text-brand-700">
        <Icon name="arrowRight" className="h-4 w-4 rotate-180" />
        {t('cases.back')}
      </Link>

      <section className="card space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h1 className="text-xl font-bold text-slate-900">{t(`cases.type.${c.type}`)}</h1>
          <CaseStatusChip status={c.status} />
        </div>
        <div className="text-sm text-slate-600">{t(`cases.categories.${c.category}`)}</div>
        <div className="text-xs text-slate-400">
          {c.myRole === 'Opener' ? t('cases.against', { name: c.against.name }) : t('cases.from', { name: c.openedBy.name })} · {formatDateTime(c.createdAt, lang)}
        </div>
        {c.fromCall && <div className="text-xs font-medium text-amber-700">{t('cases.fromCallBadge')}</div>}
        <p className="whitespace-pre-wrap break-words text-sm text-slate-800">{c.description}</p>
        <p className="rounded-lg bg-brand-50 px-3 py-2 text-xs text-slate-600">{t(`cases.statusHint.${c.status}`)}</p>
        <Link to={`/app/exchanges/${c.exchangeId}`} className="text-sm font-semibold text-brand-700 hover:underline">
          {t('cases.toExchange')}
        </Link>
        {c.canWithdraw && (
          <button className="btn-secondary" disabled={withdraw.isPending} onClick={() => withdraw.mutate(c.id)}>
            {t('cases.withdraw')}
          </button>
        )}
        {withdraw.isError && <p className="text-xs text-red-600">{errorMessage(t, withdraw.error)}</p>}
      </section>

      {showThread && items.length > 0 && (
        <section className="card space-y-3">
          <h2 className="font-bold text-slate-900">{t('cases.explanations')}</h2>
          {open && c.responseDeadlineAt && <p className="text-xs text-amber-700">{t('cases.answerBy', { date: formatDateTime(c.responseDeadlineAt, lang) })}</p>}
          <ul className="space-y-3">
            {items.map((x) => (
              <li key={x.id} className={`rounded-lg p-3 text-sm ${x.kind === 'Question' ? 'bg-brand-50' : 'border border-ink-line'}`}>
                <div className="mb-1 flex items-center justify-between gap-2 text-xs text-slate-400">
                  <span>{x.kind === 'Question' ? t('cases.moderatorAsks') : t('cases.yourAnswer')}</span>
                  <span>{formatDateTime(x.createdAt, lang)}</span>
                </div>
                <p className="whitespace-pre-wrap break-words text-slate-800">{x.text}</p>
                {mineQuestions(x) && !x.answered && open && (
                  <div className="mt-3 space-y-2">
                    <textarea
                      rows={3}
                      maxLength={2000}
                      className="input resize-none"
                      placeholder={t('cases.answerPlaceholder')}
                      value={answers[x.id] ?? ''}
                      onChange={(e) => setAnswers((a) => ({ ...a, [x.id]: e.target.value }))}
                    />
                    <button
                      className="btn-primary px-4 py-2"
                      disabled={answer.isPending || !(answers[x.id] ?? '').trim()}
                      onClick={() => answer.mutate({ id: c.id, questionId: x.id, text: (answers[x.id] ?? '').trim() })}
                    >
                      {t('cases.sendAnswer')}
                    </button>
                  </div>
                )}
              </li>
            ))}
          </ul>
          {answer.isError && <p className="text-xs text-red-600">{errorMessage(t, answer.error)}</p>}
        </section>
      )}

      {c.decision && (
        <section className="card space-y-2">
          <h2 className="font-bold text-slate-900">{t('cases.decisionTitle')}</h2>
          <div className="text-sm font-semibold text-slate-900">{t(`cases.decision.${c.decision}`)}</div>
          {c.alsoWarned && <div className="text-xs text-amber-700">{t('cases.alsoWarned')}</div>}
          {c.decisionReason && <p className="whitespace-pre-wrap break-words text-sm text-slate-700">{c.decisionReason}</p>}
          {c.resolvedAt && <div className="text-xs text-slate-400">{formatDateTime(c.resolvedAt, lang)}</div>}
          {c.canAppeal && (
            <div className="pt-2">
              <button className="btn-secondary" onClick={() => setAppealing(true)}>
                {t('cases.appeal')}
              </button>
              <p className="mt-1 text-xs text-slate-400">{t('cases.appealHint')}</p>
            </div>
          )}
        </section>
      )}

      {c.appeal && (
        <section className="card space-y-2">
          <h2 className="font-bold text-slate-900">{t('cases.appealTitle')}</h2>
          <p className="whitespace-pre-wrap break-words text-sm text-slate-700">{c.appeal.reason}</p>
          <div className="text-xs text-slate-500">
            {c.appeal.outcome
              ? `${t(`cases.appealOutcome.${c.appeal.outcome}`)}${c.appeal.finalDecision ? `: ${t(`cases.decision.${c.appeal.finalDecision}`)}` : ''}`
              : c.appeal.reviewInProgress
                ? t('cases.appealReviewing')
                : t('cases.appealFiled')}
          </div>
        </section>
      )}

      {appealing && <AppealModal id={c.id} onClose={() => setAppealing(false)} onDone={refresh} />}
    </div>
  )
}

function AppealModal({ id, onClose, onDone }: { id: string; onClose: () => void; onDone: () => void }) {
  const { t } = useTranslation()
  const [reason, setReason] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const send = useMutation({
    mutationFn: appealCase,
    onSuccess: () => {
      onDone()
      onClose()
    },
  })
  return (
    <Modal title={t('cases.appeal')} onClose={onClose}>
      <div className="space-y-4">
        <p className="text-sm text-slate-600">{t('cases.appealText')}</p>
        <textarea rows={5} maxLength={2000} className="input resize-none" placeholder={t('cases.appealPlaceholder')} value={reason} onChange={(e) => setReason(e.target.value)} />
        {(formError || send.isError) && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{formError ?? errorMessage(t, send.error)}</p>}
        <div className="flex justify-end gap-3">
          <button className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button
            className="btn-primary"
            disabled={send.isPending}
            onClick={() => {
              if (reason.trim().length < 20) return setFormError(t('cases.reasonLength'))
              setFormError(null)
              send.mutate({ id, reason: reason.trim() })
            }}
          >
            {send.isPending ? t('common.loading') : t('cases.appealSend')}
          </button>
        </div>
      </div>
    </Modal>
  )
}
