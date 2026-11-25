import { useMutation } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { toSession } from '../api/auth'
import { getStaffCode, seedCase, seedStaff, type SeedCaseStage, type SeedCaseType } from '../api/dev'
import { errorMessage } from '../api/errors'
import type { AuthSession } from '../api/types'

const TYPES: SeedCaseType[] = ['Conduct', 'NoShowAbuse', 'ReviewDispute']
const STAGES: SeedCaseStage[] = ['Open', 'Taken', 'Waiting', 'Resolved', 'Appealed', 'AppealReview']

// Только для разработки (Development-бэкенд, VITE_USE_MOCKS=false): служебные аккаунты без пароля и 2FA
// и тестовые обращения на нужной стадии. В продакшене панель не показывается и маршрутов на бэкенде нет.
export default function DevStaffPanel({ onSession }: { onSession: (s: AuthSession) => void }) {
  const { t } = useTranslation()
  const [type, setType] = useState<SeedCaseType>('Conduct')
  const [stage, setStage] = useState<SeedCaseStage>('Open')

  const staff = useMutation({ mutationFn: seedStaff })
  const signIn = useMutation({ mutationFn: (token: string) => toSession({ accessToken: token, user: { id: '', email: '', name: '', role: '', uiLanguage: 'ru' } }), onSuccess: onSession })
  const code = useMutation({ mutationFn: () => getStaffCode('dev-admin@example.com') })
  const made = useMutation({ mutationFn: () => seedCase({ type, stage }) })

  return (
    <div className="mt-6 space-y-4 rounded-2xl border border-dashed border-ink-line p-4 text-left">
      <div>
        <h2 className="font-bold text-ink">{t('dev.staffTitle')}</h2>
        <p className="mt-1 text-xs text-ink-soft">{t('dev.staffHint')}</p>
      </div>

      <button type="button" className="btn-secondary" disabled={staff.isPending} onClick={() => staff.mutate()}>
        {staff.isPending ? t('common.loading') : t('dev.staffCreate')}
      </button>
      {(staff.error || signIn.error) && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, staff.error ?? signIn.error)}</p>}
      {staff.data && (
        <div className="flex flex-wrap gap-2">
          {staff.data.map((a) => (
            <button key={a.accessToken} type="button" className="btn-primary px-3 py-1.5" disabled={signIn.isPending} onClick={() => signIn.mutate(a.accessToken)}>
              {t('dev.staffLogin', { name: a.label })}
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
        {code.error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, code.error)}</p>}
        {code.data && (
          <p className="font-mono text-sm text-ink">
            {code.data.codes.join(' / ')} <span className="text-xs text-ink-soft">({t('dev.staffCodeLeft', { s: code.data.secondsLeft })})</span>
          </p>
        )}
      </div>

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
        {made.error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, made.error)}</p>}
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
