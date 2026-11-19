import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { markAllRead, markRead } from '../../api/notifications'
import type { AppNotification } from '../../api/types'
import Spinner from '../../components/Spinner'
import { useNotifications } from '../../hooks/useNotifications'
import { useInvalidateAll } from '../../hooks/useSessionActions'
import { formatDateTime } from '../../utils/dates'

// Куда ведёт уведомление
function targetOf(n: AppNotification): string | null {
  const ex = n.payload.exchangeId
  switch (n.type) {
    case 'RequestReceived':
      return '/app/exchanges?tab=incoming'
    case 'RequestDeclined':
    case 'RequestExpired':
      return '/app/exchanges?tab=outgoing'
    case 'ReviewAvailable':
    case 'ReviewPublished':
      return '/app/reviews'
    default:
      return ex ? `/app/exchanges/${ex}` : null
  }
}

// Один тип с разным смыслом: выход партнёра по своей причине или потому что он стал недоступен
function typeKey(n: AppNotification): string {
  return n.type === 'ExchangeAborted' && n.payload.reason === 'partner_unavailable' ? 'ExchangeAbortedUnavailable' : n.type
}

export default function Notifications() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const navigate = useNavigate()
  const qc = useQueryClient()
  const invalidate = useInvalidateAll()
  const { data = [], isLoading } = useNotifications()

  const refresh = () => qc.invalidateQueries({ queryKey: ['notifications'] })
  const read = useMutation({ mutationFn: markRead, onSuccess: refresh })
  const readAll = useMutation({ mutationFn: markAllRead, onSuccess: refresh })

  if (isLoading) return <Spinner />
  const unread = data.filter((n) => !n.isRead).length

  function open(n: AppNotification) {
    if (!n.isRead) read.mutate(n.id)
    const to = targetOf(n)
    if (to) {
      invalidate()
      navigate(to)
    }
  }

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-2xl font-bold text-slate-900 sm:text-3xl">{t('notifications.title')}</h1>
        {unread > 0 && (
          <button className="btn-secondary px-4 py-2" disabled={readAll.isPending} onClick={() => readAll.mutate()}>
            {t('notifications.readAll')}
          </button>
        )}
      </div>

      {data.length === 0 ? (
        <div className="card text-sm text-slate-500">{t('notifications.empty')}</div>
      ) : (
        <ul className="space-y-2">
          {data.map((n) => (
            <li key={n.id}>
              <button
                onClick={() => open(n)}
                className={`card flex w-full items-start gap-3 text-left transition hover:bg-white/75 ${n.isRead ? 'opacity-70' : ''}`}
              >
                <span className={`mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full ${n.isRead ? 'bg-transparent' : 'bg-brand-600'}`} />
                <div className="min-w-0 flex-1">
                  <div className="text-sm text-slate-800">
                    {t([`notifications.types.${typeKey(n)}`, 'notifications.types.default'], {
                      ...n.payload,
                      time: n.payload.startAt ? formatDateTime(String(n.payload.startAt), lang) : '',
                    })}
                  </div>
                  <div className="mt-0.5 text-xs text-slate-400">{formatDateTime(n.createdAt, lang)}</div>
                </div>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
