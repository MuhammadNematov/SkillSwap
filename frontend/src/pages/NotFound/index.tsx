import { Link } from 'react-router-dom'
import { useTranslation } from 'react-i18next'

export default function NotFound() {
  const { t } = useTranslation()
  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-4">
      <h1 className="text-3xl font-bold text-brand-700">{t('errors.notFound')}</h1>
      <Link to="/" className="text-sm text-brand-600 underline">
        {t('common.backHome')}
      </Link>
    </div>
  )
}
