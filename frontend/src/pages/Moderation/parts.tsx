import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { CasePerson, ModMessage } from '../../api/cases'
import { formatDateTime } from '../../utils/dates'

// Бейджи обращения для очереди и карточки (общие для модератора и админа)
export function CaseBadges({ priority, overdue, fromCall }: { priority: string; overdue: boolean; fromCall: boolean }) {
  const { t } = useTranslation()
  return (
    <>
      {priority === 'High' && <span className="rounded-full bg-red-100/80 px-2.5 py-0.5 text-xs font-medium text-red-700">{t('moderation.priorityHigh')}</span>}
      {overdue && <span className="rounded-full bg-red-100/80 px-2.5 py-0.5 text-xs font-medium text-red-700">{t('moderation.overdue')}</span>}
      {fromCall && <span className="rounded-full bg-brand-100/80 px-2.5 py-0.5 text-xs font-medium text-brand-700">{t('cases.fromCallBadge')}</span>}
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
        {m.isEvidence && <span className="rounded-full bg-amber-100/80 px-2 py-0.5 font-medium text-amber-700">{t('moderation.chat.evidence')}</span>}
        {m.deletedAt && <span className="rounded-full bg-red-100/80 px-2 py-0.5 font-medium text-red-700">{t('moderation.chat.deleted', { time: formatDateTime(m.deletedAt, lang) })}</span>}
        {m.editedAt && !m.deletedAt && <span>{t('moderation.chat.edited')}</span>}
        {m.paymentWarning && <span className="rounded-full bg-amber-100/80 px-2 py-0.5 font-medium text-amber-700">{t('moderation.chat.paymentWarning')}</span>}
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
