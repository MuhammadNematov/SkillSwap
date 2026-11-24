import { useRef, useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { forgotPassword } from '../../api/auth'
import { USE_MOCKS } from '../../api/config'
import { ApiError, errorMessage } from '../../api/errors'
import AuthLayout from '../../components/AuthLayout'
import FormField from '../../components/FormField'
import Turnstile, { type TurnstileHandle } from '../../components/Turnstile'

export default function ForgotPassword() {
  const { t } = useTranslation()
  const [sent, setSent] = useState<string | null>(null)
  const [serverError, setServerError] = useState<string | null>(null)
  const [captchaToken, setCaptchaToken] = useState<string | null>(null)
  const captcha = useRef<TurnstileHandle>(null)
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<{ email: string }>({ mode: 'onTouched' })

  async function onSubmit({ email }: { email: string }) {
    setServerError(null)
    if (!USE_MOCKS && !captchaToken) {
      setServerError(errorMessage(t, new ApiError('captcha_required', 400)))
      return
    }
    try {
      await forgotPassword({ email: email.trim(), captchaToken: captchaToken ?? undefined })
      setSent(email.trim())
    } catch (e) {
      setServerError(errorMessage(t, e))
    } finally {
      captcha.current?.reset()
    }
  }

  if (sent) {
    return (
      <AuthLayout title={t('forgot.sentTitle')}>
        <p className="text-sm text-slate-600">{t('forgot.sentText', { email: sent })}</p>
        <Link to="/login" className="btn-primary mt-6 w-full">
          {t('registerPage.toLogin')}
        </Link>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title={t('forgot.title')} subtitle={t('forgot.hint')}>
      <form onSubmit={handleSubmit(onSubmit)} className="space-y-4" noValidate>
        <FormField label={t('loginPage.email')} error={errors.email}>
          <input
            type="email"
            className="input"
            autoComplete="email"
            {...register('email', { required: 'validation.required', pattern: { value: /^\S+@\S+\.\S+$/, message: 'validation.email' } })}
          />
        </FormField>
        <Turnstile ref={captcha} onToken={setCaptchaToken} />
        {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}
        <button className="btn-primary w-full" disabled={isSubmitting}>
          {isSubmitting ? t('common.loading') : t('forgot.submit')}
        </button>
        <p className="text-center text-sm">
          <Link to="/login" className="font-medium text-brand-600 hover:underline">
            {t('forgot.backToLogin')}
          </Link>
        </p>
      </form>
    </AuthLayout>
  )
}
