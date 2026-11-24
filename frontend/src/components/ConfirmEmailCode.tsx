import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { confirmEmailCode, resendConfirmation } from '../api/auth'
import { errorMessage } from '../api/errors'

interface Props {
  email: string
  onConfirmed: () => void
}

// Ввод шестизначного кода из письма (ТЗ FR-1: email подтверждается кодом, ссылки в письме нет).
// Код действует 15 минут, на него 5 попыток; «отправить ещё раз» присылает новый код, прежний перестаёт работать.
export default function ConfirmEmailCode({ email, onConfirmed }: Props) {
  const { t } = useTranslation()
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [resent, setResent] = useState(false)

  const digits = code.replace(/\D/g, '')

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    if (digits.length !== 6) {
      setError(t('confirmEmail.codeLength'))
      return
    }
    setError(null)
    setBusy(true)
    try {
      await confirmEmailCode({ email, code: digits })
      onConfirmed()
    } catch (err) {
      setError(errorMessage(t, err))
    } finally {
      setBusy(false)
    }
  }

  async function onResend() {
    setError(null)
    try {
      await resendConfirmation(email)
      setResent(true)
      setCode('')
    } catch (err) {
      setError(errorMessage(t, err))
    }
  }

  return (
    <form onSubmit={onSubmit} className="space-y-4" noValidate>
      <label className="block">
        <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('confirmEmail.code')}</span>
        <input
          className="input text-center text-2xl font-bold tracking-[0.5em]"
          inputMode="numeric"
          autoComplete="one-time-code"
          autoFocus
          maxLength={7}
          placeholder="••••••"
          value={code}
          onChange={(e) => setCode(e.target.value.replace(/[^\d\s-]/g, ''))}
          aria-invalid={!!error}
        />
        <span className="mt-1.5 block text-xs text-ink-soft">{t('confirmEmail.codeHint')}</span>
      </label>

      {error && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>}

      <button className="btn-primary h-12 w-full rounded-2xl text-[15px]" disabled={busy || digits.length !== 6}>
        {busy ? t('common.loading') : t('confirmEmail.submit')}
      </button>

      {resent ? (
        <p className="text-sm text-emerald-700">{t('confirmEmail.resent')}</p>
      ) : (
        <button type="button" className="text-sm font-medium text-brand-600 hover:underline" onClick={onResend}>
          {t('confirmEmail.resend')}
        </button>
      )}
    </form>
  )
}
