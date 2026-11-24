import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { acceptStaffInvitation, getStaffInvitation } from '../../api/staff'
import { errorMessage } from '../../api/errors'
import AuthLayout from '../../components/AuthLayout'
import FormField from '../../components/FormField'
import Spinner from '../../components/Spinner'
import TwoFactorSetup from '../../components/TwoFactorSetup'
import { useAuth } from '../../hooks/useAuth'
import { homeForRole } from '../../utils/roles'
import type { UiLanguage } from '../../api/types'

interface Form {
  name: string
  password: string
  confirmPassword: string
}

// Ссылка из письма: /staff/invitation/:token (бэкенд: Frontend:StaffInvitationPath). Создаёт служебный аккаунт; 2FA настраивается при первом входе.
export default function StaffInvitation() {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const { setSession } = useAuth()
  const { token = '' } = useParams()
  const [setupToken, setSetupToken] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const [serverError, setServerError] = useState<string | null>(null)
  const info = useQuery({ queryKey: ['staffInvitation', token], queryFn: () => getStaffInvitation(token), retry: false })
  const {
    register,
    handleSubmit,
    getValues,
    formState: { errors, isSubmitting },
  } = useForm<Form>({ mode: 'onTouched' })

  async function onSubmit({ name, password }: Form) {
    setServerError(null)
    try {
      const r = await acceptStaffInvitation({ token, name: name.trim(), password, uiLanguage: (i18n.resolvedLanguage ?? 'ru') as UiLanguage })
      setSetupToken(r.twoFactorToken) // дальше обязательная настройка 2FA по этому токену
    } catch (e) {
      setServerError(errorMessage(t, e))
    }
  }

  if (setupToken && !done)
    return (
      <AuthLayout title={t('twoFactor.setupTitle')} subtitle={t('twoFactor.setupMandatory')}>
        <TwoFactorSetup
          twoFactorToken={setupToken}
          onDone={(session) => {
            if (session) {
              setSession(session)
              navigate(homeForRole(session.user.role), { replace: true })
            } else setDone(true)
          }}
        />
      </AuthLayout>
    )
  if (info.isLoading) return <AuthLayout title={t('staffInvite.title')}><Spinner /></AuthLayout>
  if (info.isError)
    return (
      <AuthLayout title={t('staffInvite.title')}>
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, info.error)}</p>
      </AuthLayout>
    )
  if (done)
    return (
      <AuthLayout title={t('staffInvite.doneTitle')}>
        <p className="text-sm text-slate-600">{t('staffInvite.doneText')}</p>
        <Link to="/login" className="btn-primary mt-6 w-full">
          {t('registerPage.toLogin')}
        </Link>
      </AuthLayout>
    )

  return (
    <AuthLayout title={t('staffInvite.title')} subtitle={t('staffInvite.subtitle', { email: info.data!.email, role: t(`staff.role_${info.data!.targetRole}`) })}>
      <form onSubmit={handleSubmit(onSubmit)} className="space-y-4" noValidate>
        <FormField label={t('staffInvite.name')} error={errors.name}>
          <input className="input" maxLength={100} {...register('name', { required: 'validation.required' })} />
        </FormField>
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
        <p className="text-xs text-slate-500">{t('staffInvite.twoFaNote')}</p>
        <p className="text-xs text-slate-500">{t('staffInvite.invitedBy', { name: info.data!.invitedByName })}</p>
        {serverError && <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{serverError}</p>}
        <button className="btn-primary w-full" disabled={isSubmitting}>
          {isSubmitting ? t('common.loading') : t('staffInvite.submit')}
        </button>
      </form>
    </AuthLayout>
  )
}
