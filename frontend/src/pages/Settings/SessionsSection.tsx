import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../../api/errors'
import { getAuthSessions, revokeAllAuthSessions, revokeAuthSession } from '../../api/twoFactor'
import type { AuthSessionInfo } from '../../api/types'
import Icon from '../../components/Icon'
import Spinner from '../../components/Spinner'
import { deviceIcon, isLocalIp, sessionMoment } from '../../utils/sessions'

const errorBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'

function SessionCard({ s, busy, onRevoke }: { s: AuthSessionInfo; busy: boolean; onRevoke: () => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const moment = sessionMoment(s.lastActiveAt, lang)
  // «этот компьютер» вместо ::1 и 127.0.0.1; без адреса пишем только время
  const ip = s.ipAddress ? (isLocalIp(s.ipAddress) ? t('settings.thisComputer') : s.ipAddress) : null
  const active = t('settings.sessionActive', moment)

  return (
    <li className="flex items-center gap-3 rounded-xl bg-brand-50 px-3 py-3">
      <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-surface text-brand-600">
        <Icon name={deviceIcon(s.deviceType)} className="h-5 w-5" />
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="text-[15px] font-bold leading-tight text-ink sm:text-base">{s.deviceName ?? t('settings.unknownDevice')}</span>
          {s.isCurrent && <span className="rounded-full bg-brand-100/80 px-2 py-0.5 text-xs font-medium text-brand-700">{t('settings.thisDevice')}</span>}
        </div>
        <p className="mt-0.5 break-words text-xs text-slate-400">{ip ? `${ip} · ${active}` : active}</p>
      </div>
      {!s.isCurrent && (
        <button
          className="shrink-0 rounded-lg px-2 py-1.5 text-sm text-slate-500 transition hover:bg-red-50 hover:text-red-600 disabled:opacity-50"
          disabled={busy}
          onClick={onRevoke}
        >
          {t('settings.revoke')}
        </button>
      )}
    </li>
  )
}

// ───────── Сеансы входа: устройства без User-Agent, места и провайдера (разбор на бэкенде: GET /auth/sessions) ─────────
export default function SessionsSection() {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const { data = [], isLoading, isError, error: loadError } = useQuery({ queryKey: ['authSessions'], queryFn: getAuthSessions })
  const refresh = () => qc.invalidateQueries({ queryKey: ['authSessions'] })
  const revoke = useMutation({ mutationFn: revokeAuthSession, onSuccess: refresh })
  const revokeOthers = useMutation({ mutationFn: revokeAllAuthSessions, onSuccess: refresh })
  const error = revoke.error ?? revokeOthers.error

  // Текущее устройство сверху, остальные как отдал сервер (новые сверху)
  const sessions = [...data].sort((a, b) => Number(b.isCurrent) - Number(a.isCurrent))
  const hasOthers = sessions.some((s) => !s.isCurrent)

  return (
    <section className="card space-y-3">
      <h2 className="font-bold text-slate-900">{t('settings.sessions')}</h2>
      {error && <p className={errorBox}>{errorMessage(t, error)}</p>}
      {isLoading ? (
        <Spinner />
      ) : isError ? (
        <p className={errorBox}>{errorMessage(t, loadError)}</p>
      ) : (
        <>
          <ul className="space-y-2">
            {sessions.map((s) => (
              <SessionCard key={s.id} s={s} busy={revoke.isPending || revokeOthers.isPending} onRevoke={() => revoke.mutate(s.id)} />
            ))}
          </ul>
          {hasOthers && (
            <div className="flex justify-end">
              <button
                className="rounded-lg px-2 py-1.5 text-sm text-slate-500 transition hover:bg-red-50 hover:text-red-600 disabled:opacity-50"
                disabled={revokeOthers.isPending || revoke.isPending}
                onClick={() => revokeOthers.mutate()}
              >
                {t('settings.revokeOthers')}
              </button>
            </div>
          )}
        </>
      )}
    </section>
  )
}
