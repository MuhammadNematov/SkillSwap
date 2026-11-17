import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom'
import { login, loginTwoFactor } from '../../api/auth'
import { errorMessage } from '../../api/errors'
import type { AuthSession } from '../../api/types'
import AuthLayout from '../../components/AuthLayout'
import FormField from '../../components/FormField'
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

  const [step, setStep] = useState<'credentials' | '2fa'>('credentials')
  const [challengeToken, setChallengeToken] = useState<string | undefined>()
  const [useRecovery, setUseRecovery] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)

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
    try {
      const result = await login(values)
      if (result.requires2fa) {
        setChallengeToken(result.challengeToken)
        setStep('2fa')
      } else {
        finish(result.session)
      }
    } catch (e) {
      setServerError(errorMessage(t, e))
    }
  }

  async function onCode({ code }: CodeForm) {
    setServerError(null)
    try {
      const session = await loginTwoFactor({
        code: code.trim(),
        isRecoveryCode: useRecovery,
        challengeToken,
      })
      finish(session)
    } catch (e) {
      setServerError(errorMessage(t, e))
    }
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

        {/* TODO: капча Cloudflare Turnstile (FR-5) — подключим вместе с бэкендом */}

        {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}

        <button className="btn-primary w-full" disabled={isSubmitting}>
          {isSubmitting ? t('common.loading') : t('loginPage.submit')}
        </button>

        <p className="text-center text-sm text-slate-500">
          {t('loginPage.noAccount')}{' '}
          <Link to="/register" className="font-medium text-brand-600 hover:underline">
            {t('auth.register')}
          </Link>
        </p>
      </form>
    </AuthLayout>
  )
}
