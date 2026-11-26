import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { markAllRead, markRead } from '../../api/notifications'
import type { AppNotification } from '../../api/types'
import { SkeletonList } from '../../components/staff/Skeleton'
import { useNotifications } from '../../hooks/useNotifications'
import { getMyId } from '../../api/identity'
import { useInvalidateAll } from '../../hooks/useSessionActions'
import { formatDateTime } from '../../utils/dates'
import NotificationSettingsCard from './NotificationSettingsCard'

// Куда ведёт уведомление
export type NotificationsArea = 'app' | 'moderation' | 'admin'

function targetOf(n: AppNotification, area: NotificationsArea): string | null {
  const ex = n.payload.exchangeId
  const caseId = n.payload.caseId
  // у служебных ролей свои страницы: обычные маршруты /app им закрыты
  if (area === 'moderation') {
    if (n.type === 'CaseReturnedToWork' || n.type === 'AppealReviewAssigned') return caseId ? `/moderation/cases/${caseId}` : '/moderation'
    return n.type === 'BanRequestDecided' ? '/moderation' : null
  }
  if (area === 'admin') {
    if (n.type === 'CaseAppealed' || n.type === 'AppealConclusionReady' || n.type === 'CaseOverdue') return caseId ? `/admin/cases/${caseId}` : '/admin'
    return null
  }
  switch (n.type) {
    case 'RequestReceived':
      return '/app/exchanges?tab=incoming'
    case 'RequestDeclined':
    case 'RequestExpired':
      return '/app/exchanges?tab=outgoing'
    case 'ReviewAvailable':
    case 'ReviewReminder':
    case 'ReviewPublished':
      return '/app/reviews'
    case 'CaseTaken':
    case 'CaseResolved':
    case 'CaseExplanationRequested':
    case 'CaseExplanationReminder':
    case 'CaseAppealed':
    case 'CaseAppealReviewAssigned':
    case 'AppealDecided':
      return n.payload.caseId ? `/app/cases/${n.payload.caseId}` : '/app/cases'
    case 'WarningIssued':
    case 'WarningRevoked':
      return '/app/cases'
    default:
      return ex ? `/app/exchanges/${ex}` : null
  }
}

// Один тип с разным смыслом: выход партнёра по своей причине или потому что он стал недоступен
function typeKey(n: AppNotification): string {
  // Неявка: в payload.userId лежит тот, кто не пришёл, это может быть и сам пользователь
  if (n.type === 'SessionNoShow') return n.payload.userId === getMyId() ? 'SessionNoShowMe' : 'SessionNoShow'
  return n.type === 'ExchangeAborted' && n.payload.reason === 'partner_unavailable' ? 'ExchangeAbortedUnavailable' : n.type
}

export default function Notifications({ area = 'app' }: { area?: NotificationsArea }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const navigate = useNavigate()
  const qc = useQueryClient()
  const invalidate = useInvalidateAll()
  const { data = [], isLoading } = useNotifications()

  const refresh = () => qc.invalidateQueries({ queryKey: ['notifications'] })
  const read = useMutation({ mutationFn: markRead, onSuccess: refresh })
  const readAll = useMutation({ mutationFn: markAllRead, onSuccess: refresh })

  const params = (n: AppNotification) => {
    const p = n.payload
    const when = (v: unknown) => (v ? formatDateTime(String(v), lang) : '')
    return {
      ...p,
      time: when(p.startAt),
      deadlineAt: when(p.deadlineAt),
      dueAt: when(p.dueAt),
      decisionText: p.decision ? t(`cases.decision.${p.decision}`) : '',
      reasonText: p.reason ? t(`notifications.reason.${p.reason}`, { defaultValue: String(p.reason) }) : '',
      result: t(p.approved ? 'notifications.ban.approved' : 'notifications.ban.rejected'),
    }
  }

  if (isLoading) return <SkeletonList rows={3} label={t('common.loading')} />
  const unread = data.filter((n) => !n.isRead).length

  function open(n: AppNotification) {
    if (!n.isRead) read.mutate(n.id)
    const to = targetOf(n, area)
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

      {/* Переключатели каналов есть только у обычных аккаунтов: служебным письма и Telegram не настраиваются */}
      {area === 'app' && <NotificationSettingsCard />}

      {data.length === 0 ? (
        <div className="card text-sm text-slate-500">{t('notifications.empty')}</div>
      ) : (
        <ul className="space-y-2">
          {data.map((n) => (
            <li key={n.id}>
              <button
                onClick={() => open(n)}
                className={`card flex w-full items-start gap-3 text-left transition hover:bg-surface ${n.isRead ? 'opacity-70' : ''}`}
              >
                <span className={`mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full ${n.isRead ? 'bg-transparent' : 'bg-brand-600'}`} />
                <div className="min-w-0 flex-1">
                  <div className="text-sm text-slate-800">
                    {t([`notifications.types.${typeKey(n)}`, 'notifications.types.default'], params(n))}
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
