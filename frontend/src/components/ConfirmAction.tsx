import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import type { Confirmation } from '../api/staff'
import Modal from './Modal'

interface Props {
  title: string
  submitLabel: string
  children?: ReactNode // дополнительные поля формы (например, причина)
  extraValid?: boolean
  danger?: boolean
  onSubmit: (confirmation: Confirmation) => Promise<unknown>
  onClose: () => void
}

// Подтверждение действия сотрудника: пароль + код 2FA (или резервный). Код одноразовый, окна «подтверждено недавно» нет.
export default function ConfirmAction({ title, submitLabel, children, extraValid = true, danger, onSubmit, onClose }: Props) {
  const { t } = useTranslation()
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [recovery, setRecovery] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const valid = extraValid && password.length > 0 && (recovery ? code.trim().length > 0 : /^\d{6}$/.test(code.trim()))

  async function submit() {
    if (!valid || busy) return
    setBusy(true)
    setError(null)
    try {
      await onSubmit({ password, code: code.trim(), isRecoveryCode: recovery })
      onClose()
    } catch (e) {
      setError(errorMessage(t, e))
      setCode('') // использованный код сгорает, нужен новый
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={title} onClose={onClose}>
      <form
        className="space-y-3"
        onSubmit={(e) => {
          e.preventDefault()
          submit()
        }}
      >
        {children}
        <div className="rounded-xl bg-brand-50 p-3 text-xs text-slate-600">{t('confirm.hint')}</div>
        <label className="block">
          <span className="mb-1 block text-sm font-medium text-slate-700">{t('confirm.password')}</span>
          <input className="input" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        <label className="block">
          <span className="mb-1 block text-sm font-medium text-slate-700">{recovery ? t('confirm.recoveryCode') : t('confirm.code')}</span>
          <input
            className="input tracking-widest"
            autoComplete="one-time-code"
            inputMode={recovery ? 'text' : 'numeric'}
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
        </label>
        <label className="flex items-center gap-2 text-sm text-slate-600">
          <input
            type="checkbox"
            checked={recovery}
            onChange={(e) => {
              setRecovery(e.target.checked)
              setCode('')
            }}
          />
          {t('confirm.useRecovery')}
        </label>
        {error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}
        <div className="flex justify-end gap-3">
          <button type="button" className="btn-secondary" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button className={danger ? 'btn-primary bg-red-600 hover:bg-red-700' : 'btn-primary'} disabled={!valid || busy}>
            {busy ? t('common.loading') : submitLabel}
          </button>
        </div>
      </form>
    </Modal>
  )
}
