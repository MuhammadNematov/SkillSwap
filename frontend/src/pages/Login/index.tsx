import { useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom'
import { login, loginTwoFactor } from '../../api/auth'
import { ApiError, errorMessage, isErrorCode } from '../../api/errors'
import type { AuthSession } from '../../api/types'
import AuthLayout from '../../components/AuthLayout'
import ConfirmEmailCode from '../../components/ConfirmEmailCode'
import DevStaffPanel from '../../components/DevStaffPanel'
import CodeInput from '../../components/CodeInput'
import FormField from '../../components/FormField'
import TwoFactorSetup from '../../components/TwoFactorSetup'
import Turnstile, { type TurnstileHandle } from '../../components/Turnstile'
import { USE_MOCKS } from '../../api/config'
import { useAuth } from '../../hooks/useAuth'
import { homeForRole } from '../../utils/roles'

interface CredentialsForm {
  email: string
  password: string
}
interface CodeForm {
  code: string
}

export default function Login() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const { user, loading, setSession } = useAuth()

  const [step, setStep] = useState<'credentials' | '2fa' | 'setup' | 'confirm'>('credentials')
  const [twoFactorToken, setTwoFactorToken] = useState<string | undefined>()
  const [useRecovery, setUseRecovery] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)
  const [unconfirmedEmail, setUnconfirmedEmail] = useState<string | null>(null)
  const [justConfirmed, setJustConfirmed] = useState(false)

  const [captchaToken, setCaptchaToken] = useState<string | null>(null)
  const captcha = useRef<TurnstileHandle>(null)

  // Проверка полей только после попытки отправки (режим onSubmit по умолчанию), а потом по мере исправления: «Обязательное поле» не появляется от того, что человек просто зашёл в поле и вышел
  const credentialsForm = useForm<CredentialsForm>()
  const codeForm = useForm<CodeForm>()
  const [totp, setTotp] = useState('') // код из приложения: шесть клеток
  const [totpAttempt, setTotpAttempt] = useState(0)
  const [totpBusy, setTotpBusy] = useState(false)

  // Уже вошёл — на страницу входа ему не нужно
  if (!loading && user) return <Navigate to={homeForRole(user.role)} replace />

  const from = (location.state as { from?: { pathname: string } } | null)?.from?.pathname

  function finish(session: AuthSession) {
    setSession(session)
    navigate(from ?? homeForRole(session.user.role), { replace: true })
  }

  async function onCredentials(values: CredentialsForm) {
    setServerError(null)
    setJustConfirmed(false)
    if (!USE_MOCKS && !captchaToken) {
      setServerError(errorMessage(t, new ApiError('captcha_required', 400)))
      return
    }
    try {
      const result = await login({ ...values, captchaToken: captchaToken ?? undefined })
      if (result.requires2fa) {
        setTwoFactorToken(result.twoFactorToken)
        setStep('2fa')
      } else if (result.requires2faSetup) {
        // Служебным аккаунтам без 2FA вход разрешён только после её настройки
        setTwoFactorToken(result.twoFactorToken)
        setStep('setup')
      } else {
        finish(result.session)
      }
    } catch (e) {
      if (isErrorCode(e, 'EMAIL_NOT_CONFIRMED')) {
        // Почта не подтверждена: сразу просим код из письма
        setUnconfirmedEmail(values.email.trim())
        setStep('confirm')
      } else {
        setServerError(errorMessage(t, e))
      }
    } finally {
      captcha.current?.reset() // токен одноразовый
    }
  }

  async function onCode({ code }: CodeForm) {
    setServerError(null)
    try {
      const session = await loginTwoFactor({
        code: code.trim(),
        isRecoveryCode: useRecovery,
        twoFactorToken: twoFactorToken ?? '',
      })
      finish(session)
    } catch (e) {
      setServerError(errorMessage(t, e))
    }
  }

  // Код из приложения вводится в шесть клеток и уходит сам после шестой цифры. Резервный код (XXXX-XXXX-XXXX-XXXX) в клетки не помещается, для него обычное поле.
  async function submitTotp(code: string) {
    if (totpBusy) return
    if (code.length !== 6) {
      setServerError(t('validation.codeFormat'))
      return
    }
    setServerError(null)
    setTotpBusy(true)
    try {
      finish(await loginTwoFactor({ code, isRecoveryCode: false, twoFactorToken: twoFactorToken ?? '' }))
    } catch (e) {
      setServerError(errorMessage(t, e))
      setTotp('')
      setTotpAttempt((n) => n + 1)
    } finally {
      setTotpBusy(false)
    }
  }

  if (step === 'confirm' && unconfirmedEmail) {
    return (
      <AuthLayout title={t('confirmEmail.title')} subtitle={t('confirmEmail.loginHint', { email: unconfirmedEmail })}>
        <ConfirmEmailCode
          email={unconfirmedEmail}
          onConfirmed={() => {
            setJustConfirmed(true)
            setStep('credentials')
          }}
        />
        <button type="button" className="mt-4 text-sm text-ink-soft hover:underline" onClick={() => setStep('credentials')}>
          {t('twoFactor.back')}
        </button>
      </AuthLayout>
    )
  }

  if (step === 'setup') {
    return (
      <AuthLayout title={t('twoFactor.setupTitle')} subtitle={t('twoFactor.setupMandatory')}>
        <TwoFactorSetup
          twoFactorToken={twoFactorToken}
          onDone={(session) => (session ? finish(session) : setStep('credentials'))}
          onCancel={() => setStep('credentials')}
        />
      </AuthLayout>
    )
  }

  if (step === '2fa') {
    return (
      <AuthLayout
        title={t('twoFactor.title')}
        subtitle={useRecovery ? t('twoFactor.recoveryHint') : t('twoFactor.hint')}
      >
        <form
          onSubmit={(ev) => {
            ev.preventDefault()
            if (useRecovery) void codeForm.handleSubmit(onCode)(ev)
            else void submitTotp(totp)
          }}
          className="space-y-4"
          noValidate
        >
          {useRecovery ? (
            <FormField label={t('twoFactor.code')} error={codeForm.formState.errors.code}>
              <input
                className="input tracking-widest"
                autoComplete="one-time-code"
                autoFocus
                {...codeForm.register('code', { required: 'validation.required' })}
              />
            </FormField>
          ) : (
            <div>
              <span className="mb-1.5 block text-sm font-medium text-slate-700">{t('twoFactor.code')}</span>
              <CodeInput key={totpAttempt} value={totp} onChange={setTotp} onComplete={(v) => void submitTotp(v)} autoFocus disabled={totpBusy} invalid={!!serverError} ariaLabel={t('twoFactor.code')} />
            </div>
          )}

          {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}

          <button className="btn-primary w-full" disabled={useRecovery ? codeForm.formState.isSubmitting : totpBusy || totp.length !== 6}>
            {(useRecovery ? codeForm.formState.isSubmitting : totpBusy) ? t('common.loading') : t('twoFactor.submit')}
          </button>

          <div className="flex items-center justify-between text-sm">
            <button
              type="button"
              className="text-brand-600 hover:underline"
              onClick={() => {
                setUseRecovery((v) => !v)
                codeForm.reset()
                setTotp('')
                setServerError(null)
              }}
            >
              {useRecovery ? t('twoFactor.useApp') : t('twoFactor.useRecovery')}
            </button>
            <button
              type="button"
              className="text-slate-500 hover:underline"
              onClick={() => {
                setStep('credentials')
                setUseRecovery(false)
                setServerError(null)
                codeForm.reset()
                setTotp('')
              }}
            >
              {t('twoFactor.back')}
            </button>
          </div>
        </form>
      </AuthLayout>
    )
  }

  const { errors, isSubmitting } = credentialsForm.formState

  return (
    <AuthLayout title={t('loginPage.title')}>
      <form onSubmit={credentialsForm.handleSubmit(onCredentials)} className="space-y-4" noValidate>
        <FormField label={t('loginPage.email')} error={errors.email}>
          <input
            type="email"
            className="input"
            autoComplete="email"
            placeholder="name@example.com"
            {...credentialsForm.register('email', {
              required: 'validation.required',
              pattern: { value: /^\S+@\S+\.\S+$/, message: 'validation.email' },
            })}
          />
        </FormField>

        <FormField label={t('loginPage.password')} error={errors.password}>
          <input
            type="password"
            className="input"
            autoComplete="current-password"
            {...credentialsForm.register('password', { required: 'validation.required' })}
          />
        </FormField>

        <Turnstile ref={captcha} onToken={setCaptchaToken} />

        {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}
        {justConfirmed && <p className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-700">{t('confirmEmail.successLogin')}</p>}

        <button className="btn-primary h-12 w-full rounded-2xl text-[15px]" disabled={isSubmitting}>
          {isSubmitting ? t('common.loading') : t('loginPage.submit')}
        </button>

        {USE_MOCKS && (
          <button
            type="button"
            className="h-11 w-full rounded-2xl border border-dashed border-brand-300 bg-brand-50 text-[13px] font-semibold text-brand-600 hover:bg-brand-100"
            onClick={() => {
              credentialsForm.setValue('email', 'user@test.com')
              credentialsForm.setValue('password', 'Test1234')
            }}
          >
            {t('loginPage.fillDemo')}
          </button>
        )}

        <p className="text-center text-sm">
          <Link to="/forgot-password" className="font-medium text-brand-600 hover:underline">
            {t('loginPage.forgot')}
          </Link>
        </p>

        <p className="text-center text-sm text-slate-500">
          {t('loginPage.noAccount')}{' '}
          <Link to="/register" className="font-medium text-brand-600 hover:underline">
            {t('auth.register')}
          </Link>
        </p>
      </form>
      {import.meta.env.DEV && !USE_MOCKS && <DevStaffPanel onSession={finish} />}
    </AuthLayout>
  )
}
