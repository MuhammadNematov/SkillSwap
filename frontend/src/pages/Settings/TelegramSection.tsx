import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { errorMessage } from '../../api/errors'
import { createTelegramLink, disconnectTelegram, getTelegramStatus, type TelegramLink } from '../../api/telegram'
import Icon from '../../components/Icon'

const errorBox = 'rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700'
const POLL_MS = 3000

// Блок «Telegram» в настройках: «Подключить» открывает t.me/<бот>?start=<код>; пока ждём, статус проверяется каждые 3 секунды и обновляется
// событием telegramStatus хаба уведомлений. Без настроенного на сервере бота блок не показывается.
export default function TelegramSection() {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const [pending, setPending] = useState<TelegramLink | null>(null)
  const status = useQuery({
    queryKey: ['telegram'],
    queryFn: getTelegramStatus,
    refetchInterval: pending ? POLL_MS : false,
  })

  const connected = !!status.data?.connected

  // Подключилось (или ссылка истекла) — перестаём ждать
  useEffect(() => {
    if (connected) {
      setPending(null)
      qc.invalidateQueries({ queryKey: ['notificationSettings'] })
    }
  }, [connected, qc])
  useEffect(() => {
    if (!pending) return
    const timer = setTimeout(() => setPending(null), Math.max(0, new Date(pending.expiresAt).getTime() - Date.now()))
    return () => clearTimeout(timer)
  }, [pending])

  const connect = useMutation({
    mutationFn: async () => {
      // Окно открывается сразу, по клику: после ожидания ответа браузер счёл бы его всплывающим и заблокировал
      const win = window.open('', '_blank')
      try {
        const link = await createTelegramLink()
        if (win) {
          win.opener = null
          win.location.href = link.url
        }
        return link
      } catch (e) {
        win?.close()
        throw e
      }
    },
    onSuccess: setPending,
  })
  const disconnect = useMutation({
    mutationFn: disconnectTelegram,
    onSuccess: (data) => {
      qc.setQueryData(['telegram'], data)
      qc.invalidateQueries({ queryKey: ['notificationSettings'] })
    },
  })

  if (!status.data?.enabled) return null
  const bot = status.data.botUsername

  return (
    <section className="card space-y-3" data-testid="telegram-section">
      <div className="flex items-start gap-3">
        <span className="grid h-10 w-10 shrink-0 place-items-center rounded-xl bg-brand-100 text-brand-600">
          <Icon name="send" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <h2 className="text-lg font-bold text-slate-900">Telegram</h2>
            <span className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${connected ? 'bg-emerald-100/80 text-emerald-700' : 'bg-slate-100/80 text-slate-600'}`} data-testid="telegram-state">
              {connected ? t('telegram.on') : t('telegram.off')}
            </span>
          </div>
          <p className="mt-1 text-sm text-slate-500">{connected ? t('telegram.connected', { bot }) : t('telegram.hint')}</p>
        </div>
      </div>

      {(connect.error || disconnect.error || status.error) && <p className={errorBox}>{errorMessage(t, connect.error ?? disconnect.error ?? status.error)}</p>}

      {pending && !connected && (
        <div className="space-y-2 rounded-xl border border-ink-line bg-brand-50 p-3 text-sm text-slate-700" data-testid="telegram-pending">
          <p>{t('telegram.waiting', { bot })}</p>
          <a className="inline-block font-semibold text-brand-600 hover:underline" href={pending.url} target="_blank" rel="noreferrer">
            {t('telegram.openAgain')}
          </a>
          <p className="text-xs text-slate-400">{t('telegram.expires')}</p>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-3">
        {connected ? (
          <button className="btn-secondary" disabled={disconnect.isPending} onClick={() => disconnect.mutate()}>
            {disconnect.isPending ? t('common.loading') : t('telegram.disconnect')}
          </button>
        ) : (
          <button className="btn-primary" disabled={connect.isPending} onClick={() => connect.mutate()}>
            {connect.isPending ? t('common.loading') : t('telegram.connect')}
          </button>
        )}
        <Link to="/app/notifications?settings=1" className="text-sm font-semibold text-brand-600 hover:underline">
          {t('telegram.choose')}
        </Link>
      </div>
    </section>
  )
}
