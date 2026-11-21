import { useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom'
import { login, loginTwoFactor, resendConfirmation } from '../../api/auth'
import { ApiError, errorMessage, isErrorCode } from '../../api/errors'
import type { AuthSession } from '../../api/types'
import AuthLayout from '../../components/AuthLayout'
import DevStaffPanel from '../../components/DevStaffPanel'
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

  const [step, setStep] = useState<'credentials' | '2fa' | 'setup'>('credentials')
  const [twoFactorToken, setTwoFactorToken] = useState<string | undefined>()
  const [useRecovery, setUseRecovery] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)
  const [unconfirmedEmail, setUnconfirmedEmail] = useState<string | null>(null)
  const [resent, setResent] = useState(false)

  const [captchaToken, setCaptchaToken] = useState<string | null>(null)
  const captcha = useRef<TurnstileHandle>(null)

  const credentialsForm = useForm<CredentialsForm>()
  const codeForm = useForm<CodeForm>()

  // Уже вошёл — на страницу входа ему не нужно
  if (!loading && user) return <Navigate to={homeForRole(user.role)} replace />

  const from = (location.state as { from?: { pathname: string } } | null)?.from?.pathname

  function finish(session: AuthSession) {
    setSession(session)
    navigate(from ?? homeForRole(session.user.role), { replace: true })
  }

  async function onCredentials(values: CredentialsForm) {
    setServerError(null)
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
      setServerError(errorMessage(t, e))
      setUnconfirmedEmail(isErrorCode(e, 'EMAIL_NOT_CONFIRMED') ? values.email.trim() : null)
      setResent(false)
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
        <form onSubmit={codeForm.handleSubmit(onCode)} className="space-y-4" noValidate>
          <FormField label={t('twoFactor.code')} error={codeForm.formState.errors.code}>
            <input
              className="input tracking-widest"
              autoComplete="one-time-code"
              inputMode={useRecovery ? 'text' : 'numeric'}
              autoFocus
              {...codeForm.register('code', {
                required: 'validation.required',
                // Обычный код — ровно 6 цифр; резервный код формат не проверяем
                validate: (v) => useRecovery || /^\d{6}$/.test(v.trim()) || 'validation.codeFormat',
              })}
            />
          </FormField>

          {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}

          <button className="btn-primary w-full" disabled={codeForm.formState.isSubmitting}>
            {codeForm.formState.isSubmitting ? t('common.loading') : t('twoFactor.submit')}
          </button>

          <div className="flex items-center justify-between text-sm">
            <button
              type="button"
              className="text-brand-600 hover:underline"
              onClick={() => {
                setUseRecovery((v) => !v)
                codeForm.reset()
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
        {unconfirmedEmail &&
          (resent ? (
            <p className="text-sm text-emerald-700">{t('registerPage.resent')}</p>
          ) : (
            <button
              type="button"
              className="text-sm font-medium text-brand-600 hover:underline"
              onClick={() =>
                resendConfirmation(unconfirmedEmail)
                  .then(() => setResent(true))
                  .catch((e) => setServerError(errorMessage(t, e)))
              }
            >
              {t('registerPage.resend')}
            </button>
          ))}

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
