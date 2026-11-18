import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useSearchParams } from 'react-router-dom'
import { confirmEmail } from '../../api/auth'
import { errorMessage } from '../../api/errors'
import AuthLayout from '../../components/AuthLayout'
import Spinner from '../../components/Spinner'

// Ссылка из письма: /confirm-email?userId=...&token=...
export default function ConfirmEmail() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const userId = params.get('userId') ?? ''

  const { isLoading, isError, error } = useQuery({
    queryKey: ['confirmEmail', userId, token],
    queryFn: () => confirmEmail({ userId, token }),
    enabled: !!token && !!userId,
    retry: false,
    staleTime: Infinity, // токен одноразовый: повторно не отправляем
  })

  return (
    <AuthLayout title={t('confirmEmail.title')}>
      {!token || !userId ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{t('confirmEmail.noToken')}</p>
      ) : isLoading ? (
        <Spinner />
      ) : isError ? (
        <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{errorMessage(t, error)}</p>
      ) : (
        <p className="text-sm text-slate-600">{t('confirmEmail.success')}</p>
      )}
      <Link to="/login" className="btn-primary mt-6 w-full">
        {t('registerPage.toLogin')}
      </Link>
    </AuthLayout>
  )
}
