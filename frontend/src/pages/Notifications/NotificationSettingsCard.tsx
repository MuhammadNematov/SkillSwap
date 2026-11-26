import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { errorMessage } from '../../api/errors'
import Icon from '../../components/Icon'
import { getNotificationSettings, updateNotificationSettings, type NotificationSettings, type NotificationSettingUpdate } from '../../api/telegram'

function Toggle({ checked, disabled, label, onChange }: { checked: boolean; disabled?: boolean; label: string; onChange: (v: boolean) => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-600 disabled:cursor-not-allowed disabled:opacity-40 ${
        checked ? 'bg-brand-600' : 'bg-ink-faint/50'
      }`}
    >
      <span className={`inline-block h-5 w-5 rounded-full bg-white shadow transition ${checked ? 'translate-x-[22px]' : 'translate-x-0.5'}`} />
    </button>
  )
}

// «Какие уведомления присылать»: переключатели по группам для каналов «Почта» и «Telegram». Telegram виден, если бот настроен,
// и доступен, когда аккаунт подключён. Изменение сохраняется сразу.
export default function NotificationSettingsCard() {
  const { t } = useTranslation()
  const qc = useQueryClient()
  const [params] = useSearchParams()
  // Свёрнуто по умолчанию, чтобы не отодвигать сами уведомления; ссылка «Что присылать» из настроек открывает сразу (?settings=1)
  const [open, setOpen] = useState(params.get('settings') === '1')
  const key = ['notificationSettings'] as const
  const q = useQuery({ queryKey: key, queryFn: getNotificationSettings })
  const save = useMutation({
    mutationFn: (u: NotificationSettingUpdate) => updateNotificationSettings([u]),
    // Показываем новое положение сразу; при ошибке возвращаем прежнее
    onMutate: async (u) => {
      await qc.cancelQueries({ queryKey: key })
      const before = qc.getQueryData<NotificationSettings>(key)
      if (before) {
        qc.setQueryData<NotificationSettings>(key, { ...before, items: before.items.map((i) => (i.category === u.category ? { ...i, ...u } : i)) })
      }
      return { before }
    },
    onError: (_e, _u, context) => context?.before && qc.setQueryData(key, context.before),
    onSuccess: (data) => qc.setQueryData(key, data),
  })

  if (!q.data) return q.error ? <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, q.error)}</p> : null
  const { telegramEnabled, telegramConnected, items } = q.data
  const columns = telegramEnabled ? 'grid-cols-[minmax(0,1fr)_3.5rem_4.5rem]' : 'grid-cols-[minmax(0,1fr)_3.5rem]'

  return (
    <section className="card space-y-3" data-testid="notification-settings">
      <h2>
        <button type="button" className="flex w-full items-center justify-between gap-3 text-left" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
          <span className="text-lg font-bold text-slate-900">{t('telegram.settingsTitle')}</span>
          <Icon name="chevronDown" className={`h-5 w-5 shrink-0 text-brand-600 transition ${open ? 'rotate-180' : ''}`} />
        </button>
      </h2>
      {open && (
        <>
        {telegramEnabled && !telegramConnected && (
          <p className="mt-1 text-sm text-slate-500">
            {t('telegram.connectHint')}{' '}
            <Link to="/app/settings" className="font-semibold text-brand-600 hover:underline">
              {t('telegram.toSettings')}
            </Link>
          </p>
        )}

      {save.error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, save.error)}</p>}

      <div role="table" aria-label={t('telegram.settingsTitle')}>
        <div role="row" className={`grid ${columns} items-end gap-2 border-b border-ink-line pb-2 text-xs font-semibold uppercase tracking-wide text-slate-400`}>
          <span role="columnheader">{t('telegram.kind')}</span>
          <span role="columnheader" className="text-center">{t('telegram.email')}</span>
          {telegramEnabled && (
            <span role="columnheader" className="text-center">Telegram</span>
          )}
        </div>
        {items.map((i) => {
          const name = t(`telegram.category.${i.category}`)
          return (
            <div role="row" key={i.category} className={`grid ${columns} items-center gap-2 border-b border-ink-line py-3 last:border-0`}>
              <span role="cell" className="text-sm text-slate-800">{name}</span>
              <span role="cell" className="flex justify-center">
                {i.email === null ? (
                  <span className="text-slate-400" title={t('telegram.noEmail')} aria-label={t('telegram.noEmail')}>—</span>
                ) : (
                  <Toggle checked={i.email} label={`${name}: ${t('telegram.email')}`} onChange={(email) => save.mutate({ category: i.category, email })} />
                )}
              </span>
              {telegramEnabled && (
                <span role="cell" className="flex justify-center">
                  <Toggle
                    checked={i.telegram}
                    disabled={!telegramConnected}
                    label={`${name}: Telegram`}
                    onChange={(telegram) => save.mutate({ category: i.category, telegram })}
                  />
                </span>
              )}
            </div>
          )
        })}
      </div>
        </>
      )}
    </section>
  )
}
