import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router-dom'
import { resetPassword } from '../../api/auth'
import { errorMessage } from '../../api/errors'
import AuthLayout from '../../components/AuthLayout'
import FormField from '../../components/FormField'

interface Form {
  password: string
  confirmPassword: string
}

// Ссылка из письма: /reset-password?userId=...&token=... (токен живёт 1 час)
export default function ResetPassword() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const userId = params.get('userId') ?? ''
  const [done, setDone] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    getValues,
    formState: { errors, isSubmitting },
  } = useForm<Form>()

  async function onSubmit({ password }: Form) {
    setServerError(null)
    try {
      await resetPassword({ userId, token, newPassword: password })
      setDone(true)
    } catch (e) {
      setServerError(errorMessage(t, e))
    }
  }

  if (!token || !userId) {
    return (
      <AuthLayout title={t('reset.title')}>
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{t('confirmEmail.noToken')}</p>
        <Link to="/forgot-password" className="btn-primary mt-6 w-full">
          {t('reset.requestNew')}
        </Link>
      </AuthLayout>
    )
  }

  if (done) {
    return (
      <AuthLayout title={t('reset.doneTitle')}>
        <p className="text-sm text-slate-600">{t('reset.doneText')}</p>
        <Link to="/login" className="btn-primary mt-6 w-full">
          {t('registerPage.toLogin')}
        </Link>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title={t('reset.title')}>
      <form onSubmit={handleSubmit(onSubmit)} className="space-y-4" noValidate>
        <FormField label={t('reset.newPassword')} error={errors.password} hint={t('registerPage.passwordHint')}>
          <input
            type="password"
            className="input"
            autoComplete="new-password"
            {...register('password', {
              required: 'validation.required',
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
        {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}
        <button className="btn-primary w-full" disabled={isSubmitting}>
          {isSubmitting ? t('common.loading') : t('reset.submit')}
        </button>
      </form>
    </AuthLayout>
  )
}
