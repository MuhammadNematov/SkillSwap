import { useMutation } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { seedLesson } from '../api/dev'
import { errorMessage } from '../api/errors'
import { formatDateTime } from '../utils/dates'

// Панель разработчика: тестовый урок для проверки видеозвонка без ожидания. Показывается только в dev-сборке с настоящим бэкендом.
export default function DevSeedLesson() {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const seed = useMutation({ mutationFn: seedLesson })

  return (
    <section className="card space-y-3 border-dashed">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="font-bold text-ink">{t('dev.title')}</h2>
          <p className="text-sm text-ink-soft">{t('dev.hint')}</p>
        </div>
        <button className="btn-secondary" disabled={seed.isPending} onClick={() => seed.mutate()}>
          {seed.isPending ? t('common.loading') : t('dev.create')}
        </button>
      </div>
      {seed.error && <p className="rounded-xl bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, seed.error)}</p>}
      {seed.data && (
        <div className="space-y-2 text-sm">
          <p className="text-ink-soft">
            {t('dev.startsAt', { time: formatDateTime(seed.data.startAt, lang) })} · {t('dev.opensAt', { time: formatDateTime(seed.data.joinOpensAt, lang) })}
          </p>
          <ul className="space-y-1.5">
            {seed.data.users.map((u) => (
              <li key={u.userId} className="rounded-xl border border-ink-line bg-brand-50 px-3 py-2">
                <div className="font-semibold text-ink">{u.name}</div>
                <div className="break-all font-mono text-xs text-ink-soft">
                  {u.email} / {u.password}
                </div>
              </li>
            ))}
          </ul>
          <p className="text-xs text-ink-soft">{t('dev.how')}</p>
        </div>
      )}
    </section>
  )
}
