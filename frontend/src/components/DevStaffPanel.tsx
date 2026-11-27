import { useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { toSession } from '../api/auth'
import { DEV_USER_EMAILS, getStaffCode, loginDevUser, resetDevLesson, seedCase, seedDevUsers, seedStaff, type SeedCaseStage, type SeedCaseType } from '../api/dev'
import { errorMessage, toApiError } from '../api/errors'
import type { AuthSession } from '../api/types'
import { formatTime } from '../utils/dates'

const TYPES: SeedCaseType[] = ['Conduct', 'NoShowAbuse', 'ReviewDispute']
const STAGES: SeedCaseStage[] = ['Open', 'Taken', 'Waiting', 'Resolved', 'Appealed', 'AppealReview']

// Только для разработки (Development-бэкенд, VITE_USE_MOCKS=false): вход без пароля и кода под постоянными dev-пользователями и сотрудниками,
// тестовые обращения на нужной стадии. В продакшен-сборку панель не попадает (import.meta.env.DEV, проверяет DevStaffPanel.prod.test.ts),
// а маршруты /dev/* бэкенд отдаёт только в Development и только запросам с этого компьютера (иначе 404).
export default function DevStaffPanel({ onSession }: { onSession: (s: AuthSession) => void }) {
  const { t, i18n } = useTranslation()
  const lang = i18n.resolvedLanguage ?? 'ru'
  const [type, setType] = useState<SeedCaseType>('Conduct')
  const [stage, setStage] = useState<SeedCaseStage>('Open')

  const staff = useMutation({ mutationFn: seedStaff })
  const signIn = useMutation({ mutationFn: (token: string) => toSession({ accessToken: token, user: { id: '', email: '', name: '', role: '', uiLanguage: 'ru' } }), onSuccess: onSession })
  const code = useMutation({ mutationFn: () => getStaffCode('dev-admin@example.com') })
  const made = useMutation({ mutationFn: () => seedCase({ type, stage }) })
  const devUsers = useMutation({ mutationFn: seedDevUsers })
  const userSignIn = useMutation({
    mutationFn: async (email: string) => toSession({ ...(await loginDevUser(email)), user: { id: '', email: '', name: '', role: '', uiLanguage: 'ru' } }),
    onSuccess: onSession,
  })
  const reset = useMutation({ mutationFn: resetDevLesson })

  // Без тела 404 приходит от бэкенда, когда страница открыта не с этого компьютера (телефон, адрес в сети): объясняем, а не «неизвестная ошибка»
  const explain = (e: unknown) => {
    const err = toApiError(e)
    return err.status === 404 && !err.code.startsWith('dev_') ? t('dev.notLocal') : errorMessage(t, e)
  }
  const usersError = devUsers.error ?? userSignIn.error ?? reset.error
  const lessonAt = devUsers.data?.lesson?.startAt

  return (
    <div className="mt-6 space-y-4 rounded-2xl border border-dashed border-ink-line p-4 text-left" data-testid="dev-panel">
      <div>
        <h2 className="font-bold text-ink">{t('dev.panelTitle')}</h2>
        <p className="mt-1 text-xs text-ink-soft">{t('dev.panelHint')}</p>
      </div>

      <section className="space-y-2.5" aria-labelledby="dev-users-title">
        <h3 id="dev-users-title" className="text-sm font-bold text-ink">{t('dev.usersTitle')}</h3>
        <p className="text-xs text-ink-soft">{t('dev.usersHint')}</p>
        <div className="flex flex-wrap gap-2">
          <button type="button" className="btn-secondary" disabled={devUsers.isPending} onClick={() => devUsers.mutate()}>
            {devUsers.isPending ? t('common.loading') : t('dev.usersCreate')}
          </button>
          <button type="button" className="btn-secondary" disabled={reset.isPending} onClick={() => reset.mutate()} title={t('dev.resetHint')}>
            {reset.isPending ? t('common.loading') : t('dev.resetLesson')}
          </button>
        </div>
        <div className="flex flex-wrap gap-2">
          {DEV_USER_EMAILS.map((email) => (
            <button key={email} type="button" className="btn-primary px-3 py-1.5" disabled={userSignIn.isPending} onClick={() => userSignIn.mutate(email)}>
              {t('dev.staffLogin', { name: email.split('@')[0] })}
            </button>
          ))}
        </div>
        {usersError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{explain(usersError)}</p>}
        {devUsers.data && (
          <p className="text-xs text-ink-soft">
            {t('dev.usersDone', { password: devUsers.data.users[0]?.password })}
            {devUsers.data.lessonNote ? ` ${devUsers.data.lessonNote}` : ''}
            {devUsers.data.avatarNote ? ` ${devUsers.data.avatarNote}` : ''}
          </p>
        )}
        {reset.data ? (
          <p className="text-xs font-semibold text-emerald-700">{t('dev.resetDone', { time: formatTime(reset.data.lesson.startAt, lang) })}</p>
        ) : (
          lessonAt && <p className="text-xs font-semibold text-emerald-700">{t('dev.lessonAt', { time: formatTime(lessonAt, lang) })}</p>
        )}
      </section>

      <section className="space-y-2.5 border-t border-ink-line pt-3" aria-labelledby="dev-staff-title">
      <h3 id="dev-staff-title" className="text-sm font-bold text-ink">{t('dev.staffGroup')}</h3>
      <p className="text-xs text-ink-soft">{t('dev.staffHint')}</p>

      <button type="button" className="btn-secondary" disabled={staff.isPending} onClick={() => staff.mutate()}>
        {staff.isPending ? t('common.loading') : t('dev.staffCreate')}
      </button>
      {(staff.error || signIn.error) && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{explain(staff.error ?? signIn.error)}</p>}
      {staff.data && (
        <div className="flex flex-wrap gap-2">
          {staff.data.map((a) => (
            <button key={a.accessToken} type="button" className="btn-primary px-3 py-1.5" disabled={signIn.isPending} onClick={() => signIn.mutate(a.accessToken)}>
              {t('dev.staffLogin', { name: a.label.split('@')[0] })}
            </button>
          ))}
        </div>
      )}

      <div className="space-y-2 border-t border-ink-line pt-3">
        <p className="text-xs text-ink-soft">{t('dev.staffPassword')}</p>
        <a className="inline-block text-sm font-semibold text-brand-600 hover:underline" href="/dev/staff-qr" target="_blank" rel="noreferrer">
          {t('dev.staffQr')}
        </a>
        <button type="button" className="btn-secondary" disabled={code.isPending} onClick={() => code.mutate()}>
          {code.isPending ? t('common.loading') : t('dev.staffCode')}
        </button>
        {code.error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{explain(code.error)}</p>}
        {code.data && (
          <p className="font-mono text-sm text-ink">
            {code.data.codes.join(' / ')} <span className="text-xs text-ink-soft">({t('dev.staffCodeLeft', { s: code.data.secondsLeft })})</span>
          </p>
        )}
      </div>

      </section>

      <div className="space-y-2 border-t border-ink-line pt-3">
        <h3 className="text-sm font-bold text-ink">{t('dev.caseTitle')}</h3>
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-ink-soft">{t('dev.caseType')}</span>
          {TYPES.map((k) => (
            <button key={k} type="button" className={`chip ${type === k ? 'chip-on' : ''}`} onClick={() => setType(k)}>
              {t(`cases.type.${k}`)}
            </button>
          ))}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-ink-soft">{t('dev.caseStage')}</span>
          {STAGES.map((k) => (
            <button key={k} type="button" className={`chip ${stage === k ? 'chip-on' : ''}`} onClick={() => setStage(k)}>
              {t(`dev.stage.${k}`)}
            </button>
          ))}
        </div>
        <button type="button" className="btn-secondary" disabled={made.isPending} onClick={() => made.mutate()}>
          {made.isPending ? t('common.loading') : t('dev.caseCreate')}
        </button>
        {made.error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{explain(made.error)}</p>}
        {made.data && (
          <div className="space-y-1 text-sm">
            <p className="font-semibold text-emerald-700">{t('dev.caseDone')}</p>
            <ul className="space-y-1">
              {made.data.users.map((u) => (
                <li key={u.email} className="break-all rounded-lg border border-ink-line bg-brand-50 px-2 py-1 font-mono text-xs text-ink-soft">
                  {u.name ? `${u.name}: ` : ''}
                  {u.email} / {u.password}
                </li>
              ))}
            </ul>
            <p className="text-xs text-ink-soft">{t('dev.dueShort')}</p>
          </div>
        )}
      </div>
    </div>
  )
}
