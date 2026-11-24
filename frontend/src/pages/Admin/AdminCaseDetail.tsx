import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import {
  assignReviewer,
  CAN_ALSO_WARN,
  DECISIONS,
  decideAppeal,
  getAdminCase,
  getModerators,
  type AdminCase,
  type AppealOutcome,
  type CaseDecision,
} from '../../api/cases'
import { errorMessage } from '../../api/errors'
import { SkeletonList } from '../../components/staff/Skeleton'
import { formatDateTime } from '../../utils/dates'
import { CaseStatusChip } from '../Cases/parts'
import { CaseBadges, ChatMessageRow, ExplanationItems, ReviewSummary, authorName } from '../Moderation/parts'
import { BanCard } from './BansTab'

const errBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'
const area = 'input min-h-[96px] resize-y'

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="card space-y-3">
      <h2 className="font-bold text-slate-900">{title}</h2>
      {children}
    </section>
  )
}

function AssignReviewer({ c, done }: { c: AdminCase; done: () => void }) {
  const { t } = useTranslation()
  const [moderatorId, setModeratorId] = useState('')
  const mods = useQuery({ queryKey: ['adminModerators'], queryFn: getModerators })
  const assign = useMutation({ mutationFn: assignReviewer, onSuccess: done })
  return (
    <div className="space-y-2 border-t border-ink-line pt-3">
      <div className="text-sm font-medium text-slate-800">{t('admin.appeal.pickReviewer')}</div>
      <p className="text-xs text-slate-500">{t('admin.appeal.assignHint')}</p>
      <select className="input" value={moderatorId} onChange={(e) => setModeratorId(e.target.value)}>
        <option value="">{t('admin.appeal.pickPlaceholder')}</option>
        {(mods.data ?? []).map((m) => (
          <option key={m.id} value={m.id}>
            {m.name}
          </option>
        ))}
      </select>
      {assign.error && <p className={errBox}>{errorMessage(t, assign.error)}</p>}
      <button className="btn-primary" disabled={!moderatorId || assign.isPending} onClick={() => assign.mutate({ id: c.id, moderatorId })}>
        {assign.isPending ? t('common.loading') : t('admin.appeal.assign')}
      </button>
    </div>
  )
}

function DecideAppeal({ c, done }: { c: AdminCase; done: () => void }) {
  const { t } = useTranslation()
  const [outcome, setOutcome] = useState<AppealOutcome | null>(null)
  const [newDecision, setNewDecision] = useState<CaseDecision | null>(null)
  const [alsoWarned, setAlsoWarned] = useState(false)
  const [reason, setReason] = useState('')
  const decide = useMutation({ mutationFn: decideAppeal, onSuccess: done })
  const options = DECISIONS[c.type].filter((d) => d !== c.decision)
  const canWarn = outcome === 'Changed' && !!newDecision && CAN_ALSO_WARN.includes(newDecision)
  const ok = !!outcome && (outcome !== 'Changed' || !!newDecision) && reason.trim().length >= 20 && reason.trim().length <= 2000
  return (
    <div className="space-y-3 border-t border-ink-line pt-3">
      <div className="text-sm font-medium text-slate-800">{t('admin.appeal.decide')}</div>
      <fieldset className="space-y-1.5">
        {(['Upheld', 'Overturned', 'Changed'] as const).map((o) => (
          <label key={o} className="flex cursor-pointer items-center gap-2 text-sm text-slate-700">
            <input
              type="radio"
              name="outcome"
              checked={outcome === o}
              onChange={() => {
                setOutcome(o)
                if (o !== 'Changed') {
                  setNewDecision(null)
                  setAlsoWarned(false)
                }
              }}
            />
            {t(`admin.appeal.outcome.${o}`)}
          </label>
        ))}
      </fieldset>
      {outcome === 'Changed' && (
        <div className="space-y-2">
          <div className="text-xs text-slate-500">{t('admin.appeal.newDecision')}</div>
          <div className="flex flex-wrap gap-2">
            {options.map((d) => (
              <button
                key={d}
                type="button"
                className={`chip ${newDecision === d ? 'chip-on' : ''}`}
                onClick={() => {
                  setNewDecision(d)
                  if (!CAN_ALSO_WARN.includes(d)) setAlsoWarned(false)
                }}
              >
                {t(`cases.decision.${d}`)}
              </button>
            ))}
          </div>
          {canWarn && (
            <label className="flex cursor-pointer items-center gap-2 text-sm text-slate-700">
              <input type="checkbox" checked={alsoWarned} onChange={(e) => setAlsoWarned(e.target.checked)} />
              {t('admin.appeal.alsoWarn')}
            </label>
          )}
        </div>
      )}
      <textarea className={area} maxLength={2000} placeholder={t('admin.appeal.reasonPlaceholder')} value={reason} onChange={(e) => setReason(e.target.value)} />
      <p className="text-xs text-slate-400">{t('admin.appeal.rollbackNote')}</p>
      {decide.error && <p className={errBox}>{errorMessage(t, decide.error)}</p>}
      <button
        className="btn-primary"
        disabled={!ok || decide.isPending}
        onClick={() => outcome && decide.mutate({ id: c.id, outcome, newDecision: outcome === 'Changed' ? newDecision : null, alsoWarned: canWarn && alsoWarned, reason: reason.trim() })}
      >
        {decide.isPending ? t('common.loading') : t('admin.appeal.send')}
      </button>
    </div>
  )
}

// Карточка обращения для админа: материалы без переписки, апелляция, предупреждения и запросы на блокировку
export default function AdminCaseDetail() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { id = '' } = useParams()
  const qc = useQueryClient()
  const { data, isLoading, error } = useQuery({ queryKey: ['adminCase', id], queryFn: () => getAdminCase(id) })
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['adminCase', id] })
    qc.invalidateQueries({ queryKey: ['adminCases'] })
    qc.invalidateQueries({ queryKey: ['adminBans'] })
  }

  if (isLoading) return <SkeletonList rows={3} label={t('common.loading')} />
  if (error || !data) return <p className={errBox}>{errorMessage(t, error)}</p>

  const c = data.case
  const people = [c.openedBy, c.against]
  const a = c.appeal
  const canAssign = c.status === 'Appealed' && !!a && !a.reviewerModeratorId
  const canDecide = c.status === 'Appealed' && !!a && !a.outcome

  return (
    <>
      <Link to="/admin/cases" className="text-sm font-medium text-brand-600 hover:underline">
        ← {t('admin.back')}
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
          {c.status === 'Open' && <span>{t('admin.cases.dueAt', { time: formatDateTime(c.dueAt, lang) })}</span>}
          <span>{c.moderatorId ? t('admin.cases.moderator') : t('admin.cases.notTaken')}</span>
        </div>
        <p className="whitespace-pre-wrap break-words rounded-xl bg-brand-50 px-3 py-2 text-sm text-slate-800">{c.description}</p>
        {c.decision && (
          <div className="rounded-xl border border-ink-line px-3 py-2 text-sm">
            <b className="text-slate-900">{t(`cases.decision.${c.decision}`)}</b>
            {c.alsoWarned && <span className="text-slate-500"> · {t('moderation.resolve.alsoWarn')}</span>}
            {c.decisionReason && <p className="mt-1 whitespace-pre-wrap break-words text-slate-600">{c.decisionReason}</p>}
          </div>
        )}
      </section>

      {a && (
        <Section title={t('admin.appeal.title')}>
          <div className="text-xs text-slate-500">{t('moderation.appeal.filed', { time: formatDateTime(a.filedAt, lang) })}</div>
          <div>
            <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('moderation.appeal.reason')}</div>
            <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-800">{a.reason}</p>
          </div>
          {a.reviewerConclusion ? (
            <div>
              <div className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('moderation.appeal.conclusion')}</div>
              <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-800">{a.reviewerConclusion}</p>
            </div>
          ) : c.status === 'AppealReview' ? (
            <p className="text-sm text-amber-700">{t('admin.appeal.reviewing')}</p>
          ) : null}
          {a.outcome && (
            <p className="text-sm font-semibold text-slate-900">
              {t(`cases.appealOutcome.${a.outcome}`)}
              {a.outcome === 'Changed' && a.finalDecision ? `: ${t(`cases.decision.${a.finalDecision}`)}` : ''}
            </p>
          )}
          {canAssign && <AssignReviewer c={c} done={refresh} />}
          {canDecide && <DecideAppeal c={c} done={refresh} />}
        </Section>
      )}

      {data.review && (
        <Section title={t('moderation.review')}>
          <ReviewSummary review={data.review} />
        </Section>
      )}

      <Section title={t('admin.cases.materials')}>
        <p className="text-xs text-slate-500">{t('admin.cases.evidenceHint')}</p>
        {data.evidence.length === 0 ? (
          <p className="text-sm text-slate-500">{t('admin.cases.noEvidence')}</p>
        ) : (
          <ul className="space-y-2">
            {data.evidence.map((m) => (
              <ChatMessageRow key={m.id} m={m} author={authorName(m.senderId, people, t('moderation.chat.system'))} />
            ))}
          </ul>
        )}
      </Section>

      {data.explanations.length > 0 && (
        <Section title={t('cases.explanations')}>
          <ExplanationItems items={data.explanations} people={people} />
        </Section>
      )}

      <Section title={t('admin.cases.warnings')}>
        {data.warnings.length === 0 ? (
          <p className="text-sm text-slate-500">{t('admin.cases.noWarnings')}</p>
        ) : (
          <ul className="space-y-2">
            {data.warnings.map((w) => (
              <li key={w.id} className="rounded-xl border border-ink-line px-3 py-2 text-sm">
                <div className="font-medium text-slate-900">
                  {t('admin.cases.warningTo', { name: authorName(w.userId, people, w.userId), date: formatDateTime(w.expiresAt, lang) })}
                  {w.revokedAt && <span className="ml-2 text-xs text-slate-500">{t('admin.cases.warningRevoked')}</span>}
                </div>
                <p className="mt-0.5 whitespace-pre-wrap break-words text-slate-600">{w.reason}</p>
              </li>
            ))}
          </ul>
        )}
      </Section>

      <Section title={t('admin.cases.bans')}>
        {data.banRequests.length === 0 ? (
          <p className="text-sm text-slate-500">{t('admin.cases.noBans')}</p>
        ) : (
          <div className="space-y-3">
            {data.banRequests.map((b) => (
              <BanCard key={b.id} ban={b} />
            ))}
          </div>
        )}
      </Section>
    </>
  )
}
