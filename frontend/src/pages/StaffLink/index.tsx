import { useMutation, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { errorMessage } from '../../api/errors'
import { confirmStaffLinkRequest, declineStaffLinkRequest, getStaffLinkRequest } from '../../api/staff'
import AuthLayout from '../../components/AuthLayout'
import Spinner from '../../components/Spinner'
import { formatDateTime } from '../../utils/dates'

// Ссылка из письма: /staff-link/:token (бэкенд: Frontend:PersonalLinkPath). Страница для владельца личного аккаунта:
// он уже вошёл (ProtectedRoute), видит, кто просит привязку, и подтверждает или отказывается. Запрос живёт 24 часа.
export default function StaffLink() {
  const { t, i18n } = useTranslation()
  const { token = '' } = useParams()
  const [result, setResult] = useState<'confirmed' | 'declined' | null>(null)
  const info = useQuery({ queryKey: ['staffLinkRequest', token], queryFn: () => getStaffLinkRequest(token), retry: false })
  const decide = useMutation({
    mutationFn: async (kind: 'confirmed' | 'declined') => {
      await (kind === 'confirmed' ? confirmStaffLinkRequest(token) : declineStaffLinkRequest(token))
      return kind
    },
    onSuccess: setResult,
  })

  const toApp = (
    <Link to="/app" className="btn-primary mt-6 w-full">
      {t('staffLink.toApp')}
    </Link>
  )

  if (info.isLoading)
    return (
      <AuthLayout title={t('staffLink.title')}>
        <Spinner />
      </AuthLayout>
    )
  if (info.isError)
    return (
      <AuthLayout title={t('staffLink.title')}>
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, info.error)}</p>
        {toApp}
      </AuthLayout>
    )
  if (result)
    return (
      <AuthLayout title={t('staffLink.title')}>
        <p className="text-sm text-slate-600">{t(result === 'confirmed' ? 'staffLink.confirmed' : 'staffLink.declined')}</p>
        {toApp}
      </AuthLayout>
    )

  const lang = i18n.resolvedLanguage ?? 'ru'
  return (
    <AuthLayout title={t('staffLink.title')} subtitle={t('staffLink.intro', { name: info.data!.staffName, role: t(`staff.role_${info.data!.staffRole}`) })}>
      <p className="text-sm text-slate-600">{t('staffLink.why')}</p>
      <p className="mt-3 text-xs text-slate-500">{t('staffLink.expires', { date: formatDateTime(info.data!.expiresAt, lang) })}</p>
      {decide.isError && <p className="mt-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, decide.error)}</p>}
      <div className="mt-6 flex gap-3">
        <button className="btn-primary flex-1" disabled={decide.isPending} onClick={() => decide.mutate('confirmed')}>
          {t('staffLink.confirm')}
        </button>
        <button className="btn-secondary flex-1" disabled={decide.isPending} onClick={() => decide.mutate('declined')}>
          {t('staffLink.decline')}
        </button>
      </div>
    </AuthLayout>
  )
}
