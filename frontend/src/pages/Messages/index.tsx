import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, NavLink, useParams } from 'react-router-dom'
import { errorMessage } from '../../api/errors'
import { getConversations, getExchange } from '../../api/exchanges'
import Avatar from '../../components/Avatar'
import Chat from '../../components/Chat'
import Icon from '../../components/Icon'
import Spinner from '../../components/Spinner'
import { formatTime } from '../../utils/dates'

// Сообщения в стиле мессенджера: слева список чатов, справа выбранный чат.
// По ТЗ чат есть только внутри обмена, личных сообщений нет. На телефоне показывается что-то одно.
export default function Messages() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const { id } = useParams()
  const { data = [], isLoading } = useQuery({ queryKey: ['conversations'], queryFn: getConversations })

  function preview(c: (typeof data)[number]) {
    const m = c.last
    if (!m) return t('messages.noMessages')
    const body = m.isDeleted
      ? t('chat.deleted')
      : m.type === 'Text' || m.type === 'System'
        ? (m.preview ?? '')
        : m.type === 'File'
          ? t('chat.file')
          : m.type === 'SessionCard'
            ? t('chat.sessionCard')
            : t('chat.voice')
    return `${m.senderIsMe ? t('messages.you') : ''}${body}`
  }

  // Дата в списке: сегодня — время, иначе день и месяц
  function stamp(iso: string) {
    const d = new Date(iso)
    return d.toDateString() === new Date().toDateString() ? formatTime(iso, lang) : d.toLocaleDateString(lang, { day: 'numeric', month: 'short' })
  }

  if (isLoading) return <Spinner />

  return (
    <div className="flex h-[calc(100vh-7.5rem)] min-h-[480px] overflow-hidden rounded-2xl border border-ink-line bg-surface sm:h-[calc(100vh-10rem)]">
      <aside className={`${id ? 'hidden md:flex' : 'flex'} w-full shrink-0 flex-col border-r border-ink-line md:w-80 lg:w-96`}>
        <h1 className="px-4 py-3 text-lg font-bold text-slate-900">{t('messages.title')}</h1>
        {data.length === 0 ? (
          <p className="px-4 text-sm text-slate-500">{t('messages.empty')}</p>
        ) : (
          <ul className="min-h-0 flex-1 overflow-y-auto">
            {data.map((c) => (
              <li key={c.exchange.id}>
                <NavLink
                  to={`/app/messages/${c.exchange.id}`}
                  className={({ isActive }) => `flex items-center gap-3 px-3 py-2.5 transition ${isActive ? 'bg-brand-600 text-white' : 'hover:bg-brand-50'}`}
                >
                  {({ isActive }) => (
                    <>
                      <Avatar name={c.exchange.partner.name} src={c.exchange.partner.avatarUrl} className="h-12 w-12 shrink-0" />
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center justify-between gap-2">
                          <span className={`truncate font-semibold ${isActive ? 'text-white' : 'text-slate-900'}`}>{c.exchange.partner.name}</span>
                          {c.last && <span className={`shrink-0 text-xs ${isActive ? 'text-white/80' : 'text-slate-400'}`}>{stamp(c.last.createdAt)}</span>}
                        </div>
                        <div className="flex items-center gap-2">
                          <div className={`min-w-0 flex-1 truncate text-sm ${isActive ? 'text-white/90' : 'text-slate-500'}`}>{preview(c)}</div>
                          {!!c.exchange.unreadMessages && (
                            <span className={`min-w-5 shrink-0 rounded-full px-1.5 text-center text-xs font-semibold leading-5 ${isActive ? 'bg-white text-brand-700' : 'bg-brand-600 text-white'}`}>
                              {c.exchange.unreadMessages}
                            </span>
                          )}
                        </div>
                        <div className={`truncate text-xs ${isActive ? 'text-white/70' : 'text-slate-400'}`}>
                          {c.exchange.mySkill} ↔ {c.exchange.partnerSkill}
                        </div>
                      </div>
                    </>
                  )}
                </NavLink>
              </li>
            ))}
          </ul>
        )}
      </aside>

      <section className={`${id ? 'flex' : 'hidden md:flex'} min-w-0 flex-1 flex-col`}>
        {id ? (
          <ChatPane key={id} id={id} />
        ) : (
          <div className="m-auto text-center text-sm text-slate-400">
            <Icon name="message" className="mx-auto mb-2 h-10 w-10" />
            {t('messages.pick')}
          </div>
        )}
      </section>
    </div>
  )
}

function ChatPane({ id }: { id: string }) {
  const { t } = useTranslation()
  const { data: exchange, isLoading, error } = useQuery({ queryKey: ['exchange', id], queryFn: () => getExchange(id) })
  if (isLoading) return <Spinner />
  if (error || !exchange) return <div className="m-4 text-sm text-red-700">{errorMessage(t, error)}</div>
  return (
    <>
      <header className="flex items-center gap-3 border-b border-ink-line px-3 py-2.5">
        <Link to="/app/messages" className="rounded-lg p-1.5 text-slate-500 hover:bg-brand-50 md:hidden" aria-label={t('exchange.back')}>
          <Icon name="arrowRight" className="h-5 w-5 rotate-180" />
        </Link>
        <Avatar name={exchange.partner.name} src={exchange.partner.avatarUrl} className="h-10 w-10" />
        <div className="min-w-0 flex-1">
          <div className="truncate font-bold text-slate-900">{exchange.partner.name}</div>
          <div className="truncate text-xs text-slate-400">
            {exchange.mySkill} ↔ {exchange.partnerSkill}
          </div>
        </div>
        <Link to={`/app/exchanges/${exchange.id}`} className="shrink-0 text-sm font-medium text-brand-700 hover:underline">
          {t('messages.openExchange')}
        </Link>
      </header>
      <div className="min-h-0 flex-1 p-2 sm:p-3">
        <Chat exchange={exchange} fill />
      </div>
    </>
  )
}
