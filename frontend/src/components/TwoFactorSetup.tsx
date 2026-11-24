import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { errorMessage } from '../api/errors'
import { setupTwoFactor, verifyTwoFactor, type VerifyResult } from '../api/twoFactor'
import type { AuthSession } from '../api/types'
import CodeInput from './CodeInput'
import Spinner from './Spinner'

interface Props {
  twoFactorToken?: string // есть при обязательной настройке во время входа
  onDone: (session: AuthSession | null) => void
  onCancel?: () => void
}

// Включение 2FA: ключ для приложения-аутентификатора → код из приложения → резервные коды (показываются один раз)
export default function TwoFactorSetup({ twoFactorToken, onDone, onCancel }: Props) {
  const { t } = useTranslation()
  const [code, setCode] = useState('')
  const [result, setResult] = useState<VerifyResult | null>(null)
  const [saved, setSaved] = useState(false)
  const [copied, setCopied] = useState(false)

  const setup = useQuery({
    queryKey: ['2faSetup', twoFactorToken],
    queryFn: () => setupTwoFactor(twoFactorToken),
    staleTime: Infinity, // ключ не перезапрашиваем: каждый запрос выдаёт новый
    gcTime: 0,
    retry: false,
  })
  const verify = useMutation({ mutationFn: verifyTwoFactor, onSuccess: setResult })

  function copy(text: string) {
    navigator.clipboard?.writeText(text).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    })
  }

  // Шаг 2: резервные коды
  if (result) {
    const text = result.recoveryCodes.join('\n')
    return (
      <div className="space-y-4">
        <p className="text-sm text-slate-600">{t('twoFactor.recoveryInfo')}</p>
        <ul className="grid grid-cols-2 gap-2 rounded-xl bg-brand-50 p-4 font-mono text-sm text-slate-800">
          {result.recoveryCodes.map((c) => (
            <li key={c}>{c}</li>
          ))}
        </ul>
        <button type="button" className="btn-secondary w-full" onClick={() => copy(text)}>
          {copied ? t('twoFactor.copied') : t('twoFactor.copyCodes')}
        </button>
        <label className="flex items-start gap-2 text-sm text-slate-700">
          <input type="checkbox" className="mt-0.5" checked={saved} onChange={(e) => setSaved(e.target.checked)} />
          {t('twoFactor.savedCodes')}
        </label>
        <button className="btn-primary w-full" disabled={!saved} onClick={() => onDone(result.session)}>
          {t('twoFactor.finish')}
        </button>
      </div>
    )
  }

  if (setup.isLoading) return <Spinner />
  if (setup.isError || !setup.data) {
    return <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, setup.error)}</p>
  }

  const key = setup.data.sharedKey.replace(/(.{4})/g, '$1 ').trim()

  // Шаг 1: ключ и проверочный код
  return (
    <form
      className="space-y-4"
      onSubmit={(e) => {
        e.preventDefault()
        verify.mutate({ code: code.trim(), twoFactorToken })
      }}
    >
      <p className="text-sm text-slate-600">{t('twoFactor.setupInfo')}</p>
      <div className="rounded-xl bg-brand-50 p-4">
        <div className="text-xs text-slate-400">{t('twoFactor.key')}</div>
        <div className="mt-1 break-all font-mono text-base font-semibold text-slate-900">{key}</div>
        <div className="mt-2 flex gap-4 text-sm">
          <button type="button" className="font-medium text-brand-600 hover:underline" onClick={() => copy(setup.data.sharedKey)}>
            {copied ? t('twoFactor.copied') : t('twoFactor.copyKey')}
          </button>
          <a className="font-medium text-brand-600 hover:underline" href={setup.data.authenticatorUri}>
            {t('twoFactor.openApp')}
          </a>
        </div>
      </div>
      <CodeInput value={code} onChange={setCode} onComplete={(v) => verify.mutate({ code: v, twoFactorToken })} disabled={verify.isPending} invalid={verify.isError} ariaLabel={t('twoFactor.code')} />
      {verify.isError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, verify.error)}</p>}
      <div className="flex gap-3">
        {onCancel && (
          <button type="button" className="btn-secondary" onClick={onCancel}>
            {t('common.cancel')}
          </button>
        )}
        <button className="btn-primary flex-1" disabled={code.length !== 6 || verify.isPending}>
          {verify.isPending ? t('common.loading') : t('twoFactor.enable')}
        </button>
      </div>
    </form>
  )
}
