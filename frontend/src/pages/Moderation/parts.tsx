import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { CasePerson, Explanation, ModMessage, ModReview } from '../../api/cases'
import Tag from '../../components/staff/Tag'
import { formatDateTime } from '../../utils/dates'

// Бейджи обращения для очереди и карточки (общие для модератора и админа)
export function CaseBadges({ priority, overdue, fromCall }: { priority: string; overdue: boolean; fromCall: boolean }) {
  const { t } = useTranslation()
  return (
    <>
      {priority === 'High' && <Tag tone="red">{t('moderation.priorityHigh')}</Tag>}
      {overdue && <Tag tone="red">{t('moderation.overdue')}</Tag>}
      {fromCall && <Tag tone="brand">{t('cases.fromCallBadge')}</Tag>}
    </>
  )
}

// Одно сообщение переписки: автор, время, пометки (доказательство, удалено, изменено, предупреждение об оплате), прежние версии
export function ChatMessageRow({ m, author }: { m: ModMessage; author: string }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const [open, setOpen] = useState(false)

  let body: string
  if (m.type === 'Text') body = m.text ?? ''
  else if (m.type === 'SessionCard') body = t('moderation.chat.sessionCard')
  else if (m.type === 'System') body = m.text ?? t('moderation.chat.system')
  else if (m.attachment) body = `${t(m.type === 'Voice' ? 'moderation.chat.voice' : 'moderation.chat.file')}: ${m.attachment.fileName} (${Math.max(1, Math.round(m.attachment.sizeBytes / 1024))} ${t('moderation.chat.kb')})`
  else body = t(m.type === 'Voice' ? 'moderation.chat.voice' : 'moderation.chat.file')

  return (
    <li className={`rounded-xl border px-3 py-2 ${m.isEvidence ? 'border-amber-300 bg-amber-50/10' : 'border-ink-line bg-brand-50'}`}>
      <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
        <span className="font-semibold text-slate-800">{author}</span>
        <span>{formatDateTime(m.createdAt, lang)}</span>
        {m.isEvidence && <Tag tone="amber">{t('moderation.chat.evidence')}</Tag>}
        {m.deletedAt && <Tag tone="red">{t('moderation.chat.deleted', { time: formatDateTime(m.deletedAt, lang) })}</Tag>}
        {m.editedAt && !m.deletedAt && <span>{t('moderation.chat.edited')}</span>}
        {m.paymentWarning && <Tag tone="amber">{t('moderation.chat.paymentWarning')}</Tag>}
      </div>
      <p className={`mt-1 whitespace-pre-wrap break-words text-sm ${m.deletedAt ? 'text-slate-500' : 'text-slate-800'}`}>{body || '—'}</p>
      {m.revisions.length > 0 && (
        <div className="mt-1">
          <button className="text-xs font-medium text-brand-600 hover:underline" onClick={() => setOpen((v) => !v)}>
            {open ? t('moderation.chat.hideRevisions') : t('moderation.chat.showRevisions', { count: m.revisions.length })}
          </button>
          {open && (
            <ul className="mt-1 space-y-1 border-l-2 border-ink-line pl-3">
              {m.revisions.map((r, i) => (
                <li key={i} className="text-xs text-slate-500">
                  <span className="text-slate-400">{formatDateTime(r.changedAt, lang)}: </span>
                  {r.text}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </li>
  )
}

export function authorName(id: string | null, people: CasePerson[], fallback: string): string {
  if (!id) return fallback
  return people.find((p) => p.id === id)?.name ?? fallback
}

// Спорный отзыв: четыре оценки, среднее, статус и текст (общий для модератора и админа)
export function ReviewSummary({ review: r }: { review: ModReview }) {
  const { t } = useTranslation()
  const rows: [string, number][] = [
    ['knowledge', r.scoreKnowledge],
    ['explaining', r.scoreExplaining],
    ['punctuality', r.scorePunctuality],
    ['politeness', r.scorePoliteness],
  ]
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-x-6 gap-y-1 text-sm text-slate-600">
        {rows.map(([k, v]) => (
          <span key={k}>
            {t(`reviews.criteria.${k}`)}: <b className="text-slate-900">{v}</b>
          </span>
        ))}
        <span>
          {t('moderation.reviewAverage')}: <b className="text-slate-900">{r.average.toFixed(1)}</b>
        </span>
        <Tag tone="gray">{t(`reviews.status.${r.status}`)}</Tag>
      </div>
      <p className="whitespace-pre-wrap break-words text-sm text-slate-800">{r.text}</p>
    </div>
  )
}

// Вопросы модератора и ответы участников
export function ExplanationItems({ items, people }: { items: Explanation[]; people: CasePerson[] }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const name = (id: string) => people.find((p) => p.id === id)?.name ?? t('moderation.moderator')
  return (
    <ul className="space-y-2">
      {items.map((x) => (
        <li key={x.id} className={`rounded-xl border px-3 py-2 text-sm ${x.kind === 'Question' ? 'border-brand-200 bg-brand-50' : 'border-ink-line'}`}>
          <div className="text-xs text-slate-500">
            {x.kind === 'Question' ? t('moderation.questionTo', { name: name(x.recipientId) }) : t('moderation.answerFrom', { name: name(x.senderId) })} · {formatDateTime(x.createdAt, lang)}
            {x.kind === 'Question' && !x.answered && <span className="ml-2 font-semibold text-amber-700">{t('moderation.noAnswerYet')}</span>}
          </div>
          <p className="mt-1 whitespace-pre-wrap break-words text-slate-800">{x.text}</p>
        </li>
      ))}
    </ul>
  )
}
