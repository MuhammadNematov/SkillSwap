import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link, Navigate } from 'react-router-dom'
import { register as registerRequest } from '../../api/auth'
import { errorMessage } from '../../api/errors'
import type { UiLanguage } from '../../api/types'
import AuthLayout from '../../components/AuthLayout'
import FormField from '../../components/FormField'
import { useAuth } from '../../hooks/useAuth'
import { ageFromBirthDate, todayIso } from '../../utils/dates'
import { homeForRole } from '../../utils/roles'

interface RegisterForm {
  name: string
  email: string
  password: string
  confirmPassword: string
  dateOfBirth: string
  acceptTerms: boolean
  consentModeratorAccess: boolean
}

const MIN_AGE = 16 // ТЗ, FR-3

export default function Register() {
  const { t, i18n } = useTranslation()
  const { user, loading } = useAuth()
  const [serverError, setServerError] = useState<string | null>(null)
  const [doneEmail, setDoneEmail] = useState<string | null>(null)

  const {
    register,
    handleSubmit,
    getValues,
    formState: { errors, isSubmitting },
  } = useForm<RegisterForm>({ defaultValues: { acceptTerms: false, consentModeratorAccess: false } })

  if (!loading && user) return <Navigate to={homeForRole(user.role)} replace />

  async function onSubmit(values: RegisterForm) {
    setServerError(null)
    try {
      await registerRequest({
        name: values.name.trim(),
        email: values.email.trim(),
        password: values.password,
        dateOfBirth: values.dateOfBirth,
        uiLanguage: (i18n.resolvedLanguage ?? 'ru') as UiLanguage,
        acceptTerms: values.acceptTerms,
        consentModeratorAccess: values.consentModeratorAccess,
      })
      setDoneEmail(values.email.trim())
    } catch (e) {
      setServerError(errorMessage(t, e))
    }
  }

  // После регистрации: нужно подтвердить email по ссылке из письма (FR-1)
  if (doneEmail) {
    return (
      <AuthLayout title={t('registerPage.successTitle')}>
        <p className="text-sm text-slate-600">{t('registerPage.successText', { email: doneEmail })}</p>
        <Link to="/login" className="btn-primary mt-6 w-full">
          {t('registerPage.toLogin')}
        </Link>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title={t('registerPage.title')}>
      <form onSubmit={handleSubmit(onSubmit)} className="space-y-4" noValidate>
        <FormField label={t('registerPage.name')} error={errors.name}>
          <input
            className="input"
            autoComplete="name"
            {...register('name', {
              required: 'validation.required',
              validate: (v) => v.trim().length > 0 || 'validation.required',
            })}
          />
        </FormField>

        <FormField label={t('registerPage.email')} error={errors.email}>
          <input
            type="email"
            className="input"
            autoComplete="email"
            placeholder="name@example.com"
            {...register('email', {
              required: 'validation.required',
              pattern: { value: /^\S+@\S+\.\S+$/, message: 'validation.email' },
            })}
          />
        </FormField>

        <FormField
          label={t('registerPage.password')}
          error={errors.password}
          hint={t('registerPage.passwordHint')}
        >
          <input
            type="password"
            className="input"
            autoComplete="new-password"
            {...register('password', {
              required: 'validation.required',
              // минимум 8 символов, буквы и цифры (FR-1)
              validate: (v) => (v.length >= 8 && /\p{L}/u.test(v) && /\d/.test(v)) || 'validation.passwordRule',
            })}
          />
        </FormField>

        <FormField label={t('registerPage.confirmPassword')} error={errors.confirmPassword}>
          <input
            type="password"
            className="input"
            autoComplete="new-password"
            {...register('confirmPassword', {
              required: 'validation.required',
              validate: (v) => v === getValues('password') || 'validation.passwordMismatch',
            })}
          />
        </FormField>

        <FormField
          label={t('registerPage.dateOfBirth')}
          error={errors.dateOfBirth}
          hint={t('registerPage.dateOfBirthHint')}
        >
          <input
            type="date"
            className="input"
            max={todayIso()}
            {...register('dateOfBirth', {
              required: 'validation.required',
              validate: (v) => {
                if (v > todayIso()) return 'validation.futureDate'
                return ageFromBirthDate(v) >= MIN_AGE || 'validation.underage'
              },
            })}
          />
        </FormField>

        <div className="space-y-3 pt-1">
          <label className="flex items-start gap-3 text-sm text-slate-600">
            <input
              type="checkbox"
              className="mt-0.5 h-4 w-4 accent-brand-600"
              {...register('acceptTerms', { required: 'validation.mustAccept' })}
            />
            <span>{t('registerPage.acceptTerms')}</span>
          </label>
          {errors.acceptTerms?.message && (
            <p className="text-xs text-red-600">{t(errors.acceptTerms.message)}</p>
          )}

          <label className="flex items-start gap-3 text-sm text-slate-600">
            <input
              type="checkbox"
              className="mt-0.5 h-4 w-4 accent-brand-600"
              {...register('consentModeratorAccess', { required: 'validation.mustAccept' })}
            />
            <span>{t('registerPage.consentModerator')}</span>
          </label>
          {errors.consentModeratorAccess?.message && (
            <p className="text-xs text-red-600">{t(errors.consentModeratorAccess.message)}</p>
          )}
        </div>

        {/* TODO: капча Cloudflare Turnstile (FR-5) — подключим вместе с бэкендом */}

        {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}

        <button className="btn-primary w-full" disabled={isSubmitting}>
          {isSubmitting ? t('common.loading') : t('registerPage.submit')}
        </button>

        <p className="text-center text-sm text-slate-500">
          {t('registerPage.haveAccount')}{' '}
          <Link to="/login" className="font-medium text-brand-600 hover:underline">
            {t('auth.login')}
          </Link>
        </p>
      </form>
    </AuthLayout>
  )
}
