import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getConversations } from '../../api/exchanges'
import Avatar from '../../components/Avatar'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'

// Список чатов: по ТЗ чат есть только внутри обмена, личных сообщений нет
export default function Messages() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { data = [], isLoading } = useQuery({ queryKey: ['conversations'], queryFn: getConversations })

  if (isLoading) return <Spinner />

  function preview(c: (typeof data)[number]) {
    const m = c.last
    if (!m) return ''
    const body =
      m.type === 'Text' || m.type === 'System'
        ? m.text
        : m.type === 'File'
          ? m.fileName
          : m.type === 'SessionCard'
            ? t('chat.sessionCard')
            : t('chat.voice')
    return `${m.senderIsMe ? t('messages.you') : ''}${body ?? ''}`
  }

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('messages.title')}</h1>
      {data.length === 0 ? (
        <div className="card text-sm text-slate-500">{t('messages.empty')}</div>
      ) : (
        <ul className="space-y-3">
          {data.map((c) => (
            <li key={c.exchange.id}>
              <Link to={`/app/exchanges/${c.exchange.id}`} className="card flex items-center gap-3 transition hover:bg-white">
                <Avatar name={c.exchange.partner.name} className="h-12 w-12" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center justify-between gap-2">
                    <span className="truncate font-bold text-slate-900">{c.exchange.partner.name}</span>
                    {c.last && <span className="shrink-0 text-xs text-slate-400">{formatDateTime(c.last.createdAt, lang)}</span>}
                  </div>
                  <div className="truncate text-sm text-slate-500">{preview(c)}</div>
                  <div className="text-xs text-slate-400">
                    {c.exchange.mySkill} ↔ {c.exchange.partnerSkill}
                  </div>
                </div>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
